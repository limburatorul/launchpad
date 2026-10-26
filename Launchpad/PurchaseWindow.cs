using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launchpad.Services;

namespace Launchpad;

/// <summary>
/// Asked once per paid app, when its purchase isn't shared yet (an older copy, or bought before contract v4): the link
/// from the purchase email and the address it was bought with. Saved as data\&lt;id&gt;.purchase.json, which the app reads
/// too, so neither asks again.
/// </summary>
public sealed class PurchaseWindow : Window
{
    private readonly TextBox _link = new(), _email = new();
    private readonly TextBlock _error = new() { Foreground = Theme.Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Product _app;
    public Bought? Result { get; private set; }

    public static Bought? Ask(Product app, Window? owner)
    {
        var window = new PurchaseWindow(app);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Result;
    }

    internal PurchaseWindow(Product app)
    {
        _app = app;
        Title = $"Update {app.Name}";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico"));
        SetResourceReference(BackgroundProperty, "Ground");
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = new Image { Source = Theme.Icon(app.Id), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = $"Install {app.Name}'s update", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
        titles.Children.Add(new TextBlock { Text = "Updates come with your purchase", Foreground = Theme.TextDim });
        head.Children.Add(titles);
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = $"Paste the link from your purchase email and the address you bought {app.Name} with. Launchpad keeps them with your other Protagonist Labs settings on this PC, and {app.Name} uses them too, so you won't be asked again.",
            Foreground = Theme.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });
        body.Children.Add(Field("Link from your purchase email", _link, "https://protagonistlabs.app/…?txn=txn_…"));
        body.Children.Add(Field("Email address", _email, "you@example.com"));
        body.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = Theme.GhostButton, IsCancel = true };
        var ok = new Button { Content = "Update", Style = Theme.PrimaryButton, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var lost = new Button { Content = "Lost the email? Get the link again", Style = Theme.LinkButton, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        lost.Click += (_, _) => Installer.Open($"https://protagonistlabs.app/{app.Id}/?utm_source=launchpad&utm_medium=app&utm_campaign=lost-purchase");
        var foot = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(lost);
        body.Children.Add(foot);
        Content = body;
        _link.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;
        _email.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => _link.Focus();
    }

    /// <summary>The window's content laid out off-screen, for the self-test's pictures.</summary>
    internal static FrameworkElement Preview(Product app)
    {
        var content = (FrameworkElement)new PurchaseWindow(app).Content;
        ((Window)content.Parent).Content = null;
        var ground = new Border { Child = content, Width = 460, Background = (Brush)Application.Current.Resources["Ground"] };
        ground.Measure(new Size(460, double.PositiveInfinity));
        ground.Arrange(new Rect(ground.DesiredSize));
        return ground;
    }

    private static UIElement Field(string label, TextBox box, string hint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var caption = new Label { Content = label, Target = box, Foreground = Theme.TextDim, Padding = new Thickness(0, 0, 0, 4), FontSize = 12 };
        panel.Children.Add(caption);
        box.Padding = new Thickness(8, 6, 8, 6);
        box.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1c, 0x24));
        box.Foreground = Theme.Text;
        box.CaretBrush = Theme.Text;
        box.BorderBrush = Theme.Line;
        box.ToolTip = hint;
        AutomationProperties.SetName(box, label);
        panel.Children.Add(box);
        return panel;
    }

    private void Save()
    {
        var txn = Purchase.TxnFrom(_link.Text.Trim());
        var email = _email.Text.Trim();
        string? problem = txn is null ? "There's no order id in that. The link in your purchase email contains one (txn_…)."
            : !Purchase.ValidEmail(email) ? "Enter the address you bought with." : null;
        if (problem is not null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        // kept now; if order.php doesn't know it, the install forgets it again and says so on the card
        try { Purchase.Write(_app.Id, txn!, email); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { AppLog.Write($"Couldn't save the purchase for {_app.Name}: {ex.Message}"); }
        Result = new Bought(txn, email, null);
        Close();
    }
}
