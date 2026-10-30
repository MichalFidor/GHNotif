using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using GHNotif.Core;
using Forms = System.Windows.Forms;

namespace GHNotif.Windows;

/// Borderless flyout above the tray, modelled on the macOS menu bar panel.
internal sealed class TrayWindow : Window
{
    private enum ListMode { Active, Done, Snoozed }

    private readonly App app;
    private readonly StackPanel rows = new();
    private readonly StackPanel banners = new();
    private readonly TextBlock heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock headingCount = new() { FontSize = 15, Foreground = Palette.Secondary, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock updated = new() { FontSize = 12, Foreground = Palette.Secondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer list;
    private readonly Button toggle;
    private readonly Button snoozedToggle;
    private readonly Button refresh;
    private readonly Button markAllRead;
    private ListMode listMode;
    private double? anchorBottom;
    private DateTime hiddenOnDeactivate;

    internal TrayWindow(App app)
    {
        this.app = app;
        Title = "GHNotif";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        Background = Brushes.White;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(1);
        FontFamily = Ui.TextFont;
        FontSize = 13;
        Foreground = Palette.Text;
        UseLayoutRounding = true;
        // Keeps the DWM drop shadow on a window without a title bar.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false
        });
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/GHNotif.Windows;component/GHNotif.png"));

        var root = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(12, 10, 8, 8) };
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/GHNotif.Windows;component/GHNotif.png")),
            Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        refresh = Ui.IconButton(Glyph.Refresh, L("Odśwież listę PR-ów", "Refresh the PR list"), () => _ = app.RunAsync(app.RefreshAsync));
        toggle = Ui.IconButton(Glyph.Done, L("Pokaż załatwione PR-y", "Show done PRs"), ToggleDone);
        snoozedToggle = Ui.IconButton(Glyph.Clock, L("Pokaż odłożone PR-y", "Show snoozed PRs"), ToggleSnoozed);
        var settings = Ui.IconButton(Glyph.Settings, L("Ustawienia", "Settings"), () =>
        {
            Hide();
            app.OpenSettings();
        });
        markAllRead = Ui.IconButton(Glyph.Read, L("Oznacz wszystkie jako przeczytane", "Mark all as read"),
            () => _ = app.RunAsync(() => { app.MarkAllRead(); return Task.CompletedTask; }));
        foreach (var button in new[] { settings, refresh, snoozedToggle, toggle, markAllRead })
        {
            DockPanel.SetDock(button, Dock.Right);
            header.Children.Add(button);
        }
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(logo);
        title.Children.Add(heading);
        title.Children.Add(headingCount);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(banners, Dock.Top);
        root.Children.Add(banners);
        var top = Ui.Divider();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var footer = new DockPanel { Margin = new Thickness(12, 4, 6, 4) };
        var quit = Ui.Button(L("Zakończ", "Quit"), L("Zakończ GHNotif", "Quit GHNotif"), () => Application.Current.Shutdown());
        quit.Background = Brushes.Transparent;
        quit.BorderThickness = new Thickness(0);
        quit.Foreground = Palette.Secondary;
        quit.FontSize = 12;
        DockPanel.SetDock(quit, Dock.Right);
        footer.Children.Add(quit);
        footer.Children.Add(updated);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var bottom = Ui.Divider();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        list = new ScrollViewer
        {
            Content = rows, MaxHeight = 520, Padding = new Thickness(0, 4, 0, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(list);
        Content = root;

        app.Changed += Render;
        Closing += HideOnClose;
        SourceInitialized += (_, _) => Ui.RoundCorners(this);
        // Like a tray flyout: clicking elsewhere or pressing Esc dismisses the panel.
        Deactivated += (_, _) =>
        {
            if (!IsVisible) return;
            hiddenOnDeactivate = DateTime.UtcNow;
            Hide();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Hide();
        };
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
        // Clicking the tray icon deactivates the open panel first; don't reopen it on the same click.
        else if (DateTime.UtcNow - hiddenOnDeactivate > TimeSpan.FromMilliseconds(400)) ShowPanel();
    }

    internal void ShowPanel()
    {
        anchorBottom = null;
        Show();
        UpdateLayout();
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        list.MaxHeight = Math.Max(100, Math.Min(520, area.Height / dpi.DpiScaleY - 200));
        UpdateLayout();
        Left = Math.Max(area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - ActualWidth - 12);
        Top = Math.Max(area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - ActualHeight - 12);
        anchorBottom = Top + ActualHeight;
        Activate();
    }

    /// Closes the window for good (normally closing only hides it); used when the UI is rebuilt.
    internal void Retire()
    {
        app.Changed -= Render;
        Closing -= HideOnClose;
        Close();
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
        heading.Text = listMode switch { ListMode.Done => L("Załatwione PR-y", "Done PRs"), ListMode.Snoozed => L("Odłożone PR-y", "Snoozed PRs"), _ => L("PR-y do review", "PRs to review") };
        headingCount.Text = prs.Count.ToString();
        Ui.SetIcon(toggle, listMode == ListMode.Done ? Glyph.List : Glyph.Done,
            listMode == ListMode.Done ? L("Pokaż PR-y wymagające uwagi", "Show PRs needing attention") : L("Pokaż załatwione PR-y", "Show done PRs"));
        Ui.SetIcon(snoozedToggle, listMode == ListMode.Snoozed ? Glyph.List : Glyph.Clock,
            listMode == ListMode.Snoozed ? L("Pokaż PR-y wymagające uwagi", "Show PRs needing attention") : L("Pokaż odłożone PR-y", "Show snoozed PRs"));
        toggle.Foreground = listMode == ListMode.Done ? Palette.Accent : Palette.Icon;
        snoozedToggle.Foreground = listMode == ListMode.Snoozed ? Palette.Accent : Palette.Icon;
        refresh.IsEnabled = !app.IsRefreshing;
        markAllRead.IsEnabled = app.Results.PullRequests.Any(pr =>
            app.State.Inbox.Entries.GetValueOrDefault(pr.Id)?.IsUnread == true);
        updated.Text = app.IsRefreshing ? L("Odświeżanie…", "Refreshing…") : app.LastRefresh is { } time
            ? L($"Odświeżono {time:HH:mm}", $"Refreshed {time:HH:mm}") : L("Jeszcze nie odświeżono", "Not refreshed yet");

        banners.Children.Clear();
        if (app.AvailableRelease is { } release)
        {
            var install = Ui.Button(L("Zaktualizuj teraz", "Update now"), L("Pobierz i zainstaluj aktualizację GHNotif", "Download and install the GHNotif update"),
                () => _ = app.RunAsync(app.InstallUpdateAsync));
            install.IsEnabled = !app.IsInstallingUpdate;
            banners.Children.Add(Banner(Glyph.Download, Palette.Accent,
                app.IsInstallingUpdate ? app.UpdateStatus : L($"Dostępna nowa wersja {release.TagName}.", $"New version {release.TagName} is available."), install));
        }
        if (app.Error is not null) banners.Children.Add(Banner(Glyph.Error, Palette.Red, app.Error));
        if (app.Results.BlockedOrganizations.Count > 0)
        {
            var authorize = Ui.Button(L("Autoryzuj", "Authorize"),
                L("GitHub ukrywa wyniki z części organizacji. Autoryzuj token lub aplikację GitHub CLI dla SSO.", "GitHub hides results from some organizations. Authorize the token or the GitHub CLI app for SSO."),
                () => _ = app.RunAsync(() =>
                {
                    App.OpenGitHub(app.Tokens.Source == "gh CLI"
                        ? "https://github.com/settings/connections/applications/178c6fc778ccc68e1d6a"
                        : "https://github.com/settings/tokens");
                    return Task.CompletedTask;
                }));
            banners.Children.Add(Banner(Glyph.Lock, Palette.Orange,
                L("Brak dostępu SSO - wyniki z części organizacji są ukryte.", "No SSO access - results from some organizations are hidden."), authorize));
        }

        rows.Children.Clear();
        if (prs.Count == 0) rows.Children.Add(EmptyState());
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

    private UIElement EmptyState()
    {
        var text = app.IsRefreshing ? L("Ładowanie…", "Loading…") : app.Error is not null
            ? L("Nie udało się pobrać PR-ów - sprawdź komunikat powyżej.", "Could not fetch PRs - see the message above.")
            : !app.Config.HasSearches ? L("Dodaj obserwowane osoby w ustawieniach.", "Add watched people in settings.")
            : listMode == ListMode.Done ? L("Brak załatwionych PR-ów.", "No done PRs.")
            : listMode == ListMode.Snoozed ? L("Brak odłożonych PR-ów.", "No snoozed PRs.") : L("Brak PR-ów wymagających uwagi.", "No PRs need attention.");
        var showsApproval = listMode == ListMode.Active && !app.IsRefreshing && app.Error is null && app.Config.HasSearches
            && app.LastRefresh is not null && app.Results.BlockedOrganizations.Count == 0
            && !app.State.SnoozedPullRequests(app.Results.PullRequests).Any();
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 28, 16, 28)
        };
        var badge = System.IO.Path.Combine(AppContext.BaseDirectory, "Badges", "badge-approved.png");
        if (showsApproval && File.Exists(badge))
            panel.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(badge)), Width = 18, Height = 18,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            });
        panel.Children.Add(new TextBlock { Text = text, Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static Border Banner(string glyph, Brush tint, string text, Button? action = null)
    {
        var content = new DockPanel();
        var icon = Ui.Icon(glyph, tint, 14);
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        if (action is not null)
        {
            action.Style = (Style)Application.Current.FindResource("AccentButton");
            action.Margin = new Thickness(8, -2, 0, -2);
            action.FontSize = 12;
            action.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(action, Dock.Right);
            content.Children.Add(action);
        }
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var background = ((SolidColorBrush)tint).Color;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(26, background.R, background.G, background.B)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(10, 0, 10, 8), Child = content
        };
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
        var content = new DockPanel();
        var badge = new Border
        {
            Background = Palette.Capsule, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Secondary }
        };
        DockPanel.SetDock(badge, Dock.Right);
        content.Children.Add(badge);
        var chevron = Ui.Icon(collapsed ? Glyph.ChevronRight : Glyph.ChevronDown, Palette.Secondary, 9);
        chevron.Width = 14;
        chevron.Margin = new Thickness(0, 1, 4, 0);
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock
        {
            Text = repository, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var header = Ui.Button("", collapsed ? L($"Rozwiń {repository}", $"Expand {repository}") : L($"Zwiń {repository}", $"Collapse {repository}"),
            () => _ = app.RunAsync(() => { app.ToggleCollapsed(repository); return Task.CompletedTask; }));
        header.Content = content;
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        header.Background = Palette.GroupBackground;
        header.BorderThickness = new Thickness(0);
        header.Padding = new Thickness(8, 4, 10, 4);
        header.Margin = new Thickness(8, 6, 8, 2);
        return header;
    }

    private UIElement CreateRow(PullRequest pr)
    {
        var inActive = listMode == ListMode.Active;
        var row = new DockPanel { Margin = new Thickness(6, 0, 8, 0) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        app.State.Inbox.Entries.TryGetValue(pr.Id, out var entry);
        var unread = entry?.IsUnread == true;
        if (unread)
            actions.Children.Add(Ui.IconButton(Glyph.Read, L("Oznacz jako przeczytane", "Mark as read"),
                () => _ = app.RunAsync(() => { app.MarkRead(pr); return Task.CompletedTask; })));
        if (inActive)
        {
            var snooze = Ui.IconButton(Glyph.Clock, L("Kliknij: odłóż na 2 godziny. Prawy klik: wybierz termin.", "Click: snooze for 2 hours. Right-click: choose when."),
                () => _ = app.RunAsync(() => { app.Snooze(pr, DateTimeOffset.UtcNow.AddHours(2)); return Task.CompletedTask; }));
            var menu = new ContextMenu();
            var twoHours = new MenuItem { Header = L("Za 2 godziny", "In 2 hours") };
            twoHours.Click += (_, _) => _ = app.RunAsync(() =>
            {
                app.Snooze(pr, DateTimeOffset.UtcNow.AddHours(2));
                return Task.CompletedTask;
            });
            var tomorrow = new MenuItem { Header = L("Jutro rano", "Tomorrow morning") };
            tomorrow.Click += (_, _) => _ = app.RunAsync(() =>
            {
                app.Snooze(pr, new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)));
                return Task.CompletedTask;
            });
            menu.Items.Add(twoHours);
            menu.Items.Add(tomorrow);
            snooze.ContextMenu = menu;
            actions.Children.Add(snooze);
        }
        var done = Ui.IconButton(inActive ? Glyph.Completed : Glyph.Undo,
            inActive ? L("Oznacz jako załatwione - lokalnie, bez zmian na GitHubie", "Mark as done - locally, nothing changes on GitHub") : L("Przywróć na listę wymagających uwagi", "Move back to needs attention"),
            () => _ = app.RunAsync(() =>
            {
                if (listMode == ListMode.Snoozed) app.Unsnooze(pr);
                else app.ToggleDismissed(pr);
                return Task.CompletedTask;
            }));
        if (!inActive) done.Foreground = Palette.Accent;
        actions.Children.Add(done);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);

        var body = new DockPanel();
        var avatar = Avatar(pr);
        DockPanel.SetDock(avatar, Dock.Left);
        body.Children.Add(avatar);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 2, 0, 0) };
        if (pr.Reasons.Contains(MatchReason.Mention))
            badges.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Palette.Purple,
                Margin = new Thickness(2, 0, 2, 0), ToolTip = L("Ktoś wspomniał Cię w tym PR-ze", "Someone mentioned you in this PR"),
                Child = new TextBlock
                {
                    Text = "@", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0)
                }
            });
        if (pr.Reasons.Contains(MatchReason.ReviewRequested))
            badges.Children.Add(Ui.Status(Glyph.Person, Palette.Purple, L("Poproszono Cię o review", "Your review was requested")));
        switch (pr.ReviewDecision)
        {
            case "APPROVED": badges.Children.Add(Ui.Status(Glyph.CompletedSolid, Palette.Green, L("Review: PR zatwierdzony", "Review: approved"))); break;
            case "CHANGES_REQUESTED": badges.Children.Add(Ui.Status(Glyph.Warning, Palette.Orange, L("Review: poproszono o zmiany", "Review: changes requested"))); break;
            case "REVIEW_REQUIRED": badges.Children.Add(Ui.Status(Glyph.Eye, Palette.Secondary, L("Review: wymagane zatwierdzenie", "Review: approval required"))); break;
        }
        var (checksBrush, checksTip) = pr.Checks switch
        {
            "SUCCESS" => (Palette.Green, L("CI: wszystkie sprawdzenia zakończone powodzeniem", "CI: all checks passed")),
            "FAILURE" or "ERROR" => (Palette.Red, L("CI: błąd lub nieudane sprawdzenia", "CI: error or failed checks")),
            "PENDING" or "EXPECTED" => (Palette.Yellow, L("CI: oczekuje lub jest w toku", "CI: pending or in progress")),
            _ => ((Brush?)null, "")
        };
        if (checksBrush is not null)
            badges.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Fill = checksBrush, ToolTip = checksTip,
                Margin = new Thickness(5, 5, 3, 0), VerticalAlignment = VerticalAlignment.Top
            });
        DockPanel.SetDock(badges, Dock.Right);
        body.Children.Add(badges);

        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = pr.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis
        });
        details.Children.Add(new TextBlock
        {
            Text = $"#{pr.Number} · {pr.Author} · {Age(pr.CreatedAt)} · +{pr.Additions}/−{pr.Deletions}",
            FontSize = 12, Foreground = Palette.Secondary, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (unread && entry is not null)
        {
            details.Children.Add(new TextBlock
            {
                Text = entry.IsNew ? L("Nowy PR", "New PR") : L("Od ostatniego przeczytania", "Since last read"),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Accent, Margin = new Thickness(0, 2, 0, 0)
            });
            if (entry.Details.Count > 0)
                details.Children.Add(new TextBlock
                {
                    Text = ActivityText.Summarize(entry.Details), FontSize = 12, Foreground = Palette.Secondary,
                    TextWrapping = TextWrapping.Wrap, ToolTip = string.Join("\n", entry.Details)
                });
        }
        body.Children.Add(details);

        var open = Ui.Button("", L($"Otwórz PR w przeglądarce\n{pr.Url}", $"Open PR in browser\n{pr.Url}"),
            () => _ = app.RunAsync(() => { app.OpenPullRequest(pr); return Task.CompletedTask; }));
        open.Content = body;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.VerticalContentAlignment = VerticalAlignment.Top;
        open.Background = Brushes.Transparent;
        open.BorderThickness = new Thickness(0);
        open.Padding = new Thickness(6, 6, 6, 6);
        open.Margin = new Thickness(0, 1, 2, 1);
        row.Children.Add(open);
        return row;
    }

    private static FrameworkElement Avatar(PullRequest pr)
    {
        var avatar = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 1, 9, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = pr.Author };
        avatar.Children.Add(new Ellipse { Fill = Palette.Capsule });
        avatar.Children.Add(new TextBlock
        {
            Text = pr.Author.Length > 0 ? pr.Author[..1].ToUpperInvariant() : "?", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (Uri.TryCreate(pr.AvatarUrl, UriKind.Absolute, out var url) && url.Scheme == "https")
        {
            var image = new Image { Source = new BitmapImage(url), Clip = new EllipseGeometry(new Point(12, 12), 12, 12) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.ImageFailed += (_, args) =>
            {
                Trace.TraceWarning("Nie udało się wczytać awatara: {0}", args.ErrorException.Message);
                image.Visibility = Visibility.Collapsed;
            };
            avatar.Children.Add(image);
        }
        return avatar;
    }

    private static string Age(DateTimeOffset created)
    {
        var age = DateTimeOffset.UtcNow - created;
        return age.TotalDays >= 1 ? L($"{(int)age.TotalDays} dni temu", $"{(int)age.TotalDays} d ago")
            : age.TotalHours >= 1 ? L($"{(int)age.TotalHours} godz. temu", $"{(int)age.TotalHours} h ago") : L("przed chwilą", "just now");
    }
}

/// GitHub Primer colours, matching the macOS panel.
internal static class Palette
{
    internal static readonly SolidColorBrush Text = Freeze(0x1F, 0x23, 0x28);
    internal static readonly SolidColorBrush Secondary = Freeze(0x65, 0x6D, 0x76);
    internal static readonly SolidColorBrush Icon = Freeze(0x57, 0x60, 0x6A);
    internal static readonly SolidColorBrush Border = Freeze(0xD0, 0xD7, 0xDE);
    internal static readonly SolidColorBrush Divider = Freeze(0xE6, 0xE9, 0xED);
    internal static readonly SolidColorBrush GroupBackground = Freeze(0xF3, 0xF4, 0xF6);
    internal static readonly SolidColorBrush Capsule = Freeze(0xDD, 0xE1, 0xE6);
    internal static readonly SolidColorBrush Accent = Freeze(0x09, 0x69, 0xDA);
    internal static readonly SolidColorBrush Green = Freeze(0x1A, 0x7F, 0x37);
    internal static readonly SolidColorBrush Red = Freeze(0xCF, 0x22, 0x2E);
    internal static readonly SolidColorBrush Yellow = Freeze(0xBF, 0x87, 0x00);
    internal static readonly SolidColorBrush Orange = Freeze(0xBC, 0x4C, 0x00);
    internal static readonly SolidColorBrush Purple = Freeze(0x82, 0x50, 0xDF);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// Segoe Fluent Icons (Windows 11) code points, also present in Segoe MDL2 Assets (Windows 10).
internal static class Glyph
{
    internal const string Refresh = "\uE72C";
    internal const string Settings = "\uE713";
    internal const string Read = "\uE8C3";
    internal const string Done = "\uE930";
    internal const string Completed = "\uE930";
    internal const string CompletedSolid = "\uEC61";
    internal const string List = "\uE8FD";
    internal const string Clock = "\uE823";
    internal const string Undo = "\uE7A7";
    internal const string ChevronDown = "\uE70D";
    internal const string ChevronRight = "\uE76C";
    internal const string Download = "\uE896";
    internal const string Error = "\uE783";
    internal const string Warning = "\uE7BA";
    internal const string Lock = "\uE72E";
    internal const string Eye = "\uE7B3";
    internal const string Person = "\uE77B";
}

internal static class Ui
{
    internal static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

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

    internal static Button IconButton(string glyph, string tip, Action click)
    {
        var button = Button(glyph, tip, click);
        button.Style = (Style)Application.Current.FindResource("IconButton");
        return button;
    }

    internal static void SetIcon(Button button, string glyph, string tip)
    {
        button.Content = glyph;
        button.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
    }

    internal static TextBlock Icon(string glyph, Brush brush, double size) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center
    };

    internal static TextBlock Status(string glyph, Brush brush, string tip)
    {
        var icon = Icon(glyph, brush, 15);
        icon.ToolTip = tip;
        icon.Margin = new Thickness(3, 0, 3, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        return icon;
    }

    internal static Border Divider() => new() { Height = 1, Background = Palette.Divider };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// Windows 11 rounds the corners; older versions ignore the attribute.
    internal static void RoundCorners(Window window)
    {
        const int cornerPreference = 33, round = 2;
        var value = round;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, cornerPreference, ref value, sizeof(int));
    }
}
