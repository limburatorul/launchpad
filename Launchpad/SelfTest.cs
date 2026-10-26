using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Launchpad.Services;

namespace Launchpad;

/// <summary>
/// Launchpad.exe --selftest: the logic that decides what is installed, what is newer, what gets run and which notice is
/// due, checked without the network and without touching the real apps, their cards or their trial clocks (a scratch
/// registry key and temp folders, removed afterwards). Report in %TEMP%\launchpad-selftest.txt; exit code = failures.
/// </summary>
public static class SelfTest
{
    private static int _failed;
    private static StreamWriter? _log;

    public static int Run()
    {
        _log = new StreamWriter(Path.Combine(Path.GetTempPath(), "launchpad-selftest.txt")) { AutoFlush = true };
        var temp = Directory.CreateTempSubdirectory("launchpad-selftest-").FullName;
        try
        {
            Versions();
            Assets();
            FeedsParse();
            Cards(temp);
            Trials(temp);
            Notices(temp);
            Staging(temp);
        }
        catch (Exception ex) { Check(false, $"crashed: {ex}"); }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ProtagonistLabs\TrialTest\launchpad-selftest", throwOnMissingSubKey: false);
        }
        _log.WriteLine(_failed == 0 ? "ALL OK" : $"{_failed} FAILED");
        _log.Dispose();
        return _failed;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) _failed++;
        _log!.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    }

    private static void Versions()
    {
        Check(Detect.Parse("1.0.53") == new Version(1, 0, 53), "plain version");
        Check(Detect.Parse("v4.5.5") == new Version(4, 5, 5), "tag with v");
        Check(Detect.Parse("1.14.1-beta") == new Version(1, 14, 1), "suffix ignored");
        Check(Detect.Parse("2.3") == new Version(2, 3, 0), "two parts");
        Check(Detect.Parse("trial") is null && Detect.Parse("") is null && Detect.Parse(null) is null, "not a version");
        Check(Detect.Parse("1.0.53") > Detect.Parse("1.0.9"), "1.0.53 is newer than 1.0.9 (numbers, not text)");
    }

    /// <summary>Each app picks its installer among its latest release's real file names (26.10.2026).</summary>
    private static void Assets()
    {
        var releases = new Dictionary<string, string[]>
        {
            ["filelabs"] = ["FileLabs-1.0.53-setup.exe"],
            ["sysoptimizer"] = ["Sysoptimizer-1.21.2-setup.exe", "Sysoptimizer.exe"],
            ["lume"] = ["latest.yml", "Lume-0.5.2-portable.exe", "Lume-0.5.2-x64.exe", "Lume-0.5.2-x64.exe.blockmap"],
            ["steamradar"] = ["SteamRadar-0.9.1-portabil.exe", "SteamRadar-0.9.1-setup.exe"],
            ["glaze"] = ["Glaze-2.2.1-portable.exe", "Glaze-Setup-2.2.1.exe", "latest.yml"],
            ["uproot"] = ["Uproot-1.0.5-setup.exe", "Uproot.exe"],
            ["hotkeyscan"] = ["HotkeyScan.exe", "README.md"],
            ["autoclicker"] = ["Auto.Clicker.exe"],
            ["cursorselector"] = ["Cursor-Selector-1.4.1-with-cursors.zip", "CursorSelector.exe"],
            ["shelf"] = ["Shelf-Setup.exe", "Shelf-Trial-Setup.exe", "Shelf.Setup.4.5.5.exe"],
        };
        var want = new Dictionary<string, string>
        {
            ["filelabs"] = "FileLabs-1.0.53-setup.exe", ["sysoptimizer"] = "Sysoptimizer-1.21.2-setup.exe", ["lume"] = "Lume-0.5.2-x64.exe",
            ["steamradar"] = "SteamRadar-0.9.1-setup.exe", ["glaze"] = "Glaze-Setup-2.2.1.exe", ["uproot"] = "Uproot-1.0.5-setup.exe",
            ["hotkeyscan"] = "HotkeyScan.exe", ["autoclicker"] = "Auto.Clicker.exe", ["cursorselector"] = "CursorSelector.exe", ["shelf"] = "Shelf-Trial-Setup.exe",
        };
        foreach (var (id, names) in releases)
        {
            var picked = names.Where(Catalog.Find(id)!.Asset).ToList();
            Check(picked.Count == 1 && picked[0] == want[id], $"{id} picks {want[id]} (got {string.Join(", ", picked)})");
        }
        Check(Catalog.All.Count == 17 && Catalog.All.All(a => a.Id != "hikcheck"), "17 apps, HikCheck stays private");
        Check(Catalog.All.Where(a => a.Paid).All(a => a.TrialOffer is not null && (a.Id == "shelf" || a.TrialFile is not null)), "every paid app has its offer and a trial file");
        Check(Catalog.Find("shelf")!.BuyPage.Contains("code=SHELFTRIAL") && Catalog.Find("reel")!.BuyPage.Contains("code=TRIALEND&buy=1"), "buy links carry the trial codes");
    }

    private const string Hex = "5b8ab856f4639d368bc110f38bcd1fc4810291e45b8b025ca0aa81850c8daeca";

    private static void FeedsParse()
    {
        string release = """
            {"tag_name":"v1.0.53","body":"#### Smooth zoom\n- **Zoom** glides now.\n- more","assets":[
              {"name":"FileLabs-1.0.53-setup.exe","size":123,"browser_download_url":"https://github.com/limburatorul/file-labs/releases/download/v1.0.53/FileLabs-1.0.53-setup.exe","digest":"sha256:ABC"}]}
            """;
        var a = Feeds.FromGitHub(release, Catalog.Find("filelabs")!.Asset);
        Check(a?.Version == new Version(1, 0, 53) && a.File?.Sha256 == "abc" && a.File.Size == 123, "GitHub release: version, file, checksum");
        Check(a?.Notes == "Zoom glides now.", $"first line of notes, no heading or markers (got '{a?.Notes}')");
        var noDigest = Feeds.FromGitHub(release.Replace(",\"digest\":\"sha256:ABC\"", ""), Catalog.Find("filelabs")!.Asset);
        Check(noDigest is { File: null }, "an asset without a published checksum is never offered for install");
        Check(Feeds.FromGitHub("{\"tag_name\":\"latest\",\"assets\":[]}", _ => true) is null, "a tag that isn't a version");
        Check(Feeds.FromGitHub("not json", _ => true) is null && Feeds.FromGitHub("{}", _ => true) is null, "broken answers are 'no answer'");

        var reel = Catalog.Find("reel")!;
        var site = Feeds.FromSite($"{{\"version\":\"1.14.1\",\"notes\":\"A small release.\",\"trialSha256\":\"{Hex}\"}}", reel);
        Check(site?.Version == new Version(1, 14, 1) && site.File?.Url == "https://github.com/limburatorul/protagonist-downloads/releases/download/reel-trial/Reel-Trial-Setup.exe" && site.File.Sha256 == Hex,
            "site feed: version and the trial from protagonist-downloads");
        Check(Feeds.FromSite("{\"version\":\"1.14.1\",\"trialSha256\":\"nothex\"}", reel) is { File: null }, "a trial checksum that isn't SHA-256 hex: no trial offered");
        Check(Feeds.FromSite("{\"version\":\"x\"}", reel) is null, "site feed without a version");
    }

    private static void Cards(string temp)
    {
        var saved = Ecosystem.Folder;
        Ecosystem.Folder = Path.Combine(temp, "eco");
        try
        {
            Directory.CreateDirectory(Path.Combine(Ecosystem.Folder, "apps"));
            var exe = Path.Combine(temp, "Fake.exe");
            File.WriteAllText(exe, "");
            void Card(string id, string body) => File.WriteAllText(Path.Combine(Ecosystem.Folder, "apps", id + ".json"), body);
            Card("good", $"{{\"schema\":1,\"exe\":{System.Text.Json.JsonSerializer.Serialize(exe)},\"version\":\"1.2.3\",\"edition\":\"trial\"}}");
            Card("unc", "{\"schema\":1,\"exe\":\"\\\\\\\\host\\\\share\\\\x.exe\",\"version\":\"1.0.0\"}");
            Card("gone", "{\"schema\":1,\"exe\":\"C:\\\\Nope\\\\x.exe\",\"version\":\"1.0.0\"}");
            Card("schema2", $"{{\"schema\":2,\"exe\":{System.Text.Json.JsonSerializer.Serialize(exe)}}}");
            Card("broken", "{\"schema\":1,");
            Check(Ecosystem.Card("good") is { Version: "1.2.3", Edition: "trial" }, "a valid card");
            Check(Ecosystem.Card("unc") is null, "a UNC exe is refused (never touched)");
            Check(Ecosystem.Card("gone") is null && Ecosystem.Card("schema2") is null && Ecosystem.Card("broken") is null && Ecosystem.Card("none") is null,
                "missing exe, other schema, broken JSON, no card: all 'not there'");
            Check(!Paths.IsLocal(@"\\?\C:\x") && !Paths.IsLocal(@"\\.\pipe\x") && !Paths.IsLocal("x.exe") && Paths.IsLocal(@"C:\x"), "only X:\\ paths are local");
        }
        finally { Ecosystem.Folder = saved; }
    }

    private static void Trials(string temp)
    {
        const string app = "launchpad-selftest";
        TrialClock.RegistryRoot = @"Software\ProtagonistLabs\TrialTest";
        TrialClock.FileRoot = Path.Combine(temp, "trial");
        TrialClock.Machine = () => "machine-guid";
        long start = 1_760_000_000;
        void Write(string? registry, string? file)
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"{TrialClock.RegistryRoot}\{app}"))
                if (registry is null) key.DeleteValue("T", false); else key.SetValue("T", registry);
            Directory.CreateDirectory(TrialClock.FileRoot);
            var path = Path.Combine(TrialClock.FileRoot, app + ".dat");
            if (file is null) File.Delete(path); else File.WriteAllText(path, file);
        }
        string Good(long s, long seen) => $"{s}|{seen}|{TrialClock.Sign(app, s, seen, "machine-guid")}";

        TrialClock.Now = () => start + 11 * 86400 + 3600;          // 11 days and an hour in
        Write(Good(start, start + 100), Good(start, start + 100));
        Check(TrialClock.Read(app) == (start, 3), "11 days in: 3 days left");
        TrialClock.Now = () => start + 86400;
        Write(Good(start, start + 13 * 86400), null);
        Check(TrialClock.Read(app)?.Left == 1, "the clock turned back: 'seen' still counts (1 left), registry copy alone is enough");
        TrialClock.Now = () => start + 20 * 86400;
        Check(TrialClock.Read(app)?.Left == 0, "after 20 days: ended");
        Write(Good(start, start + 100).Replace($"{start}|", $"{start + 5 * 86400}|"), null);
        Check(TrialClock.Read(app) is null, "a start moved without a new signature: not believed");
        Write(Good(start, start + 100), "garbage");
        Check(TrialClock.Read(app) is null, "one copy tampered with: don't know");
        Write(null, null);
        Check(TrialClock.Read(app) is null, "no record: don't know");
        Check(TrialClock.Label(null) == "Trial" && TrialClock.Label(3) == "Trial · 3 days" && TrialClock.Label(1) == "Trial · last day" && TrialClock.Label(0) == "Trial ended", "labels");
    }

    /// <summary>The trial notices over a trial's last days, with the state a real run would keep.</summary>
    private static void Notices(string temp)
    {
        var saved = State.File;
        try
        {
            int Run(long start, bool trial, params int[] days)
            {
                int shown = 0;
                foreach (int left in days)
                {
                    var e = new Entry(Catalog.Find("reel")!) { Installed = new Installed(@"C:\x\Reel.exe", new Version(1, 0, 0), trial), Trial = (start, left) };
                    if (Hub.TrialKey(e) is { } key && State.MarkOnce(key)) shown++;
                }
                return shown;
            }
            State.File = Path.Combine(temp, "state-a.json");
            Check(Run(1, true, 5, 4, 3, 3, 2, 1, 1, 0) == 2, "a trial run day by day, restarted on some days: exactly 2 notices (3 days, last day)");
            Check(Hub.TrialStage(4) is null && Hub.TrialStage(3) == "3days" && Hub.TrialStage(2) == "3days" && Hub.TrialStage(1) == "lastday" && Hub.TrialStage(0) is null, "stages");
            State.File = Path.Combine(temp, "state-b.json");
            Check(Run(1, true, 4, 2, 1) == 2, "PC off on the day with 3 left: the first notice comes with 2 left");
            Check(Run(1, true, 1) == 0, "the same trial again: nothing more");
            Check(Run(2, true, 3, 1) == 2, "a new trial (another start): its own two notices");
            State.File = Path.Combine(temp, "state-c.json");
            Check(Run(1, false, 3, 1) == 0, "a bought copy: none");
            Check(Run(1, true, 0, 9, 14) == 0, "expired or early: none");
            var shelf = new Entry(Catalog.Find("filelabs")!) { Installed = new Installed(@"C:\x\FileLabs.exe", null, true), Trial = (1, 3) };
            Check(Hub.TrialKey(shelf) is null, "a free app never gets a trial notice");
        }
        finally { State.File = saved; }
    }

    private static void Staging(string temp)
    {
        var source = Path.Combine(temp, "Setup.exe");
        File.WriteAllBytes(source, [1, 2, 3, 4, 5]);
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        var staged = Installer.Stage(source, new Download("https://github.com/x", "Setup.exe", sha, 5), Path.Combine(temp, "stage1"));
        Check(staged is not null && File.ReadAllBytes(staged).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "a matching file is copied into staging");
        Check(Installer.Stage(source, new Download("https://github.com/x", "Setup.exe", new string('0', 64), 5), Path.Combine(temp, "stage2")) is null
              && !File.Exists(Path.Combine(temp, "stage2", "Setup.exe")), "a file that doesn't match its checksum is refused and its copy deleted");
        Check(Installer.Stage(source, new Download("https://github.com/x", "Setup.exe", sha, 6), Path.Combine(temp, "stage3")) is null, "a wrong size is refused");

        var zip = Path.Combine(temp, "Planner.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("Planner/Planner.exe").Open())) w.Write("new");
            using (var w = new StreamWriter(archive.CreateEntry("Planner/whats-new/1.4.webp").Open())) w.Write("pic");
        }
        var folder = Path.Combine(temp, "Planner");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plan.json"), "mine");
        File.WriteAllText(Path.Combine(folder, "Planner.exe"), "old");
        Installer.Unzip(zip, folder);
        Check(File.ReadAllText(Path.Combine(folder, "Planner.exe")) == "new" && File.Exists(Path.Combine(folder, "whats-new", "1.4.webp")), "zip extracted without its top folder, over the old files");
        Check(File.ReadAllText(Path.Combine(folder, "plan.json")) == "mine", "the user's plan.json is left alone");

        var evil = Path.Combine(temp, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("Planner/../../escaped.txt").Open())) w.Write("x");
        bool refused = false;
        try { Installer.Unzip(evil, Path.Combine(temp, "Evil")); } catch (IOException) { refused = true; }
        Check(refused && !File.Exists(Path.Combine(temp, "escaped.txt")), "a zip entry leading outside the folder is refused");

        var target = Path.Combine(temp, "App.exe");
        File.WriteAllText(target, "old");
        Installer.ReplaceExe(source, target);
        Check(File.ReadAllBytes(target).Length == 5, "a portable exe is replaced");
    }

    /// <summary>
    /// Launchpad.exe --shots &lt;folder&gt;: the window with this PC's real apps and feeds, and the two kinds of notice, drawn
    /// off-screen into PNGs, so the look can be checked without a window over the user's desktop. Reads only.
    /// </summary>
    public static async Task Shots(string folder)
    {
        Directory.CreateDirectory(folder);
        Notice.Muted = true;
        var hub = new Hub();
        await hub.Check();
        var window = new MainWindow(hub, (App)Application.Current) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Save(window.Content as FrameworkElement, Path.Combine(folder, "window.png"));
        var reel = Catalog.Find("reel")!;
        Save(Notice.Preview(new NoticeContent([Theme.Icon("reel")], "Your Reel trial ends in 3 days", "Buy Reel now and keep everything as you've set it up.",
            reel.TrialOffer!.Value.Price, reel.TrialOffer.Value.Offer, "Buy now — 20% off", () => { }, null, "x", () => true)), Path.Combine(folder, "notice-trial.png"));
        Save(Notice.Preview(new NoticeContent([Theme.Icon("filelabs"), Theme.Icon("spacescan"), Theme.Icon("lume")], "3 updates ready", "File Labs, SpaceScan, Lume",
            null, null, "Update all", () => { }, () => { }, "y", () => true)), Path.Combine(folder, "notice-updates.png"));
    }

    private static void Save(FrameworkElement? element, string path)
    {
        if (element is null) return;
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var ground = new DrawingVisual();
        using (var dc = ground.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0b, 0x0c, 0x10)), null, new Rect(0, 0, w, h));
        bitmap.Render(ground);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}
