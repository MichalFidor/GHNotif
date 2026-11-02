using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly CheckBox teamReviewRequests = new() { Content = L("…także prośby do moich zespołów", "…also requests to my teams"), Margin = new Thickness(28, 4, 8, 8) };
    private readonly CheckBox includeDrafts = new() { Content = L("Pokazuj drafty", "Show drafts"), Margin = new Thickness(8) };
    private readonly CheckBox hideApproved = new() { Content = L("Ukrywaj zatwierdzone PR-y", "Hide approved PRs"), Margin = new Thickness(8) };
    private readonly TextBox staleDays = new() { Width = 60, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox host = new() { MinWidth = 240, Margin = new Thickness(3) };
    private readonly List<(Button Check, StackPanel Report, Button Sso)> connectionPanels = [];
    private readonly TextBlock tokenStep = Ui.Text("");
    private readonly TextBlock peopleStep = Ui.Text("");
    private readonly TextBlock searchStep = Ui.Text("");
    private readonly TextBlock notificationsStep = Ui.Text("");
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
    private readonly CheckBox[] reminderDays = L("Pn Wt Śr Cz Pt So Nd", "Mo Tu We Th Fr Sa Su").Split(' ')
        .Select(day => new CheckBox { Content = day, Margin = new Thickness(8, 4, 8, 4) }).ToArray();
    private readonly StackPanel reminderRows = new();
    private readonly List<(CheckBox Enabled, TextBox Time)> reminderAlarms = [];
    private readonly TextBlock nextReminder = Ui.Text("");
    private readonly Dictionary<AlertEvent, CheckBox> eventBoxes = [];
    private readonly CheckBox quietHours = new() { Content = L("Ciche godziny", "Quiet hours"), Margin = new Thickness(8) };
    private readonly TextBox quietFrom = new() { Width = 60, Margin = new Thickness(3), ToolTip = L("Początek cichych godzin, np. 18:00", "Quiet hours start, e.g. 18:00") };
    private readonly TextBox quietTo = new() { Width = 60, Margin = new Thickness(3), ToolTip = L("Koniec cichych godzin, np. 08:00", "Quiet hours end, e.g. 08:00") };
    private bool adding;

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = L("GHNotif - ustawienia", "GHNotif - Settings");
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
        people.Children.Add(Ui.Text(L("Pokazywane są otwarte PR-y tych osób (drafty - zależnie od ustawienia w Wyszukiwaniu), w dowolnym repozytorium widocznym dla tokenu.", "Shows open PRs by these people (drafts - depending on the Search setting) in any repository visible to the token.")));
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
        teamReviewRequests.IsChecked = app.Config.IncludeTeamReviewRequests;
        teamReviewRequests.IsEnabled = reviewRequests.IsChecked == true;
        reviewRequests.Checked += (_, _) => teamReviewRequests.IsEnabled = true;
        reviewRequests.Unchecked += (_, _) => teamReviewRequests.IsEnabled = false;
        search.Children.Add(teamReviewRequests);
        search.Children.Add(Ui.Text(L("Niezależnie od autora - wzmianki obejmują też Twoje PR-y. Prośby o review domyślnie obejmują tylko te skierowane do Ciebie osobiście. Prośby do zespołów (np. gdy ktoś doda cały zespół QA jako reviewera) są oznaczone ikoną zespołu - mogą mocno wydłużyć listę, dlatego obie opcje są domyślnie wyłączone.", "Regardless of author - mentions include your own PRs too. Review requests cover only those sent to you personally by default. Team requests (e.g. when someone adds the whole QA team as reviewer) are marked with a team icon - they can make the list much longer, so both options are off by default.")));
        search.Children.Add(Heading(L("Widoczność", "Visibility")));
        includeDrafts.IsChecked = app.Config.IncludeDrafts;
        hideApproved.IsChecked = app.Config.HideApproved;
        search.Children.Add(includeDrafts);
        search.Children.Add(hideApproved);
        search.Children.Add(Ui.Text(L("Drafty są oznaczone etykietą Draft. Zatwierdzone = decyzja review „Approved” na GitHubie - takie PR-y zwykle nie potrzebują już Twojej uwagi.", "Drafts are marked with a Draft label. Approved = the \"Approved\" review decision on GitHub - such PRs usually don't need your attention anymore.")));
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
        reminders.Children.Add(Heading(L("Dni", "Days")));
        var days = new WrapPanel();
        for (var index = 0; index < reminderDays.Length; index++)
        {
            reminderDays[index].IsChecked = app.Config.ReviewReminder.Weekdays.Contains(index + 1);
            days.Children.Add(reminderDays[index]);
        }
        reminders.Children.Add(days);
        reminders.Children.Add(Heading(L("Godziny (GG:MM)", "Times (HH:MM)")));
        reminders.Children.Add(reminderRows);
        RenderReminderAlarms(app.Config.ReviewReminder.Alarms);
        reminders.Children.Add(Ui.Button(L("+ Dodaj godzinę", "+ Add time"), L("Dodaj kolejną godzinę przypomnienia", "Add another reminder time"), AddReminderAlarm));
        reminders.Children.Add(Ui.Text(L("Włączone godziny pokazują okno z listą PR-ów na środku ekranu, nad innymi aplikacjami. Jeśli komputer był uśpiony, przypomnienie pojawi się po wybudzeniu (tego samego dnia).", "Times that are on show a window with the PR list in the center of the screen, above other apps. If the computer was asleep, the reminder appears after wake (same day).")));
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
        language.ToolTip = L("Język aplikacji - zapisz, aby zastosować", "App language - save to apply");
        general.Children.Add(language);
        general.Children.Add(Heading(L("Odświeżanie co ile minut (1-60)", "Refresh every N minutes (1-60)")));
        minutes.Text = app.Config.RefreshMinutes.ToString();
        general.Children.Add(minutes);
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
        general.Children.Add(Heading(L("Wyróżniaj PR-y otwarte od N dni (0 = wyłączone)", "Highlight PRs open for N days (0 = off)")));
        staleDays.Text = app.Config.StaleAfterDays.ToString();
        staleDays.ToolTip = L("Data otwarcia starszych PR-ów jest pokazywana na pomarańczowo", "The opening date of older PRs is shown in orange");
        general.Children.Add(staleDays);
        general.Children.Add(launchAtLogin);
        general.Children.Add(Ui.Button(L("Zapisz", "Save"), L("Zapisz ustawienia ogólne", "Save general settings"), () => _ = app.RunAsync(SaveAsync)));
        general.Children.Add(Heading($"GHNotif {app.InstalledVersion}"));
        general.Children.Add(Ui.Button(L("Sprawdź aktualizacje", "Check for updates"), L("Sprawdź najnowszy release na GitHubie", "Check the latest release on GitHub"),
            () => _ = app.CheckForUpdatesAsync()));
        general.Children.Add(updateStatus);
        general.Children.Add(Ui.Text(L("Zamknięcie panelu PR-ów ukrywa go w zasobniku. Zakończenie aplikacji: prawy przycisk na ikonie → Zakończ.", "Closing the PR panel hides it in the tray. To quit: right-click the icon → Quit.")));

        var notifications = Page();
        notify.IsChecked = app.Config.Notify;
        notifications.Children.Add(notify);
        notifications.Children.Add(Heading(L("Zdarzenia", "Events")));
        foreach (var kind in Enum.GetValues<AlertEvent>())
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            var badge = Toasts.BadgePath(kind);
            if (File.Exists(badge))
                label.Children.Add(new Image { Source = new BitmapImage(new Uri(badge)), Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0) });
            label.Children.Add(new TextBlock { Text = kind.Title(), VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox
            {
                Content = label, Margin = new Thickness(8, 4, 8, 4), ToolTip = kind.Title(),
                IsChecked = !app.Config.Notifications.IsMuted(kind), VerticalContentAlignment = VerticalAlignment.Center
            };
            eventBoxes[kind] = box;
            notifications.Children.Add(box);
        }
        notifications.Children.Add(Ui.Text(L("Wyłączone zdarzenia nie wysyłają powiadomień; PR i tak jest widoczny na liście.", "Events that are off don't send notifications; the PR still shows in the list.")));
        notifications.Children.Add(Heading(L("Ciche godziny (GG:MM)", "Quiet hours (HH:MM)")));
        quietHours.IsChecked = app.Config.Notifications.QuietHoursEnabled;
        notifications.Children.Add(quietHours);
        var quietRange = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 0, 0) };
        quietFrom.Text = app.Config.Notifications.QuietFrom;
        quietTo.Text = app.Config.Notifications.QuietTo;
        quietRange.Children.Add(new TextBlock { Text = L("Od", "From"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
        quietRange.Children.Add(quietFrom);
        quietRange.Children.Add(new TextBlock { Text = L("do", "to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
        quietRange.Children.Add(quietTo);
        notifications.Children.Add(quietRange);
        notifications.Children.Add(Ui.Text(L("W tych godzinach GHNotif nie wysyła powiadomień, ale lista PR-ów dalej się odświeża. Zakres może przechodzić przez północ.", "GHNotif sends no notifications during these hours, but the PR list keeps refreshing. The range may span midnight.")));
        notifications.Children.Add(Ui.Button(L("Zapisz", "Save"), L("Zapisz ustawienia powiadomień", "Save notification settings"), () => _ = app.RunAsync(SaveAsync)));

        var token = Page();
        token.Children.Add(Heading(L("Token GitHub", "GitHub token")));
        token.Children.Add(source);
        token.Children.Add(ConnectionPanel());
        token.Children.Add(Ui.Text(L("GHNotif widzi tylko PR-y z repozytoriów dostępnych dla tokenu. Brak PR-ów z prywatnych repozytoriów = brak scope'u repo. Widać tylko PR-y z własnych repozytoriów = token nie jest autoryzowany przez SSO dla organizacji albo organizacja blokuje classic PAT lub nie zatwierdziła GitHub CLI (wtedy GitHub ukrywa wyniki bez ostrzeżenia).", "GHNotif only sees PRs from repositories the token can access. PRs from private repositories missing = no repo scope. Only PRs from your own repositories = the token is not SSO-authorized for the organization, or the organization blocks classic PATs or hasn't approved GitHub CLI (GitHub then hides results without a warning).")));
        token.Children.Add(Heading(L("Opcja 1: GitHub CLI (najprostsza)", "Option 1: GitHub CLI (simplest)")));
        token.Children.Add(Ui.Text(L("1. Zainstaluj GitHub CLI: winget install GitHub.cli i uruchom GHNotif ponownie.\n2. W terminalu uruchom gh auth login (GitHub.com, logowanie przez przeglądarkę) i autoryzuj organizacje z SSO, jeśli GitHub o to poprosi (później: gh auth refresh).\n3. Jeśli zapisany jest PAT, usuń go - PAT ma pierwszeństwo przed gh.\nUwaga: token gh ma szerokie uprawnienia (domyślnie repo, read:org, gist, także zapis). Jeśli zależy Ci na minimalnym dostępie, użyj fine-grained PAT tylko do odczytu.", "1. Install GitHub CLI: winget install GitHub.cli and restart GHNotif.\n2. In a terminal run gh auth login (GitHub.com, sign in with the browser) and authorize SSO organizations if GitHub asks (later: gh auth refresh).\n3. If a PAT is saved, remove it - a PAT takes precedence over gh.\nNote: the gh token is broad (by default repo, read:org, gist, including write access). For minimal access use a read-only fine-grained PAT.")));
        token.Children.Add(Heading(L("Opcja 2: personal access token", "Option 2: personal access token")));
        token.Children.Add(Ui.Text(L("Fine-grained PAT tylko do odczytu (najbezpieczniejszy):\n• Resource owner - organizacja z PR-ami,\n• Repository access - All repositories,\n• Permissions (przycisk je ustawia): Pull requests - read, Commit statuses - read, Members - read (opcjonalnie, import zespołów).\nJeden token obejmuje jednego właściciela - PR-y z innych organizacji nie będą widoczne. Organizacja może wymagać zatwierdzenia tokenu przez admina.", "Read-only fine-grained PAT (most secure):\n• Resource owner - the organization with the PRs,\n• Repository access - All repositories,\n• Permissions (the button sets them): Pull requests - read, Commit statuses - read, Members - read (optional, team import).\nOne token covers one owner - PRs from other organizations won't show. The organization may require an admin to approve the token.")));
        token.Children.Add(Ui.Button(L("Utwórz fine-grained PAT tylko do odczytu", "Create a read-only fine-grained PAT"), L("Otwórz GitHub z ustawionymi uprawnieniami tylko do odczytu", "Open GitHub with read-only permissions preset"),
            () => App.OpenGitHub(app.Config.GitHubHost.NewTokenUrl)));
        token.Children.Add(Ui.Text(L("Classic PAT (gdy potrzebujesz kilku organizacji): scope repo - daje też zapis, bo GitHub nie ma scope'u tylko do odczytu dla prywatnych repozytoriów. read:org jest opcjonalny (import zespołów). Organizacje z SAML SSO: przy tokenie kliknij Configure SSO → Authorize.", "Classic PAT (when you need several organizations): the repo scope - it also grants write access, because GitHub has no read-only scope for private repositories. read:org is optional (team import). SAML SSO organizations: next to the token click Configure SSO → Authorize.")));
        token.Children.Add(Ui.Button(L("Utwórz classic PAT (tylko repo)", "Create a classic PAT (repo only)"), L("Otwórz GitHub z zaznaczonym scope'em repo", "Open GitHub with the repo scope selected"),
            () => App.OpenGitHub(app.Config.GitHubHost.NewClassicTokenUrl)));
        token.Children.Add(Ui.Text(L("PAT ma pierwszeństwo przed gh, jest szyfrowany przez Windows DPAPI dla bieżącego użytkownika i przypisany do serwera poniżej - po zmianie serwera zapisz PAT utworzony na nowym serwerze. Ustaw datę wygaśnięcia tokenu.", "A PAT takes precedence over gh, is encrypted with Windows DPAPI for the current user and bound to the server below - after changing the server save a PAT created on the new one. Set an expiration date on the token.")));
        pat.ToolTip = L("Personal access token - nigdy nie jest zapisywany w config.json", "Personal access token - never saved in config.json");
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
        token.Children.Add(Heading(L("Serwer GitHub", "GitHub server")));
        var hostRow = new DockPanel();
        var applyHost = Ui.Button(L("Zastosuj", "Apply"), L("Zapisz host serwera GitHub", "Save the GitHub server host"), () => _ = app.RunAsync(ApplyHostAsync));
        DockPanel.SetDock(applyHost, Dock.Right);
        hostRow.Children.Add(applyHost);
        host.Text = app.Config.Host;
        host.ToolTip = L("Host GitHub, np. github.com lub github.firma.pl", "GitHub host, e.g. github.com or github.company.com");
        hostRow.Children.Add(host);
        token.Children.Add(hostRow);
        token.Children.Add(Ui.Text(L("Zostaw github.com, chyba że firma używa GitHub Enterprise Server lub GHE.com - wtedy wpisz jego host, np. github.firma.pl. Token jest wysyłany tylko do tego serwera, więc wpisuj wyłącznie zaufany adres firmowy. Dla GitHub CLI zaloguj się poleceniem gh auth login --hostname HOST; PAT utwórz na tym serwerze.", "Keep github.com unless your company uses GitHub Enterprise Server or GHE.com - then enter its host, e.g. github.company.com. The token is sent only to this server, so enter a trusted company address only. For GitHub CLI sign in with gh auth login --hostname HOST; create the PAT on that server.")));

        var start = Page();
        start.Children.Add(Ui.Text(L("GHNotif pokazuje w zasobniku otwarte PR-y wybranych osób. Do działania potrzebuje tokenu GitHub i listy obserwowanych osób - reszta jest opcjonalna.", "GHNotif shows open PRs by chosen people in the tray. It needs a GitHub token and a list of watched people - everything else is optional."), Palette.Text));
        start.Children.Add(Heading(L("1. Token GitHub", "1. GitHub token")));
        start.Children.Add(tokenStep);
        start.Children.Add(StepButton(L("Ustawienia tokenu", "Token settings"), 6));
        start.Children.Add(ConnectionPanel());
        start.Children.Add(Heading(L("2. Obserwowane osoby", "2. Watched people")));
        start.Children.Add(peopleStep);
        start.Children.Add(StepButton(L("Dodaj osoby", "Add people"), 1));
        start.Children.Add(Heading(L("3. Co pokazywać (opcjonalnie)", "3. What to show (optional)")));
        start.Children.Add(searchStep);
        start.Children.Add(StepButton(L("Wyszukiwanie", "Search"), 2));
        start.Children.Add(Heading(L("4. Powiadomienia i przypomnienia (opcjonalnie)", "4. Notifications and reminders (optional)")));
        start.Children.Add(notificationsStep);
        start.Children.Add(StepButton(L("Powiadomienia", "Notifications"), 4));

        tabs.Items.Add(Tab("\uE7C1", L("Pierwsze kroki", "Get started"), start));
        tabs.Items.Add(Tab("\uE716", L("Osoby", "People"), people));
        tabs.Items.Add(Tab("\uE721", L("Wyszukiwanie", "Search"), search));
        tabs.Items.Add(Tab("\uEA8F", L("Przypomnienia", "Reminders"), reminders));
        tabs.Items.Add(Tab("\uE7E7", L("Powiadomienia", "Notifications"), notifications));
        tabs.Items.Add(Tab("\uE713", L("Ogólne", "General"), general));
        tabs.Items.Add(Tab("\uE8D7", "Token", token));
        Content = tabs;
        app.Changed += UpdateSource;
        Closed += (_, _) => app.Changed -= UpdateSource;
        RenderUsers();
        UpdateSource();
        tabs.SelectedIndex = app.IsFirstLaunch || !app.Config.HasSearches ? 0 : 1;
    }

    private Button StepButton(string text, int tab)
    {
        var button = Ui.Button(text, text, () => SelectTab(tab));
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(8, 0, 8, 4);
        return button;
    }

    /// Connection check button with its report; created per page because WPF elements have one parent.
    private StackPanel ConnectionPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
        var check = Ui.Button(L("Sprawdź połączenie", "Check connection"),
            L("Sprawdź uprawnienia tokenu i dostęp do organizacji (SSO)", "Check token scopes and organization access (SSO)"),
            () => _ = app.RunAsync(app.CheckConnectionAsync));
        var sso = Ui.Button(L("Autoryzuj SSO", "Authorize SSO"), L("Otwórz stronę autoryzacji SSO na GitHubie", "Open the SSO authorization page on GitHub"),
            () => _ = app.RunAsync(() =>
            {
                if (app.ConnectionSsoUrl is { } url) App.OpenGitHub(url);
                return Task.CompletedTask;
            }));
        sso.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(check);
        buttons.Children.Add(sso);
        var report = new StackPanel { Margin = new Thickness(4, 4, 8, 0) };
        panel.Children.Add(buttons);
        panel.Children.Add(report);
        connectionPanels.Add((check, report, sso));
        return panel;
    }

    private void UpdateConnection()
    {
        foreach (var (check, report, sso) in connectionPanels)
        {
            check.IsEnabled = !app.CheckingConnection;
            check.Content = app.CheckingConnection ? L("Sprawdzanie…", "Checking…") : L("Sprawdź połączenie", "Check connection");
            sso.Visibility = app.ConnectionSsoUrl is null ? Visibility.Collapsed : Visibility.Visible;
            report.Children.Clear();
            foreach (var line in app.ConnectionReport)
            {
                var text = Ui.Text(line, line.StartsWith('✗') ? Palette.Red : line.StartsWith('?') ? Palette.Orange : null);
                text.Margin = new Thickness(4, 1, 4, 1);
                report.Children.Add(text);
            }
        }
        var config = app.Config;
        tokenStep.Text = app.Tokens.Source == "brak"
            ? L("Zaloguj się przez GitHub CLI (gh auth login) albo zapisz PAT. Potem sprawdź połączenie.", "Sign in with GitHub CLI (gh auth login) or save a PAT. Then check the connection.")
            : L($"✓ Źródło: {app.Tokens.Source}. Sprawdź, czy token widzi Twoje organizacje.", $"✓ Source: {app.Tokens.Source}. Check that the token can see your organizations.");
        peopleStep.Text = config.Users.Count == 0
            ? L("Dodaj loginy pojedynczo albo zaimportuj cały zespół z organizacji.", "Add logins one by one or import a whole team from an organization.")
            : L($"✓ Obserwowane osoby: {config.Users.Count}.", $"✓ Watched people: {config.Users.Count}.");
        var parts = new List<string>();
        if (config.IncludeMentions) parts.Add(L("wzmianki", "mentions"));
        if (config.IncludeReviewRequests) parts.Add(L("prośby o review", "review requests"));
        if (config.IncludeReviewRequests && config.IncludeTeamReviewRequests) parts.Add(L("review zespołów", "team reviews"));
        if (config.IncludeDrafts) parts.Add(L("drafty", "drafts"));
        if (config.HideApproved) parts.Add(L("bez zatwierdzonych", "no approved"));
        var list = parts.Count == 0 ? L("tylko PR-y obserwowanych osób", "only PRs by watched people") : string.Join(", ", parts);
        searchStep.Text = L($"Teraz: {list}. Możesz też ograniczyć zakres do organizacji i wykluczyć repozytoria lub etykiety.",
            $"Now: {list}. You can also limit the scope to an organization and exclude repositories or labels.");
        notificationsStep.Text = config.Notify
            ? L("Powiadomienia włączone - możesz ustawić ciche godziny i przypomnienia o review.", "Notifications on - you can set quiet hours and review reminders.")
            : L("Powiadomienia wyłączone.", "Notifications off.");
    }

    private async Task ApplyHostAsync()
    {
        if (!GitHubHost.IsValid(host.Text))
            throw new InvalidOperationException(L("Wpisz samą nazwę hosta, np. github.firma.pl (bez portu, loginu i znaków specjalnych).",
                "Enter just the host name, e.g. github.company.com (no port, login or special characters)."));
        var config = Copy();
        config.Host = GitHubHost.Normalize(host.Text);
        await app.SaveConfigAsync(config);
        host.Text = app.Config.Host;
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
            Text = glyph, FontFamily = Ui.IconFont, FontSize = 15,
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
        UpdateConnection();
        var reminder = app.Config.ReviewReminder;
        nextReminder.Text = reminder.Alarms.Count == 0 ? L("Następne przypomnienie: dodaj godzinę", "Next reminder: add a time")
            : !reminder.Enabled ? L("Następne przypomnienie: wyłączone", "Next reminder: off")
            : reminder.Weekdays.Count == 0 ? L("Następne przypomnienie: wybierz dni", "Next reminder: choose days")
            : L($"Następne przypomnienie: {app.NextReviewReminder?.ToLocalTime().ToString("dddd HH:mm", Culture) ?? "-"}", $"Next reminder: {app.NextReviewReminder?.ToLocalTime().ToString("dddd HH:mm", Culture) ?? "-"}")
              + (app.ReviewReminderSnoozedUntil is { } snoozed && snoozed > DateTimeOffset.Now
                  ? L($" (odłożone do {snoozed.ToLocalTime():HH:mm})", $" (snoozed until {snoozed.ToLocalTime():HH:mm})") : "");
    }

    private Config Copy() => app.Config.Copy();

    private async Task SaveAsync()
    {
        if (!int.TryParse(minutes.Text, out var value) || value is < 1 or > 60)
            throw new InvalidOperationException(L("Interwał musi wynosić 1-60 minut.", "The interval must be 1-60 minutes."));
        if (!int.TryParse(staleDays.Text, out var stale) || stale is < 0 or > 365)
            throw new InvalidOperationException(L("Liczba dni musi wynosić 0-365.", "The number of days must be 0-365."));
        var alarms = reminderAlarms.Select(row => new ReminderAlarm { Time = row.Time.Text.Trim(), Enabled = row.Enabled.IsChecked == true }).ToList();
        if (alarms.FirstOrDefault(alarm => !ReviewReminderSettings.TryParseTime(alarm.Time, out _))?.Time is { } invalid)
            throw new InvalidOperationException(L($"Nieprawidłowa godzina przypomnienia: {invalid}. Użyj formatu GG:MM, np. 09:00.", $"Invalid reminder time: {invalid}. Use the HH:MM format, e.g. 09:00."));
        var from = quietFrom.Text.Trim();
        var to = quietTo.Text.Trim();
        if (!ReviewReminderSettings.TryParseTime(from, out _) || !ReviewReminderSettings.TryParseTime(to, out _))
            throw new InvalidOperationException(L("Nieprawidłowe ciche godziny. Użyj formatu GG:MM, np. 18:00.", "Invalid quiet hours. Use the HH:MM format, e.g. 18:00."));
        var config = Copy();
        foreach (var (kind, box) in eventBoxes) config.Notifications.SetMuted(kind, box.IsChecked != true);
        config.Notifications.QuietHoursEnabled = quietHours.IsChecked == true;
        config.Notifications.QuietFrom = from;
        config.Notifications.QuietTo = to;
        config.ExtraQualifiers = extra.Text.Trim();
        config.RefreshMinutes = value;
        config.Notify = notify.IsChecked == true;
        config.IncludeMentions = mentions.IsChecked == true;
        config.IncludeReviewRequests = reviewRequests.IsChecked == true;
        config.IncludeTeamReviewRequests = teamReviewRequests.IsChecked == true;
        config.IncludeDrafts = includeDrafts.IsChecked == true;
        config.HideApproved = hideApproved.IsChecked == true;
        config.StaleAfterDays = stale;
        config.AutoDismissReviewed = autoDismissReviewed.IsChecked == true;
        config.ExcludedRepositories = ParseValues(excludedRepos.Text);
        config.ExcludedLabels = ParseValues(excludedLabels.Text);
        config.SortOrder = sortOrder.SelectedValue is SortOrder selected ? selected : SortOrder.RecentlyUpdated;
        if (language.SelectedValue is AppLanguage chosen) config.Language = chosen;
        config.ReviewReminder = new ReviewReminderSettings
        {
            Alarms = alarms,
            Weekdays = Enumerable.Range(1, 7).Where(day => reminderDays[day - 1].IsChecked == true).ToList()
        };
        await app.SaveConfigAsync(config);
        RenderReminderAlarms(app.Config.ReviewReminder.Alarms);
        UpdateSource();
    }

    private void RenderReminderAlarms(IEnumerable<ReminderAlarm> alarms)
    {
        reminderRows.Children.Clear();
        reminderAlarms.Clear();
        foreach (var alarm in alarms) AddReminderRow(alarm);
    }

    private void AddReminderRow(ReminderAlarm alarm)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };
        var enabled = new CheckBox
        {
            IsChecked = alarm.Enabled, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L("Włącz lub wyłącz tę godzinę", "Turn this time on or off")
        };
        var time = new TextBox
        {
            Text = alarm.Time, Width = 70, Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = L("Godzina przypomnienia, np. 09:00", "Reminder time, e.g. 09:00")
        };
        var entry = (enabled, time);
        var remove = Ui.Button("✕", L("Usuń godzinę", "Remove time"), () =>
        {
            reminderRows.Children.Remove(row);
            reminderAlarms.Remove(entry);
        });
        row.Children.Add(enabled);
        row.Children.Add(time);
        row.Children.Add(remove);
        reminderRows.Children.Add(row);
        reminderAlarms.Add(entry);
    }

    private void AddReminderAlarm()
    {
        var times = reminderAlarms.Select(row => ReviewReminderSettings.TryParseTime(row.Time.Text, out var parsed) ? parsed : (TimeSpan?)null)
            .OfType<TimeSpan>().ToList();
        var last = times.Count == 0 ? new TimeSpan(8, 0, 0) : times.Max();
        var next = new TimeSpan(Math.Min(last.Hours + 1, 23), last.Minutes, 0);
        if (times.Contains(next)) return;
        AddReminderRow(new ReminderAlarm { Time = ReviewReminderSettings.FormatTime(next) });
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
        var choice = MessageBox.Show(L("Tak - eksportuj listę do pliku. Nie - importuj istniejącą listę.", "Yes - export the list to a file. No - import an existing list."),
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
