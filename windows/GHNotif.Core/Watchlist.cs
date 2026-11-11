using System.Text.Json;

namespace GHNotif.Core;

public static class Watchlist
{
    public static IReadOnlyList<string> Import(string text)
    {
        if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('['))
            return QueryBuilder.ValidatedLogins(text.Split(['\r', '\n', ',', ' '],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var users = root;
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("users", out users))
            throw new InvalidDataException(L("Brak listy osób w pliku.", "The file has no people list."));
        if (users.ValueKind != JsonValueKind.Array || users.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException(L("Plik listy osób jest nieprawidłowy.", "The people list file is invalid."));
        return QueryBuilder.ValidatedLogins(users.EnumerateArray().Select(item => item.GetString()!));
    }
}
