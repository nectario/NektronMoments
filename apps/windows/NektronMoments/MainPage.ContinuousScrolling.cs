using Microsoft.UI.Xaml;

namespace NektronMoments;

public sealed partial class MainPage
{
    private bool _suspendingGalleryMotion;
    private void CancelScrollbarGesture()
    {
        _scrollRefresh.End();
        if (GalleryThumb is { IsDragging: true } thumb) thumb.CancelDrag();
        Controls.MediaThumbnail.SetThumbInput(false);
    }
    public void SuspendGalleryMotion()
    {
        _suspendingGalleryMotion = true;
        try { CancelScrollbarGesture(); _pixelScroll?.Stop(); }
        finally { _suspendingGalleryMotion = false; }
    }
    private void GalleryMotionSettled()
    {
        if (_pixelScroll is null || _pixelScroll.IsAnimating || GalleryThumb?.IsDragging == true ||
            Viewer.IsOpen || _suspendingGalleryMotion || _lifetime.IsCancellationRequested ||
            LibraryCanvas.Visibility != Visibility.Visible) return;
        QueueGalleryWork();
    }
}
