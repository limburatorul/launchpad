using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launchpad.Services;

/// <summary>
/// The folder the Protagonist Labs apps share on this PC (%APPDATA%\ProtagonistLabs, vault Ecosistem.md): Launchpad
/// writes its card and reads the others'. Anything read from here may be missing or broken — that's just "not there".
/// </summary>
public static class Ecosystem
{
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs");
    public static string Data => Path.Combine(Folder, "Launchpad");
    public const string Page = "https://protagonistlabs.app/launchpad/";

    public static void WriteCard(string exe, Version version) => WriteJson(Path.Combine(Folder, "apps", "launchpad.json"), new JsonObject
    {
        ["id"] = "launchpad", ["name"] = "Launchpad", ["version"] = version.ToString(3), ["exe"] = exe,
        ["edition"] = "free", ["paid"] = false, ["page"] = Page, ["commands"] = new JsonObject(),
    });

    /// <summary>An app's card when it is valid and its exe is a local file that still exists, else null.</summary>
    public static (string Exe, string? Version, string? Edition)? Card(string id)
    {
        var card = Read(Path.Combine(Folder, "apps", id + ".json"));
        if (card is null || Text(card, "exe") is not { } exe || !Paths.IsLocal(exe) || !File.Exists(exe)) return null;
        return (exe, Text(card, "version"), Text(card, "edition"));
    }

    public static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>A shared file as JSON, or null if it isn't a schema-1 object (or is unreasonably big).</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length < 1 << 20 && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o
                   && o["schema"] is JsonValue v && v.TryGetValue(out int schema) && schema == 1 ? o : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static void WriteJson(string path, JsonObject body)
    {
        var file = new JsonObject { ["schema"] = 1, ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        foreach (var (key, value) in body.ToList()) { body.Remove(key); file[key] = value; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); // UTF-8, no BOM
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public static class Paths
{
    /// <summary>A plain local path, "X:\...". UNC and device paths are refused before anything touches them:
    /// even File.Exists on \\host\... sends the user's NTLM hash to that host.</summary>
    public static bool IsLocal(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>a is b or inside b.</summary>
    public static bool Within(string a, string b)
    {
        a = a.TrimEnd('\\'); b = b.TrimEnd('\\');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
