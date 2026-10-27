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
    private readonly CheckBox notify = new() { Content = "Powiadomienia o nowych PR-ach", Margin = new Thickness(8) };
    private readonly PasswordBox pat = new() { Margin = new Thickness(8) };
    private readonly TextBlock source = Ui.Text("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private bool adding;

    internal SettingsWindow(App app)
    {
        this.app = app;
        Title = "GHNotif — ustawienia";
        Width = 500;
        Height = 700;
        MinWidth = 420;
        MinHeight = 400;
        var content = new StackPanel { Margin = new Thickness(12) };
        content.Children.Add(Heading($"GHNotif {app.InstalledVersion}"));
        content.Children.Add(Ui.Button("Sprawdź aktualizacje", "Sprawdź najnowszy release na GitHubie",
            () => _ = app.CheckForUpdatesAsync()));
        content.Children.Add(updateStatus);
        content.Children.Add(Heading("Obserwowane osoby"));
        var add = new DockPanel();
        var addButton = Ui.Button("Dodaj", "Dodaj osobę po sprawdzeniu loginu na GitHubie", () => _ = app.RunAsync(AddUserAsync));
        DockPanel.SetDock(addButton, Dock.Right);
        add.Children.Add(addButton);
        add.Children.Add(login);
        content.Children.Add(add);
        content.Children.Add(users);
        content.Children.Add(Heading("Zakres wyszukiwania"));
        extra.Text = app.Config.ExtraQualifiers;
        extra.ToolTip = "Dodatkowe kwalifikatory GitHub, np. org:my-org. Puste = wszystkie dostępne repozytoria.";
        content.Children.Add(extra);
        content.Children.Add(Heading("Odświeżanie co ile minut (1–60)"));
        minutes.Text = app.Config.RefreshMinutes.ToString();
        content.Children.Add(minutes);
        notify.IsChecked = app.Config.Notify;
        content.Children.Add(notify);
        content.Children.Add(Ui.Button("Zapisz ustawienia", "Zapisz zakres, interwał i powiadomienia",
            () => _ = app.RunAsync(SaveAsync)));
        content.Children.Add(Heading("Token GitHub"));
        content.Children.Add(source);
        content.Children.Add(Ui.Text("Domyślnie: gh auth login. PAT ma pierwszeństwo i jest szyfrowany przez Windows DPAPI dla bieżącego użytkownika. Classic PAT: scope repo; organizacje SSO wymagają autoryzacji."));
        pat.ToolTip = "Personal access token — nigdy nie jest zapisywany w config.json";
        content.Children.Add(pat);
        content.Children.Add(Ui.Button("Zapisz PAT", "Zapisz zaszyfrowany token",
            () => _ = app.RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(pat.Password))
                    throw new InvalidOperationException("Podaj token.");
                app.Tokens.Save(pat.Password);
                pat.Clear();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));
        content.Children.Add(Ui.Button("Usuń PAT i użyj gh CLI", "Usuń zapisany token i użyj konta GitHub CLI",
            () => _ = app.RunAsync(async () =>
            {
                app.Tokens.Delete();
                app.CredentialsChanged();
                await app.RefreshAsync();
            })));
        content.Children.Add(Ui.Text("Zamknięcie panelu PR-ów ukrywa go w zasobniku. Zakończenie aplikacji: prawy przycisk na ikonie → Zakończ."));
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        app.Changed += UpdateSource;
        Closed += (_, _) => app.Changed -= UpdateSource;
        RenderUsers();
        UpdateSource();
    }

    private void UpdateSource()
    {
        source.Text = $"Źródło tokenu: {app.Tokens.Source}";
        updateStatus.Text = app.UpdateStatus;
    }

    private Config Copy() => new()
    {
        Users = app.Config.Users.ToList(), ExtraQualifiers = app.Config.ExtraQualifiers,
        Notify = app.Config.Notify, RefreshMinutes = app.Config.RefreshMinutes
    };

    private async Task SaveAsync()
    {
        if (!int.TryParse(minutes.Text, out var value) || value is < 1 or > 60)
            throw new InvalidOperationException("Interwał musi wynosić 1–60 minut.");
        var config = Copy();
        config.ExtraQualifiers = extra.Text.Trim();
        config.RefreshMinutes = value;
        config.Notify = notify.IsChecked == true;
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
        foreach (var user in app.Config.Users.Order(StringComparer.OrdinalIgnoreCase))
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
