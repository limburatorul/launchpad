using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Launchpad.Services;

/// <summary>
/// Days left in a paid app's trial, read from the studio's shared trial clock (vault Trial-brief-2026-10-17): start|seen|hmac
/// in HKCU\Software\ProtagonistLabs\Trial\&lt;app&gt; (value T), a copy in %LOCALAPPDATA%\ProtagonistLabs\trial\&lt;app&gt;.dat.
/// Launchpad only reads it, never writes: a write could end or launder somebody's trial. A record that is missing or fails
/// its signature gives null, and the card says just "Trial".
/// </summary>
public static class TrialClock
{
    public const int Days = 14;

    internal static string RegistryRoot = @"Software\ProtagonistLabs\Trial";
    internal static string FileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtagonistLabs", "trial");
    internal static Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal static Func<string> Machine = () =>
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string ?? "";
    };

    public static string Sign(string app, long start, long seen, string machine) =>
        Convert.ToHexString(HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes("pl-trial-v1|" + machine + "|" + app)),
                                                Encoding.UTF8.GetBytes($"{start}|{seen}"))).ToLowerInvariant();

    public static (long Start, long Seen)? Parse(string app, string? text, string machine)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('|');
        return parts.Length == 3 && long.TryParse(parts[0], out long start) && long.TryParse(parts[1], out long seen)
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()), Encoding.ASCII.GetBytes(Sign(app, start, seen, machine)))
            ? (start, seen) : null;
    }

    /// <summary>The trial's start and days left (the app's own rule: 14 minus whole days from start to the later of now and seen), or null.</summary>
    public static (long Start, int Left)? Read(string app)
    {
        string machine = Machine();
        string? registry;
        using (var key = Registry.CurrentUser.OpenSubKey($@"{RegistryRoot}\{app}")) registry = key?.GetValue("T") as string;
        string? file = null;
        try { var path = Path.Combine(FileRoot, app + ".dat"); if (File.Exists(path)) file = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // the app keeps both copies equal; if one is tampered with, the app itself ends the trial, so a bad copy means "don't know"
        if (!string.IsNullOrWhiteSpace(registry) && Parse(app, registry, machine) is null) return null;
        if (!string.IsNullOrWhiteSpace(file) && Parse(app, file, machine) is null) return null;
        var record = Parse(app, registry, machine) ?? Parse(app, file, machine);
        if (record is not { } r) return null;
        long used = Math.Max(0, Math.Max(Now(), r.Seen) - r.Start) / 86400;
        return (r.Start, (int)Math.Clamp(Days - used, 0, Days));
    }

    public static string Label(int? left) => left switch
    {
        null => "Trial",
        0 => "Trial ended",
        1 => "Trial · last day",
        _ => $"Trial · {left} days",
    };
}
