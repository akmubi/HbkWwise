using System.Globalization;

namespace HbkWwise.Gui;

public enum GuiLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record GuiLogEntry(DateTimeOffset Time, GuiLogLevel Level, string Message);

public sealed class GuiLog
{
    private readonly List<GuiLogEntry> entries = [];

    public IReadOnlyList<GuiLogEntry> Entries => entries;

    public GuiLogEntry Write(GuiLogLevel level, string message)
    {
        var entry = new GuiLogEntry(DateTimeOffset.Now, level, message.Trim());
        entries.Add(entry);

        return entry;
    }

    public string Format() => string.Join(Environment.NewLine, entries.Select(entry =>
        $"{entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  "
        + $"{entry.Level.ToString().ToUpperInvariant(),-7}  {entry.Message}"));
}
