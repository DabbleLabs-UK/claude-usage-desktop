#!/usr/bin/env python3
"""Export complete, account-wide OpenRouter analytics without inference calls.

Python 3.9+; Windows Python may additionally need the tzdata package.
Credentials stay on this machine. Only aggregate counters leave this exporter.
"""

import argparse
import copy
import hashlib
import json
import os
import queue
import tempfile
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from zoneinfo import ZoneInfo


ANALYTICS_URL = "https://openrouter.ai/api/v1/analytics/query"
METRICS = ("credits_usage", "request_count", "tokens_prompt", "tokens_completion")
MAX_SECONDS = 140
MAX_RESPONSE_BYTES = 16 * 1024 * 1024


class ExportError(Exception):
    """A safe, non-sensitive export failure."""


def utc_text(moment):
    return moment.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def query_ranges(now, local_zone):
    local_now = now.astimezone(local_zone)
    midnight = local_now.replace(hour=0, minute=0, second=0, microsecond=0)
    sunday = midnight - timedelta(days=(midnight.weekday() + 1) % 7)
    return {
        "rolling_24h": now - timedelta(hours=24),
        "rolling_7d": now - timedelta(days=7),
        "calendar_week": sunday.astimezone(timezone.utc),
        "month_to_date": midnight.replace(day=1).astimezone(timezone.utc),
        "daily_history": (midnight - timedelta(days=30)).astimezone(timezone.utc),
    }


def metric_number(value, integer=False):
    if isinstance(value, bool) or not isinstance(value, (int, float, str, Decimal)):
        raise ExportError("Analytics contains a missing or invalid metric.")
    try:
        number = Decimal(str(value))
    except InvalidOperation as exc:
        raise ExportError("Analytics contains an invalid metric.") from exc
    if not number.is_finite() or number < 0 or number > Decimal("1e24"):
        raise ExportError("Analytics contains an out-of-range metric.")
    if integer:
        if number != number.to_integral_value():
            raise ExportError("Analytics contains a non-integer count.")
        return int(number)
    return number


def money(value):
    return float(value.quantize(Decimal("0.00000001")))


