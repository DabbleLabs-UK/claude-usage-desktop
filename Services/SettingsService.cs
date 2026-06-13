using System.Text.Json;
using ClaudeUsage.Models;
using Microsoft.Win32;

namespace ClaudeUsage.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string _dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsage");

    private static readonly string _filePath = Path.Combine(_dataDir, "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClaudeUsage";

    private AppSettings _current;

    public SettingsService() => _current = Load();

    public AppSettings Current => _current;

    public bool GetActualAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(RunValueName) is not null;
    }

    public void Save(AppSettings settings)
    {
        _current = settings;
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, _json));
        ApplyAutostart(settings.StartWithWindows);
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AppSettings();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, _json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    private static void ApplyAutostart(bool enable)
    {
        if (!enable) { RemoveAutostart(); return; }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            var exePath = Environment.ProcessPath;
            if (exePath is null) return;
            key.SetValue(RunValueName, $"\"{exePath}\"");
        }
        catch { }
    }

    // Remove the HKCU Run autostart entry. Single source of truth for deleting the registry value,
    // shared by the disable path of ApplyAutostart (driven by the in-app reset via Save) and by the
    // headless --uninstall-cleanup path (UninstallCleanup), so the two can't target different keys.
    public static void RemoveAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
