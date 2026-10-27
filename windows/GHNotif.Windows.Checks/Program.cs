using System.Net;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GHNotif.Core;
using GHNotif.Windows;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var result = 1;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.InvokeAsync(async () =>
        {
            try { await RunChecks(); result = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return result;
    }

    private static async Task RunChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ghnotif-ui-" + Guid.NewGuid());
        // WPF schedules OnStartup automatically; disable real tray/configuration startup for this fixture.
        var app = new App(startAutomatically: false);
        app.InitializeComponent();
        using var http = new HttpClient(new FixtureHandler());
        try
        {
            new JsonStore<Config>(Path.Combine(folder, "config.json")).Save(new Config { Users = ["example"] });
            app.InitializeStores(folder, http);
            app.Tokens.Save("fixture-token");
            Check(await app.Tokens.ResolveAsync() == "fixture-token", "DPAPI token round trip");
            Check(!File.ReadAllText(Path.Combine(folder, "token.dat")).Contains("fixture-token"),
                "Token must not be stored in plaintext");
            await app.RefreshAsync();
            Check(app.Results.PullRequests.Count == 1, "Fixture search must return one PR");
            Check(app.InstalledVersion == File.ReadAllText("VERSION").Trim(), "Version matches release metadata");
            await app.CheckForUpdatesAsync();
            Check(app.AvailableRelease?.TagName == "v99.0.0", "New release is exposed to the UI");
            var panel = new TrayWindow(app);
            panel.ShowPanel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            Check(panel.ActualHeight > 150 && panel.ActualWidth >= 500, "PR panel must not collapse");
            var preview = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * 1.5),
                (int)Math.Ceiling(panel.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            preview.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            Directory.CreateDirectory("build");
            using (var file = File.Create("build/windows-preview.png")) encoder.Save(file);
            var rendered = Buttons(panel).ToList();
            Check(rendered.Any(b => b.ToolTip?.ToString() == "Otwórz GitHub Releases (aktualizacja ręczna)"),
                "Update download button is visible");
            Check(rendered.Any(b => b.ToolTip?.ToString()?.StartsWith("Oznacz jako załatwione") == true),
                "Missing dismiss button; rendered tooltips: " + string.Join(" | ", rendered.Select(b => b.ToolTip)));
            Check(rendered.Any(b => b.ToolTip?.ToString()?.StartsWith("Kliknij: odłóż na 2 godziny") == true),
                "Snooze action is available on active PRs");
            var done = rendered.Single(b => b.ToolTip?.ToString()?.StartsWith("Oznacz jako załatwione") == true);
            done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(app.State.Visible(app.Results.PullRequests).Count() == 0, "Dismiss button removes PR");
            await app.RefreshAsync();
            Check(app.State.Visible(app.Results.PullRequests).Count() == 0, "Refresh must not restore PR");
            Check(new JsonStore<LocalState>(Path.Combine(folder, "state.json")).Load().Dismissals.ContainsKey("fixture"),
                "Dismiss saved to disk");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Pokaż załatwione PR-y"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            Buttons(panel).Single(b => Equals(b.ToolTip, "Przywróć na listę wymagających uwagi"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(app.State.Visible(app.Results.PullRequests).Count() == 1, "Restore button works");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Pokaż PR-y wymagające uwagi"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            Buttons(panel).Single(b => Equals(b.ToolTip, "Kliknij: odłóż na 2 godziny. Prawy klik: wybierz termin."))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!app.State.Visible(app.Results.PullRequests).Any(), "Snoozing hides the PR from the active list");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Pokaż odłożone PR-y"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Buttons(panel).Any(b => Equals(b.ToolTip, "Przywróć na listę wymagających uwagi")),
                "Snoozed view provides a restore action");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Przywróć na listę wymagających uwagi"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Buttons(panel).Single(b => Equals(b.ToolTip, "Pokaż PR-y wymagające uwagi"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            panel.ShowPanel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var expandedBottom = panel.Top + panel.ActualHeight;
            var expandedHeight = panel.ActualHeight;
            Buttons(panel).Single(b => Equals(b.ToolTip, "Zwiń example/demo"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(panel.ActualHeight < expandedHeight && Math.Abs(panel.Top + panel.ActualHeight - expandedBottom) < 1,
                "Collapsed panel shrinks and stays docked above the taskbar");
            Check(!Buttons(panel).Any(b => b.ToolTip?.ToString()?.StartsWith("Oznacz jako") == true)
                && app.State.Collapsed.Contains("example/demo"), "Collapsing a repo hides its rows");
            Check(new JsonStore<LocalState>(Path.Combine(folder, "state.json")).Load().Collapsed.Contains("example/demo"),
                "Collapsed repo saved to disk");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Rozwiń example/demo"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.ShowPanel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Buttons(panel).Single(b => Equals(b.ToolTip, "Ustawienia")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var openedSettings = Application.Current.Windows.OfType<SettingsWindow>().SingleOrDefault();
            Check(!panel.IsVisible && openedSettings is { IsVisible: true }, "Header settings button hides the panel and opens settings");
            openedSettings?.Close();
            var settings = new SettingsWindow(app);
            settings.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            settings.UpdateLayout();
            Check(Buttons(settings).Any(b => Equals(b.Content, "Dodaj")), "People tab renders first");
            Check(Buttons(settings).Any(b => Equals(b.Content, "Importuj członków zespołu")),
                "Team import is available in settings");
            var nav = Descendants<TabControl>(settings).Single();
            Check(nav.TabStripPlacement == Dock.Left && nav.Items.Count == 5, "Settings use a left navigation menu");
            settings.SelectTab(2);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            settings.UpdateLayout();
            var reminderBoxes = Descendants<CheckBox>(settings).ToList();
            reminderBoxes.Single(box => Equals(box.Content, "Pokazuj okno z listą PR-ów o stałych porach")).IsChecked = true;
            reminderBoxes.Single(box => Equals(box.Content, "So")).IsChecked = true;
            Descendants<TextBox>(settings).Single(box => Equals(box.ToolTip, "Np. 09:00, 14:00")).Text = "14:00, 9:00";
            Buttons(settings).Single(b => Equals(b.ToolTip, "Zapisz ustawienia przypomnienia"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && !app.Config.ReviewReminder.Enabled; i++) await Task.Delay(10);
            Check(app.Config.ReviewReminder.Enabled && app.Config.ReviewReminder.Times.SequenceEqual(["09:00", "14:00"])
                && app.Config.ReviewReminder.Weekdays.SequenceEqual([1, 2, 3, 4, 5, 6]), "Reminder settings saved from the tab");
            Check(app.State.ReviewReminder.LastHandled is not null, "Saving the schedule records a baseline");
            var tomorrowNine = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9));
            var remindersBefore = app.ReviewRemindersShown;
            var savedWeekdays = app.Config.ReviewReminder.Weekdays.ToList();
            var allDays = app.Config.Copy();
            allDays.ReviewReminder.Weekdays = [1, 2, 3, 4, 5, 6, 7];
            await app.SaveConfigAsync(allDays);
            app.CheckReviewReminder(tomorrowNine.AddMinutes(-1));
            Check(app.ReviewRemindersShown == remindersBefore, "Reminder waits for its time");
            app.CheckReviewReminder(tomorrowNine);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(app.ReviewRemindersShown == remindersBefore + 1 && app.ReminderWindow is { IsVisible: true, Topmost: true },
                "Reminder window opens on top at the scheduled time");
            Check(app.ReminderWindow!.WindowStartupLocation == WindowStartupLocation.CenterScreen, "Reminder is centered");
            app.CheckReviewReminder(tomorrowNine.AddMinutes(1));
            Check(app.ReviewRemindersShown == remindersBefore + 1, "Reminder opens once");
            Buttons(app.ReminderWindow).Single(b => Equals(b.Content, "Przypomnij za 15 min"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(app.ReminderWindow is null && app.State.ReviewReminder.SnoozedUntil is not null,
                "Snoozing closes the reminder");
            Check(new JsonStore<LocalState>(Path.Combine(folder, "state.json")).Load().ReviewReminder.SnoozedUntil is not null,
                "Reminder snooze saved to disk");
            Check(savedWeekdays.Count == 6, "Weekday selection round trip");
            settings.SelectTab(4);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            settings.UpdateLayout();
            Check(Buttons(settings).Any(b => Equals(b.Content, "Zapisz PAT")), "Token tab renders");
            settings.Close();
            panel.Close();
            Check(!panel.IsVisible, "Closing panel hides it");
            app.OpenSettings();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var polishSettings = app.Settings;
            var english = app.Config.Copy();
            english.Language = AppLanguage.English;
            await app.SaveConfigAsync(english);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            app.Settings!.UpdateLayout();
            Check(app.Settings != polishSettings && Buttons(app.Settings).Any(b => Equals(b.Content, "Add")),
                "Switching to English reopens settings in English");
            Check(new JsonStore<Config>(Path.Combine(folder, "config.json")).Load().Language == AppLanguage.English,
                "Language choice saved to disk");
            var englishPanel = new TrayWindow(app);
            englishPanel.ShowPanel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Buttons(englishPanel).Any(b => Equals(b.ToolTip, "Settings")), "Panel renders in English");
            englishPanel.Retire();
            var polish = app.Config.Copy();
            polish.Language = AppLanguage.Polish;
            await app.SaveConfigAsync(polish);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            app.Settings!.UpdateLayout();
            Check(Buttons(app.Settings).Any(b => Equals(b.Content, "Dodaj")), "Switching back to Polish restores Polish texts");
            app.Settings.Close();
            app.Tokens.Delete();
            Check(!File.Exists(Path.Combine(folder, "token.dat")), "PAT deletion");
            Console.WriteLine("All Windows UI checks passed (layout, buttons, persistence, settings, reminder, language, DPAPI).");
        }
        finally
        {
            app.Shutdown();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/releases/latest") == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"tag_name":"v99.0.0","html_url":"https://github.com/MichalFidor/GHNotif/releases/tag/v99.0.0",
                    "draft":false,"prerelease":false}
                    """)
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":{"viewer":{"login":"me"},"search":{"issueCount":1,"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{
                "id":"fixture","number":1,"title":"Example PR","url":"https://github.com/example/demo/pull/1",
                "author":{"login":"example","avatarUrl":null},"repository":{"nameWithOwner":"example/demo"},
                "createdAt":"2026-01-01T10:00:00Z","updatedAt":"2026-01-01T10:00:00Z",
                "reviewDecision":"APPROVED","commits":{"nodes":[{"commit":{"oid":"c1","statusCheckRollup":{"state":"SUCCESS"}}}]},
                "comments":{"nodes":[]},"reviews":{"nodes":[]}
                }]}}}
                """)
            });
        }
    }
}
