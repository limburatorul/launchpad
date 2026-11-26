using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launchpad;

/// <summary>Theme.xaml's brushes and styles for the windows built in code, and the apps' icons.</summary>
public static class Theme
{
    private static T Get<T>(string key) => (T)Application.Current.Resources[key];
    public static Brush Text => Get<Brush>("Text");
    public static Brush TextDim => Get<Brush>("TextDim");
    public static Brush TextMute => Get<Brush>("TextMute");
    public static Brush Accent => Get<Brush>("Accent");
    public static Brush AccentText => Get<Brush>("AccentText");
    public static Brush Ok => Get<Brush>("Ok");
    public static Brush Warn => Get<Brush>("Warn");
    public static Brush WarnBack => Get<Brush>("WarnBack");
    public static Brush Bad => Get<Brush>("Bad");
    public static Brush Card => Get<Brush>("Card");
    public static Brush Line => Get<Brush>("Line");
    public static Brush UpdateCard => Get<Brush>("UpdateCard");
    public static Style PrimaryButton => Get<Style>("PrimaryButton");
    public static Style GhostButton => Get<Style>("GhostButton");
    public static Style IconButton => Get<Style>("IconButton");
    public static Style LinkButton => Get<Style>("LinkButton");

    /// <summary>The dark title bar Windows draws for dark apps, so every window's frame matches its ground. Asked for
    /// when the window gets its handle, before it shows, so it never flashes light first.</summary>
    public static void DarkTitleBar(Window window) => window.SourceInitialized += (_, _) =>
    {
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref on, sizeof(int));
    };

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Tells a screen reader about something that changed without the focus moving: a download's phase, how it
    /// ended, a notice, an error under a field. With no screen reader running it does nothing.</summary>
    /// <paramref name="passing"/>: a step on the way (a download's phase), which the next one may replace unheard; the
    /// rest (how it ended, a notice, an error) are each said in full.
    public static void Say(UIElement from, string text, bool passing = false) =>
        (UIElementAutomationPeer.FromElement(from) ?? UIElementAutomationPeer.CreatePeerForElement(from))?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            passing ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.ImportantAll, text, passing ? "launchpad-phase" : "launchpad");

    private static readonly Dictionary<string, BitmapSource> Colour = new(), Grey = new();

    /// <summary>The app's real icon (Icons\&lt;id&gt;.png, from the site's set), built into the exe.</summary>
    public static BitmapSource Icon(string id)
    {
        if (Colour.TryGetValue(id, out var cached)) return cached;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Icons/{id}.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return Colour[id] = image;
    }

    /// <summary>The icon faded to grey for an app that isn't installed; its alpha is kept, so the rounded corners stay.</summary>
    public static BitmapSource GreyIcon(string id)
    {
        if (Grey.TryGetValue(id, out var cached)) return cached;
        var source = new FormatConvertedBitmap(Icon(id), PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[h * stride];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // 15% of the colour left in, and darker: still recognisable, plainly "not yours yet"
            double y = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Clamp((y * 0.85 + pixels[i + c] * 0.15) * 0.72, 0, 255);
        }
        var grey = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        grey.Freeze();
        return Grey[id] = grey;
    }
}
