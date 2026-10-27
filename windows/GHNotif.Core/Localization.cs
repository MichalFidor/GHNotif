global using static GHNotif.Core.Localization;
using System.Globalization;

namespace GHNotif.Core;

public enum AppLanguage { System, Polish, English }

public static class Localization
{
    private static volatile int language = (int)Resolve(AppLanguage.System, CultureInfo.CurrentUICulture.Name);

    /// The resolved language used by <see cref="L"/>; never <see cref="AppLanguage.System"/>.
    public static AppLanguage Language
    {
        get => (AppLanguage)language;
        set => language = (int)Resolve(value, CultureInfo.CurrentUICulture.Name);
    }

    /// <see cref="AppLanguage.System"/> resolves to Polish for a Polish UI culture, otherwise English.
    public static AppLanguage Resolve(AppLanguage choice, string uiCulture) => choice != AppLanguage.System ? choice
        : uiCulture.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Polish : AppLanguage.English;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == AppLanguage.Polish ? "pl-PL" : "en-US");

    /// Picks the Polish or English text for the current language.
    public static string L(string polish, string english) => Language == AppLanguage.Polish ? polish : english;

    /// Polish plural: 1 → one, 2–4 (not 12–14) → few, otherwise many.
    public static string PolishPlural(int count, string one, string few, string many) =>
        count == 1 ? one : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? few : many;
}
