using System.Net;
using System.Text.Json;
using GHNotif.Core;
using static GHNotif.Core.Localization;

var checks = 0;
Localization.Language = AppLanguage.Polish;
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
    "additions":14,"deletions":3,"labels":{"nodes":[{"name":"wip"}]},
    "repository":{"nameWithOwner":"example/demo"},"commits":{"nodes":[{"commit":{"oid":"c1","author":{"user":{"login":"pusher"}},"statusCheckRollup":null}}]},
    "comments":{"nodes":[{"id":"comment-1","createdAt":"2026-10-03T10:00:00Z","author":{"__typename":"User","login":"reviewer"}},
      {"createdAt":"2026-10-03T11:00:00Z","author":{"__typename":"Bot","login":"ci"}},{"createdAt":"2026-10-03T12:00:00Z","author":null}]},
    "reviews":{"nodes":[{"id":"review-1","submittedAt":"2026-10-04T10:00:00Z","state":"APPROVED","commit":{"oid":"c1"},
      "author":{"__typename":"User","login":"me"}},{"submittedAt":null,"author":{"__typename":"User","login":"pending"}}]}}
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
    Check(pr.Additions == 14 && pr.Deletions == 3 && pr.Labels.SequenceEqual(["wip"]), "Diff size and labels parsed");
    Check(pr.HasViewerReviewedCurrentHead("ME"), "Review on current head identified case-insensitively");
    var reviewedState = new LocalState();
    reviewedState.AutoDismissReviewed([pr], "me");
    Check(reviewedState.IsDismissed(pr.Id), "Current-head review automatically dismisses PR");
    Check(reviewedState.Reactivate([pr with { HeadCommitOid = "c2" }], "me").Single().Id == pr.Id,
        "New commit after review reactivates PR");
    Check(pr.Activity!.Select(a => a.Login).SequenceEqual(["reviewer", "me"]), "Only human, submitted activity kept");
    Check(pr.Activity!.Select(a => a.Kind).SequenceEqual([ActivityKind.Comment, ActivityKind.Approved]), "Activity kinds parsed");
    Check(pr.HeadCommitAuthor == "pusher", "Head commit author parsed");
    var since = new DismissalSnapshot { DismissedAt = DateTimeOffset.MinValue, HeadCommitOid = "c0" };
    Check(pr.Changes(since, "me").SequenceEqual(["↑ pusher - nowe commity", "• reviewer - komentarz"]),
        "Changes describe who did what, excluding the viewer");

    var reviewState = new LocalState();
    reviewState.AutoDismissReviewed([pr], "me");
    reviewState.ObserveAlerts([pr], true, true, "me");
    var reviewAt = pr.ViewerReviewAt("me")!.Value;
    var comment = new PrActivity("commenter", reviewAt.AddMinutes(1), Id: "after-review");
    var commented = pr with { Activity = [.. pr.Activity!, comment] };
    List<PullRequestAlert> RefreshReviewed(PullRequest current)
    {
        reviewState.AutoDismissReviewed([current], "me");
        var returned = reviewState.Reactivate([current], "me", out var details);
        return reviewState.ObserveAlerts([current], true, false, "me", returned,
            reactivationChanges: details);
    }
    Check(RefreshReviewed(commented).Single().Details.SequenceEqual(["• commenter - komentarz"]),
        "Comment after review notifies once");
    for (var i = 0; i < 4; i++)
    {
        reviewState = JsonSerializer.Deserialize<LocalState>(JsonSerializer.Serialize(reviewState))!;
        Check(RefreshReviewed(commented).Count == 0 && !reviewState.IsDismissed(pr.Id),
            "Repeated refresh and state reload do not re-notify a comment after review");
    }
    var nextComment = new PrActivity("commenter", reviewAt.AddMinutes(2), Id: "next-after-review");
    commented = commented with { Activity = [.. commented.Activity!, nextComment] };
    Check(RefreshReviewed(commented).Single().Details.SequenceEqual(["• commenter - komentarz"]),
        "A genuinely new comment after review still notifies");
    Check(RefreshReviewed(commented).Count == 0, "New comment is not repeated either");
    reviewState.Snooze(commented, DateTimeOffset.UtcNow.AddHours(1));
    Check(RefreshReviewed(commented).Count == 0 && reviewState.IsSnoozed(pr.Id, DateTimeOffset.UtcNow),
        "Old review does not cancel snooze after later activity");
    var reviewedAgain = commented with
    {
        Reviews = [new PullRequestReview("me", "c1", reviewAt.AddMinutes(3))]
    };
    Check(RefreshReviewed(reviewedAgain).Count == 0 && reviewState.IsDismissed(pr.Id),
        "A newer review marks the PR done again");
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
var result = await client.SearchAsync("test-token", new Config { Users = ["example"], IncludeMentions = false });
Check(requests.Count == 2 && requests[1].Contains("cursor"), "Pagination passes cursor");
Check(result.PullRequests.Count == 2, "Deduplicate across pages");
Check(result.BlockedOrganizations.SetEquals(["123"]), "SSO surfaced with search results");
Check(result.ViewerLogin == "me", "Viewer login parsed");
Check(result.PullRequests.All(pr => pr.Reasons.SetEquals([MatchReason.WatchedAuthor])), "Author search reason");

