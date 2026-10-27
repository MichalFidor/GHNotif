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
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<PullRequestReview> Reviews { get; init; } = [];

    /// True when, since the dismissal, someone pushed commits or someone other than the viewer commented/reviewed.
    public bool HasNewActivity(DismissalSnapshot snapshot, string? viewer) =>
        (snapshot.HeadCommitOid is not null && HeadCommitOid is not null && snapshot.HeadCommitOid != HeadCommitOid)
        || (Activity ?? []).Any(item => item.Date > snapshot.DismissedAt
            && !string.Equals(item.Login, viewer, StringComparison.OrdinalIgnoreCase));

    public DateTimeOffset? ViewerReviewAt(string? viewer)
    {
        if (viewer is null || HeadCommitOid is null) return null;
        var dates = Reviews.Where(review => string.Equals(review.Login, viewer, StringComparison.OrdinalIgnoreCase)
            && review.CommitOid == HeadCommitOid).Select(review => review.SubmittedAt).OfType<DateTimeOffset>();
        return dates.Cast<DateTimeOffset?>().Max();
    }

    public bool HasViewerReviewedCurrentHead(string? viewer) => ViewerReviewAt(viewer) is not null;
}

public enum MatchReason { WatchedAuthor, Mention, ReviewRequested }
public enum SortOrder { RecentlyUpdated, OldestFirst, LargestChanges }

