using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _scrollbarTracking, _syncingScrollbar;
    private void GalleryScrollbarLoaded(object sender, RoutedEventArgs e) => StyleGalleryScrollbar();
    private void StyleGalleryScrollbar()
    {
        GalleryScrollbar.ApplyTemplate();
        foreach (var thumb in AssetDescendants(GalleryScrollbar).OfType<Thumb>().Where(part => part.Name == "VerticalThumb")) {
            thumb.Width = thumb.MinWidth = 14; thumb.MinHeight = 48;
            thumb.Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Thumb\"><Border CornerRadius=\"7\" Background=\"{TemplateBinding Background}\"/></ControlTemplate>");
        }
    }
    private void SyncGalleryScrollbar()
    {
        if (_pixelScroll is null || _scrollbarTracking) return;
        var scroll = _pixelScroll.Scroll;
        _syncingScrollbar = true;
        GalleryScrollbar.Maximum = Math.Max(0, scroll.ScrollableHeight);
        GalleryScrollbar.ViewportSize = scroll.ViewportHeight;
        GalleryScrollbar.SmallChange = 48;
        GalleryScrollbar.LargeChange = Math.Max(48, scroll.ViewportHeight * .85);
        GalleryScrollbar.IsEnabled = scroll.ScrollableHeight > 0;
        if (!_pixelScroll.IsAnimating) GalleryScrollbar.Value = scroll.VerticalOffset;
        _syncingScrollbar = false;
    }
    private void GalleryScrollbarChanged(object sender, ScrollEventArgs e)
    {
        if (_syncingScrollbar || _pixelScroll is null) return;
        SeekGalleryScrollbar(e.NewValue, e.ScrollEventType == ScrollEventType.ThumbTrack);
    }
    private void SeekGalleryScrollbar(double value, bool tracking)
    {
        if (_pixelScroll is null) return;
        _scrollbarTracking = tracking;
        _pixelScroll.SeekFromScrollbar(value);
        if (!tracking) SyncGalleryScrollbar();
    }
    private async Task ShowScrollingSettingsAsync()
    {
        if (_pixelScroll is null) return;
        var wheel = new Slider { Minimum = 12, Maximum = 144, StepFrequency = 4, Value = _pixelScroll.WheelDistance };
        var glide = new Slider { Minimum = 0, Maximum = 250, StepFrequency = 10, Value = _pixelScroll.ScrollbarGlideMs };
        var wheelLabel = new TextBlock(); var glideLabel = new TextBlock();
        void Update() {
            _pixelScroll.WheelDistance = wheel.Value; _pixelScroll.ScrollbarGlideMs = glide.Value;
            wheelLabel.Text = $"Mouse wheel · {wheel.Value:0} pixels per notch";
            glideLabel.Text = glide.Value == 0 ? "Scrollbar · Immediate" : $"Scrollbar glide · {glide.Value:0} ms";
        }
        wheel.ValueChanged += (_, _) => Update(); glide.ValueChanged += (_, _) => Update(); Update();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(wheel, "Mouse wheel scroll speed");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(glide, "Scrollbar glide response");
        var panel = new StackPanel { Spacing = 10, Width = 400 };
        panel.Children.Add(wheelLabel); panel.Children.Add(wheel);
        panel.Children.Add(new TextBlock { Text = "Lower values make it easier to browse one moment at a time.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(glideLabel); panel.Children.Add(glide);
        panel.Children.Add(new TextBlock { Text = "The scrollbar keeps its full-library position range. Its glide is independent of wheel speed and always follows the latest position.", TextWrapping = TextWrapping.Wrap });
        await ShowDialogAsync("Scrolling", panel);
        UserPreferences.Set("wheelPixelsPerNotch", wheel.Value);
        UserPreferences.Set("scrollbarGlideMs", glide.Value);
        SyncGalleryScrollbar();
    }
}
