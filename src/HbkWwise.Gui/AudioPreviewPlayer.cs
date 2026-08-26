using System.Diagnostics;
using HbkWwise.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HbkWwise.Gui;

internal enum AudioPreviewState
{
    Stopped,
    Playing,
    Paused
}

internal sealed record TimelinePreviewTrack(
    Guid Id,
    TimelineAudioPlacement[] Placements,
    double Gain,
    bool IsMuted,
    bool IsSolo);

internal sealed record TimelinePreviewTrackState(Guid Id, double Gain, bool IsMuted, bool IsSolo);

internal sealed class AudioPreviewPlayer : IDisposable
{
    private const int TimelineRate = 48_000;
    private readonly Stopwatch positionClock = new();
    private readonly List<WaveStream> readers = [];
    private readonly Dictionary<Guid, VolumeSampleProvider> trackVolumes = [];
    private WaveOutEvent? output;
    private VolumeSampleProvider? masterVolume;
    private string? wavPath;
    private TimelinePreviewTrack[]? timelineTracks;
    private double sourceStartMs;
    private double logicalStartMs;
    private double logicalEndMs;
    private double lastPositionMs;
    private double clockStartMs;
    private bool suppressStopped;
    private double masterGain = 1;

    public event Action? PlaybackEnded;

    public AudioPreviewState State { get; private set; }

    public bool HasSource => wavPath is not null || timelineTracks is not null;

    public bool HasLiveTrackMix => timelineTracks is not null;

    public double MasterGain
    {
        get => masterGain;
        set
        {
            masterGain = Math.Clamp(value, 0, 1);
            if (masterVolume is not null)
            {
                masterVolume.Volume = (float)masterGain;
            }
        }
    }

    public double PositionMs => State == AudioPreviewState.Playing
        ? Math.Clamp(clockStartMs + positionClock.Elapsed.TotalMilliseconds, logicalStartMs, logicalEndMs)
        : lastPositionMs;

    public void Play(
        string path,
        double sourceOffsetMs = 0,
        double? durationMs = null,
        double timelineStartMs = 0,
        double? initialTimelinePositionMs = null)
    {
        Stop();
        wavPath = Path.GetFullPath(path);
        using (var probe = new WaveFileReader(wavPath))
        {
            sourceStartMs = Math.Clamp(sourceOffsetMs, 0, probe.TotalTime.TotalMilliseconds);
            var available = Math.Max(0, probe.TotalTime.TotalMilliseconds - sourceStartMs);
            var duration = durationMs is > 0 ? Math.Min(durationMs.Value, available) : available;

            logicalStartMs = Math.Max(0, timelineStartMs);
            logicalEndMs = logicalStartMs + duration;
        }

        StartAt(initialTimelinePositionMs ?? logicalStartMs, play: true);
    }

