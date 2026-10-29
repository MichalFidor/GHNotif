using System.Text.Json;

namespace GHNotif.Core;

/// Which notification events are delivered and when nothing is sent; matches the macOS config format.
public sealed class NotificationPreferences
{
    /// camelCase event names; unknown names (e.g. from a newer version) are ignored.
    public List<string> MutedEvents { get; set; } = [];
    public bool QuietHoursEnabled { get; set; }
    public string QuietFrom { get; set; } = "18:00";
    public string QuietTo { get; set; } = "08:00";

    public NotificationPreferences Copy() => new()
    {
        MutedEvents = MutedEvents.ToList(), QuietHoursEnabled = QuietHoursEnabled, QuietFrom = QuietFrom, QuietTo = QuietTo
    };

    public static string Key(AlertEvent kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public bool IsMuted(AlertEvent kind) => MutedEvents.Contains(Key(kind), StringComparer.OrdinalIgnoreCase);

    public void SetMuted(AlertEvent kind, bool muted)
    {
        MutedEvents.RemoveAll(name => string.Equals(name, Key(kind), StringComparison.OrdinalIgnoreCase));
        if (muted) MutedEvents.Add(Key(kind));
    }

    /// Quiet hours may span midnight (e.g. 18:00-08:00); equal start and end means no quiet time.
    public bool IsQuiet(TimeSpan localTime)
    {
        if (!QuietHoursEnabled || !ReviewReminderSettings.TryParseTime(QuietFrom, out var from)
            || !ReviewReminderSettings.TryParseTime(QuietTo, out var to) || from == to) return false;
        var now = new TimeSpan(localTime.Hours, localTime.Minutes, 0);
        return from < to ? now >= from && now < to : now >= from || now < to;
    }

    /// Drops muted lines and everything during quiet hours; an alert with nothing left is not delivered.
    public List<PullRequestAlert> Filter(IEnumerable<PullRequestAlert> alerts, TimeSpan localTime)
    {
        if (IsQuiet(localTime)) return [];
        return alerts
            .Select(alert => alert with { Details = alert.Details.Where(line => !IsMuted(ActivityText.EventOf(line))).ToList() })
            .Where(alert => alert.Details.Count > 0)
            .ToList();
    }

    public void Normalize()
    {
        MutedEvents = (MutedEvents ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!ReviewReminderSettings.TryParseTime(QuietFrom ?? "", out _) || !ReviewReminderSettings.TryParseTime(QuietTo ?? "", out _))
            throw new InvalidDataException(L("Nieprawidłowe ciche godziny. Użyj formatu GG:MM, np. 18:00.", "Invalid quiet hours. Use the HH:MM format, e.g. 18:00."));
    }
}
