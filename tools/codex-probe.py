#!/usr/bin/env python3
"""
codex-probe.py -- feasibility probe, READ-ONLY, stdlib only.

Question this answers: can Codex (OpenAI's CLI) usage/rate-limit data be
read locally from this machine, and if so, by which mechanism?

It does two things, in order:
  Strategy A (preferred): shell out to the first-party `codex` binary in
    read-only/untrusted sandbox mode, speak newline-delimited JSON-RPC 2.0
    over its stdin/stdout, and call account/read and
    account/rateLimits/read. This is the supported first-party interface,
    if the installed codex version exposes it.
  Strategy B (fallback): read the OAuth token Codex already has cached on
    disk (CODEX_HOME/auth.json, default ~/.codex/auth.json) and call the
    same two backend HTTP endpoints the official client uses.

This script never writes to auth.json, never refreshes or rotates any
token, and never prints a token / refresh token / id_token payload beyond
a plan type and email domain. It only reports presence, lengths, and
expiry times so you can confirm the token is readable and still valid.

Run it on the HOST (not the VM) because the token file lives at a host
path this VM cannot see:

    python tools/codex-probe.py
"""

import base64
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone


def hr(title):
    print()
    print("=" * 8 + " " + title + " " + "=" * 8)


def utc_and_local(unix_ts):
    try:
        dt_utc = datetime.fromtimestamp(unix_ts, tz=timezone.utc)
    except (OverflowError, OSError, ValueError):
        return "unparseable timestamp: {}".format(unix_ts)
    dt_local = dt_utc.astimezone()
    return "{} UTC ({} local)".format(
        dt_utc.strftime("%Y-%m-%d %H:%M:%S"),
        dt_local.strftime("%Y-%m-%d %H:%M:%S %Z"),
    )


def b64url_decode(segment):
    padded = segment + "=" * (-len(segment) % 4)
    return base64.urlsafe_b64decode(padded)


def jwt_claims(token):
    """Best-effort JWT payload decode. Returns dict or None (not a JWT)."""
    if not token or token.count(".") != 2:
        return None
    try:
        _, payload_seg, _ = token.split(".")
        return json.loads(b64url_decode(payload_seg))
    except Exception:
        return None


def describe_token(label, token):
    print("- {}: ".format(label), end="")
    if not token:
        print("ABSENT")
        return
    print("present, length={}".format(len(token)))
    claims = jwt_claims(token)
    if claims is None:
        print("    (not a decodable JWT, or opaque token -- no further info)")
        return
    exp = claims.get("exp")
    if isinstance(exp, (int, float)):
        print("    expires: {}".format(utc_and_local(exp)))
    else:
        print("    no exp claim found")
    iat = claims.get("iat")
    if isinstance(iat, (int, float)):
        print("    issued:  {}".format(utc_and_local(iat)))


def extract_plan_and_email_domain(id_token):
    """From the id_token JWT only: plan type + email DOMAIN (never full email)."""
    claims = jwt_claims(id_token)
    if not claims:
        return None, None
    auth_claims = claims.get("https://api.openai.com/auth", {}) or {}
    plan_type = auth_claims.get("chatgpt_plan_type") or claims.get("chatgpt_plan_type")
    email = claims.get("email")
    email_domain = None
    if isinstance(email, str) and "@" in email:
        email_domain = email.split("@", 1)[1]
    return plan_type, email_domain


