using System.ComponentModel;
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
        await webView.EnsureCoreWebView2Async();
        var port = ((App)Application.Current).SettingsService.Current.ServerPort;
        webView.Source = new Uri($"http://localhost:{port}/");
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
