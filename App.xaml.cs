using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using ClaudeUsage.Hubs;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClaudeUsage;

public partial class App : Application
{
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    private static Mutex? _singleInstanceMutex;

    // Single-instance "focus existing": the PRIMARY instance creates and waits on this named
    // event; a second launch opens it and Set()s it (asking the primary to surface its window),
    // then exits cleanly. Pure-.NET cross-process signal -- no window-message P/Invoke needed.
    private const string ShowEventName = "Local\\ClaudeUsage_ShowInstance_v1";
    private EventWaitHandle? _showInstanceEvent;
    private RegisteredWaitHandle? _showWaitRegistration;

    private IHost? _host;
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private bool _trayIconAvailable;

    public bool IsQuitting { get; private set; }
    public bool TrayIconAvailable => _trayIconAvailable;
    public SettingsService SettingsService { get; private set; } = null!;
    private FirewallService _firewallService = null!;

    // Debounce for NetworkChange events: NetworkAddressChanged/NetworkAvailabilityChanged fire in
    // rapid bursts during a single adapter/IP transition. The timer is (re)armed on each event and
    // only fires its action once the burst settles (NetworkSettleDelay of quiet).
    private System.Threading.Timer? _networkDebounceTimer;
    private readonly object _networkDebounceLock = new();
    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(2);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: only one poller should ever hit the endpoint at a time.
        _singleInstanceMutex = new Mutex(true, "Local\\ClaudeUsage_SingleInstance_v1", out bool createdNew);
        if (!createdNew)
        {
            // Already running. Don't start a duplicate -- instead ask the running instance to
            // surface its window so the user gets visible feedback, then exit cleanly.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            SignalExistingInstance();
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        WinForms.Application.EnableVisualStyles();

        try
        {
            SettingsService = new SettingsService();
            _firewallService = new FirewallService();
            _host = BuildWebApp();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            // Genuine can't-start (most commonly the port is already bound by another program).
            // Surface a clear message + log it instead of dying silently, then exit.
            ReportFatalStartupError(ex);
            Shutdown();
            return;
        }

        InitTrayIcon();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // React to local network changes (the USB-tether / Wi-Fi swap, link up/down). On a settled
        // change we drop any sticky 429 cooldown (the egress IP that earned it is gone) and fire an
        // immediate re-probe, so a network blip recovers in seconds instead of sitting in a wait.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        // Listen for second-launch pings only once the window exists, so a ping always has a
        // window to surface (and we never race-create a duplicate window during slow startup).
        RegisterShowInstanceListener();

        _ = Task.Run(CheckFirewallOnStartupAsync);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _networkDebounceTimer?.Dispose();
        _showWaitRegistration?.Unregister(null);
        _showInstanceEvent?.Dispose();
        _notifyIcon?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    public void Quit()
    {
        IsQuitting = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Shutdown();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null || !_mainWindow.IsLoaded)
        {
            // Window was fully closed (not just hidden-to-tray) -- recreate it.
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        else
        {
            if (!_mainWindow.IsVisible) _mainWindow.Show();           // surface from tray (Hide())
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;         // un-minimise
        }

        _mainWindow.Activate();
        // Foreground-stealing prevention can swallow Activate() when the request originates from
        // another process. Briefly flash Topmost to force the window forward, then clear it so it
        // does not actually stay always-on-top.
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    // Second-instance path: open the primary's named event and signal it to surface its window.
    // Best-effort -- if the event isn't there yet (primary still starting) we simply exit, which
    // is the old silent behaviour for that narrow window only.
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { /* signalling must never prevent the second process from exiting cleanly */ }
    }

    // Primary-instance path: create the named event and register a wait that, whenever a second
    // launch sets it, marshals to the UI thread and surfaces the window. AutoReset re-arms after
    // each ping; the registration is torn down in OnExit.
    private void RegisterShowInstanceListener()
    {
        try
        {
            _showInstanceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showInstanceEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // If this fails the app still runs single-instance; it just won't focus-existing.
        }
    }

    // Clear, logged exit when the app genuinely can't start (e.g. the port is already bound by
    // another program -- a second copy of THIS app is caught earlier by the mutex). Writes a
    // durable line (ILogger has no providers in Release) and shows a message box.
    private void ReportFatalStartupError(Exception ex)
    {
        var port = SettingsService?.Current.ServerPort.ToString() ?? "the configured port";
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeUsage", "startup-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:o}  STARTUP FAILED (port {port}): {ex}\n");
        }
        catch { /* logging is best-effort */ }

