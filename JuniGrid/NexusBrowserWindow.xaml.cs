using System.Windows;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JuniGrid;

/// <summary>
/// Built-in Nexus browser. Free accounts complete the full download flow here:
/// log in to Nexus → open the mod page → click "Mod Manager Download" →
/// the nxm:// redirect is intercepted at the moment it fires → handed to InstallService
/// for automatic download and installation, all without leaving the launcher.
/// Login state is kept in the WebView2 user data directory, so no re-login next time.
/// </summary>
public partial class NexusBrowserWindow : Window
{
    private readonly string _startUrl;
    private readonly bool _queueMode;
    private bool _ready;

    public NexusBrowserWindow(string startUrl, bool queueMode = false)
    {
        InitializeComponent();
        _startUrl = startUrl;
        _queueMode = queueMode;

        if (_queueMode)
        {
            var q = App.Services?.GetService<UpdateQueueService>();
            if (q is not null)
            {
                q.OnAdvanced += OnQueueAdvanced;
                Closed += (_, _) => q.OnAdvanced -= OnQueueAdvanced;
            }
            UpdateQueueTitle();
        }

        Loaded += async (_, _) => await InitAsync();
    }

    // ---- Update queue: after one install finishes → automatically open the next mod's files page ----
    private void OnQueueAdvanced()
    {
        Dispatcher.Invoke(() =>
        {
            var q = App.Services?.GetService<UpdateQueueService>();
            if (q is null) return;
            if (q.CurrentModId is int next && _ready)
            {
                web.CoreWebView2.Navigate(
                    $"https://www.nexusmods.com/stardewvalley/mods/{next}?tab=files");
            }
            UpdateQueueTitle();
        });
    }

    private void UpdateQueueTitle()
    {
        var q = App.Services?.GetService<UpdateQueueService>();
        if (q is null) return;
        Title = q.CurrentModId is not null
            ? $"Update queue {q.Done + 1}/{q.Total} — click Mod Manager Download on the page"
            : $"✅ Queue complete ({q.Total} installed) — you can close this window";
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            await web.EnsureCoreWebView2Async();
            var cwv = web.CoreWebView2;

            // Intercept nxm:// — clicking Mod Manager Download on the page ultimately navigates to this protocol
            cwv.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    var installer = App.Services?.GetService<InstallService>();
                    if (installer is not null)
                        _ = installer.HandleNxmLinkAsync(e.Uri);
                    Dispatcher.Invoke(() =>
                        Title = "✅ Download taken over — check install progress at the top of the launcher's Mods page");
                }
            };

            // When the page wants to open a new window, open it in this window instead (e.g. login redirects)
            cwv.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                cwv.Navigate(e.Uri);
            };

            cwv.SourceChanged += (_, _) =>
                Dispatcher.Invoke(() => txtUrl.Text = web.Source?.ToString() ?? "");

            web.Source = new Uri(_startUrl);
            _ready = true;
        }
        catch (Exception ex)
        {
            txtUrl.Text = "Browser initialization failed: " + ex.Message;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_ready && web.CanGoBack) web.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_ready && web.CanGoForward) web.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) web.Reload();
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        UpdateService.OpenUrl(web.Source?.ToString() ?? _startUrl);
    }
}
