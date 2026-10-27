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
    private readonly CheckBox notify = new() { Content = "Powiadamiaj o nowych PR-ach i nowej aktywności", Margin = new Thickness(8) };
    private readonly CheckBox mentions = new() { Content = "PR-y, w których ktoś mnie wspomniał (@)", Margin = new Thickness(8) };
    private readonly CheckBox reviewRequests = new() { Content = "PR-y z prośbą o moje review", Margin = new Thickness(8) };
    private readonly TextBox filter = new() { Margin = new Thickness(3, 6, 3, 6) };
    private readonly TextBox organization = new() { Margin = new Thickness(3), MinWidth = 140 };
    private readonly TextBox team = new() { Margin = new Thickness(3), MinWidth = 140 };
    private readonly TextBox excludedRepos = new() { Margin = new Thickness(8) };
    private readonly TextBox excludedLabels = new() { Margin = new Thickness(8) };
    private readonly CheckBox autoDismissReviewed = new()
    {
        Content = "Oznaczaj jako załatwione po moim review bieżącego commita",
        Margin = new Thickness(8)
    };
    private readonly CheckBox launchAtLogin = new() { Content = "Uruchamiaj przy logowaniu", Margin = new Thickness(8) };
    private readonly ComboBox sortOrder = new() { Margin = new Thickness(8), MinWidth = 190 };
    private readonly Expander peopleExpander = new() { Margin = new Thickness(0, 12, 0, 0), IsExpanded = false };
    private readonly TabControl tabs = new() { TabStripPlacement = Dock.Left, Padding = new Thickness(8, 0, 0, 0) };
    private readonly PasswordBox pat = new() { Margin = new Thickness(8) };
    private readonly TextBlock source = Ui.Text("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private bool adding;

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = "GHNotif — ustawienia";
        Width = 680;
        Height = 560;
        MinWidth = 560;
        MinHeight = 400;

        var people = Page();
        people.Children.Add(Heading("Dodaj osobę"));
        var add = new DockPanel();
        var addButton = Ui.Button("Dodaj", "Dodaj osobę po sprawdzeniu loginu na GitHubie", () => _ = app.RunAsync(AddUserAsync));
        DockPanel.SetDock(addButton, Dock.Right);
        add.Children.Add(addButton);
        add.Children.Add(login);
        people.Children.Add(add);
        people.Children.Add(Ui.Text("Pokazywane są otwarte PR-y (bez draftów) tych osób, w dowolnym repozytorium widocznym dla tokenu."));
        filter.ToolTip = "Filtruj listę obserwowanych osób";
        filter.TextChanged += (_, _) => RenderUsers();
        var list = new StackPanel();
        list.Children.Add(filter);
        list.Children.Add(new ScrollViewer { Content = users, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        peopleExpander.Content = list;
        people.Children.Add(peopleExpander);
        people.Children.Add(Heading("Import zespołu GitHub"));
        var teamFields = new StackPanel { Orientation = Orientation.Horizontal };
        teamFields.Children.Add(organization);
        teamFields.Children.Add(team);
        people.Children.Add(teamFields);
        people.Children.Add(Ui.Button("Importuj członków zespołu",
            "Wymaga dostępu do członkostwa organizacji: read:org (classic) lub Members: read (fine-grained)",
            () => _ = app.RunAsync(ImportTeamAsync)));
        people.Children.Add(Ui.Button("Importuj / eksportuj listę osób", "Wymiana pliku JSON zawierającego wyłącznie loginy",
            () => _ = app.RunAsync(ExchangeWatchlistAsync)));
        people.Children.Add(Ui.Text("Import zespołu wymaga dostępu do członkostwa organizacji. Eksport nie zawiera tokenu ani innych ustawień."));

        var search = Page();
        search.Children.Add(Heading("Dodatkowo pokazuj"));
        mentions.IsChecked = app.Config.IncludeMentions;
        reviewRequests.IsChecked = app.Config.IncludeReviewRequests;
        search.Children.Add(mentions);
        search.Children.Add(reviewRequests);
        search.Children.Add(Ui.Text("Niezależnie od autora — wzmianki obejmują też Twoje PR-y. Prośby o review obejmują tylko te skierowane do Ciebie osobiście, nie do zespołu — mimo to mogą mocno wydłużyć listę, dlatego są domyślnie wyłączone."));
        search.Children.Add(Heading("Zakres"));
        extra.Text = app.Config.ExtraQualifiers;
        extra.ToolTip = "Dodatkowe kwalifikatory GitHub, np. org:my-org. Puste = wszystkie dostępne repozytoria.";
        search.Children.Add(extra);
        excludedRepos.Text = string.Join(", ", app.Config.ExcludedRepositories);
        excludedLabels.Text = string.Join(", ", app.Config.ExcludedLabels);
        search.Children.Add(Heading("Wykluczenia"));
        excludedRepos.ToolTip = "Repozytoria owner/repo, oddzielone przecinkami";
        excludedLabels.ToolTip = "Etykiety, oddzielone przecinkami";
        search.Children.Add(Ui.Text("Repozytoria (owner/repo)"));
        search.Children.Add(excludedRepos);
        search.Children.Add(Ui.Text("Etykiety"));
        search.Children.Add(excludedLabels);
        search.Children.Add(Ui.Button("Zapisz", "Zapisz ustawienia wyszukiwania", () => _ = app.RunAsync(SaveAsync)));

        var general = Page();
        general.Children.Add(Heading("Odświeżanie co ile minut (1–60)"));
        minutes.Text = app.Config.RefreshMinutes.ToString();
        general.Children.Add(minutes);
        notify.IsChecked = app.Config.Notify;
        general.Children.Add(notify);
        autoDismissReviewed.IsChecked = app.Config.AutoDismissReviewed;
        general.Children.Add(autoDismissReviewed);
        sortOrder.SelectedValuePath = "Tag";
        sortOrder.Items.Add(new ComboBoxItem { Content = "Ostatnio aktualizowane", Tag = SortOrder.RecentlyUpdated });
        sortOrder.Items.Add(new ComboBoxItem { Content = "Najstarsze najpierw", Tag = SortOrder.OldestFirst });
        sortOrder.Items.Add(new ComboBoxItem { Content = "Największe zmiany", Tag = SortOrder.LargestChanges });
        sortOrder.SelectedValue = app.Config.SortOrder;
        general.Children.Add(Heading("Sortowanie"));
        general.Children.Add(sortOrder);
        launchAtLogin.IsChecked = app.LaunchAtLogin;
        launchAtLogin.Checked += (_, _) => _ = app.RunAsync(() => { app.SetLaunchAtLogin(true); return Task.CompletedTask; });
        launchAtLogin.Unchecked += (_, _) => _ = app.RunAsync(() => { app.SetLaunchAtLogin(false); return Task.CompletedTask; });
        general.Children.Add(launchAtLogin);
        general.Children.Add(Ui.Button("Zapisz", "Zapisz interwał i powiadomienia", () => _ = app.RunAsync(SaveAsync)));
        general.Children.Add(Heading($"GHNotif {app.InstalledVersion}"));
        general.Children.Add(Ui.Button("Sprawdź aktualizacje", "Sprawdź najnowszy release na GitHubie",
            () => _ = app.CheckForUpdatesAsync()));
        general.Children.Add(updateStatus);
        general.Children.Add(Ui.Text("Zamknięcie panelu PR-ów ukrywa go w zasobniku. Zakończenie aplikacji: prawy przycisk na ikonie → Zakończ."));

        var token = Page();
        token.Children.Add(Heading("Token GitHub"));
        token.Children.Add(source);
        token.Children.Add(Ui.Text("Domyślnie: gh auth login. PAT ma pierwszeństwo i jest szyfrowany przez Windows DPAPI dla bieżącego użytkownika. Classic PAT: scope repo; organizacje SSO wymagają autoryzacji."));
        pat.ToolTip = "Personal access token — nigdy nie jest zapisywany w config.json";
        token.Children.Add(pat);
        token.Children.Add(Ui.Button("Zapisz PAT", "Zapisz zaszyfrowany token",
            () => _ = app.RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(pat.Password))
                    throw new InvalidOperationException("Podaj token.");
                app.Tokens.Save(pat.Password);
                pat.Clear();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));
        token.Children.Add(Ui.Button("Usuń PAT i użyj gh CLI", "Usuń zapisany token i użyj konta GitHub CLI",
            () => _ = app.RunAsync(async () =>
            {
                app.Tokens.Delete();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));

        tabs.Items.Add(Tab("\uE716", "Osoby", people));
        tabs.Items.Add(Tab("\uE721", "Wyszukiwanie", search));
        tabs.Items.Add(Tab("\uE713", "Ogólne", general));
        tabs.Items.Add(Tab("\uE8D7", "Token", token));
        Content = tabs;
        app.Changed += UpdateSource;
        Closed += (_, _) => app.Changed -= UpdateSource;
        RenderUsers();
        UpdateSource();
    }

    internal void SelectTab(int index) => tabs.SelectedIndex = index;

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
        source.Text = $"Źródło tokenu: {app.Tokens.Source}";
        updateStatus.Text = app.UpdateStatus;
    }

    private Config Copy() => app.Config.Copy();

    private async Task SaveAsync()
    {
        if (!int.TryParse(minutes.Text, out var value) || value is < 1 or > 60)
            throw new InvalidOperationException("Interwał musi wynosić 1–60 minut.");
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
        await app.SaveConfigAsync(config);
    }

    private async Task ImportTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(organization.Text) || string.IsNullOrWhiteSpace(team.Text))
            throw new InvalidOperationException("Podaj organizację i slug zespołu.");
        await app.ImportTeamAsync(organization.Text.Trim(), team.Text.Trim());
        RenderUsers();
    }

    private async Task ExchangeWatchlistAsync()
    {
        var choice = MessageBox.Show("Tak — eksportuj listę do pliku. Nie — importuj istniejącą listę.",
            "Lista obserwowanych osób", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes)
        {
            var save = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ghnotif-watchlist.json" };
            if (save.ShowDialog() != true) return;
            File.WriteAllText(save.FileName, JsonSerializer.Serialize(new { formatVersion = 1, users = app.Config.Users },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var open = new OpenFileDialog { Filter = "JSON lub tekst (*.json;*.txt)|*.json;*.txt|Wszystkie pliki|*.*" };
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
            ?? throw new InvalidOperationException("Podaj login.");
        adding = true;
        try
        {
            var canonical = await app.Client.CanonicalLoginAsync(await app.Tokens.ResolveAsync(), name);
            var config = Copy();
            if (config.Users.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ta osoba jest już obserwowana.");
            config.Users.Add(canonical);
            await app.SaveConfigAsync(config);
            login.Clear();
        }
        finally { adding = false; RenderUsers(); }
    }

    private void RenderUsers()
    {
        users.Children.Clear();
        peopleExpander.Header = $"Obserwowane osoby ({app.Config.Users.Count})";
        var query = filter.Text.Trim();
        foreach (var user in app.Config.Users.Order(StringComparer.OrdinalIgnoreCase)
            .Where(u => query.Length == 0 || u.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new DockPanel();
            var remove = Ui.Button("−", $"Przestań obserwować {user}", () => _ = app.RunAsync(async () =>
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
