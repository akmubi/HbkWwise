using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HbkWwise.Core;
using NAudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HbkWwise.Gui;

internal sealed record BpmDetectionResult(double Bpm, double LeadingGapMs);

internal sealed class BpmDetectionDialog : Window
{
    private readonly BpmPreviewPlayer player;
    private readonly BpmWaveformControl waveform;
    private readonly TextBox bpmInput;
    private readonly TextBlock tapCount = new()
    {
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly SelectableTextBlock alignmentText = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox metronome = new() { Content = "Metronome" };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Width = 100 };
    private readonly TextBlock volumeValue = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider zoom = new() { Minimum = 1, Maximum = 500, Width = 150 };
    private readonly List<TapSample> taps = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly bool canApply;
    private BeatGridAlignment? alignment;
    private double bpm;

    public BpmDetectionDialog(
        string audioName,
        string wavPath,
        WaveformEnvelope envelope,
        double initialBpm,
        double initialVolume,
        bool canApply)
    {
        this.canApply = canApply;
        bpm = Math.Clamp(initialBpm, 20, 400);
        player = new BpmPreviewPlayer(wavPath, envelope.DurationMs);
        player.Bpm = bpm;
        player.Gain = Math.Clamp(initialVolume, 0, 1);
        waveform = new BpmWaveformControl(audioName, envelope) { Bpm = bpm };
        bpmInput = new TextBox
        {
            Text = bpm.ToString("0.###", CultureInfo.InvariantCulture),
            Width = 76,
            Height = 28,
            MinHeight = 0,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        volume.Value = player.Gain;
        volumeValue.Text = $"{player.Gain * 100:0}%";

        Title = $"Calculate BPM - {audioName}";
        Width = 1_050;
        Height = 470;
        MinWidth = 720;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent(audioName);

        waveform.SeekRequested += Seek;
        waveform.AudioOffsetCommitted += CommitAudioOffset;
        player.PlaybackEnded += () => Dispatcher.UIThread.Post(PlaybackEnded);
        timer.Tick += (_, _) => UpdatePosition();
        bpmInput.LostFocus += (_, _) => ApplyBpmInput();
        bpmInput.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                ApplyBpmInput();
                waveform.Focus();
            }
        };
        metronome.IsCheckedChanged += (_, _) => player.MetronomeEnabled = metronome.IsChecked == true;
        volume.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                player.Gain = volume.Value;
                volumeValue.Text = $"{player.Gain * 100:0}%";
            }
        };
        zoom.Value = waveform.PixelsPerSecond;
        zoom.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                waveform.PixelsPerSecond = zoom.Value;
            }
        };
        waveform.ZoomChanged += value => zoom.Value = value;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => waveform.Fit();
        Closed += (_, _) =>
        {
            timer.Stop();
            player.Dispose();
        };
        UpdateTapCount();
        UpdateAlignment();
    }

    private Control BuildContent(string audioName)
    {
        var play = new Button { Content = "Play" };
        var pause = new Button { Content = "Pause" };
        var stop = new Button { Content = "Stop" };
        var resetTaps = new Button { Content = "Reset taps" };
        play.Click += (_, _) => Play();
        pause.Click += (_, _) => Pause();
        stop.Click += (_, _) => Stop();
        resetTaps.Click += (_, _) => ResetTaps();

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        controls.Children.Add(play);
        controls.Children.Add(pause);
        controls.Children.Add(stop);
        controls.Children.Add(new TextBlock
        {
            Text = "BPM",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        controls.Children.Add(bpmInput);
        controls.Children.Add(tapCount);
        controls.Children.Add(resetTaps);
        controls.Children.Add(metronome);
        controls.Children.Add(new TextBlock
        {
            Text = "Volume",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        controls.Children.Add(volume);
        controls.Children.Add(volumeValue);
        controls.Children.Add(new TextBlock
        {
            Text = "Zoom",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        controls.Children.Add(zoom);

        var hint = new TextBlock
        {
            Text = "Press T on each beat while audio is playing. Hold Alt and drag the audio to align its tap markers with the fixed beat grid. Space plays or stops; R returns to the start; F centers the playhead; Ctrl+wheel zooms.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap
        };

        var close = new Button { Content = "Close", Padding = new Thickness(18, 6) };
        close.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(close);

        if (canApply)
        {
            var apply = new Button { Content = "Apply", Padding = new Thickness(18, 6) };
            apply.Click += (_, _) =>
            {
                ApplyBpmInput();
                Close(new BpmDetectionResult(bpm, waveform.AudioOffsetMs));
            };
            buttons.Children.Add(apply);
        }

        var title = new TextBlock
        {
            Text = audioName,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(14)
        };

        layout.Children.Add(title);
        Grid.SetRow(controls, 1);
        controls.Margin = new Thickness(0, 10, 0, 8);
        layout.Children.Add(controls);
        Grid.SetRow(waveform, 2);
        layout.Children.Add(waveform);
        Grid.SetRow(alignmentText, 3);
        alignmentText.Margin = new Thickness(0, 8, 0, 0);
        layout.Children.Add(alignmentText);
        Grid.SetRow(hint, 4);
        hint.Margin = new Thickness(0, 8, 0, 0);
        layout.Children.Add(hint);
        Grid.SetRow(buttons, 5);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(buttons);
        return layout;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        if (e.Source is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.T:
                Tap();
                e.Handled = true;
                break;
            case Key.Space:
                if (player.State == AudioPreviewState.Stopped)
                {
                    Play();
                }
                else
                {
                    Stop();
                }
                e.Handled = true;
                break;
            case Key.R:
                Stop();
                waveform.PositionMs = 0;
                waveform.CenterOn(0);
                e.Handled = true;
                break;
            case Key.F:
                waveform.CenterOn(waveform.PositionMs);
                e.Handled = true;
                break;
        }
    }

    private void Play()
    {
        ApplyBpmInput();
        if (player.State == AudioPreviewState.Paused)
        {
            player.TogglePause();
        }
        else
        {
            var start = waveform.PositionMs >= waveform.DurationMs - 1 ? 0 : waveform.PositionMs;
            player.Play(start);
            waveform.PositionMs = start;
        }

        timer.Start();
    }

    private void Pause()
    {
        if (player.State is AudioPreviewState.Playing or AudioPreviewState.Paused)
        {
            player.TogglePause();
            UpdatePosition();
        }
    }

    private void Stop()
    {
        timer.Stop();
        player.Stop();
        waveform.PositionMs = 0;
    }

    private void Seek(double positionMs)
    {
        waveform.PositionMs = positionMs;
        if (player.State is AudioPreviewState.Playing or AudioPreviewState.Paused)
        {
            player.Seek(positionMs);
        }
    }

    private void UpdatePosition()
    {
        waveform.PositionMs = player.PositionMs;
        if (player.State == AudioPreviewState.Stopped)
        {
            timer.Stop();
        }
    }

    private void PlaybackEnded()
    {
        timer.Stop();
        waveform.PositionMs = 0;
    }

    private void Tap()
    {
        if (player.State != AudioPreviewState.Playing)
        {
            alignmentText.Text = "Start playback before tapping so each marker has an audio position.";
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (taps.Count > 0 && Stopwatch.GetElapsedTime(taps[^1].Clock, now) > TimeSpan.FromSeconds(2.5))
        {
            taps.Clear();
        }

        var sourcePosition = player.PositionMs - waveform.AudioOffsetMs;
        if (sourcePosition < 0)
        {
            alignmentText.Text = "The playhead is still inside the leading gap; tap after the audio begins.";
            return;
        }

        taps.Add(new TapSample(now, sourcePosition));
        if (taps.Count > 16)
        {
            taps.RemoveAt(0);
        }

        if (taps.Count >= 2)
        {
            var first = taps[0].Clock;
            var tapTimes = taps.Select(value => Stopwatch.GetElapsedTime(first, value.Clock).TotalMilliseconds).ToArray();
            if (TapTempoCalculator.EstimateBpm(tapTimes) is { } estimate)
            {
                SetBpm(Math.Clamp(estimate, 20, 400));
            }
        }

        UpdateAlignment();
        UpdateTapCount();
    }

    private void ResetTaps()
    {
        taps.Clear();
        UpdateAlignment();
        UpdateTapCount();
    }

    private void UpdateTapCount() => tapCount.Text = $"{taps.Count} tap{(taps.Count == 1 ? string.Empty : "s")}";

    private void ApplyBpmInput()
    {
        var text = bpmInput.Text ?? string.Empty;
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && value is >= 20 and <= 400)
        {
            SetBpm(value);
        }
        else
        {
            bpmInput.Text = bpm.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private void SetBpm(double value)
    {
        bpm = value;
        bpmInput.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        waveform.Bpm = value;
        player.Bpm = value;
        UpdateAlignment();
    }

    private void UpdateAlignment()
    {
        alignment = BeatGridAlignmentCalculator.Fit(taps.Select(tap => tap.PositionMs).ToArray(), bpm);
        waveform.SetBeatAnchors(taps.Select(tap => tap.PositionMs).ToArray());
        if (alignment is null)
        {
            alignmentText.Text = "No beat anchors";
            return;
        }

        var placed = BeatGridAlignmentCalculator.Fit(
            taps.Select(tap => tap.PositionMs + waveform.AudioOffsetMs).ToArray(),
            bpm)!;
        alignmentText.Text = $"Suggested leading gap: {FormatMilliseconds(alignment.LeadingSilenceMs)}   |   "
            + $"Current gap: {FormatMilliseconds(waveform.AudioOffsetMs)}   |   "
            + $"Grid error: {placed.MeanTapErrorMs:0.#} ms";
    }

    private void CommitAudioOffset(double value)
    {
        var position = waveform.PositionMs;
        var state = player.State;
        player.AudioOffsetMs = value;
        if (state is AudioPreviewState.Playing or AudioPreviewState.Paused)
        {
            player.Seek(Math.Clamp(position, 0, player.DurationMs));
        }

        UpdateAlignment();
    }

    private static string FormatMilliseconds(double value) => TimeSpan.FromMilliseconds(value)
        .ToString("s\\.fff", CultureInfo.InvariantCulture);

    private sealed record TapSample(long Clock, double PositionMs);
}

internal sealed class BpmWaveformControl : Control
{
    private const double RulerHeight = 30;
    private readonly string name;
    private readonly WaveformEnvelope waveform;
    private double pixelsPerSecond = 90;
    private double positionMs;
    private double offsetMs;
    private double bpm = 120;
    private double[] tapPositions = [];
    private double audioOffsetMs;
    private AudioDrag? audioDrag;

    public BpmWaveformControl(string name, WaveformEnvelope waveform)
    {
        this.name = name;
        this.waveform = waveform;
        Focusable = true;
        ClipToBounds = true;
    }

    public event Action<double>? SeekRequested;
    public event Action<double>? ZoomChanged;
    public event Action<double>? AudioOffsetCommitted;
    public double DurationMs => waveform.DurationMs + audioOffsetMs;
    public double AudioOffsetMs => audioOffsetMs;

    public double PositionMs
    {
        get => positionMs;
        set
        {
            positionMs = Math.Clamp(value, 0, DurationMs);
            InvalidateVisual();
        }
    }

    public double Bpm
    {
        get => bpm;
        set
        {
            bpm = Math.Clamp(value, 20, 400);
            InvalidateVisual();
        }
    }

    public double PixelsPerSecond
    {
        get => pixelsPerSecond;
        set
        {
            pixelsPerSecond = Math.Clamp(value, 1, 500);
            ClampOffset();
            InvalidateVisual();
        }
    }

    public void Fit()
    {
        PixelsPerSecond = Math.Clamp((Bounds.Width - 2) * 1_000 / Math.Max(1, DurationMs), 1, 500);
        offsetMs = 0;
        ZoomChanged?.Invoke(PixelsPerSecond);
        InvalidateVisual();
    }

    public void CenterOn(double timeMs)
    {
        offsetMs = timeMs - VisibleDurationMs / 2;
        ClampOffset();
        InvalidateVisual();
    }

    public void SetBeatAnchors(IReadOnlyCollection<double> positions)
    {
        tapPositions = positions.ToArray();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brush("#10151B"), new Rect(Bounds.Size));
        context.FillRectangle(Brush("#171D24"), new Rect(0, 0, Bounds.Width, RulerHeight));
        DrawTimeGrid(context);

        var clip = ClipRect();
        if (audioOffsetMs > 0)
        {
            var gapLeft = XAt(0);
            var gapRight = XAt(audioOffsetMs);
            var visibleLeft = Math.Max(0, gapLeft);
            var visibleRight = Math.Min(Bounds.Width, gapRight);
            if (visibleRight > visibleLeft)
            {
                context.FillRectangle(Brush("#3A2A1E80"),
                    new Rect(visibleLeft, RulerHeight + 8, visibleRight - visibleLeft, clip.Height), 3);
                if (visibleRight - visibleLeft > 70)
                {
                    DrawText(context, $"GAP {FormatTime(audioOffsetMs)}", visibleLeft + 8, clip.Top + 8, 10,
                        Brushes.Orange, visibleRight - visibleLeft - 16);
                }
            }
        }

        context.FillRectangle(Brush("#243540"), clip, 4);
        context.DrawRectangle(new Pen(NameColorPalette.Brush($"bpm:{name}"), 1.5), clip, 4);
        DrawEnvelope(context, clip);
        DrawBeatGrid(context);
        DrawText(context, name, clip.Left + 10, clip.Top + 8, 12,
            NameColorPalette.Brush($"bpm:{name}"), clip.Width - 20);
        DrawTapMarkers(context);

        var playheadX = XAt(PositionMs);
        if (playheadX is >= 0 and <= double.MaxValue && playheadX <= Bounds.Width)
        {
            context.DrawLine(new Pen(Brush("#F45B69"), 1.5), new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
            context.FillRectangle(Brush("#F45B69"), new Rect(playheadX - 4, 0, 8, 7));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && ClipRect().Contains(point))
        {
            audioDrag = new AudioDrag(point.X, audioOffsetMs);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        PositionMs = Math.Clamp(offsetMs + point.X * 1_000 / PixelsPerSecond, 0, DurationMs);
        SeekRequested?.Invoke(PositionMs);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (audioDrag is not { } drag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var desired = drag.InitialOffsetMs + (e.GetPosition(this).X - drag.StartX) * 1_000 / PixelsPerSecond;
        audioOffsetMs = SnapAudioOffset(Math.Max(0, desired));
        PositionMs = Math.Min(PositionMs, DurationMs);
        ClampOffset();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (audioDrag is null)
        {
            return;
        }

        audioDrag = null;
        e.Pointer.Capture(null);
        AudioOffsetCommitted?.Invoke(audioOffsetMs);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var anchored = offsetMs + point.X * 1_000 / PixelsPerSecond;
            PixelsPerSecond *= e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
            offsetMs = anchored - point.X * 1_000 / PixelsPerSecond;
            ClampOffset();
            ZoomChanged?.Invoke(PixelsPerSecond);
        }
        else
        {
            offsetMs -= e.Delta.Y * VisibleDurationMs * 0.12;
            ClampOffset();
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private void DrawEnvelope(DrawingContext context, Rect rect)
    {
        if (waveform.Points == 0 || DurationMs <= 0)
        {
            return;
        }

        var pen = new Pen(Brush("#8DA7B8"), 0.8);
        var amplitude = rect.Height * 0.34;
        for (var x = 2d; x < Bounds.Width - 2; x++)
        {
            var sourceMs = offsetMs + x * 1_000 / PixelsPerSecond - audioOffsetMs;
            if (sourceMs < 0 || sourceMs > waveform.DurationMs)
            {
                continue;
            }

            var sample = sourceMs / waveform.DurationMs * (waveform.Points - 1);
            var index = Math.Clamp((int)sample, 0, waveform.Points - 1);
            var next = Math.Min(waveform.Points - 1, index + 1);
            var fraction = (float)(sample - index);
            var maximum = waveform.Maximums[index] + (waveform.Maximums[next] - waveform.Maximums[index]) * fraction;
            var minimum = waveform.Minimums[index] + (waveform.Minimums[next] - waveform.Minimums[index]) * fraction;
            context.DrawLine(pen,
                new Point(x, rect.Center.Y - maximum * amplitude),
                new Point(x, rect.Center.Y - minimum * amplitude));
        }
    }

    private void DrawTimeGrid(DrawingContext context)
    {
        var rawStepMs = 90_000 / PixelsPerSecond;
        var steps = new[] { 100d, 250, 500, 1_000, 2_000, 5_000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000 };
        var step = steps.FirstOrDefault(value => value >= rawStepMs);
        if (step == 0)
        {
            step = 600_000;
        }

        var first = Math.Floor(offsetMs / step) * step;
        for (var time = first; time <= offsetMs + VisibleDurationMs; time += step)
        {
            if (time < 0)
            {
                continue;
            }

            var x = XAt(time);
            context.DrawLine(new Pen(Brush("#42505D"), 1), new Point(x, 0), new Point(x, Bounds.Height));
            DrawText(context, FormatTime(time), x + 4, 7, 10, Brushes.LightGray, 80);
        }
    }

    private void DrawBeatGrid(DrawingContext context)
    {
        var beatMs = 60_000 / Bpm;
        var beatPixels = beatMs * PixelsPerSecond / 1_000;
        var barPixels = beatPixels * 4;
        var barStride = Math.Max(1, (int)Math.Ceiling(4 / barPixels));
        var labelStride = Math.Max(1, (int)Math.Ceiling(48 / barPixels));
        var showIndividualBeats = beatPixels >= 7;
        var firstBeat = Math.Max(0, (int)Math.Floor(offsetMs / beatMs));
        var lastBeat = (int)Math.Ceiling((offsetMs + VisibleDurationMs) / beatMs);
        for (var beat = firstBeat; beat <= lastBeat; beat++)
        {
            var bar = beat % 4 == 0;
            var barIndex = beat / 4;
            if (bar && barIndex % barStride != 0 || !bar && !showIndividualBeats)
            {
                continue;
            }

            var x = XAt(beat * beatMs);
            context.DrawLine(
                new Pen(Brush(bar ? "#D2A64D" : "#71808D"), bar ? 1.4 : 0.8),
                new Point(x, bar ? 0 : RulerHeight),
                new Point(x, Bounds.Height));
            if (bar && barIndex % labelStride == 0)
            {
                DrawText(context, $"B{barIndex + 1}", x + 3, 17, 9, Brush("#E9C779"), 42);
            }
        }
    }

    private void DrawTapMarkers(DrawingContext context)
    {
        var pen = new Pen(Brush("#FFB45E"), 1.5);
        for (var index = 0; index < tapPositions.Length; index++)
        {
            var x = XAt(tapPositions[index] + audioOffsetMs);
            if (x < 0 || x > Bounds.Width)
            {
                continue;
            }

            context.DrawLine(pen, new Point(x, RulerHeight - 2), new Point(x, Bounds.Height));
            var label = new Rect(Math.Clamp(x - 11, 0, Math.Max(0, Bounds.Width - 24)), RulerHeight - 17, 24, 15);
            context.FillRectangle(Brush("#8B5928"), label, 3);
            DrawText(context, $"T{index + 1}", label.Left + 3, label.Top + 1, 9, Brushes.White, label.Width - 5);
        }
    }

    private double XAt(double timeMs) => (timeMs - offsetMs) * PixelsPerSecond / 1_000;
    private double VisibleDurationMs => Math.Max(1, Bounds.Width * 1_000 / PixelsPerSecond);

    private Rect ClipRect() => new(
        XAt(audioOffsetMs),
        RulerHeight + 8,
        Math.Max(2, waveform.DurationMs * PixelsPerSecond / 1_000),
        Math.Max(20, Bounds.Height - RulerHeight - 16));

    private double SnapAudioOffset(double desired)
    {
        var toleranceMs = Math.Min(100, 10_000 / PixelsPerSecond);
        return BeatGridAlignmentCalculator.SnapAudioOffset(tapPositions, Bpm, desired, toleranceMs);
    }

    private void ClampOffset() => offsetMs = Math.Clamp(offsetMs, 0, Math.Max(0, DurationMs - VisibleDurationMs));

    private static string FormatTime(double value) => TimeSpan.FromMilliseconds(value)
        .ToString(value >= 60_000 ? "m\\:ss" : "s\\.fff", CultureInfo.InvariantCulture);

    private static void DrawText(
        DrawingContext context,
        string value,
        double x,
        double y,
        double size,
        IBrush brush,
        double maxWidth)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(text, new Point(x, y));
    }

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
    private sealed record AudioDrag(double StartX, double InitialOffsetMs);
}

internal sealed class BpmPreviewPlayer : IDisposable
{
    private const int SampleRate = 48_000;
    private readonly string wavPath;
    private readonly double sourceDurationMs;
    private readonly Stopwatch clock = new();
    private WaveOutEvent? output;
    private WaveStream? reader;
    private bool suppressStopped;
    private int outputBytesPerSecond;
    private double startMs;
    private double stoppedPositionMs;
    private double bpm = 120;
    private bool metronomeEnabled;
    private double audioOffsetMs;
    private double gain = 1;
    private VolumeSampleProvider? volume;

    public BpmPreviewPlayer(string wavPath, double durationMs)
    {
        this.wavPath = Path.GetFullPath(wavPath);
        sourceDurationMs = durationMs;
    }

    public event Action? PlaybackEnded;
    public AudioPreviewState State { get; private set; }
    public double PositionMs
    {
        get
        {
            if (State == AudioPreviewState.Playing)
            {
                try
                {
                    if (output is not null && outputBytesPerSecond > 0)
                    {
                        return Math.Clamp(startMs + output.GetPosition() * 1_000d / outputBytesPerSecond, 0, DurationMs);
                    }
                }
                catch (MmException)
                {
                }

                return Math.Clamp(startMs + clock.Elapsed.TotalMilliseconds, 0, DurationMs);
            }

            return stoppedPositionMs;
        }
    }

    public double Bpm
    {
        get => bpm;
        set => bpm = Math.Clamp(value, 20, 400);
    }

    public bool MetronomeEnabled
    {
        get => metronomeEnabled;
        set => metronomeEnabled = value;
    }

    public double Gain
    {
        get => gain;
        set
        {
            gain = Math.Clamp(value, 0, 1);
            if (volume is not null)
            {
                volume.Volume = (float)gain;
            }
        }
    }

    public double AudioOffsetMs
    {
        get => audioOffsetMs;
        set => audioOffsetMs = Math.Max(0, value);
    }

    public double DurationMs => sourceDurationMs + AudioOffsetMs;

    public void Play(double positionMs) => StartAt(positionMs, true);

    public void TogglePause()
    {
        if (State == AudioPreviewState.Playing)
        {
            stoppedPositionMs = PositionMs;
            output?.Pause();
            clock.Stop();
            State = AudioPreviewState.Paused;
        }
        else if (State == AudioPreviewState.Paused)
        {
            startMs = stoppedPositionMs;
            clock.Restart();
            output?.Play();
            State = AudioPreviewState.Playing;
        }
    }

    public void Seek(double positionMs) => StartAt(positionMs, State == AudioPreviewState.Playing);

    public void Stop()
    {
        stoppedPositionMs = 0;
        DisposeOutput();
        State = AudioPreviewState.Stopped;
    }

    public void Dispose() => Stop();

    private void StartAt(double positionMs, bool play)
    {
        DisposeOutput();
        stoppedPositionMs = Math.Clamp(positionMs, 0, DurationMs);
        if (stoppedPositionMs >= DurationMs - 0.5)
        {
            State = AudioPreviewState.Stopped;
            return;
        }

        var sourcePositionMs = Math.Max(0, stoppedPositionMs - AudioOffsetMs);
        reader = new WaveFileReader(wavPath) { CurrentTime = TimeSpan.FromMilliseconds(sourcePositionMs) };
        ISampleProvider audio = reader.ToSampleProvider();
        audio = audio.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(audio),
            2 => audio,
            _ => new MultichannelToStereoSampleProvider(audio)
        };
        if (audio.WaveFormat.SampleRate != SampleRate)
        {
            audio = new WdlResamplingSampleProvider(audio, SampleRate);
        }

        if (stoppedPositionMs < AudioOffsetMs)
        {
            audio = new OffsetSampleProvider(audio)
            {
                DelayBy = TimeSpan.FromMilliseconds(AudioOffsetMs - stoppedPositionMs)
            };
        }

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
        mixer.AddMixerInput(audio);
        mixer.AddMixerInput(new TapMetronomeSampleProvider(
            SampleRate,
            stoppedPositionMs,
            () => Bpm,
            () => MetronomeEnabled));

        output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
        output.PlaybackStopped += OutputStopped;
        volume = new VolumeSampleProvider(mixer) { Volume = (float)Gain };
        var provider = volume.Take(TimeSpan.FromMilliseconds(DurationMs - stoppedPositionMs)).ToWaveProvider();
        outputBytesPerSecond = provider.WaveFormat.AverageBytesPerSecond;
        output.Init(provider);
        State = play ? AudioPreviewState.Playing : AudioPreviewState.Paused;
        startMs = stoppedPositionMs;
        clock.Restart();
        if (play)
        {
            output.Play();
        }
        else
        {
            clock.Stop();
        }
    }

    private void OutputStopped(object? sender, StoppedEventArgs e)
    {
        if (suppressStopped || !ReferenceEquals(sender, output))
        {
            return;
        }

        clock.Stop();
        stoppedPositionMs = 0;
        State = AudioPreviewState.Stopped;
        PlaybackEnded?.Invoke();
    }

    private void DisposeOutput()
    {
        suppressStopped = true;
        try
        {
            clock.Stop();
            if (output is not null)
            {
                output.PlaybackStopped -= OutputStopped;
                output.Stop();
                output.Dispose();
            }

            reader?.Dispose();
        }
        finally
        {
            output = null;
            reader = null;
            volume = null;
            outputBytesPerSecond = 0;
            suppressStopped = false;
        }
    }

    private sealed class TapMetronomeSampleProvider(
        int sampleRate,
        double startMs,
        Func<double> bpm,
        Func<bool> enabled) : ISampleProvider
    {
        private double frame = startMs / 1_000 * sampleRate;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            if (!enabled())
            {
                frame += count / 2d;
                return count;
            }

            var beatFrames = sampleRate * 60 / Math.Clamp(bpm(), 20, 400);
            for (var index = 0; index + 1 < count; index += 2, frame++)
            {
                var beat = (long)Math.Floor(frame / beatFrames);
                var phase = frame - beat * beatFrames;
                if (phase >= sampleRate * 0.055)
                {
                    continue;
                }

                var time = phase / sampleRate;
                var frequency = beat % 4 == 0 ? 1_760d : 1_180d;
                var value = (float)(Math.Sin(2 * Math.PI * frequency * time) * Math.Exp(-time * 75) * 0.45);
                buffer[offset + index] = value;
                buffer[offset + index + 1] = value;
            }

            return count;
        }
    }
}
