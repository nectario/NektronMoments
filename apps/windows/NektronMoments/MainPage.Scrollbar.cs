using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    // Read-only access to the GridView's own bar. WinUI owns its range, value,
    // pointer capture and viewport connection, exactly as in version 0.1.1.
    private ScrollBar? _galleryScrollbar;
    private Thumb? _galleryThumb;
    private ScrollBar GalleryScrollbar => _galleryScrollbar ??
        throw new InvalidOperationException("Native gallery scrollbar is not ready.");
    private Thumb? GalleryThumb => _galleryThumb;

    private int GetGalleryColumns(double itemWidth)
    {
        var width = (Gallery.ItemsPanelRoot as FrameworkElement)?.ActualWidth ?? 0;
        if (width <= 0) width = Gallery.ActualWidth - Gallery.Padding.Left - Gallery.Padding.Right;
        return Math.Max(1, (int)Math.Floor(Math.Max(0, width) / Math.Max(1, itemWidth)));
    }

    private async Task ShowScrollingSettingsAsync()
    {
        if (_pixelScroll is null) return;
        var wheel = new Slider { Minimum = 12, Maximum = 144, StepFrequency = 4, Value = _pixelScroll.WheelDistance };
        var label = new TextBlock();
        void Update() {
            _pixelScroll.WheelDistance = wheel.Value;
            label.Text = $"Mouse wheel · {wheel.Value:0} pixels per notch";
        }
        wheel.ValueChanged += (_, _) => Update(); Update();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(wheel, "Mouse wheel scroll speed");
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(label); panel.Children.Add(wheel);
        panel.Children.Add(new TextBlock {
            Text = "The scrollbar uses direct Windows dragging, as in version 0.1.1. Photos follow the thumb without added glide or precision modes. Wheel speed and smooth motion are independent.",
            TextWrapping = TextWrapping.Wrap,
        });
        await ShowDialogAsync("Scrolling", panel);
        try { await UserPreferences.SetAsync("wheelPixelsPerNotch", wheel.Value); }
        catch (Exception) { StatusText.Text = "Scroll speed changed for this session; its preference could not be saved."; }
    }
}