        try
        {
            WinForms.MessageBox.Show(
                $"Claude Usage couldn't start.\n\n{ex.Message}\n\n" +
                $"This usually means port {port} is already in use by another program. " +
                "Close that program (or change the port in Settings) and try again.",
                "Claude Usage",
                WinForms.MessageBoxButtons.OK,
                WinForms.MessageBoxIcon.Error);
        }
        catch { /* headless / no desktop: the log line above is the record */ }
    }

    public void ShowSettingsView()
    {
        ShowMainWindow();
        _mainWindow?.OpenSettings();
    }

    private void InitTrayIcon()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
            menu.Items.Add("Settings", null, (_, _) => ShowSettingsView());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());

            var icon = CreateTrayIcon();

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = "Claude Usage",
                ContextMenuStrip = menu,
            };
            _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
            _notifyIcon.Visible = true;
            _trayIconAvailable = true;
        }
        catch
        {
            // If tray icon init fails, _trayIconAvailable stays false.
            // MainWindow.OnClosing will not hide-to-tray in that case,
            // so the user is never left with a running but unreachable app.
            _trayIconAvailable = false;
        }
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        // Primary: load from the embedded .NET resource. This is reliable in
        // single-file publish — the assembly is bundled in the exe and
        // GetManifestResourceStream works correctly from the bundle.
        // ExtractAssociatedIcon is NOT used as primary because it delegates to
        // SHGetFileInfo/the shell image list and can return a handle that the
        // shell owns and may invalidate, producing a visible-but-blank tray slot.
        try
        {
            var asm = typeof(App).Assembly;
            var names = asm.GetManifestResourceNames();
            var name = Array.Find(names,
                n => n.EndsWith("logo_icon.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var raw = asm.GetManifestResourceStream(name)!;
                // Copy into a fresh MemoryStream so the Icon owns its backing bytes
                // independently of the resource stream's lifetime.
                var ms = new MemoryStream();
                raw.CopyTo(ms);
                ms.Position = 0;
                return new Drawing.Icon(ms);
            }
        }
        catch { }

        // Fallback: read the Win32 PE icon resources from the running exe.
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                    return icon;
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _host?.Services.GetRequiredService<UsagePoller>().TriggerImmediatePoll();
    }

    // An IP/adapter change (e.g. USB-tether took over routing, or Wi-Fi reconnected) -- always act,
    // even if the network stayed "available", because the egress IP may have changed underneath us.
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ScheduleNetworkSettleAction();

    // Link availability flipped. Only act when it comes back UP; a transition to DOWN is handled by
    // the poller's offline path (no point poking it while there's no route).
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) ScheduleNetworkSettleAction();
    }

    // (Re)arm the debounce timer; the action runs once the event burst has been quiet for
    // NetworkSettleDelay, coalescing a flurry of address/availability events into one re-probe.
    private void ScheduleNetworkSettleAction()
    {
        lock (_networkDebounceLock)
        {
            _networkDebounceTimer ??= new System.Threading.Timer(_ => OnNetworkSettled());
            _networkDebounceTimer.Change(NetworkSettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // Fires once a network change has settled: drop any sticky 429 cooldown earned by the old
    // egress IP, then trigger an immediate poll. TriggerImmediatePoll only releases the poller's
    // wake semaphore, so the one-poller-at-a-time invariant is preserved. Best-effort throughout.
    private void OnNetworkSettled()
    {
        if (_host is null) return;
        try
        {
            _host.Services.GetRequiredService<UsageService>().NotifyNetworkChanged();
            _host.Services.GetRequiredService<UsagePoller>().TriggerImmediatePoll();
        }
        catch { /* a transient resolve/poll failure here is harmless; the next cycle re-probes */ }
    }

    private async Task CheckFirewallOnStartupAsync()
    {
        var settings = SettingsService.Current;
        var port = settings.ServerPort;

        if (_firewallService.RuleExists(port))
        {
            // Only records that the port is open. Save from Current (not the stale snapshot) and
            // touch ONLY the firewall flag, so a concurrent onboarding write (e.g. a reset clearing
            // SeenPhoneOnboarding) is never clobbered. "Port is open" and "user has seen the intro"
            // are independent facts -- this must not resurrect onboarding state.
            if (!SettingsService.Current.FirewallSetupDone)
                SettingsService.Save(SettingsService.Current with { FirewallSetupDone = true });
            return;
        }

        if (settings.FirewallDeclined)
            return;

        var result = await _firewallService.TryAddRuleAsync(port);
        switch (result)
        {
            case FirewallResult.Added:
                SettingsService.Save(SettingsService.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                break;
            case FirewallResult.Declined:
                SettingsService.Save(SettingsService.Current with { FirewallDeclined = true });
                break;
        }
    }

    private static readonly string[] VirtualAdapterKeywords =
    [
        "VirtualBox", "VMware", "Hyper-V", "vEthernet", "WSL",
        "Bluetooth", "Loopback", "TAP", "Virtual", "RNDIS", "Remote NDIS"
    ];

    private static bool IsVirtualAdapter(NetworkInterface ni, string? ip = null)
    {
        if (VirtualAdapterKeywords.Any(kw =>
                ni.Description.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                ni.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            return true;
        // VirtualBox host-only subnet — belt-and-suspenders for adapters without "VirtualBox" in name
        if (ip is not null && ip.StartsWith("192.168.56."))
            return true;
        return false;
    }

    private static bool HasIpv4Gateway(NetworkInterface ni) =>
        ni.GetIPProperties().GatewayAddresses
            .Any(gw => gw.Address.AddressFamily == AddressFamily.InterNetwork);

    private static string? GetPrivateIp(NetworkInterface ni) =>
        ni.GetIPProperties().UnicastAddresses
            .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork
                      && !ua.Address.ToString().StartsWith("169.254"))
            .Select(ua => ua.Address.ToString())
            .FirstOrDefault(addr => addr.StartsWith("192.168.")
                                 || addr.StartsWith("10.")
                                 || IsPrivate172(addr));

    private static IReadOnlyList<(string Name, string Ip, bool IsVirtual)> GetAdapterCandidates()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(ni => (ni, ip: GetPrivateIp(ni)))
            .Where(t => t.ip is not null)
            .OrderByDescending(t => !IsVirtualAdapter(t.ni))
            .ThenByDescending(t => HasIpv4Gateway(t.ni))
            .ThenByDescending(t => t.ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                || t.ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .Select(t => (t.ni.Name, t.ip!, IsVirtual: IsVirtualAdapter(t.ni, t.ip)))
            .ToList();
    }

    private static string? GetLanIp(string? preferredAdapterName = null)
    {
        var candidates = GetAdapterCandidates();

        // If a preferred adapter is stored, try to find it first
        if (preferredAdapterName is not null)
        {
            var preferred = candidates.FirstOrDefault(c =>
                string.Equals(c.Name, preferredAdapterName, StringComparison.OrdinalIgnoreCase));
            if (preferred.Ip is not null) return preferred.Ip;
        }

        // Auto: prefer physical adapters first
        var autoIp = candidates.Where(c => !c.IsVirtual).Select(c => c.Ip).FirstOrDefault();
        if (autoIp is not null) return autoIp;

        // Fall back to any candidate (virtual included)
        return candidates.Select(c => c.Ip).FirstOrDefault();
    }

    private static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4
            && int.TryParse(parts[1], out var second)
            && second >= 16 && second <= 31;
    }

    private IHost BuildWebApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{SettingsService.Current.ServerPort}");

        builder.Logging.ClearProviders();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddCors(options =>
            options.AddDefaultPolicy(policy =>
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials()));

        // Serialize enums (ConnectivityState) as camelCase strings on the wire -- "noNetwork",
        // "rateLimited", etc. -- so the JS switch can compare readable values instead of ints.
        builder.Services.AddSignalR()
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddSingleton(SettingsService);
        builder.Services.AddSingleton(_firewallService);
        builder.Services.AddSingleton<UsageState>();
        builder.Services.AddSingleton<UsageLog>();
        builder.Services.AddSingleton<AuthRefreshLog>();
        builder.Services.AddSingleton<ClaudeCli>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<UsagePoller>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsagePoller>());

        var app = builder.Build();

        app.UseCors();

        var av = typeof(App).Assembly.GetName().Version;
        var assetVer = av is null ? "0" : $"{av.Major}.{av.Minor}.{av.Build}";
        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // Intercept / and /index.html before static-file middleware; inject ?v=VERSION into
        // local asset URLs so a new build busts sub-resource cache regardless of page URL.
        // Replaces UseDefaultFiles() — the root path is served directly here.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path == "/" || string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await File.ReadAllTextAsync(Path.Combine(wwwrootPath, "index.html"));
                html = html.Replace("\"logo_dabblelabs.png\"", $"\"logo_dabblelabs.png?v={assetVer}\"");

                // Discriminate the embedded desktop WebView2 (loads via localhost -> loopback
                // remote IP) from a remote LAN browser (the phone, a non-loopback IP). Server-side
                // and unspoofable. The page defaults the flag to false, so anything we can't prove
                // local is treated as remote and the phone-access setup surface stays hidden.
                var ip = ctx.Connection.RemoteIpAddress;
                var isLocalClient = ip is not null
                    && (System.Net.IPAddress.IsLoopback(ip)
                        || (ip.IsIPv4MappedToIPv6 && System.Net.IPAddress.IsLoopback(ip.MapToIPv4())));
                if (isLocalClient)
                    html = html.Replace("window.__isLocalClient = false;", "window.__isLocalClient = true;");

                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                ctx.Response.Headers["Pragma"] = "no-cache";
                await ctx.Response.WriteAsync(html);
                return;
            }
            await next(ctx);
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"] = "no-cache";
                }
            }
        });
        app.MapHub<UsageHub>("/hub/usage");

        app.MapGet("/api/usage", (UsageState state) =>
            state.Current is { } data
                ? Results.Ok(data)
                : Results.NotFound(new { message = "No data yet." }));

        app.MapGet("/api/backoff", (UsageState state) =>
            Results.Ok(state.Backoff));

        // Cumulative usage series for the stats graph. range = 1d | 7d | 30d (default 1d).
        // Returns the SESSION (five_hour) and WEEK (seven_day) series, each computed from
        // the logged samples in range via the cumulative-positive-delta calculator. Points
        // are { t: UTC ISO, v: cumulative % }. hasData is false when nothing is logged yet.
        app.MapGet("/api/usage-log", (UsageLog log, string? range) =>
        {
            var span = range switch
            {
                "30d" => TimeSpan.FromDays(30),
                "7d"  => TimeSpan.FromDays(7),
                _     => TimeSpan.FromDays(1),
            };

            var now     = DateTimeOffset.UtcNow;
            var samples = log.ReadRange(now - span, now);

            static object[] ToPoints(List<CumulativePoint> pts) =>
                pts.Select(p => (object)new
                {
                    t = p.Time.ToUniversalTime().ToString("o"),
                    v = p.Cumulative,
                }).ToArray();

            var session = CumulativeUsage.Compute(UsageLog.SeriesFor(samples, "five_hour"));
            var week    = CumulativeUsage.Compute(UsageLog.SeriesFor(samples, "seven_day"));

            return Results.Ok(new
            {
                hasData = samples.Count > 0,
                range   = range ?? "1d",
                session = ToPoints(session),
                week    = ToPoints(week),
            });
        });

        app.MapGet("/api/settings", (SettingsService settings) =>
            Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() }));

        app.MapPost("/api/settings", async (HttpRequest request, SettingsService settings) =>
        {
            // Parse the raw body so we can tell which keys the client actually sent. Needed for the
            // bool merge below: a bool can't signal "absent" once bound, and the record default would
            // silently force an omitted field back to its default. Web defaults => camelCase + case-insensitive.
            var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var body = doc.Deserialize<AppSettings>(webJson) ?? new AppSettings();
            var root = doc.RootElement;

            // claudeCliPath is a manual settings.json-only override (not in the UI form). Preserve
            // any existing value when the request omits it, so a UI save can't silently wipe it.
            var mergedCliPath = body.ClaudeCliPath ?? settings.Current.ClaudeCliPath;

            // autoRefreshLogin is a real UI toggle, but still preserve the stored value when a client
            // omits the key (older cached client) -- otherwise the record default (ON) would override
            // a user's explicit OFF. Only an explicitly-sent value changes it.
            var mergedAutoRefresh = root.ValueKind == JsonValueKind.Object
                                    && root.TryGetProperty("autoRefreshLogin", out _)
                ? body.AutoRefreshLogin
                : settings.Current.AutoRefreshLogin;

            var merged = body with { ClaudeCliPath = mergedCliPath, AutoRefreshLogin = mergedAutoRefresh };
            settings.Save(merged);
            return Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() });
        });

        app.MapGet("/api/network-info", (SettingsService settings) =>
            Results.Ok(new { lanIp = GetLanIp(settings.Current.PreferredAdapterName), port = settings.Current.ServerPort }));

        app.MapGet("/api/adapters", (SettingsService settings) =>
        {
            var preferred = settings.Current.PreferredAdapterName;
            var autoIp = GetLanIp(); // auto-selected (no preference)
            var candidates = GetAdapterCandidates();
            var result = candidates.Select(c => new
            {
                name       = c.Name,
                ip         = c.Ip,
                label      = $"{c.Name} — {c.Ip}{(c.IsVirtual ? " [virtual/VPN]" : "")}",
                isVirtual  = c.IsVirtual,
                isAutoSelected = c.Ip == autoIp && preferred is null,
            });
            return Results.Ok(result);
        });

        app.MapGet("/api/instance", () =>
            Results.Ok(new { instanceId = InstanceId }));

        app.MapGet("/api/version", () =>
        {
            var v = typeof(App).Assembly.GetName().Version;
            return Results.Ok(new { version = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}" });
        });

        app.MapPost("/api/firewall/enable", async (SettingsService settingsSvc, FirewallService firewallSvc) =>
        {
            var port = settingsSvc.Current.ServerPort;

            if (firewallSvc.RuleExists(port))
            {
                settingsSvc.Save(settingsSvc.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                return Results.Ok(new { status = "already_exists" });
            }

            // Clear declined so the attempt is recorded fresh
            settingsSvc.Save(settingsSvc.Current with { FirewallDeclined = false });

            var result = await firewallSvc.TryAddRuleAsync(port);
            switch (result)
            {
                case FirewallResult.Added:
                    settingsSvc.Save(settingsSvc.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                    return Results.Ok(new { status = "added" });
                case FirewallResult.Declined:
                    settingsSvc.Save(settingsSvc.Current with { FirewallDeclined = true });
                    return Results.Ok(new { status = "declined" });
                default:
                    return Results.Ok(new { status = "error" });
            }
        });

        // Persist that the guided phone-access onboarding has been shown, so first-run auto-show
        // happens exactly once. Uses `Current with` to avoid clobbering any other settings field.
        app.MapPost("/api/onboarding/seen", (SettingsService settings) =>
        {
            settings.Save(settings.Current with { SeenPhoneOnboarding = true });
            return Results.Ok(new { ok = true });
        });

        // Permanently dismiss a hint card by id (generic — reusable for future hint cards).
        // Appends to the persisted DismissedHints set; no-op if already present.
        app.MapPost("/api/hints/dismiss", (HintDismissRequest body, SettingsService settings) =>
        {
            if (!string.IsNullOrWhiteSpace(body?.Id))
            {
                var dismissed = settings.Current.DismissedHints?.ToList() ?? new List<string>();
                if (!dismissed.Contains(body.Id))
                {
                    dismissed.Add(body.Id);
                    settings.Save(settings.Current with { DismissedHints = dismissed.ToArray() });
                }
            }
            return Results.Ok(new { ok = true });
        });

        // Selective reset / clear-data. Only ticked categories are cleared; all-false is a no-op.
        // The settings.json-backed categories (settings/onboarding/firewall flags/autostart) are
        // merged into ONE Save() so they can't half-apply and so in-memory Current is updated --
        // nothing rewrites the old values on exit. Locked-while-running data (WebView2 cache) is
        // removed by a detached helper after this process exits; that same helper performs the
        // optional restart (sidestepping the single-instance mutex). After responding, the app
        // closes (default) or the helper relaunches it.
        app.MapPost("/api/reset", async (ResetRequest req, SettingsService settings, FirewallService firewall) =>
        {
            var any = req.Settings || req.Onboarding || req.WebView2 || req.UsageLogs || req.Firewall || req.Autostart;
            if (!any)
                return Results.Ok(new { noop = true, cleared = Array.Empty<string>(), failed = Array.Empty<string>() });

            var cleared = new List<string>();
            var failed  = new List<string>();
            string? firewallStatus = null;
            var port = settings.Current.ServerPort;

            // 1. Firewall rule (UAC elevation, inverse of the add path).
            var firewallRemoved = false;
            if (req.Firewall)
            {
                var res = await firewall.TryRemoveRuleAsync(port);
                firewallStatus = res switch
                {
                    FirewallResult.Added    => "removed",
                    FirewallResult.Declined => "declined",
                    _                       => "error",
                };
                if (res == FirewallResult.Added) { firewallRemoved = true; cleared.Add($"Firewall rule (port {port})"); }
                else failed.Add($"Firewall rule — {firewallStatus}");
            }

            // 2. Merge every settings.json-backed category into a single Save (also resets the
            //    in-memory Current and applies the autostart-registry change).
            var d = new AppSettings();
            var s = settings.Current;
            if (req.Settings)
                s = s with
                {
                    ServerPort           = d.ServerPort,
                    CloseToTray          = d.CloseToTray,
                    OrangeThreshold      = d.OrangeThreshold,
                    RedThreshold         = d.RedThreshold,
                    YellowThreshold      = d.YellowThreshold,
                    YellowGreenThreshold = d.YellowGreenThreshold,
                    PreferredAdapterName = d.PreferredAdapterName,
                };
            if (req.Onboarding)
                s = s with { SeenPhoneOnboarding = false, DismissedHints = null };
            if (firewallRemoved)
                // Mark not-set-up AND suppress the startup auto-re-add: CheckFirewallOnStartupAsync
                // would otherwise re-elevate and reopen the port on the next launch. This is cleared
                // the moment the user re-enables phone access.
                s = s with { FirewallSetupDone = false, FirewallDeclined = true };
            if (req.Autostart)
                s = s with { StartWithWindows = false };

            if (req.Settings || req.Onboarding || firewallRemoved || req.Autostart)
            {
                settings.Save(s); // persists cleared flags, updates Current, applies autostart removal
                if (req.Settings)   cleared.Add("Settings & preferences");
                if (req.Onboarding) cleared.Add("Onboarding state");
                if (req.Autostart)  cleared.Add("Start-with-Windows entry");
            }

            // Diagnostic logs are app-generated throwaway state -- clear them with preferences so a
            // "settings" reset really is fresh. Best-effort; these aren't surfaced as failures.
            if (req.Settings)
            {
                Teardown.DeleteAppDataFile("auth-refresh.log", out _);
                Teardown.DeleteAppDataFile("startup-error.log", out _);
            }

            // 3. Usage logs — JSONL files are opened per-write (not held), so they delete in-process.
            if (req.UsageLogs)
            {
                if (Teardown.DeleteAppDataSubdir("usage-log", out var why)) cleared.Add("Usage logs");
                else failed.Add($"Usage logs — {why}");
            }

            // 4. WebView2 cache — locked while running; try in-process, else defer to the exit helper.
            var deferWebView2 = false;
            if (req.WebView2)
            {
                var dir = Teardown.WebView2CacheDir();
                if (dir is null || !Directory.Exists(dir)) cleared.Add("Browser cache");
                else
                {
                    try { Directory.Delete(dir, true); cleared.Add("Browser cache"); }
                    catch { deferWebView2 = true; cleared.Add("Browser cache (cleared after close)"); }
                }
            }

            // 5. A detached helper clears the still-locked WebView2 cache and/or relaunches the app,
            //    once this process has exited (files unlock, mutex frees).
            if (deferWebView2 || req.Restart)
                ScheduleExitHelper(deferWebView2 ? Teardown.WebView2CacheDir() : null, req.Restart);

            // 6. Close (default) or restart shortly after responding, so the client can render first.
            _ = Task.Run(async () =>
            {
                await Task.Delay(900);
                await Dispatcher.InvokeAsync(() => Quit());
            });

            return Results.Ok(new
            {
                cleared,
                failed,
                firewall    = firewallStatus,
                willRestart = req.Restart,
                willClose   = true,
            });
        });

        return app;
    }

    // Spawn a detached PowerShell that waits for THIS process to exit, then (optionally) removes a
    // directory we couldn't delete while running and/or relaunches the app. Running it as a separate
    // process is what lets us clear the still-locked WebView2 cache and dodge the single-instance
    // mutex on restart. Best-effort: a failed helper just means no deferred-delete / no restart.
    private void ScheduleExitHelper(string? deleteDir, bool restart)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            static string Q(string v) => "'" + v.Replace("'", "''") + "'";

            var cmd = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; ";
            if (deleteDir is not null)
                // Retry while the WebView2 child processes finish winding down and release handles.
                cmd += $"for ($i=0; $i -lt 20; $i++) {{ try {{ Remove-Item -LiteralPath {Q(deleteDir)} -Recurse -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 300 }} }}; ";
            if (restart)
                cmd += $"Start-Process -FilePath {Q(exe)}; ";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -Command \"{cmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }
}
