using System.Text.Json.Serialization;

namespace GHNotif.Core;

public sealed record PullRequest(
    string Id, int Number, string Title, string Url, string Repository, string Author,
    string? AvatarUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ReviewDecision, string? Checks,
    string? HeadCommitOid = null, IReadOnlyList<PrActivity>? Activity = null)
{
    /// Why the PR is listed; a PR can match several searches.
    public IReadOnlySet<MatchReason> Reasons { get; init; } = new HashSet<MatchReason> { MatchReason.WatchedAuthor };

    /// True when, since the dismissal, someone pushed commits or someone other than the viewer commented/reviewed.
    public bool HasNewActivity(DismissalSnapshot snapshot, string? viewer) =>
        (snapshot.HeadCommitOid is not null && HeadCommitOid is not null && snapshot.HeadCommitOid != HeadCommitOid)
        || (Activity ?? []).Any(item => item.Date > snapshot.DismissedAt
            && !string.Equals(item.Login, viewer, StringComparison.OrdinalIgnoreCase));
}

public enum MatchReason { WatchedAuthor, Mention, ReviewRequested }

/// A comment or submitted review by a human (bots are skipped while parsing).
public sealed record PrActivity(string Login, DateTimeOffset Date);

/// State of a PR when it was dismissed; newer activity brings the PR back.
public sealed class DismissalSnapshot
{
    public DateTimeOffset DismissedAt { get; set; }
    public string? HeadCommitOid { get; set; }
}

public sealed class Config
{
    public List<string> Users { get; set; } = [];
    public string ExtraQualifiers { get; set; } = "";
    public int RefreshMinutes { get; set; } = 5;
    public bool Notify { get; set; } = true;
    /// Also show open PRs (by anyone) that @mention the signed-in user.
    public bool IncludeMentions { get; set; } = true;
    /// Also show PRs where a review is requested from the signed-in user personally (not via a team).
    public bool IncludeReviewRequests { get; set; }

    public bool HasSearches => Users.Count > 0 || IncludeMentions || IncludeReviewRequests;

    public Config Copy() => new()
    {
        Users = Users.ToList(), ExtraQualifiers = ExtraQualifiers, RefreshMinutes = RefreshMinutes, Notify = Notify,
        IncludeMentions = IncludeMentions, IncludeReviewRequests = IncludeReviewRequests
    };

    public void Validate()
    {
        if (Users is null || ExtraQualifiers is null || RefreshMinutes is < 1 or > 60)
            throw new InvalidDataException("Nieprawidłowa konfiguracja. Interwał musi wynosić 1–60 minut.");
        Users = QueryBuilder.Normalize(Users).ToList();
    }
}

public sealed class LocalState
{
    public Dictionary<string, DismissalSnapshot> Dismissals { get; set; } = [];
    /// Pre-snapshot format (IDs only); converted by <see cref="Migrate"/>.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? Dismissed { get; set; }
    public HashSet<string> Collapsed { get; set; } = [];
    public HashSet<string>? Seen { get; set; }

    public void Migrate(DateTimeOffset now)
    {
        Dismissals ??= [];
        Collapsed ??= [];
        foreach (var id in Dismissed ?? []) Dismissals.TryAdd(id, new DismissalSnapshot { DismissedAt = now });
        Dismissed = null;
    }

    public LocalState Clone() => new()
    {
        Dismissals = Dismissals.ToDictionary(kv => kv.Key,
            kv => new DismissalSnapshot { DismissedAt = kv.Value.DismissedAt, HeadCommitOid = kv.Value.HeadCommitOid }),
        Collapsed = Collapsed.ToHashSet(),
        Seen = Seen?.ToHashSet()
    };

    public bool IsDismissed(string id) => Dismissals.ContainsKey(id);

    public void Dismiss(PullRequest pr, DateTimeOffset now) =>
        Dismissals[pr.Id] = new DismissalSnapshot { DismissedAt = now, HeadCommitOid = pr.HeadCommitOid };

    public bool Restore(string id) => Dismissals.Remove(id);

    public void ToggleCollapsed(string repository)
    {
        if (!Collapsed.Remove(repository)) Collapsed.Add(repository);
    }

    public IEnumerable<PullRequest> Visible(IEnumerable<PullRequest> prs) => prs.Where(pr => !IsDismissed(pr.Id));

    /// Un-dismisses PRs with new activity and returns them.
    public List<PullRequest> Reactivate(IEnumerable<PullRequest> prs, string? viewer)
    {
        var reactivated = new List<PullRequest>();
        foreach (var pr in prs)
        {
            if (!Dismissals.TryGetValue(pr.Id, out var snapshot)) continue;
            if (pr.HasNewActivity(snapshot, viewer))
            {
                Dismissals.Remove(pr.Id);
                reactivated.Add(pr);
            }
            // Migrated dismissals get their commit baseline on the first refresh.
            else snapshot.HeadCommitOid ??= pr.HeadCommitOid;
        }
        return reactivated;
    }

    public List<PullRequest> Observe(IEnumerable<PullRequest> prs, bool notify, bool baseline,
        IReadOnlyCollection<PullRequest>? reactivated = null)
    {
        var current = prs.ToList();
        var alerts = notify && !baseline && Seen is not null
            ? current.Where(pr => !Seen.Contains(pr.Id) && !IsDismissed(pr.Id))
                .Concat((reactivated ?? []).Where(pr => Seen.Contains(pr.Id))).ToList()
            : [];
        Seen = current.Select(pr => pr.Id).ToHashSet();
        return alerts;
    }
}

public static class QueryBuilder
{
    public static IEnumerable<string> Normalize(IEnumerable<string> logins) =>
        logins.Select(login => login.Trim().TrimStart('@'))
            .Where(login => login.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<(string Query, MatchReason Reason)> Searches(Config config)
    {
        foreach (var query in Build(config.Users, config.ExtraQualifiers)) yield return (query, MatchReason.WatchedAuthor);
        // Mentions match any author, including the user; GitHub can't request a review from a PR's own author.
        string Viewer(string qualifiers) =>
            $"is:pr is:open draft:false {qualifiers} {config.ExtraQualifiers.Trim()}".Trim();
        if (config.IncludeMentions) yield return (Viewer("mentions:@me"), MatchReason.Mention);
        if (config.IncludeReviewRequests) yield return (Viewer("user-review-requested:@me -author:@me"), MatchReason.ReviewRequested);
    }

    public static IEnumerable<string> Build(IEnumerable<string> users, string extra) =>
        Normalize(users).Chunk(10).Select(batch =>
            ("is:pr is:open draft:false " + string.Join(" ", batch.Select(u => $"author:{u}")) +
             " " + extra.Trim()).Trim());
}
