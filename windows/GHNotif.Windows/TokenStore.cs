using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GHNotif.Windows;

internal sealed class TokenStore(string directory)
{
    private string Path => System.IO.Path.Combine(directory, "token.dat");
    public string Source { get; private set; } = "brak";

    public async Task<string> ResolveAsync()
    {
        if (File.Exists(Path))
        {
            var data = ProtectedData.Unprotect(await File.ReadAllBytesAsync(Path), null,
                DataProtectionScope.CurrentUser);
            Source = "PAT (Windows DPAPI)";
            return Encoding.UTF8.GetString(data);
        }
        var start = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "auth", "token", "--hostname", "github.com" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
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
            throw new InvalidOperationException(L("Brak tokenu. Wykonaj gh auth login albo dodaj PAT w ustawieniach.", "No token. Run gh auth login or add a PAT in settings."));
        Source = "gh CLI";
        return token;
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
