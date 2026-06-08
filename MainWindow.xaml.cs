using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using DabbleLabs.Wpf.About;

namespace ClaudeUsage;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await webView.EnsureCoreWebView2Async();
        // The HTML "About" button (next to the settings gear) posts "about" over the WebView2 bridge;
        // we answer by popping the shared DabbleLabs About window (a WPF control can't live in the page).
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        var port = ((App)Application.Current).SettingsService.Current.ServerPort;
        webView.Source = new Uri($"http://localhost:{port}/");
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }   // non-string payload: not ours

        if (message == "about")
            ShowAbout();
    }

    private void ShowAbout()
    {
        // Theme the shared control to match Claude Usage's dark palette (CSS vars in wwwroot/index.html).
        static SolidColorBrush Hex(string h) =>
            new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(h));

        new AboutWindow(
            appVersion: GetAppVersion(),
            tosUrl: "https://dabblelabs.uk/claude-usage-desktop/tos.html",
            privacyUrl: "https://dabblelabs.uk/claude-usage-desktop/privacy.html",
            panelBackground: Hex("#151526"),   // --surface
            textForeground: Hex("#dde1f0"),    // --text
            mutedForeground: Hex("#60688a"),   // --muted
            accentBrush: Hex("#a78bfa"))       // --accent
        {
            Owner = this
        }.ShowDialog();
    }

    /// <summary>Product version (e.g. "0.1.2") for the About window, read from the assembly.</summary>
    private static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public void OpenSettings()
    {
        if (webView.CoreWebView2 is { } cw2)
            _ = cw2.ExecuteScriptAsync("typeof window.openSettings === 'function' && window.openSettings()");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsQuitting && app.SettingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!((App)Application.Current).IsQuitting)
            Application.Current.Shutdown();
    }
}
