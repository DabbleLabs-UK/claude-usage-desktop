using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace ClaudeUsage;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
        }
        catch
        {
            return;
        }

        if (webView.CoreWebView2 is null)
            return;

        // Page links (About modal, update banner) use target="_blank". Route those new-window requests
        // to the user's default browser instead of letting WebView2 spawn its own popup window.
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

        var port = ((App)Application.Current).SettingsService.Current.ServerPort;
        var vx = typeof(MainWindow).Assembly.GetName().Version;
        var ver = vx is null ? "0" : $"{vx.Major}.{vx.Minor}.{vx.Build}";
        webView.Source = new Uri($"http://localhost:{port}/?v={ver}");
    }

    private void OnNewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;   // don't open a WebView2 popup
        try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
        catch { /* no browser / blocked: opening a link must never crash the host */ }
    }

    public void OpenSettings()
    {
        if (webView.CoreWebView2 is { } cw2)
            _ = cw2.ExecuteScriptAsync("typeof window.openSettings === 'function' && window.openSettings()");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsQuitting && app.TrayIconAvailable && app.SettingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Do NOT call Application.Shutdown() here. The server and tray run for the full
        // application lifetime -- only App.Quit() (tray "Quit") should stop them.
        // Calling Shutdown() here was the bug: it killed Kestrel whenever CloseToTray
        // was false and the user clicked X, even though the server should keep serving.
        base.OnClosed(e);
    }
}
