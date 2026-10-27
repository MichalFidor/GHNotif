using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GHNotif.Core;

public sealed record SearchResult(List<PullRequest> PullRequests, HashSet<string> BlockedOrganizations,
    string? ViewerLogin = null);

public sealed class GitHubClient(HttpClient http)
{
    private const string SearchQuery = """
        query($q: String!, $after: String) {
          viewer { login }
          search(query: $q, type: ISSUE, first: 100, after: $after) {
            issueCount pageInfo { hasNextPage endCursor }
            nodes { ... on PullRequest {
              id number title url createdAt updatedAt reviewDecision
              author { login avatarUrl } repository { nameWithOwner }
              commits(last: 1) { nodes { commit { oid statusCheckRollup { state } } } }
              comments(last: 10) { nodes { createdAt author { __typename login } } }
              reviews(last: 10) { nodes { submittedAt author { __typename login } } }
            } }
          }
        }
        """;

    public async Task<SearchResult> SearchAsync(string token, Config config, CancellationToken ct = default)
    {
        var prs = new Dictionary<string, PullRequest>();
        var blocked = new HashSet<string>();
        string? viewer = null;
        foreach (var query in QueryBuilder.Build(config.Users, config.ExtraQualifiers))
        {
            string? cursor = null;
            for (var page = 0; ; page++)
            {
                using var response = await PostAsync(token, SearchQuery, new { q = query, after = cursor }, ct);
                if (response.Headers.TryGetValues("X-GitHub-SSO", out var headers))
                    blocked.UnionWith(ParseSso(string.Join(";", headers)));
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                ThrowErrors(json.RootElement);
                var data = json.RootElement.GetProperty("data");
                if (data.TryGetProperty("viewer", out var viewerNode)) viewer ??= Text(viewerNode, "login");
                var search = data.GetProperty("search");
                if (search.GetProperty("issueCount").GetInt32() > 1000)
                    throw new InvalidOperationException("GitHub ogranicza wyszukiwanie do 1000 wyników. Zawęź zakres w ustawieniach.");
                foreach (var node in search.GetProperty("nodes").EnumerateArray())
                {
                    if (node.ValueKind == JsonValueKind.Null || !node.TryGetProperty("id", out _)) continue;
                    var pr = ParsePullRequest(node);
                    prs[pr.Id] = pr;
                }
                var info = search.GetProperty("pageInfo");
                if (!info.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = Text(info, "endCursor")
                    ?? throw new InvalidDataException("GitHub nie zwrócił kursora następnej strony.");
                if (page >= 9) throw new InvalidDataException("Przekroczono limit stron GitHub.");
            }
        }
        return new(prs.Values.OrderByDescending(pr => pr.UpdatedAt).ToList(), blocked, viewer);
    }

    public async Task<string> CanonicalLoginAsync(string token, string login, CancellationToken ct = default)
    {
        using var response = await PostAsync(token,
            "query($login:String!){user(login:$login){login}}", new { login }, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        ThrowErrors(json.RootElement);
        var user = json.RootElement.GetProperty("data").GetProperty("user");
        return Text(user, "login") ?? throw new InvalidOperationException("Nie znaleziono użytkownika.");
    }

    private async Task<HttpResponseMessage> PostAsync(string token, string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GHNotif/1.0");
        request.Content = JsonContent.Create(new { query, variables });
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Unauthorized
                ? "Token GitHub jest nieprawidłowy lub wygasł."
                : $"GitHub API: HTTP {(int)response.StatusCode}. Sprawdź dostęp, SSO lub limit zapytań.";
            response.Dispose();
            throw new HttpRequestException(message);
        }
        return response;
    }

    public static void ThrowErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException("GitHub API: " +
                string.Join("; ", errors.EnumerateArray().Select(e => e.GetProperty("message").GetString())));
    }

    public static HashSet<string> ParseSso(string header)
    {
        if (!header.Contains("partial-results", StringComparison.OrdinalIgnoreCase)) return [];
        var field = header.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith("organizations=", StringComparison.OrdinalIgnoreCase));
        return field is null ? [] : field["organizations=".Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    public static PullRequest ParsePullRequest(JsonElement node)
    {
        var author = node.GetProperty("author");
        string? checks = null;
        string? oid = null;
        var commits = node.GetProperty("commits").GetProperty("nodes");
        if (commits.GetArrayLength() > 0)
        {
            var commit = commits[0].GetProperty("commit");
            oid = Text(commit, "oid");
            if (commit.TryGetProperty("statusCheckRollup", out var rollup)) checks = Text(rollup, "state");
        }
        var activity = Activity(node, "comments", "createdAt").Concat(Activity(node, "reviews", "submittedAt")).ToList();
        return new(node.GetProperty("id").GetString()!, node.GetProperty("number").GetInt32(),
            node.GetProperty("title").GetString()!, node.GetProperty("url").GetString()!,
            node.GetProperty("repository").GetProperty("nameWithOwner").GetString()!,
            Text(author, "login") ?? "ghost", Text(author, "avatarUrl"),
            node.GetProperty("createdAt").GetDateTimeOffset(), node.GetProperty("updatedAt").GetDateTimeOffset(),
            Text(node, "reviewDecision"), checks, oid, activity);
    }

    private static IEnumerable<PrActivity> Activity(JsonElement node, string connection, string dateField)
    {
        if (!node.TryGetProperty(connection, out var conn) || conn.ValueKind != JsonValueKind.Object) yield break;
        foreach (var item in conn.GetProperty("nodes").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("author", out var author)) continue;
            var login = Text(author, "login");
            var date = Text(item, dateField);
            if (login is null || date is null || Text(author, "__typename") == "Bot") continue;
            yield return new PrActivity(login, DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
