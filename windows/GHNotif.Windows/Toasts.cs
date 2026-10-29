using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GHNotif.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GHNotif.Windows;

/// Windows 10/11 toast notifications with a GitHub-style badge; the tray balloon is the fallback.
internal static class Toasts
{
    private const string UrlArgument = "url";
    private const string ActionArgument = "action";

    /// Clicking a toast opens the PR, or the panel when the toast covers several PRs.
    internal static void Initialize(Action<string?, string?> activated)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
            {
                var arguments = ToastArguments.Parse(args.Argument);
                activated(arguments.TryGetValue(UrlArgument, out var url) ? url : null,
                    arguments.TryGetValue(ActionArgument, out var action) ? action : null);
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning(L("Nie udało się podłączyć obsługi powiadomień Windows: ", "Could not register Windows notification activation: ") + ex.Message);
        }
    }

    internal static bool TryShowUpdate(string version)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(L("Dostępna aktualizacja GHNotif", "GHNotif update available"))
                .AddText(L($"Wersja {version} jest gotowa do instalacji.", $"Version {version} is ready to install."))
                .AddArgument(ActionArgument, "update")
                .AddButton(new ToastButton().SetContent(L("Zaktualizuj teraz", "Update now")).AddArgument(ActionArgument, "update"))
                .AddButton(new ToastButton().SetContent(L("Później", "Later")).AddArgument(ActionArgument, "later"))
                .Show(toast => { toast.Tag = "update"; toast.Group = "ghnotif"; });
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            return false;
        }
    }

    internal static bool TryShow(string title, string? subtitle, string body, AlertEvent kind, string? url, string? tag)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrEmpty(subtitle)) builder.AddText(subtitle);
            builder.AddText(body);
            if (url is not null) builder.AddArgument(UrlArgument, url);
            var badge = BadgePath(kind);
            if (File.Exists(badge)) builder.AddAppLogoOverride(new Uri(badge), ToastGenericAppLogoCrop.Circle);
            builder.Show(toast =>
            {
                if (tag is not null) toast.Tag = tag.Length <= 64 ? tag : tag[..64];
                toast.Group = "ghnotif";
            });
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            return false;
        }
    }

    internal static string BadgePath(AlertEvent kind) =>
        Path.Combine(AppContext.BaseDirectory, "Badges", $"badge-{NotificationPreferences.Key(kind)}.png");
}
