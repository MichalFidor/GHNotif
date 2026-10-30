namespace GHNotif.Core;

public sealed class PullRequestInbox
{
    public sealed class Entry
    {
        public NotificationSnapshot Snapshot { get; set; } = new();
        public bool IsNew { get; set; }
        public List<PrActivity> Activity { get; set; } = [];
        public bool HasNewCommits { get; set; }
        public string? CommitAuthor { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsUnread => IsNew || HasNewCommits || Activity.Count > 0;

        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<string> Details
        {
            get
            {
                var lines = new List<string>();
                if (HasNewCommits)
                    lines.Add(AlertEvent.Commits.Line(CommitAuthor is null ? L("Nowe commity", "New commits")
                        : L($"{CommitAuthor} - nowe commity", $"{CommitAuthor} - new commits")));
                lines.AddRange(Activity.Select(item => item.Kind.Event().Line($"{item.Login} - {item.Kind.Label()}")));
                return lines;
            }
        }

        public Entry Copy() => new()
        {
            Snapshot = new NotificationSnapshot
            {
                HeadCommitOid = Snapshot.HeadCommitOid,
                ActivityDates = Snapshot.ActivityDates.ToDictionary(kv => kv.Key, kv => kv.Value)
            },
            IsNew = IsNew, Activity = Activity.ToList(), HasNewCommits = HasNewCommits, CommitAuthor = CommitAuthor
        };
    }

    public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
    public bool Initialized { get; set; }

    public void Observe(IEnumerable<PullRequest> prs, string? viewer, bool baseline = false)
    {
        foreach (var pr in prs)
        {
            if (!Entries.TryGetValue(pr.Id, out var entry))
            {
                Entries[pr.Id] = new Entry
                {
                    Snapshot = pr.CreateNotificationSnapshot(null), IsNew = Initialized && !baseline
                };
                continue;
            }
            if (entry.Snapshot.HeadCommitOid is not null && pr.HeadCommitOid is not null
                && entry.Snapshot.HeadCommitOid != pr.HeadCommitOid)
            {
                entry.HasNewCommits = true;
                entry.CommitAuthor = pr.HeadCommitAuthor;
            }
            foreach (var item in (pr.Activity ?? []).OrderBy(item => item.Date)
                         .Where(item => !entry.Snapshot.ActivityDates.ContainsKey(item.NotificationKey)
                             && !string.Equals(item.Login, viewer, StringComparison.OrdinalIgnoreCase)))
                if (!entry.Activity.Any(previous =>
                        string.Equals(previous.Login, item.Login, StringComparison.OrdinalIgnoreCase) && previous.Kind == item.Kind))
                    entry.Activity.Add(item);
            entry.Snapshot = pr.CreateNotificationSnapshot(entry.Snapshot);
        }
        Initialized = true;
    }

    public void MarkRead(PullRequest pr) =>
        Entries[pr.Id] = new Entry { Snapshot = pr.CreateNotificationSnapshot(Entries.GetValueOrDefault(pr.Id)?.Snapshot) };

    public PullRequestInbox Copy() => new()
    {
        Initialized = Initialized,
        Entries = Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal)
    };
}
