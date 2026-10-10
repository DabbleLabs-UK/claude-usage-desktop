import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock
from zoneinfo import ZoneInfo


MODULE_PATH = Path(__file__).resolve().parents[2] / "tools" / "openrouter-spend-export.py"
SPEC = importlib.util.spec_from_file_location("openrouter_spend_export", MODULE_PATH)
exporter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(exporter)
NOW = datetime(2026, 10, 10, 15, 28, 29, tzinfo=timezone.utc)
KEY = "unit-test-placeholder-not-a-real-key"
ACCOUNT_ID = hashlib.sha256(KEY.encode()).hexdigest()


def api_row(**changes):
    value = {"created_at__hour": "2026-10-10 15:00:00", "model": "test/model", "provider": "Test Provider",
             "credits_usage": "1.972726", "request_count": 172, "tokens_prompt": 14000000, "tokens_completion": 500000}
    value.update(changes)
    return value


def payload(rows=None, **metadata):
    return {"data": {"data": [api_row()] if rows is None else rows, "metadata": {"truncated": False, **metadata}}}


def parsed_rows():
    return exporter.parse_rows(payload(), NOW - timedelta(days=31), NOW)


def snapshot(fetch=None):
    return exporter.build_snapshot(KEY, ACCOUNT_ID, NOW, "Europe/London", fetch or (lambda *args: parsed_rows()))


class QueryTests(unittest.TestCase):
    def test_request_is_account_wide_and_uses_exact_range(self):
        start = NOW - timedelta(hours=24)
        with mock.patch.object(exporter.urllib.request, "urlopen", return_value=io.BytesIO(json.dumps(payload()).encode())) as request:
            rows = exporter.fetch_rows(KEY, start, NOW, exporter.time.monotonic() + 140)
        sent = request.call_args.args[0]
        body = json.loads(sent.data)
        self.assertEqual(sent.full_url, exporter.ANALYTICS_URL)
        self.assertEqual(sent.method, "POST")
        self.assertEqual(body["filters"], [])
        self.assertNotIn("api_key_id", json.dumps(body))
        self.assertEqual(body["time_range"], {"start": "2026-10-09T15:28:29.000Z", "end": "2026-10-10T15:28:29.000Z"})
        self.assertEqual(body["metrics"], list(exporter.METRICS))
        self.assertEqual(body["dimensions"], ["model", "provider"])
        self.assertEqual(body["granularity"], "hour")
        self.assertEqual(rows[0]["priced_runs"], 172)
        self.assertLessEqual(request.call_args.kwargs["timeout"], 30)

    def test_truncation_or_missing_metadata_rejected(self):
        for value in (payload(truncated=True), {"data": {"data": [], "metadata": {}}}, {"data": {"data": []}}):
            with self.subTest(value=value), self.assertRaises(exporter.ExportError):
                exporter.parse_rows(value, NOW - timedelta(days=1), NOW)

    def test_missing_null_negative_nonfinite_and_invalid_metrics_rejected(self):
        for metric in exporter.METRICS:
            for invalid in (None, -1, "NaN", "Infinity", float("inf"), True, {}, ""):
                with self.subTest(metric=metric, invalid=invalid), self.assertRaises(exporter.ExportError):
                    exporter.parse_rows(payload([api_row(**{metric: invalid})]), NOW - timedelta(days=1), NOW)
            missing = api_row()
            del missing[metric]
            with self.subTest(metric=metric, missing=True), self.assertRaises(exporter.ExportError):
                exporter.parse_rows(payload([missing]), NOW - timedelta(days=1), NOW)

    def test_counts_must_be_integral(self):
        for metric in ("request_count", "tokens_prompt", "tokens_completion"):
            with self.subTest(metric=metric), self.assertRaises(exporter.ExportError):
                exporter.parse_rows(payload([api_row(**{metric: 1.5})]), NOW - timedelta(days=1), NOW)

    def test_zero_request_row_still_requires_valid_metrics(self):
        with self.assertRaises(exporter.ExportError):
            exporter.parse_rows(payload([api_row(request_count=0, credits_usage=None)]), NOW - timedelta(days=1), NOW)

    def test_bucket_containing_range_start_is_allowed_without_rounding_query(self):
        start = NOW.replace(minute=17)
        rows = exporter.parse_rows(payload(), start, NOW)
        self.assertEqual(len(rows), 1)
        for timestamp in ("2026-10-10 14:00:00", "2026-10-10 16:00:00", "2026-10-10 15:01:00", "bad"):
            with self.subTest(timestamp=timestamp), self.assertRaises(exporter.ExportError):
                exporter.parse_rows(payload([api_row(created_at__hour=timestamp)]), start, NOW)

    def test_invalid_row_dimensions_rejected(self):
        for row in (None, [], api_row(model=""), api_row(provider=None)):
            with self.subTest(row=row), self.assertRaises(exporter.ExportError):
                exporter.parse_rows(payload([row]), NOW - timedelta(days=1), NOW)

    def test_network_exception_does_not_expose_response_or_key(self):
        with mock.patch.object(exporter.urllib.request, "urlopen", side_effect=OSError(KEY)):
            with self.assertRaises(exporter.ExportError) as error:
                exporter.fetch_rows(KEY, NOW - timedelta(days=1), NOW, exporter.time.monotonic() + 140)
        self.assertNotIn(KEY, str(error.exception))


