import Foundation

public enum AppLanguage: String, Codable, CaseIterable, Sendable {
    case system
    case polish
    case english

    /// `.system` resolves to Polish when it is the first preferred language, otherwise English.
    public func resolved(preferredLanguages: [String] = Locale.preferredLanguages) -> AppLanguage {
        guard self == .system else { return self }
        return preferredLanguages.first?.lowercased().hasPrefix("pl") == true ? .polish : .english
    }

    public var locale: Locale {
        Locale(identifier: resolved() == .polish ? "pl_PL" : "en_US")
    }
}

public enum Localization {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var storedLanguage: AppLanguage = AppLanguage.system.resolved()

    /// The resolved language used by `L(_:_:)`; never `.system`.
    public static var language: AppLanguage {
        get { lock.withLock { storedLanguage } }
        set { lock.withLock { storedLanguage = newValue.resolved() } }
    }

    /// Polish plural: 1 → `one`, 2–4 (not 12–14) → `few`, otherwise `many`.
    public static func polishPlural(_ count: Int, one: String, few: String, many: String) -> String {
        if count == 1 { return one }
        let tens = count % 100, units = count % 10
        return (2...4).contains(units) && !(12...14).contains(tens) ? few : many
    }
}

/// Picks the Polish or English text for the current language.
public func L(_ polish: String, _ english: String) -> String {
    Localization.language == .polish ? polish : english
}
