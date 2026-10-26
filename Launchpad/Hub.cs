using Launchpad.Services;

namespace Launchpad;

/// <summary>One app as the window shows it: what's on this PC, what's published, and what Launchpad is doing with it.</summary>
public sealed class Entry(Product app)
{
    public Product App { get; } = app;
    public Installed? Installed { get; set; }
    public Available? Available { get; set; }
    /// <summary>The trial's start and days left, when this is a trial copy and its clock reads true.</summary>
    public (long Start, int Left)? Trial { get; set; }
    public string? Busy { get; set; }
    public double? Progress { get; set; }
    public Outcome? Last { get; set; }

    public bool HasUpdate => Installed?.Version is { } v && Available?.Version is { } a && a > v;
    public bool TrialEnded => Installed?.Trial == true && Trial is { Left: 0 };
    public bool CanInstall => Installed is null && Available?.File is not null;
    public bool OfferDays => Installed?.Trial == true && Trial is { Left: >= 1 and <= 3 };

    /// <summary>
    /// How Launchpad would install this app's newest version, or null when it can't (vault Ecosistem.md, contract v4):
    /// a free app from its release; a running trial from the public trial (an ended one never updates); a bought copy with
    /// the installer order.php serves for its purchase, checked against latest.json's setupSha256; Shelf, whose installer is
    /// public, only when the key's Update Pass covers the release, otherwise Shelf itself says how to renew.
    /// </summary>
    public (JobKind Kind, Download File)? Route
    {
        get
        {
            if (Available is not { } a) return null;
            if (!App.Paid) return a.File is { } f ? (JobKind.Free, f) : null;
            if (Installed is null || Installed.Trial) return TrialEnded || a.File is null ? null : (JobKind.Trial, a.File);
            if (App.Id == "shelf")
                return a.File is { } setup && a.Published is { } published && Purchase.ShelfUntil(Purchase.Read("shelf")?.Licence) is { } until
                       && published.Date <= until ? (JobKind.Bought, setup) : null;
            return a.Bought is { } bought ? (JobKind.Bought, bought) : null;
        }
    }

    public bool UpdatesHere => HasUpdate && Route is not null;
}

/// <summary>Everything the window and the tray show, and what they can ask for. Lives on the UI thread.</summary>
public sealed class Hub
{
    public List<Entry> Entries { get; } = Catalog.All.Select(a => new Entry(a)).ToList();
    public event Action? Changed;
    /// <summary>Only this entry's progress moved: the window updates its bar in place instead of building every card again
    /// (a rebuild per downloaded chunk swallowed clicks on the other cards).</summary>
    public event Action<Entry>? Progressed;
    public bool Checking { get; private set; }
    public Action? ShowWindow { get; set; }
    /// <summary>Asks for a paid app's purchase when none is shared yet (the purchase window); null = cancelled.</summary>
    public Func<Entry, Bought?>? AskPurchase { get; set; }
    private bool _installing;
    /// <summary>Apps asked for while a batch runs: they go next, as their own batch, instead of being ignored.</summary>
    private readonly List<Entry> _queued = [];

    public IEnumerable<Entry> Updates => Entries.Where(e => e.HasUpdate);
    public IEnumerable<Entry> InstalledApps => Entries.Where(e => e.Installed is not null);
    public IEnumerable<Entry> Missing => Entries.Where(e => e.Installed is null);

    /// <summary>What's installed, read again (cards, Uninstall entries, trial clocks). Cheap: run it whenever the window opens.</summary>
    public void Scan()
    {
        foreach (var e in Entries)
        {
            e.Installed = Detect.Find(e.App);
            e.Trial = e.Installed?.Trial == true ? TrialClock.Read(e.App.Id) : null;
            e.Available ??= Feeds.Cached(e.App);
        }
        Changed?.Invoke();
    }

