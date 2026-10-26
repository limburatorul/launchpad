using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    public MainWindow(Hub hub, App app)
    {
        InitializeComponent();
        _hub = hub;
        VersionText.Text = $"Launchpad {Updater.Current.ToString(3)}";
        StartWithWindows.IsChecked = State.StartWithWindows;
        StartWithWindows.Click += (_, _) => State.StartWithWindows = StartWithWindows.IsChecked == true;
        CheckNow.Click += async (_, _) => await app.CheckNow();
        WhatsNew.Click += (_, _) => WhatsNewWindow.Open();
        foreach (var filter in new[] { ShowAll, ShowUpdates, ShowInstalled, ShowMissing }) filter.Checked += (_, _) => Render();
        HeroButton.Click += async (_, _) => await _hub.UpdateAll();
        _hub.Changed += Render;
        _clock.Tick += (_, _) => RenderChecked();
        _clock.Start();
        Closing += (_, e) => { e.Cancel = true; Hide(); };      // the tray keeps Launchpad; Quit is in its menu
        SourceInitialized += (_, _) =>
        {
            // the dark title bar Windows draws for dark apps, so the frame matches the ground
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };
        Render();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void RenderChecked() =>
        Checked.Text = _hub.Checking ? "Checking for updates…"
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
        var installed = _hub.InstalledApps.ToList();
        var missing = _hub.Missing.ToList();
        CountAll.Text = _hub.Entries.Count.ToString();
        CountUpdates.Text = updates.Count.ToString();
        UpdatesBadge.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountInstalled.Text = installed.Count.ToString();
        CountMissing.Text = missing.Count.ToString();
        RenderChecked();
        RenderHero(updates);

        Sections.Children.Clear();
        if (ShowUpdates.IsChecked == true)
        {
            if (updates.Count == 0) Sections.Children.Add(Empty("Everything is up to date", "Launchpad looks for new versions every 6 hours, and tells you when one is out."));
            else AddSection("Updates", updates);
        }
        if (ShowAll.IsChecked == true || ShowInstalled.IsChecked == true)
        {
            if (installed.Count == 0) Sections.Children.Add(Empty("No Protagonist Labs apps yet", "Pick one below: free apps install in one click, paid apps start a 14-day trial."));
            else AddSection("Installed", installed);
        }
        if (ShowAll.IsChecked == true || ShowMissing.IsChecked == true)
            if (missing.Count > 0) AddSection("Get more", missing, "free apps install in one click, paid apps start a 14-day trial");
    }

    private void RenderHero(List<Entry> updates)
    {
        Hero.Visibility = updates.Count > 0 && ShowMissing.IsChecked != true && ShowInstalled.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        if (updates.Count == 0) return;
        HeroTitle.Text = updates.Count == 1 ? "1 update ready" : $"{updates.Count} updates ready";
        HeroNotes.Text = string.Join(" · ", updates.Take(3).Select(e =>
            $"{e.App.Name} {e.Available!.Version.ToString(3)}" + (string.IsNullOrWhiteSpace(e.Available.Notes) ? "" : $" — {Short(e.Available.Notes)}")))
            + (updates.Where(e => !e.UpdatesHere).Select(e => e.App.Name).ToList() is { Count: > 0 } self
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
        HeroButton.Content = here == updates.Count ? "Update all" : $"Update {here} {(here == 1 ? "app" : "apps")}";
        HeroButton.IsEnabled = !updates.Any(e => e.Busy is not null);
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
            ToolTip = e.App.Blurb,
        };
        var layer = new Grid();
        var stack = new StackPanel();
        layer.Children.Add(stack);

        var icon = new Image { Source = installed ? Theme.Icon(e.App.Id) : Theme.GreyIcon(e.App.Id), Width = 60, Height = 60, Margin = new Thickness(0, 0, 0, 9) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        if (!installed)
        {
            icon.Cursor = Cursors.Hand;
            icon.MouseLeftButtonUp += (_, _) => Installer.Open(e.App.Page());
        }
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = e.App.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = installed ? Theme.Text : Theme.TextDim,
        });

        var (status, brush) = Status(e);
        stack.Children.Add(new TextBlock { Text = status, Foreground = brush, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = status });

        if (e.Busy is not null)
        {
            var track = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 10) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x2f, 0x33, 0x42)) });
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            fill.Width = e.Progress is { } p ? Math.Max(6, 114 * p) : 114;
            if (e.Progress is null) fill.Opacity = 0.55;
            track.Children.Add(fill);
            stack.Children.Add(track);
        }
        else stack.Children.Add(Action(e));

        if (e.OfferDays && e.App.TrialOffer is not null)
        {
            var buy = new Button { Content = "Buy · 20% off", Style = Theme.LinkButton, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(buy, $"Buy {e.App.Name} with 20% off");
            buy.Click += (_, _) => Installer.Open(e.App.BuyPage);
            stack.Children.Add(buy);
        }

        if (e.HasUpdate)
            layer.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xff, 0x2b, 0xd6)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -1, 0) });
        if (e.Installed?.Trial == true)
            layer.Children.Add(new Border
            {
                Background = Theme.WarnBack, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -8, 0, 0),
                Child = new TextBlock { Text = TrialClock.Label(e.Trial?.Left), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Warn },
            });
        card.Child = layer;
        AutomationProperties.SetName(card, $"{e.App.Name}, {status}");
        return card;
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
            button = new Button { Content = "Buy · 20% off", Style = Theme.PrimaryButton };
            button.Click += (_, _) => Installer.Open(e.App.BuyPage);
            AutomationProperties.SetName(button, $"Buy {e.App.Name} with 20% off");
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
            // nothing known to install yet (offline, or no check so far): the app's page
            button = new Button { Content = "See page", Style = Theme.GhostButton };
            button.Click += (_, _) => Installer.Open(e.App.Page());
            AutomationProperties.SetName(button, $"{e.App.Name} page");
        }
        button.Padding = new Thickness(0, 6, 0, 6);
        button.FontSize = 12;
        return button;
    }
}
