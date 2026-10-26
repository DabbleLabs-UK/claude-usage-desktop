# Codex outage and recovery review - 26 October 2026

## Evidence and limits

The existing local poll.log records (UTC; also UK local time on this date):

| Time | Codex collector result |
| --- | --- |
| 13:02:53.490 | Successful reading |
| 13:05:53.766 through 13:32:55.477 | HTTP 401 at the unchanged three-minute cadence |
| 13:36:02 | Installed CU process creation time from Windows process metadata |
| 13:36:04.546 | First recorded post-restart request also returned HTTP 401 |
| 13:39:04.795 | HTTP 401 |
| 13:42:05.159 | Successful automatic reading, without another restart |

All Codex polls inspected since 13:00 used the local default auth.json source.
The collector runs inside ClaudeUsage.exe, as its independent CodexPoller
background service. It reads local Windows Credential Manager first, then the
collector process's CODEX_HOME/auth.json or local profile .codex/auth.json.
There is no Codex relay or DELL credential synchronization in this path. A DELL
TUI sign-in does not by itself refresh this collector's saved credentials.

The owner reports subscription expiry and subsequent renewal. The 401 timeline
is consistent with that report, including delayed recovery, but the collector
did not record upstream error bodies or entitlement evidence. It cannot prove
subscription expiry from a 401, distinguish entitlement propagation from a
credential rotation, or establish an exact renewal-to-recovery duration. The
separately observed desktop workspace-routing 401 is consistent with account
access trouble, but came from a different operation; shared credentials and an
identical failure mechanism are not established by these CU logs.

No credentials, tokens, account identifiers or upstream bodies were copied
into this review. No sign-out, token refresh or remote TUI operation was run.

## What worked

The fixed 180-second wait after each Codex poll continued through rejection and
recovered automatically. Authentication files were reread on each cycle, without
CU refreshing or writing them. In-process last-known Codex readings were marked
stale. Claude and OpenRouter retained their independent failure domains.

## Restart fallback

CodexUsageState is in memory only. A new process starts without a home snapshot;
the usage endpoint returns 404 until a successful reading. Monthly account-bound
quota history and credit history are persisted, but are not full home snapshots
and are not used to seed this state. Claude separately restores its full last
usage-log sample as stale. Its latest monthly file was last written on 17 October.
OpenRouter independently rereads its own exported summary. This explains the
missing Codex Week/Credits cards and apparently older fallback; it is not evidence
that Codex history was erased or that OpenRouter supplied Codex data.

## Bounded changes

* Keep existing Codex states and request behavior, but pass safe typed reasons
  for missing/expired local credentials versus HTTP 401/403 rejection, numeric
  error status, and last/approximate next check times to the UI.
* Use neutral access wording for rejection, not an inferred subscription or a
  compulsory sign-in remedy; make automatic checking explicit in expanded status.
* Refresh status reads only that provider's existing cached local endpoints,
  acknowledges pending/completed/partial/failed reads and prevents duplicate
  clicks while pending. It never forces an upstream usage request.
* Explain missing current-session data/history and identify the footer's Claude
  timestamp so old Claude state cannot be mistaken for Codex fallback.
* Version the updated shared status asset through the existing cache-busting path.

No change to Claude acquisition, either credential resolver, provider endpoint,
headers, token expiry rule, timeouts, retry cadence, history files or restoration
policy. No upstream bodies are inspected or relayed. A persisted Codex home
snapshot remains an optional future feature requiring explicit account binding,
latest-empty-snapshot rules, credit/unlimited fidelity and stale forecast tests;
it is not introduced as an outage repair.
