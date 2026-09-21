namespace NektronMoments.Models;

public static class ReorderPolicy
{
    public static int Destination(int from, int target, bool after, int count)
    {
        if (count <= 0 || from < 0 || from >= count || target < 0 || target >= count) return -1;
        var insertion = target + (after ? 1 : 0);
        return Math.Clamp(insertion > from ? insertion - 1 : insertion, 0, count - 1);
    }
}