var searches = QueryBuilder.Searches(new Config { Users = ["a"], ExtraQualifiers = "org:x" }).ToList();
Check(searches.Select(s => s.Reason).SequenceEqual([MatchReason.WatchedAuthor, MatchReason.Mention]),
    "Mentions on, review requests off by default");
Check(searches[1].Query == "is:pr is:open draft:false mentions:@me org:x", "Mention query includes every author");
Check(QueryBuilder.Searches(new Config { IncludeMentions = false, IncludeReviewRequests = true }).Single().Query ==
    "is:pr is:open draft:false user-review-requested:@me -author:@me", "Personal review request query");
Check(!new Config { IncludeMentions = false }.HasSearches && new Config().HasSearches, "HasSearches");
var legacyConfig = JsonSerializer.Deserialize<Config>("{\"users\":[\"a\"],\"notify\":false}",
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
Check(legacyConfig.IncludeMentions && !legacyConfig.IncludeReviewRequests, "Old config gets new defaults");

var filtering = new Config { ExcludedRepositories = ["EXAMPLE/DEMO"] };
var parsedPr = GitHubClient.ParsePullRequest(JsonDocument.Parse(Node("filter")).RootElement);
Check(!filtering.Includes(parsedPr), "Repository exclusion is case-insensitive");
filtering.ExcludedRepositories.Clear();
filtering.ExcludedLabels = ["WIP"];
Check(!filtering.Includes(parsedPr), "Label exclusion is case-insensitive");

var merging = 0;
using var mergeHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(++merging == 1 ? Page(Node("one"), false) : Page(Node("one") + "," + Node("three"), false))
})));
var merged = await new GitHubClient(mergeHttp).SearchAsync("t", new Config { Users = ["example"] });
Check(merged.PullRequests.Single(pr => pr.Id == "one").Reasons.SetEquals([MatchReason.WatchedAuthor, MatchReason.Mention])
    && merged.PullRequests.Single(pr => pr.Id == "three").Reasons.SetEquals([MatchReason.Mention]), "Reasons merged across searches");

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
var activeState = new LocalState();
Check(activeState.ObserveAlerts([one], true, false, "me").Count == 0, "Active activity baseline does not notify");
var activeReview = one with
{
    Activity = (one.Activity ?? []).Append(new("reviewer", later, ActivityKind.ChangesRequested, "review-new")).ToList()
};
var activityAlert = activeState.ObserveAlerts([activeReview], true, false, "me").Single();
Check(activityAlert.Details.SequenceEqual(["✗ reviewer - prośba o zmiany"]),
    "New review on an active PR reports actor and action");
Check(activeState.ObserveAlerts([activeReview], true, false, "me").Count == 0, "Active review does not notify twice");
var activeCommit = activeReview with { HeadCommitOid = "c2", HeadCommitAuthor = "pusher" };
Check(activeState.ObserveAlerts([activeCommit], true, false, "me").Single().Details
    .SequenceEqual(["↑ pusher - nowe commity"]), "New commit on an active PR is reported");
