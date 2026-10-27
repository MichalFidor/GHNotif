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
            Check(rendered.Any(b => b.ToolTip?.ToString()?.StartsWith("Oznacz jako załatwione") == true),
                "Missing dismiss button; rendered tooltips: " + string.Join(" | ", rendered.Select(b => b.ToolTip)));
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
            Buttons(panel).Single(b => Equals(b.ToolTip, "Zwiń example/demo"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            Check(!Buttons(panel).Any(b => b.ToolTip?.ToString()?.StartsWith("Oznacz jako") == true)
                && app.State.Collapsed.Contains("example/demo"), "Collapsing a repo hides its rows");
            Check(new JsonStore<LocalState>(Path.Combine(folder, "state.json")).Load().Collapsed.Contains("example/demo"),
                "Collapsed repo saved to disk");
            Buttons(panel).Single(b => Equals(b.ToolTip, "Rozwiń example/demo"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = new SettingsWindow(app);
            settings.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            settings.UpdateLayout();
            Check(Buttons(settings).Any(b => Equals(b.Content, "Zapisz PAT")), "Settings render");
            settings.Close();
            panel.Close();
            Check(!panel.IsVisible, "Closing panel hides it");
            app.Tokens.Delete();
            Check(!File.Exists(Path.Combine(folder, "token.dat")), "PAT deletion");
            Console.WriteLine("All Windows UI checks passed (layout, buttons, persistence, settings, DPAPI).");
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

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
