using System;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GHNotif.Core;

namespace GHNotif.Windows;

/// Centered, always-on-top window shown at the scheduled review time.
internal sealed class ReviewReminderWindow : Window
{
    private readonly App app;
    private readonly TextBlock summary = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel rows = new();

    internal ReviewReminderWindow(App app)
    {
        this.app = app;
        Title = L("GHNotif — czas na review", "GHNotif — time to review");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = L("Czas na review PR-ów", "Time to review PRs"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var snooze = Ui.Button(L("Przypomnij za 15 min", "Remind me in 15 min"), L("Pokaż to okno ponownie za 15 minut", "Show this window again in 15 minutes"), () =>
        {
            app.SnoozeReviewReminder(TimeSpan.FromMinutes(15));
            Close();
        });
        var panel = Ui.Button(L("Otwórz panel", "Open panel"), L("Otwórz pełny panel PR-ów", "Open the full PR panel"), () =>
        {
            app.ShowPanel();
            Close();
        });
        var done = Ui.Button(L("Gotowe", "Done"), L("Zamknij przypomnienie", "Close the reminder"), Close);
        done.IsDefault = true;
        done.IsCancel = true;
        foreach (var button in new[] { snooze, panel, done })
        {
            button.Padding = new Thickness(12, 4, 12, 4);
            button.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new ScrollViewer
        {
            Content = rows, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = root;

        app.Changed += Render;
        Closed += (_, _) => app.Changed -= Render;
        Render();
    }

    internal void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SystemSounds.Asterisk.Play();
    }

    private void Render()
    {
        var prs = app.State.Visible(app.Results.PullRequests).ToList();
        summary.Text = prs.Count switch
        {
            0 when app.IsRefreshing => L("Sprawdzam listę…", "Checking the list…"),
            0 => L("Nic nie czeka na Twoje review 🎉", "Nothing is waiting for your review 🎉"),
            var count => L(PolishPlural(count, "1 PR czeka na Twoją uwagę", $"{count} PR-y czekają na Twoją uwagę",
                $"{count} PR-ów czeka na Twoją uwagę"), count == 1 ? "1 PR needs your attention" : $"{count} PRs need your attention")
        };
        rows.Children.Clear();
        foreach (var group in prs.GroupBy(pr => pr.Repository).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Children.Add(new TextBlock
            {
                Text = $"{group.Key} · {group.Count()}", FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 8, 0, 2)
            });
            foreach (var pr in group.OrderBy(pr => pr.CreatedAt)) rows.Children.Add(CreateRow(pr));
        }
    }

    private UIElement CreateRow(PullRequest pr)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var done = Ui.Button("✓", L("Oznacz jako załatwione — lokalnie, bez zmian na GitHubie", "Mark as done — locally, nothing changes on GitHub"),
            () => _ = app.RunAsync(() => { app.ToggleDismissed(pr); return Task.CompletedTask; }));
        done.Margin = new Thickness(6, 0, 0, 0);
        done.Padding = new Thickness(8, 2, 8, 2);
        DockPanel.SetDock(done, Dock.Right);
        row.Children.Add(done);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = pr.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = $"#{pr.Number} · {pr.Author} · +{pr.Additions}/−{pr.Deletions}", FontSize = 12, Foreground = Brushes.DimGray
        });
        var open = Ui.Button("", L($"Otwórz PR w przeglądarce\n{pr.Url}", $"Open PR in browser\n{pr.Url}"),
            () => _ = app.RunAsync(() => { App.OpenGitHub(pr.Url); return Task.CompletedTask; }));
        open.Content = text;
        open.HorizontalContentAlignment = HorizontalAlignment.Left;
        open.Background = Brushes.Transparent;
        open.BorderThickness = new Thickness(0);
        open.Padding = new Thickness(4, 2, 4, 2);
        row.Children.Add(open);
        return row;
    }
}