Check(activeState.Clone().NotificationSnapshots.ContainsKey("one"), "Notification state is cloned for persistence");
var probe = state.Clone();
Check(probe.Reactivate([one with { Activity = [new("ME", later)] }], "me").Count == 0, "Own comment ignored");
Check(probe.Reactivate([one with { Activity = [new("reviewer", later, ActivityKind.ChangesRequested)] }], "me",
        out var described).Count == 1 && !probe.IsDismissed("one"),
    "New review by someone else reactivates");
Check(described["one"].SequenceEqual(["✗ reviewer - prośba o zmiany"]), "Reactivation reports who did what");
Check(ActivityText.Summarize(["a", "b", "c", "d"]) == "a\nb\nc\n+1 więcej", "Long change lists truncated");
Check(state.IsDismissed("one"), "Clone is independent");
probe = state.Clone();
Check(probe.Reactivate([one with { HeadCommitOid = "c2" }], "me").Count == 1, "New commit reactivates");
var alerts = state.Clone();
var back = alerts.Reactivate([one with { HeadCommitOid = "c2" }, prs[1]], "me");
Check(alerts.Observe(prs, true, false, back).Select(pr => pr.Id).SequenceEqual(["one"]), "Reactivated PR notifies");
state.ToggleCollapsed("example/demo");
Check(state.Collapsed.Contains("example/demo"), "Collapse toggles on");
state.Snooze(one, DateTimeOffset.UtcNow.AddHours(2));
Check(state.Visible(prs).All(pr => pr.Id != "one") && state.SnoozedPullRequests(prs).Any(pr => pr.Id == "one"),
    "Snoozed PR is hidden from active and retained in snoozed view");
Check(state.Clone().Snoozed.ContainsKey("one"), "Snooze state is cloned for persistence");
var expired = state.ExpireSnoozes(prs, DateTimeOffset.UtcNow.AddHours(3)).ToHashSet();
Check(expired.Contains("one") && state.Visible(prs).Any(pr => pr.Id == "one"), "Expired snooze returns PR to active");
state.Dismiss(one, DateTimeOffset.UtcNow);

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

var update = new GitHubRelease("v0.2.0", "https://github.com/MichalFidor/GHNotif/releases/tag/v0.2.0", false, false);
Check(update.IsNewer("0.1.0"), "New release detected");
Check(!update.IsNewer("0.2.0") && !update.IsNewer("0.3.0"), "Same and older releases ignored");
Check((update with { TagName = "v0.10.0" }).IsNewer("0.9.0"), "Numeric version comparison");
Check(!(update with { Draft = true }).IsNewer("0.1.0"), "Draft release ignored");
Check(!(update with { Prerelease = true }).IsNewer("0.1.0"), "Prerelease ignored");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() => GitHubRelease.ParseVersion("0.1-beta")), "Invalid version rejected");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
    (update with { HtmlUrl = "https://example.com/" }).IsNewer("0.1.0")), "Foreign update URL rejected");
var packageRelease = update with
{
    Assets = [new GitHubReleaseAsset("GHNotif-win-x64.zip",
        "https://github.com/MichalFidor/GHNotif/releases/download/v0.2.0/GHNotif-win-x64.zip")]
};
Check(packageRelease.DownloadUri("GHNotif-win-x64.zip").Host == "github.com", "Official update asset accepted");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
    (packageRelease with
    {
        Assets = [new GitHubReleaseAsset("GHNotif-win-x64.zip",
            "https://github.com/MichalFidor/GHNotif/releases/download/v0.1.0/GHNotif-win-x64.zip")]
    }).DownloadUri("GHNotif-win-x64.zip")), "Asset from a different release rejected");
