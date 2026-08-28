using System.Buffers.Binary;

namespace HbkWwise.Core;

public sealed record BnkTimelineClipEdit(
    int SourceIdOffset,
    double StartMs,
    double SourceOffsetMs,
    double DurationMs,
    BnkTimelineClipAnchor? Anchor = null);

public sealed record BnkTimelineClipAnchor(
    uint TrackObjectId,
    uint? SegmentObjectId,
    int PlaylistIndex,
    uint MediaId);

public sealed record BnkTimelineEditResult(
    byte[] Data,
    int EditedClips,
    int PatchCount,
    int UnchangedRepeatingClips);

public static class BnkTimelineEditor
{
    public static BnkTimelineEditResult Apply(
        byte[] bank,
        BnkTimelineValidation authored,
        IReadOnlyCollection<BnkTimelineClipEdit> edits,
        IReadOnlyDictionary<uint, double> replacementDurationsMs)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(replacementDurationsMs);
        var originals = authored.Clips.Where(clip => clip.SourceIdOffset is not null)
            .GroupBy(clip => clip.SourceIdOffset!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var normalized = edits.GroupBy(edit => edit.SourceIdOffset).ToDictionary(
            group => group.Key,
            group =>
            {
                var first = group.First();
                if (group.Skip(1).Any(item => !Same(first, item)))
                {
                    throw new InvalidDataException(
                        $"Linked placements for source reference 0x{group.Key:X} have conflicting timeline edits.");
                }

                return first;
            });
        var unknown = normalized.Keys.Except(originals.Keys).ToArray();

        if (unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"Timeline structure changed: {unknown.Length} edits have no authored clip.");
        }

        var patches = new Dictionary<int, double>();
        foreach (var item in normalized)
        {
            var clip = originals[item.Key];
            var edit = item.Value;

            if (!double.IsFinite(edit.StartMs) || !double.IsFinite(edit.SourceOffsetMs)
                || !double.IsFinite(edit.DurationMs) || edit.StartMs < 0 || edit.SourceOffsetMs < 0 || edit.DurationMs <= 0)
            {
                throw new InvalidDataException($"Clip source reference 0x{item.Key:X} has invalid timeline coordinates.");
            }

            var fields = clip.FieldOffsets
                ?? throw new InvalidDataException($"Clip source reference 0x{item.Key:X} has no wwiser field offsets.");
            var sourceDuration = replacementDurationsMs.GetValueOrDefault(clip.MediaId, clip.SourceDurationMs);

            if (!double.IsFinite(sourceDuration) || sourceDuration <= 0)
            {
                throw new InvalidDataException($"Media {clip.MediaId} has invalid physical duration {sourceDuration:0.###} ms.");
            }

            Add(patches, fields.PlayAt, edit.StartMs - edit.SourceOffsetMs, "fPlayAt", item.Key);
            Add(patches, fields.BeginTrim, edit.SourceOffsetMs, "fBeginTrimOffset", item.Key);
            Add(patches, fields.EndTrim, edit.DurationMs - (sourceDuration - edit.SourceOffsetMs), "fEndTrimOffset", item.Key);
            if (replacementDurationsMs.ContainsKey(clip.MediaId))
            {
                Add(patches, fields.SourceDuration, sourceDuration, "fSrcDuration", item.Key);
            }
        }

        var output = bank.ToArray();
        var changed = 0;

        foreach (var patch in patches.OrderBy(item => item.Key))
        {
            if (patch.Key < 0 || patch.Key > output.Length - 8)
            {
                throw new InvalidDataException($"Timeline field offset 0x{patch.Key:X} is outside the BNK.");
            }

            var current = BinaryPrimitives.ReadDoubleLittleEndian(output.AsSpan(patch.Key, 8));
            if (Math.Abs(current - patch.Value) <= 0.0000001)
            {
                continue;
            }

            BinaryPrimitives.WriteDoubleLittleEndian(output.AsSpan(patch.Key, 8), patch.Value);
            changed++;
        }

        return new BnkTimelineEditResult(output, normalized.Count, changed, 0);
    }

    private static void Add(
        Dictionary<int, double> patches,
        int? offset,
        double value,
        string field,
        int sourceIdOffset)
    {
        if (offset is null)
        {
            throw new InvalidDataException($"Clip source reference 0x{sourceIdOffset:X} has no {field} offset.");
        }

        if (patches.TryGetValue(offset.Value, out var existing) && Math.Abs(existing - value) > 0.0000001)
        {
            throw new InvalidDataException($"Conflicting timeline edits target {field} at 0x{offset.Value:X}.");
        }

        patches[offset.Value] = value;
    }

    private static bool Same(BnkTimelineClipEdit left, BnkTimelineClipEdit right) =>
        Math.Abs(left.StartMs - right.StartMs) <= 0.001
        && Math.Abs(left.SourceOffsetMs - right.SourceOffsetMs) <= 0.001
        && Math.Abs(left.DurationMs - right.DurationMs) <= 0.001;
}
