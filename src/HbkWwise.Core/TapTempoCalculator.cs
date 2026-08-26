namespace HbkWwise.Core;

public static class TapTempoCalculator
{
    public static double? EstimateBpm(IReadOnlyList<double> tapTimesMs)
    {
        ArgumentNullException.ThrowIfNull(tapTimesMs);
        if (tapTimesMs.Count < 2)
        {
            return null;
        }

        var intervals = tapTimesMs.Zip(tapTimesMs.Skip(1), (left, right) => right - left)
            .Where(value => double.IsFinite(value) && value > 0)
            .Order()
            .ToArray();
        if (intervals.Length == 0)
        {
            return null;
        }

        var median = intervals[intervals.Length / 2];
        var stable = intervals.Where(value => Math.Abs(value - median) <= median * 0.25).ToArray();
        var interval = stable.Length == 0 ? median : stable.Average();
        return 60_000 / interval;
    }
}

public sealed record BeatGridAlignment(
    double Bpm,
    double BeatPeriodMs,
    double PhaseMs,
    double LeadingSilenceMs,
    double MeanTapErrorMs);

public static class BeatGridAlignmentCalculator
{
    public static BeatGridAlignment? Fit(IReadOnlyList<double> tapPositionsMs, double bpm)
    {
        ArgumentNullException.ThrowIfNull(tapPositionsMs);
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(bpm));
        }

        var taps = tapPositionsMs.Where(value => double.IsFinite(value) && value >= 0).ToArray();
        if (taps.Length == 0)
        {
            return null;
        }

        var period = 60_000 / bpm;
        var angles = taps.Select(value => PositiveModulo(value, period) / period * Math.Tau).ToArray();
        var angle = Math.Atan2(angles.Average(Math.Sin), angles.Average(Math.Cos));
        var phase = PositiveModulo(angle / Math.Tau * period, period);
        if (phase < 0.001 || period - phase < 0.001)
        {
            phase = 0;
        }

        var meanError = taps.Average(value => Math.Abs(CircularDifference(value - phase, period)));
        var silence = phase == 0 ? 0 : period - phase;
        return new BeatGridAlignment(bpm, period, phase, silence, meanError);
    }

    public static double SnapAudioOffset(
        IReadOnlyList<double> tapPositionsMs,
        double bpm,
        double desiredOffsetMs,
        double toleranceMs)
    {
        ArgumentNullException.ThrowIfNull(tapPositionsMs);
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(bpm));
        }

        if (!double.IsFinite(desiredOffsetMs) || desiredOffsetMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredOffsetMs));
        }

        if (!double.IsFinite(toleranceMs) || toleranceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMs));
        }

        var period = 60_000 / bpm;
        var taps = tapPositionsMs.Where(value => double.IsFinite(value) && value >= 0).ToArray();
        var candidates = taps.Length == 0
            ? [Math.Round(desiredOffsetMs / period, MidpointRounding.AwayFromZero) * period]
            : taps.Select(tap =>
                    Math.Round((tap + desiredOffsetMs) / period, MidpointRounding.AwayFromZero) * period - tap)
                .Where(value => value >= 0)
                .ToArray();
        if (candidates.Length == 0)
        {
            return desiredOffsetMs;
        }

        var closest = candidates.MinBy(value => Math.Abs(value - desiredOffsetMs));
        return Math.Abs(closest - desiredOffsetMs) <= toleranceMs ? closest : desiredOffsetMs;
    }

    internal static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static double CircularDifference(double value, double period) =>
        PositiveModulo(value + period / 2, period) - period / 2;
}
