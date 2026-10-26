using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launchpad;

/// <summary>What a notice says and offers.</summary>
public sealed record NoticeContent(IReadOnlyList<ImageSource> Icons, string Title, string Body, string? OldPrice, string? NewPrice,
                                   string Primary, Action OnPrimary, Action? OnOpen, string Key, Func<bool> FirstTime);

/// <summary>
/// Launchpad's own notifications, drawn in its look instead of Windows' toasts (the user's choice, 26.10): a card in the
/// corner above the taskbar with the app's real icon, the text, the offer and two buttons. It never takes the focus,
/// waits while a full-screen game or a presentation runs (or Focus assist says not now), stays while the pointer is
/// over it and leaves on its own after a while.
/// </summary>
public sealed class Notice : Window
{
    private static readonly List<Notice> Open = new();
    private static readonly Queue<NoticeContent> Waiting = new();
    private static DispatcherTimer? _retry;
    /// <summary>Set by --shots: a test run never puts a notice on the user's screen.</summary>
    internal static bool Muted;
    private readonly DispatcherTimer _life;

    /// <summary>Shows the notice now, or as soon as the user can be interrupted. Its FirstTime is asked at that moment
    /// (State.MarkOnce), so a notice that waited through a game is still shown once, not lost and not twice.</summary>
    public static void Post(NoticeContent content)
    {
        if (Muted) return;
        if (Waiting.Any(c => c.Key == content.Key) || Open.Any(n => n._key == content.Key)) return;
        Waiting.Enqueue(content);
        Pump();
    }

    private static void Pump()
    {
        while (Waiting.Count > 0 && CanInterrupt() && Open.Count < 3)
        {
            var content = Waiting.Dequeue();
            if (!content.FirstTime()) continue;
            new Notice(content).Appear();
        }
        if (Waiting.Count > 0 && _retry is null)
        {
            _retry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _retry.Tick += (_, _) => { _retry!.Stop(); _retry = null; Pump(); };
            _retry.Start();
        }
    }

    /// <summary>The notice's card, laid out but never on screen: for the self-test's pictures.</summary>
    internal static FrameworkElement Preview(NoticeContent content)
    {
        var card = (FrameworkElement)new Notice(content).Content;
        ((Window)card.Parent).Content = null;
        card.Margin = new Thickness(0);
        card.Width = 372;
        card.Measure(new Size(392, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        return card;
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS only: not during a full-screen app, a presentation or quiet hours.</summary>
    private static bool CanInterrupt() => SHQueryUserNotificationState(out int state) != 0 || state == 5;

    private readonly string _key;

    private Notice(NoticeContent c)
    {
        _key = c.Key;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 392;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Theme.Text;

        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 14, 14), Margin = new Thickness(10),
            Background = new LinearGradientBrush(Color.FromRgb(0x2a, 0x1a, 0x45), Color.FromRgb(0x18, 0x1a, 0x24), 30),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0x35, 0x78)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45 },
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // the first app's icon; more apps show as a "+N" on its corner, so the notice stays one clean line of art
        var icons = new Grid { Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
        icons.Children.Add(new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = new ImageBrush(c.Icons[0]) });
        if (c.Icons.Count > 1)
            icons.Children.Add(new Border
            {
                Background = Theme.Accent, CornerRadius = new CornerRadius(10), MinWidth = 22, Height = 20, Padding = new Thickness(5, 0, 5, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x1a, 0x36)), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -7, -5),
                Child = new TextBlock { Text = $"+{c.Icons.Count - 1}", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        grid.Children.Add(icons);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var top = new DockPanel();
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Style = Theme.IconButton, ToolTip = "Close" };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Leave();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(new TextBlock { Text = c.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
        text.Children.Add(top);
        text.Children.Add(new TextBlock { Text = c.Body, Foreground = Theme.TextDim, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (c.NewPrice is not null)
        {
            var price = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 13 };
            if (c.OldPrice is not null) price.Inlines.Add(new System.Windows.Documents.Run(c.OldPrice + "  ") { Foreground = Theme.TextMute, TextDecorations = TextDecorations.Strikethrough });
            price.Inlines.Add(new System.Windows.Documents.Run(c.NewPrice) { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Theme.Accent });
            price.Inlines.Add(new System.Windows.Documents.Run("  with 20% off") { Foreground = Theme.TextDim });
            text.Children.Add(price);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var primary = new Button { Content = c.Primary, Style = Theme.PrimaryButton, Padding = new Thickness(16, 7, 16, 7) };
        primary.Click += (_, _) => { c.OnPrimary(); Leave(); };
        var later = new Button { Content = "Later", Style = Theme.GhostButton, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => Leave();
        buttons.Children.Add(primary);
        buttons.Children.Add(later);
        text.Children.Add(buttons);
        grid.Children.Add(text);
        card.Child = grid;
        Content = card;

        if (c.OnOpen is { } open)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button && FindParent<Button>(e.OriginalSource as DependencyObject) is null) { open(); Leave(); } };
        }

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(c.NewPrice is null ? 20 : 40) };
        _life.Tick += (_, _) => { if (!IsMouseOver) Leave(); };
        MouseLeave += (_, _) => { _life.Stop(); _life.Start(); };
        SourceInitialized += (_, _) =>
        {
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never steals the focus from a game or a text being typed, and stays out of Alt+Tab
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
        };
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void Appear()
    {
        Open.Add(this);
        Left = -10000; Top = -10000;
        Show();
        Place();
        var slide = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var shift = new TranslateTransform();
        ((FrameworkElement)Content).RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.XProperty, slide);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        _life.Start();
    }

    /// <summary>Bottom right of the screen the taskbar is on, the newest at the bottom, the others above it.</summary>
    private static void Place()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 6;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var n = Open[i];
            n.Left = area.Right - n.ActualWidth - 6;
            n.Top = bottom - n.ActualHeight;
            bottom = n.Top;
        }
    }

    private bool _leaving;

    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        _life.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { Close(); Open.Remove(this); Place(); Pump(); };
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
