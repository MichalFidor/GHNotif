using System.Net;
using System.Text.Json;
using GHNotif.Core;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}

var queries = QueryBuilder.Build(Enumerable.Range(1, 23).Select(i => $"user{i}"), " org:example ").ToList();
Check(queries.Count == 3, "Batch authors by 10");
Check(queries[2] == "is:pr is:open draft:false author:user21 author:user22 author:user23 org:example", "Query format");
Check(QueryBuilder.Build([" @Alice ", "alice", "bob"], "").Single() ==
    "is:pr is:open draft:false author:Alice author:bob", "Normalize and deduplicate");
Check(!QueryBuilder.Build([], "").Any(), "Empty list does not search globally");
Check(GitHubClient.ParseSso("partial-results; organizations=12,34").SetEquals(["12", "34"]), "SSO warning");

string Node(string id, bool deleted = false) => $$$$"""
    {"id":"{{{{id}}}}","number":42,"title":"Test","url":"https://github.com/example/demo/pull/42",
    "createdAt":"2026-10-01T10:00:00Z","updatedAt":"2026-10-02T10:00:00Z",
    "reviewDecision":null,"author":{{{{(deleted ? "null" : """{"login":"example","avatarUrl":null}""")}}}},
    "repository":{"nameWithOwner":"example/demo"},"commits":{"nodes":[{"commit":{"oid":"c1","statusCheckRollup":null}}]},
    "comments":{"nodes":[{"createdAt":"2026-10-03T10:00:00Z","author":{"__typename":"User","login":"reviewer"}},
      {"createdAt":"2026-10-03T11:00:00Z","author":{"__typename":"Bot","login":"ci"}},{"createdAt":"2026-10-03T12:00:00Z","author":null}]},
    "reviews":{"nodes":[{"submittedAt":null,"author":{"__typename":"User","login":"pending"}}]}}
    """;
string Page(string nodes, bool more, string cursor = "cursor") => $$$$"""
    {"data":{"viewer":{"login":"me"},"search":{"issueCount":2,"pageInfo":{"hasNextPage":{{{{more.ToString().ToLowerInvariant()}}}},"endCursor":"{{{{cursor}}}}"},
    "nodes":[{{{{nodes}}}}]}}}
    """;
using (var document = JsonDocument.Parse(Node("legacy-id", true)))
{
    var pr = GitHubClient.ParsePullRequest(document.RootElement);
    Check(pr.Id == "legacy-id" && pr.Author == "ghost" && pr.Checks is null, "Deleted author and missing CI");
    Check(pr.HeadCommitOid == "c1", "Head commit parsed");
    Check(pr.Activity!.Select(a => a.Login).SequenceEqual(["reviewer"]), "Only human, submitted activity kept");
}

var requests = new List<string>();
using var http = new HttpClient(new FakeHandler(async request =>
{
    Check(request.Headers.Authorization?.Parameter == "test-token", "Auth header");
    var body = await request.Content!.ReadAsStringAsync();
    requests.Add(body);
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(requests.Count == 1
            ? Page(Node("one"), true) : Page(Node("one") + "," + Node("two"), false))
    };
    response.Headers.Add("X-GitHub-SSO", "partial-results; organizations=123");
    return response;
}));
var client = new GitHubClient(http);
var result = await client.SearchAsync("test-token", new Config { Users = ["example"] });
Check(requests.Count == 2 && requests[1].Contains("cursor"), "Pagination passes cursor");
Check(result.PullRequests.Count == 2, "Deduplicate across pages");
Check(result.BlockedOrganizations.SetEquals(["123"]), "SSO surfaced with search results");
Check(result.ViewerLogin == "me", "Viewer login parsed");

using var unauthorized = new HttpClient(new FakeHandler(_ =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
await ThrowsAsync<HttpRequestException>(() =>
    new GitHubClient(unauthorized).SearchAsync("bad", new Config { Users = ["example"] }), "Unauthorized must fail");

using var partial = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("""{"data":{"search":null},"errors":[{"message":"Missing permission"}]}""")
})));
await ThrowsAsync<InvalidOperationException>(() =>
    new GitHubClient(partial).SearchAsync("bad", new Config { Users = ["example"] }), "GraphQL errors cannot look successful");