    /// <summary>Asks every feed for the newest versions, then shows the notices that are due.</summary>
    public async Task Check()
    {
        if (Checking) return;
        Checking = true;
        Changed?.Invoke();
        try
        {
            Scan();
            // GitHub answers in parallel; the site's feeds queue themselves a second apart inside Feeds
            await Task.WhenAll(Entries.Select(async e => e.Available = await Feeds.Latest(e.App) ?? e.Available));
            State.LastCheck = DateTime.UtcNow;
            State.Save();
            AppLog.Write($"Checked: {Updates.Count()} updates, {Entries.Count(e => e.Available is null)} feeds without an answer");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
        PostNotices();
    }

    public Task UpdateAll() => Install(Updates.Where(e => e.UpdatesHere).ToList());

    /// <summary>Installs or updates the apps, free and paid (trials and bought copies), one UAC prompt for the lot.</summary>
    public async Task Install(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return;
        if (_installing)
        {
            foreach (var e in entries.Where(e => e.Busy is null && e.Route is not null))
            {
                _queued.Add(e);
                e.Busy = "Waiting"; e.Progress = null; e.Last = null;
            }
            Changed?.Invoke();
            return;
        }
        _installing = true;
        var jobs = new List<Job>();
        foreach (var e in entries)
        {
            if (e.Route is not { } route) continue;
            Bought? purchase = null;
            if (route.Kind == JobKind.Bought && e.App.Id != "shelf")
            {
                purchase = Purchase.Read(e.App.Id) ?? AskPurchase?.Invoke(e);
                if (purchase is null) continue;      // cancelled: this one waits, the rest go ahead
            }
            jobs.Add(new Job(e.App, route.File, e.Available!.Version, route.Kind, purchase));
            e.Busy = "Waiting"; e.Progress = null; e.Last = null;
        }
        if (jobs.Count == 0) { _installing = false; return; }
        Changed?.Invoke();
        try
        {
            var outcomes = await Installer.Run(jobs, (id, status, progress) =>
            {
                var e = Entries.First(x => x.App.Id == id);
                if (e.Busy == status) return;       // the same whole percent: nothing to draw
                e.Busy = status; e.Progress = progress;
                Progressed?.Invoke(e);
            }, CancellationToken.None);
            foreach (var e in entries)
            {
                e.Busy = null; e.Progress = null;
                if (outcomes.TryGetValue(e.App.Id, out var o)) e.Last = o;
            }
        }
        finally
        {
            _installing = false;
            Scan();
            if (_queued.Count > 0)
            {
                var next = _queued.ToList();
                _queued.Clear();
                foreach (var e in next) e.Busy = null;
                _ = Install(next);
            }
        }
    }

    public void Open(Entry e)
    {
        if (e.Installed is { } i) Installer.Open(i.Exe);
        else Installer.Open(e.App.Page());
    }

    // ───────────── notices (vault Launchpad.md §5) ─────────────

    /// <summary>Which trial notice is due with this many days left: "3days" with 3 or 2 left (the PC may have been off on the
    /// day), "lastday" on the last day, none otherwise.</summary>
    public static string? TrialStage(int left) => left switch { 3 or 2 => "3days", 1 => "lastday", _ => null };

    /// <summary>The key of the trial notice due for this app now, or null. The trial's start is in it: a new trial gets its
    /// own notices, a restart doesn't repeat one, and a bought copy (no longer "trial" on its card) gets none.</summary>
    public static string? TrialKey(Entry e) =>
        e.Installed?.Trial == true && e.Trial is { } t && e.App.TrialOffer is not null && TrialStage(t.Left) is { } stage
            ? $"trial:{e.App.Id}:{t.Start}:{stage}" : null;

    public void PostNotices()
    {
        var fresh = Updates.Where(e => !State.Seen($"update:{e.App.Id}:{e.Available!.Version}")).ToList();
        if (fresh.Count > 0)
        {
            var free = fresh.Any(e => e.UpdatesHere);
            Notice.Post(new NoticeContent(
                fresh.Select(e => (System.Windows.Media.ImageSource)Theme.Icon(e.App.Id)).ToList(),
                fresh.Count == 1 ? $"{fresh[0].App.Name} {fresh[0].Available!.Version.ToString(3)} is ready" : $"{fresh.Count} updates ready",
                fresh.Count == 1 ? Fallback(fresh[0].Available!.Notes, "A new version is out.") : string.Join(", ", fresh.Select(e => e.App.Name)),
                null, null,
                free ? (fresh.Count == 1 ? "Update" : "Update all") : "Show",
                free ? () => _ = UpdateAll() : () => ShowWindow?.Invoke(),
                () => ShowWindow?.Invoke(),
                "updates:" + string.Join(",", fresh.Select(e => $"{e.App.Id}{e.Available!.Version}")),
                () => fresh.Aggregate(false, (any, e) => State.MarkOnce($"update:{e.App.Id}:{e.Available!.Version}") | any)));
        }

        foreach (var e in Entries)
        {
            if (TrialKey(e) is not { } key || State.Seen(key) || e.Trial is not { } t || e.App.TrialOffer is not { } offer) continue;
            var stage = TrialStage(t.Left);
            Notice.Post(new NoticeContent(
                [Theme.Icon(e.App.Id)],
                stage == "lastday" ? $"Your {e.App.Name} trial ends today" : $"Your {e.App.Name} trial ends in {t.Left} days",
                $"Buy {e.App.Name} now and keep everything as you've set it up.",
                offer.Price, offer.Offer,
                "Buy now — 20% off", () => Installer.Open(e.App.BuyPage),
                null, key, () => State.MarkOnce(key)));
        }
    }

    private static string Fallback(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;
}
