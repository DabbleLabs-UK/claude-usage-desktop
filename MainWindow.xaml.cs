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
        webView.Source = new Uri("http://localhost:5005/");
    }
}
