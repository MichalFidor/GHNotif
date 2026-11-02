using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GHNotif.Core;

namespace GHNotif.Windows;

internal sealed class TokenStore(string directory)
{
    // Each PAT is bound to the host it was saved for, so changing the host never sends it to another server.
    // github.com keeps the original file name, so tokens saved by older versions still work.
    private string PathFor(string host) => System.IO.Path.Combine(directory,
        host == GitHubHost.DotCom ? "token.dat" : $"token-{host}.dat");
    private string Path => PathFor(Host);
    public string Source { get; private set; } = "brak";

    private string host = GitHubHost.DotCom;
    public string Host { get => host; set => host = GitHubHost.Normalize(value); }

    /// Returns the token paired with the host it was resolved for. The host is fixed before the first await,
    /// so a host change while gh runs can't attach this token to the new server.
    public async Task<GitHubCredential> ResolveAsync()
    {
        var target = new GitHubHost(Host);
        var path = PathFor(target.Name);
        if (File.Exists(path))
        {
            var data = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path), null,
                DataProtectionScope.CurrentUser);
            Source = "PAT (Windows DPAPI)";
            return new(target, Encoding.UTF8.GetString(data));
        }
        var gh = FindGitHubCli()
            ?? throw new InvalidOperationException(L("Nie znaleziono GitHub CLI (gh). Zainstaluj go lub dodaj PAT w Ustawieniach → Token.", "GitHub CLI (gh) was not found. Install it or add a PAT in Settings → Token."));
        using var process = Process.Start(GitHubCliStartInfo(gh, target))
            ?? throw new InvalidOperationException(L("Nie udało się uruchomić gh. Zainstaluj GitHub CLI lub dodaj PAT.", "Could not run gh. Install GitHub CLI or add a PAT."));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(L("gh nie odpowiedział w ciągu 15 sekund.", "gh did not respond within 15 seconds."));
        }
        var token = (await output).Trim();
        await error;
        if (process.ExitCode != 0 || token.Length == 0)
            throw new InvalidOperationException(L("Brak tokenu. Wykonaj gh auth login albo dodaj PAT - szczegóły w Ustawieniach → Token.", "No token. Run gh auth login or add a PAT - see Settings → Token."));
        Source = "gh CLI";
        return new(target, token);
    }

    /// `gh auth token --hostname HOST` without the token environment variables gh would hand to any host.
    internal static ProcessStartInfo GitHubCliStartInfo(string gh, GitHubHost target)
    {
        var start = new ProcessStartInfo(gh)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "auth", "token", "--hostname", target.Name })
            start.ArgumentList.Add(argument);
        foreach (var name in target.GitHubCliVariablesToClear)
            start.Environment.Remove(name);
        return start;
    }

    /// Resolves gh.exe from known install folders and absolute PATH entries only, so a gh.exe dropped into the
    /// working directory or a relative PATH entry can't receive the token request.
    internal static string? FindGitHubCli()
    {
        var known = new[]
        {
            (Environment.SpecialFolder.ProgramFiles, @"GitHub CLI\gh.exe"),
            (Environment.SpecialFolder.ProgramFilesX86, @"GitHub CLI\gh.exe"),
            (Environment.SpecialFolder.LocalApplicationData, @"Programs\GitHub CLI\gh.exe"),
            (Environment.SpecialFolder.UserProfile, @"scoop\shims\gh.exe")
        }.Select(entry => (Root: Environment.GetFolderPath(entry.Item1), entry.Item2))
            .Where(entry => entry.Root.Length > 0)
            .Select(entry => System.IO.Path.Combine(entry.Root, entry.Item2));
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(System.IO.Path.IsPathFullyQualified)
            .Select(folder => System.IO.Path.Combine(folder, "gh.exe"));
        return known.Concat(fromPath).FirstOrDefault(File.Exists);
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(directory);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path, data);
        Source = "PAT (Windows DPAPI)";
    }

    public void Delete()
    {
        if (File.Exists(Path)) File.Delete(Path);
        Source = "gh CLI";
    }
}
