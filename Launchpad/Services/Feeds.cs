using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launchpad.Services;

/// <summary>A file Launchpad can install: where it is, what it must hash to, and how big it is when that's published.</summary>
public sealed record Download(string Url, string FileName, string Sha256, long? Size);

/// <summary>The newest version of an app, its first lines of notes, and the file that installs it (installer or trial).</summary>
public sealed record Available(Version Version, string Notes, Download? File);

/// <summary>
/// Where the versions come from (vault Launchpad.md §3), all of it already published: GitHub's latest release for the free
/// apps and Shelf, the site's /&lt;app&gt;/latest.json for the other paid apps, their trials from protagonist-downloads.
/// GitHub is asked with If-None-Match, since a 304 doesn't count against its 60 requests an hour; the site one request
/// at a time, a second apart (Imunify bans the IP on bursts). A failed request falls back to the last good answer.
/// </summary>
public static class Feeds
{
    private static readonly HttpClient Http = CreateClient();
    internal static string GitHubApi = "https://api.github.com";
    internal static string Site = "https://protagonistlabs.app";
    internal static string Downloads = "https://github.com/limburatorul/protagonist-downloads/releases/download";
    private static DateTime _lastSiteRequest = DateTime.MinValue;
    private static readonly SemaphoreSlim SiteGate = new(1, 1);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProtagonistLabs-Launchpad");
        return client;
    }

    /// <summary>The app's newest version, or null when it can't be known now (offline, no answer ever, an unreadable one).
    /// <paramref name="fresh"/> skips the cache: the elevated installer trusts nothing written in the user's folder.</summary>
    public static async Task<Available?> Latest(Product app, bool fresh = false)
    {
        if (app.Repo is { } repo)
        {
            var body = await Get($"{GitHubApi}/repos/{repo}/releases/latest", fresh);
            return body is null ? null : FromGitHub(body, app.Asset);
        }
        var feed = await Get($"{Site}/{app.Id}/latest.json", fresh, site: true);
        return feed is null ? null : FromSite(feed, app);
    }

    internal static Available? FromGitHub(string json, Func<string, bool> pick)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Detect.Parse(root.GetProperty("tag_name").GetString()) is not { } version) return null;
            Download? file = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                // GitHub publishes each asset's SHA-256 as "sha256:<hex>"; without one nothing gets installed
                if (!pick(name) || a.TryGetProperty("digest", out var d) is false || d.GetString() is not { } digest || !digest.StartsWith("sha256:")) continue;
                file = new Download(a.GetProperty("browser_download_url").GetString()!, name, digest[7..].ToLowerInvariant(), a.GetProperty("size").GetInt64());
                break;
            }
            return new Available(version, FirstLine(root.TryGetProperty("body", out var b) ? b.GetString() : null), file);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    internal static Available? FromSite(string json, Product app)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Detect.Parse(root.GetProperty("version").GetString()) is not { } version) return null;
            Download? trial = null;
            if (app.TrialFile is { } name && root.TryGetProperty("trialSha256", out var sha) && sha.GetString() is { } hex && Regex.IsMatch(hex, "^[0-9a-fA-F]{64}$"))
                trial = new Download($"{Downloads}/{app.Id}-trial/{name}", name, hex.ToLowerInvariant(), null);
            return new Available(version, FirstLine(root.TryGetProperty("notes", out var n) ? n.GetString() : null), trial);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>The first line of release notes worth showing: no headings, no bullets or bold markers, about one line long.</summary>
    internal static string FirstLine(string? notes)
    {
        foreach (var raw in (notes ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', ' ').Replace("**", "").Trim();
            if (line.Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            return line.Length > 110 ? line[..107].TrimEnd() + "…" : line;
        }
        return "";
    }

    private static async Task<string?> Get(string url, bool fresh, bool site = false)
    {
        var (etag, cached) = fresh ? (null, null) : State.Feed(url);
        if (site) await SiteGate.WaitAsync();
        try
        {
            if (site)
            {
                var wait = _lastSiteRequest.AddSeconds(1.2) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _lastSiteRequest = DateTime.UtcNow;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag is not null && cached is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) return cached;
            if (!response.IsSuccessStatusCode) return cached;      // 403 rate limit, 404 no release yet, 5xx: the last good answer
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 2 << 20) return cached;
            if (!fresh) State.SetFeed(url, response.Headers.ETag?.ToString(), body);
            return body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return cached;
        }
        finally
        {
            if (site) SiteGate.Release();
        }
    }

    /// <summary>Downloads a file over HTTPS into <paramref name="folder"/>, reporting 0..1, and checks its size and SHA-256.
    /// A file that doesn't match is deleted and never returned.</summary>
    public static async Task<string> Fetch(Download file, string folder, IProgress<double>? progress, CancellationToken token)
    {
        if (!file.Url.StartsWith("https://github.com/", StringComparison.Ordinal)) throw new IOException("Not a GitHub download");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(file.FileName));
        using (var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("The download left HTTPS");
            var total = response.Content.Headers.ContentLength ?? file.Size ?? 0;
            using var source = await response.Content.ReadAsStreamAsync(token);
            using var target = File.Create(path);
            var buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), token);
                done += n;
                progress?.Report(total > 0 ? Math.Min(1, (double)done / total) : 0);
            }
        }
        if (file.Size is { } size && new FileInfo(path).Length != size) { File.Delete(path); throw new IOException("The download was cut short"); }
        string actual = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }, token);
        if (actual != file.Sha256) { File.Delete(path); throw new IOException("The download doesn't match its published checksum"); }
        return path;
    }
}
