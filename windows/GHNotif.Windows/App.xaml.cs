using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
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
    private readonly DispatcherTimer reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private TrayWindow? panel;
    internal ReviewReminderWindow? ReminderWindow => reminderWindow;
    private ReviewReminderWindow? reminderWindow;
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
            UpdateStatus = AvailableRelease is { } next ? L($"Dostępna wersja {next.TagName}", $"Version {next.TagName} is available") : L("Masz najnowszą wersję.", "You have the latest version.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            UpdateStatus = L($"Nie udało się sprawdzić aktualizacji: {ex.Message}", $"Could not check for updates: {ex.Message}");
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
    internal bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("GHNotif") is string;
        }
    }
    internal event Action? Changed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startAutomatically) return;
        instance = new Mutex(true, @"Local\GHNotif.Windows", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show(L("GHNotif już działa. Kliknij ikonę przy zegarku.", "GHNotif is already running. Click the icon next to the clock."), "GHNotif");
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
            Text = L("GHNotif - PR-y do review", "GHNotif - PRs to review"), Visible = true
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => panel?.Toggle());
        };
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => panel?.ShowPanel());
        tray.ContextMenuStrip = CreateTrayMenu();
        timer.Interval = TimeSpan.FromMinutes(Config.RefreshMinutes);
        timer.Tick += (_, _) =>
        {
            _ = RunAsync(RefreshAsync);
            if (lastUpdateCheck is null || DateTimeOffset.UtcNow - lastUpdateCheck >= TimeSpan.FromHours(6))
                _ = CheckForUpdatesAsync();
        };
        timer.Start();
        reminderTimer.Tick += (_, _) => _ = RunAsync(() => { CheckReviewReminder(DateTimeOffset.Now); return Task.CompletedTask; });
        reminderTimer.Start();
        CheckReviewReminder(DateTimeOffset.Now);
        if (Config.Users.Count == 0) OpenSettings();
        _ = CheckForUpdatesAsync();
        await RefreshAsync();
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L("Otwórz PR-y", "Open PRs"), null, (_, _) => Dispatcher.Invoke(() => panel?.ShowPanel()));
        menu.Items.Add(L("Ustawienia", "Settings"), null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(L("Odśwież", "Refresh"), null, (_, _) => Dispatcher.Invoke(() => _ = RunAsync(RefreshAsync)));
        menu.Items.Add(L("Zakończ", "Quit"), null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        return menu;
    }

    internal void InitializeStores(string folder, HttpClient? client = null)
    {
        configStore = new(Path.Combine(folder, "config.json"));
        stateStore = new(Path.Combine(folder, "state.json"));
        var newInstallation = !configStore.Exists;
        // Invalid files are reported without overwriting the user's configuration.
        Config = configStore.Load();
        if (newInstallation) Config.Language = AppLanguage.System;
        Config.Validate();
        GHNotif.Core.Localization.Language = Config.Language;
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
                ? L("Nie można uruchomić gh. Zainstaluj GitHub CLI lub dodaj PAT w ustawieniach.", "Cannot run gh. Install GitHub CLI or add a PAT in settings.")
                : ex.Message;
            if (tray is not null) tray.Text = L("GHNotif - błąd; otwórz panel", "GHNotif - error; open the panel");
            Changed?.Invoke();
            if (panel is null || settings?.IsVisible == true)
                MessageBox.Show(Error, L("GHNotif - błąd", "GHNotif - error"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var config = Config.Copy();
                var result = !config.HasSearches ? new SearchResult([], [])
                    : await Client.SearchAsync(await Tokens.ResolveAsync(), config);
                if (version != revision) { refreshQueued = true; continue; }
                // Save seen IDs before publishing or notifying; a write failure must not look like success.
                var next = State.Clone();
                var wokeFromSnooze = next.ExpireSnoozes(result.PullRequests, DateTimeOffset.UtcNow);
                if (config.AutoDismissReviewed) next.AutoDismissReviewed(result.PullRequests, result.ViewerLogin);
                var reactivated = next.Reactivate(result.PullRequests, result.ViewerLogin, out var changes);
                var woke = wokeFromSnooze.ToHashSet(StringComparer.Ordinal);
                var fresh = next.ObserveAlerts(result.PullRequests, config.Notify, baseline, result.ViewerLogin,
                    reactivated, woke, changes);
                stateStore.Save(next);
                State = next;
                Results = result;
                baseline = false;
                Error = null;
                LastRefresh = DateTimeOffset.Now;
                UpdateTray();
                if (fresh.Count > 0 && tray is not null)
                {
                    var single = fresh.Count == 1 ? fresh[0] : null;
                    tray.BalloonTipTitle = Clip(single is null ? L($"{fresh.Count} PR-ów wymaga uwagi", $"{fresh.Count} PRs need attention")
                        : $"{single.PullRequest.Repository} #{single.PullRequest.Number}", 63);
                    tray.BalloonTipText = Clip(single is null
                        ? ActivityText.Summarize(fresh.Select(alert =>
                            $"#{alert.PullRequest.Number}: {alert.Details.FirstOrDefault() ?? alert.PullRequest.Title}").ToList())
                        : $"{single.PullRequest.Title}\n{ActivityText.Summarize(single.Details)}", 255);
                    tray.ShowBalloonTip(5000);
                }
            } while (refreshQueued);
        }
        finally { refreshing = false; Changed?.Invoke(); }
    }

    internal DateTimeOffset? NextReviewReminder => Config.ReviewReminder.NextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
    internal DateTimeOffset? ReviewReminderSnoozedUntil => State.ReviewReminder.SnoozedUntil;
    internal int ReviewRemindersShown { get; private set; }

    /// Called every 30 s; a reminder missed during sleep is caught up on the first tick after wake.
    internal void CheckReviewReminder(DateTimeOffset now)
    {
        var reminder = State.ReviewReminder.Copy();
        var show = reminder.ShouldShow(Config.ReviewReminder, now, TimeZoneInfo.Local);
        if (reminder.LastHandled != State.ReviewReminder.LastHandled || reminder.SnoozedUntil != State.ReviewReminder.SnoozedUntil)
            UpdateState(next => next.ReviewReminder = reminder);
        if (show) ShowReviewReminder();
    }

    internal void ShowReviewReminder()
    {
        ReviewRemindersShown++;
        if (reminderWindow is null)
        {
            reminderWindow = new(this);
            reminderWindow.Closed += (_, _) => reminderWindow = null;
        }
        reminderWindow.Present();
        _ = RunAsync(RefreshAsync);
    }

    internal void SnoozeReviewReminder(TimeSpan delay) =>
        UpdateState(next => next.ReviewReminder.SnoozedUntil = DateTimeOffset.Now + delay);

    internal void ShowPanel() => panel?.ShowPanel();

    internal void ToggleDismissed(PullRequest pr) => UpdateState(next =>
    {
        if (!next.Restore(pr.Id)) next.Dismiss(pr, DateTimeOffset.UtcNow);
    });

    internal void Snooze(PullRequest pr, DateTimeOffset until) =>
        UpdateState(next => next.Snooze(pr, until));

    internal void Unsnooze(PullRequest pr) => UpdateState(next => next.Unsnooze(pr.Id));

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
        var changedSearch = !config.Users.SequenceEqual(Config.Users) || config.ExtraQualifiers != Config.ExtraQualifiers
            || config.IncludeMentions != Config.IncludeMentions || config.IncludeReviewRequests != Config.IncludeReviewRequests
            || config.ExcludedRepositories.SequenceEqual(Config.ExcludedRepositories) == false
            || config.ExcludedLabels.SequenceEqual(Config.ExcludedLabels) == false
            || config.AutoDismissReviewed != Config.AutoDismissReviewed;
        var reminderChanged = !config.ReviewReminder.SameAs(Config.ReviewReminder);
        var languageChanged = config.Language != Config.Language;
        configStore.Save(config);
        Config = config;
        if (languageChanged) ApplyLanguage();
        // Changed schedules apply from now on; times already past today don't fire immediately.
        if (reminderChanged)
            UpdateState(next => next.ReviewReminder = new ReviewReminderState { LastHandled = DateTimeOffset.Now });
        baseline |= changedSearch;
        revision++;
        timer.Interval = TimeSpan.FromMinutes(Config.RefreshMinutes);
        await RefreshAsync();
    }

    internal async Task ImportTeamAsync(string organization, string team)
    {
        var users = await Client.TeamMembersAsync(await Tokens.ResolveAsync(), organization, team);
        var config = Config.Copy();
        config.Users = QueryBuilder.Normalize(config.Users.Concat(users)).ToList();
        await SaveConfigAsync(config);
    }

    internal void SetLaunchAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
            ?? throw new InvalidOperationException(L("Nie można otworzyć klucza autostartu Windows.", "Cannot open the Windows startup registry key."));
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException(L("Nie można ustalić ścieżki aplikacji dla autostartu.", "Cannot determine the app path for startup."));
            key.SetValue("GHNotif", $"\"{executable}\"");
        }
        else key.DeleteValue("GHNotif", false);
        Changed?.Invoke();
    }

    internal void CredentialsChanged()
    {
        revision++;
        baseline = true;
    }

    private void UpdateTray()
    {
        if (tray is not null) tray.Text = L($"GHNotif - {State.Visible(Results.PullRequests).Count()} PR-ów do review", $"GHNotif - {State.Visible(Results.PullRequests).Count()} PRs to review");
    }

    /// Windows build their texts once, so a language switch recreates the visible UI.
    private void ApplyLanguage()
    {
        GHNotif.Core.Localization.Language = Config.Language;
        if (tray is not null)
        {
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = CreateTrayMenu();
            old?.Dispose();
            UpdateTray();
        }
        if (panel is not null)
        {
            var visible = panel.IsVisible;
            panel.Retire();
            panel = new(this);
            MainWindow = panel;
            if (visible) panel.ShowPanel();
        }
        if (reminderWindow is { } reminder)
        {
            reminder.Close();
            reminderWindow = new(this);
            reminderWindow.Closed += (_, _) => reminderWindow = null;
            reminderWindow.Show();
        }
        if (settings is { } open)
        {
            var (tab, left, top) = (open.SelectedTab, open.Left, open.Top);
            open.Close();
            OpenSettings();
            settings!.Left = left;
            settings.Top = top;
            settings.SelectTab(tab);
        }
    }

    internal SettingsWindow? Settings => settings;

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

    // NotifyIcon balloons reject titles over 63 and texts over 255 characters.
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    internal static void OpenGitHub(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            throw new InvalidOperationException(L("Nieprawidłowy adres GitHub.", "Invalid GitHub URL."));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop();
        reminderTimer.Stop();
        tray?.Dispose();
        http.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
