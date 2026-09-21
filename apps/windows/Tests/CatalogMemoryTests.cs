using NektronMoments.Models;
internal static class CatalogMemoryTests
{
    public static void Run(Action<bool, string> check)
    {
        static string Copy(string text) => new(text.ToCharArray());
        var first = new MediaItem { Key = Copy("hash"), Hash = Copy("hash"), Path = Copy("primary"),
            Paths = [Copy("primary"), Copy("fallback")], MediaType = Copy("Photo"), Source = Copy("Photos"), DateSource = Copy("Exif") };
        var second = new MediaItem { Key = "different", Hash = "other", Path = "elsewhere",
            MediaType = Copy("Photo"), Source = Copy("Photos"), DateSource = Copy("Exif") };
        CatalogMemory.Compact([first, second]);
        check(ReferenceEquals(first.Key, first.Hash), "Equal catalog key/hash share storage");
        check(ReferenceEquals(first.Path, first.Paths[0]), "Primary and fallback-list path share storage");
        check(first.Paths.SequenceEqual(new[] { "primary", "fallback" }), "Alternate original paths preserve order and values");
        check(ReferenceEquals(first.Source, second.Source) && ReferenceEquals(first.MediaType, second.MediaType) &&
            ReferenceEquals(first.DateSource, second.DateSource), "Repeated labels share storage without global interning");
        check(second.Key == "different" && second.Hash == "other", "Different identities are never collapsed");
        CatalogMemory.Compact([first, second]);
        check(first.Paths.Length == 2 && second.Paths.Length == 0, "Compaction is repeatable and preserves empty path lists");
    }
}