Check(packageRelease.Checksum("GHNotif-win-x64.zip",
    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  GHNotif-win-x64.zip")
    == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Update checksum parsed");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() => packageRelease.Checksum("missing.zip", "")),
    "Missing update checksum rejected");
using var noRelease = new HttpClient(new FakeHandler(_ =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
Check(await GitHubRelease.LatestAsync(noRelease) is null, "No published release is supported");
using var rateLimited = new HttpClient(new FakeHandler(_ =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
await ThrowsAsync<HttpRequestException>(() => GitHubRelease.LatestAsync(rateLimited), "Update HTTP errors surfaced");
using var releases = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(update))
})));
Check((await GitHubRelease.LatestAsync(releases))?.IsNewer("0.1.0") == true, "Release response parsed");

var warsaw = TimeZoneInfo.CreateCustomTimeZone("PL", TimeSpan.FromHours(1), "PL", "PL");
DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(1));
var reminder = new ReviewReminderSettings
{
    Alarms = [new() { Time = "14:00" }, new() { Time = "9:00", Enabled = false }, new() { Time = "09:00" }],
    Weekdays = [5, 1, 2, 3, 4, 9]
};
reminder.Normalize();
Check(reminder.Alarms.Select(alarm => alarm.Time).SequenceEqual(["09:00", "14:00"]) && reminder.Alarms.All(alarm => alarm.Enabled)
    && reminder.Weekdays.SequenceEqual([1, 2, 3, 4, 5]), "Reminder times and days are normalized");
Check(!ReviewReminderSettings.TryParseTime("24:00", out _) && !ReviewReminderSettings.TryParseTime("ab", out _),
    "Invalid reminder time rejected");
try { new ReviewReminderSettings { Alarms = [new() { Time = "25:00" }] }.Normalize(); Check(false, "Invalid time must fail validation"); }
catch (InvalidDataException) { checks++; }
Check(ReviewReminderSettings.IsoWeekday(new DateTime(2026, 10, 26)) == 1
    && ReviewReminderSettings.IsoWeekday(new DateTime(2026, 10, 25)) == 7, "ISO weekdays");
Check(reminder.NextOccurrence(At(26, 9, 30), warsaw) == At(26, 14), "Next reminder same day");
Check(reminder.NextOccurrence(At(30, 15), warsaw) == new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1)),
    "Friday afternoon -> Monday 9:00");
var reminderState = new ReviewReminderState();
Check(!reminderState.ShouldShow(reminder, At(26, 10), warsaw) && reminderState.LastHandled == At(26, 10),
    "First run records a baseline");
Check(!reminderState.ShouldShow(reminder, At(26, 13, 59), warsaw), "Nothing before 14:00");
Check(reminderState.ShouldShow(reminder, At(26, 14), warsaw), "Reminder fires at 14:00");
Check(!reminderState.ShouldShow(reminder, At(26, 14, 1), warsaw), "Reminder fires once");
Check(reminderState.ShouldShow(reminder, At(27, 16), warsaw) && !reminderState.ShouldShow(reminder, At(27, 16, 1), warsaw),
    "Missed reminders collapse into one catch-up");
Check(!reminderState.ShouldShow(reminder, At(31, 10), warsaw), "Saturday skipped");
reminderState = new ReviewReminderState { LastHandled = At(26, 9, 1), SnoozedUntil = At(26, 9, 16) };
Check(!reminderState.ShouldShow(reminder, At(26, 9, 15), warsaw), "Snooze not over yet");
Check(reminderState.ShouldShow(reminder, At(26, 9, 16), warsaw) && reminderState.SnoozedUntil is null, "Snooze returns");
var disabledReminder = reminder.Copy();
disabledReminder.Alarms.ForEach(alarm => alarm.Enabled = false);
reminderState = new ReviewReminderState { LastHandled = At(26, 8), SnoozedUntil = At(26, 8, 30) };
Check(!reminderState.ShouldShow(disabledReminder, At(26, 9), warsaw) && reminderState.SnoozedUntil is null,
    "Disabled reminder never fires");
Check(legacyConfig.ReviewReminder is { Enabled: false } && legacyConfig.ReviewReminder.Alarms.Single().Time == "09:00",
    "Old configs get a disabled reminder");