def parse_rows(payload, start, end):
    data = payload.get("data") if isinstance(payload, dict) else None
    rows = data.get("data") if isinstance(data, dict) else None
    metadata = data.get("metadata") if isinstance(data, dict) else None
    if not isinstance(rows, list) or not isinstance(metadata, dict):
        raise ExportError("Analytics returned an invalid response.")
    if metadata.get("truncated") is not False:
        raise ExportError("Analytics results are incomplete; no partial totals were saved.")
    parsed = []
    earliest_bucket = start.replace(minute=0, second=0, microsecond=0)
    for row in rows:
        if not isinstance(row, dict):
            raise ExportError("Analytics contains an invalid row.")
        if any(not isinstance(row.get(name), str) or not row[name].strip() for name in ("model", "provider")):
            raise ExportError("Analytics contains an invalid model or provider.")
        try:
            bucket = datetime.strptime(row["created_at__hour"], "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc)
        except (KeyError, TypeError, ValueError) as exc:
            raise ExportError("Analytics contains an invalid timestamp.") from exc
        if bucket.minute or bucket.second or bucket < earliest_bucket or bucket > end:
            raise ExportError("Analytics contains an out-of-range hour.")
        parsed.append({
            "at": bucket,
            "model": row["model"],
            "provider": row["provider"],
            "cost": metric_number(row.get("credits_usage")),
            "priced_runs": metric_number(row.get("request_count"), integer=True),
            "input_tokens": metric_number(row.get("tokens_prompt"), integer=True),
            "output_tokens": metric_number(row.get("tokens_completion"), integer=True),
        })
    return parsed


def fetch_rows(key, start, end, deadline):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ExportError("Analytics refresh timed out.")
    payload = {
        "metrics": list(METRICS),
        "dimensions": ["model", "provider"],
        "granularity": "hour",
        "time_range": {"start": utc_text(start), "end": utc_text(end)},
        "filters": [],
        "limit": 1000,
    }
    request = urllib.request.Request(
        ANALYTICS_URL,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json", "Accept": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=min(30, remaining)) as response:
            body = response.read(MAX_RESPONSE_BYTES + 1)
        if len(body) > MAX_RESPONSE_BYTES:
            raise ExportError("Analytics response exceeded the safe size limit.")
        value = json.loads(body)
    except ExportError:
        raise
    except Exception as exc:
        # Never include response bodies, request headers or credentials in logs.
        raise ExportError("OpenRouter analytics could not be refreshed.") from exc
    return parse_rows(value, start, end)


def period(rows):
    return {
        "total_cost": money(sum((row["cost"] for row in rows), Decimal(0))),
        "priced_runs": sum(row["priced_runs"] for row in rows),
        "unpriced_runs": 0,
    }


def breakdown(rows, fields):
    groups = {}
    for row in rows:
        key = tuple(row[field] for field in fields)
        groups.setdefault(key, []).append(row)
    result = []
    for key, group in groups.items():
        item = dict(zip(fields, key))
        totals = period(group)
        item.update(cost=totals["total_cost"], priced_runs=totals["priced_runs"], unpriced_runs=0)
        if "model" in fields:
            item.update(input_tokens=sum(row["input_tokens"] for row in group),
                        output_tokens=sum(row["output_tokens"] for row in group))
        result.append(item)
    return sorted(result, key=lambda item: (-item["cost"], tuple(item[field] for field in fields)))


def build_snapshot(key, account_id, now, timezone_name, fetch=fetch_rows):
    local_zone = ZoneInfo(timezone_name)
    ranges = query_ranges(now, local_zone)
    deadline = time.monotonic() + MAX_SECONDS
    # Query each exact range separately. Hour buckets from a broad query cannot
    # be trimmed accurately at an arbitrary rolling-24-hour boundary.
    windows = {name: fetch(key, start, now, deadline) for name, start in ranges.items()}
    stamp = utc_text(now)
    snapshot = {
        "schema_version": 1,
        "scope": "openrouter_account",
        "account_scope_id": account_id,
        "generated_at": stamp,
        "currency": "USD",
        "timezone": timezone_name,
        "source": {"status": "ok", "last_success_at": stamp},
    }
    for name in ("rolling_24h", "rolling_7d", "calendar_week", "month_to_date"):
        snapshot[name] = period(windows[name])
    snapshot["rolling_7d"]["daily_average"] = money(Decimal(str(snapshot["rolling_7d"]["total_cost"])) / 7)
    snapshot["calendar_week"]["starts_at"] = utc_text(ranges["calendar_week"])
    snapshot["run_rates"] = {
        "monthly_at_24h_pace": money(Decimal(str(snapshot["rolling_24h"]["total_cost"])) * 30),
        "monthly_at_7d_pace": money(Decimal(str(snapshot["rolling_7d"]["total_cost"])) / 7 * 30),
    }
    snapshot["breakdown_by_model"] = breakdown(windows["month_to_date"], ("model", "provider"))
    snapshot["breakdown_by_provider"] = breakdown(windows["month_to_date"], ("provider",))
    groups = {}
    for row in windows["daily_history"]:
        groups.setdefault(row["at"].astimezone(local_zone).date().isoformat(), []).append(row)
    days = []
    first_day = ranges["daily_history"].astimezone(local_zone).date()
    for offset in range(31):
        day = (first_day + timedelta(days=offset)).isoformat()
        totals = period(groups.get(day, []))
        days.append({"date": day, "cost": totals["total_cost"], "priced_runs": totals["priced_runs"], "unpriced_runs": 0})
    snapshot["daily_history"] = days
    timestamps = [row["at"] for rows in windows.values() for row in rows]
    if timestamps:
        snapshot["data_through"] = utc_text(max(timestamps))
    return snapshot


def complete_snapshot(value, account_id):
    if not isinstance(value, dict) or not account_id:
        return False
    if value.get("schema_version") != 1 or value.get("scope") != "openrouter_account" or value.get("account_scope_id") != account_id:
        return False
    try:
        datetime.fromisoformat(value["generated_at"].replace("Z", "+00:00"))
        datetime.fromisoformat(value["source"]["last_success_at"].replace("Z", "+00:00"))
        for name in ("rolling_24h", "rolling_7d", "calendar_week", "month_to_date"):
            metric_number(value[name]["total_cost"])
            metric_number(value[name]["priced_runs"], integer=True)
            metric_number(value[name]["unpriced_runs"], integer=True)
        for name in ("monthly_at_24h_pace", "monthly_at_7d_pace"):
            metric_number(value["run_rates"][name])
        if any(not isinstance(value[name], list) for name in ("breakdown_by_model", "breakdown_by_provider", "daily_history")):
            return False
    except (KeyError, TypeError, ValueError, AttributeError, ExportError):
        return False
    return True


def failure_snapshot(previous, account_id, timezone_name):
    if complete_snapshot(previous, account_id):
        result = copy.deepcopy(previous)
        result["source"]["status"] = "error"
        return result
    result = {"schema_version": 1, "scope": "openrouter_account", "currency": "USD",
              "timezone": timezone_name, "source": {"status": "error"}}
    if account_id:
        result["account_scope_id"] = account_id
    return result


def atomic_write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, prefix=path.name + ".", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            if os.name != "nt":
                os.chmod(temporary, 0o600)
            json.dump(value, stream, ensure_ascii=True, allow_nan=False, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def bounded_snapshot(key, account_id, now, timezone_name):
    result = queue.Queue(maxsize=1)

    def collect():
        try:
            result.put((build_snapshot(key, account_id, now, timezone_name), None))
        except Exception:
            result.put((None, ExportError("OpenRouter analytics could not be refreshed.")))

    # A slow socket cannot keep the command alive indefinitely. The worker never
    # writes files, so a timed-out collection cannot overwrite a failure snapshot.
    worker = threading.Thread(target=collect, daemon=True)
    worker.start()
    try:
        value, error = result.get(timeout=MAX_SECONDS)
    except queue.Empty as exc:
        raise ExportError("Analytics refresh timed out.") from exc
    if error:
        raise error
    return value


def run_export(credential_file, output, timezone_name="Europe/London", now=None, collect=bounded_snapshot):
    previous = None
    try:
        previous = json.loads(Path(output).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        pass
    account_id = None
    try:
        credential = json.loads(Path(credential_file).expanduser().read_text(encoding="utf-8-sig"))
        key = credential.get("openrouter_management_api_key")
        if not isinstance(key, str) or not key.strip():
            raise ExportError("An OpenRouter management credential is required.")
        key = key.strip()
        account_id = hashlib.sha256(key.encode("utf-8")).hexdigest()
        snapshot = collect(key, account_id, now or datetime.now(timezone.utc), timezone_name)
        if not complete_snapshot(snapshot, account_id):
            raise ExportError("Analytics snapshot is incomplete.")
        succeeded = True
    except Exception:
        snapshot = failure_snapshot(previous, account_id, timezone_name)
        succeeded = False
    atomic_write(output, snapshot)
    return succeeded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--credential-file", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timezone", default="Europe/London")
    args = parser.parse_args()
    try:
        succeeded = run_export(args.credential_file, args.output.expanduser(), args.timezone)
    except Exception:
        print("OpenRouter account spend could not be saved; check destination access.")
        return 1
    print("OpenRouter account spend refreshed." if succeeded else
          "OpenRouter account spend unavailable; any matching last-good snapshot is marked stale.")
    return 0 if succeeded else 1


if __name__ == "__main__":
    raise SystemExit(main())
