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
    /// GitHub login of the head commit's author; the pusher isn't exposed by the API.
    public string? HeadCommitAuthor { get; init; }

    /// Human-readable list of what changed since the snapshot, e.g. "bob — zatwierdzenie".
    public List<string> Changes(DismissalSnapshot snapshot, string? viewer)
    {
        var lines = new List<string>();
        if (snapshot.HeadCommitOid is not null && HeadCommitOid is not null && snapshot.HeadCommitOid != HeadCommitOid)
            lines.Add(HeadCommitAuthor is null ? L("Nowe commity", "New commits") : L($"{HeadCommitAuthor} — nowe commity", $"{HeadCommitAuthor} — new commits"));
        foreach (var item in (Activity ?? []).Where(item => item.Date > snapshot.DismissedAt
                     && !string.Equals(item.Login, viewer, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Date))
        {
            var line = $"{item.Login} — {item.Kind.Label()}";
            if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase)) lines.Add(line);
        }
        return lines;
    }

    public List<string> NewActivity(NotificationSnapshot snapshot, string? viewer)
    {
        var lines = new List<string>();
        if (snapshot.HeadCommitOid is not null && HeadCommitOid is not null && snapshot.HeadCommitOid != HeadCommitOid)
            lines.Add(HeadCommitAuthor is null ? L("Nowe commity", "New commits") : L($"{HeadCommitAuthor} — nowe commity", $"{HeadCommitAuthor} — new commits"));
        foreach (var item in (Activity ?? []).Where(item => !snapshot.ActivityDates.ContainsKey(item.NotificationKey)
                     && !string.Equals(item.Login, viewer, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Date))
            lines.Add($"{item.Login} — {item.Kind.Label()}");
        return lines;
    }

    public NotificationSnapshot CreateNotificationSnapshot(NotificationSnapshot? previous)
    {
        var activityDates = previous?.ActivityDates.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var item in Activity ?? []) activityDates[item.NotificationKey] = item.Date;
        if (activityDates.Count > 200)
            activityDates = activityDates.OrderByDescending(kv => kv.Value).Take(200)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new NotificationSnapshot
        {
            HeadCommitOid = HeadCommitOid ?? previous?.HeadCommitOid,
            ActivityDates = activityDates
        };
    }

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
public sealed record PrActivity(string Login, DateTimeOffset Date, ActivityKind Kind = ActivityKind.Comment, string? Id = null)
{
    public string NotificationKey => Id ?? $"{Login.ToUpperInvariant()}|{Kind}|{Date.UtcDateTime.Ticks}";
}

public enum ActivityKind { Comment, Approved, ChangesRequested, ReviewComment, ReviewDismissed }

