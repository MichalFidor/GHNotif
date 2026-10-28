using System.Globalization;
using System.Text.Json.Serialization;

namespace GHNotif.Core;

/// One reminder time with its own on/off switch, like an alarm on iOS.
public sealed class ReminderAlarm
{
    public string Time { get; set; } = "09:00";
    public bool Enabled { get; set; } = true;

    public ReminderAlarm Copy() => new() { Time = Time, Enabled = Enabled };
}

/// Times are "HH:mm" and weekdays use ISO numbering (1 = Monday … 7 = Sunday), matching the macOS app.
public sealed class ReviewReminderSettings
{
    public List<ReminderAlarm> Alarms { get; set; } = [new() { Time = "09:00", Enabled = false }];
    public List<int> Weekdays { get; set; } = [1, 2, 3, 4, 5];

    // Pre-0.5.2 format: one switch for all times. Migrated by Normalize and never written back.
    [JsonPropertyName("enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyEnabled { get; set; }
    [JsonPropertyName("times"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyTimes { get; set; }

    /// Reminders are active when at least one alarm is switched on.
    [JsonIgnore] public bool Enabled => Alarms.Any(alarm => alarm.Enabled);

    public ReviewReminderSettings Copy() => new()
    {
        Alarms = Alarms.Select(alarm => alarm.Copy()).ToList(), Weekdays = Weekdays.ToList(),
        LegacyEnabled = LegacyEnabled, LegacyTimes = LegacyTimes?.ToList()
    };

    public bool SameAs(ReviewReminderSettings other) =>
        Alarms.Count == other.Alarms.Count
        && Alarms.Zip(other.Alarms).All(pair => pair.First.Time == pair.Second.Time && pair.First.Enabled == pair.Second.Enabled)
        && Weekdays.SequenceEqual(other.Weekdays);

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || hour is < 0 or > 23 || minute is < 0 or > 59) return false;
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    public static string FormatTime(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    public void Normalize()
    {
        if (LegacyTimes is not null)
        {
            Alarms = LegacyTimes.Select(time => new ReminderAlarm { Time = time, Enabled = LegacyEnabled ?? false }).ToList();
            LegacyTimes = null;
        }
        LegacyEnabled = null;
        if (Alarms is null || Weekdays is null || Alarms.Any(alarm => alarm is null))
            throw new InvalidDataException(L("Nieprawidłowe ustawienia przypomnienia.", "Invalid reminder settings."));
        var parsed = new List<(TimeSpan Time, bool Enabled)>();
        foreach (var alarm in Alarms)
        {
            if (!TryParseTime(alarm.Time ?? "", out var time))
                throw new InvalidDataException(L($"Nieprawidłowa godzina przypomnienia: {alarm.Time}. Użyj formatu GG:MM.", $"Invalid reminder time: {alarm.Time}. Use the HH:MM format."));
            parsed.Add((time, alarm.Enabled));
        }
        // A time listed twice stays on if any of its copies was on.
        Alarms = parsed.GroupBy(alarm => alarm.Time).OrderBy(group => group.Key)
            .Select(group => new ReminderAlarm { Time = FormatTime(group.Key), Enabled = group.Any(alarm => alarm.Enabled) })
            .ToList();
        Weekdays = Weekdays.Where(day => day is >= 1 and <= 7).Distinct().Order().ToList();
    }

    public static int IsoWeekday(DateTime date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    private IEnumerable<TimeSpan> ParsedTimes() =>
        Alarms.Where(alarm => alarm.Enabled)
            .Select(alarm => TryParseTime(alarm.Time, out var time) ? time : (TimeSpan?)null).OfType<TimeSpan>();

    private static DateTimeOffset At(DateTime day, TimeSpan time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.Date + time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// Latest scheduled reminder on the same local day that is not later than <paramref name="now"/>.
    public DateTimeOffset? LatestOccurrence(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (!Weekdays.Contains(IsoWeekday(local.DateTime))) return null;
        var due = ParsedTimes().Select(time => At(local.DateTime, time, zone)).Where(at => at <= now).ToList();
        return due.Count == 0 ? null : due.Max();
    }

    public DateTimeOffset? NextOccurrence(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!Enabled || Weekdays.Count == 0) return null;
        var times = ParsedTimes().Order().ToList();
        if (times.Count == 0) return null;
        var today = TimeZoneInfo.ConvertTime(now, zone).DateTime.Date;
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = today.AddDays(offset);
            if (!Weekdays.Contains(IsoWeekday(day))) continue;
            foreach (var time in times)
            {
                var at = At(day, time, zone);
                if (at > now) return at;
            }
        }
        return null;
    }
}

public sealed class ReviewReminderState
{
    /// Reminders scheduled at or before this moment were already shown (or skipped).
    public DateTimeOffset? LastHandled { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }

    public ReviewReminderState Copy() => new() { LastHandled = LastHandled, SnoozedUntil = SnoozedUntil };

    /// Returns true when the reminder window should be shown and records it as handled. A reminder missed during
    /// sleep or while the app was closed is shown later the same day; several missed ones collapse into one window.
    public bool ShouldShow(ReviewReminderSettings settings, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!settings.Enabled)
        {
            SnoozedUntil = null;
            return false;
        }
        if (LastHandled is not { } lastHandled)
        {
            // First run: don't replay reminders scheduled before the app started tracking them.
            LastHandled = now;
            return false;
        }
        if (SnoozedUntil is { } snoozed && snoozed <= now)
        {
            SnoozedUntil = null;
            LastHandled = now;
            return true;
        }
        if (settings.LatestOccurrence(now, zone) is { } due && due > lastHandled)
        {
            LastHandled = now;
            SnoozedUntil = null;
            return true;
        }
        return false;
    }
}
