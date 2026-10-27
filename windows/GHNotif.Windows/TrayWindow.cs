using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GHNotif.Core;
using Forms = System.Windows.Forms;

namespace GHNotif.Windows;

internal sealed class TrayWindow : Window
{
    private enum ListMode { Active, Done, Snoozed }

    private readonly App app;
    private readonly StackPanel rows = new();
    private readonly StackPanel banners = new();
    private readonly TextBlock heading = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock updated = Ui.Text("");
    private readonly ScrollViewer list;
    private readonly Button toggle;
    private readonly Button snoozedToggle;
    private readonly Button refresh;
    private ListMode listMode;
    private double? anchorBottom;

    internal TrayWindow(App app)
    {
        this.app = app;
        Title = "GHNotif";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/GHNotif.Windows;component/GHNotif.png"));
        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(12) };
        refresh = Ui.Button("↻", "Odśwież listę PR-ów", () => _ = app.RunAsync(app.RefreshAsync));
        toggle = Ui.Button("✓", "Pokaż załatwione PR-y", ToggleDone);
        snoozedToggle = Ui.Button("◷", "Pokaż odłożone PR-y", ToggleSnoozed);
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(snoozedToggle, Dock.Right);
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(snoozedToggle);
        header.Children.Add(toggle);
        header.Children.Add(heading);
        root.Children.Add(header);
        root.Children.Add(banners);
        root.Children.Add(new Separator());
        list = new ScrollViewer
        {
            Content = rows, MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(list);
        root.Children.Add(new Separator());
        var footer = new DockPanel { Margin = new Thickness(8) };
        var settings = Ui.Button("Ustawienia…", "Osoby, token i odświeżanie", app.OpenSettings);
        DockPanel.SetDock(settings, Dock.Right);
        footer.Children.Add(settings);
        footer.Children.Add(updated);
        root.Children.Add(footer);
        Content = root;
        app.Changed += Render;
        Closing += HideOnClose;
        // Collapsing groups shrinks the window from the bottom; keep it docked above the taskbar.
        SizeChanged += (_, args) =>
        {
            if (IsVisible && args.HeightChanged && anchorBottom is { } bottom) Top = Math.Max(0, bottom - ActualHeight);
        };
        Render();
    }

    internal void Toggle()
    {
        if (IsVisible) Hide();
        else ShowPanel();
    }

    internal void ShowPanel()
    {
        anchorBottom = null;
        Show();
        UpdateLayout();
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        list.MaxHeight = Math.Max(100, Math.Min(520, area.Height / dpi.DpiScaleY - 240));
        UpdateLayout();
        Left = Math.Max(area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - ActualWidth - 12);
        Top = Math.Max(area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - ActualHeight - 12);
        anchorBottom = Top + ActualHeight;
        Activate();
    }

    private void HideOnClose(object? sender, CancelEventArgs args)
    {
        args.Cancel = true;
        Hide();
    }

