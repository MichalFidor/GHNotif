using System;
using System.IO;
using GHNotif.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GHNotif.Windows;

/// Windows 10/11 toast notifications with a GitHub-style badge; the tray balloon is the fallback.
internal static class Toasts
{
    private const string UrlArgument = "url";

    /// Clicking a toast opens the PR, or the panel when the toast covers several PRs.
    internal static void Initialize(Action<string?> activated)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
            {
                var arguments = ToastArguments.Parse(args.Argument);
                activated(arguments.TryGetValue(UrlArgument, out var url) ? url : null);
            };
        }
        catch (Exception) { }
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
                // Same tag replaces the previous toast about this PR.
                if (tag is not null) toast.Tag = tag.Length <= 64 ? tag : tag[..64];
                toast.Group = "ghnotif";
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string BadgePath(AlertEvent kind) =>
        Path.Combine(AppContext.BaseDirectory, "Badges", $"badge-{NotificationPreferences.Key(kind)}.png");
}
