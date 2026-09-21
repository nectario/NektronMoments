using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NektronMoments.Models;
using Windows.Foundation;

namespace NektronMoments.Controls;

/// <summary>Animate realized cards, not collection notifications. Layout runs once per move;
/// independent render transforms carry the intervening frames on the compositor.</summary>
public sealed class ThumbnailReorderMotion(GridView gallery, ScrollViewer? scroll) : IDisposable
{
    private sealed record Motion(MediaItem Item, Transform Original, TranslateTransform Translation, Storyboard Story);
    private readonly Dictionary<GridViewItem, Motion> _active = [];
    private readonly Windows.UI.ViewManagement.UISettings _settings = new();
    private TransitionCollection? _normalTransitions;
    private bool _transitionsHeld, _moving;
    public int ActiveCount => _active.Count;
    public const int DurationMs = 320;

    public void Move(ObservableCollection<MediaItem> items, int from, int to)
    {
        if (from == to) return;
        var positions = new Dictionary<MediaItem, Point>();
        if (_settings.AnimationsEnabled && gallery.ItemsPanelRoot is { } panel) {
            // Include one nearby row; don't traverse/materialize the whole catalog.
            foreach (var container in panel.Children.OfType<GridViewItem>()) {
                if (container.Content is not MediaItem item || container.ActualWidth <= 0 || gallery.IndexFromContainer(container) < 0) continue;
                var point = container.TransformToVisual(gallery).TransformPoint(new Point());
                if (point.Y + container.ActualHeight >= -container.ActualHeight && point.Y <= gallery.ActualHeight + container.ActualHeight)
                    positions[item] = point; // Includes the current in-flight translation when retargeting.
            }
        }
        _moving = true;
        Stop();
        var scrollOffset = scroll?.VerticalOffset;
        try {
            // ItemsWrapGrid's stock collection transitions do not supply this live
            // hover animation. They must not compete with our explicit transforms.
            if (!_transitionsHeld) {
                _normalTransitions = gallery.ItemContainerTransitions; _transitionsHeld = true;
                gallery.ItemContainerTransitions = [];
            }
            items.Move(from, to);
            gallery.UpdateLayout();
            // Moving the first visible item otherwise makes ItemsWrapGrid follow
            // that item as its anchor. A drag rearranges cards, not the viewport.
            if (scroll is not null && scrollOffset is { } offset) {
                scroll.ChangeView(null, offset, null, disableAnimation: true);
                scroll.UpdateLayout(); gallery.UpdateLayout();
            }
            if (positions.Count == 0 || gallery.ItemsPanelRoot is not { } movedPanel) return;
            foreach (var (item, before) in positions) {
                if (gallery.ContainerFromItem(item) is not GridViewItem container || gallery.IndexFromContainer(container) < 0) continue;
                var after = container.TransformToVisual(gallery).TransformPoint(new Point());
                // Virtualization can park a recycled container far outside layout.
                // Never animate that parking coordinate into the visible gallery.
                if (after.Y < -container.ActualHeight || after.Y > gallery.ActualHeight + container.ActualHeight ||
                    after.X < -container.ActualWidth || after.X > gallery.ActualWidth) continue;
                var dx = before.X - after.X; var dy = before.Y - after.Y;
                if (Math.Abs(dx) < .5 && Math.Abs(dy) < .5) continue;
                var original = container.RenderTransform;
                // Keep the pre-animation frame at the displayed location too;
                // Begin() does not advance the independent clock synchronously.
                var translation = new TranslateTransform { X = dx, Y = dy };
                container.RenderTransform = translation;
                var story = new Storyboard();
                AddAxis("X", dx); AddAxis("Y", dy);
                _active.Add(container, new(item, original, translation, story));
                story.Completed += Completed;
                story.Begin();
                void AddAxis(string axis, double fromValue) {
                    var animation = new DoubleAnimation {
                        From = fromValue, To = 0, Duration = TimeSpan.FromMilliseconds(DurationMs),
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                    };
                    Storyboard.SetTarget(animation, container);
                    Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(TranslateTransform." + axis + ")");
                    story.Children.Add(animation);
                }
            }
        } catch { Stop(); throw; }
        finally { _moving = false; RestoreTransitionsWhenSettled(); }
    }
    private void Completed(object? sender, object args)
    {
        var container = _active.FirstOrDefault(pair => ReferenceEquals(pair.Value.Story, sender)).Key;
        if (container is not null) Stop(container);
    }
    public void ContainerChanging(GridViewItem container, object item, bool recycled)
    {
        if (_active.TryGetValue(container, out var motion) && (recycled || !ReferenceEquals(item, motion.Item))) Stop(container);
    }
    private void Stop(GridViewItem container)
    {
        if (!_active.Remove(container, out var motion)) return;
        motion.Translation.X = motion.Translation.Y = 0;
        motion.Story.Completed -= Completed; motion.Story.Stop(); motion.Story.Children.Clear();
        // WinRT getters need not return the same managed projection instance.
        // This class owns the slot until completion/recycle; don't use RCW
        // ReferenceEquals to decide whether to clear the old displacement.
        container.RenderTransform = motion.Original;
        RestoreTransitionsWhenSettled();
    }
    private void RestoreTransitionsWhenSettled()
    {
        if (_moving || _active.Count != 0 || !_transitionsHeld) return;
        // Restoring in the collection-move UI turn lets WinUI schedule a second,
        // opposing reposition animation during the next render. Hold suppression
        // until our explicit movement is finished, not just until UpdateLayout.
        gallery.ItemContainerTransitions = _normalTransitions;
        _normalTransitions = null; _transitionsHeld = false;
    }
    public void Stop() { foreach (var container in _active.Keys.ToArray()) Stop(container); RestoreTransitionsWhenSettled(); }
    public void Dispose() => Stop();
}