    private void Render()
    {
        var prs = listMode switch
        {
            ListMode.Done => app.Results.PullRequests.Where(pr => app.State.IsDismissed(pr.Id)).ToList(),
            ListMode.Snoozed => app.State.SnoozedPullRequests(app.Results.PullRequests).ToList(),
            _ => app.State.Visible(app.Results.PullRequests).ToList()
        };
        heading.Text = $"{listMode switch { ListMode.Done => "Załatwione PR-y", ListMode.Snoozed => "Odłożone PR-y", _ => "PR-y do review" }}  {prs.Count}";
        toggle.Content = listMode == ListMode.Done ? "☰" : "✓";
        toggle.ToolTip = listMode == ListMode.Done ? "Pokaż PR-y wymagające uwagi" : "Pokaż załatwione PR-y";
        snoozedToggle.Content = listMode == ListMode.Snoozed ? "↶" : "◷";
        snoozedToggle.ToolTip = listMode == ListMode.Snoozed ? "Pokaż PR-y wymagające uwagi" : "Pokaż odłożone PR-y";
        refresh.IsEnabled = !app.IsRefreshing;
        updated.Text = app.IsRefreshing ? "Odświeżanie…" : app.LastRefresh is { } time
            ? $"Odświeżono {time:HH:mm}" : "Jeszcze nie odświeżono";
        banners.Children.Clear();
        if (app.AvailableRelease is { } release)
            banners.Children.Add(Ui.Button($"Dostępna wersja {release.TagName} — pobierz",
                "Otwórz GitHub Releases (aktualizacja ręczna)",
                () => _ = app.RunAsync(() => { App.OpenGitHub(release.HtmlUrl); return Task.CompletedTask; })));
        if (app.Error is not null) banners.Children.Add(Ui.Text(app.Error, Brushes.Firebrick));
        if (app.Results.BlockedOrganizations.Count > 0)
            banners.Children.Add(Ui.Button("🔒 Brak dostępu SSO — autoryzuj token",
                "GitHub ukrywa wyniki z części organizacji. Autoryzuj token lub aplikację GitHub CLI dla SSO.",
                () => _ = app.RunAsync(() =>
                {
                    App.OpenGitHub(app.Tokens.Source == "gh CLI"
                        ? "https://github.com/settings/connections/applications/178c6fc778ccc68e1d6a"
                        : "https://github.com/settings/tokens");
                    return Task.CompletedTask;
                })));
        rows.Children.Clear();
        if (prs.Count == 0)
            rows.Children.Add(Ui.Text(app.IsRefreshing ? "Ładowanie…" : app.Error is not null
                ? "Nie udało się pobrać PR-ów — sprawdź komunikat powyżej."
                : !app.Config.HasSearches ? "Dodaj obserwowane osoby w ustawieniach."
                : listMode == ListMode.Done ? "Brak załatwionych PR-ów."
                : listMode == ListMode.Snoozed ? "Brak odłożonych PR-ów." : "Brak PR-ów wymagających uwagi."));
        foreach (var group in prs.GroupBy(pr => pr.Repository).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var collapsed = app.State.Collapsed.Contains(group.Key);
            rows.Children.Add(CreateGroupHeader(group.Key, group.Count(), collapsed));
            if (!collapsed)
                foreach (var pr in group.OrderBy(pr => app.Config.SortOrder switch
                {
                    SortOrder.OldestFirst => pr.CreatedAt.UtcDateTime.Ticks,
                    SortOrder.LargestChanges => -((long)pr.Additions + pr.Deletions),
                    _ => -pr.UpdatedAt.UtcDateTime.Ticks
                })) rows.Children.Add(CreateRow(pr));
        }
    }

    private void ToggleDone()
    {
        listMode = listMode == ListMode.Done ? ListMode.Active : ListMode.Done;
        Render();
    }

    private void ToggleSnoozed()
    {
        listMode = listMode == ListMode.Snoozed ? ListMode.Active : ListMode.Snoozed;
        Render();
    }

