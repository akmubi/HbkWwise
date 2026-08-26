using System.Buffers.Binary;

namespace HbkWwise.Core;

public sealed record BnkTimelineMarkerEdit(int PositionOffset, double PositionMs);

public sealed record BnkTimelineSegmentDurationEdit(int DurationOffset, double DurationMs);

public sealed record BnkTimelineMarkerEditResult(byte[] Data, int PatchCount);

public static class BnkTimelineMarkerEditor
{
    public static BnkTimelineMarkerEditResult Apply(
        byte[] bank,
        IReadOnlyCollection<BnkTimelineMarkerEdit>? edits,
        IReadOnlyCollection<BnkTimelineSegmentDurationEdit>? durationEdits = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (edits is not { Count: > 0 } && durationEdits is not { Count: > 0 })
        {
            return new BnkTimelineMarkerEditResult(bank.ToArray(), 0);
        }

        var normalized = (edits ?? []).GroupBy(edit => edit.PositionOffset).ToDictionary(
            group => group.Key,
            group =>
            {
                var first = group.First();
                if (group.Any(item => Math.Abs(item.PositionMs - first.PositionMs) > 0.001))
                {
                    throw new InvalidDataException($"Conflicting cue edits target BNK offset 0x{group.Key:X}.");
                }

                return first;
            });
        var output = bank.ToArray();
        var changed = 0;

        foreach (var edit in normalized.Values)
        {
            if (edit.PositionOffset < 0 || edit.PositionOffset > output.Length - sizeof(double)
                || !double.IsFinite(edit.PositionMs) || edit.PositionMs < 0)
            {
                throw new InvalidDataException($"Cue edit at 0x{edit.PositionOffset:X} is invalid.");
            }

            var target = output.AsSpan(edit.PositionOffset, sizeof(double));
            if (Math.Abs(BinaryPrimitives.ReadDoubleLittleEndian(target) - edit.PositionMs) <= 0.0000001)
            {
                continue;
            }

            BinaryPrimitives.WriteDoubleLittleEndian(target, edit.PositionMs);
            changed++;
        }


        foreach (var edit in durationEdits ?? [])
        {
            if (edit.DurationOffset < 0 || edit.DurationOffset > output.Length - sizeof(double)
                || !double.IsFinite(edit.DurationMs) || edit.DurationMs <= 0)
            {
                throw new InvalidDataException($"Segment duration edit at 0x{edit.DurationOffset:X} is invalid.");
            }

            var target = output.AsSpan(edit.DurationOffset, sizeof(double));
            if (Math.Abs(BinaryPrimitives.ReadDoubleLittleEndian(target) - edit.DurationMs) <= 0.0000001)
            {
                continue;
            }

            BinaryPrimitives.WriteDoubleLittleEndian(target, edit.DurationMs);
            changed++;
        }

        return new BnkTimelineMarkerEditResult(output, changed);
    }
}
