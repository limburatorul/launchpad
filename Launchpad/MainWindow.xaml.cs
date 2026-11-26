using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Launchpad.Services;

namespace Launchpad;

/// <summary>
/// The window from the approved mockup (mockup/launchpad.html): filters on the left, a banner while there are updates,
/// then the installed apps and the ones still to get, each a card with its real icon. Closing it leaves Launchpad in the tray.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Hub _hub;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, (TextBlock Status, Border Fill)> _bars = [];
    /// <summary>The cards' buttons by what they are ("reel", "reel:about", "reel:buy"), built again with the cards.</summary>
    private readonly Dictionary<string, Button> _buttons = [];
    /// <summary>The card button that had the keyboard focus last. Every Render builds the cards again, and without this
    /// the focus fell back to the window: after each Update, and after every check, a keyboard user started from the top.</summary>
    private string? _focus;
    /// <summary>Per app, the phase or the outcome a screen reader was last told.</summary>
    private readonly Dictionary<string, object> _said = [];
    /// <summary>The width of a card's text: 136 wide, less its border and its padding.</summary>
    internal const double Column = 114;

    public MainWindow(Hub hub, App app)
    {
        InitializeComponent();
        _hub = hub;
        VersionText.Text = $"Launchpad {Updater.Current.ToString(3)}";
        StartWithWindows.IsChecked = State.StartWithWindows;
        StartWithWindows.Click += (_, _) => State.StartWithWindows = StartWithWindows.IsChecked == true;
        CheckNow.Click += async (_, _) => await app.CheckNow();
        WhatsNew.Click += (_, _) => WhatsNewWindow.Open();
        Pizza.Click += (_, _) => Installer.Open("https://buymeacoffee.com/protagonistlabs");
        Nudge.Attach(Pizza, this);
        TeaserIcon.Source = Theme.Icon("shelf");
        TeaserLink.Click += (_, _) => Installer.Open(Catalog.Find("shelf")!.Page("teaser"));
        foreach (var filter in new[] { ShowAll, ShowUpdates, ShowInstalled, ShowMissing }) filter.Checked += (_, _) => Render();
        HeroButton.Click += async (_, _) => await _hub.UpdateAll();
        _hub.Changed += Render;
        _hub.Progressed += e => { Announce(e); if (_bars.TryGetValue(e.App.Id, out var bar)) Draw(e, bar.Status, bar.Fill); else Render(); };
        _clock.Tick += (_, _) => RenderChecked();
        _clock.Start();
        Closing += (_, e) =>
        {
            e.Cancel = true; Hide();      // the tray keeps Launchpad; Quit is in its menu
            // said once, the first time: the X looks like Quit, and the icon may sit in the taskbar's overflow
            if (!App.Quitting)
                Notice.Post(new NoticeContent([Theme.Icon("launchpad")], "Launchpad is still running",
                    "It stays in the notification area to keep your apps up to date. Quit is in its icon's menu.",
                    null, null, "Got it", () => { }, null, "tray-hint", () => State.MarkOnce("tray-hint"), Later: false));
        };
        // for RestoreFocus. The window itself getting the focus is what WPF does when the focused button is rebuilt, not a choice.
        GotKeyboardFocus += (_, e) => { if (e.NewFocus is FrameworkElement f && f != this) _focus = (f as Button)?.Tag as string; };
        Theme.DarkTitleBar(this);
        Render();
    }

    private void RenderChecked() =>
        Checked.Text = _hub.Checking ? "Checking for updates…"
            : Feeds.LimitedUntil is { } until ? $"GitHub is limiting checks from this connection. Trying again at {until.ToLocalTime():HH:mm}."
            : _hub.Offline ? "Couldn't reach the update servers. Trying again in a few minutes."
            : State.LastCheck is { } last ? $"Checked {Ago(DateTime.UtcNow - last)}" : "Not checked yet";

    private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now"
        : t.TotalMinutes < 2 ? "a minute ago"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} minutes ago"
        : t.TotalHours < 2 ? "an hour ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} hours ago" : $"{(int)t.TotalDays} days ago";

    private void Render()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Render); return; }
        var updates = _hub.Updates.ToList();
        // the ones with an update lead, as in the mockup; the sort is stable, so the rest keep the catalogue's order
        var installed = _hub.InstalledApps.OrderByDescending(e => e.HasUpdate).ToList();
        var missing = _hub.Missing.ToList();
        CountAll.Text = _hub.Entries.Count.ToString();
        CountUpdates.Text = updates.Count.ToString();
        UpdatesBadge.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountInstalled.Text = installed.Count.ToString();
        CountMissing.Text = missing.Count.ToString();
        // a filter's content is a panel (its label and its count), and a panel gives a screen reader no name to read
        AutomationProperties.SetName(ShowAll, $"All apps, {_hub.Entries.Count}");
        AutomationProperties.SetName(ShowUpdates, $"Updates, {updates.Count}");
        AutomationProperties.SetName(ShowInstalled, $"Installed, {installed.Count}");
        AutomationProperties.SetName(ShowMissing, $"Not installed, {missing.Count}");
        RenderChecked();
        RenderHero(updates);
        Teaser.Visibility = ShowAll.IsChecked == true && _hub.Entries.FirstOrDefault(e => e.App.Id == "shelf")?.Available?.Version is not { Major: >= 6 }
            ? Visibility.Visible : Visibility.Collapsed;

        Sections.Children.Clear();
        _bars.Clear();
        _buttons.Clear();
        if (ShowUpdates.IsChecked == true)
        {
            if (updates.Count > 0) AddSection("Updates", updates);
            else Sections.Children.Add(_hub.Offline
                ? Empty("Couldn't check for updates", "Launchpad couldn't reach the update servers. It keeps trying, and Check now tries at once.")
                : Empty("Everything is up to date", "Launchpad looks for new versions every 6 hours, and tells you when one is out."));
        }
        if (ShowAll.IsChecked == true || ShowInstalled.IsChecked == true)
        {
            // "below" is only true under All apps, where the rest of the apps follow
            if (installed.Count == 0) Sections.Children.Add(Empty("No Protagonist Labs apps yet", (ShowAll.IsChecked == true ? "Pick one below" : "Pick one under Not installed")
                + ": free apps install in one click, paid apps start a 14-day trial."));
            else AddSection("Installed", installed);
        }
        if (ShowAll.IsChecked == true || ShowMissing.IsChecked == true)
        {
            if (missing.Count > 0) AddSection("Get more", missing, "free apps install in one click, paid apps start a 14-day trial");
            else if (ShowMissing.IsChecked == true)
                Sections.Children.Add(Empty("You have every app", $"All {_hub.Entries.Count} Protagonist Labs apps are installed. New ones appear here when they come out."));
        }
        foreach (var e in _hub.Entries) Announce(e);
        RestoreFocus();
    }

    /// <summary>Gives the keyboard focus back to the card button that had it before the cards were built again. A busy
    /// card has no button: the focus returns to it when its button does, unless the user has moved on by then. Only for
    /// someone using the keyboard: focusing a button scrolls it into view, which a mouse user never asked for.</summary>
    private void RestoreFocus()
    {
        if (_focus is not { } was || !IsActive || InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
        if (!_buttons.TryGetValue(was, out var button) && !_buttons.TryGetValue(was.Split(':')[0], out button)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsActive && _focus == was) button.Focus(); });
    }

    /// <summary>What a screen reader is told about an app: each phase once (not every percent), and how it ended.</summary>
    private void Announce(Entry e)
    {
        object? now = e.Busy is { } busy ? busy.Split(' ')[0] : e.Last;
        if (now is null) { _said.Remove(e.App.Id); return; }
        if (_said.TryGetValue(e.App.Id, out var said) && Equals(said, now)) return;
        _said[e.App.Id] = now;
        Theme.Say(Checked, $"{e.App.Name}: {(now as Outcome)?.Message ?? e.Busy}", passing: now is not Outcome);
    }

    private void RenderHero(List<Entry> updates)
    {
        Hero.Visibility = updates.Count > 0 && ShowMissing.IsChecked != true && ShowInstalled.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        if (updates.Count == 0) return;
        HeroTitle.Text = updates.Count == 1 ? "1 update ready" : $"{updates.Count} updates ready";
        // Shelf with a key whose Update Pass doesn't cover the release isn't named among the ones that update themselves:
        // it offers a renewal, as its card says
        HeroNotes.Text = string.Join(" · ", updates.Take(3).Select(e =>
            $"{e.App.Name} {e.Available!.Version.ToString(3)}" + (e.Note.Length == 0 ? "" : $" — {Short(e.Note)}")))
            + (updates.Where(e => !e.UpdatesHere && !(e.App.Id == "shelf" && Purchase.ShelfUntil(Purchase.Read("shelf")?.Licence) is not null)).Select(e => e.App.Name).ToList() is { Count: > 0 } self
                ? $"  ·  {string.Join(", ", self)} {(self.Count == 1 ? "updates itself when you open it" : "update themselves when you open them")}." : "");
        HeroIcons.Children.Clear();
        var shown = updates.Take(4).ToList();
        for (int i = shown.Count - 1; i >= 0; i--)
            HeroIcons.Children.Add(new Border
            {
                Width = 62, Height = 62, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(3),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x18, 0x4d)), Background = new ImageBrush(Theme.Icon(shown[i].App.Id)),
                Margin = new Thickness(i * 46, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            });
        int here = updates.Count(e => e.UpdatesHere);
        HeroButton.Visibility = here > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroButton.Content = Hub.UpdateLabel(here, updates.Count);
        HeroButton.IsEnabled = updates.Any(e => e.UpdatesHere && e.Busy is null);
    }

    private static string Short(string text) => text.Length <= 60 ? text : text[..57].TrimEnd(' ', ',', '.', ':') + "…";

    private void AddSection(string title, List<Entry> entries, string? hint = null)
    {
        var header = new TextBlock { Margin = new Thickness(0, 18, 0, 10), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextDim };
        header.Inlines.Add(title);
        header.Inlines.Add(new System.Windows.Documents.Run($"   {entries.Count}{(hint is null ? "" : " · " + hint)}") { FontWeight = FontWeights.Normal, Foreground = Theme.TextMute, FontSize = 12 });
        Sections.Children.Add(header);
        var wrap = new WrapPanel();
        foreach (var e in entries) wrap.Children.Add(Card(e));
        Sections.Children.Add(wrap);
    }

    private static UIElement Empty(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 24, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, Foreground = Theme.TextDim, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private UIElement Card(Entry e)
    {
        bool installed = e.Installed is not null;
        var card = new Border
        {
            Width = 136, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 14, 10, 12),
            BorderThickness = new Thickness(1),
            Background = e.HasUpdate ? Theme.UpdateCard : Theme.Card,
            BorderBrush = e.HasUpdate ? new SolidColorBrush(Color.FromRgb(0x7b, 0x5c, 0xff)) : Theme.Line,
        };
        var layer = new Grid();
        var stack = new StackPanel();
        layer.Children.Add(stack);

        var icon = new Image { Source = installed ? Theme.Icon(e.App.Id) : Theme.GreyIcon(e.App.Id), Width = 60, Height = 60, Margin = new Thickness(0, 0, 0, 9) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = e.App.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = installed ? Theme.Text : Theme.TextDim,
        });

        stack.Children.Add(Blurb(e.App.Blurb));

        var (status, brush) = Status(e);
        // a failure is the one status that has to be read whole, on two or three lines; every other one fits on its line
        bool failed = e.Busy is null && e.Last is { Ok: false };
        var statusText = new TextBlock
        {
            Text = status, Foreground = brush, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10), ToolTip = status,
            TextTrimming = failed ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = failed ? TextWrapping.Wrap : TextWrapping.NoWrap, TextAlignment = TextAlignment.Center,
        };
        stack.Children.Add(statusText);

        if (e.Busy is not null)
        {
            var track = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 10) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x2f, 0x33, 0x42)) });
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            Draw(e, statusText, fill);
            _bars[e.App.Id] = (statusText, fill);
            track.Children.Add(fill);
            stack.Children.Add(track);
        }
        else
        {
            var action = Action(e);
            Keep(action, e.App.Id);
            // what the card shows beside its button: a screen reader reads it with the button, which is where Tab stops
            AutomationProperties.SetHelpText(action, Spoken(e, status) + (action.ToolTip is string why ? ". " + why : ""));
            stack.Children.Add(action);
            if (!installed)
            {
                // the icon does what the button says (Install / Try free); the app's page is the small About link below
                icon.Cursor = Cursors.Hand;
                icon.MouseLeftButtonUp += (_, _) => action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        }

        if (!installed)
        {
            var about = new Button { Content = "About ↗", Style = Theme.LinkButton, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(about, $"{e.App.Name}'s page");
            Keep(about, e.App.Id + ":about");
            about.Click += (_, _) => Installer.Open(e.App.Page());
            stack.Children.Add(about);
        }

        if (e.OfferDays && e.App.TrialOffer is not null)
        {
            var buy = new Button { Content = "Buy · 20% off", Style = Theme.LinkButton, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(buy, $"Buy {e.App.Name} with 20% off");
            Keep(buy, e.App.Id + ":buy");
            buy.Click += (_, _) => Installer.Open(e.App.BuyPage);
            stack.Children.Add(buy);
        }

        if (e.HasUpdate)
            layer.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xff, 0x2b, 0xd6)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -1, 0) });
        // while the trial runs; once it has ended the status line says so, and one "Trial ended" on a card is enough
        if (e.Installed?.Trial == true && !e.TrialEnded)
            layer.Children.Add(new Border
            {
                Background = Theme.WarnBack, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -8, 0, 0),
                Child = new TextBlock { Text = TrialClock.Label(e.Trial?.Left), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Warn },
            });
        card.Child = layer;
        return card;
    }

    private void Keep(Button button, string what)
    {
        button.Tag = what;
        _buttons[what] = button;
    }

    /// <summary>What the app does, in two lines on every card, so nobody has to go to the site to find out. The self-test
    /// measures every description as this draws it: a third line pushes that card's button below the others in its row.</summary>
    internal static TextBlock Blurb(string text) => new()
    {
        Text = text, Foreground = Theme.TextDim, FontSize = 11, LineHeight = 14, MinHeight = 28, Margin = new Thickness(0, 3, 0, 4),
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>The card's description and status as a screen reader should say them: the marks the eye reads ("✓",
    /// "→", "·") in words, and the trial's days, which are only on the badge.</summary>
    private static string Spoken(Entry e, string status)
    {
        var said = status.Replace("✓ ", "Installed, version ").Replace(" → ", " to ").Replace(" · ", ", ");
        if (e.HasUpdate && e.Last is not { Ok: false }) said = "Update from version " + said;
        if (e.Installed?.Trial == true && !e.TrialEnded) said += ", " + TrialClock.Label(e.Trial?.Left).Replace(" · ", ", ");
        return $"{e.App.Blurb}. {said}";
    }

    private static void Draw(Entry e, TextBlock status, Border fill)
    {
        status.Text = e.Busy; status.ToolTip = e.Busy;
        fill.Width = e.Progress is { } p ? Math.Max(6, Column * p) : Column;
        fill.Opacity = e.Progress is null ? 0.55 : 1;
    }

    private static (string, Brush) Status(Entry e)
    {
        if (e.Busy is not null) return (e.Busy, Theme.AccentText);
        if (e.Last is { Ok: false } failed) return (failed.Message, Theme.Bad);
        if (e.Installed is { } i)
        {
            if (e.TrialEnded) return ("Trial ended", Theme.Warn);
            if (e.HasUpdate) return ($"{i.Version!.ToString(3)} → {e.Available!.Version.ToString(3)}", Theme.AccentText);
            return i.Version is { } v ? ($"✓ {v.ToString(3)}", Theme.Ok) : ("Installed", Theme.Ok);
        }
        return e.App.Paid ? ("Paid · 14-day trial", Theme.TextMute) : ("Free", Theme.TextMute);
    }

    private Button Action(Entry e)
    {
        Button button;
        if (e.Installed is not null && e.TrialEnded)
        {
            // HikCheck has no end-of-trial code: a plain Buy
            button = new Button { Content = e.App.TrialOffer is null ? "Buy" : "Buy · 20% off", Style = Theme.PrimaryButton };
            button.Click += (_, _) => Installer.Open(e.App.BuyPage);
            AutomationProperties.SetName(button, e.App.TrialOffer is null ? $"Buy {e.App.Name}" : $"Buy {e.App.Name} with 20% off");
        }
        else if (e.Installed is not null && e.UpdatesHere)
        {
            button = new Button { Content = "Update", Style = Theme.PrimaryButton };
            button.Click += async (_, _) => await _hub.Install([e]);
            AutomationProperties.SetName(button, $"Update {e.App.Name}");
        }
        else if (e.Installed is not null && e.HasUpdate)
        {
            // Launchpad can't install this one: Shelf past its Update Pass, or a paid app whose site feed doesn't publish
            // its bought installer's checksum yet. The app's own updater takes it from here.
            button = new Button { Content = "Open to update", Style = Theme.PrimaryButton };
            button.ToolTip = e.App.Id == "shelf" ? "Your Update Pass doesn't cover this version: Shelf shows how to renew it"
                                                 : $"Opens {e.App.Name}, which offers its update a few seconds after it starts";
            button.Click += (_, _) => _hub.Open(e);
            AutomationProperties.SetName(button, $"Open {e.App.Name} to update it");
        }
        else if (e.Installed is not null)
        {
            button = new Button { Content = "Open", Style = Theme.GhostButton };
            button.Click += (_, _) => _hub.Open(e);
            AutomationProperties.SetName(button, $"Open {e.App.Name}");
        }
        else if (e.CanInstall)
        {
            button = new Button { Content = e.App.Paid ? "Try free" : "Install", Style = Theme.GhostButton };
            button.Click += async (_, _) => await _hub.Install([e]);
            AutomationProperties.SetName(button, $"{(e.App.Paid ? "Try" : "Install")} {e.App.Name}");
        }
        else
        {
            // nothing known yet (the first run, before any check has come back): check now, then install; only when
            // there is still nothing to install (offline) does the app's page open instead
            button = new Button { Content = e.App.Paid ? "Try free" : "Install", Style = Theme.GhostButton };
            button.Click += async (_, _) =>
            {
                await _hub.CheckFor(e);      // waits for a check already running too: it used to go on without its answer, to the page
                if (e.CanInstall) await _hub.Install([e]);
                else Installer.Open(e.App.Page());
            };
            AutomationProperties.SetName(button, $"{(e.App.Paid ? "Try" : "Install")} {e.App.Name}");
        }
        button.Padding = new Thickness(0, 6, 0, 6);
        button.FontSize = 12;
        return button;
    }
}
