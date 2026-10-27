import Foundation

/// Time of day stored as `"HH:mm"` so the config file stays readable and matches the Windows format.
public struct ReminderTime: Codable, Hashable, Comparable, Sendable, CustomStringConvertible {
    public let hour: Int
    public let minute: Int

    public init?(hour: Int, minute: Int) {
        guard (0...23).contains(hour), (0...59).contains(minute) else { return nil }
        self.hour = hour
        self.minute = minute
    }

    public init?(parsing text: String) {
        let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ":")
        guard parts.count == 2, let hour = Int(parts[0]), let minute = Int(parts[1]) else { return nil }
        self.init(hour: hour, minute: minute)
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let text = try container.decode(String.self)
        guard let time = ReminderTime(parsing: text) else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Nieprawidłowa godzina: \(text)")
        }
        self = time
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(description)
    }

    public var description: String { String(format: "%02d:%02d", hour, minute) }

    public static func < (lhs: ReminderTime, rhs: ReminderTime) -> Bool {
        (lhs.hour, lhs.minute) < (rhs.hour, rhs.minute)
    }
}

/// Weekdays use ISO numbering: 1 = Monday … 7 = Sunday (shared with the Windows app).
public struct ReviewReminderSettings: Codable, Equatable, Sendable {
    public var enabled: Bool
    public var times: [ReminderTime]
    public var weekdays: Set<Int>

    private enum CodingKeys: String, CodingKey { case enabled, times, weekdays }
    public init(enabled: Bool = false, times: [ReminderTime] = [ReminderTime(hour: 9, minute: 0)!],
                weekdays: Set<Int> = [1, 2, 3, 4, 5]) {
        self.enabled = enabled
        self.times = times
        self.weekdays = weekdays
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let fallback = ReviewReminderSettings()
        enabled = try container.decodeIfPresent(Bool.self, forKey: .enabled) ?? fallback.enabled
        times = try container.decodeIfPresent([ReminderTime].self, forKey: .times) ?? fallback.times
        weekdays = try container.decodeIfPresent(Set<Int>.self, forKey: .weekdays)?.filter { (1...7).contains($0) }
            ?? fallback.weekdays
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(enabled, forKey: .enabled)
        try container.encode(times, forKey: .times)
        try container.encode(weekdays.sorted(), forKey: .weekdays)
    }

    public static func isoWeekday(of date: Date, calendar: Calendar) -> Int {
        (calendar.component(.weekday, from: date) + 5) % 7 + 1
    }

    /// Latest scheduled reminder on the same calendar day that is not later than `now`.
    public func latestOccurrence(atOrBefore now: Date, calendar: Calendar = .current) -> Date? {
        guard weekdays.contains(Self.isoWeekday(of: now, calendar: calendar)) else { return nil }
        return times.compactMap { calendar.date(bySettingHour: $0.hour, minute: $0.minute, second: 0, of: now) }
            .filter { $0 <= now }
            .max()
    }

    public func nextOccurrence(after now: Date, calendar: Calendar = .current) -> Date? {
        guard enabled, !times.isEmpty, !weekdays.isEmpty else { return nil }
        let startOfToday = calendar.startOfDay(for: now)
        for offset in 0...7 {
            guard let day = calendar.date(byAdding: .day, value: offset, to: startOfToday),
                  weekdays.contains(Self.isoWeekday(of: day, calendar: calendar)) else { continue }
            let next = times.sorted()
                .compactMap { calendar.date(bySettingHour: $0.hour, minute: $0.minute, second: 0, of: day) }
                .first { $0 > now }
            if let next { return next }
        }
        return nil
    }
}

public struct ReviewReminderState: Codable, Equatable, Sendable {
    /// Reminders scheduled at or before this moment were already shown (or skipped).
    public var lastHandled: Date?
    public var snoozedUntil: Date?

    public init(lastHandled: Date? = nil, snoozedUntil: Date? = nil) {
        self.lastHandled = lastHandled
        self.snoozedUntil = snoozedUntil
    }

    /// Returns true when the reminder window should be shown now and records it as handled.
    /// A reminder missed while the computer slept or the app was closed is shown later the same day;
    /// several missed reminders collapse into one window.
    public mutating func shouldShow(settings: ReviewReminderSettings, now: Date, calendar: Calendar = .current) -> Bool {
        guard settings.enabled else {
            snoozedUntil = nil
            return false
        }
        guard let lastHandled else {
            // First run: don't replay reminders scheduled before the app started tracking them.
            self.lastHandled = now
            return false
        }
        if let snoozedUntil, snoozedUntil <= now {
            self.snoozedUntil = nil
            self.lastHandled = now
            return true
        }
        if let due = settings.latestOccurrence(atOrBefore: now, calendar: calendar), due > lastHandled {
            self.lastHandled = now
            snoozedUntil = nil
            return true
        }
        return false
    }
}
