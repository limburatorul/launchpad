using System.IO;

namespace Launchpad.Services;

public enum Kind { Inno, Nsis, Portable, Zip }

/// <summary>
/// One Protagonist Labs app as Launchpad knows it. Everything here is fixed by how the app is published (vault
/// Launchpad.md §3): where its version is announced, which release file installs it and how that file runs silently.
/// </summary>
/// <param name="Repo">GitHub repo whose latest release announces the version (free apps and Shelf); null = the site's latest.json.</param>
/// <param name="Asset">Picks the installer (free apps) or the trial (Shelf) among the release's files.</param>
/// <param name="UninstallKey">The app's own key under ...\Uninstall (AppId); null when it isn't known, then the name is matched.</param>
public sealed record Product(string Id, string Name, string Blurb, bool Paid, string Exe, Kind Kind, string? Repo, Func<string, bool> Asset, string? UninstallKey)
{
    public string Page(string campaign = "launchpad") =>
        $"https://protagonistlabs.app/{Id}/?utm_source=launchpad&utm_medium=app&utm_campaign={campaign}";

    /// <summary>The trial's file for a paid app; Shelf's comes from shelf-releases, the others from protagonist-downloads.</summary>
    public string? TrialFile => Id switch
    {
        "reel" => "Reel-Trial-Setup.exe", "reminder" => "Reminder-Trial-Setup.exe", "stash" => "Stash-Trial-Setup.exe",
        "frostpane" => "Frostpane-Trial.exe", "planner" => "Planner-Trial.zip", _ => null,
    };

    /// <summary>The paid apps' trial offer, as each app shows it (TRIAL_PRICE / Trial.Price): list price, price with the code, code.</summary>
    public (string Price, string Offer, string Code)? TrialOffer => Id switch
    {
        "shelf" => ("$9.99", "$8.00", "SHELFTRIAL"),
        "stash" => ("$4.99", "$3.99", "TRIALEND"),
        "reel" => ("$4.99", "$3.99", "TRIALEND"),
        "frostpane" => ("$7.99", "$6.39", "TRIALEND"),
        "planner" => ("$24.99", "$19.99", "TRIALEND"),
        "reminder" => ("$24.99", "$19.99", "TRIALEND"),
        _ => null,
    };

    public string BuyPage => $"https://protagonistlabs.app/{Id}/?utm_source=launchpad&utm_medium=app&utm_campaign=trial&code={TrialOffer?.Code}&buy=1";

    /// <summary>Where Launchpad puts the app: the studio's one folder for programs (Ecosistem v3), portable ones included.</summary>
    public string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProtagonistLabs", Name);
}

public static class Catalog
{
    private static Func<string, bool> Ends(string tail) => n => n.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    private static Func<string, bool> Is(string name) => n => n.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static readonly Func<string, bool> None = _ => false;

    /// <summary>The studio's apps in the order the site lists them, the paid ones first. HikCheck stays private.</summary>
    public static readonly IReadOnlyList<Product> All =
    [
        new("shelf", "Shelf", "Every game from every launcher", true, "Shelf.exe", Kind.Nsis, "limburatorul/shelf-releases", Is("Shelf-Trial-Setup.exe"), "93869666-a03d-5775-ba3a-19dde48e73bc"),
        new("stash", "Stash", "Thunderbird backups on a schedule", true, "Stash.exe", Kind.Inno, null, None, "{9C1E7A3B-5F2D-4C8B-9E61-3A7D2B1F0C44}_is1"),
        new("frostpane", "Frostpane", "Glass panes for your desktop icons", true, "Frostpane.exe", Kind.Inno, null, None, "{82C24B45-5C50-4728-BB6B-5B2134B44C5C}_is1"),
        new("reel", "Reel", "Your films, with posters and where you stopped", true, "Reel.exe", Kind.Nsis, null, None, "95693626-e8c4-5132-9e4f-6f4a12169be6"),
        new("planner", "Planner", "Plans and tasks on one board", true, "Planner.exe", Kind.Zip, null, None, null),
        new("reminder", "Reminder", "Reminders and a daily digest", true, "Reminder.exe", Kind.Nsis, null, None, null),
        new("filelabs", "File Labs", "A file manager with tabs and previews", false, "FileLabs.exe", Kind.Inno, "limburatorul/file-labs", Ends("-setup.exe"), "{7E4C2B9A-3F1D-4C8E-9A6B-51D0C2E8F4A7}_is1"),
        new("backuplabs", "Backup Labs", "Backups on a schedule, to any drive", false, "BackupLabs.exe", Kind.Inno, "limburatorul/backup-labs", Ends("-setup.exe"), "{3B9E6A12-8C4D-4F7B-A1E5-6D2F0B8C9E34}_is1"),
        new("spacescan", "SpaceScan", "See what fills your drives", false, "SpaceScan.exe", Kind.Inno, "limburatorul/spacescan", Ends("-setup.exe"), "{5B7F3C21-9D4A-4E86-B0C7-2A9E6F1D8B53}_is1"),
        new("sysoptimizer", "Sysoptimizer", "What used your PC, and when", false, "Sysoptimizer.exe", Kind.Inno, "limburatorul/sysoptimizer", Ends("-setup.exe"), "{8716521D-484A-4927-87F1-5246AD1F815A}_is1"),
        new("uproot", "Uproot", "Uninstall apps with what they leave", false, "Uproot.exe", Kind.Inno, "limburatorul/uproot", Ends("-setup.exe"), "{5B7C1E92-4D3A-4F86-9C21-8E0A7D6B3F14}_is1"),
        new("glaze", "Glaze", "YouTube without the clutter", false, "Glaze.exe", Kind.Inno, "limburatorul/glaze", n => n.StartsWith("Glaze-Setup-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "{93E393FD-2A35-4E0F-9CEE-B98EC1665065}_is1"),
        new("lume", "Lume", "Find and open anything with Alt+Space", false, "Lume.exe", Kind.Nsis, "limburatorul/lume", Ends("-x64.exe"), "c99ac480-c993-55fb-b9e0-27333504c63e"),
        new("steamradar", "SteamRadar", "Steam deals on the games you watch", false, "SteamRadar.exe", Kind.Nsis, "limburatorul/steamradar", Ends("-setup.exe"), null),
        new("hotkeyscan", "HotkeyScan", "Finds which app holds a shortcut", false, "HotkeyScan.exe", Kind.Portable, "limburatorul/hotkeyscan", Is("HotkeyScan.exe"), null),
        new("autoclicker", "Auto Clicker", "Clicks and keys on repeat", false, "Auto Clicker.exe", Kind.Portable, "limburatorul/auto-clicker", Is("Auto.Clicker.exe"), null),
        new("cursorselector", "Cursor Selector", "Change your mouse cursors in one click", false, "CursorSelector.exe", Kind.Portable, "limburatorul/cursor-selector", Is("CursorSelector.exe"), null),
    ];

    public static Product? Find(string id) => All.FirstOrDefault(a => a.Id == id);
}