var nineOff = reminder.Copy();
nineOff.Alarms[0].Enabled = false;
Check(nineOff.Enabled && nineOff.NextOccurrence(At(26, 8), warsaw) == At(26, 14), "Switched-off time is skipped");
reminderState = new ReviewReminderState { LastHandled = At(26, 8) };
Check(!reminderState.ShouldShow(nineOff, At(26, 9), warsaw) && reminderState.ShouldShow(nineOff, At(26, 14), warsaw),
    "Only switched-on times fire");
var reminderFolder = Path.Combine(Path.GetTempPath(), "ghnotif-reminder-" + Guid.NewGuid());
try
{
    var reminderConfigStore = new JsonStore<Config>(Path.Combine(reminderFolder, "config.json"));
    reminderConfigStore.Save(new Config { ReviewReminder = reminder.Copy() });
    var reloadedReminder = reminderConfigStore.Load().ReviewReminder;
    Check(reloadedReminder.SameAs(reminder), "Reminder settings persist");
    var reminderJson = File.ReadAllText(Path.Combine(reminderFolder, "config.json"));
    Check(reminderJson.Contains("\"alarms\"") && !reminderJson.Contains("\"times\""), "Reminder saved in the alarms format");
    var oldReminderPath = Path.Combine(reminderFolder, "old.json");
    File.WriteAllText(oldReminderPath, """{"users":["a"],"reviewReminder":{"enabled":true,"times":["09:47","9:00"],"weekdays":[1]}}""");
    var migratedReminder = new JsonStore<Config>(oldReminderPath).Load();
    migratedReminder.Validate();
    Check(migratedReminder.ReviewReminder.Alarms.Select(alarm => alarm.Time).SequenceEqual(["09:00", "09:47"])
        && migratedReminder.ReviewReminder.Alarms.All(alarm => alarm.Enabled)
        && migratedReminder.ReviewReminder.LegacyTimes is null, "Old enabled+times format migrates to switched-on alarms");
    var reminderStateStore = new JsonStore<LocalState>(Path.Combine(reminderFolder, "state.json"));
    var savedState = new LocalState { ReviewReminder = new ReviewReminderState { LastHandled = At(26, 9) } };
    reminderStateStore.Save(savedState.Clone());
    Check(reminderStateStore.Load().ReviewReminder.LastHandled == At(26, 9), "Reminder state persists and clones");
}
finally { if (Directory.Exists(reminderFolder)) Directory.Delete(reminderFolder, true); }

Check(Localization.Resolve(AppLanguage.System, "pl-PL") == AppLanguage.Polish
    && Localization.Resolve(AppLanguage.System, "en-GB") == AppLanguage.English
    && Localization.Resolve(AppLanguage.English, "pl-PL") == AppLanguage.English, "Language resolution");
var languageFolder = Path.Combine(Path.GetTempPath(), "ghnotif-language-" + Guid.NewGuid());
try
{
    var languagePath = Path.Combine(languageFolder, "config.json");
    Directory.CreateDirectory(languageFolder);
    File.WriteAllText(languagePath, """{"users":["a"]}""");
    var languageStore = new JsonStore<Config>(languagePath);
    Check(languageStore.Load().Language == AppLanguage.Polish, "Configs from before the option stay Polish");
    languageStore.Save(new Config { Language = AppLanguage.English });
    Check(languageStore.Load().Language == AppLanguage.English && File.ReadAllText(languagePath).Contains("\"english\""),
        "Language round trip uses the macOS value names");
    Check(new Config { Language = AppLanguage.English }.Copy().Language == AppLanguage.English, "Language copied");
}
finally { Directory.Delete(languageFolder, true); }
Check(string.Concat(new[] { 1, 2, 5, 12, 22, 25 }.Select(n => PolishPlural(n, "o", "f", "m"))) == "ofmmfm", "Polish plural forms");
Localization.Language = AppLanguage.English;
Check(ActivityKind.Approved.Label() == "approved" && ActivityText.Summarize(["a", "b", "c", "d"]).EndsWith("+1 more"),
    "English texts");
