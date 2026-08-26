using Avalonia.Media;

namespace HbkWwise.Gui;

internal static class NameColorPalette
{
    private static readonly string[] Colors =
    [
        "#FF6B6B", "#4ECDC4", "#FFD166", "#A78BFA", "#5DADE2", "#F78FB3",
        "#82E0AA", "#FFA94D", "#66D9EF", "#E879F9", "#B8E356", "#FF8A65",
        "#7FDBFF", "#B39DDB", "#26D7AE", "#F4D35E", "#EF7A85", "#70A1FF",
        "#C7F464", "#FF9FF3", "#45AAF2", "#E6B566", "#9AECDB", "#D980FA"
    ];

    public static string Hex(string? value)
    {
        if (value?.Equals("cue:Entry", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "#4ECDC4";
        }

        if (value?.Equals("cue:Exit", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "#FF6B6B";
        }

        var hash = 2_166_136_261u;
        foreach (var character in value ?? string.Empty)
        {
            hash = (hash ^ character) * 16_777_619u;
        }

        return Colors[hash % Colors.Length];
    }

    public static SolidColorBrush Brush(string? value) => new(Color.Parse(Hex(value)));
}