    public void PlayTimeline(
        IReadOnlyCollection<TimelinePreviewTrack> tracks,
        double durationMs,
        double rangeStartMs,
        double rangeEndMs,
        double initialTimelinePositionMs)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0 || durationMs <= 0)
        {
            throw new ArgumentException("A live timeline preview requires at least one track and a positive duration.");
        }

        Stop();
        timelineTracks = tracks.ToArray();
        logicalStartMs = Math.Clamp(rangeStartMs, 0, durationMs);
        logicalEndMs = Math.Clamp(rangeEndMs, logicalStartMs, durationMs);
        StartAt(initialTimelinePositionMs, play: true);
    }

    public bool UpdateTrackMix(IReadOnlyCollection<TimelinePreviewTrackState> states)
    {
        if (timelineTracks is null || states.Count != timelineTracks.Length)
        {
            return false;
        }

        var stateById = states.ToDictionary(state => state.Id);
        if (timelineTracks.Any(track => !stateById.ContainsKey(track.Id)))
        {
            return false;
        }

        timelineTracks = timelineTracks.Select(track =>
        {
            var state = stateById[track.Id];
            return track with { Gain = state.Gain, IsMuted = state.IsMuted, IsSolo = state.IsSolo };
        }).ToArray();
        ApplyTrackVolumes();

        return true;
    }

    public void TogglePause()
    {
        if (State == AudioPreviewState.Playing)
        {
            lastPositionMs = PositionMs;
            output?.Pause();
            positionClock.Stop();
            State = AudioPreviewState.Paused;
        }
        else if (State == AudioPreviewState.Paused)
        {
            clockStartMs = lastPositionMs;
            positionClock.Restart();
            output?.Play();
            State = AudioPreviewState.Playing;
        }
    }

    public void Seek(double timelinePositionMs)
    {
        if (!HasSource)
        {
            return;
        }

        StartAt(Math.Clamp(timelinePositionMs, logicalStartMs, logicalEndMs), State == AudioPreviewState.Playing);
    }

    public void Stop()
    {
        lastPositionMs = PositionMs;
        DisposeOutput();
        wavPath = null;
        timelineTracks = null;
        State = AudioPreviewState.Stopped;
    }

    public void Dispose() => Stop();

    private void StartAt(double logicalPositionMs, bool play)
    {
        if (!HasSource)
        {
            throw new InvalidOperationException("No preview source is loaded.");
        }

        DisposeOutput();
        lastPositionMs = Math.Clamp(logicalPositionMs, logicalStartMs, logicalEndMs);
        if (lastPositionMs >= logicalEndMs - 0.5)
        {
            State = AudioPreviewState.Stopped;

            PlaybackEnded?.Invoke();

            return;
        }

        var source = timelineTracks is null
            ? CreateFileSource(lastPositionMs)
            : CreateTimelineSource(lastPositionMs);
        masterVolume = new VolumeSampleProvider(source) { Volume = (float)masterGain };
        output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        output.PlaybackStopped += OutputStopped;
        output.Init(masterVolume.ToWaveProvider());
        State = play ? AudioPreviewState.Playing : AudioPreviewState.Paused;
        clockStartMs = lastPositionMs;
        positionClock.Restart();
        if (play)
        {
            output.Play();
        }
        else
        {
            positionClock.Stop();
        }
    }

    private ISampleProvider CreateFileSource(double positionMs)
    {
        var path = wavPath ?? throw new InvalidOperationException("No file preview is loaded.");
        var reader = new WaveFileReader(path)
        {
            CurrentTime = TimeSpan.FromMilliseconds(sourceStartMs + positionMs - logicalStartMs)
        };

        readers.Add(reader);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels > 2)
        {
            source = new MultichannelToStereoSampleProvider(source);
        }

        return new OffsetSampleProvider(source)
        {
            Take = TimeSpan.FromMilliseconds(logicalEndMs - positionMs)
        };
    }

    private ISampleProvider CreateTimelineSource(double positionMs)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(TimelineRate, 2);
        var master = new MixingSampleProvider(format) { ReadFully = true };
        var tracks = timelineTracks ?? throw new InvalidOperationException("No timeline preview is loaded.");

        foreach (var track in tracks)
        {
            var trackMixer = new MixingSampleProvider(format) { ReadFully = true };
            foreach (var placement in track.Placements)
            {
                var placementEnd = placement.StartMs + placement.DurationMs;
                if (placementEnd <= positionMs || placement.StartMs >= logicalEndMs)
                {
                    continue;
                }

                var elapsedMs = Math.Max(0, positionMs - placement.StartMs);
                var remainingMs = Math.Min(
                    placement.DurationMs - elapsedMs,
                    logicalEndMs - Math.Max(positionMs, placement.StartMs));

                if (remainingMs <= 0)
                {
                    continue;
                }

                var reader = new WaveFileReader(Path.GetFullPath(placement.WavPath));
                readers.Add(reader);
                WaveStream wave = placement.Repeat ? new LoopingWaveStream(reader) : reader;
                ISampleProvider source = wave.ToSampleProvider();
                source = source.WaveFormat.Channels switch
                {
                    1 => new MonoToStereoSampleProvider(source),
                    2 => source,
                    _ => new MultichannelToStereoSampleProvider(source)
                };
                if (source.WaveFormat.SampleRate != TimelineRate)
                {
                    source = new WdlResamplingSampleProvider(source, TimelineRate);
                }

                var window = new OffsetSampleProvider(source)
                {
                    SkipOver = TimeSpan.FromMilliseconds(Math.Max(0, placement.SourceOffsetMs + elapsedMs)),
                    Take = TimeSpan.FromMilliseconds(Math.Max(1, remainingMs))
                };
                ISampleProvider shaped = placement.FadeInMs <= 0 && placement.FadeOutMs <= 0
                    ? window
                    : new FadeEnvelopeSampleProvider(
                        window,
                        placement.DurationMs,
                        placement.FadeInMs,
                        placement.FadeOutMs,
                        elapsedMs);
                trackMixer.AddMixerInput(new OffsetSampleProvider(shaped)
                {
                    DelayBy = TimeSpan.FromMilliseconds(Math.Max(0, placement.StartMs - positionMs))
                });
            }

            var volume = new VolumeSampleProvider(trackMixer);
            trackVolumes[track.Id] = volume;
            master.AddMixerInput(volume);
        }

        ApplyTrackVolumes();
        return master.Take(TimeSpan.FromMilliseconds(logicalEndMs - positionMs));
    }

    private void ApplyTrackVolumes()
    {
        if (timelineTracks is null)
        {
            return;
        }

        var anySolo = timelineTracks.Any(track => track.IsSolo);
        foreach (var track in timelineTracks)
        {
            if (trackVolumes.TryGetValue(track.Id, out var volume))
            {
                volume.Volume = (float)((anySolo ? track.IsSolo : !track.IsMuted) ? track.Gain : 0);
            }
        }
    }

    private void OutputStopped(object? sender, StoppedEventArgs e)
    {
        if (suppressStopped || !ReferenceEquals(sender, output))
        {
            return;
        }

        positionClock.Stop();
        lastPositionMs = logicalEndMs;
        State = AudioPreviewState.Stopped;

        PlaybackEnded?.Invoke();
    }

    private void DisposeOutput()
    {
        suppressStopped = true;
        try
        {
            positionClock.Stop();
            if (output is not null)
            {
                output.PlaybackStopped -= OutputStopped;
                output.Stop();
                output.Dispose();
            }

            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
        finally
        {
            output = null;
            masterVolume = null;
            readers.Clear();
            trackVolumes.Clear();
            suppressStopped = false;
        }
    }

    private sealed class FadeEnvelopeSampleProvider(
        ISampleProvider source,
        double durationMs,
        double fadeInMs,
        double fadeOutMs,
        double startMs) : ISampleProvider
    {
        private long samplePosition = (long)(startMs / 1_000 * source.WaveFormat.SampleRate * source.WaveFormat.Channels);

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var channels = WaveFormat.Channels;

            for (var index = 0; index < read; index += channels)
            {
                var frame = (samplePosition + index) / channels;
                var timeMs = frame * 1_000d / WaveFormat.SampleRate;
                var fadeIn = fadeInMs <= 0 ? 1 : Math.Clamp(timeMs / fadeInMs, 0, 1);
                var fadeOut = fadeOutMs <= 0 ? 1 : Math.Clamp((durationMs - timeMs) / fadeOutMs, 0, 1);
                var gain = (float)Math.Min(fadeIn, fadeOut);

                for (var channel = 0; channel < channels && index + channel < read; channel++)
                {
                    buffer[offset + index + channel] *= gain;
                }
            }

            samplePosition += read;
            return read;
        }
    }

    private sealed class LoopingWaveStream(WaveStream source) : WaveStream
    {
        public override WaveFormat WaveFormat => source.WaveFormat;
        public override long Length => long.MaxValue;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value % source.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = source.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    source.Position = 0;
                    read = source.Read(buffer, offset + total, count - total);
                    if (read == 0)
                    {
                        break;
                    }
                }

                total += read;
            }

            return total;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