/// A comment or submitted review by a human (bots are skipped while parsing).
public sealed record PrActivity(string Login, DateTimeOffset Date);
public sealed record PullRequestReview(string Login, string? CommitOid, DateTimeOffset? SubmittedAt);

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
    public bool AutoDismissReviewed { get; set; } = true;
    public List<string> ExcludedRepositories { get; set; } = [];
    public List<string> ExcludedLabels { get; set; } = [];
    public SortOrder SortOrder { get; set; }

    public bool HasSearches => Users.Count > 0 || IncludeMentions || IncludeReviewRequests;
    public bool Includes(PullRequest pr) =>
        !ExcludedRepositories.Contains(pr.Repository, StringComparer.OrdinalIgnoreCase)
        && !pr.Labels.Any(label => ExcludedLabels.Contains(label, StringComparer.OrdinalIgnoreCase));

    public Config Copy() => new()
    {
        Users = Users.ToList(), ExtraQualifiers = ExtraQualifiers, RefreshMinutes = RefreshMinutes, Notify = Notify,
        IncludeMentions = IncludeMentions, IncludeReviewRequests = IncludeReviewRequests,
        AutoDismissReviewed = AutoDismissReviewed, ExcludedRepositories = ExcludedRepositories.ToList(),
        ExcludedLabels = ExcludedLabels.ToList(), SortOrder = SortOrder
    };

    public void Validate()
    {
        if (Users is null || ExtraQualifiers is null || ExcludedRepositories is null || ExcludedLabels is null
            || RefreshMinutes is < 1 or > 60 || !Enum.IsDefined(SortOrder))
            throw new InvalidDataException("Nieprawidłowa konfiguracja. Interwał musi wynosić 1–60 minut.");
        Users = QueryBuilder.Normalize(Users).ToList();
        ExcludedRepositories = NormalizeValues(ExcludedRepositories);
        ExcludedLabels = NormalizeValues(ExcludedLabels);
    }

    private static List<string> NormalizeValues(IEnumerable<string> values) => values
        .Select(value => value.Trim()).Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class LocalState
{
    public Dictionary<string, DismissalSnapshot> Dismissals { get; set; } = [];
    /// Pre-snapshot format (IDs only); converted by <see cref="Migrate"/>.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? Dismissed { get; set; }
    public HashSet<string> Collapsed { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Snoozed { get; set; } = [];
    public HashSet<string>? Seen { get; set; }

    public void Migrate(DateTimeOffset now)
    {
        Dismissals ??= [];
        Collapsed ??= [];
        Snoozed ??= [];
        foreach (var id in Dismissed ?? []) Dismissals.TryAdd(id, new DismissalSnapshot { DismissedAt = now });
        Dismissed = null;
    }

    public LocalState Clone() => new()
    {
        Dismissals = Dismissals.ToDictionary(kv => kv.Key,
            kv => new DismissalSnapshot { DismissedAt = kv.Value.DismissedAt, HeadCommitOid = kv.Value.HeadCommitOid }),
        Collapsed = Collapsed.ToHashSet(),
        Snoozed = Snoozed.ToDictionary(kv => kv.Key, kv => kv.Value),
        Seen = Seen?.ToHashSet()
    };

    public bool IsDismissed(string id) => Dismissals.ContainsKey(id);
    public bool IsSnoozed(string id, DateTimeOffset now) => Snoozed.TryGetValue(id, out var until) && until > now;

    public void Dismiss(PullRequest pr, DateTimeOffset now) =>
        DismissAndUnsnooze(pr, now);

    private void DismissAndUnsnooze(PullRequest pr, DateTimeOffset now)
    {
        Snoozed.Remove(pr.Id);
        Dismissals[pr.Id] = new DismissalSnapshot { DismissedAt = now, HeadCommitOid = pr.HeadCommitOid };
    }

    public bool Restore(string id) => Dismissals.Remove(id);
    public void AutoDismissReviewed(IEnumerable<PullRequest> prs, string? viewer)
    {
        if (viewer is null) return;
        foreach (var pr in prs)
            if (!IsDismissed(pr.Id) && pr.ViewerReviewAt(viewer) is { } reviewedAt)
            {
                Snoozed.Remove(pr.Id);
                Dismissals[pr.Id] = new DismissalSnapshot
                {
                    DismissedAt = reviewedAt,
                    HeadCommitOid = pr.HeadCommitOid
                };
            }
    }
    public void Snooze(PullRequest pr, DateTimeOffset until)
    {
        Dismissals.Remove(pr.Id);
        Snoozed[pr.Id] = until;
    }
    public bool Unsnooze(string id) => Snoozed.Remove(id);
    public List<string> ExpireSnoozes(IEnumerable<PullRequest> current, DateTimeOffset now)
    {
        var due = current.Where(pr => Snoozed.TryGetValue(pr.Id, out var until) && until <= now)
            .Select(pr => pr.Id).ToList();
        foreach (var id in due) Snoozed.Remove(id);
        return due;
    }

    public void ToggleCollapsed(string repository)
    {
        if (!Collapsed.Remove(repository)) Collapsed.Add(repository);
    }

    public IEnumerable<PullRequest> Visible(IEnumerable<PullRequest> prs, DateTimeOffset? now = null) =>
        prs.Where(pr => !IsDismissed(pr.Id) && !IsSnoozed(pr.Id, now ?? DateTimeOffset.UtcNow));
    public IEnumerable<PullRequest> SnoozedPullRequests(IEnumerable<PullRequest> prs, DateTimeOffset? now = null) =>
        prs.Where(pr => IsSnoozed(pr.Id, now ?? DateTimeOffset.UtcNow));

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
        IReadOnlyCollection<PullRequest>? reactivated = null, IReadOnlySet<string>? wokeFromSnooze = null)
    {
        var current = prs.ToList();
        var alerts = notify && !baseline && Seen is not null
            ? current.Where(pr => !Seen.Contains(pr.Id) && !IsDismissed(pr.Id) && !IsSnoozed(pr.Id, DateTimeOffset.UtcNow))
                .Concat((reactivated ?? []).Where(pr => Seen.Contains(pr.Id))).ToList()
                .Concat(current.Where(pr => wokeFromSnooze?.Contains(pr.Id) == true && Seen.Contains(pr.Id)
                    && !IsDismissed(pr.Id))).ToList()
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