class PeriodTests(unittest.TestCase):
    def test_five_distinct_exact_queries(self):
        calls = []

        def fetch(key, start, end, deadline):
            calls.append((start, end))
            return parsed_rows()

        value = snapshot(fetch)
        self.assertEqual(len(calls), 5)
        self.assertEqual(calls[0][0], NOW - timedelta(hours=24))
        self.assertEqual(calls[1][0], NOW - timedelta(days=7))
        self.assertEqual(calls[2][0], datetime(2026, 10, 3, 23, tzinfo=timezone.utc))
        self.assertEqual(calls[3][0], datetime(2026, 9, 30, 23, tzinfo=timezone.utc))
        self.assertEqual(calls[4][0], datetime(2026, 9, 9, 23, tzinfo=timezone.utc))
        self.assertTrue(all(end == NOW for _, end in calls))
        self.assertEqual(value["calendar_week"]["total_cost"], 1.972726)
        self.assertEqual(value["calendar_week"]["priced_runs"], 172)
        self.assertEqual(value["calendar_week"]["starts_at"], "2026-10-03T23:00:00.000Z")
        self.assertAlmostEqual(value["run_rates"]["monthly_at_7d_pace"], 1.972726 / 7 * 30, places=7)
        self.assertEqual(value["breakdown_by_model"][0]["output_tokens"], 500000)
        self.assertEqual(value["breakdown_by_provider"][0]["provider"], "Test Provider")
        self.assertEqual(len(value["daily_history"]), 31)
        self.assertEqual(value["daily_history"][-1]["date"], "2026-10-10")
        self.assertEqual(value["daily_history"][-1]["cost"], 1.972726)
        self.assertNotIn(KEY, json.dumps(value))

    def test_dst_calendar_boundaries_remain_local_midnight(self):
        zone = ZoneInfo("Europe/London")
        for now, expected in (
            (datetime(2026, 3, 29, 12, tzinfo=timezone.utc), "2026-03-29T00:00:00.000Z"),
            (datetime(2026, 10, 25, 12, tzinfo=timezone.utc), "2026-10-24T23:00:00.000Z"),
            (datetime(2026, 10, 26, 12, tzinfo=timezone.utc), "2026-10-24T23:00:00.000Z"),
        ):
            with self.subTest(now=now):
                ranges = exporter.query_ranges(now, zone)
                self.assertEqual(exporter.utc_text(ranges["calendar_week"]), expected)
                self.assertEqual(ranges["rolling_24h"], now - timedelta(hours=24))
                self.assertEqual(ranges["rolling_7d"], now - timedelta(hours=168))

    def test_sunday_midnight_is_new_calendar_week(self):
        now = datetime(2026, 10, 10, 23, tzinfo=timezone.utc)
        self.assertEqual(exporter.query_ranges(now, ZoneInfo("Europe/London"))["calendar_week"], now)

    def test_empty_complete_queries_are_real_zero_not_unavailable(self):
        value = snapshot(lambda *args: [])
        self.assertEqual(value["source"]["status"], "ok")
        self.assertEqual(value["rolling_24h"]["total_cost"], 0)
        self.assertEqual(value["month_to_date"]["priced_runs"], 0)
        self.assertEqual(value["breakdown_by_model"], [])
        self.assertNotIn("data_through", value)