public sealed class NotificationSnapshot
{
    public string? HeadCommitOid { get; set; }
    public Dictionary<string, DateTimeOffset> ActivityDates { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PullRequestAlert(PullRequest PullRequest, IReadOnlyList<string> Details);

public static class ActivityText
{
    /// Maps a GitHub `PullRequestReviewState`.
    public static ActivityKind FromReviewState(string? state) => state switch
    {
        "APPROVED" => ActivityKind.Approved,
        "CHANGES_REQUESTED" => ActivityKind.ChangesRequested,
        "DISMISSED" => ActivityKind.ReviewDismissed,
        _ => ActivityKind.ReviewComment
    };

    public static string Label(this ActivityKind kind) => kind switch
    {
        ActivityKind.Approved => L("zatwierdzenie ✅", "approved ✅"),
        ActivityKind.ChangesRequested => L("prośba o zmiany", "changes requested"),
        ActivityKind.ReviewComment => L("komentarz w review", "review comment"),
        ActivityKind.ReviewDismissed => L("odrzucone review", "review dismissed"),
        _ => L("komentarz", "comment")
    };

    /// Notification body lines: at most `limit` entries, with a "+N więcej" suffix for the rest.
    public static string Summarize(IReadOnlyList<string> lines, int limit = 3) => lines.Count <= limit
        ? string.Join("\n", lines)
        : string.Join("\n", lines.Take(limit).Append(L($"+{lines.Count - limit} więcej", $"+{lines.Count - limit} more")));

    public static string NewPullRequest(PullRequest pr) =>
        pr.Reasons.Contains(MatchReason.ReviewRequested) ? L("Poproszono Cię o review", "Your review was requested")
        : pr.Reasons.Contains(MatchReason.Mention) ? L("Wspomniano Cię w PR-ze", "You were mentioned") : L($"Nowy PR od {pr.Author}", $"New PR by {pr.Author}");
}
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
    public ReviewReminderSettings ReviewReminder { get; set; } = new();
    /// Configs from before the language option were always Polish; new installations start with System.
    public AppLanguage Language { get; set; } = AppLanguage.Polish;

    public bool HasSearches => Users.Count > 0 || IncludeMentions || IncludeReviewRequests;
    public bool Includes(PullRequest pr) =>
        !ExcludedRepositories.Contains(pr.Repository, StringComparer.OrdinalIgnoreCase)
        && !pr.Labels.Any(label => ExcludedLabels.Contains(label, StringComparer.OrdinalIgnoreCase));

    public Config Copy() => new()
    {
        Users = Users.ToList(), ExtraQualifiers = ExtraQualifiers, RefreshMinutes = RefreshMinutes, Notify = Notify,
        IncludeMentions = IncludeMentions, IncludeReviewRequests = IncludeReviewRequests,
        AutoDismissReviewed = AutoDismissReviewed, ExcludedRepositories = ExcludedRepositories.ToList(),
        ExcludedLabels = ExcludedLabels.ToList(), SortOrder = SortOrder, ReviewReminder = ReviewReminder.Copy(),
        Language = Language
    };

    public void Validate()
    {
        if (Users is null || ExtraQualifiers is null || ExcludedRepositories is null || ExcludedLabels is null
            || RefreshMinutes is < 1 or > 60 || !Enum.IsDefined(SortOrder) || !Enum.IsDefined(Language))
            throw new InvalidDataException(L("Nieprawidłowa konfiguracja. Interwał musi wynosić 1–60 minut.", "Invalid configuration. The interval must be 1–60 minutes."));
        Users = QueryBuilder.Normalize(Users).ToList();
        ExcludedRepositories = NormalizeValues(ExcludedRepositories);
        ExcludedLabels = NormalizeValues(ExcludedLabels);
        ReviewReminder ??= new();
        ReviewReminder.Normalize();
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
    public Dictionary<string, NotificationSnapshot> NotificationSnapshots { get; set; } = new(StringComparer.Ordinal);
    public ReviewReminderState ReviewReminder { get; set; } = new();

    public void Migrate(DateTimeOffset now)
    {
        Dismissals ??= [];
        Collapsed ??= [];
        Snoozed ??= [];
        NotificationSnapshots ??= new(StringComparer.Ordinal);
        ReviewReminder ??= new();
        foreach (var id in Dismissed ?? []) Dismissals.TryAdd(id, new DismissalSnapshot { DismissedAt = now });
        Dismissed = null;
    }

    public LocalState Clone() => new()
    {
        Dismissals = Dismissals.ToDictionary(kv => kv.Key,
            kv => new DismissalSnapshot { DismissedAt = kv.Value.DismissedAt, HeadCommitOid = kv.Value.HeadCommitOid }),
        Collapsed = Collapsed.ToHashSet(),
        Snoozed = Snoozed.ToDictionary(kv => kv.Key, kv => kv.Value),
        Seen = Seen?.ToHashSet(),
        NotificationSnapshots = NotificationSnapshots.ToDictionary(kv => kv.Key, kv => new NotificationSnapshot
        {
            HeadCommitOid = kv.Value.HeadCommitOid,
            ActivityDates = kv.Value.ActivityDates.ToDictionary(item => item.Key, item => item.Value)
        }, StringComparer.Ordinal),
        ReviewReminder = ReviewReminder.Copy()
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
                var snapshot = new DismissalSnapshot
                {
                    DismissedAt = reviewedAt,
                    HeadCommitOid = pr.HeadCommitOid
                };
                if (pr.HasNewActivity(snapshot, viewer)) continue;
                Snoozed.Remove(pr.Id);
                Dismissals[pr.Id] = snapshot;
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
    public List<PullRequest> Reactivate(IEnumerable<PullRequest> prs, string? viewer) =>
        Reactivate(prs, viewer, out _);

    /// Un-dismisses PRs with new activity; `changes` describes what happened per PR ID.
    public List<PullRequest> Reactivate(IEnumerable<PullRequest> prs, string? viewer,
        out Dictionary<string, List<string>> changes)
    {
        var reactivated = new List<PullRequest>();
        changes = new(StringComparer.Ordinal);
        foreach (var pr in prs)
        {
            if (!Dismissals.TryGetValue(pr.Id, out var snapshot)) continue;
            if (pr.HasNewActivity(snapshot, viewer))
            {
                var described = pr.Changes(snapshot, viewer);
                changes[pr.Id] = described.Count > 0 ? described : [L("Nowa aktywność", "New activity")];
                Dismissals.Remove(pr.Id);
                reactivated.Add(pr);
            }
            // Migrated dismissals get their commit baseline on the first refresh.
            else snapshot.HeadCommitOid ??= pr.HeadCommitOid;
        }
        return reactivated;
    }

    public List<PullRequest> Observe(IEnumerable<PullRequest> prs, bool notify, bool baseline,
        IReadOnlyCollection<PullRequest>? reactivated = null, IReadOnlySet<string>? wokeFromSnooze = null) =>
        ObserveAlerts(prs, notify, baseline, null, reactivated, wokeFromSnooze)
            .Select(alert => alert.PullRequest).ToList();

    public List<PullRequestAlert> ObserveAlerts(IEnumerable<PullRequest> prs, bool notify, bool baseline,
        string? viewer, IReadOnlyCollection<PullRequest>? reactivated = null,
        IReadOnlySet<string>? wokeFromSnooze = null,
        IReadOnlyDictionary<string, List<string>>? reactivationChanges = null)
    {
        var current = prs.ToList();
        var previousSeen = Seen;
        var alerts = new List<PullRequestAlert>();
        if (notify && !baseline && previousSeen is not null)
        {
            var fresh = current.Where(pr => !previousSeen.Contains(pr.Id) && !IsDismissed(pr.Id)
                && !IsSnoozed(pr.Id, DateTimeOffset.UtcNow)).ToList();
            alerts.AddRange(fresh.Select(pr => new PullRequestAlert(pr, [ActivityText.NewPullRequest(pr)])));
            foreach (var pr in (reactivated ?? []).Where(pr => previousSeen.Contains(pr.Id)))
            {
                var details = reactivationChanges is not null && reactivationChanges.TryGetValue(pr.Id, out var changes)
                    ? changes : [L("Nowa aktywność", "New activity")];
                alerts.Add(new PullRequestAlert(pr, details));
            }
            foreach (var pr in current.Where(pr => wokeFromSnooze?.Contains(pr.Id) == true
                         && previousSeen.Contains(pr.Id) && !IsDismissed(pr.Id)))
                alerts.Add(new PullRequestAlert(pr, [L("Koniec odłożenia", "Snooze ended")]));

            var handled = alerts.Select(alert => alert.PullRequest.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var pr in current.Where(pr => previousSeen.Contains(pr.Id) && !handled.Contains(pr.Id)
                         && !IsDismissed(pr.Id) && !IsSnoozed(pr.Id, DateTimeOffset.UtcNow)))
            {
                if (!NotificationSnapshots.TryGetValue(pr.Id, out var snapshot)) continue;
                var changes = pr.NewActivity(snapshot, viewer);
                if (changes.Count > 0) alerts.Add(new PullRequestAlert(pr, changes));
            }
        }
        NotificationSnapshots = current.ToDictionary(pr => pr.Id,
            pr => pr.CreateNotificationSnapshot(NotificationSnapshots.GetValueOrDefault(pr.Id)),
            StringComparer.Ordinal);
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
