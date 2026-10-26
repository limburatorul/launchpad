using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Launchpad.Services;

/// <summary>
/// What Launchpad remembers, in %APPDATA%\ProtagonistLabs\Launchpad\state.json: notifications already shown, versions
/// of the portable apps it installed, the feeds' last good answers with their ETags, and the What's new it last showed.
/// A broken file is a fresh start, never an error.
/// </summary>
public static class State
{
    private static readonly object Gate = new();
    private static JsonObject? _state;
    private static string _file = Path.Combine(Ecosystem.Data, "state.json");

    /// <summary>The file; set by the self-test to a scratch one, which starts it afresh.</summary>
    public static string File
    {
        get => _file;
        set { lock (Gate) { _file = value; _state = null; } }
    }

    private static JsonObject Root
    {
        get
        {
            lock (Gate) return _state ??= Ecosystem.Read(File) ?? new JsonObject();
        }
    }

    private static JsonObject Section(string name)
    {
        lock (Gate)
        {
            if (Root[name] is JsonObject o) return o;
            var created = new JsonObject();
            Root[name] = created;
            return created;
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                var copy = (JsonObject)Root.DeepClone();
                copy.Remove("schema"); copy.Remove("updated");
                Ecosystem.WriteJson(File, copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write($"Couldn't save state: {ex.Message}"); }
        }
    }

    public static bool Seen(string key)
    {
        lock (Gate) return Section("seen").ContainsKey(key);
    }

    /// <summary>True the first time a key is marked; the notifications use it to come once.</summary>
    public static bool MarkOnce(string key)
    {
        lock (Gate)
        {
            var seen = Section("seen");
            if (seen.ContainsKey(key)) return false;
            seen[key] = DateTime.UtcNow.ToString("o");
            Save();
            return true;
        }
    }

    public static Version? InstalledVersion(string id)
    {
        lock (Gate) return Section("installed")[id] is JsonValue v && v.TryGetValue(out string? s) ? Detect.Parse(s) : null;
    }

    public static void SetInstalledVersion(string id, Version version)
    {
        lock (Gate) { Section("installed")[id] = version.ToString(3); Save(); }
    }

    /// <summary>A feed's last good answer and its ETag, for If-None-Match.</summary>
    public static (string? ETag, string? Body) Feed(string url)
    {
        lock (Gate)
            return Section("feeds")[url] is JsonObject f ? (Ecosystem.Text(f, "etag"), Ecosystem.Text(f, "body")) : (null, null);
    }

    public static void SetFeed(string url, string? etag, string body)
    {
        lock (Gate) Section("feeds")[url] = new JsonObject { ["etag"] = etag, ["body"] = body };
    }

    public static DateTime? LastCheck
    {
        get { lock (Gate) return Root["lastCheck"] is JsonValue v && v.TryGetValue(out DateTime d) ? d : null; }
        set { lock (Gate) Root["lastCheck"] = value; }
    }

    public static string? LastSeenVersion
    {
        get { lock (Gate) return Root["whatsNewSeen"] is JsonValue v && v.TryGetValue(out string? s) ? s : null; }
        set { lock (Gate) { Root["whatsNewSeen"] = value; Save(); } }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Protagonist Labs Launchpad";

    /// <summary>Starts hidden in the tray at logon: a value under HKCU\...\Run, per user even though the program is per-machine.</summary>
    public static bool StartWithWindows
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunName) is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}
