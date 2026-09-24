namespace NektronMoments.Models;

/// <summary>Absolute pointer mapping avoids accumulating animation lag or changing
/// sensitivity as the thumb follows the viewport. Geometry is frozen per gesture.</summary>
public static class ThumbDragTarget
{
    public static double Resolve(double offset, double origin, double pointer, double maximum, double travel) =>
        !double.IsFinite(offset) || !double.IsFinite(origin) || !double.IsFinite(pointer) ||
        !double.IsFinite(maximum) || !double.IsFinite(travel) || maximum <= 0 || travel <= 0
            ? 0 : Math.Clamp(offset + (pointer - origin) * maximum / travel, 0, maximum);
}
