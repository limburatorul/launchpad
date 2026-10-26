using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Launchpad.Services;

public sealed record Installed(string Exe, Version? Version, bool Trial);

/// <summary>
/// What is installed (vault Launchpad.md §2): the app's card first, then its Uninstall entry (HKLM both views, then HKCU),
/// then the folder Launchpad itself installs portable apps to. A card can be a version behind after a silent update the
/// app hasn't run since, so the higher of the card's and the Uninstall entry's version wins.
/// </summary>
public static class Detect
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static Installed? Find(Product app)
    {
        var card = Ecosystem.Card(app.Id);
        var entry = FromRegistry(app);
        if (card is { } c)
            return new Installed(c.Exe, Max(Parse(c.Version), entry?.Version), c.Edition == "trial");
        if (entry is not null) return entry;
        var own = Path.Combine(app.Folder, app.Exe);
        if (File.Exists(own)) return new Installed(own, State.InstalledVersion(app.Id) ?? FileVersion(own), false);
        return null;
    }

    private static Installed? FromRegistry(Product app)
    {
        var name = new Regex($@"^{Regex.Escape(app.Name)}( \d+(\.\d+)*)?$", RegexOptions.IgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(Uninstall);
                if (root is null) continue;
                var keys = app.UninstallKey is { } k ? [k] : root.GetSubKeyNames().Where(n => name.IsMatch(root.OpenSubKey(n)?.GetValue("DisplayName") as string ?? "")).ToArray();
                foreach (var key in keys)
                {
                    using var sub = root.OpenSubKey(key);
                    if (sub is null) continue;
                    // HKCU can hold an old per-user copy whose files are gone (File Labs 1.0.33 on the user's PC): skipped by the exe check.
                    var exe = ExeOf(sub, app);
                    if (exe is null) continue;
                    return new Installed(exe, Parse(sub.GetValue("DisplayVersion") as string), false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    private static string? ExeOf(RegistryKey sub, Product app)
    {
        if (sub.GetValue("DisplayIcon") is string icon)
        {
            var path = icon.Split(',')[0].Trim('"', ' ');
            if (Paths.IsLocal(path) && path.EndsWith(app.Exe, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
        }
        if (sub.GetValue("InstallLocation") is string dir && Paths.IsLocal(dir.Trim('"')))
        {
            var path = Path.Combine(dir.Trim('"'), app.Exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>"1.0.53", "v4.5.5", "1.14.1-beta" → a three-part version; anything else → null.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success && int.TryParse(m.Groups[1].Value, out int a) && int.TryParse(m.Groups[2].Value, out int b)
            ? new Version(a, b, m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int c) ? c : 0) : null;
    }

    private static Version? Max(Version? a, Version? b) => a is null ? b : b is null ? a : a >= b ? a : b;

    private static Version? FileVersion(string exe)
    {
        try { return Parse(FileVersionInfo.GetVersionInfo(exe).ProductVersion); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
