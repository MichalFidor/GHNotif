using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using GHNotif.Core;
using Forms = System.Windows.Forms;

[assembly: InternalsVisibleTo("GHNotif.Windows.Checks")]

namespace GHNotif.Windows;

public partial class App : Application
{
    private readonly bool startAutomatically;
    public App() : this(true) { }
    internal App(bool startAutomatically) => this.startAutomatically = startAutomatically;

    private Mutex? instance;
    private bool ownsMutex;
    private Forms.NotifyIcon? tray;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly DispatcherTimer timer = new();
    private TrayWindow? panel;
    private SettingsWindow? settings;
    private JsonStore<Config> configStore = null!;
    private JsonStore<LocalState> stateStore = null!;
    private bool refreshing;
    private bool refreshQueued;
    private int revision;
    private bool baseline;
    private DateTimeOffset? lastUpdateCheck;
    private bool checkingUpdates;
    private HttpClient? updateHttp;
    internal GitHubRelease? AvailableRelease { get; private set; }
    internal string UpdateStatus { get; private set; } = "";
    internal string InstalledVersion => typeof(App).Assembly.GetName().Version!.ToString(3);

    internal async Task CheckForUpdatesAsync()
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            var release = await GitHubRelease.LatestAsync(updateHttp ?? http);
            AvailableRelease = release?.IsNewer(InstalledVersion) == true ? release : null;
            UpdateStatus = AvailableRelease is { } next ? $"Dostępna wersja {next.TagName}" : "Masz najnowszą wersję.";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            UpdateStatus = $"Nie udało się sprawdzić aktualizacji: {ex.Message}";
        }
        finally
        {
            lastUpdateCheck = DateTimeOffset.UtcNow;
            checkingUpdates = false;
            Changed?.Invoke();
        }
    }

    internal Config Config { get; private set; } = new();
    internal LocalState State { get; private set; } = new();
    internal SearchResult Results { get; private set; } = new([], []);
    internal TokenStore Tokens { get; private set; } = null!;
    internal GitHubClient Client { get; private set; } = null!;
    internal string? Error { get; private set; }
    internal DateTimeOffset? LastRefresh { get; private set; }
    internal bool IsRefreshing => refreshing;
    internal event Action? Changed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startAutomatically) return;
        instance = new Mutex(true, @"Local\GHNotif.Windows", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("GHNotif już działa. Kliknij ikonę przy zegarku.", "GHNotif");
            Shutdown();
            return;
        }
        _ = RunAsync(StartAsync);
    }

    private async Task StartAsync()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GHNotif");
        InitializeStores(folder);
        panel = new(this);
        MainWindow = panel;
        tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "GHNotif.ico")),
            Text = "GHNotif — PR-y do review", Visible = true
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => panel.Toggle());
        };
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => panel.ShowPanel());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Otwórz PR-y", null, (_, _) => Dispatcher.Invoke(() => panel.ShowPanel()));
        menu.Items.Add("Ustawienia", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Odśwież", null, (_, _) => Dispatcher.Invoke(() => _ = RunAsync(RefreshAsync)));
        menu.Items.Add("Zakończ", null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        tray.ContextMenuStrip = menu;
        timer.Interval = TimeSpan.FromMinutes(Config.RefreshMinutes);
        timer.Tick += (_, _) =>
        {
            _ = RunAsync(RefreshAsync);
            if (lastUpdateCheck is null || DateTimeOffset.UtcNow - lastUpdateCheck >= TimeSpan.FromHours(6))
                _ = CheckForUpdatesAsync();
        };
        timer.Start();
        if (Config.Users.Count == 0) OpenSettings();
        _ = CheckForUpdatesAsync();
        await RefreshAsync();
    }

    internal void InitializeStores(string folder, HttpClient? client = null)
    {
        configStore = new(Path.Combine(folder, "config.json"));
        stateStore = new(Path.Combine(folder, "state.json"));
        // Invalid files are reported without overwriting the user's configuration.
        Config = configStore.Load();
        Config.Validate();
        State = stateStore.Load();
        State.Migrate(DateTimeOffset.UtcNow);
        Tokens = new(folder);
        Client = new(client ?? http);
        updateHttp = client ?? http;
    }

    internal async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or HttpRequestException or InvalidOperationException or Win32Exception
            or CryptographicException or OperationCanceledException)
        {
            Error = ex is Win32Exception
                ? "Nie można uruchomić gh. Zainstaluj GitHub CLI lub dodaj PAT w ustawieniach."
                : ex.Message;
            if (tray is not null) tray.Text = "GHNotif — błąd; otwórz panel";
            Changed?.Invoke();
            if (panel is null || settings?.IsVisible == true)
                MessageBox.Show(Error, "GHNotif — błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (panel is null) Shutdown(1);
        }
    }

    internal async Task RefreshAsync()
    {
        if (refreshing) { refreshQueued = true; return; }
        refreshing = true;
        Changed?.Invoke();
        try
        {
            do
            {
                refreshQueued = false;
                var version = revision;
                var config = new Config
                {
                    Users = Config.Users.ToList(), ExtraQualifiers = Config.ExtraQualifiers,
                    Notify = Config.Notify, RefreshMinutes = Config.RefreshMinutes
                };
                var result = config.Users.Count == 0 ? new SearchResult([], [])
                    : await Client.SearchAsync(await Tokens.ResolveAsync(), config);
                if (version != revision) { refreshQueued = true; continue; }
                // Save seen IDs before publishing or notifying; a write failure must not look like success.
                var next = State.Clone();
                var reactivated = next.Reactivate(result.PullRequests, result.ViewerLogin);
                var fresh = next.Observe(result.PullRequests, config.Notify, baseline, reactivated);
                stateStore.Save(next);
                State = next;
                Results = result;
                baseline = false;
                Error = null;
                LastRefresh = DateTimeOffset.Now;
                UpdateTray();
                if (fresh.Count > 0 && tray is not null)
                {
                    tray.BalloonTipTitle = $"{fresh.Count} PR-ów wymaga uwagi";
                    tray.BalloonTipText = string.Join(", ", fresh.Select(pr => pr.Author).Distinct());
                    tray.ShowBalloonTip(5000);
                }
            } while (refreshQueued);
        }
        finally { refreshing = false; Changed?.Invoke(); }
    }

    internal void ToggleDismissed(PullRequest pr) => UpdateState(next =>
    {
        if (!next.Restore(pr.Id)) next.Dismiss(pr, DateTimeOffset.UtcNow);
    });

    internal void ToggleCollapsed(string repository) => UpdateState(next => next.ToggleCollapsed(repository));

    private void UpdateState(Action<LocalState> change)
    {
        var next = State.Clone();
        change(next);
        stateStore.Save(next);
        State = next;
        UpdateTray();
        Changed?.Invoke();
    }

    internal async Task SaveConfigAsync(Config config)
    {
        config.Validate();
        var changedSearch = !config.Users.SequenceEqual(Config.Users) || config.ExtraQualifiers != Config.ExtraQualifiers;
        configStore.Save(config);
        Config = config;
        baseline |= changedSearch;
        revision++;
        timer.Interval = TimeSpan.FromMinutes(Config.RefreshMinutes);
        await RefreshAsync();
    }

    internal void CredentialsChanged()
    {
        revision++;
        baseline = true;
    }

    private void UpdateTray()
    {
        if (tray is not null) tray.Text = $"GHNotif — {State.Visible(Results.PullRequests).Count()} PR-ów do review";
    }

    internal void OpenSettings()
    {
        if (settings is null)
        {
            settings = new(this);
            settings.Closed += (_, _) => settings = null;
        }
        settings.Show();
        settings.Activate();
    }

    internal static void OpenGitHub(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            throw new InvalidOperationException("Nieprawidłowy adres GitHub.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop();
        tray?.Dispose();
        http.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
