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
    "repository":{"nameWithOwner":"example/demo"},"commits":{"nodes":[{"commit":{"statusCheckRollup":null}}]}}
    """;
string Page(string nodes, bool more, string cursor = "cursor") => $$$$"""
    {"data":{"search":{"issueCount":2,"pageInfo":{"hasNextPage":{{{{more.ToString().ToLowerInvariant()}}}},"endCursor":"{{{{cursor}}}}"},
    "nodes":[{{{{nodes}}}}]}}}
    """;
using (var document = JsonDocument.Parse(Node("legacy-id", true)))
{
    var pr = GitHubClient.ParsePullRequest(document.RootElement);
    Check(pr.Id == "legacy-id" && pr.Author == "ghost" && pr.Checks is null, "Deleted author and missing CI");
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
state.Dismissed.Add("one");
Check(state.Visible(prs).Single().Id == "two", "Dismiss hides PR from list and count");
state.Observe([], true, false);
Check(state.Observe(prs, true, false).All(pr => pr.Id != "one"), "Dismissed PR reappearance does not notify");
Check(state.Visible(prs).Single().Id == "two", "Dismiss survives refresh and reappearance");

var directory = Path.Combine(Path.GetTempPath(), "ghnotif-checks-" + Guid.NewGuid());
try
{
    var path = Path.Combine(directory, "state.json");
    var store = new JsonStore<LocalState>(path);
    store.Save(state);
    var loaded = store.Load();
    Check(loaded.Dismissed.Contains("one"), "Dismiss persists across restarts");
    loaded.Dismissed.Remove("one");
    store.Save(loaded);
    Check(store.Load().Visible(prs).Count() == 2, "Restore persists");
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