Localization.Language = AppLanguage.Polish;
Check(ActivityKind.Approved.Label() == "zatwierdzenie", "Switching back to Polish");

var quiet = new NotificationPreferences { QuietHoursEnabled = true };
Check(quiet.IsQuiet(new TimeSpan(18, 0, 0)) && quiet.IsQuiet(new TimeSpan(2, 0, 0))
    && !quiet.IsQuiet(new TimeSpan(8, 0, 0)) && !quiet.IsQuiet(new TimeSpan(12, 0, 0)), "Quiet hours span midnight");
quiet.QuietFrom = "12:00";
quiet.QuietTo = "13:30";
Check(quiet.IsQuiet(new TimeSpan(13, 29, 59)) && !quiet.IsQuiet(new TimeSpan(13, 30, 0)) && !quiet.IsQuiet(new TimeSpan(11, 59, 0)),
    "Same-day quiet hours");
quiet.QuietHoursEnabled = false;
Check(!quiet.IsQuiet(new TimeSpan(12, 30, 0)), "Quiet hours off");
var notifyPr = new PullRequest("P", 1, "t", "https://github.com/o/r/pull/1", "o/r", "a", null,
    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
PullRequestAlert[] notifyAlerts =
[
    new(notifyPr, ["• bob - komentarz", "✓ ann - zatwierdzenie"]),
    new(notifyPr, ["• eve - komentarz"])
];
var mutedComments = new NotificationPreferences();
mutedComments.SetMuted(AlertEvent.Comment, true);
var delivered = mutedComments.Filter(notifyAlerts, new TimeSpan(12, 0, 0));
Check(delivered.Count == 1 && delivered[0].Details.SequenceEqual(["✓ ann - zatwierdzenie"])
    && ActivityText.PrimaryEvent(delivered[0].Details) == AlertEvent.Approved, "Muted events are dropped");
Check(new NotificationPreferences { QuietHoursEnabled = true }.Filter(notifyAlerts, new TimeSpan(22, 0, 0)).Count == 0,
    "Nothing is sent during quiet hours");
Check(ActivityText.PrimaryEvent(["↑ Nowe commity", "✗ x - prośba o zmiany"]) == AlertEvent.ChangesRequested
    && ActivityText.EventOf("Bez prefiksu") == AlertEvent.Activity, "Primary event picks the most important line");
Check(mutedComments.MutedEvents.SequenceEqual(["comment"]) && NotificationPreferences.Key(AlertEvent.NewPullRequest) == "newPullRequest",
    "Event names match the macOS config");
var prefsDirectory = Path.Combine(Path.GetTempPath(), "ghnotif-prefs-" + Guid.NewGuid());
try
{
    Directory.CreateDirectory(prefsDirectory);
    var prefsPath = Path.Combine(prefsDirectory, "config.json");
    await File.WriteAllTextAsync(prefsPath, """{"users":[],"notifications":{"mutedEvents":["comment","fromTheFuture"],"quietHoursEnabled":true,"quietFrom":"21:15"}}""");
    var prefsConfig = new JsonStore<Config>(prefsPath).Load();
    prefsConfig.Validate();
    Check(prefsConfig.Notifications.IsMuted(AlertEvent.Comment) && prefsConfig.Notifications.QuietFrom == "21:15"
        && prefsConfig.Notifications.QuietTo == "08:00", "Lenient notification preferences loading");
    await File.WriteAllTextAsync(prefsPath, """{"users":[]}""");
    var oldConfig = new JsonStore<Config>(prefsPath).Load();
    oldConfig.Validate();
    Check(oldConfig.Notifications.MutedEvents.Count == 0 && !oldConfig.Notifications.QuietHoursEnabled,
        "Old configs get default notification preferences");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
        new Config { Notifications = new NotificationPreferences { QuietFrom = "25:00" } }.Validate()), "Invalid quiet hours fail");
}
finally { if (Directory.Exists(prefsDirectory)) Directory.Delete(prefsDirectory, recursive: true); }

Console.WriteLine($"All {checks} Windows core checks passed.");

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request);
}
