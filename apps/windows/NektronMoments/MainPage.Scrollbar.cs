using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NektronMoments.Services;

namespace NektronMoments;

public sealed partial class MainPage
{
    // The GridView's own bar still owns its range and viewport connection.
    // Mouse thumb input is animated; keyboard, paging and touch remain native.
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
        await ShowSettingsAsync();
        AssetDescendants(SettingsHost).OfType<Pivot>().Single().SelectedIndex = 2;
    }
}
