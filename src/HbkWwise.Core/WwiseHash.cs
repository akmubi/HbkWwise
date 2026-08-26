using System.Text;

namespace HbkWwise.Core;

public static class WwiseHash
{
    public const uint MaxMediaId = 0x3FFF_FFFF;

    public static uint Fnv1(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = 2166136261u;
        foreach (var octet in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash *= 16777619u;
            hash ^= octet;
        }

        return hash;
    }

    public static uint MediaId(string value) => Fnv1(value) & MaxMediaId;

    public static bool IsMediaId(uint value) => value is > 0 and <= MaxMediaId;

    public static uint AllocateMediaId(string seed, IReadOnlySet<uint> used)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(used);
        for (var suffix = 0; ; suffix++)
        {
            var id = MediaId($"{seed}_{suffix}");
            if (IsMediaId(id) && !used.Contains(id))
            {
                return id;
            }
        }
    }
}
