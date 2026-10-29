using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GHNotif.Core;

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset>? Assets = null)
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

    public Uri DownloadUri(string name)
    {
        var asset = Assets?.FirstOrDefault(candidate => candidate.Name == name);
        if (asset is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url)
            || url.Scheme != "https" || url.Host != "github.com"
            || !url.AbsolutePath.Contains($"/MichalFidor/GHNotif/releases/download/{TagName}/", StringComparison.Ordinal)
            || !string.Equals(url.Segments.LastOrDefault()?.TrimEnd('/'), name, StringComparison.Ordinal))
            throw new InvalidDataException(L("Nie znaleziono prawidłowego pliku aktualizacji GHNotif.", "A valid GHNotif update asset was not found."));
        return url;
    }

    public string Checksum(string fileName, string manifest)
    {
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1].TrimStart('*') != fileName) continue;
            if (fields[0].Length != 64 || !fields[0].All(Uri.IsHexDigit))
                throw new InvalidDataException(L("Nieprawidłowa suma kontrolna aktualizacji.", "The update checksum is invalid."));
            return fields[0].ToLowerInvariant();
        }
        throw new InvalidDataException(L("Brak sumy kontrolnej dla pliku aktualizacji.", "No checksum found for the update asset."));
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

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
