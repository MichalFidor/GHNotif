using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using GHNotif.Core;

namespace GHNotif.Windows;

internal sealed class SettingsWindow : Window
{
    private readonly App app;
    private readonly StackPanel users = new();
    private readonly TextBox login = new() { MinWidth = 240, Margin = new Thickness(3) };
    private readonly TextBox extra = new() { Margin = new Thickness(8) };
    private readonly TextBox minutes = new() { Width = 60, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox notify = new() { Content = L("Powiadamiaj o nowych PR-ach i nowej aktywności", "Notify about new PRs and new activity"), Margin = new Thickness(8) };
    private readonly CheckBox mentions = new() { Content = L("PR-y, w których ktoś mnie wspomniał (@)", "PRs where someone mentioned me (@)"), Margin = new Thickness(8) };
    private readonly CheckBox reviewRequests = new() { Content = L("PR-y z prośbą o moje review", "PRs requesting my review"), Margin = new Thickness(8) };
    private readonly TextBox filter = new() { Margin = new Thickness(3, 6, 3, 6) };
    private readonly TextBox organization = new() { Margin = new Thickness(3), MinWidth = 140 };
    private readonly TextBox team = new() { Margin = new Thickness(3), MinWidth = 140 };
    private readonly TextBox excludedRepos = new() { Margin = new Thickness(8) };
    private readonly TextBox excludedLabels = new() { Margin = new Thickness(8) };
    private readonly CheckBox autoDismissReviewed = new()
    {
        Content = L("Oznaczaj jako załatwione po moim review bieżącego commita", "Mark as done after I review the current commit"),
        Margin = new Thickness(8)
    };
    private readonly CheckBox launchAtLogin = new() { Content = L("Uruchamiaj przy logowaniu", "Launch at login"), Margin = new Thickness(8) };
    private readonly ComboBox sortOrder = new() { Margin = new Thickness(8), MinWidth = 190 };
    private readonly ComboBox language = new() { Margin = new Thickness(8), MinWidth = 190 };
    private readonly Expander peopleExpander = new() { Margin = new Thickness(0, 12, 0, 0), IsExpanded = false };
    private readonly TabControl tabs = new() { TabStripPlacement = Dock.Left, Padding = new Thickness(8, 0, 0, 0) };
    private readonly PasswordBox pat = new() { Margin = new Thickness(8) };
    private readonly TextBlock source = Ui.Text("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private readonly CheckBox reminderEnabled = new() { Content = L("Pokazuj okno z listą PR-ów o stałych porach", "Show a window with the PR list at set times"), Margin = new Thickness(8) };
    private readonly CheckBox[] reminderDays = L("Pn Wt Śr Cz Pt So Nd", "Mo Tu We Th Fr Sa Su").Split(' ')
        .Select(day => new CheckBox { Content = day, Margin = new Thickness(8, 4, 8, 4) }).ToArray();
    private readonly TextBox reminderTimes = new() { Margin = new Thickness(8), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock nextReminder = Ui.Text("");
    private bool adding;

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = L("GHNotif — ustawienia", "GHNotif — Settings");
        Width = 680;
        Height = 560;
        MinWidth = 560;
        MinHeight = 400;

        var people = Page();
        people.Children.Add(Heading(L("Dodaj osobę", "Add person")));
        var add = new DockPanel();
        var addButton = Ui.Button(L("Dodaj", "Add"), L("Dodaj osobę po sprawdzeniu loginu na GitHubie", "Add a person after checking the login on GitHub"), () => _ = app.RunAsync(AddUserAsync));
        DockPanel.SetDock(addButton, Dock.Right);
        add.Children.Add(addButton);
        add.Children.Add(login);
        people.Children.Add(add);
        people.Children.Add(Ui.Text(L("Pokazywane są otwarte PR-y (bez draftów) tych osób, w dowolnym repozytorium widocznym dla tokenu.", "Shows open PRs (no drafts) by these people in any repository visible to the token.")));
        filter.ToolTip = L("Filtruj listę obserwowanych osób", "Filter the watched people list");
        filter.TextChanged += (_, _) => RenderUsers();
        var list = new StackPanel();
        list.Children.Add(filter);
        list.Children.Add(new ScrollViewer { Content = users, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        peopleExpander.Content = list;
        people.Children.Add(peopleExpander);
        people.Children.Add(Heading(L("Import zespołu GitHub", "Import a GitHub team")));
        var teamFields = new StackPanel { Orientation = Orientation.Horizontal };
        teamFields.Children.Add(organization);
        teamFields.Children.Add(team);
        people.Children.Add(teamFields);
        people.Children.Add(Ui.Button(L("Importuj członków zespołu", "Import team members"),
            L("Wymaga dostępu do członkostwa organizacji: read:org (classic) lub Members: read (fine-grained)", "Requires organization membership access: read:org (classic) or Members: read (fine-grained)"),
            () => _ = app.RunAsync(ImportTeamAsync)));
        people.Children.Add(Ui.Button(L("Importuj / eksportuj listę osób", "Import / export the people list"), L("Wymiana pliku JSON zawierającego wyłącznie loginy", "Exchange a JSON file containing only logins"),
            () => _ = app.RunAsync(ExchangeWatchlistAsync)));
        people.Children.Add(Ui.Text(L("Import zespołu wymaga dostępu do członkostwa organizacji. Eksport nie zawiera tokenu ani innych ustawień.", "Team import requires organization membership access. The export contains no token or other settings.")));

        var search = Page();
        search.Children.Add(Heading(L("Dodatkowo pokazuj", "Also show")));
        mentions.IsChecked = app.Config.IncludeMentions;
        reviewRequests.IsChecked = app.Config.IncludeReviewRequests;
        search.Children.Add(mentions);
        search.Children.Add(reviewRequests);
        search.Children.Add(Ui.Text(L("Niezależnie od autora — wzmianki obejmują też Twoje PR-y. Prośby o review obejmują tylko te skierowane do Ciebie osobiście, nie do zespołu — mimo to mogą mocno wydłużyć listę, dlatego są domyślnie wyłączone.", "Regardless of author — mentions include your own PRs too. Review requests cover only those sent to you personally, not to a team — they can still make the list much longer, so they're off by default.")));
        search.Children.Add(Heading(L("Zakres", "Scope")));
        extra.Text = app.Config.ExtraQualifiers;
        extra.ToolTip = L("Dodatkowe kwalifikatory GitHub, np. org:my-org. Puste = wszystkie dostępne repozytoria.", "Extra GitHub qualifiers, e.g. org:my-org. Empty = every accessible repository.");
        search.Children.Add(extra);
        excludedRepos.Text = string.Join(", ", app.Config.ExcludedRepositories);
        excludedLabels.Text = string.Join(", ", app.Config.ExcludedLabels);
        search.Children.Add(Heading(L("Wykluczenia", "Exclusions")));
        excludedRepos.ToolTip = L("Repozytoria owner/repo, oddzielone przecinkami", "owner/repo repositories, comma separated");
        excludedLabels.ToolTip = L("Etykiety, oddzielone przecinkami", "Labels, comma separated");
        search.Children.Add(Ui.Text(L("Repozytoria (owner/repo)", "Repositories (owner/repo)")));
        search.Children.Add(excludedRepos);
        search.Children.Add(Ui.Text(L("Etykiety", "Labels")));
        search.Children.Add(excludedLabels);
        search.Children.Add(Ui.Button(L("Zapisz", "Save"), L("Zapisz ustawienia wyszukiwania", "Save search settings"), () => _ = app.RunAsync(SaveAsync)));

        var reminders = Page();
        reminderEnabled.IsChecked = app.Config.ReviewReminder.Enabled;
        reminders.Children.Add(reminderEnabled);
        reminders.Children.Add(Ui.Text(L("Okno pojawia się na środku ekranu, nad innymi aplikacjami. Jeśli komputer był uśpiony, przypomnienie pojawi się po wybudzeniu (tego samego dnia).", "The window appears in the center of the screen, above other apps. If the computer was asleep, the reminder appears after wake (same day).")));
        reminders.Children.Add(Heading(L("Dni", "Days")));
        var days = new WrapPanel();
        for (var index = 0; index < reminderDays.Length; index++)
        {
            reminderDays[index].IsChecked = app.Config.ReviewReminder.Weekdays.Contains(index + 1);
            days.Children.Add(reminderDays[index]);
        }
        reminders.Children.Add(days);
        reminders.Children.Add(Heading(L("Godziny (GG:MM, oddzielone przecinkami)", "Times (HH:MM, comma separated)")));
        reminderTimes.Text = string.Join(", ", app.Config.ReviewReminder.Times);
        reminderTimes.ToolTip = L("Np. 09:00, 14:00", "E.g. 09:00, 14:00");
        reminders.Children.Add(reminderTimes);
        reminders.Children.Add(nextReminder);
        var reminderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        reminderButtons.Children.Add(Ui.Button(L("Zapisz", "Save"), L("Zapisz ustawienia przypomnienia", "Save reminder settings"), () => _ = app.RunAsync(SaveAsync)));
        var showNow = Ui.Button(L("Pokaż teraz", "Show now"), L("Podgląd okna przypomnienia", "Preview the reminder window"), app.ShowReviewReminder);
        showNow.Margin = new Thickness(8, 0, 0, 0);
        reminderButtons.Children.Add(showNow);
        reminders.Children.Add(reminderButtons);

        var general = Page();
        general.Children.Add(Heading(L("Język", "Language")));
        language.SelectedValuePath = "Tag";
        language.Items.Add(new ComboBoxItem { Content = L("Systemowy", "System"), Tag = AppLanguage.System });
        language.Items.Add(new ComboBoxItem { Content = "Polski", Tag = AppLanguage.Polish });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = AppLanguage.English });
        language.SelectedValue = app.Config.Language;
        language.ToolTip = L("Język aplikacji — zapisz, aby zastosować", "App language — save to apply");
        general.Children.Add(language);
        general.Children.Add(Heading(L("Odświeżanie co ile minut (1–60)", "Refresh every N minutes (1–60)")));
        minutes.Text = app.Config.RefreshMinutes.ToString();
        general.Children.Add(minutes);
        notify.IsChecked = app.Config.Notify;
        general.Children.Add(notify);
        autoDismissReviewed.IsChecked = app.Config.AutoDismissReviewed;
        general.Children.Add(autoDismissReviewed);
        sortOrder.SelectedValuePath = "Tag";
        sortOrder.Items.Add(new ComboBoxItem { Content = L("Ostatnio aktualizowane", "Recently updated"), Tag = SortOrder.RecentlyUpdated });
        sortOrder.Items.Add(new ComboBoxItem { Content = L("Najstarsze najpierw", "Oldest first"), Tag = SortOrder.OldestFirst });
        sortOrder.Items.Add(new ComboBoxItem { Content = L("Największe zmiany", "Largest changes"), Tag = SortOrder.LargestChanges });
        sortOrder.SelectedValue = app.Config.SortOrder;
        general.Children.Add(Heading(L("Sortowanie", "Sorting")));
        general.Children.Add(sortOrder);
        launchAtLogin.IsChecked = app.LaunchAtLogin;
        launchAtLogin.Checked += (_, _) => _ = app.RunAsync(() => { app.SetLaunchAtLogin(true); return Task.CompletedTask; });
        launchAtLogin.Unchecked += (_, _) => _ = app.RunAsync(() => { app.SetLaunchAtLogin(false); return Task.CompletedTask; });
        general.Children.Add(launchAtLogin);
        general.Children.Add(Ui.Button(L("Zapisz", "Save"), L("Zapisz interwał i powiadomienia", "Save general settings"), () => _ = app.RunAsync(SaveAsync)));
        general.Children.Add(Heading($"GHNotif {app.InstalledVersion}"));
        general.Children.Add(Ui.Button(L("Sprawdź aktualizacje", "Check for updates"), L("Sprawdź najnowszy release na GitHubie", "Check the latest release on GitHub"),
            () => _ = app.CheckForUpdatesAsync()));
        general.Children.Add(updateStatus);
        general.Children.Add(Ui.Text(L("Zamknięcie panelu PR-ów ukrywa go w zasobniku. Zakończenie aplikacji: prawy przycisk na ikonie → Zakończ.", "Closing the PR panel hides it in the tray. To quit: right-click the icon → Quit.")));

        var token = Page();
        token.Children.Add(Heading(L("Token GitHub", "GitHub token")));
        token.Children.Add(source);
        token.Children.Add(Ui.Text(L("Domyślnie: gh auth login. PAT ma pierwszeństwo i jest szyfrowany przez Windows DPAPI dla bieżącego użytkownika. Classic PAT: scope repo; organizacje SSO wymagają autoryzacji.", "Default: gh auth login. A PAT takes precedence and is encrypted with Windows DPAPI for the current user. Classic PAT: repo scope; SSO organizations require authorization.")));
        pat.ToolTip = L("Personal access token — nigdy nie jest zapisywany w config.json", "Personal access token — never saved in config.json");
        token.Children.Add(pat);
        token.Children.Add(Ui.Button(L("Zapisz PAT", "Save PAT"), L("Zapisz zaszyfrowany token", "Save the encrypted token"),
            () => _ = app.RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(pat.Password))
                    throw new InvalidOperationException(L("Podaj token.", "Enter a token."));
                app.Tokens.Save(pat.Password);
                pat.Clear();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));
        token.Children.Add(Ui.Button(L("Usuń PAT i użyj gh CLI", "Remove PAT and use gh CLI"), L("Usuń zapisany token i użyj konta GitHub CLI", "Remove the saved token and use the GitHub CLI account"),
            () => _ = app.RunAsync(async () =>
            {
                app.Tokens.Delete();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));

        tabs.Items.Add(Tab("\uE716", L("Osoby", "People"), people));
        tabs.Items.Add(Tab("\uE721", L("Wyszukiwanie", "Search"), search));
        tabs.Items.Add(Tab("\uEA8F", L("Przypomnienia", "Reminders"), reminders));
        tabs.Items.Add(Tab("\uE713", L("Ogólne", "General"), general));
        tabs.Items.Add(Tab("\uE8D7", "Token", token));
        Content = tabs;
        app.Changed += UpdateSource;
        Closed += (_, _) => app.Changed -= UpdateSource;
        RenderUsers();
        UpdateSource();
    }

    internal void SelectTab(int index) => tabs.SelectedIndex = index;
    internal int SelectedTab => tabs.SelectedIndex;

    private static StackPanel Page() => new() { Margin = new Thickness(12) };

    /// Left navigation entry (Windows Settings style): Segoe MDL2 glyph + label.
    private static TabItem Tab(string glyph, string header, UIElement content)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 150, Margin = new Thickness(4, 6, 4, 6) };
        label.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
            Width = 22, VerticalAlignment = VerticalAlignment.Center
        });
        label.Children.Add(new TextBlock { Text = header, FontSize = 14, Margin = new Thickness(6, 0, 0, 0) });
        return new()
        {
            Header = label,
            ToolTip = header,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    private void UpdateSource()
    {
        source.Text = L("Źródło tokenu: ", "Token source: ") + (app.Tokens.Source == "brak" ? L("brak", "none") : app.Tokens.Source);
        updateStatus.Text = app.UpdateStatus;
        var reminder = app.Config.ReviewReminder;
        nextReminder.Text = !reminder.Enabled ? L("Następne przypomnienie: wyłączone", "Next reminder: off")
            : reminder.Times.Count == 0 ? L("Następne przypomnienie: dodaj godzinę", "Next reminder: add a time")
            : reminder.Weekdays.Count == 0 ? L("Następne przypomnienie: wybierz dni", "Next reminder: choose days")
            : L($"Następne przypomnienie: {app.NextReviewReminder?.ToLocalTime().ToString("dddd HH:mm", Culture) ?? "—"}", $"Next reminder: {app.NextReviewReminder?.ToLocalTime().ToString("dddd HH:mm", Culture) ?? "—"}")
              + (app.ReviewReminderSnoozedUntil is { } snoozed && snoozed > DateTimeOffset.Now
                  ? L($" (odłożone do {snoozed.ToLocalTime():HH:mm})", $" (snoozed until {snoozed.ToLocalTime():HH:mm})") : "");
    }

    private Config Copy() => app.Config.Copy();

    private async Task SaveAsync()
    {
        if (!int.TryParse(minutes.Text, out var value) || value is < 1 or > 60)
            throw new InvalidOperationException(L("Interwał musi wynosić 1–60 minut.", "The interval must be 1–60 minutes."));
        var times = ParseValues(reminderTimes.Text);
        if (times.FirstOrDefault(time => !ReviewReminderSettings.TryParseTime(time, out _)) is { } invalid)
            throw new InvalidOperationException(L($"Nieprawidłowa godzina przypomnienia: {invalid}. Użyj formatu GG:MM, np. 09:00.", $"Invalid reminder time: {invalid}. Use the HH:MM format, e.g. 09:00."));
        var config = Copy();
        config.ExtraQualifiers = extra.Text.Trim();
        config.RefreshMinutes = value;
        config.Notify = notify.IsChecked == true;
        config.IncludeMentions = mentions.IsChecked == true;
        config.IncludeReviewRequests = reviewRequests.IsChecked == true;
        config.AutoDismissReviewed = autoDismissReviewed.IsChecked == true;
        config.ExcludedRepositories = ParseValues(excludedRepos.Text);
        config.ExcludedLabels = ParseValues(excludedLabels.Text);
        config.SortOrder = sortOrder.SelectedValue is SortOrder selected ? selected : SortOrder.RecentlyUpdated;
        if (language.SelectedValue is AppLanguage chosen) config.Language = chosen;
        config.ReviewReminder = new ReviewReminderSettings
        {
            Enabled = reminderEnabled.IsChecked == true,
            Times = times,
            Weekdays = Enumerable.Range(1, 7).Where(day => reminderDays[day - 1].IsChecked == true).ToList()
        };
        await app.SaveConfigAsync(config);
        reminderTimes.Text = string.Join(", ", app.Config.ReviewReminder.Times);
        UpdateSource();
    }

    private async Task ImportTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(organization.Text) || string.IsNullOrWhiteSpace(team.Text))
            throw new InvalidOperationException(L("Podaj organizację i slug zespołu.", "Enter the organization and team slug."));
        await app.ImportTeamAsync(organization.Text.Trim(), team.Text.Trim());
        RenderUsers();
    }

    private async Task ExchangeWatchlistAsync()
    {
        var choice = MessageBox.Show(L("Tak — eksportuj listę do pliku. Nie — importuj istniejącą listę.", "Yes — export the list to a file. No — import an existing list."),
            L("Lista obserwowanych osób", "Watched people list"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes)
        {
            var save = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ghnotif-watchlist.json" };
            if (save.ShowDialog() != true) return;
            File.WriteAllText(save.FileName, JsonSerializer.Serialize(new { formatVersion = 1, users = app.Config.Users },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var open = new OpenFileDialog { Filter = L("JSON lub tekst (*.json;*.txt)|*.json;*.txt|Wszystkie pliki|*.*", "JSON or text (*.json;*.txt)|*.json;*.txt|All files|*.*") };
        if (open.ShowDialog() != true) return;
        var text = File.ReadAllText(open.FileName);
        List<string> imported;
        if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('['))
            imported = text.Split(['\r', '\n', ',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        else using (var json = JsonDocument.Parse(text))
        {
            var root = json.RootElement;
            imported = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToList()
                : root.GetProperty("users").EnumerateArray().Select(item => item.GetString()!).ToList();
        }
        var config = Copy();
        config.Users = QueryBuilder.Normalize(config.Users.Concat(imported)).ToList();
        await app.SaveConfigAsync(config);
        RenderUsers();
    }

    private static List<string> ParseValues(string value) => value.Split([',', '\r', '\n'],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task AddUserAsync()
    {
        if (adding) return;
        var name = QueryBuilder.Normalize([login.Text]).SingleOrDefault()
            ?? throw new InvalidOperationException(L("Podaj login.", "Enter a login."));
        adding = true;
        try
        {
            var canonical = await app.Client.CanonicalLoginAsync(await app.Tokens.ResolveAsync(), name);
            var config = Copy();
            if (config.Users.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(L("Ta osoba jest już obserwowana.", "This person is already watched."));
            config.Users.Add(canonical);
            await app.SaveConfigAsync(config);
            login.Clear();
        }
        finally { adding = false; RenderUsers(); }
    }

    private void RenderUsers()
    {
        users.Children.Clear();
        peopleExpander.Header = L($"Obserwowane osoby ({app.Config.Users.Count})", $"Watched people ({app.Config.Users.Count})");
        var query = filter.Text.Trim();
        foreach (var user in app.Config.Users.Order(StringComparer.OrdinalIgnoreCase)
            .Where(u => query.Length == 0 || u.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new DockPanel();
            var remove = Ui.Button("−", L($"Przestań obserwować {user}", $"Stop watching {user}"), () => _ = app.RunAsync(async () =>
            {
                var config = Copy();
                config.Users.Remove(user);
                try { await app.SaveConfigAsync(config); }
                finally { RenderUsers(); }
            }));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(Ui.Text(user));
            users.Children.Add(row);
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 16, 8, 6)
    };
}