def load_auth():
    codex_home = os.environ.get("CODEX_HOME") or os.path.expanduser("~/.codex")
    auth_path = os.path.join(codex_home, "auth.json")
    if not os.path.isfile(auth_path):
        return None, auth_path
    try:
        with open(auth_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        print("Failed to read/parse {}: {}".format(auth_path, e))
        return None, auth_path
    return data, auth_path


def pull_tokens(auth):
    """auth.json shape isn't fully documented -- tolerate tokens.* or top-level."""
    tokens = auth.get("tokens", {}) if isinstance(auth, dict) else {}
    access_token = tokens.get("access_token") or auth.get("access_token")
    refresh_token = tokens.get("refresh_token") or auth.get("refresh_token")
    id_token = tokens.get("id_token") or auth.get("id_token")
    account_id = tokens.get("account_id") or auth.get("account_id")
    return access_token, refresh_token, id_token, account_id


def find_field(data, candidate_names, _depth=0):
    """Best-effort recursive search for a key whose name matches (case
    insensitively) one of candidate_names, anywhere in a nested
    dict/list JSON structure. Returns (key_found, value) or (None, None).
    The exact response shape isn't documented, so this is deliberately
    permissive rather than a strict schema match."""
    if _depth > 6:
        return None, None
    lowered = {c.lower() for c in candidate_names}
    if isinstance(data, dict):
        for k, v in data.items():
            if isinstance(k, str) and k.lower() in lowered:
                return k, v
        for v in data.values():
            k2, v2 = find_field(v, candidate_names, _depth + 1)
            if k2 is not None:
                return k2, v2
    elif isinstance(data, list):
        for item in data:
            k2, v2 = find_field(item, candidate_names, _depth + 1)
            if k2 is not None:
                return k2, v2
    return None, None


# ---------------------------------------------------------------------------
# Strategy A: codex app-server over JSON-RPC (newline-delimited JSON-RPC 2.0)
# ---------------------------------------------------------------------------

def launch_codex_app_server(codex_path):
    args = [codex_path, "-s", "read-only", "-a", "untrusted", "app-server"]
    try:
        return subprocess.Popen(
            args,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
    except OSError:
        # On Windows, npm-installed CLIs are often .cmd shims that
        # CreateProcess cannot exec directly without a shell.
        if os.name == "nt":
            cmd_line = subprocess.list2cmdline(args)
            return subprocess.Popen(
                cmd_line,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
                shell=True,
            )
        raise


def start_stdout_reader(pipe):
    q = queue.Queue()

    def _reader():
        try:
            for line in iter(pipe.readline, ""):
                q.put(line)
        finally:
            q.put(None)

    t = threading.Thread(target=_reader, daemon=True)
    t.start()
    return q


def read_jsonrpc_response(q, expected_id, timeout=15):
    deadline = time.time() + timeout
    while True:
        remaining = deadline - time.time()
        if remaining <= 0:
            return None, "timed out after {}s waiting for id={}".format(timeout, expected_id)
        try:
            line = q.get(timeout=remaining)
        except queue.Empty:
            return None, "timed out after {}s waiting for id={}".format(timeout, expected_id)
        if line is None:
            return None, "codex app-server closed stdout before responding"
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue  # non-JSON log/noise line on stdout; skip
        if isinstance(obj, dict) and obj.get("id") == expected_id:
            return obj, None
        # else: notification or response to a different id -- keep waiting


def jsonrpc_call(proc, q, req_id, method, params=None):
    request = {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params or {}}
    try:
        proc.stdin.write(json.dumps(request) + "\n")
        proc.stdin.flush()
    except (BrokenPipeError, OSError) as e:
        return None, "failed to write request: {}".format(e)
    return read_jsonrpc_response(q, req_id)


def run_strategy_a():
    hr("STRATEGY A: codex app-server (JSON-RPC over stdio)")
    codex_path = shutil.which("codex")
    if not codex_path:
        print("codex not found on PATH -- skipping Strategy A.")
        return {"available": False}

    print("Found codex at: {}".format(codex_path))
    print("Launching: codex -s read-only -a untrusted app-server")
    try:
        proc = launch_codex_app_server(codex_path)
    except Exception as e:
        print("Failed to launch codex app-server: {}".format(e))
        return {"available": False}

    stdout_q = start_stdout_reader(proc.stdout)
    result = {"available": True, "account_read": None, "rate_limits": None, "success": False}

    account_resp, err = jsonrpc_call(proc, stdout_q, 1, "account/read")
    if err:
        print("account/read: FAILED -- {}".format(err))
    else:
        print("account/read response:")
        print(json.dumps(account_resp, indent=2))
        if "error" not in account_resp:
            result["account_read"] = account_resp.get("result", account_resp)

    rate_resp, err = jsonrpc_call(proc, stdout_q, 2, "account/rateLimits/read")
    if err:
        print("account/rateLimits/read: FAILED -- {}".format(err))
    else:
        print("account/rateLimits/read response:")
        print(json.dumps(rate_resp, indent=2))
        if "error" not in rate_resp:
            result["rate_limits"] = rate_resp.get("result", rate_resp)

    try:
        proc.stdin.close()
    except Exception:
        pass
    try:
        proc.terminate()
        proc.wait(timeout=5)
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass

    stderr_output = ""
    try:
        stderr_output = proc.stderr.read() or ""
    except Exception:
        pass
    if stderr_output.strip():
        print("(codex app-server stderr output, for diagnostics:)")
        print(stderr_output.strip())

    result["success"] = result["account_read"] is not None and result["rate_limits"] is not None
    return result


# ---------------------------------------------------------------------------
# Strategy B: direct HTTP calls to the backend the official client uses
# ---------------------------------------------------------------------------

def http_get_json(url, access_token, account_id):
    req = urllib.request.Request(url, method="GET")
    req.add_header("Authorization", "Bearer {}".format(access_token))
    req.add_header("Accept", "application/json")
    if account_id:
        req.add_header("ChatGPT-Account-Id", account_id)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            status = resp.status
            body = resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        status = e.code
        body = e.read().decode("utf-8", errors="replace")
    except urllib.error.URLError as e:
        return None, None, "request failed: {}".format(e)

    print("HTTP {}".format(status))
    try:
        parsed = json.loads(body)
        print(json.dumps(parsed, indent=2))
        return status, parsed, None
    except json.JSONDecodeError:
        print(body)
        return status, None, None


def run_strategy_b(access_token, account_id):
    hr("STRATEGY B: direct HTTP calls to chatgpt.com backend-api")
    if not access_token:
        print("No access_token available -- skipping Strategy B.")
        return {"available": False}
    if not account_id:
        print("(no account_id found -- ChatGPT-Account-Id header will be omitted)")

    result = {"available": True, "usage": None, "rate_limit_reset_credits": None, "success": False}

    print("GET https://chatgpt.com/backend-api/wham/usage")
    _, usage_json, err = http_get_json(
        "https://chatgpt.com/backend-api/wham/usage", access_token, account_id
    )
    if err:
        print("FAILED -- {}".format(err))
    else:
        result["usage"] = usage_json

    print()
    print("GET https://chatgpt.com/backend-api/wham/rate-limit-reset-credits")
    _, credits_json, err = http_get_json(
        "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", access_token, account_id
    )
    if err:
        print("FAILED -- {}".format(err))
    else:
        result["rate_limit_reset_credits"] = credits_json

    result["success"] = result["usage"] is not None
    return result


# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

def render_reset_at(value):
    if isinstance(value, (int, float)):
        return utc_and_local(value)
    return str(value)


def summarize(strategy_a, strategy_b, plan_type, email_domain):
    hr("SUMMARY")

    if strategy_a.get("success"):
        winner = "A (codex app-server JSON-RPC)"
        data = strategy_a
    elif strategy_b.get("success"):
        winner = "B (direct HTTP to chatgpt.com backend-api)"
        data = strategy_b
    else:
        print("Neither strategy produced usable data.")
        print("Strategy A available: {}".format(strategy_a.get("available")))
        print("Strategy B available: {}".format(strategy_b.get("available")))
        return

    print("Working strategy: {}".format(winner))
    if plan_type:
        print("plan_type: {}".format(plan_type))
    if email_domain:
        print("account email domain: {}".format(email_domain))

    if winner.startswith("A"):
        blob = {"account": data.get("account_read"), "rate_limits": data.get("rate_limits")}
    else:
        blob = {"usage": data.get("usage"), "credits": data.get("rate_limit_reset_credits")}

    for label, candidates in (
        ("primary_window used_percent", ["primary_used_percent", "used_percent"]),
        ("secondary_window used_percent", ["secondary_used_percent"]),
        ("limit_window_seconds", ["limit_window_seconds", "window_seconds", "window_minutes"]),
        ("reset_at", ["reset_at", "resets_at", "reset_time"]),
        ("credits balance", ["balance", "credits", "credit_balance"]),
    ):
        key, value = find_field(blob, candidates)
        if key is None:
            print("{}: not found in response".format(label))
        elif label == "reset_at":
            print("{}: {} (raw key: {})".format(label, render_reset_at(value), key))
        else:
            print("{}: {} (raw key: {})".format(label, value, key))

    print()
    print("Note: field names above are best-effort matches against an")
    print("undocumented response shape -- see the raw JSON printed above")
    print("for ground truth.")


def main():
    hr("STEP 1: local token presence/expiry (no secrets printed)")
    auth, auth_path = load_auth()
    print("auth.json path: {}".format(auth_path))
    if auth is None:
        print("Could not load auth.json -- Strategy B will be skipped,")
        print("but Strategy A (which reads codex's own stored credentials)")
        print("will still be attempted.")
        access_token = refresh_token = id_token = account_id = None
    else:
        access_token, refresh_token, id_token, account_id = pull_tokens(auth)
        describe_token("access_token", access_token)
        describe_token("refresh_token", refresh_token)
        describe_token("id_token", id_token)
        print("- account_id: {}".format(account_id if account_id else "ABSENT"))

    plan_type, email_domain = (None, None)
    if auth is not None and id_token:
        plan_type, email_domain = extract_plan_and_email_domain(id_token)
        if plan_type:
            print("- id_token plan_type: {}".format(plan_type))
        if email_domain:
            print("- id_token email domain: {}".format(email_domain))

    strategy_a = run_strategy_a()
    if strategy_a.get("success"):
        strategy_b = {"available": False}
        print()
        print("Strategy A succeeded fully -- skipping Strategy B (fallback not needed).")
    else:
        strategy_b = run_strategy_b(access_token, account_id)

    summarize(strategy_a, strategy_b, plan_type, email_domain)


if __name__ == "__main__":
    main()
