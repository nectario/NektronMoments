using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NektronMoments.Models;

namespace NektronMoments;

public sealed partial class MainPage
{
    private CancellationTokenSource? _selectionDetails;

    private void CancelSelectionDetails()
    {
        ++_selectionGeneration;
        _selectionDetails?.Cancel();
        _selectionDetails = null;
    }

    private async void MediaClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is MediaItem item) await SelectGalleryItemAsync(item);
    }

    private async Task SelectGalleryItemAsync(MediaItem item)
    {
        if (Viewer.IsOpen) return;
        _pixelScroll?.Stop();
        CancelSelectionDetails();
        _selected = item;
        Gallery.SelectedItem = item;
        // A single click must not change the canvas width between the two taps
        // of a double-click. Details are opened explicitly with the Details button.
        if (DetailsSplit.IsPaneOpen) await LoadSelectedDetailsAsync(item);
    }

    private async Task LoadSelectedDetailsAsync(MediaItem item)
    {
        CancelSelectionDetails();
        var selection = _selectionGeneration;
        var library = _generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _selectionDetails = cancellation;
        FillDetails(item, null, "Loading indexed details…");
        bool IsCurrent() => !cancellation.IsCancellationRequested && selection == _selectionGeneration &&
            library == _generation && _selected?.Key == item.Key && !Viewer.IsOpen && DetailsSplit.IsPaneOpen;
        try {
            var detail = await _bridge.CallAsync<MediaDetail>(new { command = "detail", key = item.Key }, cancellation.Token);
            if (IsCurrent()) {
                // Metadata responses enrich the details pane, not the identity or
                // original path chosen by the gallery click.
                FillDetails(detail.Item, detail.Remote, detail.Notice);
            }
        } catch (OperationCanceledException) { }
        catch (Exception) { if (IsCurrent()) DetailNotice.Text = "Showing cached metadata. Indexed details are unavailable."; }
        finally { if (ReferenceEquals(_selectionDetails, cancellation)) _selectionDetails = null; }
    }

    private MediaItem? GalleryItemFromElement(DependencyObject? element)
    {
        while (element is not null && element != Gallery) {
            if (element is GridViewItem container)
                return Gallery.IndexFromContainer(container) >= 0 ? container.Content as MediaItem : null;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private async void ViewMedia(object sender, RoutedEventArgs e) => await OpenCanvasAsync(false);

    private async void GalleryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Footer, empty gutter and scrollbar double-clicks must not reuse a stale
        // selection. Resolve the exact nested image/label container instead.
        if (GalleryItemFromElement(e.OriginalSource as DependencyObject) is not { } item) return;
        e.Handled = true;
        await OpenCanvasAsync(false, item);
    }
}