class PersistenceTests(unittest.TestCase):
    def test_failure_preserves_only_same_account_complete_data_and_old_timestamps(self):
        previous = snapshot()
        failed = exporter.failure_snapshot(previous, ACCOUNT_ID, "Europe/London")
        self.assertEqual(failed["source"]["status"], "error")
        self.assertEqual(failed["generated_at"], previous["generated_at"])
        self.assertEqual(failed["source"]["last_success_at"], previous["source"]["last_success_at"])
        self.assertEqual(failed["calendar_week"], previous["calendar_week"])
        self.assertEqual(previous["source"]["status"], "ok")
        for account in (None, "another-account"):
            self.assertNotIn("rolling_24h", exporter.failure_snapshot(previous, account, "Europe/London"))

    def test_incomplete_prior_data_is_not_retained(self):
        previous = snapshot()
        del previous["month_to_date"]["total_cost"]
        failed = exporter.failure_snapshot(previous, ACCOUNT_ID, "Europe/London")
        self.assertNotIn("rolling_24h", failed)
        self.assertNotIn("generated_at", failed)

    def test_export_writes_atomically_and_retains_last_good_on_failed_query(self):
        with tempfile.TemporaryDirectory() as directory:
            credential = Path(directory) / "credentials.json"
            output = Path(directory) / "summary.json"
            credential.write_text(json.dumps({"openrouter_management_api_key": KEY}), encoding="utf-8")
            self.assertTrue(exporter.run_export(credential, output, now=NOW, collect=lambda *args: snapshot()))
            previous_bytes = output.read_bytes()
            previous = json.loads(previous_bytes)
            with mock.patch.object(exporter, "atomic_write", wraps=exporter.atomic_write) as writer:
                self.assertFalse(exporter.run_export(credential, output, now=NOW + timedelta(hours=1), collect=mock.Mock(side_effect=RuntimeError(KEY))))
            self.assertEqual(writer.call_count, 1)
            failed = json.loads(output.read_text())
            self.assertEqual(failed["source"]["status"], "error")
            self.assertEqual(failed["generated_at"], previous["generated_at"])
            self.assertEqual(failed["calendar_week"], previous["calendar_week"])
            self.assertNotIn(KEY, output.read_text())
            self.assertFalse(list(Path(directory).glob("*.tmp")))
            if os.name != "nt":
                self.assertEqual(output.stat().st_mode & 0o777, 0o600)

    def test_first_failure_and_changed_credential_do_not_invent_totals(self):
        with tempfile.TemporaryDirectory() as directory:
            credential = Path(directory) / "credentials.json"
            output = Path(directory) / "summary.json"
            credential.write_text(json.dumps({"openrouter_management_api_key": KEY}), encoding="utf-8")
            fail = mock.Mock(side_effect=RuntimeError("unavailable"))
            self.assertFalse(exporter.run_export(credential, output, collect=fail))
            value = json.loads(output.read_text())
            self.assertEqual(value["source"]["status"], "error")
            self.assertNotIn("rolling_24h", value)
            self.assertTrue(exporter.run_export(credential, output, collect=lambda *args: snapshot()))
            credential.write_text(json.dumps({"openrouter_management_api_key": "different-test-key"}), encoding="utf-8")
            self.assertFalse(exporter.run_export(credential, output, collect=fail))
            self.assertNotIn("rolling_24h", json.loads(output.read_text()))

    def test_missing_credentials_write_unavailable_without_retaining_unknown_account(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "summary.json"
            exporter.atomic_write(output, snapshot())
            self.assertFalse(exporter.run_export(Path(directory) / "missing.json", output))
            self.assertNotIn("generated_at", json.loads(output.read_text()))

    def test_total_collection_deadline_is_bounded(self):
        with mock.patch.object(exporter, "MAX_SECONDS", 0), mock.patch.object(exporter, "build_snapshot", side_effect=lambda *args: exporter.time.sleep(0.1)):
            with self.assertRaises(exporter.ExportError):
                exporter.bounded_snapshot(KEY, ACCOUNT_ID, NOW, "Europe/London")


@unittest.skipUnless(os.name == "nt" and shutil.which("powershell.exe"), "Windows sync integration test")
class WindowsSyncTests(unittest.TestCase):
    def test_installer_uses_absolute_paths_without_task_working_directory(self):
        with tempfile.TemporaryDirectory(prefix="account sync installer ") as directory:
            installer = str(MODULE_PATH.parent / "install-openrouter-spend-sync.ps1").replace("'", "''")
            folder = directory.replace("'", "''")
            script = r'''
function New-ScheduledTaskAction {
    param($Execute, $Argument, $WorkingDirectory)
    if ($PSBoundParameters.ContainsKey('WorkingDirectory')) { throw 'Task must not depend on a profile working directory.' }
    if (-not [IO.Path]::IsPathRooted($Execute)) { throw 'Task executable must be absolute.' }
    if ($Argument -notmatch '^//B //Nologo "[A-Za-z]:\\') { throw 'Helper path must be absolute and quoted.' }
    [pscustomobject]@{ Execute = $Execute; Arguments = $Argument }
}
function New-ScheduledTaskTrigger { param([switch]$Once, $At, $RepetitionInterval, [switch]$AtLogOn, $User); [pscustomobject]@{ Mock = $true } }
function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel); [pscustomobject]@{ Mock = $true } }
function New-ScheduledTaskSettingsSet {
    param([switch]$StartWhenAvailable, $ExecutionTimeLimit, $MultipleInstances, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries)
    if ($ExecutionTimeLimit.TotalMinutes -ne 4 -or $MultipleInstances -ne 'IgnoreNew') { throw 'Unexpected task bounds.' }
    [pscustomobject]@{ Mock = $true }
}
function Register-ScheduledTask { param($TaskName, $Action, $Trigger, $Principal, $Settings, [switch]$Force); if ($TaskName -ne 'ClaudeUsage OpenRouter Account Spend Sync') { throw 'Unexpected task name.' } }
function Start-ScheduledTask { throw 'NoStart must not launch a task.' }
'''
            script += "\n& '" + installer + "' -Mode Remote -RemoteHost fake-test-host -CredentialFile '~/.private/test.json' -SshPath powershell.exe -InstallDirectory '" + folder + "' -Destination '" + folder + "\\summary.json' -NoStart\n"
            result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue((Path(directory) / "run-hidden.vbs").is_file())
            self.assertEqual(json.loads((Path(directory) / "config.json").read_text())["remote_host"], "fake-test-host")

    def run_sync(self, directory, collector_body, previous=None):
        directory = Path(directory)
        collector = directory / "fake collector's script.py"
        collector.write_text(collector_body, encoding="utf-8")
        destination = directory / "account summary.json"
        config = {
            "mode": "Local", "remote_host": "", "python_path": sys.executable,
            "exporter_path": str(collector), "credential_file": "not-a-real-credential",
            "timezone": "Europe/London", "destination": str(destination),
        }
        config_path = directory / "sync config.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        if previous is not None:
            destination.write_text(json.dumps(previous), encoding="utf-8")
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
             str(MODULE_PATH.parent / "sync-openrouter-spend.ps1"), "-ConfigPath", str(config_path)],
            capture_output=True, text=True, timeout=30,
        )
        return result, json.loads(destination.read_text(encoding="utf-8-sig"))

    def test_local_sync_handles_spaces_preserves_failed_refresh_and_no_overlap_source(self):
        with tempfile.TemporaryDirectory(prefix="account spend ") as directory:
            success = "import json, pathlib, sys\nvalue = " + repr(snapshot()) + "\npathlib.Path(sys.argv[sys.argv.index('--output') + 1]).write_text(json.dumps(value), encoding='utf-8')\n"
            result, first = self.run_sync(directory, success)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(first["calendar_week"]["total_cost"], 1.972726)
            self.assertEqual(first["source"]["status"], "ok")
            result, failed = self.run_sync(directory, "raise SystemExit(1)\n", first)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertEqual(failed["source"]["status"], "error")
            self.assertEqual(failed["generated_at"], first["generated_at"])
            self.assertEqual(failed["calendar_week"], first["calendar_week"])
            first["_sync_source_id"] = "different-collector"
            result, replaced = self.run_sync(directory, "raise SystemExit(1)\n", first)
            self.assertEqual(result.returncode, 1)
            self.assertNotIn("calendar_week", replaced)

    def test_first_failed_sync_is_unavailable(self):
        with tempfile.TemporaryDirectory(prefix="account spend ") as directory:
            result, value = self.run_sync(directory, "raise SystemExit(1)\n")
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertEqual(value["source"]["status"], "error")
            self.assertNotIn("rolling_24h", value)


if __name__ == "__main__":
    unittest.main()
