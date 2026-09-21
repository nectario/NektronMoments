using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NektronMoments.Models;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _settingsOpen, _restoringSettingsSelection;
    private object? _lastLibraryNavigation;
    private static TextBlock SettingsText(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static StackPanel SettingsSection(string heading)
    {
        var section = new StackPanel { Spacing = 16, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        section.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return section;
    }
    private void PresentSettings(StackPanel processing, TextBlock estimate)
    {
        if (!_settingsOpen && !ReferenceEquals(Navigation.SelectedItem, Navigation.SettingsItem)) _lastLibraryNavigation = Navigation.SelectedItem;
        SuspendGalleryMotion(); Viewer.Stop(); _thumbnailPrefetch?.Cancel();
        CancelDisplayWarm(); _warmGeneration = -1;
        _settingsOpen = true;
        _restoringSettingsSelection = true;
        try { Navigation.SelectedItem = Navigation.SettingsItem; }
        finally { _restoringSettingsSelection = false; }
        DetailsSplit.Visibility = Visibility.Collapsed;
        SettingsHost.Children.Clear(); SettingsHost.Visibility = Visibility.Visible;
        var page = new Grid { Padding = new Thickness(24, 18, 24, 16), RowSpacing = 14 };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "Settings", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var back = new Button { Content = "Back to library", HorizontalAlignment = HorizontalAlignment.Right };
        back.Click += (_, _) => CloseSettings(); header.Children.Add(back); page.Children.Add(header);
        // Pricing is deliberately outside the tab/scroll content. It stays visible
        // while customizing any section, without opening a link or expander.
        var pricing = new StackPanel { Name = "SettingsPricing", Spacing = 5 };
        pricing.Children.Add(new TextBlock { Text = "AI pricing · USD", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        pricing.Children.Add(SettingsText(AiProcessingOptions.RateSummary, 13));
        estimate.FontSize = 12; pricing.Children.Add(estimate);
        pricing.Children.Add(new HyperlinkButton { Content = "Official pricing details", NavigateUri = new Uri("https://developers.openai.com/api/docs/pricing"), Padding = new Thickness(0), MinHeight = 24 });
        Grid.SetRow(pricing, 1); page.Children.Add(pricing);
        var sections = new Pivot { Name = "SettingsSections" };
        sections.Items.Add(new PivotItem { Header = "AI & processing", Content = SettingsScroll(processing) });
        sections.Items.Add(new PivotItem { Header = "Appearance", Content = SettingsScroll(BuildAppearanceSettings()) });
        sections.Items.Add(new PivotItem { Header = "Browsing", Content = SettingsScroll(BuildBrowsingSettings()) });
        Grid.SetRow(sections, 2); page.Children.Add(sections);
        SettingsHost.Children.Add(page);
    }
    private static ScrollViewer SettingsScroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 14, 16, 16) };
    private StackPanel BuildAppearanceSettings()
    {
        var section = SettingsSection("Appearance");
        var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "Light", "Dark" }, SelectedIndex = ActualTheme == ElementTheme.Dark ? 1 : 0, MinWidth = 240 };
        theme.SelectionChanged += (_, _) => {
            if ((theme.SelectedIndex == 1) != (ActualTheme == ElementTheme.Dark)) ChangeTheme(theme, new RoutedEventArgs());
        };
        section.Children.Add(theme);
        var sizes = new ComboBox { Header = "Thumbnail preset", ItemsSource = new[] { "Small", "Medium", "Large", "X-large" },
            SelectedIndex = Array.IndexOf(new[] { 144d, 240d, 336d, 432d }, _thumbnailSize), MinWidth = 240 };
        sizes.SelectionChanged += (_, _) => { if (sizes.SelectedIndex >= 0) ApplyThumbnailSize(new[] { 144d, 240d, 336d, 432d }[sizes.SelectedIndex], true); };
        section.Children.Add(sizes);
        section.Children.Add(SettingsText("Use the ribbon slider for a custom thumbnail size. Your library position and decoded-image cache are kept while Settings is open."));
        return section;
    }
    private StackPanel BuildBrowsingSettings()
    {
        var section = SettingsSection("Browsing & motion");
        var label = SettingsText("");
        var wheel = new Slider { Minimum = 12, Maximum = 144, StepFrequency = 4, Width = 340,
            Value = _pixelScroll?.WheelDistance ?? UserPreferences.Number("wheelPixelsPerNotch", 64) };
        void UpdateWheel() { if (_pixelScroll is not null) _pixelScroll.WheelDistance = wheel.Value; label.Text = $"Mouse wheel · {wheel.Value:0} pixels per notch"; }
        UpdateWheel();
        wheel.ValueChanged += async (_, _) => {
            UpdateWheel();
            try { await UserPreferences.SetAsync("wheelPixelsPerNotch", wheel.Value); }
            catch (Exception) { StatusText.Text = "Wheel speed changed, but could not be saved."; }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(wheel, "Mouse wheel scroll speed");
        section.Children.Add(label); section.Children.Add(wheel);
        section.Children.Add(SettingsText("Scrollbar dragging follows native Windows input at the available display cadence, with no application 60-FPS cap. During a drag we request the high-refresh compositor mode where supported. Actual FPS depends on Windows, the display, GPU and workload."));
        var hide = new CheckBox { Content = "Hide screenshots and text captures", IsChecked = _hideScreenshots };
        hide.Checked += (_, _) => HideScreenshotsCheck.IsChecked = true;
        hide.Unchecked += (_, _) => HideScreenshotsCheck.IsChecked = false;
        section.Children.Add(hide);
        var reset = new Button { Content = "Reset Order" }; reset.Click += ResetOrderClick; section.Children.Add(reset);
        section.Children.Add(SettingsText("Reset Order clears the saved arrangement for the current view and restores newest first. Original files are never moved."));
        return section;
    }
    private void CloseSettings(bool restoreSelection = true)
    {
        if (!_settingsOpen) return;
        _settingsOpen = false; SettingsHost.Visibility = Visibility.Collapsed;
        SettingsHost.Children.Clear(); DetailsSplit.Visibility = Visibility.Visible;
        if (restoreSelection) {
            _restoringSettingsSelection = true;
            try { Navigation.SelectedItem = _lastLibraryNavigation ?? LibraryNav; }
            finally { _restoringSettingsSelection = false; }
        }
        QueueGalleryWork();
    }
}