    private UIElement CreateGroupHeader(string repository, int count, bool collapsed)
    {
        var content = new DockPanel { LastChildFill = true };
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(214, 218, 226)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray }
        };
        DockPanel.SetDock(badge, Dock.Right);
        content.Children.Add(badge);
        content.Children.Add(new TextBlock
        {
            Text = $"{(collapsed ? "▸" : "▾")}  {repository}", FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var header = Ui.Button("", collapsed ? $"Rozwiń {repository}" : $"Zwiń {repository}",
            () => _ = app.RunAsync(() => { app.ToggleCollapsed(repository); return Task.CompletedTask; }));
        header.Content = content;
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        header.Background = new SolidColorBrush(Color.FromRgb(235, 237, 242));
        header.BorderThickness = new Thickness(0);
        header.Padding = new Thickness(12, 6, 12, 6);
        return header;
    }

    private UIElement CreateRow(PullRequest pr)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var inActive = listMode == ListMode.Active;
        if (inActive)
        {
            var snooze = Ui.Button("◷", "Kliknij: odłóż na 2 godziny. Prawy klik: wybierz termin.",
                () => _ = app.RunAsync(() => { app.Snooze(pr, DateTimeOffset.UtcNow.AddHours(2)); return Task.CompletedTask; }));
            var menu = new ContextMenu();
            var twoHours = new MenuItem { Header = "Za 2 godziny" };
            twoHours.Click += (_, _) => _ = app.RunAsync(() =>
            {
                app.Snooze(pr, DateTimeOffset.UtcNow.AddHours(2));
                return Task.CompletedTask;
            });
            var tomorrow = new MenuItem { Header = "Jutro rano" };
            tomorrow.Click += (_, _) => _ = app.RunAsync(() =>
            {
                app.Snooze(pr, new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)));
                return Task.CompletedTask;
            });
            menu.Items.Add(twoHours);
            menu.Items.Add(tomorrow);
            snooze.ContextMenu = menu;
            snooze.Margin = new Thickness(2, 0, 4, 0);
            row.Children.Add(snooze);
        }
        var done = Ui.Button(inActive ? "✓" : "↶",
            inActive ? "Oznacz jako załatwione — lokalnie, bez zmian na GitHubie" : "Przywróć na listę wymagających uwagi",
            () => _ = app.RunAsync(() =>
            {
                if (listMode == ListMode.Snoozed) app.Unsnooze(pr);
                else app.ToggleDismissed(pr);
                return Task.CompletedTask;
            }));
        done.Margin = new Thickness(2, 0, 4, 0);
        var (checks, checksTip, checksBrush) = pr.Checks switch
        {
            "SUCCESS" => ("●", "CI: wszystkie sprawdzenia zakończone powodzeniem", Brushes.ForestGreen),
            "FAILURE" or "ERROR" => ("●", "CI: błąd lub nieudane sprawdzenia", Brushes.Firebrick),
            "PENDING" or "EXPECTED" => ("●", "CI: oczekuje lub jest w toku", Brushes.DarkGoldenrod),
            _ => ("○", "CI: brak informacji o sprawdzeniach", Brushes.Gray)
        };
        var check = Badge(checks, checksTip, checksBrush);
        row.Children.Add(check);
        var (review, reviewTip) = pr.ReviewDecision switch
        {
            "APPROVED" => ("✓", "Review: PR zatwierdzony"),
            "CHANGES_REQUESTED" => ("!", "Review: poproszono o zmiany"),
            "REVIEW_REQUIRED" => ("◉", "Review: wymagane zatwierdzenie"),
            _ => ("–", "Review: brak decyzji")
        };
        if (pr.Reasons.Contains(MatchReason.ReviewRequested))
        {
            var requested = Badge("👁", "Poproszono Cię o review", Brushes.Purple);
            row.Children.Add(requested);
        }
        if (pr.Reasons.Contains(MatchReason.Mention))
        {
            var mention = Badge("@", "Ktoś wspomniał Cię w tym PR-ze", Brushes.Purple);
            mention.FontWeight = FontWeights.Bold;
            row.Children.Add(mention);
        }
        var reviewBadge = Badge(review, reviewTip, pr.ReviewDecision == "APPROVED" ? Brushes.ForestGreen : Brushes.DimGray);
        row.Children.Add(reviewBadge);
        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = pr.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis
        });
        details.Children.Add(new TextBlock
        {
            Text = $"#{pr.Number} · {pr.Author} · {Age(pr.CreatedAt)} · +{pr.Additions}/−{pr.Deletions}",
            FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0)
        });
        var body = new StackPanel { Orientation = Orientation.Horizontal };
        if (Uri.TryCreate(pr.AvatarUrl, UriKind.Absolute, out var avatar) && avatar.Scheme == "https")
        {
            var image = new Image
            {
                Source = new BitmapImage(avatar), Width = 26, Height = 26,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                ToolTip = pr.Author
            };
            image.ImageFailed += (_, args) =>
            {
                Trace.TraceWarning("Nie udało się wczytać awatara: {0}", args.ErrorException.Message);
                image.Visibility = Visibility.Collapsed;
            };
            body.Children.Add(image);
        }
        details.MaxWidth = 280;
        body.Children.Add(details);
        var open = Ui.Button("", $"Otwórz PR w przeglądarce\n{pr.Url}",
            () => _ = app.RunAsync(() => { App.OpenGitHub(pr.Url); return Task.CompletedTask; }));
        open.Content = body;
        open.MaxWidth = 320;
        open.HorizontalContentAlignment = HorizontalAlignment.Left;
        open.Background = Brushes.Transparent;
        open.BorderThickness = new Thickness(0);
        open.Padding = new Thickness(0);
        open.Margin = new Thickness(0, 0, 2, 0);
        row.Children.Insert(inActive ? 0 : 0, open);
        row.Children.Add(done);
        return row;
    }

    private static TextBlock Badge(string text, string tip, Brush brush) => new()
    {
        Text = text, ToolTip = tip, Foreground = brush, FontSize = 18,
        Margin = new Thickness(5, 6, 5, 0), VerticalAlignment = VerticalAlignment.Top
    };

    private static string Age(DateTimeOffset created)
    {
        var age = DateTimeOffset.UtcNow - created;
        return age.TotalDays >= 1 ? $"{(int)age.TotalDays} dni temu"
            : age.TotalHours >= 1 ? $"{(int)age.TotalHours} godz. temu" : "przed chwilą";
    }
}

internal static class Ui
{
    internal static TextBlock Text(string text, Brush? brush = null) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8),
        Foreground = brush ?? Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center
    };

    internal static Button Button(string text, string tip, Action click)
    {
        var button = new Button { Content = text, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }
}
