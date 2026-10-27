namespace GHNotif.Core;

public sealed record PullRequest(
    string Id, int Number, string Title, string Url, string Repository, string Author,
    string? AvatarUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ReviewDecision, string? Checks);

public sealed class Config
{
    public List<string> Users { get; set; } = [];
    public string ExtraQualifiers { get; set; } = "";
    public int RefreshMinutes { get; set; } = 5;
    public bool Notify { get; set; } = true;

    public void Validate()
    {
        if (Users is null || ExtraQualifiers is null || RefreshMinutes is < 1 or > 60)
            throw new InvalidDataException("Nieprawidłowa konfiguracja. Interwał musi wynosić 1–60 minut.");
        Users = QueryBuilder.Normalize(Users).ToList();
    }
}

public sealed class LocalState
{
    public HashSet<string> Dismissed { get; set; } = [];
    public HashSet<string>? Seen { get; set; }

    public IEnumerable<PullRequest> Visible(IEnumerable<PullRequest> prs) =>
        prs.Where(pr => !Dismissed.Contains(pr.Id));

    public List<PullRequest> Observe(IEnumerable<PullRequest> prs, bool notify, bool baseline)
    {
        var current = prs.ToList();
        var fresh = notify && !baseline && Seen is not null
            ? current.Where(pr => !Seen.Contains(pr.Id) && !Dismissed.Contains(pr.Id)).ToList()
            : [];
        Seen = current.Select(pr => pr.Id).ToHashSet();
        return fresh;
    }
}

public static class QueryBuilder
{
    public static IEnumerable<string> Normalize(IEnumerable<string> logins) =>
        logins.Select(login => login.Trim().TrimStart('@'))
            .Where(login => login.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Build(IEnumerable<string> users, string extra) =>
        Normalize(users).Chunk(10).Select(batch =>
            ("is:pr is:open draft:false " + string.Join(" ", batch.Select(u => $"author:{u}")) +
             " " + extra.Trim()).Trim());
}
