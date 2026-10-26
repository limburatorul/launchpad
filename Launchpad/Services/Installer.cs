using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Launchpad.Services;

public enum JobKind { Free, Trial, Bought }

/// <summary>One app to install: a free app, a paid app's public trial, or a paid app's bought installer (with its purchase).</summary>
public sealed record Job(Product App, Download File, Version Version, JobKind Kind, Bought? Purchase = null);

public sealed record Outcome(bool Ok, string Message);

/// <summary>
/// Installing and updating (vault Launchpad.md §4). Files are downloaded as the user and checked against the published
/// SHA-256; then one elevated copy of Launchpad (one UAC prompt for the whole batch) runs them.
///
/// The elevated side trusts nothing the user's folder holds: the batch only names app ids and files, the checksum is
/// asked again from GitHub or the site, and each file is copied into Launchpad's own folder in Program Files (where only
/// admins write) while it is hashed. Only that copy, once it matches, is run. Without this, anything running as the user
/// could swap the file between the check and the run and get it started with admin rights.
/// </summary>
public static class Installer
{
    public static string DownloadFolder => Path.Combine(Ecosystem.Data, "downloads");

    /// <summary>Downloads every job's file, then installs them; reports each app's progress and outcome as it goes.</summary>
    public static async Task<Dictionary<string, Outcome>> Run(IReadOnlyList<Job> jobs, Action<string, string, double?> progress, CancellationToken token)
    {
        var outcomes = new Dictionary<string, Outcome>();
        var ready = new List<(Job Job, string Path)>();
        foreach (var job in jobs)
        {
            try
            {
                progress(job.App.Id, "Downloading", 0);
                var report = new Progress<double>(p => progress(job.App.Id, $"Downloading {p * 100:0}%", p));
                var path = job.Kind == JobKind.Bought && job.Purchase is { Txn: not null } purchase
                    ? await Feeds.FetchBought(job.File, purchase, DownloadFolder, report, token)
                    : await Feeds.Fetch(job.File, DownloadFolder, report, token);
                ready.Add((job, path));
            }
            catch (UnknownPurchase)
            {
                // as the apps do: a purchase order.php doesn't know is forgotten, and asked for again next time
                Purchase.Forget(job.App.Id);
                outcomes[job.App.Id] = new Outcome(false, "Purchase not found. Check the link and address.");
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
            {
                AppLog.Write($"{job.App.Name}: download failed: {ex.Message}");
                outcomes[job.App.Id] = new Outcome(false, token.IsCancellationRequested ? "Cancelled" : "Couldn't download. Try again later.");
            }
        }
        if (ready.Count == 0) return outcomes;

        // A portable app the user keeps in a folder of their own is replaced there, as the user: no admin needed, nothing to elevate.
        foreach (var (job, path) in ready.ToList())
        {
            if (job.App.Kind != Kind.Portable || Detect.Find(job.App) is not { } found || Paths.Within(found.Exe, job.App.Folder)) continue;
            ready.Remove((job, path));
            try
            {
                ReplaceExe(path, found.Exe);
                outcomes[job.App.Id] = new Outcome(true, "Updated");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder the user can't write (it was put there by someone else): the copy goes to Program Files instead
                ready.Add((job, path));
                AppLog.Write($"{job.App.Name}: couldn't replace {found.Exe}: {ex.Message}");
            }
        }

        if (ready.Count > 0)
        {
            var running = ready.Select(r => r.Job.App).Where(IsRunning).ToList();
            foreach (var (job, _) in ready) progress(job.App.Id, "Installing…", null);
            foreach (var pair in await Elevate(ready, token)) outcomes[pair.Key] = pair.Value;
            // Inno installers start their app again themselves; electron-builder's /S doesn't, so what was running is started here, as the user.
            foreach (var app in running.Where(a => !IsRunning(a)))
                if (Detect.Find(app) is { } found) Open(found.Exe);
        }
        foreach (var (job, path) in ready) TryDelete(path);
        // a portable exe may carry no version of its own (Auto Clicker's PyInstaller build): remember the one installed
        foreach (var job in jobs)
            if (job.App.Kind is Kind.Portable or Kind.Zip && outcomes.TryGetValue(job.App.Id, out var o) && o.Ok)
                State.SetInstalledVersion(job.App.Id, job.Version);
        return outcomes;
    }

    private static async Task<Dictionary<string, Outcome>> Elevate(List<(Job Job, string Path)> ready, CancellationToken token)
    {
        var batch = Path.Combine(DownloadFolder, $"batch-{Guid.NewGuid():N}.json");
        var items = new JsonArray();
        foreach (var (job, path) in ready) items.Add(new JsonObject { ["id"] = job.App.Id, ["file"] = path, ["kind"] = job.Kind.ToString() });
        Ecosystem.WriteJson(batch, new JsonObject { ["items"] = items });
        var result = batch + ".result.json";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--install \"{batch}\"") { UseShellExecute = true, Verb = "runas" })!;
            await process.WaitForExitAsync(token);
            var outcomes = new Dictionary<string, Outcome>();
            var answer = Ecosystem.Read(result);
            foreach (var (job, _) in ready)
                outcomes[job.App.Id] = answer?[job.App.Id] is JsonObject o && o["ok"] is JsonValue ok && ok.TryGetValue(out bool done)
                    ? new Outcome(done, Ecosystem.Text(o, "message") ?? (done ? "Installed" : "Not installed"))
                    : new Outcome(false, "Not installed");
            return outcomes;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)       // the UAC prompt was declined
        {
            AppLog.Write("Install: UAC declined");
            return ready.ToDictionary(r => r.Job.App.Id, _ => new Outcome(false, "Update not installed"));
        }
        finally
        {
            TryDelete(batch);
            TryDelete(result);
        }
    }

    // ───────────── the elevated side: Launchpad.exe --install <batch.json> ─────────────

    /// <summary>Runs in the elevated copy. Exit code 0; each app's outcome goes to &lt;batch&gt;.result.json.</summary>
    public static int RunElevated(string batchPath)
    {
        var results = new JsonObject();
        if (!Paths.IsLocal(batchPath) || Ecosystem.Read(batchPath)?["items"] is not JsonArray items) return 1;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // ponytail: only an installed copy installs anything; a build run from bin\ has no admin-only folder to stage in
        if (!Paths.Within(AppContext.BaseDirectory, programFiles)) { AppLog.Write("Install refused: Launchpad isn't running from Program Files"); return 1; }
        var staging = Path.Combine(AppContext.BaseDirectory, "staging", Guid.NewGuid().ToString("N"));
        AppLog.Write($"Install batch: {items.Count} item(s)");
        foreach (var item in items.OfType<JsonObject>())
        {
            var id = Ecosystem.Text(item, "id") ?? "";
            var app = Catalog.Find(id);
            if (app is null) continue;
            if (!Enum.TryParse(Ecosystem.Text(item, "kind"), out JobKind kind)) continue;
            try
            {
                results[id] = Result(InstallOne(app, Ecosystem.Text(item, "file"), kind, staging));
            }
            catch (Exception ex)
            {
                AppLog.Write($"{app.Name}: install failed: {ex}");
                results[id] = Result(new Outcome(false, "Couldn't install. Try again later."));
            }
        }
        try { Directory.Delete(staging, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Ecosystem.WriteJson(batchPath + ".result.json", results);
        return 0;
    }

    private static JsonObject Result(Outcome o) => new() { ["ok"] = o.Ok, ["message"] = o.Message };

    private static Outcome InstallOne(Product app, string? file, JobKind kind, string staging)
    {
        if (app.Paid == (kind == JobKind.Free)) return new Outcome(false, "Not installed");      // free apps as Free, paid apps as Trial or Bought
        if (!Paths.IsLocal(file)) return new Outcome(false, "Not installed");
        // on the thread pool: blocking the UI thread on an await that wants to come back to it hung 1.0.0's first real batch
        var latest = Task.Run(() => Feeds.Latest(app, fresh: true)).GetAwaiter().GetResult();
        // the bought installer's checksum comes from latest.json; Shelf has one public installer for every copy
        var expected = kind == JobKind.Bought && app.Repo is null ? latest?.Bought : latest?.File;
        if (expected is null || !Path.GetFileName(file!).Equals(expected.FileName, StringComparison.OrdinalIgnoreCase))
            return new Outcome(false, "Couldn't check the download. Try again later.");

        var staged = Stage(file!, expected, staging);
        if (staged is null) return new Outcome(false, "The download didn't match. Try again.");

        switch (app.Kind)
        {
            case Kind.Inno:
            case Kind.Nsis:
                using (var setup = Process.Start(new ProcessStartInfo(staged, app.Kind == Kind.Inno ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : "/S") { UseShellExecute = false })!)
                {
                    if (!setup.WaitForExit(TimeSpan.FromMinutes(15))) return new Outcome(false, "The installer is taking too long");
                    AppLog.Write($"{app.Name}: installer exited with {setup.ExitCode}");
                    return setup.ExitCode == 0 ? new Outcome(true, "Installed") : new Outcome(false, $"Not installed (code {setup.ExitCode})");
                }
            case Kind.Portable:
                Directory.CreateDirectory(app.Folder);
                ReplaceExe(staged, Path.Combine(app.Folder, app.Exe));
                Shortcut(app);
                return new Outcome(true, "Installed");
            case Kind.Zip:
                Unzip(staged, app.Folder);
                // Planner keeps plan.json beside its exe (vault Planner.md): the folder must be writable by the people who use it
                AllowUsers(app.Folder);
                Shortcut(app);
                return new Outcome(true, "Installed");
        }
        return new Outcome(false, "Not installed");
    }

    /// <summary>Copies the file into the admin-only staging folder while hashing what is read, and keeps the source open,
    /// shared for reading only, so it can't change under the copy. Null when the copy doesn't match.</summary>
    internal static string? Stage(string source, Download expected, string staging)
    {
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, expected.FileName);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = File.Create(target))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024];
            int n;
            while ((n = input.Read(buffer)) > 0) { hash.AppendData(buffer, 0, n); output.Write(buffer, 0, n); }
            bool sizeOk = expected.Size is not { } size || output.Length == size;
            if (sizeOk && Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() == expected.Sha256) return target;
        }
        File.Delete(target);
        return null;
    }

    /// <summary>Puts a new exe in place; a running one is renamed to .old first (Windows lets a running exe be renamed).</summary>
    internal static void ReplaceExe(string source, string target)
    {
        var old = target + ".old";
        if (File.Exists(old)) TryDelete(old);
        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { File.Move(target, old, overwrite: true); }
        }
        File.Copy(source, target);
    }

    /// <summary>Extracts a release zip, without its one top folder ("Planner/...") when it has one, over what's there.
    /// Files not in the zip (the user's plan.json) are left alone; an entry leading outside the folder is refused.</summary>
    internal static void Unzip(string zip, string folder)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !n.EndsWith('/')).ToList();
        var top = names.Count > 0 && names.All(n => n.Contains('/')) && names.Select(n => n[..n.IndexOf('/')]).Distinct().Count() == 1
            ? names[0][..(names[0].IndexOf('/') + 1)] : "";
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(folder, name[top.Length..]));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Unsafe path in the zip: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { File.Move(target, target + ".old", overwrite: true); }
            }
            entry.ExtractToFile(target);
        }
    }

    private static void AllowUsers(string folder)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    /// <summary>A Start Menu shortcut for everyone, for the apps without an installer of their own.</summary>
    private static void Shortcut(Product app)
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), app.Name + ".lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = Path.Combine(app.Folder, app.Exe);
        shortcut.WorkingDirectory = app.Folder;
        shortcut.Save();
    }

    // ───────────── helpers ─────────────

    public static bool IsRunning(Product app) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.Exe)).Length > 0;

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { AppLog.Write($"Couldn't open {target}: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
