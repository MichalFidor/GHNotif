using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GHNotif.Core;

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease)
{
    public static Version ParseVersion(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidDataException(L("Brak wersji GHNotif.", "Missing GHNotif version."));
        var text = value.StartsWith('v') ? value[1..] : value;
        if (text.Split('.').Length != 3 || text.Any(c => c != '.' && !char.IsAsciiDigit(c))
            || !Version.TryParse(text, out var version))
            throw new InvalidDataException(L("Nieprawidłowa wersja GHNotif.", "Invalid GHNotif version."));
        return version;
    }

    public bool IsNewer(string installed)
    {
        if (!Uri.TryCreate(HtmlUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/MichalFidor/GHNotif/releases/", StringComparison.Ordinal))
            throw new InvalidDataException(L("Nieprawidłowy adres wydania GHNotif.", "Invalid GHNotif release URL."));
        return !Draft && !Prerelease && ParseVersion(TagName) > ParseVersion(installed);
    }

    public static async Task<GitHubRelease?> LatestAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/MichalFidor/GHNotif/releases/latest");
        request.Headers.UserAgent.ParseAdd("GHNotif");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>()
            ?? throw new InvalidDataException(L("Pusta odpowiedź aktualizacji GHNotif.", "Empty GHNotif update response."));
    }
}
