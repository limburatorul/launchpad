using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Launchpad.Services;

/// <summary>A purchase as the apps keep it: the order (txn) and the address it was bought with, or Shelf's licence key.</summary>
public sealed record Bought(string? Txn, string? Email, string? Licence);

/// <summary>
/// The purchase each paid app shares in %APPDATA%\ProtagonistLabs\data\&lt;id&gt;.purchase.json (vault Ecosistem.md, contract v4).
/// The app writes it when it saves its purchase; Launchpad reads it to fetch the bought installer from order.php, and writes
/// it when the purchase is typed here, so the app doesn't ask again. In the clear, in the user's profile, like the apps'
/// own update.json and registry values: nothing new is exposed, and it is sent only to that app's order.php.
/// </summary>
public static class Purchase
{
    private static readonly Regex TxnRx = new("^txn_[a-z0-9]{26}$");

    private static string PathOf(string id) => Path.Combine(Ecosystem.Folder, "data", id + ".purchase.json");

    public static bool ValidTxn(string? txn) => txn is not null && TxnRx.IsMatch(txn);
    public static bool ValidEmail(string? email) =>
        email is { Length: >= 3 and <= 254 } && email.Contains('@') && !email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    public static bool ValidLicence(string? key) => key is { Length: <= 2000 } && key.StartsWith("SHELF1.") && key.Split('.').Length == 3;

    /// <summary>The order id inside the link from the purchase email, or the id itself.</summary>
    public static string? TxnFrom(string? text) => text is null ? null : Regex.Match(text, "txn_[a-z0-9]{26}").Value is { Length: > 0 } v ? v : null;

    public static Bought? Read(string id)
    {
        try
        {
            var path = PathOf(id);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            if (Ecosystem.Read(path) is not { } o) return null;
            if (id == "shelf") return ValidLicence(Ecosystem.Text(o, "licence")) ? new Bought(null, null, Ecosystem.Text(o, "licence")) : null;
            var txn = Ecosystem.Text(o, "txn");
            var email = Ecosystem.Text(o, "email");
            return ValidTxn(txn) && ValidEmail(email) ? new Bought(txn, email, null) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write(string id, string txn, string email) =>
        Ecosystem.WriteJson(PathOf(id), new JsonObject { ["txn"] = txn, ["email"] = email });

    /// <summary>Forgets a purchase order.php said it doesn't know, as the apps do.</summary>
    public static void Forget(string id)
    {
        try { File.Delete(PathOf(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The day a Shelf key's Update Pass runs to ("until", yyyy-MM-dd), read from its payload.
    /// ponytail: the signature isn't checked here (.NET 8 has no Ed25519). It only decides whether Launchpad installs Shelf's
    /// installer, which is public anyway; Shelf checks the key itself and stays the judge of what it unlocks.
    /// </summary>
    public static DateTime? ShelfUntil(string? licence)
    {
        if (!ValidLicence(licence)) return null;
        try
        {
            var part = licence!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
            return doc.RootElement.TryGetProperty("until", out var u) && DateTime.TryParseExact(u.GetString(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return null; }
    }
}