using var capped = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(Page(Node("one"), false).Replace("\"issueCount\":2", "\"issueCount\":1001"))
})));
await ThrowsAsync<InvalidOperationException>(() =>
    new GitHubClient(capped).SearchAsync("test", new Config { Users = ["example"] }), "Search truncation must be reported");

var prs = result.PullRequests;
var state = new LocalState();
Check(state.Observe(prs, true, false).Count == 0, "First refresh is a baseline");
state.Dismiss(prs.Single(pr => pr.Id == "one"), DateTimeOffset.Parse("2026-10-04T00:00:00Z"));
Check(state.Visible(prs).Single().Id == "two", "Dismiss hides PR from list and count");
state.Observe([], true, false);
Check(state.Observe(prs, true, false).All(pr => pr.Id != "one"), "Dismissed PR reappearance does not notify");
Check(state.Visible(prs).Single().Id == "two", "Dismiss survives refresh and reappearance");
Check(state.Reactivate(prs, "me").Count == 0, "Old activity keeps PR dismissed");

var one = prs.Single(pr => pr.Id == "one");
var later = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
var probe = state.Clone();
Check(probe.Reactivate([one with { Activity = [new("ME", later)] }], "me").Count == 0, "Own comment ignored");
Check(probe.Reactivate([one with { Activity = [new("reviewer", later)] }], "me").Count == 1 && !probe.IsDismissed("one"),
    "New comment by someone else reactivates");
Check(state.IsDismissed("one"), "Clone is independent");
probe = state.Clone();
Check(probe.Reactivate([one with { HeadCommitOid = "c2" }], "me").Count == 1, "New commit reactivates");
var alerts = state.Clone();
var back = alerts.Reactivate([one with { HeadCommitOid = "c2" }, prs[1]], "me");
Check(alerts.Observe(prs, true, false, back).Select(pr => pr.Id).SequenceEqual(["one"]), "Reactivated PR notifies");
state.ToggleCollapsed("example/demo");
Check(state.Collapsed.Contains("example/demo"), "Collapse toggles on");

var directory = Path.Combine(Path.GetTempPath(), "ghnotif-checks-" + Guid.NewGuid());
try
{
    var path = Path.Combine(directory, "state.json");
    var store = new JsonStore<LocalState>(path);
    store.Save(state);
    var loaded = store.Load();
    loaded.Migrate(DateTimeOffset.UtcNow);
    Check(loaded.Dismissals["one"].HeadCommitOid == "c1", "Dismiss persists across restarts");
    Check(loaded.Collapsed.Contains("example/demo"), "Collapsed repos persist");
    Check(!File.ReadAllText(path).Contains("\"dismissed\""), "Legacy field not written");
    loaded.Restore("one");
    store.Save(loaded);
    Check(store.Load().Visible(prs).Count() == 2, "Restore persists");
    var legacyPath = Path.Combine(directory, "legacy.json");
    await File.WriteAllTextAsync(legacyPath, """{"dismissed":["one"],"seen":["one","two"]}""");
    var legacy = new JsonStore<LocalState>(legacyPath).Load();
    legacy.Migrate(DateTimeOffset.UtcNow);
    Check(legacy.IsDismissed("one") && legacy.Dismissed is null && legacy.Collapsed.Count == 0, "Legacy state migrates");
    legacy.Reactivate(prs, "me");
    Check(legacy.Dismissals["one"].HeadCommitOid == "c1", "Migrated dismissal gets commit baseline");
    var configPath = Path.Combine(directory, "config.json");
    var configStore = new JsonStore<Config>(configPath);
    Check(configStore.Load().Users.Count == 0, "New installations contain no watched users");
    configStore.Save(new Config { Users = ["example"], ExtraQualifiers = "org:example", Notify = false });
    Check(configStore.Load().ExtraQualifiers == "org:example" && !configStore.Load().Notify, "Config round trip");
    await File.WriteAllTextAsync(configPath, "broken");
    await ThrowsAsync<JsonException>(() => Task.Run(() => configStore.Load()), "Corrupt config must not reset silently");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => new Config { RefreshMinutes = 0 }.Validate()),
        "Invalid interval must fail");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

Console.WriteLine($"All {checks} Windows core checks passed.");

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request);
}
