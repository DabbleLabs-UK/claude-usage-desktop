# OpenRouter account spend

The **OpenRouter account spend** card reads OpenRouter's account-wide analytics,
not the launcher ledger and not a single API key. All API keys visible to the
configured management credential are included. There is no API-key filter and
no workspace filter. A workspace dashboard can therefore differ if the account
contains more than that one workspace. These figures are API usage costs, not
credit purchases, available balance, tax, or payment fees.

The older **Launcher Spend** card is narrower: it covers runs recorded by the
Agents & Inference launcher and its configured key-specific analytics. A zero
there does not mean the OpenRouter account has spent nothing. It is not added to
account spend because those requests can overlap.

## Figures and freshness

The app-owned exporter makes five read-only analytics queries. It never sends
an inference request or reads prompts/completions:

- Rolling 24 hours, queried at the exact start and end times.
- Rolling seven days, queried independently, with a daily average.
- This calendar week, starting Sunday at 00:00 in the configured timezone.
- Month to date, also used for the model/provider breakdown.
- Daily history covering today and the previous 30 local calendar days.

The default timezone is `Europe/London`, including daylight-saving transitions.
The monthly projections use the 24-hour and seven-day rates multiplied by 30;
they are estimates, not invoices. OpenRouter reports hourly analytics, and
newest usage can lag its actual request time. `generated_at` means a successful
analytics retrieval, not that OpenRouter has finished reporting every request.
`data_through`, when present, is the latest returned hour bucket.

Missing/null/negative/non-finite metrics, invalid timestamps, and truncated
responses fail the whole refresh. The exporter never accepts a partial total.
The API currently limits a response to 1,000 grouped rows. Accounts exceeding
that limit may need a future paginated/split-query collector; until then the
card explicitly remains stale rather than undercounting.

Failures retain a complete last-good snapshot only for the same credential
identity and mark `source.status` as `error`; neither `generated_at` nor
`last_success_at` advances. A first failure is unavailable, not `$0`. Credential
changes start a separate snapshot identity. The one-way identity hash stays in
the local summary and is not intended as display data.

## Setup on the machine with the credential

Requirements: Python 3.9+ and the IANA timezone database. Linux normally supplies
the database; Windows Python may need `python -m pip install tzdata`.

Use an existing project-scoped, gitignored secret file when appropriate. The
exporter reads one JSON field and ignores the others:

```json
{
  "_comment": "Keep this file private and out of source control.",
  "openrouter_management_api_key": "REPLACE_LOCALLY"
}
```

Do not put the key in command-line arguments, committed files, or the desktop's
sync configuration. The OpenRouter credential must be a management key with
analytics access; an ordinary inference key is not sufficient.

Example Bash command on the credential-owning machine:

```bash
python3 ~/.local/share/claude-usage/openrouter-spend-export.py \
  --credential-file ~/.config/your-app/secrets.json \
  --output ~/.local/state/claude-usage/openrouter-spend.v1.json \
  --timezone Europe/London
```

Install `tools/openrouter-spend-export.py` at that path first. The exporter uses
atomic replacement and owner-only file permissions on POSIX. It stores only
costs, request/token counts, dates, model/provider names and freshness metadata.
The management key remains on that machine.

## Windows sync

Run the installer explicitly as the Windows user who owns the desktop app.
It is opt-in, keeps backups of existing sync scripts/configuration, and does
not install a public application release. Dot-sourcing it registers no task.
It creates **ClaudeUsage OpenRouter Account Spend Sync**, using the app-owned
hidden helper rather than any Infra checkout. The task runs after sign-in and
every five minutes, including on battery. It has a four-minute execution limit,
prevents concurrent instances, and leaves the previous output intact on errors.

For a remote credential, configure SSH key authentication and a trusted host
entry first. An interactive SSH login must already work without a password.
The remote exporter has a 140-second total limit; SSH/sync has a 190-second limit.
The desktop receives only the aggregate JSON, never the management credential.

From Bash in the app repository (replace the host and secret path):

```bash
powershell.exe -NoProfile -File tools/install-openrouter-spend-sync.ps1 \
  -Mode Remote -RemoteHost your-ssh-alias \
  -CredentialFile '~/.config/your-app/secrets.json'
```

The default remote exporter/output locations match the example above. Override
them with `-RemoteExporterPath` and `-RemoteOutput` if needed. `-PythonPath`
defaults to `python3` remotely and can point to a virtual environment. The
installer does not copy anything to the SSH host automatically.

For a local credential:

```bash
powershell.exe -NoProfile -File tools/install-openrouter-spend-sync.ps1 \
  -Mode Local -CredentialFile 'C:\path\to\private\secrets.json' \
  -PythonPath 'C:\path\to\python.exe'
```

The summary defaults to `%LOCALAPPDATA%\ClaudeUsage\openrouter-spend.v1.json`.
Scripts and non-secret configuration go in
`%LOCALAPPDATA%\ClaudeUsage\openrouter-sync`. The generated configuration has
documenting `_comment` metadata. Use `-InstallDirectory`, `-Destination` and
`-Timezone` to override the defaults. `-NoStart` registers the schedule without
immediately starting a sync. The hidden helper requires Windows Script Host;
if organizational policy disables it, the scheduled task will not run.

No app window or terminal needs to remain open. The scheduled task requires the
Windows user to be signed in. Disabling this task in Task Scheduler stops future
syncs without deleting credentials, summaries, or app data.

## Development checks

```bash
python -m unittest discover -s tests/python -p 'test_openrouter_spend_export.py' -v
```

These tests use fake credentials and mocked analytics only. Do not point tests
at production secrets or replace a real summary with test fixtures.
