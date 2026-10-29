import Foundation

/// Which notification events are delivered and when they arrive silently.
public struct NotificationPreferences: Codable, Equatable, Sendable {
    public var mutedEvents: Set<AlertEvent>
    /// No notifications are sent during quiet hours; the PR list still updates.
    public var quietHoursEnabled: Bool
    public var quietFrom: ReminderTime
    public var quietTo: ReminderTime

    private enum CodingKeys: String, CodingKey { case mutedEvents, quietHoursEnabled, quietFrom, quietTo }

    public init(mutedEvents: Set<AlertEvent> = [], quietHoursEnabled: Bool = false,
                quietFrom: ReminderTime = ReminderTime(hour: 18, minute: 0)!,
                quietTo: ReminderTime = ReminderTime(hour: 8, minute: 0)!) {
        self.mutedEvents = mutedEvents
        self.quietHoursEnabled = quietHoursEnabled
        self.quietFrom = quietFrom
        self.quietTo = quietTo
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let fallback = NotificationPreferences()
        // Unknown event names (e.g. from a newer version) are skipped instead of failing the whole config.
        mutedEvents = Set((try container.decodeIfPresent([String].self, forKey: .mutedEvents) ?? [])
            .compactMap(AlertEvent.init(rawValue:)))
        quietHoursEnabled = try container.decodeIfPresent(Bool.self, forKey: .quietHoursEnabled) ?? fallback.quietHoursEnabled
        quietFrom = try container.decodeIfPresent(ReminderTime.self, forKey: .quietFrom) ?? fallback.quietFrom
        quietTo = try container.decodeIfPresent(ReminderTime.self, forKey: .quietTo) ?? fallback.quietTo
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(AlertEvent.allCases.filter(mutedEvents.contains).map(\.rawValue), forKey: .mutedEvents)
        try container.encode(quietHoursEnabled, forKey: .quietHoursEnabled)
        try container.encode(quietFrom, forKey: .quietFrom)
        try container.encode(quietTo, forKey: .quietTo)
    }

    /// Quiet hours may span midnight (e.g. 18:00-08:00); equal start and end means no quiet time.
    public func isQuiet(at date: Date, calendar: Calendar = .current) -> Bool {
        guard quietHoursEnabled else { return false }
        let parts = calendar.dateComponents([.hour, .minute], from: date)
        let now = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
        let from = quietFrom.hour * 60 + quietFrom.minute
        let to = quietTo.hour * 60 + quietTo.minute
        if from == to { return false }
        return from < to ? (from..<to).contains(now) : now >= from || now < to
    }

    /// Drops muted lines and everything during quiet hours; an alert with nothing left is not delivered.
    public func filter(_ alerts: [PullRequestAlert], at date: Date = Date(), calendar: Calendar = .current) -> [PullRequestAlert] {
        if isQuiet(at: date, calendar: calendar) { return [] }
        return alerts.compactMap { alert in
            let kept = alert.details.filter { !mutedEvents.contains(AlertEvent.of($0)) }
            return kept.isEmpty ? nil : PullRequestAlert(pullRequest: alert.pullRequest, details: kept)
        }
    }
}
