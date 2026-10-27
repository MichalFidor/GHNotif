using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Expander peopleExpander = new() { Margin = new Thickness(0, 12, 0, 0), IsExpanded = false };
    private readonly TabControl tabs = new();
    private readonly PasswordBox pat = new() { Margin = new Thickness(8) };
    private readonly TextBlock source = Ui.Text("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private bool adding;

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = "GHNotif — ustawienia";
        Width = 520;
        Height = 560;
        MinWidth = 420;
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

        var search = Page();
        search.Children.Add(Heading("Dodatkowo pokazuj"));
        mentions.IsChecked = app.Config.IncludeMentions;
        reviewRequests.IsChecked = app.Config.IncludeReviewRequests;
        search.Children.Add(mentions);
        search.Children.Add(reviewRequests);
        search.Children.Add(Ui.Text("Niezależnie od autora. Prośby o review obejmują tylko te skierowane do Ciebie osobiście, nie do zespołu — mimo to mogą mocno wydłużyć listę, dlatego są domyślnie wyłączone. Twoje własne PR-y są pomijane."));
        search.Children.Add(Heading("Zakres"));
        extra.Text = app.Config.ExtraQualifiers;
        extra.ToolTip = "Dodatkowe kwalifikatory GitHub, np. org:my-org. Puste = wszystkie dostępne repozytoria.";
        search.Children.Add(extra);
        search.Children.Add(Ui.Button("Zapisz", "Zapisz ustawienia wyszukiwania", () => _ = app.RunAsync(SaveAsync)));

        var general = Page();
        general.Children.Add(Heading("Odświeżanie co ile minut (1–60)"));
        minutes.Text = app.Config.RefreshMinutes.ToString();
        general.Children.Add(minutes);
        notify.IsChecked = app.Config.Notify;
        general.Children.Add(notify);
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

        tabs.Items.Add(Tab("Osoby", people));
        tabs.Items.Add(Tab("Wyszukiwanie", search));
        tabs.Items.Add(Tab("Ogólne", general));
        tabs.Items.Add(Tab("Token", token));
        Content = tabs;
        app.Changed += UpdateSource;
        Closed += (_, _) => app.Changed -= UpdateSource;
        RenderUsers();
        UpdateSource();
    }

    internal void SelectTab(int index) => tabs.SelectedIndex = index;

    private static StackPanel Page() => new() { Margin = new Thickness(12) };

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
    };

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
        await app.SaveConfigAsync(config);
    }

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
