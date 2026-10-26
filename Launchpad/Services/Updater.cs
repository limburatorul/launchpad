using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Launchpad.Services;

/// <summary>
/// Launchpad updates itself the way the studio's free apps do (vault Ecosistem-v3 §4): GitHub's latest release, the
/// setup checked against its published SHA-256, run through UAC silently; the installer starts Launchpad again. Declining
/// the prompt leaves this copy running, with "Update not installed". It's offered with a notice, once per version a session.
/// </summary>
public static class Updater
{
    public const string Repo = "limburatorul/launchpad";
    private static readonly Product Self = new("launchpad", "Launchpad", "", false, "Launchpad.exe", Kind.Inno, Repo, n => n.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase), null);
    private static Version? _offered;

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>A copy installed by its setup (Program Files, with its uninstaller), not a build run from bin\.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static async Task<Available?> Check()
    {
        var latest = await Feeds.Latest(Self);
        return latest is { File: not null } && latest.Version > Current ? latest : null;
    }

    public static async Task CheckAndOffer(Action quit)
    {
        if (!Installed || await Check() is not { } latest || latest.Version == _offered) return;
        _offered = latest.Version;
        Notice.Post(new NoticeContent([Theme.Icon("launchpad")], $"Launchpad {latest.Version.ToString(3)} is ready",
            string.IsNullOrWhiteSpace(latest.Notes) ? "A new version of Launchpad is out." : latest.Notes, null, null,
            "Update", () => _ = Install(latest, quit), null, "self:" + latest.Version, () => true));
    }

    public static async Task Install(Available latest, Action quit)
    {
        try
        {
            var setup = await Feeds.Fetch(latest.File!, Installer.DownloadFolder, null, CancellationToken.None);
            Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true, Verb = "runas" });
            quit();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Notice.Post(new NoticeContent([Theme.Icon("launchpad")], "Update not installed", "Launchpad keeps running as it is. You can update it from Settings.",
                null, null, "OK", () => { }, null, "self-declined:" + latest.Version, () => true));
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Write($"Self-update failed: {ex.Message}");
        }
    }
}
