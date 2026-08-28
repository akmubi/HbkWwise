using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HbkWwise.Core;
using NAudio.Wave;

namespace HbkWwise.Gui;

public sealed partial class MainWindow : Window
{
    private static readonly Guid MetronomeTrackId = new("90e53fdc-e629-4cc7-9d3a-72a534baec02");
    private static readonly DataFormat<string> ImportedAudioDataFormat = DataFormat.CreateStringApplicationFormat("hbkwwise.imported-audio");
    private static readonly DataFormat<string> CatalogClipDataFormat = DataFormat.CreateStringApplicationFormat("hbkwwise.catalog-clip");

    private readonly TimelineControl timeline;
    private readonly string? startupProjectPath;
    private readonly MusicTimelineDocument document = new(136.05, 4, 1, createDefaultTrack: false);
    private readonly GuiSettings settings = GuiSettingsStore.Load();
    private readonly ObservableCollection<ImportedAudio> importedAudio = [];
    private readonly ListBox importedAudioList = new();
    private readonly ObservableCollection<ClipCatalogItem> clipCatalog = [];
    private readonly ObservableCollection<ClipCatalogItem> pinnedClips = [];
    private readonly HashSet<string> pinnedClipKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox clipCatalogList = new();
    private readonly ListBox pinnedClipList = new();
    private readonly TextBox clipSearch = new() { Watermark = "Search imported and game audio" };
    private readonly ObservableCollection<TimelineTab> timelineTabItems = [];
    private readonly ListBox timelineTabList = new();
    private readonly AudioPreviewPlayer previewPlayer = new();
    private readonly TextBox search = new() { Watermark = "Search" };
    private readonly CheckBox browserHasAudio = new() { Content = "Has audio", IsChecked = true };
    private readonly TreeView results = new();
    private readonly StackPanel details = new() { Spacing = 5 };
    private readonly GuiLog log = new();

    private readonly SelectableTextBlock status = new()
    {
        Text = "Preparing Hi-Fi RUSH game data",
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBox bpmInput = new()
    {
        Text = "136.05",
        Width = 82,
        Height = 28,
        Padding = new Thickness(6, 2),
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private readonly CheckBox snapEnabled = new() { Content = "Snap", IsChecked = true };
    private readonly CheckBox showWwiseCues = new() { Content = "Wwise cues", IsChecked = true };
    private readonly CheckBox metronomeEnabled = new() { Content = "Metronome" };
    private readonly Slider masterVolume = new() { Minimum = 0, Maximum = 1, Width = 105 };
    private readonly SelectableTextBlock masterVolumeValue = new()
    {
        Width = 38,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        Foreground = Brushes.LightGray
    };
    private readonly Slider zoom = new() { Minimum = 1, Maximum = 500, Width = 130 };
    private readonly Button playTimeline = new() { Content = "Play", IsEnabled = false };
    private readonly Button pausePreview = new() { Content = "Pause", IsEnabled = false };
    private readonly Button stopPreview = new() { Content = "Stop" };
    private readonly SelectableTextBlock transportTime = new()
    {
        Text = "0:00.000",
        Width = 70,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        Foreground = Brushes.LightGray
    };
    private readonly Button buildPak = new() { Content = "Build mod PAK", IsEnabled = false };
    private readonly MenuItem buildPakMenu = new() { Header = "_Build mod PAK", IsEnabled = false };
    private readonly MenuItem openRecentProject = new() { Header = "Open _recent project", IsEnabled = false };
    private readonly SelectableTextBlock timelineHeading = new()
    {
        Text = "COMPOSITION TIMELINE",
        Foreground = Brushes.Gray,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly ProgressBar progress = new() { Width = 150, IsIndeterminate = true, IsVisible = false };
    private readonly Button cancelOperation = new() { Content = "Cancel", IsVisible = false, Padding = new Thickness(9, 3) };
    private readonly TextBlock timelineLoadingMessage = new()
    {
        Text = "Loading timeline audio",
        Foreground = Brushes.LightGray,
        FontSize = 11
    };
    private readonly ProgressBar timelineLoadingProgress = new()
    {
        Width = 110,
        Height = 4,
        IsIndeterminate = true
    };
    private readonly Border timelineLoadingOverlay = new()
    {
        IsVisible = false,
        Background = ColorBrush("#E91A2028"),
        BorderBrush = ColorBrush("#596572"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 7),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(10)
    };

    private readonly Dictionary<string, WaveformEnvelope> waveformCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(uint BankId, uint EventId), BrowserNode[]> eventStructures = [];
    private readonly DispatcherTimer transportTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<uint, ScopeReplacement> scopeReplacements = [];
    private readonly Dictionary<uint, StructuralImport> scopeImports = [];

    private CancellationTokenSource? indexOperation;
    private CancellationTokenSource? browserRefresh;
    private CancellationTokenSource? previewOperation;
    private CancellationTokenSource? waveformOperation;
    private WwiseIndex? index;
    private BrowserSearchIndex? browserSearchIndex;
    private string? currentIndexPath;
    private string? currentProjectPath;
    private LoadedEventTimeline? loadedTimeline;
    private bool renderingComposition;
    private bool restoringProject;
    private bool projectDirty;
    private string? cleanProjectFingerprint;
    private bool closingConfirmed;
    private bool resourcesDisposed;
    private bool suppressBrowserSelection;
    private BrowserNode? acceptedBrowserSelection;
    private bool operationBusy;
    private bool followPlaybackTimeline;
    private double? mixRefreshStartMs;
    private bool pauseAfterMixRefresh;
    private uint? previewSegmentId;
    private uint? bpmEditorSegmentId;
    private Point? importedAudioDragStart;
    private ImportedAudio? importedAudioDragItem;
    private TimelineTab? activeTimelineTab;
    private bool switchingTimelineTab;
    private bool synchronizingTimelineTabs;
    private CopiedTimelineClip? copiedTimelineClip;
    private Point? catalogDragStart;
    private ClipCatalogItem? catalogDragItem;
    private bool selectingCatalogItem;
    private bool timelineContentLoading;
    private bool playWhenTimelineReady;
    private MediaRecord? ActiveStandaloneMedia => activeTimelineTab?.StandaloneMedia;

    public MainWindow(string? startupProjectPath = null)
    {
        this.startupProjectPath = startupProjectPath;
        Title = "HBK Wwise";
        Icon = new WindowIcon(AssetLoader.Open(
            new Uri("avares://HbkWwise/Assets/HbkWwise.png")));
        Width = 1500;
        Height = 900;
        MinWidth = 980;
        MinHeight = 620;
        Background = ColorBrush("#0F1217");

        timeline = new TimelineControl(document);
        timeline.ShowMarkers = showWwiseCues.IsChecked == true;

        masterVolume.Value = settings.MasterVolume;
        metronomeEnabled.IsChecked = settings.MetronomeEnabled;
        masterVolumeValue.Text = $"{settings.MasterVolume * 100:0}%";
        previewPlayer.MasterGain = settings.MasterVolume;

        ToolTip.SetTip(showWwiseCues, "Show segment-level Wwise Entry, Exit, and custom synchronization cues. They are shared by every parallel track in their Music Segment. Drag a cue to adjust it; moving Exit also resizes that segment.");
        ToolTip.SetTip(metronomeEnabled, "Add a click track to preview playback using the BPM of whichever Music Segment is active.");
        ToolTip.SetTip(masterVolume, "Master preview volume for timeline, clip, media, and imported-audio playback.");

        transportTimer.Tick += (_, _) => UpdateTransportPosition();
        previewPlayer.PlaybackEnded += () => Dispatcher.UIThread.Post(PlaybackEnded);
        Content = BuildLayout();

        ResetTimelineTabs();
        UpdateRecentProjectMenu();
        UpdateWindowTitle();
        ShowInspector("INSPECTOR\n\nSelect a soundbank object to see its properties.");
        AttachEvents();
        SetStatus("Preparing Hi-Fi RUSH game data");
        UpdateTimelineControlAvailability();

        Opened += LoadInitialContentAsync;
        Closing += OnWindowClosing;
    }

    public void ReportError(string message) => SetStatus($"Unexpected GUI error: {message}", GuiLogLevel.Error);

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
        root.Children.Add(BuildMenu());

        var controls = BuildTimelineControls();
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("400,9,*")
        };
        Grid.SetRow(workspace, 2);

        var browser = BuildBrowser();
        browser.MinWidth = 320;
        workspace.Children.Add(browser);

        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            Background = ColorBrush("#303741")
        };
        Grid.SetColumn(splitter, 1);
        workspace.Children.Add(splitter);

        var timelinePanel = BuildTimelinePanel();
        Grid.SetColumn(timelinePanel, 2);
        workspace.Children.Add(timelinePanel);
        root.Children.Add(workspace);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private Control BuildMenu()
    {
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(MenuAction("_New project", NewProject));
        file.Items.Add(MenuAction("_Open project    Ctrl+O", OpenProjectAsync));
        openRecentProject.Click += OpenRecentProjectAsync;
        file.Items.Add(openRecentProject);
        file.Items.Add(MenuAction("_Save project    Ctrl+S", SaveProjectAsync));
        file.Items.Add(MenuAction("Save project _as", SaveProjectAsAsync));
        file.Items.Add(new Separator());
        file.Items.Add(buildPakMenu);
        file.Items.Add(new Separator());
        file.Items.Add(MenuAction("E_xit", (_, _) => Close()));

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(MenuAction("_Undo    Ctrl+Z", (_, _) => timeline.Undo()));
        edit.Items.Add(MenuAction("_Redo    Ctrl+Y", (_, _) => timeline.Redo()));
        edit.Items.Add(new Separator());
        edit.Items.Add(MenuAction("_Copy selected clip    Ctrl+C", (_, _) => CopySelectedClip()));
        edit.Items.Add(MenuAction("_Paste clip at playhead    Ctrl+V", PasteClipAsync));
        edit.Items.Add(new Separator());
        edit.Items.Add(MenuAction("_Duplicate selected clip    Ctrl+D", (_, _) => timeline.DuplicateSelected()));
        edit.Items.Add(MenuAction("_Split at playhead    S", (_, _) => timeline.SplitSelected()));
        edit.Items.Add(MenuAction("_Delete clip    Delete", (_, _) => timeline.DeleteSelected()));
        edit.Items.Add(new Separator());
        edit.Items.Add(MenuAction("_Preferences", OpenSettingsAsync));

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(MenuAction("_Fit timeline to width    Ctrl+F", (_, _) => FitTimeline()));
        view.Items.Add(new Separator());
        view.Items.Add(MenuAction("_Log", (_, _) => new LogDialog(log).Show(this)));

        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(MenuAction("_About HBK Wwise", async (_, _) => await new AboutDialog().ShowDialog(this)));
        help.Items.Add(new Separator());
        help.Items.Add(MenuAction("_License", (_, _) => OpenPackagedDocument("LICENSE.txt")));
        help.Items.Add(MenuAction("_Third-party notices", (_, _) => OpenPackagedDocument("THIRD-PARTY-NOTICES.txt")));

        return new Menu
        {
            ItemsSource = new[] { file, edit, view, help },
            Background = ColorBrush("#171C23")
        };
    }

    private Control BuildTimelineControls()
    {
        var settings = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        settings.Children.Add(snapEnabled);
        settings.Children.Add(showWwiseCues);
        settings.Children.Add(metronomeEnabled);
        settings.Children.Add(Label("Master"));
        settings.Children.Add(masterVolume);
        settings.Children.Add(masterVolumeValue);
        settings.Children.Add(Label("Zoom"));

        zoom.Value = timeline.PixelsPerSecond;
        zoom.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                timeline.PixelsPerSecond = zoom.Value;
            }
        };
        settings.Children.Add(zoom);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 5)
        };
        actions.Children.Add(playTimeline);
        actions.Children.Add(pausePreview);
        actions.Children.Add(stopPreview);
        actions.Children.Add(transportTime);
        actions.Children.Add(buildPak);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        row.Children.Add(actions);

        Grid.SetColumn(settings, 1);
        row.Children.Add(settings);

        return new Border
        {
            Background = ColorBrush("#151A20"),
            BorderBrush = ColorBrush("#303741"),
            BorderThickness = new Thickness(0, 1),
            Child = row
        };
    }

    private Grid BuildTimelinePanel()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };
        timelineTabList.ItemsSource = timelineTabItems;
        timelineTabList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        });
        timelineTabList.ItemTemplate = new FuncDataTemplate<TimelineTab>(
            (item, _) => item is null ? new Border() : TimelineTabItem(item)
        );

        timelineTabList.Height = 46;
        timelineTabList.Background = ColorBrush("#11161C");

        ScrollViewer.SetHorizontalScrollBarVisibility(timelineTabList, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(timelineTabList, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        panel.Children.Add(timelineTabList);

        var heading = new Border
        {
            Background = ColorBrush("#151A20"),
            Padding = new Thickness(12, 7),
            Child = timelineHeading
        };
        Grid.SetRow(heading, 1);
        panel.Children.Add(heading);

        var timelineSurface = new Grid();
        timelineSurface.Children.Add(timeline);
        timelineSurface.Children.Add(bpmInput);

        var loadingContent = new StackPanel { Spacing = 6 };
        loadingContent.Children.Add(timelineLoadingMessage);
        loadingContent.Children.Add(timelineLoadingProgress);
        timelineLoadingOverlay.Child = loadingContent;
        timelineSurface.Children.Add(timelineLoadingOverlay);

        var body = new Border
        {
            BorderBrush = ColorBrush("#303741"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = timelineSurface
        };
        Grid.SetRow(body, 2);
        panel.Children.Add(body);
        return panel;
    }

    private Control TimelineTabItem(TimelineTab item)
    {
        var label = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Padding = new Thickness(12, 0),
            Height = 34,
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
            ContextMenu = BuildTimelineTabContextMenu(item)
        };

        void Refresh()
        {
            label.Text = item.IsDirty ? $"{item.Title} *" : item.Title;
            label.Foreground = item.IsDirty ? ColorBrush("#FFC36E") : Brushes.LightGray;
            label.FontStyle = item.IsPreview ? FontStyle.Italic : FontStyle.Normal;
            border.Background = item.IsDirty ? ColorBrush("#382A1D") : Brushes.Transparent;
            border.BorderBrush = item.IsDirty ? ColorBrush("#C58437") : Brushes.Transparent;
            border.BorderThickness = new Thickness(0, 0, 0, item.IsDirty ? 2 : 0);
            ToolTip.SetTip(border, item.IsPreview
                ? "Preview timeline - click this tab or edit its contents to keep it open"
                : item.Title);
        }

        border.PointerPressed += (_, _) => PromoteTimelineTab(item);
        item.VisualChanged += Refresh;
        Refresh();
        return border;
    }

    private ContextMenu BuildTimelineTabContextMenu(TimelineTab item)
    {
        var close = MenuAction("Close timeline", (_, _) => CloseTimelineTab(item));
        var copyName = MenuAction("Copy timeline name", async (_, _) => await CopyTextAsync(item.Title));
        var menu = new ContextMenu { ItemsSource = new Control[] { copyName, new Separator(), close } };

        menu.Opening += (_, _) =>
        {
            timelineTabList.SelectedItem = item;
        };
        return menu;
    }

    private Control BuildBrowser()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("3*,5,2*"),
            Margin = new Thickness(10),
            MinWidth = 320
        };

        var gameContent = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var filters = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        filters.Children.Add(search);
        browserHasAudio.Margin = new Thickness(8, 0, 0, 0);
        browserHasAudio.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(browserHasAudio, "Show only soundbanks and Events that reference playable audio.");
        Grid.SetColumn(browserHasAudio, 1);
        filters.Children.Add(browserHasAudio);
        gameContent.Children.Add(filters);

        Grid.SetRow(results, 1);
        results.ItemTemplate = new FuncTreeDataTemplate<BrowserNode>(
            (entry, _) => entry is null ? new Border() : BrowserItem(entry),
            entry => entry.Children
        );
        gameContent.Children.Add(results);

        importedAudioList.ItemsSource = importedAudio;
        clipCatalogList.ItemsSource = clipCatalog;
        clipCatalogList.ItemTemplate = new FuncDataTemplate<ClipCatalogItem>(
            (item, _) => item is null ? new Border() : ClipCatalogRow(item)
        );
        pinnedClipList.ItemsSource = pinnedClips;
        pinnedClipList.ItemTemplate = new FuncDataTemplate<ClipCatalogItem>(
            (item, _) => item is null ? new Border() : ClipCatalogRow(item)
        );

        var addAudio = new Button { Content = "Import audio", Padding = new Thickness(10, 4) };
        addAudio.Click += AddAudioAsync;

        var availableContent = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
        availableContent.Children.Add(clipSearch);

        Grid.SetRow(addAudio, 1);
        addAudio.Margin = new Thickness(0, 7, 0, 7);
        addAudio.HorizontalAlignment = HorizontalAlignment.Left;
        availableContent.Children.Add(addAudio);

        Grid.SetRow(clipCatalogList, 2);
        availableContent.Children.Add(clipCatalogList);
        var catalogHint = new TextBlock
        {
            Text = "Imported audio appears first. Drag any clip onto an authored track; game audio is decoded and assigned through that track's Wwise template.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 7, 2, 2)
        };
        Grid.SetRow(catalogHint, 3);
        availableContent.Children.Add(catalogHint);

        var pinnedContent = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        pinnedContent.Children.Add(pinnedClipList);

        var pinnedHint = new TextBlock
        {
            Text = "Pin frequently reused imported or game clips from the Available Clips context menu.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 7, 2, 2)
        };
        Grid.SetRow(pinnedHint, 1);
        pinnedContent.Children.Add(pinnedHint);

        var tabs = new TabControl { MinWidth = 300 };
        tabs.Items.Add(BrowserTab("GAME", "Game Content", gameContent));
        tabs.Items.Add(BrowserTab("CLIPS", "Available Clips", availableContent));
        tabs.Items.Add(BrowserTab("PINNED", "Pinned Clips", pinnedContent));
        grid.Children.Add(tabs);

        var detailSplitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Rows,
            Background = ColorBrush("#303741")
        };
        Grid.SetRow(detailSplitter, 1);
        grid.Children.Add(detailSplitter);

        var detailBorder = new Border
        {
            BorderBrush = ColorBrush("#303741"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new ScrollViewer
            {
                Content = details,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
        Grid.SetRow(detailBorder, 2);
        grid.Children.Add(detailBorder);
        return grid;
    }

    private static TabItem BrowserTab(string header, string description, Control content)
    {
        var item = new TabItem
        {
            Header = new TextBlock
            {
                Text = header,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap
            },
            Content = content
        };
        ToolTip.SetTip(item, description);
        return item;
    }

    private Control ImportedAudioItem(ImportedAudio item)
    {
        var row = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        row.Children.Add(new TextBlock
        {
            Text = "⠿",
            FontSize = 17,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var name = new TextBlock
        {
            Text = item.Name,
            Foreground = NameColorPalette.Brush($"imported:{item.Path}"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var details = new TextBlock
        {
            Text = $"{FormatMs(item.DurationMs)}  |  {item.Format.Channels} ch  |  {item.Format.SampleRate:N0} Hz  |  {item.Path}",
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(details, 1);
        Grid.SetColumn(details, 1);
        row.Children.Add(details);

        row.ContextMenu = BuildAudioLibraryContextMenu(item);
        row.ContextRequested += (_, _) => importedAudioList.SelectedItem = item;
        return row;
    }

    private Control ClipCatalogRow(ClipCatalogItem item)
    {
        var row = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        row.Children.Add(new TextBlock
        {
            Text = "☰",
            FontSize = 15,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            Foreground = NameColorPalette.Brush(item.Media is { } media
                ? $"audio:{media.Bank}:{item.Name}"
                : $"imported:{item.Imported?.Path ?? item.Name}"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 10.5,
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(detail, 1);
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);

        row.ContextMenu = BuildCatalogContextMenu(item);
        row.ContextRequested += (_, _) => SelectCatalogItem(item);
        return row;
    }

    private ContextMenu BuildCatalogContextMenu(ClipCatalogItem item)
    {
        var play = MenuAction("Play", (_, _) => PlayCatalogClipAsync(item));
        var calculate = MenuAction("Calculate BPM", async (_, _) => await CalculateCatalogBpmAsync(item));
        var add = MenuAction("Add to selected track at playhead", (_, _) => AddCatalogClipAsync(item));
        var pin = MenuAction("Pin clip", (_, _) => TogglePinnedClip(item));
        var items = new List<Control> { play, calculate, add, new Separator(), pin };

        if (item.Media is { } standaloneMedia)
        {
            items.Insert(2, MenuAction(
                "Open sound editor",
                async (_, _) => await OpenStandaloneSoundEditorAsync(standaloneMedia)
            ));
        }

        if (item.Media?.IsMusic == true)
        {
            items.Insert(2, MenuAction(
                "Show all timeline occurrences",
                async (_, _) => await OpenMediaOccurrencesAsync(item)
            ));
        }

        if (item.Imported is not null)
        {
            items.Add(MenuAction("Remove imported source", (_, _) => RemoveImportedAudio()));
        }

        var menu = new ContextMenu { ItemsSource = items };
        menu.Opening += (_, _) =>
        {
            SelectCatalogItem(item);
            add.IsEnabled = timeline.SelectedTrackId is not null;
            pin.Header = pinnedClipKeys.Contains(item.Key) ? "Unpin clip" : "Pin clip";
        };
        return menu;
    }

    private void SelectCatalogItem(ClipCatalogItem item)
    {
        selectingCatalogItem = true;
        try
        {
            if (clipCatalog.Contains(item))
            {
                clipCatalogList.SelectedItem = item;
            }

            if (pinnedClips.Contains(item))
            {
                pinnedClipList.SelectedItem = item;
            }
            importedAudioList.SelectedItem = item.Imported;
        }
        finally
        {
            selectingCatalogItem = false;
        }
    }

    private void TogglePinnedClip(ClipCatalogItem item)
    {
        if (!pinnedClipKeys.Remove(item.Key))
        {
            pinnedClipKeys.Add(item.Key);
            SetStatus($"Pinned {item.Name}");
        }
        else
        {
            SetStatus($"Unpinned {item.Name}");
        }

        RefreshPinnedClips();
        MarkProjectDirty();
    }

    private void RefreshClipCatalog()
    {
        var query = clipSearch.Text?.Trim() ?? string.Empty;

        static bool Matches(string query, params string?[] values) =>
          query.Length == 0 || values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

        var items = importedAudio
            .Where(audio => Matches(query, audio.Name, audio.Path))
            .Select(audio => ClipCatalogItem.FromImported(audio))
            .Concat((index?.Media ?? [])
                .Where(media => media.IsPlayableAudio)
                .Where(media => Matches(query, media.SourceName, media.Id.ToString(), media.Bank, media.Path))
                .GroupBy(media => (media.Id, media.Bank))
                .Select(group => ClipCatalogItem.FromMedia(group.First()))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        clipCatalog.Clear();
        foreach (var item in items)
        {
            clipCatalog.Add(item);
        }

        RefreshPinnedClips();
    }

    private void RefreshPinnedClips()
    {
        var available = importedAudio.Select(ClipCatalogItem.FromImported)
            .Concat((index?.Media ?? [])
                .Where(media => media.IsPlayableAudio)
                .GroupBy(media => (media.Id, media.Bank))
                .Select(group => ClipCatalogItem.FromMedia(group.First())))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        pinnedClips.Clear();
        foreach (var key in pinnedClipKeys)
        {
            if (available.TryGetValue(key, out var item))
            {
                pinnedClips.Add(item);
            }
        }
    }

    private ClipCatalogItem? FindCatalogItem(string key) =>
        clipCatalog.Concat(pinnedClips).FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private ClipCatalogItem? CatalogItemForTimelineClip(MusicTimelineClip clip)
    {
        if (!string.IsNullOrWhiteSpace(clip.SourcePath))
        {
            var source = Path.GetFullPath(clip.SourcePath);
            var imported = importedAudio.FirstOrDefault(
                item => Path.GetFullPath(item.Path).Equals(source, StringComparison.OrdinalIgnoreCase)
            );

            if (imported is not null)
            {
                return ClipCatalogItem.FromImported(imported);
            }
        }

        if (clip.MediaId is not { } mediaId)
        {
            return null;
        }

        var preferredBank = loadedTimeline?.Event.Bank ?? ActiveStandaloneMedia?.Bank;
        var media = index?.Media.FirstOrDefault(
            item => item.Id == mediaId && preferredBank is not null && item.Bank.Equals(preferredBank, StringComparison.OrdinalIgnoreCase)
        ) ?? index?.Media.FirstOrDefault(item => item.Id == mediaId);

        return media is null ? null : ClipCatalogItem.FromMedia(media);
    }

    private async Task CopyTextAsync(string value)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetStatus("The system clipboard is unavailable", GuiLogLevel.Warning);
            return;
        }

        await clipboard.SetTextAsync(value);
        SetStatus($"Copied {value}");
    }

    private async void PlayCatalogClipAsync(ClipCatalogItem item)
    {
        SelectCatalogItem(item);
        if (item.Imported is not null)
        {
            PlayImportedAudioAsync(null, new RoutedEventArgs());
            return;
        }

        if (item.Media is { } media)
        {
            await PreviewMediaAsync(media);
        }
    }

    private async void AddCatalogClipAsync(ClipCatalogItem item) => await AddCatalogClipAsync(item, null);

    private async Task AddCatalogClipAsync(ClipCatalogItem item, Point? position)
    {
        SelectCatalogItem(item);
        if (item.Imported is { } imported)
        {
            AddImportedToTimeline(imported, position);
            return;
        }

        if (item.Media is not { } media)
        {
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(required: true);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Preparing game clip {media.Id}", stopCurrentPlayback: false);
        try
        {
            var wav = await PrepareMediaWavAsync(media.Id, aesKey, operation.Token);
            var audio = await ImportAudioAsync(wav, Path.GetFileNameWithoutExtension(media.SourceName));

            AddImportedToTimeline(audio, position);
            SetStatus($"Added game clip {media.Id} to the selected Wwise track");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Adding game clip cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Could not add game clip", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private Control BrowserItem(BrowserNode entry)
    {
        var item = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(4, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = entry.Media is { } media
                ? NameColorPalette.Brush($"audio:{media.Bank}:{entry.Name}")
                : Brushes.LightGray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        top.Children.Add(name);

        var kind = new Border
        {
            Background = ColorBrush(entry.Kind switch
            {
                "EVENT" => "#5B3F73",
                "BANK" => "#5C5732",
                "ACTION" => "#73483F",
                "SEGMENT" => "#3F7357",
                "GROUP" => "#414955",
                _ => "#315F73"
            }),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            Child = new TextBlock { Text = entry.Kind, FontSize = 9, Foreground = Brushes.White }
        };
        Grid.SetColumn(kind, 1);
        top.Children.Add(kind);
        item.Children.Add(top);

        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("92,125,*"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        Grid.SetRow(bottom, 1);
        bottom.Children.Add(new TextBlock
        {
            Text = entry.Id,
            FontSize = 11,
            Foreground = Brushes.Gray
        });

        var type = new TextBlock
        {
            Text = entry.Type,
            FontSize = 11,
            Foreground = Brushes.LightBlue
        };
        Grid.SetColumn(type, 1);
        bottom.Children.Add(type);

        var location = new TextBlock
        {
            Text = entry.Location,
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(location, 2);
        bottom.Children.Add(location);
        item.Children.Add(bottom);

        item.ContextMenu = BuildBrowserContextMenu(entry);
        item.ContextRequested += (_, _) => SelectBrowserNodeForContext(entry);
        item.PointerPressed += (_, args) => ActivateBrowserNode(entry, args);
        return item;
    }

    private void ActivateBrowserNode(BrowserNode node, PointerPressedEventArgs args)
    {
        if (args.ClickCount != 1 || !args.GetCurrentPoint(results).Properties.IsLeftButtonPressed)
        {
            return;
        }

        results.SelectedItem = node;
        if (node.Children.Count > 0 && BrowserContainer(node) is { IsExpanded: true } expanded)
        {
            expanded.IsExpanded = false;
            ShowInspector(node.Detail);
            SetStatus($"Collapsed {node.Name}");
            return;
        }

        ShowBrowserSelection();
    }

    private ContextMenu BuildAudioLibraryContextMenu(ImportedAudio audio)
    {
        var play = MenuAction("Play", PlayImportedAudioAsync);
        var add = MenuAction("Add to selected track at playhead", (_, _) => AddImportedAtPlayhead());
        var remove = MenuAction("Remove imported source", (_, _) => RemoveImportedAudio());
        var menu = new ContextMenu
        {
            ItemsSource = new Control[] { play, add, new Separator(), remove }
        };

        menu.Opening += (_, _) =>
        {
            importedAudioList.SelectedItem = audio;
            add.IsEnabled = timeline.SelectedTrackId is not null;
        };
        return menu;
    }

    private ContextMenu BuildBrowserContextMenu(BrowserNode node)
    {
        var items = new List<Control>();
        MenuItem? addMedia = null;

        if (node.Kind == "EVENT" || node.Segment is not null)
        {
            items.Add(MenuAction("Open in timeline", (_, _) => OpenBrowserNodeFromContext(node)));
        }

        if (node.Media is { } media)
        {
            var play = MenuAction("Play audio", PreviewSelectedGameMediaAsync);
            var edit = MenuAction("Open sound editor", async (_, _) => await OpenStandaloneSoundEditorAsync(media));
            addMedia = MenuAction("Add as clip to selected track", (_, _) => AddSelectedMedia());

            var import = MenuAction("Import into Available Clips", ImportSelectedGameMediaAsync);
            var export = MenuAction("Export audio", ExportSelectedGameMediaAsync);
            play.IsEnabled = media.IsPlayableAudio;
            edit.IsEnabled = media.IsPlayableAudio;
            addMedia.IsEnabled = media.IsPlayableAudio && timeline.SelectedTrackId is not null;
            import.IsEnabled = media.IsPlayableAudio;
            export.IsEnabled = media.IsPlayableAudio;

            items.Add(play);
            items.Add(edit);
            items.Add(addMedia);
            items.Add(import);
            items.Add(export);
        }

        if (node.Children.Count > 0 || node.Kind is "BANK" or "EVENT" or "ACTION" or "GROUP")
        {
            items.Add(MenuAction("Expand / collapse", (_, _) => ToggleBrowserNode(node)));
        }

        if (items.Count > 0)
        {
            items.Add(new Separator());
        }

        items.Add(MenuAction("Inspect properties", (_, _) =>
        {
            SelectBrowserNodeForContext(node);
            SetStatus($"Inspecting {node.Kind.ToLowerInvariant()} {node.Name}");
        }));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Opening += (_, _) =>
        {
            SelectBrowserNodeForContext(node);
            if (addMedia is not null)
            {
                addMedia.IsEnabled = node.Media?.IsPlayableAudio == true && timeline.SelectedTrackId is not null;
            }
        };
        return menu;
    }

    private Control BuildFooter()
    {
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };

        footer.Children.Add(new ScrollViewer
        {
            Content = status,
            MaxHeight = 54,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        Grid.SetColumn(progress, 1);

        progress.Margin = new Thickness(8, 0);
        footer.Children.Add(progress);
        Grid.SetColumn(cancelOperation, 2);
        footer.Children.Add(cancelOperation);

        return new Border
        {
            Background = ColorBrush("#171C23"),
            BorderBrush = ColorBrush("#303741"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 6),
            Child = footer
        };
    }

    private void AttachEvents()
    {
        search.TextChanged += (_, _) => ScheduleBrowserRefresh();
        browserHasAudio.IsCheckedChanged += (_, _) => ScheduleBrowserRefresh();
        clipSearch.TextChanged += (_, _) => RefreshClipCatalog();
        clipCatalogList.SelectionChanged += (_, _) => SelectCatalogListItem(clipCatalogList);
        pinnedClipList.SelectionChanged += (_, _) => SelectCatalogListItem(pinnedClipList);
        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is BrowserNode node)
            {
                ShowInspector(node.Detail);
            }
        };
        results.ContextRequested += BrowserContainerContextRequested;
        results.DoubleTapped += PreviewBrowserMediaAsync;
        clipCatalogList.ContextRequested += CatalogContainerContextRequested;
        pinnedClipList.ContextRequested += CatalogContainerContextRequested;
        timeline.SelectionChanged += ShowTimelineSelection;
        timeline.StatusChanged += SetStatus;
        timeline.ContextRequested += ShowTimelineContextMenu;
        timeline.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) => PromoteTimelineTab(activeTimelineTab),
            RoutingStrategies.Tunnel,
            true);
        timelineTabList.SelectionChanged += TimelineTabSelectionChanged;
        timelineTabList.AddHandler(
            InputElement.PointerWheelChangedEvent,
            TimelineTabsPointerWheelChanged,
            RoutingStrategies.Tunnel,
            true
        );

        playTimeline.Click += PlayTimelineAsync;
        pausePreview.Click += (_, _) => ToggleAudioPause();
        stopPreview.Click += (_, _) => StopAudioPreview();
        timeline.SeekRequested += SeekAudioPreview;
        timeline.ZoomChanged += value =>
        {
            if (Math.Abs(zoom.Value - value) > 0.001)
            {
                zoom.Value = value;
            }
        };

        timeline.PlayPauseRequested += ToggleTimelinePlayStop;
        timeline.TrackMixChanged += RefreshPlayingTimelineMix;
        timeline.AuditionSegmentChanged += OnAuditionSegmentChanged;
        timeline.SegmentBpmEditRequested += _ =>
        {
            UpdateSelectedSegmentTempoUi();
            bpmInput.Focus();
            bpmInput.SelectAll();
        };

        timeline.SegmentBpmEditorPlacementChanged += PlaceSegmentBpmEditor;
        buildPak.Click += BuildScopedPakAsync;
        buildPakMenu.Click += BuildScopedPakAsync;
        snapEnabled.IsCheckedChanged += (_, _) =>
        {
            document.SetSnapEnabled(snapEnabled.IsChecked == true);
            SetStatus(document.SnapEnabled ? "Snapping enabled" : "Snapping disabled");
        };

        showWwiseCues.IsCheckedChanged += (_, _) =>
        {
            timeline.ShowMarkers = showWwiseCues.IsChecked == true;
            SetStatus(timeline.ShowMarkers
                ? "Wwise transition cues shown"
                : "Wwise transition cues hidden");
        };

        metronomeEnabled.IsCheckedChanged += (_, _) =>
        {
            settings.MetronomeEnabled = metronomeEnabled.IsChecked == true;
            SyncGlobalMetronomeState();
            SaveGuiSettingsQuietly();
            RefreshPlayingTimelineMix();
            SetStatus(settings.MetronomeEnabled
                ? "Global preview metronome enabled"
                : "Global preview metronome disabled");
        };

        masterVolume.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty)
            {
                return;
            }

            settings.MasterVolume = Math.Clamp(masterVolume.Value, 0, 1);
            previewPlayer.MasterGain = settings.MasterVolume;
            masterVolumeValue.Text = $"{settings.MasterVolume * 100:0}%";
        };

        masterVolume.PointerReleased += (_, _) => SaveGuiSettingsQuietly();
        masterVolume.KeyUp += (_, _) => SaveGuiSettingsQuietly();
        bpmInput.LostFocus += (_, _) => ApplyBpmInput();
        bpmInput.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                ApplyBpmInput();
                timeline.Focus();
            }
        };

        cancelOperation.Click += (_, _) => indexOperation?.Cancel();
        document.Changed += OnDocumentChanged;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        importedAudioList.AddHandler(
            PointerPressedEvent,
            CaptureImportedAudioDragStart,
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        importedAudioList.AddHandler(
            PointerMovedEvent,
            StartImportedAudioDrag,
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        importedAudioList.AddHandler(
            PointerReleasedEvent,
            ClearImportedAudioDrag,
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        AttachCatalogDragSource(clipCatalogList);
        AttachCatalogDragSource(pinnedClipList);
        DragDrop.SetAllowDrop(timeline, true);
        DragDrop.AddDragOverHandler(timeline, TimelineDragOver);
        DragDrop.AddDropHandler(timeline, TimelineDrop);
    }

    private void AttachCatalogDragSource(ListBox list)
    {
        list.AddHandler(PointerPressedEvent, CaptureCatalogDragStart, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, StartCatalogDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, ClearCatalogDrag, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void BrowserContainerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Control source || HasContextMenuAncestor(source))
        {
            return;
        }

        var container = source as TreeViewItem ?? source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (container?.DataContext is not BrowserNode node)
        {
            return;
        }

        SelectBrowserNodeForContext(node);
        BuildBrowserContextMenu(node).Open(container);
        e.Handled = true;
    }

    private void CatalogContainerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Control source || HasContextMenuAncestor(source))
        {
            return;
        }

        var container = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is not ClipCatalogItem item)
        {
            return;
        }

        SelectCatalogItem(item);
        BuildCatalogContextMenu(item).Open(container);
        e.Handled = true;
    }

    private static bool HasContextMenuAncestor(Control source) =>
        source.ContextMenu is not null || source.GetVisualAncestors().OfType<Control>().Any(control => control.ContextMenu is not null);

    private async void SelectCatalogListItem(ListBox list)
    {
        if (selectingCatalogItem || list.SelectedItem is not ClipCatalogItem item)
        {
            return;
        }

        importedAudioList.SelectedItem = item.Imported;
        if (item.Media?.IsMusic == true)
        {
            await OpenMediaOccurrencesAsync(item);
        }
        else if (item.Media is not null)
        {
            await OpenStandaloneSoundEditorAsync(item.Media);
        }
    }

    private async void NewProject(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProjectReplacementAsync())
        {
            return;
        }

        restoringProject = true;
        try
        {
            ClearTimeline();
            ResetTimelineTabs();
            importedAudio.Clear();
            pinnedClipKeys.Clear();
            RefreshClipCatalog();
            ClearBrowserSelection();
        }
        finally
        {
            restoringProject = false;
        }

        currentProjectPath = null;
        projectDirty = false;
        SetProjectClean();

        UpdateWindowTitle();
        ShowInspector("NEW PROJECT\n\nChoose an Event to open its Music Segment composition.");
        SetStatus("New project");
    }

    private async void OpenProjectAsync(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open HBK Wwise project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("HBK Wwise project") { Patterns = ["*.hbkproj"] }]
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            if (await ConfirmProjectReplacementAsync())
            {
                await LoadProjectAsync(path);
            }
        }
    }

    private async void OpenRecentProjectAsync(object? sender, RoutedEventArgs e)
    {
        if (settings.RecentProjectPath is { } path && File.Exists(path))
        {
            if (await ConfirmProjectReplacementAsync())
            {
                await LoadProjectAsync(path);
            }
        }
        else
        {
            UpdateRecentProjectMenu();
            SetStatus("The recent project no longer exists", GuiLogLevel.Warning);
        }
    }

    private async void SaveProjectAsync(object? sender, RoutedEventArgs e)
    {
        if (currentProjectPath is null)
        {
            await SaveProjectAsCoreAsync();
            return;
        }

        await SaveProjectToAsync(currentProjectPath);
    }

    private async void SaveProjectAsAsync(object? sender, RoutedEventArgs e) => await SaveProjectAsCoreAsync();

    private async Task<bool> SaveProjectAsCoreAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save HBK Wwise project",
            SuggestedFileName = string.Empty,
            DefaultExtension = "hbkproj",
            FileTypeChoices = [new FilePickerFileType("HBK Wwise project") { Patterns = ["*.hbkproj"] }]
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            return await SaveProjectToAsync(path);
        }

        return false;
    }

    private async Task<bool> SaveProjectToAsync(string path)
    {
        try
        {
            var project = CaptureProject();
            await HbkWwiseProjectStore.SaveAsync(project, path);
            currentProjectPath = Path.GetFullPath(path);
            SetProjectClean();
            RememberProject(currentProjectPath);
            UpdateWindowTitle();
            SetStatus($"Saved project {Path.GetFileName(currentProjectPath)}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetFailure("Project save failed", exception);
            return false;
        }
    }

    private async Task<bool> ConfirmProjectReplacementAsync()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        var name = currentProjectPath is null ? "Untitled" : Path.GetFileName(currentProjectPath);
        var choice = await new UnsavedChangesDialog(name).ShowDialog<UnsavedChangesChoice?>(this);
        return choice switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => currentProjectPath is null
                ? await SaveProjectAsCoreAsync()
                : await SaveProjectToAsync(currentProjectPath),
            _ => false
        };
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!closingConfirmed && projectDirty)
        {
            e.Cancel = true;
            if (await ConfirmProjectReplacementAsync())
            {
                closingConfirmed = true;
                Close();
            }

            return;
        }

        DisposeResources();
    }

    private void DisposeResources()
    {
        if (resourcesDisposed)
        {
            return;
        }

        resourcesDisposed = true;
        indexOperation?.Cancel();
        browserRefresh?.Cancel();
        previewOperation?.Cancel();
        waveformOperation?.Cancel();
        transportTimer.Stop();
        previewPlayer.Dispose();
        SaveGuiSettingsQuietly();
    }

    private void SaveGuiSettingsQuietly()
    {
        try
        {
            GuiSettingsStore.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write(GuiLogLevel.Warning, $"Could not save GUI settings: {exception}");
        }
    }

    private async Task LoadProjectAsync(string path)
    {
        indexOperation?.Cancel();
        SetBusy(true, $"Opening {Path.GetFileName(path)}");
        try
        {
            var project = await HbkWwiseProjectStore.LoadAsync(path);
            if (index is null)
            {
                throw new InvalidOperationException(
                    "Game data is not ready. Complete setup in Preferences before opening a project.");
            }

            using var operation = new CancellationTokenSource();
            indexOperation = operation;
            SetBusy(true, $"Restoring {Path.GetFileName(path)}");
            LoadedEventTimeline? composition = null;
            if (project.Composition is { } identity)
            {
                var localIndex = index ?? throw new InvalidOperationException("No index is loaded.");
                var selectedEvent = localIndex.Events.SingleOrDefault(item => item.Id == identity.EventId)
                    ?? throw new InvalidDataException($"Event {identity.EventId} is absent from the current index.");
                var bank = localIndex.Banks.FirstOrDefault(item => item.Name.Equals(selectedEvent.Bank, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Bank {selectedEvent.Bank} is absent from the current index.");
                var xmlPath = await EnsureBankXmlAsync(localIndex, selectedEvent, bank, operation.Token);
                composition = await ResolveTimelineAsync(new SegmentContext(selectedEvent, bank, xmlPath, identity.SegmentId), operation.Token);
                if (composition.Scope.ObjectId != identity.ScopeId)
                {
                    throw new InvalidDataException($"Segment {identity.SegmentId} now belongs to timing scope {composition.Scope.ObjectId}, not saved scope {identity.ScopeId}.");
                }
            }

            var normalized = NormalizeProjectMediaIds(project);
            project = normalized.Project;
            if (project.Timelines is { Length: > 0 } savedTimelines)
            {
                var restoredTimelines = new List<(HbkProjectTimeline Project, LoadedEventTimeline? Runtime)>();
                foreach (var saved in savedTimelines)
                {
                    var runtime = saved.Composition is null
                        ? null
                        : SameComposition(saved.Composition, project.Composition) && composition is not null
                            ? composition
                            : await ResolveSavedCompositionAsync(saved.Composition, operation.Token);
                    restoredTimelines.Add((saved, runtime));
                }

                RestoreTabbedProject(project, restoredTimelines);
            }
            else
            {
                RestoreProject(project, composition);
                SyncGlobalMetronomeState();
                CreateTimelineTabForCurrent();
                if (activeTimelineTab is not null)
                {
                    activeTimelineTab.UpdateSnapshot(CaptureTimelineSnapshot());
                }
            }
            ClearBrowserSelection();
            currentProjectPath = Path.GetFullPath(path);
            SetProjectClean();
            if (normalized.ChangedIds > 0)
            {
                MarkProjectDirty();
            }

            RememberProject(currentProjectPath);
            UpdateWindowTitle();
            var missing = project.ImportedAudio.Count(item => !File.Exists(item.Path));
            SetStatus($"Opened project {Path.GetFileName(currentProjectPath)}"
                + (normalized.ChangedIds == 0
                    ? string.Empty
                    : $"; upgraded {normalized.ChangedIds} legacy media ID{(normalized.ChangedIds == 1 ? string.Empty : "s")}")
                + (missing == 0 ? string.Empty : $"; {missing} audio-library file{(missing == 1 ? string.Empty : "s")} missing"),
                missing == 0 ? GuiLogLevel.Info : GuiLogLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Project loading cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or InvalidOperationException)
        {
            SetFailure("Project loading failed", exception);
        }
        finally
        {
            indexOperation = null;
            SetBusy(false);
        }
    }

    private static bool SameComposition(HbkProjectComposition left, HbkProjectComposition? right) => right is not null
        && left.EventId == right.EventId
        && left.ScopeId == right.ScopeId
        && left.SegmentId == right.SegmentId;

    private async Task<LoadedEventTimeline> ResolveSavedCompositionAsync(
        HbkProjectComposition identity,
        CancellationToken cancellationToken)
    {
        var localIndex = index ?? throw new InvalidOperationException("No index is loaded.");
        var selectedEvent = localIndex.Events.SingleOrDefault(item => item.Id == identity.EventId)
            ?? throw new InvalidDataException($"Event {identity.EventId} is absent from the current index.");
        var bank = localIndex.Banks.FirstOrDefault(item => item.Name.Equals(selectedEvent.Bank, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Bank {selectedEvent.Bank} is absent from the current index.");
        var xmlPath = await EnsureBankXmlAsync(localIndex, selectedEvent, bank, cancellationToken);
        var composition = await ResolveTimelineAsync(
            new SegmentContext(selectedEvent, bank, xmlPath, identity.SegmentId), cancellationToken);
        if (composition.Scope.ObjectId != identity.ScopeId)
        {
            throw new InvalidDataException($"Segment {identity.SegmentId} now belongs to timing scope {composition.Scope.ObjectId}, not saved scope {identity.ScopeId}.");
        }

        return composition;
    }

    private void RestoreTabbedProject(
        HbkWwiseProject project,
        IReadOnlyCollection<(HbkProjectTimeline Project, LoadedEventTimeline? Runtime)> savedTimelines)
    {
        restoringProject = true;
        switchingTimelineTab = true;
        try
        {
            importedAudio.Clear();
            foreach (var audio in project.ImportedAudio)
            {
                importedAudio.Add(new ImportedAudio(audio.Id, audio.Name, audio.Path, audio.Format));
            }

            pinnedClipKeys.Clear();
            foreach (var key in project.PinnedClipKeys ?? [])
            {
                pinnedClipKeys.Add(key);
            }

            timelineTabItems.Clear();
            foreach (var saved in savedTimelines)
            {
                var single = TimelineProjectAsRoot(project, saved.Project);
                var tracks = HbkWwiseProjectTimeline.RestoreTracks(single, saved.Runtime?.Validation);
                var focus = saved.Runtime?.Segment.ObjectId;
                var view = new TimelineViewState(
                    100, 0, 0, 360, 0, null, null, focus, focus,
                    tracks.FirstOrDefault(track => track.SegmentObjectId == focus)?.Id ?? tracks.FirstOrDefault()?.Id,
                    null,
                    null
                );

                var snapshot = new TimelineSnapshot(
                    saved.Runtime,
                    saved.Project.Bpm,
                    saved.Project.BeatsPerBar,
                    saved.Project.SubdivisionsPerBeat,
                    saved.Project.SnapEnabled,
                    saved.Project.TimelineLengthMs,
                    tracks,
                    RestoreProjectMarkers(saved.Project.Markers, saved.Runtime?.Validation),
                    saved.Project.SegmentTempos.ToDictionary(item => item.SegmentId, item => item.Bpm),
                    saved.Project.Replacements.ToDictionary(
                        item => item.OriginalMediaId,
                        item => new ScopeReplacement(item.NewMediaId, item.Path, item.PhysicalDurationMs)),
                    saved.Project.Imports.ToDictionary(
                        item => item.NewMediaId,
                        item => new StructuralImport(
                            item.TemplateMediaId, item.NewMediaId, item.Path, item.PhysicalDurationMs)),
                    [],
                    saved.Project.VisibleSegmentIds?.ToHashSet(),
                    view);

                timelineTabItems.Add(new TimelineTab(
                    saved.Project.Id,
                    saved.Project.Name,
                    snapshot,
                    saved.Project.OccurrenceMediaId,
                    saved.Project.InspectionEventId,
                    ResolveStandaloneMedia(saved.Project)));
            }

            activeTimelineTab = timelineTabItems.FirstOrDefault(tab => tab.Id == project.ActiveTimelineId)
                ?? timelineTabItems.FirstOrDefault();
            timelineTabList.SelectedItem = activeTimelineTab;
        }
        finally
        {
            switchingTimelineTab = false;
            restoringProject = false;
        }

        SynchronizeCompositionTabs();
        if (activeTimelineTab is not null)
        {
            RestoreTimelineSnapshot(activeTimelineTab.Snapshot);
        }
        else
        {
            ClearTimeline();
        }
        RefreshClipCatalog();
    }

    private static HbkWwiseProject TimelineProjectAsRoot(
        HbkWwiseProject project,
        HbkProjectTimeline timeline) => new(
            project.Version,
            project.IndexPath,
            timeline.Composition,
            timeline.Bpm,
            timeline.BeatsPerBar,
            timeline.SubdivisionsPerBeat,
            timeline.SnapEnabled,
            timeline.TimelineLengthMs,
            timeline.Tracks,
            timeline.Markers,
            project.ImportedAudio,
            timeline.Replacements,
            timeline.Imports,
            timeline.SegmentTempos);

    private HbkWwiseProject CaptureProject()
    {
        SaveActiveTimelineTab();
        var active = CaptureTimelineSnapshot();
        var tracks = CaptureProjectTracks(active.Tracks, active.LoadedTimeline);
        var composition = ProjectComposition(active.LoadedTimeline);
        var timelineProjects = timelineTabItems.Select(tab =>
        {
            var snapshot = ReferenceEquals(tab, activeTimelineTab) ? active : tab.Snapshot;
            return new HbkProjectTimeline(
                tab.Id,
                tab.Title,
                ProjectComposition(snapshot.LoadedTimeline),
                snapshot.Bpm,
                snapshot.BeatsPerBar,
                snapshot.SubdivisionsPerBeat,
                snapshot.SnapEnabled,
                snapshot.TimelineLengthMs,
                CaptureProjectTracks(snapshot.Tracks, snapshot.LoadedTimeline),
                snapshot.Markers,
                snapshot.Replacements.Select(item => new HbkProjectReplacement(item.Key, item.Value.NewMediaId, item.Value.Path, item.Value.PhysicalDurationMs)).ToArray(),
                snapshot.Imports.Values.Select(item => new HbkProjectImport(item.TemplateMediaId, item.NewMediaId, item.Path, item.PhysicalDurationMs)).ToArray(),
                snapshot.SegmentBpms.Select(item => new HbkProjectSegmentTempo(item.Key, item.Value)).ToArray(),
                snapshot.MetronomeSegments.ToArray(),
                snapshot.VisibleSegmentIds?.ToArray(),
                tab.OccurrenceMediaId,
                tab.InspectionEventId,
                tab.StandaloneMedia?.Id,
                tab.StandaloneMedia?.Bank);
        }).ToArray();
        return new HbkWwiseProject(
            HbkWwiseProject.CurrentVersion,
            null,
            composition,
            active.Bpm,
            active.BeatsPerBar,
            active.SubdivisionsPerBeat,
            active.SnapEnabled,
            active.TimelineLengthMs,
            tracks,
            active.Markers,
            importedAudio.Select(item => new HbkProjectAudio(item.Id, item.Name, item.Path, item.Format)).ToArray(),
            active.Replacements.Select(item => new HbkProjectReplacement(
                item.Key,
                item.Value.NewMediaId,
                item.Value.Path,
                item.Value.PhysicalDurationMs)
            ).ToArray(),
            active.Imports.Values.Select(item => new HbkProjectImport(
                item.TemplateMediaId,
                item.NewMediaId,
                item.Path,
                item.PhysicalDurationMs)
            ).ToArray(),
            active.SegmentBpms.Select(item => new HbkProjectSegmentTempo(item.Key, item.Value)).ToArray(),
            timelineProjects,
            activeTimelineTab?.Id,
            pinnedClipKeys.ToArray(),
            active.MetronomeSegments.ToArray());
    }

    private static HbkProjectTrack[] CaptureProjectTracks(
        IEnumerable<MusicTimelineTrack> sourceTracks,
        LoadedEventTimeline? timeline)
    {
        var anchors = timeline?.Validation.Clips
            .Where(clip => clip.SourceIdOffset is not null)
            .GroupBy(clip => clip.SourceIdOffset!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        return sourceTracks.Select(track => new HbkProjectTrack(
            track.Id,
            track.Name,
            track.ObjectId,
            track.SegmentObjectId,
            track.LengthMs,
            track.Clips.Select(clip =>
            {
                BnkTimelineClip? source = null;
                if (clip.SourceIdOffset is { } offset)
                {
                    anchors?.TryGetValue(offset, out source);
                }

                var anchor = source is null
                    ? null
                    : new HbkProjectClipAnchor(
                        source.TrackObjectId,
                        source.SegmentObjectId,
                        source.PlaylistIndex,
                        source.MediaId);
                return new HbkProjectClip(
                    clip.Id,
                    clip.Name,
                    clip.MediaId,
                    clip.SourcePath,
                    clip.StartMs,
                    clip.SourceOffsetMs,
                    clip.DurationMs,
                    clip.ReplacementMediaId,
                    clip.PhysicalDurationMs,
                    clip.RepeatsSource,
                    anchor,
                    clip.FadeInMs,
                    clip.FadeOutMs);
            }).ToArray(),
            track.IsMuted,
            track.IsSolo,
            track.Gain)).ToArray();
    }

    private static HbkProjectComposition? ProjectComposition(LoadedEventTimeline? timeline) => timeline is null
        ? null
        : new HbkProjectComposition(
            timeline.Event.Id,
            timeline.Segment.ObjectId,
            timeline.Scope.ObjectId,
            timeline.AuthoredBpm);

    private MediaRecord? ResolveStandaloneMedia(HbkProjectTimeline timelineProject)
    {
        if (timelineProject.StandaloneMediaId is not { } mediaId || index is null)
        {
            return null;
        }

        return index.Media.FirstOrDefault(media => media.Id == mediaId
            && (string.IsNullOrWhiteSpace(timelineProject.StandaloneMediaBank)
                || media.Bank.Equals(timelineProject.StandaloneMediaBank, StringComparison.OrdinalIgnoreCase)));
    }

    private (HbkWwiseProject Project, int ChangedIds) NormalizeProjectMediaIds(HbkWwiseProject project)
    {
        var used = (index?.Media.Select(media => media.Id) ?? [])
            .Concat(project.Replacements.Select(item => item.NewMediaId))
            .Concat(project.Imports.Select(item => item.NewMediaId))
            .Where(WwiseHash.IsMediaId)
            .ToHashSet();
        var replacements = project.Replacements.Select(item => (
                item.NewMediaId,
                TemplateMediaId: item.OriginalMediaId,
                item.Path))
            .Concat(project.Imports.Select(item => (item.NewMediaId, item.TemplateMediaId, item.Path)))
            .Where(item => !WwiseHash.IsMediaId(item.NewMediaId))
            .DistinctBy(item => item.NewMediaId)
            .ToArray();
        if (replacements.Length == 0)
        {
            return (project, 0);
        }

        var scopeId = project.Composition?.ScopeId ?? 0;
        var remapped = new Dictionary<uint, uint>();
        foreach (var item in replacements)
        {
            var id = WwiseHash.AllocateMediaId(
                $"HBK_{scopeId}_{item.TemplateMediaId}_{Path.GetFileName(item.Path)}",
                used);
            remapped.Add(item.NewMediaId, id);
            used.Add(id);
        }

        var tracks = project.Tracks.Select(track => track with
        {
            Clips = track.Clips.Select(clip => clip.ReplacementMediaId is { } id
                    && remapped.TryGetValue(id, out var replacement)
                ? clip with { ReplacementMediaId = replacement }
                : clip).ToArray()
        }).ToArray();
        return (project with
        {
            Tracks = tracks,
            Replacements = project.Replacements.Select(item => remapped.TryGetValue(item.NewMediaId, out var id)
                ? item with { NewMediaId = id }
                : item).ToArray(),
            Imports = project.Imports.Select(item => remapped.TryGetValue(item.NewMediaId, out var id)
                ? item with { NewMediaId = id }
                : item).ToArray()
        }, remapped.Count);
    }

    private void RestoreProject(HbkWwiseProject project, LoadedEventTimeline? composition)
    {
        restoringProject = true;
        try
        {
            var restoredBpms = project.SegmentTempos?.ToDictionary(item => item.SegmentId, item => item.Bpm);
            var selectedBpm = composition is null
                ? project.Bpm
                : restoredBpms?.GetValueOrDefault(composition.Segment.ObjectId) ?? project.Bpm;
            loadedTimeline = composition is null ? null : composition with { PreviewBpm = selectedBpm };
            scopeReplacements.Clear();
            foreach (var replacement in project.Replacements)
            {
                scopeReplacements[replacement.OriginalMediaId] = new ScopeReplacement(
                    replacement.NewMediaId,
                    replacement.Path,
                    replacement.PhysicalDurationMs);
            }

            scopeImports.Clear();
            foreach (var import in project.Imports)
            {
                scopeImports[import.NewMediaId] = new StructuralImport(
                    import.TemplateMediaId,
                    import.NewMediaId,
                    import.Path,
                    import.PhysicalDurationMs);
            }

            var tracks = HbkWwiseProjectTimeline.RestoreTracks(project, composition?.Validation);
            document.Reset(
                project.Bpm,
                project.TimelineLengthMs,
                tracks,
                RestoreProjectMarkers(project.Markers, composition?.Validation),
                project.BeatsPerBar,
                project.SubdivisionsPerBeat,
                project.SnapEnabled,
                restoredBpms);
            snapEnabled.IsChecked = project.SnapEnabled;
            importedAudio.Clear();
            foreach (var audio in project.ImportedAudio)
            {
                importedAudio.Add(new ImportedAudio(audio.Id, audio.Name, audio.Path, audio.Format));
            }

            pinnedClipKeys.Clear();
            foreach (var key in project.PinnedClipKeys ?? [])
            {
                pinnedClipKeys.Add(key);
            }

            RefreshClipCatalog();
        }
        finally
        {
            restoringProject = false;
        }

        bpmInput.Text = document.SegmentBpm(loadedTimeline?.Segment.ObjectId)
            .ToString("0.###", CultureInfo.InvariantCulture);
        timeline.SetSegmentFocus(loadedTimeline?.Segment.ObjectId);
        UpdateTimelineControlAvailability();
        if (loadedTimeline is null)
        {
            timelineHeading.Text = "TIMELINE  |  manual arrangement";
            ShowInspector(
              $"PROJECT\n\nTracks: {document.Tracks.Count}\nAvailable imported clips: {importedAudio.Count}\nBPM: {document.Bpm:0.###}"
            );
        }
        else
        {
            timelineHeading.Text = $"COMPOSITION TIMELINE  |  {loadedTimeline.Event.Name}  |  restored edited arrangement";
            ShowInspector(
              $"RESTORED MUSIC COMPOSITION\n\nEvent: {loadedTimeline.Event.Name}\n"
                + $"Bank: {loadedTimeline.Event.Bank}\nTiming scope: {loadedTimeline.Scope.ObjectId}\n"
                + $"Selected segment: {loadedTimeline.Segment.ObjectId}\nAuthored BPM: {loadedTimeline.AuthoredBpm:0.###}\n"
                + $"Replacement BPM: {loadedTimeline.PreviewBpm:0.###}\nTracks: {document.Tracks.Count}\n"
                + $"Clips:                 {document.Tracks.Sum(track => track.Clips.Length)}\nReplacements: {scopeReplacements.Count}\n"
                + $"Imported playlist media: {ActiveStructuralImports().Length}"
            );
        }

        ScheduleWaveforms(clear: true);
    }

    private static MusicTimelineMarker[] RestoreProjectMarkers(
        IEnumerable<MusicTimelineMarker> markers,
        BnkTimelineValidation? validation)
    {
        if (validation is null)
        {
            return markers.ToArray();
        }

        return markers.Select(marker => marker.PositionOffsets is { Length: > 0 }
            ? marker
            : marker with
            {
                PositionOffsets = validation.Segments
                    .Where(segment => segment.ObjectId == marker.SegmentObjectId)
                    .SelectMany(segment => segment.Markers)
                    .Where(source => source.Id == marker.Id)
                    .OrderBy(source => Math.Abs(source.PositionMs - marker.PositionMs))
                    .Take(1)
                    .Select(source => source.PositionOffset)
                    .OfType<int>()
                    .ToArray()
            }).ToArray();
    }

    private void RememberProject(string path)
    {
        settings.RecentProjectPath = path;
        UpdateRecentProjectMenu();
        try
        {
            GuiSettingsStore.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Project opened, but its recent-path setting could not be saved: {exception.Message}", GuiLogLevel.Warning);
        }
    }

    private void UpdateRecentProjectMenu()
    {
        var path = settings.RecentProjectPath;
        openRecentProject.IsEnabled = path is not null && File.Exists(path);
        openRecentProject.Header = path is null
            ? "Open _recent project"
            : $"Open _recent project: {Path.GetFileName(path)}";
        ToolTip.SetTip(openRecentProject, path);
    }

    private void MarkProjectDirty()
    {
        if (restoringProject)
        {
            return;
        }

        projectDirty = true;
        UpdateWindowTitle();
    }

    private void SetProjectClean()
    {
        SaveActiveTimelineTab();
        foreach (var tab in timelineTabItems)
        {
            tab.MarkClean();
        }

        timeline.SetDirtyTracks([]);
        cleanProjectFingerprint = ProjectFingerprint();
        projectDirty = false;
        UpdateWindowTitle();
    }

    private bool HasUnsavedChanges()
    {
        if (!projectDirty)
        {
            return false;
        }

        if (cleanProjectFingerprint == ProjectFingerprint())
        {
            projectDirty = false;
            UpdateWindowTitle();
            return false;
        }

        return true;
    }

    private string ProjectFingerprint() => JsonSerializer.Serialize(CaptureProject());

    private void UpdateWindowTitle()
    {
        var name = currentProjectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(currentProjectPath);
        Title = $"HBK Wwise - {name}{(projectDirty ? " *" : string.Empty)}";
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            e.Handled = true;
            ToggleTimelinePlayStop();
            return;
        }

        if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            e.Handled = true;
            timeline.MovePlayheadToStart();
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            e.Handled = true;
            timeline.FocusPlayhead();
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (e.Key == Key.W)
        {
            e.Handled = true;
            if (activeTimelineTab is not null)
            {
                CloseTimelineTab(activeTimelineTab);
            }
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            SwitchTimelineTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.S)
        {
            e.Handled = true;
            if (currentProjectPath is null)
            {
                await SaveProjectAsCoreAsync();
            }
            else
            {
                await SaveProjectToAsync(currentProjectPath);
            }
        }
        else if (e.Key == Key.O)
        {
            e.Handled = true;
            OpenProjectAsync(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.F)
        {
            e.Handled = true;
            FitTimeline();
        }
        else if (e.Key == Key.D && e.Source is not TextBox)
        {
            e.Handled = true;
            timeline.DuplicateSelected();
        }
        else if (e.Key == Key.C && e.Source is not TextBox && !IsSelectableTextSource(e.Source))
        {
            e.Handled = true;
            CopySelectedClip();
        }
        else if (e.Key == Key.V && e.Source is not TextBox)
        {
            e.Handled = true;
            PasteClipAsync(sender, new RoutedEventArgs());
        }
    }

    private void SwitchTimelineTab(int direction)
    {
        if (timelineTabItems.Count < 2)
        {
            return;
        }

        var current = activeTimelineTab is null ? -1 : timelineTabItems.IndexOf(activeTimelineTab);
        var next = (current + direction) % timelineTabItems.Count;
        if (next < 0)
        {
            next += timelineTabItems.Count;
        }

        timelineTabList.SelectedItem = timelineTabItems[next];
    }

    private void FitTimeline()
    {
        timeline.FitToWidth();
        zoom.Value = timeline.PixelsPerSecond;
        SetStatus("Timeline fitted to width");
    }

    private async void LoadInitialContentAsync(object? sender, EventArgs e)
    {
        if (!await EnsureApplicationSetupAsync())
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(startupProjectPath))
        {
            await LoadProjectAsync(startupProjectPath);
            return;
        }

        SetStatus("Ready");
    }

    private void OpenPackagedDocument(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            SetStatus($"{fileName} is missing from this application folder", GuiLogLevel.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetFailure($"Could not open {fileName}", exception);
        }
    }

    private async void OpenSettingsAsync(object? sender, RoutedEventArgs e) =>
        await OpenSettingsCoreAsync();

    private async Task<bool> LoadIndexAsync(string path)
    {
        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();

        indexOperation = operation;
        SetBusy(true, "Loading Hi-Fi RUSH game data");
        try
        {
            var loaded = await Task.Run(
                () => IndexStore.LoadAsync(path, operation.Token).GetAwaiter().GetResult(),
                operation.Token
            );

            browserSearchIndex = await Task.Run(
              () => BuildBrowserSearchIndex(loaded),
              operation.Token
            );

            index = loaded;
            timeline.SetNonAudioMediaIds(index.Media.Where(media => !media.IsPlayableAudio).Select(media => media.Id));
            currentIndexPath = Path.GetFullPath(path);

            eventStructures.Clear();
            ResetTimelineForNavigation();
            ClearBrowserSelection();
            ScheduleBrowserRefresh();
            RefreshClipCatalog();

            SetStatus($"Game data ready: {index.Media.Length:N0} media, {index.Events.Length:N0} Events, {index.Banks.Length:N0} banks");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Index loading cancelled");
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            SetStatus($"Could not load game data: {exception.Message}", GuiLogLevel.Error);
            return false;
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private void ScheduleBrowserRefresh()
    {
        browserRefresh?.Cancel();
        browserRefresh?.Dispose();
        browserRefresh = new CancellationTokenSource();
        _ = RefreshBrowserAsync(browserRefresh.Token);
    }

    private async Task RefreshBrowserAsync(CancellationToken cancellationToken)
    {
        var localIndex = index;
        if (localIndex is null)
        {
            results.ItemsSource = Array.Empty<BrowserNode>();
            return;
        }

        var query = search.Text ?? string.Empty;
        var hasAudio = browserHasAudio.IsChecked == true;
        var structures = eventStructures.ToDictionary(item => item.Key, item => item.Value);
        var searchIndex = browserSearchIndex ?? BuildBrowserSearchIndex(localIndex);
        try
        {
            await Task.Delay(100, cancellationToken);
            var entries = await Task.Run(
                () => BuildBrowserTree(localIndex, searchIndex, query, hasAudio, structures, cancellationToken),
                cancellationToken
            );
            results.ItemsSource = entries;
            SetStatus($"{entries.Length:N0} soundbank{(entries.Length == 1 ? string.Empty : "s")} in browser tree"
                + (hasAudio ? " with playable audio" : string.Empty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Browser refresh failed: {exception.Message}", GuiLogLevel.Error);
        }
    }

    private static BrowserNode[] BuildBrowserTree(
        WwiseIndex index,
        BrowserSearchIndex searchIndex,
        string query,
        bool hasAudio,
        IReadOnlyDictionary<(uint BankId, uint EventId), BrowserNode[]> structures,
        CancellationToken cancellationToken)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mediaNames = searchIndex.MediaNames;
        var eventMediaNames = searchIndex.EventMediaNames;
        var roots = new List<BrowserNode>();
        foreach (var bank in index.Banks.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasAudio && !searchIndex.AudioBanks.Contains(bank.Name))
            {
                continue;
            }

            var bankMatches = Matches($"{bank.Id} {bank.Name} {bank.Path} {bank.Language}", terms);
            var events = index.Events
                .Where(item => item.Bank.Equals(bank.Name, StringComparison.OrdinalIgnoreCase))
                .Where(item => !hasAudio || searchIndex.AudioEvents.Contains(item.Id))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    var eventIdentityMatches = Matches($"{item.Id} {item.Name} {item.ObjectPath}", terms);
                    var eventMediaMatches = Matches(eventMediaNames.GetValueOrDefault(item.Id) ?? string.Empty, terms);
                    var eventMatches = eventIdentityMatches || eventMediaMatches;

                    structures.TryGetValue((bank.Id, item.Id), out var structure);
                    var filteredStructure = structure is null
                        ? []
                        : structure.Select(node => terms.Length == 0 || bankMatches || eventIdentityMatches
                                ? CloneBrowserNode(node)
                                : FilterBrowserNode(node, terms))
                            .Select(node => hasAudio && node is not null ? FilterAudioBrowserNode(node) : node)
                            .Where(node => node is not null)
                            .Cast<BrowserNode>()
                            .ToArray();
                    if (!bankMatches && !eventMatches && filteredStructure.Length == 0)
                    {
                        return null;
                    }

                    var eventNode = new BrowserNode(
                        item.Name,
                        "EVENT",
                        item.Id.ToString(CultureInfo.InvariantCulture),
                        item.DurationType.ToUpperInvariant(),
                        bank.Name,
                        EventDetails(mediaNames, item),
                        Event: item,
                        Bank: bank
                    );

                    foreach (var child in filteredStructure)
                    {
                        eventNode.Children.Add(child);
                    }

                    eventNode.StructureLoaded = structure is not null;
                    return eventNode;
                })
                .Where(item => item is not null)
                .Cast<BrowserNode>()
                .ToArray();

            var media = index.Media
                .Where(item => item.Bank.Equals(bank.Name, StringComparison.OrdinalIgnoreCase))
                .Where(item => !hasAudio || item.IsPlayableAudio)
                .Where(_ => terms.Length > 0)
                .Where(item => Matches($"{item.Id} {item.SourceName} {item.Storage}", terms))
                .OrderBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .Take(250)
                .Select(item => new BrowserNode(
                    Path.GetFileName(item.SourceName),
                    "MEDIA",
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    MediaType(item),
                    PakLabel(item.EffectiveAsset()),
                    MediaDetails(item),
                    Media: item,
                    Bank: bank)).ToArray();

            if (!bankMatches && events.Length == 0 && media.Length == 0)
            {
                continue;
            }

            var root = new BrowserNode(
                bank.Name,
                "BANK",
                bank.Id.ToString(CultureInfo.InvariantCulture),
                "SOUNDBANK",
                PakLabel(bank.EffectiveAsset()),
                BankDetails(index, bank),
                Bank: bank
            );

            root.Children.Add(BrowserNode.Group($"Events ({events.Length:N0})", events));
            root.Children.Add(BrowserNode.Group(terms.Length == 0
                ? "Media - search to browse"
                : $"Matching media ({media.Length:N0})", media));
            roots.Add(root);
        }

        return roots.ToArray();
    }

    private static BrowserSearchIndex BuildBrowserSearchIndex(WwiseIndex index) => new(
        index.Media.GroupBy(media => media.Id).ToDictionary(group => group.Key, group => group.First().SourceName),
        index.Media.SelectMany(media => media.Uses.Select(use => (use.EventId, media.SourceName)))
            .GroupBy(item => item.EventId)
            .ToDictionary(
                group => group.Key,
                group => string.Join(' ', group.Select(item => item.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            ),
        index.Media.Where(media => media.IsPlayableAudio)
            .SelectMany(media => media.Uses.Select(use => use.EventId))
            .ToHashSet(),
        index.Media.Where(media => media.IsPlayableAudio)
            .Select(media => media.Bank)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
    );

    private static bool Matches(string text, string[] terms) =>
        terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static BrowserNode? FilterBrowserNode(BrowserNode node, string[] terms)
    {
        if (Matches($"{node.Id} {node.Name} {node.Kind} {node.Type} {node.Location}", terms))
        {
            return CloneBrowserNode(node);
        }

        var children = node.Children.Select(child => FilterBrowserNode(child, terms))
            .Where(child => child is not null)
            .Cast<BrowserNode>()
            .ToArray();
        if (children.Length == 0)
        {
            return null;
        }

        return CloneBrowserNode(node, children);
    }

    private static BrowserNode? FilterAudioBrowserNode(BrowserNode node)
    {
        if (node.Media?.IsPlayableAudio == true || node.Segment is not null)
        {
            return CloneBrowserNode(node);
        }

        var children = node.Children.Select(FilterAudioBrowserNode)
            .Where(child => child is not null)
            .Cast<BrowserNode>()
            .ToArray();
        return children.Length == 0 ? null : CloneBrowserNode(node, children);
    }

    private static BrowserNode CloneBrowserNode(BrowserNode node) =>
        CloneBrowserNode(node, node.Children.Select(CloneBrowserNode));

    private static BrowserNode CloneBrowserNode(BrowserNode node, IEnumerable<BrowserNode> children)
    {
        var clone = new BrowserNode(
            node.Name,
            node.Kind,
            node.Id,
            node.Type,
            node.Location,
            node.Detail,
            node.Media,
            node.Event,
            node.Bank,
            node.Segment)
        {
            StructureLoaded = node.StructureLoaded
        };

        foreach (var child in children)
        {
            clone.Children.Add(child);
        }

        return clone;
    }

    private async void ShowBrowserSelection()
    {
        if (suppressBrowserSelection)
        {
            return;
        }

        var entry = results.SelectedItem as BrowserNode;
        if (entry is null)
        {
            acceptedBrowserSelection = null;
            if (loadedTimeline is not null || document.Tracks.Count > 0)
            {
                return;
            }

            ShowInspector("INSPECTOR\n\nSelect a soundbank object to inspect it. A Music Segment opens its complete composition scope.");
            return;
        }

        var selectedEntry = entry;
        var sameSegment = selectedEntry.Segment is { } selectedSegment
            && loadedTimeline is not null
            && selectedSegment.Event.Id == loadedTimeline.Event.Id
            && selectedSegment.SegmentId == loadedTimeline.Segment.ObjectId;

        var sameEvent = selectedEntry.Kind == "EVENT"
            && selectedEntry.Event?.Id == loadedTimeline?.Event.Id
            && selectedEntry.StructureLoaded;

        var changesComposition = selectedEntry.Segment is not null || selectedEntry.Kind == "EVENT";

        acceptedBrowserSelection = selectedEntry;
        ShowInspector(selectedEntry.Detail);
        if (sameSegment)
        {
            return;
        }

        if (sameEvent)
        {
            ExpandBrowserNode(selectedEntry);
            return;
        }

        if (!changesComposition && selectedEntry.Children.Count > 0)
        {
            ExpandBrowserNode(selectedEntry);
            SetStatus($"Expanded {selectedEntry.Name}");
            return;
        }

        if (selectedEntry.Media is { IsPlayableAudio: true } media && !changesComposition)
        {
            await OpenStandaloneSoundEditorAsync(media);
            return;
        }

        if (!changesComposition)
        {
            return;
        }

        if (selectedEntry.Segment is { } segment)
        {
            await OpenSegmentTimelineAsync(segment);
            return;
        }

        if (selectedEntry.Event is not null)
        {
            await LoadEventStructureAsync(selectedEntry);
            return;
        }

        ExpandBrowserNode(selectedEntry);
    }

    private async Task LoadEventStructureAsync(BrowserNode selectedNode)
    {
        if (selectedNode.Event is not { } selectedEvent)
        {
            return;
        }

        if (selectedNode.StructureLoaded)
        {
            ExpandBrowserNode(selectedNode);
            await OpenFirstEventSegmentAsync(selectedNode);
            return;
        }

        var localIndex = index;
        if (localIndex?.Paks is not { Length: > 0 })
        {
            SetStatus("The game data cache was not generated from game PAKs", GuiLogLevel.Error);
            return;
        }

        var bank = localIndex.Banks.FirstOrDefault(item => item.Name.Equals(selectedEvent.Bank, StringComparison.OrdinalIgnoreCase));
        var asset = bank?.EffectiveAsset();
        if (bank is null || asset is null)
        {
            SetStatus($"No effective BNK asset was indexed for {selectedEvent.Bank}", GuiLogLevel.Error);
            return;
        }

        var indexDirectory = currentIndexPath is null
            ? Path.Combine(Environment.CurrentDirectory, ".hbkwwise")
            : Path.GetDirectoryName(currentIndexPath)!;
        var cache = Path.Combine(indexDirectory, "gui-timeline", localIndex.CreatedUtc.UtcDateTime.Ticks.ToString("X", CultureInfo.InvariantCulture));
        var bankPath = Path.Combine(cache, $"{bank.Id}-{Path.GetFileName(asset.EntryPath)}");
        var xmlPath = $"{bankPath}.xml";

        string? aesKey = null;
        if (!File.Exists(bankPath))
        {
            aesKey = CurrentAesKey();
            if (string.IsNullOrWhiteSpace(aesKey))
            {
                aesKey = await new PasswordPromptDialog(
                    $"Extracting {bank.Name} from the encrypted game PAK requires the AES key.")
                    .ShowDialog<string?>(this);
                if (string.IsNullOrWhiteSpace(aesKey))
                {
                    SetStatus("Event structure loading cancelled");
                    return;
                }

                settings.AesKey = aesKey;
            }
        }

        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();

        indexOperation = operation;
        SetBusy(true, $"Analyzing {selectedEvent.Name}");
        try
        {
            Directory.CreateDirectory(cache);
            if (!File.Exists(bankPath))
            {
                var owner = localIndex.Paks.Where(pak => Path.GetFullPath(pak.Path).Equals(Path.GetFullPath(asset.PakPath), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (owner.Length == 0)
                {
                    throw new InvalidDataException($"Indexed owner PAK is unavailable: {asset.PakPath}");
                }

                await RepakArchive.ExtractEntryAsync(owner, asset.EntryPath, bankPath, settings.RepakPath, aesKey, operation.Token);
            }

            if (!File.Exists(xmlPath))
            {
                await WwiserClient.DumpXmlAsync(bankPath, xmlPath, settings.WwiserPath, settings.PythonPath, cancellationToken: operation.Token);
            }

            var graph = await Task.Run(() => WwiserHircGraph.Load(xmlPath), operation.Token);
            var program = graph.EventProgram(selectedEvent.Name);

            var mediaNames = localIndex.Media.GroupBy(media => media.Id)
                .ToDictionary(group => group.Key, group => group.First().SourceName);

            var mediaRecords = localIndex.Media.GroupBy(media => media.Id)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(media =>
                            media.Bank.Equals(bank.Name, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(media => media.IsPlayableAudio)
                        .First()
                );

            var knownNames = localIndex.Names.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().Name);

            var actionNodes = await Task.Run(
                () => BuildActionNodes(
                    graph,
                    program,
                    selectedEvent,
                    bank,
                    xmlPath,
                    mediaNames,
                    mediaRecords,
                    knownNames),
                operation.Token);

            selectedNode.Children.Clear();
            foreach (var node in actionNodes)
            {
                selectedNode.Children.Add(node);
            }

            selectedNode.StructureLoaded = true;
            eventStructures[(bank.Id, selectedEvent.Id)] = actionNodes;
            ExpandBrowserNode(selectedNode);

            var playActions = program.Actions.Count(action => action.Kind == WwiserActionKind.Play);
            SetStatus($"Loaded {selectedEvent.Name}: {program.Actions.Length:N0} Actions, {playActions:N0} playable branch{(playActions == 1 ? string.Empty : "es")}");
            await OpenFirstEventSegmentAsync(selectedNode);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Event structure loading cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Event structure loading failed", exception);
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private void ExpandBrowserNode(BrowserNode node)
    {
        if (BrowserContainer(node) is { } item)
        {
            item.IsExpanded = true;
        }
    }

    private TreeViewItem? BrowserContainer(BrowserNode node) => results
        .GetVisualDescendants()
        .OfType<TreeViewItem>()
        .FirstOrDefault(item => ReferenceEquals(item.DataContext, node));

    private void SelectBrowserNodeForContext(BrowserNode node)
    {
        suppressBrowserSelection = true;
        try
        {
            results.SelectedItem = node;
        }
        finally
        {
            suppressBrowserSelection = false;
        }

        ShowInspector(node.Detail);
    }

    private void OpenBrowserNodeFromContext(BrowserNode node)
    {
        SelectBrowserNodeForContext(node);
        ShowBrowserSelection();
    }

    private void ToggleBrowserNode(BrowserNode node)
    {
        SelectBrowserNodeForContext(node);
        if (BrowserContainer(node) is { } item)
        {
            item.IsExpanded = !item.IsExpanded;
            SetStatus($"{(item.IsExpanded ? "Expanded" : "Collapsed")} {node.Name}");
        }
    }

    private async Task OpenFirstEventSegmentAsync(BrowserNode eventNode)
    {
        var segment = Descendants(eventNode).Select(node => node.Segment).FirstOrDefault(item => item is not null);
        if (segment is null)
        {
            var directSounds = Descendants(eventNode)
                .Select(node => node.Media)
                .Where(media => media?.IsPlayableAudio == true)
                .Cast<MediaRecord>()
                .DistinctBy(media => (media.Bank.ToUpperInvariant(), media.Id))
                .ToArray();

            if (directSounds.Length == 1 && eventNode.Event is { } directSoundEvent)
            {
                await OpenStandaloneSoundEditorAsync(directSounds[0], directSoundEvent);
                return;
            }

            OpenEventInspectionTab(eventNode);
            return;
        }

        await OpenSegmentTimelineAsync(segment);
    }

    private void OpenEventInspectionTab(BrowserNode eventNode)
    {
        if (eventNode.Event is not { } selectedEvent)
        {
            return;
        }

        ResetTransportForTimelineNavigation();

        var existing = timelineTabItems.FirstOrDefault(tab => tab.InspectionEventId == selectedEvent.Id);
        DiscardReplaceablePreviewTabs(existing);
        if (existing is not null)
        {
            if (!ReferenceEquals(existing, activeTimelineTab))
            {
                SaveActiveTimelineTab();
                switchingTimelineTab = true;
                activeTimelineTab = existing;
                timelineTabList.SelectedItem = existing;
                switchingTimelineTab = false;
                RestoreTimelineSnapshot(existing.Snapshot);
            }
        }
        else
        {
            SaveActiveTimelineTab();
            restoringProject = true;
            try
            {
                ClearTimeline();
            }
            finally
            {
                restoringProject = false;
            }

            var tab = new TimelineTab(
                Guid.NewGuid(),
                $"{selectedEvent.Name} | Event",
                CaptureTimelineSnapshot(),
                inspectionEventId: selectedEvent.Id,
                isPreview: true);

            switchingTimelineTab = true;
            timelineTabItems.Add(tab);

            activeTimelineTab = tab;
            timelineTabList.SelectedItem = tab;
            switchingTimelineTab = false;
            SaveActiveTimelineTab(markClean: true);
        }

        UpdateTimelineHeading();
        SetStatus($"{selectedEvent.Name} has no playable Music Segment; its Actions remain available in Game Content");
    }

    private static IEnumerable<BrowserNode> Descendants(BrowserNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static BrowserNode[] BuildActionNodes(
        WwiserHircGraph graph,
        WwiserEventProgram program,
        EventRecord selectedEvent,
        BankRecord bank,
        string xmlPath,
        IReadOnlyDictionary<uint, string> mediaNames,
        IReadOnlyDictionary<uint, MediaRecord> mediaRecords,
        IReadOnlyDictionary<uint, string> knownNames) => program.Actions.Select(action =>
    {
        var targetObject = action.TargetIds.Select(id => graph.Objects.GetValueOrDefault(id)).FirstOrDefault(item => item is not null);
        var actionNode = new BrowserNode(
            ActionDisplayName(action, targetObject),
            "ACTION",
            action.ActionId.ToString(CultureInfo.InvariantCulture),
            action.ObjectClass,
            action.Kind == WwiserActionKind.Play ? "playable" : "relationship / state change",
            $"WWISE ACTION\n\nType: {action.ObjectClass}\nID: {action.ActionId}\nSemantic type: {action.Kind}\n"
            + (action.Kind == WwiserActionKind.Play
                ? "Its target hierarchy contains selectable Music Segments."
                : "This action does not populate the timeline.")
            + "\n\nAuthored Action names are not stored in the cooked BNK; this descriptive name is derived from its semantic type and target.");
        foreach (var targetId in action.TargetIds)
        {
            if (action.Kind == WwiserActionKind.Play)
            {
                actionNode.Children.Add(BuildObjectNode(
                    graph,
                    targetId,
                    selectedEvent,
                    bank,
                    xmlPath,
                    mediaNames,
                    mediaRecords,
                    knownNames,
                    new HashSet<uint>(),
                    true
                ));
            }
            else
            {
                var target = graph.Objects.GetValueOrDefault(targetId);
                var knownTarget = knownNames.GetValueOrDefault(targetId);
                var reference = new BrowserNode(
                    target is null ? FriendlyPathPart(knownTarget ?? "External target") : $"Target - {FriendlyObjectName(target)}",
                    "REFERENCE",
                    targetId.ToString(CultureInfo.InvariantCulture),
                    target?.Name ?? knownTarget ?? "external object",
                    action.Kind.ToString(),
                    $"{action.Kind.ToString().ToUpperInvariant()} TARGET\n\nName: {target?.Name ?? knownTarget ?? "Object outside this bank"}\nID: {targetId}\n"
                    + (target is null
                        ? "The target is a State/Switch value or another object that is not stored as a HIRC node in this bank."
                        : "Its hierarchy is inspectable below, but its Segments remain read-only because this Action does not play them."));

                if (target is not null)
                {
                    reference.Children.Add(BuildObjectNode(
                        graph,
                        targetId,
                        selectedEvent,
                        bank,
                        xmlPath,
                        mediaNames,
                        mediaRecords,
                        knownNames,
                        new HashSet<uint>(),
                        false
                    ));
                }

                actionNode.Children.Add(reference);
            }
        }

        return actionNode;
    }).ToArray();

    private static BrowserNode BuildObjectNode(
        WwiserHircGraph graph,
        uint objectId,
        EventRecord selectedEvent,
        BankRecord bank,
        string xmlPath,
        IReadOnlyDictionary<uint, string> mediaNames,
        IReadOnlyDictionary<uint, MediaRecord> mediaRecords,
        IReadOnlyDictionary<uint, string> knownNames,
        HashSet<uint> ancestors,
        bool playable)
    {
        if (!graph.Objects.TryGetValue(objectId, out var item))
        {
            return new BrowserNode(
                "Missing target", "REFERENCE", objectId.ToString(CultureInfo.InvariantCulture),
                "external object", bank.Name, $"Object {objectId} is referenced but is not present in this bank.");
        }

        if (!ancestors.Add(objectId))
        {
            return new BrowserNode(
                "Shared reference", "REFERENCE", objectId.ToString(CultureInfo.InvariantCulture),
                item.Name, bank.Name, "This object was already encountered in the current hierarchy path.");
        }

        var isSegment = item.Type == 10;
        var soundMedia = item.Type == 2 && item.Media.Length == 1
            ? mediaRecords.GetValueOrDefault(item.Media[0].MediaId)
            : null;

        var node = new BrowserNode(
            ObjectDisplayName(item, graph, mediaNames),
            isSegment && playable ? "SEGMENT" : isSegment ? "REFERENCE" : "OBJECT",
            objectId.ToString(CultureInfo.InvariantCulture),
            item.Name,
            bank.Name,
            isSegment
                ? $"WWISE MUSIC SEGMENT\n\nObject ID: {objectId}\nEvent: {selectedEvent.Name}\nBank: {bank.Name}\n\n"
                    + (playable
                        ? "Selecting this Segment loads its real Tracks and Clips into the timeline."
                        : "This Segment is shown only to explain the Action target; it is not played by the selected Event.")
                : ObjectDetails(item, objectId, mediaNames, knownNames),
            Media: soundMedia,
            Bank: bank,
            Segment: isSegment && playable ? new SegmentContext(selectedEvent, bank, xmlPath, objectId) : null
        );

        if (!isSegment)
        {
            foreach (var reference in item.Media.DistinctBy(reference => reference.MediaId))
            {
                var media = mediaRecords.GetValueOrDefault(reference.MediaId);
                node.Children.Add(new BrowserNode(
                    media is null ? $"Media {reference.MediaId}" : Path.GetFileName(media.SourceName),
                    "MEDIA",
                    reference.MediaId.ToString(CultureInfo.InvariantCulture),
                    media is null ? "UNKNOWN" : MediaType(media),
                    media is null ? bank.Name : PakLabel(media.EffectiveAsset()),
                    media is null
                        ? $"MEDIA REFERENCE\n\nID: {reference.MediaId}\nNo generated media metadata was indexed."
                        : MediaDetails(media),
                    Media: media,
                    Bank: bank
                ));
            }

            var flowTargets = item.FlowTargets.Select(flow => flow.ObjectId).ToHashSet();
            foreach (var flow in item.FlowTargets)
            {
                var target = graph.Objects.GetValueOrDefault(flow.ObjectId);
                var branch = new BrowserNode(
                    FlowBranchName(item, flow, knownNames),
                    "BRANCH",
                    flow.ObjectId.ToString(CultureInfo.InvariantCulture),
                    item.Type == 12 ? "SWITCH PATH" : "PLAYLIST ITEM",
                    target is null ? "missing target" : ObjectDisplayName(target, graph, mediaNames),
                    FlowBranchDetail(item, flow, knownNames));

                if (target is not null)
                {
                    branch.Children.Add(BuildObjectNode(
                        graph,
                        flow.ObjectId,
                        selectedEvent,
                        bank,
                        xmlPath,
                        mediaNames,
                        mediaRecords,
                        knownNames,
                        new HashSet<uint>(ancestors),
                        playable
                    ));
                }
                node.Children.Add(branch);
            }

            foreach (var childId in item.ChildIds.Where(id => !flowTargets.Contains(id)))
            {
                node.Children.Add(BuildObjectNode(
                    graph,
                    childId,
                    selectedEvent,
                    bank,
                    xmlPath,
                    mediaNames,
                    mediaRecords,
                    knownNames,
                    new HashSet<uint>(ancestors),
                    playable
                ));
            }
        }

        return node;
    }

    private static string FriendlyObjectName(WwiserHircObject item) => item.Type switch
    {
        2 => "Sound",
        5 => "Random / Sequence Container",
        10 => "Music Segment",
        12 => "Music Switch Container",
        13 => "Music Random / Sequence Container",
        _ => item.Name.StartsWith("CAk", StringComparison.Ordinal) ? item.Name[3..] : item.Name
    };

    private static string ObjectDetails(
        WwiserHircObject item,
        uint objectId,
        IReadOnlyDictionary<uint, string> mediaNames,
        IReadOnlyDictionary<uint, string> knownNames)
    {
        var media = item.Media.Select(reference =>
            $"{reference.MediaId}\t{mediaNames.GetValueOrDefault(reference.MediaId) ?? "unknown"}").ToArray();

        var sound = item.Type == 2
            ? "\n\nA Sound is a directly playable object, commonly one song or sound source. It is inspectable now; direct Sound replacement will use a separate non-timeline workflow."
            : string.Empty;

        return $"WWISE OBJECT\n\nType: {FriendlyObjectName(item)}\nObject ID: {objectId}\nChildren: {item.ChildIds.Length}\nMedia references: {item.Media.Length}"
            + sound
            + (media.Length == 0 ? string.Empty : $"\n\nREFERENCED MEDIA\n{string.Join('\n', media)}")
            + (item.Behavior.Length == 0
                ? string.Empty
                : $"\n\nHOW IT CHANGES MUSIC FLOW\n{ResolveKnownNames(item.Behavior, knownNames)}");
    }

    private static string ActionDisplayName(WwiserEventAction action, WwiserHircObject? target) =>
        target is null ? action.Kind.ToString() : $"{action.Kind} {FriendlyObjectName(target)}";

    private static string ObjectDisplayName(
        WwiserHircObject item,
        WwiserHircGraph graph,
        IReadOnlyDictionary<uint, string> mediaNames)
    {
        if (item.Type != 10)
        {
            return FriendlyObjectName(item);
        }

        var sources = item.ChildIds
            .Where(graph.Objects.ContainsKey)
            .SelectMany(id => graph.Objects[id].Media)
            .Select(media => mediaNames.GetValueOrDefault(media.MediaId))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return sources.Length switch
        {
            0 => "Music Segment",
            1 => $"Music Segment - {sources[0]}",
            _ => $"Music Segment - {sources[0]} (+{sources.Length - 1})"
        };
    }

    private static string FlowBranchName(
        WwiserHircObject container,
        WwiserFlowTarget flow,
        IReadOnlyDictionary<uint, string> knownNames)
    {
        if (container.Type == 13)
        {
            return $"Playlist {flow.Order}";
        }

        var values = container.FlowArgumentIds.Select((_, index) =>
        {
            var key = index < flow.Keys.Length ? flow.Keys[index] : 0;
            return key == 0
                ? "Default"
                : knownNames.TryGetValue(key, out var name) ? FriendlyPathPart(name) : $"Alternate {index + 1}";
        }).ToArray();

        return values.Length switch
        {
            0 => "Default",
            1 => values[0],
            2 => $"{values[0]}.{values[1]}",
            _ => $"{values[0]}.{values[1]} ({string.Join(" / ", values.Skip(2))})"
        };
    }

    private static string FriendlyPathPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Alternate";
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"^_?\d+_?", string.Empty)
            .Replace('_', ' ')
            .Trim();
        return cleaned.Length == 0 ? "Alternate" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
    }

    private static string FlowBranchDetail(
        WwiserHircObject container,
        WwiserFlowTarget flow,
        IReadOnlyDictionary<uint, string> knownNames) => container.Type == 13
        ? $"PLAYLIST FLOW\n\nPosition: {flow.Order}\nDestination object: {flow.ObjectId}\n\n"
            + "This destination is chosen according to the container's sequence/random, loop, weight, shuffle, and repeat-avoidance settings."
        : $"SWITCH FLOW\n\n{FlowBranchName(container, flow, knownNames)}\nDestination object: {flow.ObjectId}\n\n"
            + "When the current game States best-match this path, Wwise schedules the destination using the container's transition rules.";

    private static string ResolveKnownNames(string value, IReadOnlyDictionary<uint, string> knownNames)
        => System.Text.RegularExpressions.Regex.Replace(value, @"\b\d+\b", match =>
            uint.TryParse(match.Value, out var id) && knownNames.TryGetValue(id, out var name) ? name : match.Value);

    private async Task OpenSegmentTimelineAsync(SegmentContext context)
    {
        var localIndex = index;
        if (localIndex is null)
        {
            return;
        }

        ResetTransportForTimelineNavigation();

        var alreadyOpen = timelineTabItems.FirstOrDefault(tab =>
            tab.OccurrenceMediaId is null
            && tab.Snapshot.LoadedTimeline is { } open
            && open.Event.Id == context.Event.Id
            && open.Event.Bank.Equals(context.Event.Bank, StringComparison.OrdinalIgnoreCase)
            && open.Validation.Segments.Any(segment => segment.ObjectId == context.SegmentId));

        if (alreadyOpen is not null)
        {
            FocusExistingEventTimeline(alreadyOpen, context.SegmentId);
            return;
        }

        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();

        indexOperation = operation;
        SetBusy(true, $"Loading Music Segment {context.SegmentId}");
        try
        {
            var resolved = await ResolveTimelineAsync(context, operation.Token);
            var existing = timelineTabItems.FirstOrDefault(tab =>
                tab.OccurrenceMediaId is null
                && tab.Snapshot.LoadedTimeline is { } open
                && open.Event.Id == resolved.Event.Id
                && open.Event.Bank.Equals(resolved.Event.Bank, StringComparison.OrdinalIgnoreCase));

            DiscardReplaceablePreviewTabs(existing);
            if (existing is not null)
            {
                FocusExistingEventTimeline(existing, context.SegmentId);
                return;
            }

            SaveActiveTimelineTab();
            loadedTimeline = resolved;
            scopeReplacements.Clear();
            scopeImports.Clear();
            timeline.SetVisibleSegments(null);

            var tab = new TimelineTab(
                Guid.NewGuid(),
                resolved.Event.Name,
                CaptureTimelineSnapshot(),
                isPreview: true);

            switchingTimelineTab = true;
            timelineTabItems.Add(tab);

            activeTimelineTab = tab;
            timelineTabList.SelectedItem = tab;
            switchingTimelineTab = false;
            RenderLoadedComposition();
            SaveActiveTimelineTab(markClean: true);
            if (SynchronizeCompositionTabs())
            {
                RestoreTimelineSnapshot(tab.Snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Composition loading cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Composition loading failed", exception);
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private void FocusExistingEventTimeline(TimelineTab tab, uint segmentId)
    {
        DiscardReplaceablePreviewTabs(tab);
        if (!ReferenceEquals(tab, activeTimelineTab))
        {
            SaveActiveTimelineTab();
            switchingTimelineTab = true;
            activeTimelineTab = tab;
            timelineTabList.SelectedItem = tab;
            switchingTimelineTab = false;
            RestoreTimelineSnapshot(tab.Snapshot);
        }

        loadedTimeline = SelectLoadedSegment(loadedTimeline!, segmentId);
        timeline.SetSegmentFocus(segmentId);
        UpdateSelectedSegmentTempoUi();
        SaveActiveTimelineTab();
        SetStatus($"Focused Music Segment {segmentId} in {tab.Title}");
    }

    private static LoadedEventTimeline SelectLoadedSegment(LoadedEventTimeline timeline, uint segmentId)
    {
        var timingScope = timeline.AllTimingScopes
            .Where(scope => scope.Validation.Segments.Any(segment => segment.ObjectId == segmentId))
            .OrderBy(scope => scope.Scope.ObjectIds.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"Segment {segmentId} is absent from the loaded Event timeline.");

        var segment = timingScope.Validation.Segments.Single(item => item.ObjectId == segmentId);

        return timeline with
        {
            Scope = timingScope.Scope,
            Segment = segment,
            AuthoredBpm = timingScope.AuthoredBpm,
            PreviewBpm = timingScope.AuthoredBpm
        };
    }

    private async Task OpenMediaOccurrencesAsync(ClipCatalogItem item)
    {
        if (item.Media is not { } media)
        {
            return;
        }

        ResetTransportForTimelineNavigation();

        var existing = timelineTabItems.FirstOrDefault(tab =>
            tab.OccurrenceMediaId == media.Id
            && tab.Snapshot.LoadedTimeline?.Event.Bank.Equals(media.Bank, StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            DiscardReplaceablePreviewTabs(existing);
            if (!ReferenceEquals(existing, activeTimelineTab))
            {
                SaveActiveTimelineTab();
                switchingTimelineTab = true;
                activeTimelineTab = existing;
                timelineTabList.SelectedItem = existing;
                switchingTimelineTab = false;
                RestoreTimelineSnapshot(existing.Snapshot);
            }

            SetStatus($"Showing all {item.Name} occurrences in {existing.Title}");
            return;
        }

        var localIndex = index;
        if (localIndex is null)
        {
            return;
        }

        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();
        indexOperation = operation;

        SetBusy(true, $"Finding every {item.Name} occurrence");
        try
        {
            var occurrence = await ResolveMediaOccurrencesAsync(media, operation.Token);
            DiscardReplaceablePreviewTabs();
            SaveActiveTimelineTab();
            loadedTimeline = occurrence.Timeline;
            scopeReplacements.Clear();
            scopeImports.Clear();
            timeline.SetVisibleSegments(occurrence.SegmentIds);

            var tab = new TimelineTab(
                Guid.NewGuid(),
                $"Occurrences | {item.Name}",
                CaptureTimelineSnapshot(),
                media.Id,
                isPreview: true);

            switchingTimelineTab = true;
            timelineTabItems.Add(tab);
            activeTimelineTab = tab;
            timelineTabList.SelectedItem = tab;
            switchingTimelineTab = false;
            RenderLoadedComposition();
            timeline.SetVisibleSegments(occurrence.SegmentIds);
            timeline.SetSegmentFocus(occurrence.Timeline.Segment.ObjectId);
            UpdateTimelineHeading();
            SaveActiveTimelineTab(markClean: true);
            if (SynchronizeCompositionTabs())
            {
                RestoreTimelineSnapshot(tab.Snapshot);
            }
            SetStatus($"Showing {occurrence.Placements:N0} occurrence{(occurrence.Placements == 1 ? string.Empty : "s")} of {item.Name} across {occurrence.SegmentIds.Length:N0} segment{(occurrence.SegmentIds.Length == 1 ? string.Empty : "s")}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Occurrence search cancelled");
        }
        catch (InvalidDataException exception)
        {
            SetStatus(exception.Message, GuiLogLevel.Warning);
        }
        catch (Exception exception)
        {
            SetFailure("Could not open clip occurrences", exception);
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private async Task OpenStandaloneSoundEditorAsync(MediaRecord media, EventRecord? eventContext = null)
    {
        if (!media.IsPlayableAudio)
        {
            SetStatus($"{media.SourceName} is Wwise MIDI/control data and has no editable waveform", GuiLogLevel.Warning);
            return;
        }

        ResetTransportForTimelineNavigation();

        var existing = timelineTabItems.FirstOrDefault(tab => tab.StandaloneMedia is { } open
            && open.Id == media.Id
            && open.Bank.Equals(media.Bank, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            DiscardReplaceablePreviewTabs(existing);
            if (!ReferenceEquals(existing, activeTimelineTab))
            {
                SaveActiveTimelineTab();
                switchingTimelineTab = true;
                activeTimelineTab = existing;
                timelineTabList.SelectedItem = existing;
                switchingTimelineTab = false;
                RestoreTimelineSnapshot(existing.Snapshot);
            }

            SetStatus($"Opened sound editor for {Path.GetFileNameWithoutExtension(media.SourceName)}");
            return;
        }

        if (index is null)
        {
            SetStatus("Game data is not ready. Complete setup in Preferences before editing game audio", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(required: true);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();
        indexOperation = operation;

        var name = Path.GetFileNameWithoutExtension(media.SourceName);
        SetBusy(true, $"Opening sound editor for {name}");
        try
        {
            var wav = await PrepareMediaWavAsync(media.Id, aesKey, operation.Token);
            double durationMs;
            using (var reader = new WaveFileReader(wav))
            {
                durationMs = Math.Max(1, reader.TotalTime.TotalMilliseconds);
            }

            DiscardReplaceablePreviewTabs();
            SaveActiveTimelineTab();
            restoringProject = true;
            try
            {
                ClearLoadedTimelineReference();
                timeline.SetStandaloneAudioMode(true);
                var clip = new MusicTimelineClip(
                    Guid.NewGuid(),
                    media.Id,
                    name,
                    null,
                    0,
                    0,
                    durationMs,
                    PhysicalDurationMs: durationMs
                );

                var track = new MusicTimelineTrack(Guid.NewGuid(), name, [clip]);

                document.Reset(120, null, [track], [], snapEnabled: false);
                timeline.SetPlaybackPosition(0);
                timeline.SetVisibleSegments(null);
                timeline.SetSegmentFocus(null);
            }
            finally
            {
                restoringProject = false;
            }

            var tab = new TimelineTab(
                Guid.NewGuid(),
                eventContext is null ? $"Sound | {name}" : $"{eventContext.Name} | Sound",
                CaptureTimelineSnapshot(),
                inspectionEventId: eventContext?.Id,
                standaloneMedia: media,
                isPreview: true);
            switchingTimelineTab = true;
            timelineTabItems.Add(tab);
            activeTimelineTab = tab;
            timelineTabList.SelectedItem = tab;
            switchingTimelineTab = false;
            timeline.FitToWidth();
            SaveActiveTimelineTab(markClean: true);
            UpdateTimelineHeading();
            UpdateTimelineControlAvailability();
            ScheduleWaveforms(clear: true);
            ShowInspector($"SOUND EDITOR\n\n{(eventContext is null ? string.Empty : $"Event: {eventContext.Name}\n")}"
                + $"Sound: {name}\nMedia ID: {media.Id}\nBank: {media.Bank}\n"
                + $"Storage: {media.Storage}\nDuration: {FormatMs(durationMs)}\n\n"
                + "This tab edits one rendered sound. Moving, trimming, splitting, fading, or replacing blocks is baked into a direct replacement for this media when the mod PAK is built.");
            SetStatus($"Opened {name} as a single-track sound editor");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Sound editor loading cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Could not open sound editor", exception);
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private async Task<MediaOccurrenceTimeline> ResolveMediaOccurrencesAsync(
        MediaRecord media,
        CancellationToken cancellationToken)
    {
        var localIndex = index ?? throw new InvalidOperationException("No index is loaded.");
        var bank = localIndex.Banks.FirstOrDefault(item =>
            item.Name.Equals(media.Bank, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"SoundBank {media.Bank} is absent from the index.");
        var events = media.Uses.Select(use => use.EventId)
            .Distinct()
            .Select(id => localIndex.Events.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null
                && item.Bank.Equals(media.Bank, StringComparison.OrdinalIgnoreCase))
            .Cast<EventRecord>()
            .ToArray();
        if (events.Length == 0)
        {
            throw new InvalidDataException($"Media {media.Id} has no indexed Event usage.");
        }

        var xmlPath = await EnsureBankXmlAsync(localIndex, events[0], bank, cancellationToken);
        var mediaNames = localIndex.Media.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().SourceName);
        var candidates = new List<MediaOccurrenceTimeline>();
        foreach (var selectedEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BnkTimingScope[] scopes;
            try
            {
                scopes = await Task.Run(
                    () => BnkRetimer.FindTimingScopes(xmlPath, selectedEvent.Name),
                    cancellationToken);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            foreach (var scope in scopes.Where(scope => scope.MediaIds.Contains(media.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authoredBpm = scope.Bpms.FirstOrDefault();
                if (authoredBpm <= 0)
                {
                    continue;
                }

                BnkTimelineValidation validation;
                try
                {
                    validation = await Task.Run(
                        () => BnkTimelineValidator.Validate(
                            xmlPath,
                            scope.ObjectId,
                            new Dictionary<uint, double>(),
                            authoredBpm,
                            authoredBpm,
                            eventNameOrId: selectedEvent.Name),
                        cancellationToken);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                var matchingClips = validation.Clips.Where(clip => clip.MediaId == media.Id).ToArray();
                var segmentIds = matchingClips.Select(clip => clip.SegmentObjectId)
                    .OfType<uint>()
                    .Distinct()
                    .Where(id => validation.Segments.Any(segment => segment.ObjectId == id))
                    .ToArray();
                if (segmentIds.Length == 0)
                {
                    continue;
                }

                var segment = validation.Segments.First(item => item.ObjectId == segmentIds[0]);
                candidates.Add(new MediaOccurrenceTimeline(
                    new LoadedEventTimeline(
                        selectedEvent,
                        scope,
                        validation,
                        segment,
                        mediaNames,
                        authoredBpm,
                        authoredBpm),
                    segmentIds,
                    matchingClips.Length));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.SegmentIds.Length)
            .ThenByDescending(candidate => candidate.Placements)
            .ThenByDescending(candidate => candidate.Timeline.Scope.ObjectIds.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"Media {media.Id} is not placed inside an editable Music Segment timing scope.");
    }

    private async Task<LoadedEventTimeline> ResolveTimelineAsync(
        SegmentContext context,
        CancellationToken cancellationToken)
    {
        var localIndex = index ?? throw new InvalidOperationException("No index is loaded.");
        var scopes = await Task.Run(() => BnkRetimer.FindTimingScopes(context.XmlPath, context.Event.Name), cancellationToken);
        var validated = await Task.Run(() => scopes.Select(scope =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authoredBpm = scope.Bpms.FirstOrDefault();
            if (authoredBpm <= 0)
            {
                return null;
            }

            var validation = BnkTimelineValidator.Validate(
                context.XmlPath,
                scope.ObjectId,
                new Dictionary<uint, double>(),
                authoredBpm,
                authoredBpm,
                eventNameOrId: context.Event.Name);
            return validation.Segments.Length == 0
                ? null
                : new LoadedTimingScope(scope, validation, authoredBpm);
        }).OfType<LoadedTimingScope>().ToArray(), cancellationToken);

        var timingScopes = AssignSegmentsToMostSpecificScopes(validated);
        var selectedScope = timingScopes
            .Where(item => item.Validation.Segments.Any(segment => segment.ObjectId == context.SegmentId))
            .OrderBy(item => item.Scope.ObjectIds.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"Segment {context.SegmentId} has no active-meter timing scope.");
        var segment = selectedScope.Validation.Segments.Single(item => item.ObjectId == context.SegmentId);
        var combinedValidation = CombineTimelineValidations(timingScopes, selectedScope.Scope.ObjectId);

        var mediaNames = localIndex.Media.GroupBy(media => media.Id)
            .ToDictionary(group => group.Key, group => group.First().SourceName);

        return new LoadedEventTimeline(
            context.Event,
            selectedScope.Scope,
            combinedValidation,
            segment,
            mediaNames,
            selectedScope.AuthoredBpm,
            selectedScope.AuthoredBpm,
            timingScopes
        );
    }

    private static LoadedTimingScope[] AssignSegmentsToMostSpecificScopes(
        IReadOnlyCollection<LoadedTimingScope> timingScopes)
    {
        var owners = timingScopes.SelectMany(scope => scope.Validation.Segments.Select(segment => new
        {
            segment.ObjectId,
            TimingScope = scope
        }))
            .GroupBy(item => item.ObjectId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.TimingScope.Scope.ObjectIds.Length).First().TimingScope.Scope.ObjectId);

        return timingScopes.Select(scope =>
        {
            var segmentIds = scope.Validation.Segments
                .Where(segment => owners.GetValueOrDefault(segment.ObjectId) == scope.Scope.ObjectId)
                .Select(segment => segment.ObjectId)
                .ToHashSet();

            var trackIds = scope.Validation.Clips
                .Where(clip => clip.SegmentObjectId is { } id && segmentIds.Contains(id))
                .Select(clip => clip.TrackObjectId)
                .ToHashSet();

            var validation = scope.Validation with
            {
                Segments = scope.Validation.Segments.Where(segment => segmentIds.Contains(segment.ObjectId)).ToArray(),
                Clips = scope.Validation.Clips.Where(clip => clip.SegmentObjectId is { } id && segmentIds.Contains(id)).ToArray(),
                DurationValidation = scope.Validation.DurationValidation with
                {
                    ClipUsages = scope.Validation.DurationValidation.ClipUsages
                        .Where(usage => trackIds.Contains(usage.ObjectId))
                        .ToArray()
                }
            };
            return scope with { Validation = validation };
        }).Where(scope => scope.Validation.Segments.Length > 0).ToArray();
    }

    private static BnkTimelineValidation CombineTimelineValidations(
        IReadOnlyCollection<LoadedTimingScope> timingScopes,
        uint primaryScopeId)
    {
        var validations = timingScopes.Select(scope => scope.Validation).ToArray();
        return new BnkTimelineValidation(
            primaryScopeId,
            1,
            validations.SelectMany(validation => validation.Segments).DistinctBy(segment => segment.ObjectId).ToArray(),
            validations.SelectMany(validation => validation.Clips)
                .DistinctBy(clip => (clip.TrackObjectId, clip.SegmentObjectId, clip.SourceIdOffset, clip.PlaylistIndex))
                .ToArray(),
            validations.SelectMany(validation => validation.Transitions).Distinct().ToArray(),
            validations.SelectMany(validation => validation.Loops).Distinct().ToArray(),
            new BnkDurationValidation(
                primaryScopeId,
                validations.SelectMany(validation => validation.DurationValidation.ClipUsages).Distinct().ToArray(),
                validations.SelectMany(validation => validation.DurationValidation.Checks).Distinct().ToArray()),
            validations.SelectMany(validation => validation.Issues).Distinct().ToArray());
    }

    private async Task<string> EnsureBankXmlAsync(
        WwiseIndex localIndex,
        EventRecord selectedEvent,
        BankRecord bank,
        CancellationToken cancellationToken)
    {
        if (localIndex.Paks is not { Length: > 0 })
        {
            throw new InvalidDataException("The game data cache was not generated from game PAKs.");
        }

        var asset = bank.EffectiveAsset() ?? throw new InvalidDataException($"No effective BNK asset was indexed for {selectedEvent.Bank}.");
        var indexDirectory = currentIndexPath is null
            ? Path.Combine(Environment.CurrentDirectory, ".hbkwwise")
            : Path.GetDirectoryName(currentIndexPath)!;
        var cache = Path.Combine(
            indexDirectory,
            "gui-timeline",
            localIndex.CreatedUtc.UtcDateTime.Ticks.ToString("X", CultureInfo.InvariantCulture));
        var bankPath = Path.Combine(cache, $"{bank.Id}-{Path.GetFileName(asset.EntryPath)}");
        var xmlPath = $"{bankPath}.xml";

        string? aesKey = null;
        if (!File.Exists(bankPath))
        {
            aesKey = CurrentAesKey();
            if (string.IsNullOrWhiteSpace(aesKey))
            {
                aesKey = await new PasswordPromptDialog(
                    $"Extracting {bank.Name} from the encrypted game PAK requires the AES key.")
                    .ShowDialog<string?>(this);
                if (string.IsNullOrWhiteSpace(aesKey))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                settings.AesKey = aesKey;
            }
        }

        Directory.CreateDirectory(cache);
        if (!File.Exists(bankPath))
        {
            var owner = localIndex.Paks.Where(pak => Path.GetFullPath(pak.Path)
                .Equals(Path.GetFullPath(asset.PakPath), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (owner.Length == 0)
            {
                throw new InvalidDataException($"Indexed owner PAK is unavailable: {asset.PakPath}");
            }

            await RepakArchive.ExtractEntryAsync(
                owner,
                asset.EntryPath,
                bankPath,
                settings.RepakPath,
                aesKey,
                cancellationToken
            );
        }

        if (!File.Exists(xmlPath))
        {
            await WwiserClient.DumpXmlAsync(
                bankPath,
                xmlPath,
                settings.WwiserPath,
                settings.PythonPath,
                cancellationToken: cancellationToken
            );
        }

        return xmlPath;
    }

    private void RenderLoadedComposition()
    {
        if (loadedTimeline is null)
        {
            return;
        }

        renderingComposition = true;
        MusicScopeTimelineImportResult imported;
        try
        {
            timeline.SetStandaloneAudioMode(false);
            timeline.SetSourceEvent(loadedTimeline.Event.Name);
            imported = MusicTimelineImporter.LoadScopes(
                document,
                loadedTimeline.AllTimingScopes.Select(scope => new MusicTimelineScopeSource(
                    scope.Validation,
                    scope.AuthoredBpm)).ToArray(),
                loadedTimeline.MediaNames,
                snapEnabled.IsChecked == true,
                loadedTimeline.Segment.ObjectId
            );

            foreach (var replacement in scopeReplacements)
            {
                document.ReplaceMediaReferences(
                    replacement.Key,
                    replacement.Value.Path,
                    replacement.Value.NewMediaId,
                    replacement.Value.PhysicalDurationMs);
            }
        }
        finally
        {
            renderingComposition = false;
        }

        timeline.SetSegmentFocus(loadedTimeline.Segment.ObjectId);
        UpdateTimelineControlAvailability();
        UpdateSelectedSegmentTempoUi();

        var issues = loadedTimeline.Validation.Issues.Length;
        timelineHeading.Text = $"EVENT COMPOSITION  |  {timeline.VisibleSegmentCount} segments  |  {timeline.VisibleTracks.Count} tracks  |  click a segment header to audition/edit its BPM";
        ShowInspector($"MUSIC COMPOSITION\n\nEvent: {loadedTimeline.Event.Name}\nBank: {loadedTimeline.Event.Bank}\n"
            + $"Timing scopes: {loadedTimeline.AllTimingScopes.Count}\nSelected scope: {loadedTimeline.Scope.ObjectId}\nSelected segment: {loadedTimeline.Segment.ObjectId}\n"
            + $"Inherited BPM: {loadedTimeline.AuthoredBpm:0.###}\nSelected segment BPM: {document.SegmentBpm(loadedTimeline.Segment.ObjectId):0.###}\n"
            + $"Loaded segment timelines: {imported.Segments}\n"
            + $"All scope tracks: {imported.Tracks}\nAll scope clips: {imported.Clips}\n"
            + $"Distinct media: {imported.Media}\nVisible markers: {imported.Markers}\nIssues: {issues}\n"
            + $"Replacements: {scopeReplacements.Count}\nImported playlist media: {ActiveStructuralImports().Length}\n\n"
            + "All editable Music Segments reached by this Event are stacked under one cursor and horizontal scroll. Each segment retains its own BPM and beat grid.");
        SetStatus($"Loaded {imported.Segments} event segments ({imported.Tracks} tracks); selected {loadedTimeline.Segment.ObjectId} at {document.SegmentBpm(loadedTimeline.Segment.ObjectId):0.###} BPM");
        ScheduleWaveforms(clear: true);
        if (!projectDirty)
        {
            SetProjectClean();
        }
    }

    private void ClearLoadedTimelineReference()
    {
        loadedTimeline = null;
        timeline.SetSourceEvent(null);
        scopeReplacements.Clear();
        scopeImports.Clear();

        waveformOperation?.Cancel();
        waveformOperation = null;
        playWhenTimelineReady = false;
        SetTimelineContentLoading(false);
        timeline.ClearWaveforms();

        timeline.SetSegmentFocus(null);
        timeline.SetVisibleSegments(null);
        bpmInput.IsVisible = false;
        timelineHeading.Text = "COMPOSITION TIMELINE  |  select an Event, then a Music Segment";
    }

    private void ClearTimeline()
    {
        ClearLoadedTimelineReference();
        timeline.SetStandaloneAudioMode(false);
        document.Clear();
        SyncGlobalMetronomeState();
        bpmInput.Text = document.Bpm.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void SyncGlobalMetronomeState() => timeline.SetMetronomeSegments(
        settings.MetronomeEnabled
            ? document.Tracks.Select(track => track.SegmentObjectId).OfType<uint>().Distinct()
            : []);

    private void PromoteTimelineTab(TimelineTab? tab)
    {
        if (tab?.IsPreview != true)
        {
            return;
        }

        tab.Promote();
        MarkProjectDirty();
    }

    private void DiscardReplaceablePreviewTabs(TimelineTab? except = null)
    {
        var discarded = timelineTabItems
            .Where(tab => tab.IsPreview && !ReferenceEquals(tab, except))
            .ToArray();
        if (discarded.Length == 0)
        {
            return;
        }

        switchingTimelineTab = true;
        try
        {
            foreach (var tab in discarded)
            {
                timelineTabItems.Remove(tab);
                if (ReferenceEquals(tab, activeTimelineTab))
                {
                    activeTimelineTab = null;
                }
            }

            timelineTabList.SelectedItem = activeTimelineTab;
        }
        finally
        {
            switchingTimelineTab = false;
        }
    }

    private void ResetTimelineTabs()
    {
        ResetTransportForTimelineNavigation();
        switchingTimelineTab = true;
        try
        {
            timelineTabItems.Clear();
            activeTimelineTab = null;
            timelineTabList.SelectedItem = null;
        }
        finally
        {
            switchingTimelineTab = false;
        }
    }

    private void CreateTimelineTabForCurrent()
    {
        ResetTimelineTabs();
        if (loadedTimeline is null && document.Tracks.Count == 0)
        {
            return;
        }

        var title = loadedTimeline is null
            ? "Manual arrangement"
            : loadedTimeline.Event.Name;

        var tab = new TimelineTab(Guid.NewGuid(), title, CaptureTimelineSnapshot());

        switchingTimelineTab = true;
        timelineTabItems.Add(tab);
        activeTimelineTab = tab;
        timelineTabList.SelectedItem = tab;
        switchingTimelineTab = false;
    }

    private TimelineSnapshot CaptureTimelineSnapshot() => new(
        loadedTimeline,
        document.Bpm,
        document.BeatsPerBar,
        document.SubdivisionsPerBeat,
        document.SnapEnabled,
        document.TimelineLengthMs,
        document.Tracks.ToArray(),
        document.Markers.ToArray(),
        document.SegmentBpms.ToDictionary(item => item.Key, item => item.Value),
        scopeReplacements.ToDictionary(item => item.Key, item => item.Value),
        scopeImports.ToDictionary(item => item.Key, item => item.Value),
        [],
        timeline.VisibleSegmentIds?.ToHashSet(),
        timeline.CaptureViewState()
    );

    private void SaveActiveTimelineTab(bool markClean = false)
    {
        if (activeTimelineTab is not null)
        {
            activeTimelineTab.UpdateSnapshot(CaptureTimelineSnapshot());
            if (markClean)
            {
                activeTimelineTab.MarkClean();
            }

            timeline.SetDirtyTracks(activeTimelineTab.DirtyTrackIds);
        }
    }

    private bool SynchronizeCompositionTabs(TimelineTab? source = null)
    {
        if (synchronizingTimelineTabs)
        {
            return false;
        }

        synchronizingTimelineTabs = true;
        try
        {
            var activeChanged = false;
            if (source is not null)
            {
                foreach (var target in timelineTabItems.Where(tab => !ReferenceEquals(tab, source)))
                {
                    if (!SameCompositionScope(target.Snapshot, source.Snapshot))
                    {
                        continue;
                    }

                    var changed = target.UpdateSnapshot(MergeCompositionSnapshot(target.Snapshot, source.Snapshot));
                    activeChanged |= changed && ReferenceEquals(target, activeTimelineTab);
                }

                return activeChanged;
            }

            foreach (var group in timelineTabItems
                .Where(tab => tab.Snapshot.LoadedTimeline is not null && tab.StandaloneMedia is null)
                .GroupBy(tab => CompositionScopeKey(tab.Snapshot), StringComparer.OrdinalIgnoreCase))
            {
                var canonical = group
                    .OrderBy(tab => SnapshotEditScore(tab.Snapshot))
                    .ThenBy(tab => timelineTabItems.IndexOf(tab))
                    .Last();
                foreach (var target in group.Where(tab => !ReferenceEquals(tab, canonical)))
                {
                    var changed = target.UpdateSnapshot(MergeCompositionSnapshot(target.Snapshot, canonical.Snapshot));
                    activeChanged |= changed && ReferenceEquals(target, activeTimelineTab);
                }
            }

            return activeChanged;
        }
        finally
        {
            synchronizingTimelineTabs = false;
        }
    }

    private static string CompositionScopeKey(TimelineSnapshot snapshot)
    {
        var composition = snapshot.LoadedTimeline
            ?? throw new InvalidOperationException("A composition scope key requires a loaded timeline.");
        return $"{composition.Event.Bank}\0{composition.Scope.ObjectId}";
    }

    private static bool SameCompositionScope(TimelineSnapshot left, TimelineSnapshot right) =>
        left.LoadedTimeline is { } leftTimeline
        && right.LoadedTimeline is { } rightTimeline
        && leftTimeline.Event.Bank.Equals(rightTimeline.Event.Bank, StringComparison.OrdinalIgnoreCase)
        && leftTimeline.AllTimingScopes.Select(scope => scope.Scope.ObjectId)
            .Intersect(rightTimeline.AllTimingScopes.Select(scope => scope.Scope.ObjectId))
            .Any();

    private static int SnapshotEditScore(TimelineSnapshot snapshot)
    {
        if (snapshot.LoadedTimeline is not { } composition)
        {
            return 0;
        }

        var referencedImports = snapshot.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.ReplacementMediaId)
            .OfType<uint>()
            .Distinct()
            .Count(snapshot.Imports.ContainsKey);
        var replacements = snapshot.Tracks.SelectMany(track => track.Clips)
            .Count(clip => clip.SourcePath is not null || clip.ReplacementMediaId is not null);
        var tempos = snapshot.SegmentBpms.Count(item =>
            !Near(item.Value, AuthoredBpmForSegment(composition, item.Key)));
        var originals = composition.Validation.Clips
            .Where(clip => clip.SourceIdOffset is not null)
            .GroupBy(clip => clip.SourceIdOffset!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var placements = snapshot.Tracks.SelectMany(track => track.Clips.Select(clip => (Track: track, Clip: clip)))
            .ToArray();
        var structure = placements.Count(item => item.Clip.SourceIdOffset is null
            || !originals.TryGetValue(item.Clip.SourceIdOffset.Value, out var original)
            || original.TrackObjectId != item.Track.ObjectId)
            + Math.Abs(placements.Length - originals.Count);
        var timing = placements.Count(item =>
        {
            if (item.Clip.SourceIdOffset is not { } offset || !originals.TryGetValue(offset, out var original))
            {
                return false;
            }

            var ratio = AuthoredBpmForSegment(composition, item.Track.SegmentObjectId)
                / SegmentBpm(snapshot.SegmentBpms, composition, item.Track.SegmentObjectId);
            return !Near(item.Clip.StartMs, Math.Max(0, original.TimelineStartMs) * ratio)
                || !Near(item.Clip.SourceOffsetMs, Math.Max(0, original.BeginTrimMs) * ratio)
                || !Near(item.Clip.DurationMs, Math.Max(
                    1,
                    (original.TimelineEndMs - Math.Max(0, original.TimelineStartMs)) * ratio))
                || FadeChanged(original, item.Clip);
        });
        var markerEdits = CreateMarkerEdits(composition, snapshot.Markers, snapshot.SegmentBpms).Length;
        var durationEdits = CreateSegmentDurationEdits(composition, snapshot.Tracks, snapshot.SegmentBpms).Length;
        return referencedImports * 1000 + replacements * 100 + structure * 20 + tempos * 10
            + timing + markerEdits + durationEdits;
    }

    private static TimelineSnapshot MergeCompositionSnapshot(
        TimelineSnapshot target,
        TimelineSnapshot source)
    {
        if (!SameCompositionScope(target, source))
        {
            return target;
        }

        var sharedSegments = target.LoadedTimeline!.Validation.Segments.Select(segment => segment.ObjectId)
            .Intersect(source.LoadedTimeline!.Validation.Segments.Select(segment => segment.ObjectId))
            .ToHashSet();
        var sourceTracks = source.Tracks
            .Where(track => track.SegmentObjectId is { } segmentId && sharedSegments.Contains(segmentId))
            .ToArray();
        var targetByObject = target.Tracks
            .Where(track => track.ObjectId is not null && track.SegmentObjectId is not null)
            .GroupBy(track => (track.ObjectId, track.SegmentObjectId))
            .ToDictionary(group => group.Key, group => group.First());
        var mergedSharedTracks = sourceTracks.Select(track =>
        {
            targetByObject.TryGetValue((track.ObjectId, track.SegmentObjectId), out var existing);
            return MergeSharedTrack(existing, track);
        }).ToArray();
        var tracks = target.Tracks
            .Where(track => track.SegmentObjectId is not { } segmentId || !sharedSegments.Contains(segmentId))
            .Concat(mergedSharedTracks)
            .ToArray();

        var markers = target.Markers
            .Where(marker => marker.SegmentObjectId is not { } segmentId || !sharedSegments.Contains(segmentId))
            .Concat(source.Markers.Where(marker => marker.SegmentObjectId is { } segmentId
                && sharedSegments.Contains(segmentId)))
            .ToArray();
        var segmentBpms = target.SegmentBpms.ToDictionary(item => item.Key, item => item.Value);
        foreach (var tempo in source.SegmentBpms.Where(item => sharedSegments.Contains(item.Key)))
        {
            segmentBpms[tempo.Key] = tempo.Value;
        }

        return target with
        {
            TimelineLengthMs = source.TimelineLengthMs,
            Tracks = tracks,
            Markers = markers,
            SegmentBpms = segmentBpms,
            Replacements = source.Replacements.ToDictionary(item => item.Key, item => item.Value),
            Imports = source.Imports.ToDictionary(item => item.Key, item => item.Value)
        };
    }

    private static MusicTimelineTrack MergeSharedTrack(
        MusicTimelineTrack? target,
        MusicTimelineTrack source)
    {
        if (target is null)
        {
            return source;
        }

        var remaining = target.Clips.ToList();
        var clips = source.Clips.Select(clip =>
        {
            var existing = remaining.FirstOrDefault(candidate =>
                candidate.SourceIdOffset == clip.SourceIdOffset
                && candidate.PlaylistIndex == clip.PlaylistIndex
                && candidate.MediaId == clip.MediaId);
            if (existing is not null)
            {
                remaining.Remove(existing);
            }

            return clip with { Id = existing?.Id ?? clip.Id };
        }).ToArray();
        return source with
        {
            Id = target.Id,
            Name = target.Name,
            Clips = clips,
            IsMuted = target.IsMuted,
            IsSolo = target.IsSolo,
            Gain = target.Gain
        };
    }

    private void RestoreTimelineSnapshot(TimelineSnapshot snapshot)
    {
        StopAudioPreview();
        restoringProject = true;
        try
        {
            loadedTimeline = snapshot.LoadedTimeline;
            timeline.SetStandaloneAudioMode(ActiveStandaloneMedia is not null);
            timeline.SetSourceEvent(snapshot.LoadedTimeline?.Event.Name);
            scopeReplacements.Clear();
            foreach (var replacement in snapshot.Replacements)
            {
                scopeReplacements.Add(replacement.Key, replacement.Value);
            }

            scopeImports.Clear();
            foreach (var import in snapshot.Imports)
            {
                scopeImports.Add(import.Key, import.Value);
            }

            document.Reset(
                snapshot.Bpm,
                snapshot.TimelineLengthMs,
                snapshot.Tracks,
                snapshot.Markers,
                snapshot.BeatsPerBar,
                snapshot.SubdivisionsPerBeat,
                snapshot.SnapEnabled,
                snapshot.SegmentBpms
            );

            SyncGlobalMetronomeState();
            timeline.SetVisibleSegments(snapshot.VisibleSegmentIds);
            timeline.RestoreViewState(snapshot.View);
            timeline.SetPlaybackPosition(0);
            timeline.ShowMarkers = showWwiseCues.IsChecked == true;
            snapEnabled.IsChecked = snapshot.SnapEnabled;
        }
        finally
        {
            restoringProject = false;
        }

        UpdateTimelineHeading();
        UpdateSelectedSegmentTempoUi();
        timeline.SetDirtyTracks(activeTimelineTab is null
            ? Array.Empty<Guid>()
            : activeTimelineTab.DirtyTrackIds);
        UpdateTimelineControlAvailability();
        ScheduleWaveforms(clear: true);
    }

    private void TimelineTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (timelineTabList.SelectedItem is not TimelineTab selected)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => timelineTabList.ScrollIntoView(selected));
        if (switchingTimelineTab || ReferenceEquals(selected, activeTimelineTab))
        {
            return;
        }

        SaveActiveTimelineTab();
        activeTimelineTab = selected;
        RestoreTimelineSnapshot(selected.Snapshot);
        PromoteTimelineTab(selected);
        SetStatus($"Switched to {selected.Title}");
    }

    private void TimelineTabsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var scroll = timelineTabList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null)
        {
            return;
        }

        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;

        scroll.Offset = new Vector(Math.Clamp(scroll.Offset.X - delta * 90, 0, maximum), 0);
        e.Handled = true;
    }

    private void CloseTimelineTab(TimelineTab tab)
    {
        var index = timelineTabItems.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, activeTimelineTab);
        if (wasActive)
        {
            ResetTransportForTimelineNavigation();
        }

        switchingTimelineTab = true;
        timelineTabItems.Remove(tab);
        switchingTimelineTab = false;
        if (wasActive)
        {
            var next = timelineTabItems.Count == 0
                ? null
                : timelineTabItems[Math.Clamp(index, 0, timelineTabItems.Count - 1)];
            activeTimelineTab = next;
            timelineTabList.SelectedItem = next;
            if (next is null)
            {
                ClearTimeline();
                UpdateTimelineControlAvailability();
            }
            else
            {
                RestoreTimelineSnapshot(next.Snapshot);
            }
        }

        MarkProjectDirty();
        SetStatus($"Closed {tab.Title}");
    }

    private void UpdateTimelineHeading()
    {
        timeline.SetAudioColorScope(ActiveStandaloneMedia?.Bank ?? loadedTimeline?.Event.Bank);
        timelineHeading.Text = ActiveStandaloneMedia is { } sound
            ? activeTimelineTab?.InspectionEventId is not null
                ? $"EVENT SOUND  |  {activeTimelineTab.Title}  |  media {sound.Id}  |  single rendered track"
                : $"SOUND EDITOR  |  {Path.GetFileNameWithoutExtension(sound.SourceName)}  |  media {sound.Id}  |  single rendered track"
            : loadedTimeline is null
            ? activeTimelineTab?.InspectionEventId is not null
                ? $"EVENT  |  {activeTimelineTab.Title}  |  no playable Music Segment"
                : "COMPOSITION TIMELINE  |  select an Event"
            : activeTimelineTab?.OccurrenceMediaId is { } mediaId
                ? $"CLIP OCCURRENCES  |  media {mediaId}  |  {timeline.VisibleSegmentCount} segments  |  {timeline.VisibleTracks.Count} tracks"
                : $"EVENT COMPOSITION  |  {timeline.VisibleSegmentCount} segments  |  {timeline.VisibleTracks.Count} tracks  |  click a segment header to audition/edit its BPM";
    }

    private void ResetTimelineForNavigation()
    {
        restoringProject = true;
        try
        {
            ClearTimeline();
            ResetTimelineTabs();
        }
        finally
        {
            restoringProject = false;
        }

        currentProjectPath = null;
        projectDirty = importedAudio.Count > 0;
        cleanProjectFingerprint = importedAudio.Count == 0 ? ProjectFingerprint() : cleanProjectFingerprint;

        UpdateWindowTitle();
    }

    private void ClearBrowserSelection()
    {
        suppressBrowserSelection = true;
        results.SelectedItem = null;
        acceptedBrowserSelection = null;
        suppressBrowserSelection = false;
    }

    private void UpdateTimelineControlAvailability()
    {
        var available = document.Tracks.Count > 0;
        var standalone = ActiveStandaloneMedia is not null;

        bpmInput.IsEnabled = available;

        snapEnabled.IsVisible = !standalone;
        showWwiseCues.IsVisible = !standalone;
        metronomeEnabled.IsVisible = !standalone;
        snapEnabled.IsEnabled = available && !standalone;

        playTimeline.IsEnabled = available && !timelineContentLoading;
        pausePreview.IsEnabled = previewPlayer.State is AudioPreviewState.Playing or AudioPreviewState.Paused;
        pausePreview.Content = previewPlayer.State == AudioPreviewState.Paused ? "Resume" : "Pause";
        playTimeline.Content = timelineContentLoading ? "Loading..." : "Play";

        var canBuild = !operationBusy && index is not null && ProjectHasBuildableChanges();

        buildPak.IsEnabled = canBuild;
        buildPakMenu.IsEnabled = canBuild;
    }

    private bool ProjectHasBuildableChanges() => timelineTabItems.Any(tab =>
        tab.StandaloneMedia is { } media
            ? HasStandaloneEdits(tab.Snapshot, media)
            : SnapshotEditScore(tab.Snapshot) > 0);

    private void OnDocumentChanged()
    {
        if (!renderingComposition)
        {
            MarkProjectDirty();
        }

        if (!renderingComposition && loadedTimeline is not null)
        {
            var assignments = document.Tracks.SelectMany(track => track.Clips)
                .Where(clip => clip.MediaId is not null && clip.ReplacementMediaId is { } replacementId
                    && !scopeImports.ContainsKey(replacementId) && clip.SourcePath is not null)
                .GroupBy(clip => clip.MediaId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => new ScopeReplacement(
                        group.First().ReplacementMediaId!.Value,
                        group.First().SourcePath!,
                        group.First().PhysicalDurationMs ?? group.First().DurationMs));
            scopeReplacements.Clear();
            foreach (var assignment in assignments)
            {
                scopeReplacements.Add(assignment.Key, assignment.Value);
            }
        }

        if (!renderingComposition && !bpmInput.IsFocused)
        {
            UpdateSelectedSegmentTempoUi();
        }

        if (!renderingComposition && !restoringProject && activeTimelineTab is not null)
        {
            PromoteTimelineTab(activeTimelineTab);
            activeTimelineTab.UpdateSnapshot(CaptureTimelineSnapshot());
            SynchronizeCompositionTabs(activeTimelineTab);
            timeline.SetDirtyTracks(activeTimelineTab.DirtyTrackIds);
        }

        SyncGlobalMetronomeState();
        UpdateTimelineControlAvailability();
    }

    private void ShowTimelineContextMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(timeline, out var position) && !timeline.SelectForContext(position))
        {
            return;
        }

        if (timeline.SelectedTrackId is not { } trackId
            || document.Tracks.FirstOrDefault(track => track.Id == trackId) is not { } track)
        {
            return;
        }

        var items = new List<Control>();
        if (timeline.SelectedClipId is not null)
        {
            var (_, selectedClip) = document.FindClip(timeline.SelectedClipId.Value);
            var catalogItem = CatalogItemForTimelineClip(selectedClip);
            var playClip = MenuAction("Play selected clip", PlaySelectedClipAsync);
            var fadeIn = MenuAction("Fade in", (_, _) => MakeFadeFromSelection(MusicTimelineFadeKind.FadeIn));
            var fadeOut = MenuAction("Fade out", (_, _) => MakeFadeFromSelection(MusicTimelineFadeKind.FadeOut));
            fadeIn.IsEnabled = timeline.SelectionEndMs - timeline.SelectionStartMs > 1;
            fadeOut.IsEnabled = fadeIn.IsEnabled;

            items.Add(playClip);
            items.Add(MenuAction("Calculate BPM", CalculateSelectedBpmAsync));
            items.Add(MenuAction("Copy clip", (_, _) => CopySelectedClip()));
            items.Add(MenuAction("Copy clip name", async (_, _) => await CopyTextAsync(selectedClip.Name)));
            if (catalogItem is not null)
            {
                items.Add(MenuAction(
                    pinnedClipKeys.Contains(catalogItem.Key) ? "Unpin clip" : "Pin clip",
                    (_, _) => TogglePinnedClip(catalogItem)));
            }
            items.Add(MenuAction("Duplicate clip", (_, _) => timeline.DuplicateSelected()));
            items.Add(MenuAction("Split clip at playhead", (_, _) => timeline.SplitSelected()));
            items.Add(fadeIn);
            items.Add(fadeOut);
            items.Add(MenuAction("Delete clip", (_, _) => timeline.DeleteSelected()));
            items.Add(new Separator());
        }

        items.Add(MenuAction(ActiveStandaloneMedia is null
            ? "Play this segment from cursor"
            : "Play sound from cursor", PlayTimelineAsync));
        if (ActiveStandaloneMedia is null)
        {
            items.Add(MenuAction(track.IsMuted ? "Unmute track" : "Mute track",
                (_, _) => timeline.ToggleSelectedTrackMuted()));
            items.Add(MenuAction(track.IsSolo ? "Clear track isolation" : "Isolate track",
                (_, _) => timeline.ToggleSelectedTrackSolo()));
            items.Add(new Separator());
            items.Add(MenuAction("Add track below", (_, _) => AddTrack()));
            items.Add(MenuAction("Remove track", (_, _) => timeline.RemoveSelectedTrack()));
        }

        var paste = MenuAction("Paste copied clip at playhead", PasteClipAsync);
        paste.IsEnabled = copiedTimelineClip is not null;
        items.Add(paste);
        items.Add(new Separator());
        items.Add(MenuAction("Fit timeline to width", (_, _) => FitTimeline()));
        new ContextMenu { ItemsSource = items }.Open(timeline);
        e.Handled = true;
    }

    private void ShowTimelineSelection()
    {
        if (timeline.SelectedClipId is not { } id)
        {
            return;
        }

        var (selectedTrack, clip) = document.FindClip(id);
        var occurrences = clip.MediaId is { } mediaId
            ? document.Tracks.SelectMany(track => track.Clips).Count(item => item.MediaId == mediaId)
            : 1;
        var indexedMedia = clip.MediaId is { } indexedId
            ? index?.Media.FirstOrDefault(item => item.Id == indexedId)
            : null;
        var fade = (clip.HasFadeIn, clip.HasFadeOut) switch
        {
            (true, true) => "fade in and fade out",
            (true, false) => "fade in",
            (false, true) => "fade out",
            _ => "none"
        };
        var fit = MusicTimelineAnalysis.Analyze(selectedTrack, clip);
        var fitLabel = fit.Severity switch
        {
            MusicClipFitSeverity.Error => "invalid placement",
            MusicClipFitSeverity.Warning => "replacement requires attention",
            _ => "valid placement"
        };
        var repeated = fit.RepeatedMs > 1
            ? clip.SourcePath is null && clip.RepeatsSource
                ? $"{FormatMs(fit.RepeatedMs)} (authored loop)"
                : $"{FormatMs(fit.RepeatedMs)} (replacement must repeat)"
            : "none";
        ShowInspector($"TIMELINE CLIP\n\nName: {clip.Name}\n"
            + $"Start: {FormatMs(clip.StartMs)}\nDuration: {FormatMs(clip.DurationMs)}\n"
            + $"Source offset: {FormatMs(clip.SourceOffsetMs)}\n"
            + $"Physical source: {(clip.PhysicalDurationMs is { } physical ? FormatMs(physical) : "unknown")}\n"
            + $"Playlist item: {(clip.PlaylistIndex is { } playlist ? (playlist + 1).ToString(CultureInfo.InvariantCulture) : "manual")}\n"
            + $"Automation: {fade}\n"
            + $"Fade in: {(clip.FadeInMs > 0 ? FormatMs(clip.FadeInMs) : "none")}\n"
            + $"Fade out: {(clip.FadeOutMs > 0 ? FormatMs(clip.FadeOutMs) : "none")}\n"
            + $"Trimmed head: {FormatMs(fit.TrimmedHeadMs)}\n"
            + $"Used source: {FormatMs(fit.UsedPhysicalMs)}\n"
            + $"Unused tail: {FormatMs(fit.UnusedTailMs)}\n"
            + $"Repeated region: {repeated}\n"
            + $"Segment overrun: {(fit.SegmentOverrunMs > 1 ? FormatMs(fit.SegmentOverrunMs) : "none")}\n"
            + $"Validation: {fitLabel}\n"
            + $"Original media: {clip.MediaId?.ToString(CultureInfo.InvariantCulture) ?? "external audio"}\n"
            + $"Storage: {(indexedMedia is null ? "unknown" : TimelineStorage(indexedMedia))}\n"
            + $"New media: {clip.ReplacementMediaId?.ToString(CultureInfo.InvariantCulture) ?? "not assigned"}\n"
            + $"Occurrences in composition: {occurrences}\n"
            + $"Replacement: {clip.SourcePath ?? "none"}");
    }

    private void MakeFadeFromSelection(MusicTimelineFadeKind kind)
    {
        if (timeline.SelectedClipId is not { } clipId
            || timeline.SelectionStartMs is not { } selectedStart
            || timeline.SelectionEndMs is not { } selectedEnd
            || selectedEnd - selectedStart <= 1)
        {
            SetStatus("Select a range inside the clip first", GuiLogLevel.Warning);
            return;
        }

        var (_, clip) = document.FindClip(clipId);
        MusicTimelineFadeResult result;
        try
        {
            result = document.MakeFadeFromSelection(clipId, selectedStart, selectedEnd, kind);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException)
        {
            SetStatus(exception.Message, GuiLogLevel.Warning);
            return;
        }

        ShowTimelineSelection();
        var fadeLabel = result.Kind == MusicTimelineFadeKind.FadeIn ? "fade-in" : "fade-out";
        SetStatus($"Created {FormatMs(result.DurationMs)} {fadeLabel} on {clip.Name}"
            + (result.TrimmedClip ? "; audio outside the selected fade edge was trimmed" : string.Empty));
        RefreshPlayingTimelineArrangement();
    }

    private void RefreshPlayingTimelineArrangement()
    {
        if (!followPlaybackTimeline || !previewPlayer.HasSource)
        {
            return;
        }

        mixRefreshStartMs = timeline.PlayheadMs;
        pauseAfterMixRefresh = previewPlayer.State == AudioPreviewState.Paused;
        PlayTimelineAsync(null, new RoutedEventArgs());
    }

    private async void BuildScopedPakAsync(object? sender, RoutedEventArgs e)
    {
        var localIndex = index;
        if (localIndex is null)
        {
            SetStatus("Game data is not ready. Complete setup in Preferences before building a mod PAK", GuiLogLevel.Warning);
            return;
        }

        SaveActiveTimelineTab();
        if (SynchronizeCompositionTabs() && activeTimelineTab is not null)
        {
            RestoreTimelineSnapshot(activeTimelineTab.Snapshot);
        }

        var compositionSnapshots = timelineTabItems
            .Where(tab => tab.Snapshot.LoadedTimeline is not null && tab.StandaloneMedia is null)
            .SelectMany(tab => tab.Snapshot.LoadedTimeline!.AllTimingScopes.Select(scope =>
                SnapshotForTimingScope(tab.Snapshot, scope)))
            .GroupBy(CompositionScopeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(SnapshotEditScore).Last())
            .Where(snapshot => SnapshotEditScore(snapshot) > 0)
            .ToArray();
        var soundTabs = timelineTabItems
            .Where(tab => tab.StandaloneMedia is { } media && HasStandaloneEdits(tab.Snapshot, media))
            .GroupBy(tab => tab.StandaloneMedia!.Id)
            .Select(group => group.Last())
            .ToArray();
        if (compositionSnapshots.Length == 0 && soundTabs.Length == 0)
        {
            SetStatus("Make at least one audio or timing edit anywhere in the project first", GuiLogLevel.Warning);
            return;
        }

        var requests = new List<ScopedModPakRequest>(compositionSnapshots.Length);
        try
        {
            foreach (var snapshot in compositionSnapshots)
            {
                var composition = snapshot.LoadedTimeline!;
                var activeImports = ActiveStructuralImports(snapshot);
                var activeReplacements = ActiveScopeReplacements(snapshot);
                var edits = CreateScopedTimelineEdits(
                    composition,
                    snapshot.Tracks,
                    snapshot.SegmentBpms,
                    snapshot.Replacements,
                    snapshot.Imports);
                var replacements = activeReplacements.Select(item => new ScopedMediaReplacement(
                    item.Key,
                    item.Value.NewMediaId,
                    item.Value.Path)).Concat(activeImports.Select(item => new ScopedMediaReplacement(
                        item.TemplateMediaId,
                        item.NewMediaId,
                        item.Path,
                        ReferencesAlreadyUseNewId: true))).ToArray();
                requests.Add(new ScopedModPakRequest(
                    composition.Event.Id.ToString(CultureInfo.InvariantCulture),
                    composition.Scope.ObjectId,
                    composition.AuthoredBpm,
                    composition.AuthoredBpm,
                    replacements,
                    edits.FieldEdits,
                    edits.PlaylistEdits,
                    snapshot.SegmentBpms
                        .Where(item => !Near(item.Value, composition.AuthoredBpm))
                        .Select(item => new ScopedSegmentTempoChange(
                            item.Key,
                            composition.AuthoredBpm,
                            item.Value))
                        .ToArray(),
                    CreateMarkerEdits(composition, snapshot.Markers, snapshot.SegmentBpms),
                    CreateSegmentDurationEdits(composition, snapshot.Tracks, snapshot.SegmentBpms)));
            }
        }
        catch (InvalidDataException exception)
        {
            SetStatus($"Cannot build this project yet: {exception.Message}", GuiLogLevel.Error);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Build mod PAK",
            SuggestedFileName = SuggestedPakFileName(),
            DefaultExtension = "pak",
            FileTypeChoices = [new FilePickerFileType("Unreal Engine PAK") { Patterns = ["*.pak"] }]
        });
        if (file?.TryGetLocalPath() is not { } outputPath)
        {
            return;
        }

        var aesKey = CurrentAesKey();
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            aesKey = await new PasswordPromptDialog(
                "Building the mod requires extracting the effective bank from the encrypted game PAK.")
                .ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(aesKey))
            {
                SetStatus("Mod PAK build cancelled");
                return;
            }

            settings.AesKey = aesKey;
        }

        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();
        indexOperation = operation;
        var workingDirectory = Path.Combine(Path.GetTempPath(), "HbkWwise", $"project-{Guid.NewGuid():N}");
        SetBusy(true, $"Building every project edit into {Path.GetFileName(outputPath)}");
        try
        {
            Directory.CreateDirectory(workingDirectory);
            var directSources = new List<WwiseSourceInput>(soundTabs.Length);
            foreach (var tab in soundTabs)
            {
                var media = tab.StandaloneMedia!;
                var track = tab.Snapshot.Tracks.SingleOrDefault()
                    ?? throw new InvalidDataException($"Sound tab {tab.Title} does not contain exactly one track.");
                var placements = new List<TimelineAudioPlacement>(track.Clips.Length);
                foreach (var clip in track.Clips.Where(IsPreviewableClip))
                {
                    placements.Add(new TimelineAudioPlacement(
                        await PrepareClipWavAsync(clip, aesKey, operation.Token),
                        clip.StartMs,
                        clip.SourceOffsetMs,
                        clip.DurationMs,
                        clip.RepeatsSource,
                        clip.FadeInMs,
                        clip.FadeOutMs,
                        track.Gain));
                }

                if (placements.Count == 0)
                {
                    throw new InvalidDataException($"Sound tab {tab.Title} has no playable audio blocks.");
                }

                var rendered = Path.Combine(workingDirectory, $"sound-{media.Id}.wav");
                await Task.Run(
                    () => TimelineAudioRenderer.Render(placements, rendered, operation.Token),
                    operation.Token);
                directSources.Add(new WwiseSourceInput(media.Id, rendered));
            }

            var directMedia = directSources.Count == 0
                ? new Dictionary<uint, string>()
                : await WwiseSourceConverter.ConvertAsync(
                    directSources,
                    Path.Combine(workingDirectory, "direct-encoder"),
                    settings.WwiseConsolePath,
                    settings.VgmstreamPath,
                    operation.Token);
            var result = await Task.Run(
                () => ProjectModPakBuilder.BuildAsync(
                    localIndex,
                    requests,
                    directMedia,
                    outputPath,
                    settings.RepakPath,
                    aesKey,
                    settings.WwiserPath,
                    settings.PythonPath,
                    vgmstreamPath: settings.VgmstreamPath,
                    wwiseConsolePath: settings.WwiseConsolePath,
                    cancellationToken: operation.Token).GetAwaiter().GetResult(),
                operation.Token);
            var warnings = result.Compositions.Sum(composition =>
                composition.Validation.Issues.Count(item => item.Severity == BnkTimelineSeverity.Warning));
            var mountWarning = DirectPakMountWarning(localIndex, result.OutputPath);
            ShowInspector($"PROJECT MOD PAK BUILT\n\nOutput: {result.OutputPath}\n"
                + $"SoundBanks: {string.Join(", ", result.Banks)}\n"
                + $"Edited compositions: {result.Compositions.Length}\n"
                + $"Edited standalone sounds: {soundTabs.Length}\n"
                + $"New media: {result.Compositions.Sum(item => item.Imports.Length)}\n"
                + $"Timeline patches: {result.Compositions.Sum(item => item.TimelinePatchCount)}\n"
                + $"PAK entries: {result.Entries.Length}\nWarnings: {warnings}\n"
                + (mountWarning is null ? string.Empty : $"Mount warning: {mountWarning}\n")
                + "\n"
                + string.Join('\n', result.Entries.Select(entry => $"  {entry}")));
            SetStatus(
                $"Built {Path.GetFileName(result.OutputPath)} with every project edit across {result.Banks.Length} SoundBank{(result.Banks.Length == 1 ? string.Empty : "s")}"
                + (warnings == 0 ? string.Empty : $" and {warnings} physical-duration warning{(warnings == 1 ? string.Empty : "s")}")
                + (mountWarning is null ? string.Empty : $"; {mountWarning}"),
                warnings == 0 && mountWarning is null ? GuiLogLevel.Info : GuiLogLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Mod PAK build cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Mod PAK build failed", exception);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                try
                {
                    Directory.Delete(workingDirectory, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private static string? DirectPakMountWarning(WwiseIndex index, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        var installedBesideGamePaks = index.Paks?.Any(pak => string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(pak.Path)),
            outputDirectory,
            StringComparison.OrdinalIgnoreCase)) == true;
        var updatePakExists = index.Paks?.Any(pak => Path.GetFileNameWithoutExtension(pak.Path)
            .EndsWith("_P", StringComparison.OrdinalIgnoreCase)) == true;
        return installedBesideGamePaks
            && updatePakExists
            && !Path.GetFileNameWithoutExtension(outputPath).EndsWith("_P", StringComparison.OrdinalIgnoreCase)
                ? "this file may be overridden by the game's _P update PAK; mount it through Overdub or rename it with an _P suffix"
                : null;
    }

    private static ScopedTimelineExportEdits CreateScopedTimelineEdits(
        LoadedEventTimeline composition,
        IReadOnlyCollection<MusicTimelineTrack> tracks,
        IReadOnlyDictionary<uint, double> segmentBpms,
        IReadOnlyDictionary<uint, ScopeReplacement> replacements,
        IReadOnlyDictionary<uint, StructuralImport> imports)
    {
        var originals = composition.Validation.Clips
            .Where(clip => clip.SourceIdOffset is not null)
            .GroupBy(clip => clip.SourceIdOffset!.Value)
            .Select(group => group.First())
            .ToArray();
        var placements = tracks.SelectMany(track => track.Clips.Select(clip => (Track: track, Clip: clip))).ToArray();
        if (placements.Any(item => item.Clip.SourceIdOffset is null || item.Clip.FieldOffsets is null))
        {
            throw new InvalidDataException("Imported blocks need a Wwise playlist source assignment before they can be exported.");
        }

        var authoredCounts = originals.GroupBy(clip => clip.SourceIdOffset!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var currentCounts = placements.GroupBy(item => item.Clip.SourceIdOffset!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var structural = authoredCounts.Count != currentCounts.Count || authoredCounts.Any(item =>
                currentCounts.GetValueOrDefault(item.Key) != item.Value)
            || placements.Any(placement => originals.First(item =>
                    item.SourceIdOffset == placement.Clip.SourceIdOffset).TrackObjectId
                != placement.Track.ObjectId)
            || placements.Any(placement => FadeChanged(
                originals.First(item => item.SourceIdOffset == placement.Clip.SourceIdOffset),
                placement.Clip));
        if (structural)
        {
            if (tracks.Any(track => track.ObjectId is null))
            {
                throw new InvalidDataException("A manual track cannot become a new Wwise Music Track yet; move its blocks onto an authored track.");
            }

            var knownTracks = originals.Select(clip => clip.TrackObjectId).ToHashSet();
            var unknownTarget = tracks.FirstOrDefault(track =>
                track.ObjectId is { } objectId && !knownTracks.Contains(objectId));
            if (unknownTarget is not null)
            {
                throw new InvalidDataException($"Track {unknownTarget.Name} is outside the loaded composition.");
            }

            var playlistEdits = knownTracks.Order().Select(trackId => new BnkTrackPlaylistEdit(
                trackId,
                placements.Where(item => item.Track.ObjectId == trackId).Select(item =>
                {
                    var clip = item.Clip;
                    var original = originals.Single(value => value.SourceIdOffset == clip.SourceIdOffset);
                    var directImport = clip.ReplacementMediaId is { } replacementId
                        && imports.ContainsKey(replacementId);
                    return new BnkTrackPlaylistItemEdit(
                        clip.SourceIdOffset,
                        directImport ? clip.ReplacementMediaId!.Value : original.MediaId,
                        original.TrackObjectId == trackId ? original.SubTrackId : 0,
                        original.EventId,
                        clip.StartMs,
                        clip.SourceOffsetMs,
                        clip.DurationMs,
                        clip.PhysicalDurationMs ?? original.SourceDurationMs,
                        PreserveAutomation: !directImport,
                        Fades: FadeChanged(original, clip)
                            ? new BnkClipFadeEdit(clip.FadeInMs, clip.FadeOutMs)
                            : null,
                        TemplateMediaId: directImport
                            ? imports[clip.ReplacementMediaId!.Value].TemplateMediaId
                            : null);
                }).ToArray())).ToArray();
            return new ScopedTimelineExportEdits(null, playlistEdits);
        }

        var edits = new List<BnkTimelineClipEdit>();
        foreach (var placement in placements)
        {
            var clip = placement.Clip;
            var sourceOffset = clip.SourceIdOffset!.Value;
            var original = originals.FirstOrDefault(item =>
                item.SourceIdOffset == sourceOffset
                && item.TrackObjectId == placement.Track.ObjectId
                && item.SegmentObjectId == placement.Track.SegmentObjectId)
                ?? throw new InvalidDataException(
                    $"Block {clip.Name} was moved to another Wwise track. Cross-track reassignment is not supported yet.");
            var ratio = AuthoredBpmForSegment(composition, placement.Track.SegmentObjectId)
                / SegmentBpm(segmentBpms, composition, placement.Track.SegmentObjectId);
            var expectedStart = Math.Max(0, original.TimelineStartMs) * ratio;
            var expectedSourceOffset = Math.Max(0, original.BeginTrimMs) * ratio;
            var expectedDuration = Math.Max(
                1,
                (original.TimelineEndMs - Math.Max(0, original.TimelineStartMs)) * ratio);
            var replaced = original.MediaId != 0 && replacements.ContainsKey(original.MediaId);
            if (replaced || !Near(clip.StartMs, expectedStart)
                || !Near(clip.SourceOffsetMs, expectedSourceOffset)
                || !Near(clip.DurationMs, expectedDuration))
            {
                edits.Add(new BnkTimelineClipEdit(
                    sourceOffset,
                    clip.StartMs,
                    clip.SourceOffsetMs,
                    clip.DurationMs));
            }
        }

        return new ScopedTimelineExportEdits(edits.ToArray(), null);
    }

    private static BnkTimelineMarkerEdit[] CreateMarkerEdits(
        LoadedEventTimeline composition,
        IReadOnlyCollection<MusicTimelineMarker> markers,
        IReadOnlyDictionary<uint, double> segmentBpms)
    {
        var originals = composition.Validation.Segments
            .SelectMany(segment => segment.Markers.Select(marker => (SegmentId: segment.ObjectId, Marker: marker)))
            .Where(item => item.Marker.PositionOffset is not null)
            .ToDictionary(item => item.Marker.PositionOffset!.Value);
        return markers.SelectMany(marker => (marker.PositionOffsets ?? []).Select(offset =>
            {
                if (!originals.TryGetValue(offset, out var original))
                {
                    return null;
                }

                var expected = original.Marker.PositionMs
                    * AuthoredBpmForSegment(composition, original.SegmentId)
                    / SegmentBpm(segmentBpms, composition, original.SegmentId);
                return Near(marker.PositionMs, expected)
                    ? null
                    : new BnkTimelineMarkerEdit(offset, marker.PositionMs);
            }))
            .OfType<BnkTimelineMarkerEdit>()
            .ToArray();
    }

    private static BnkTimelineSegmentDurationEdit[] CreateSegmentDurationEdits(
        LoadedEventTimeline composition,
        IReadOnlyCollection<MusicTimelineTrack> tracks,
        IReadOnlyDictionary<uint, double> segmentBpms) =>
        composition.Validation.Segments
            .Where(segment => segment.DurationOffset is not null)
            .Select(segment =>
            {
                var current = tracks.Where(track => track.SegmentObjectId == segment.ObjectId)
                    .Select(track => track.LengthMs)
                    .OfType<double>()
                    .DefaultIfEmpty(segment.DurationMs)
                    .Max();
                var expected = segment.DurationMs
                    * AuthoredBpmForSegment(composition, segment.ObjectId)
                    / SegmentBpm(segmentBpms, composition, segment.ObjectId);
                return Near(current, expected)
                    ? null
                    : new BnkTimelineSegmentDurationEdit(segment.DurationOffset!.Value, current);
            })
            .OfType<BnkTimelineSegmentDurationEdit>()
            .ToArray();

    private static bool HasStandaloneEdits(TimelineSnapshot snapshot, MediaRecord media)
    {
        if (snapshot.Tracks.Length != 1 || snapshot.Tracks[0].Clips.Length != 1)
        {
            return snapshot.Tracks.SelectMany(track => track.Clips).Any();
        }

        var track = snapshot.Tracks[0];
        var clip = track.Clips[0];
        var originalDuration = clip.PhysicalDurationMs ?? clip.DurationMs;
        return clip.MediaId != media.Id
            || clip.SourcePath is not null
            || !Near(clip.StartMs, 0)
            || !Near(clip.SourceOffsetMs, 0)
            || !Near(clip.DurationMs, originalDuration)
            || clip.RepeatsSource
            || !Near(clip.FadeInMs, 0)
            || !Near(clip.FadeOutMs, 0)
            || !Near(track.Gain, 1);
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) <= 0.01;

    private static bool FadeChanged(BnkTimelineClip original, MusicTimelineClip clip) =>
        !Near(original.FadeInMs, clip.FadeInMs) || !Near(original.FadeOutMs, clip.FadeOutMs);

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private string SuggestedPakFileName() => currentProjectPath is { Length: > 0 }
        ? $"{SafeFileName(Path.GetFileNameWithoutExtension(currentProjectPath))}.pak"
        : "HbkWwise-Mod.pak";

    private uint AllocateReplacementMediaId(uint oldMediaId, string path)
    {
        var used = (index?.Media.Select(media => media.Id) ?? [])
            .Concat(scopeReplacements.Values.Select(item => item.NewMediaId))
            .Concat(scopeImports.Keys)
            .ToHashSet();
        return WwiseHash.AllocateMediaId(
            $"HBK_{loadedTimeline?.Scope.ObjectId}_{oldMediaId}_{Path.GetFileName(path)}",
            used);
    }

    private StructuralImport[] ActiveStructuralImports()
        => ActiveStructuralImports(CaptureTimelineSnapshot());

    private static StructuralImport[] ActiveStructuralImports(TimelineSnapshot snapshot)
    {
        var activeIds = snapshot.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.ReplacementMediaId)
            .OfType<uint>()
            .ToHashSet();
        return snapshot.Imports.Where(item => activeIds.Contains(item.Key)).Select(item => item.Value).ToArray();
    }

    private static KeyValuePair<uint, ScopeReplacement>[] ActiveScopeReplacements(TimelineSnapshot snapshot)
    {
        var activeIds = snapshot.Tracks.SelectMany(track => track.Clips)
            .Where(clip => clip.MediaId is not null
                && clip.SourcePath is not null
                && clip.ReplacementMediaId is { } replacementId
                && !snapshot.Imports.ContainsKey(replacementId))
            .Select(clip => clip.MediaId!.Value)
            .ToHashSet();
        return snapshot.Replacements.Where(item => activeIds.Contains(item.Key)).ToArray();
    }

    private void AddSelectedMedia()
    {
        if (results.SelectedItem is not BrowserNode { Media: { } media })
        {
            SetStatus("Select one media item in the browser first");
            return;
        }

        var duration = document.BeatMilliseconds * document.BeatsPerBar * 8;
        var trackId = timeline.SelectedTrackId ?? document.AddTrack();
        document.AddClip(trackId, Path.GetFileNameWithoutExtension(media.SourceName),
            timeline.PlayheadMs, duration, media.Id);
        ScheduleWaveforms();
        SetStatus($"Added media {media.Id} as an editable 8-bar block");
    }

    private async void AddAudioAsync(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add audio to library",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.flac", "*.mp3", "*.ogg", "*.wem"] }]
        });
        var added = 0;
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
            {
                continue;
            }

            try
            {
                _ = await ImportAudioAsync(path);
                added++;
            }
            catch (Exception exception)
            {
                SetFailure($"Could not import {Path.GetFileName(path)}", exception);
            }
        }

        if (added > 0)
        {
            SetStatus($"Imported {added} audio file{(added == 1 ? string.Empty : "s")}; drag them onto a clip or timeline track");
        }
    }

    private async Task<ImportedAudio> ImportAudioAsync(string path, string? displayName = null)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = importedAudio.FirstOrDefault(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            importedAudioList.SelectedItem = existing;
            return existing;
        }

        var format = await VgmstreamClient.InspectAsync(fullPath, settings.VgmstreamPath);
        var item = new ImportedAudio(
            Guid.NewGuid(),
            displayName ?? Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            format);
        importedAudio.Add(item);
        importedAudioList.SelectedItem = item;
        RefreshClipCatalog();
        MarkProjectDirty();
        return item;
    }

    private async void ImportSelectedGameMediaAsync(object? sender, RoutedEventArgs e)
    {
        if (results.SelectedItem is not BrowserNode { Media: { } media } || index is null)
        {
            SetStatus("Select a Media item in the browser first", GuiLogLevel.Warning);
            return;
        }

        if (!media.IsPlayableAudio)
        {
            SetStatus($"{media.SourceName} is Wwise MIDI/control data and cannot be imported as audio", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(required: true);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Importing game media {media.Id}", stopCurrentPlayback: false);
        try
        {
            var clip = new MusicTimelineClip(
                Guid.NewGuid(),
                media.Id,
                Path.GetFileNameWithoutExtension(media.SourceName),
                null,
                0,
                0,
                1);
            var wav = await PrepareClipWavAsync(clip, aesKey, operation.Token);
            var imported = await ImportAudioAsync(wav, Path.GetFileNameWithoutExtension(media.SourceName));
            SetStatus($"Imported {media.Id} as {imported.Name}; drag it onto a target segment to create an exportable lane");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Game-media import cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Game-media import failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private async void ExportSelectedGameMediaAsync(object? sender, RoutedEventArgs e)
    {
        if (results.SelectedItem is not BrowserNode { Media: { } media } || index is null)
        {
            SetStatus("Select a Media item in the browser first", GuiLogLevel.Warning);
            return;
        }

        if (!media.IsPlayableAudio)
        {
            SetStatus($"{media.SourceName} is Wwise MIDI/control data and cannot be exported as audio", GuiLogLevel.Warning);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected game media",
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(media.SourceName)}-{media.Id}.wav",
            DefaultExtension = "wav",
            FileTypeChoices =
            [
                new FilePickerFileType("Decoded WAV") { Patterns = ["*.wav"] },
                new FilePickerFileType("Original WEM") { Patterns = ["*.wem"] }
            ]
        });
        if (file?.TryGetLocalPath() is not { } outputPath)
        {
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(required: true);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Exporting game media {media.Id}", stopCurrentPlayback: false);
        try
        {
            if (Path.GetExtension(outputPath).Equals(".wem", StringComparison.OrdinalIgnoreCase))
            {
                await MediaExtractor.ExtractAsync(
                    index,
                    media.Id,
                    outputPath,
                    settings.RepakPath,
                    aesKey,
                    operation.Token);
            }
            else
            {
                var clip = new MusicTimelineClip(
                    Guid.NewGuid(),
                    media.Id,
                    Path.GetFileNameWithoutExtension(media.SourceName),
                    null,
                    0,
                    0,
                    1);
                var wav = await PrepareClipWavAsync(clip, aesKey, operation.Token);
                File.Copy(wav, outputPath, true);
            }

            SetStatus($"Exported {media.Storage} media {media.Id} to {outputPath}; WAV and WEM files can both be imported as clips");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Game-media export cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Game-media export failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private void AddImportedAtPlayhead()
    {
        if (importedAudioList.SelectedItem is not ImportedAudio item)
        {
            SetStatus("Select an imported clip first", GuiLogLevel.Warning);
            return;
        }

        AddImportedToTimeline(item);
    }

    private void RemoveImportedAudio()
    {
        if (importedAudioList.SelectedItem is ImportedAudio item)
        {
            importedAudio.Remove(item);
            pinnedClipKeys.Remove($"imported:{item.Id:N}");
            RefreshClipCatalog();
            MarkProjectDirty();
            SetStatus($"Removed imported source {item.Name} (timeline blocks are unchanged)");
        }
    }

    private void CaptureImportedAudioDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(importedAudioList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        importedAudioDragStart = e.GetPosition(importedAudioList);
        importedAudioDragItem = e.Source is StyledElement { DataContext: ImportedAudio item }
            ? item
            : null;
    }

    private void ClearImportedAudioDrag(object? sender, PointerReleasedEventArgs e)
    {
        importedAudioDragStart = null;
        importedAudioDragItem = null;
    }

    private async void StartImportedAudioDrag(object? sender, PointerEventArgs e)
    {
        if (importedAudioDragStart is not { } start
            || importedAudioDragItem is not { } item
            || !e.GetCurrentPoint(importedAudioList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(importedAudioList);
        if (Math.Abs(current.X - start.X) < 5 && Math.Abs(current.Y - start.Y) < 5)
        {
            return;
        }

        importedAudioDragStart = null;
        importedAudioDragItem = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ImportedAudioDataFormat, item.Id.ToString("N")));
        _ = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void CaptureCatalogDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        catalogDragStart = e.GetPosition(sender as Visual);
        catalogDragItem = e.Source is StyledElement { DataContext: ClipCatalogItem item } ? item : null;
        if (catalogDragItem is not null)
        {
            SelectCatalogItem(catalogDragItem);
        }
    }

    private void ClearCatalogDrag(object? sender, PointerReleasedEventArgs e)
    {
        catalogDragStart = null;
        catalogDragItem = null;
    }

    private async void StartCatalogDrag(object? sender, PointerEventArgs e)
    {
        var visual = sender as Visual;
        if (catalogDragStart is not { } start || catalogDragItem is not { } item
            || !e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(visual);
        if (Math.Abs(current.X - start.X) < 5 && Math.Abs(current.Y - start.Y) < 5)
        {
            return;
        }

        catalogDragStart = null;
        catalogDragItem = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(CatalogClipDataFormat, item.Key));
        _ = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void TimelineDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = (e.DataTransfer.Contains(ImportedAudioDataFormat)
                || e.DataTransfer.Contains(CatalogClipDataFormat))
            && timeline.TrackAt(e.GetPosition(timeline)) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void TimelineDrop(object? sender, DragEventArgs e)
    {
        var position = e.GetPosition(timeline);
        if (e.DataTransfer.TryGetValue(ImportedAudioDataFormat) is { } importedValue
            && Guid.TryParseExact(importedValue, "N", out var id)
            && importedAudio.FirstOrDefault(item => item.Id == id) is { } imported)
        {
            AddImportedToTimeline(imported, position);
            e.Handled = true;
            return;
        }

        if (e.DataTransfer.TryGetValue(CatalogClipDataFormat) is { } key
            && FindCatalogItem(key) is { } catalogItem)
        {
            await AddCatalogClipAsync(catalogItem, position);
            e.Handled = true;
        }
    }

    private Guid? AddImportedToTimeline(ImportedAudio audio, Point? position = null)
    {
        var targetTrack = timeline.TrackAt(position) is { } targetTrackId
            ? document.Tracks.FirstOrDefault(track => track.Id == targetTrackId)
            : null;
        if (targetTrack is null)
        {
            SetStatus("Select a track, or drop the audio onto an existing track", GuiLogLevel.Warning);
            return null;
        }

        var targetSegmentId = targetTrack.SegmentObjectId;
        if (loadedTimeline is null || targetSegmentId is null)
        {
            var clipId = timeline.AddExternalClip(audio.Name, audio.Path, audio.DurationMs, position);
            ScheduleWaveforms();
            if (ActiveStandaloneMedia is not null)
            {
                SetStatus($"Added {audio.Name} to the sound lane; its placement will be mixed into the replacement");
            }
            else
            {
                SetStatus($"Added {audio.Name} to {targetTrack.Name}; load a Music Segment to make it exportable", GuiLogLevel.Warning);
            }

            return clipId;
        }

        if (targetTrack?.ObjectId is not { } objectId)
        {
            SetStatus($"Segment {targetSegmentId} has no authored Wwise track that can provide an export template", GuiLogLevel.Warning);
            return null;
        }

        var template = targetTrack.Clips
            .Where(clip => clip.SourceIdOffset is not null)
            .Select(clip => loadedTimeline.Validation.Clips.FirstOrDefault(item =>
                item.SourceIdOffset == clip.SourceIdOffset))
            .FirstOrDefault(item => item is not null)
            ?? loadedTimeline.Validation.Clips.FirstOrDefault(item => item.TrackObjectId == objectId);
        if (template?.SourceIdOffset is not { } sourceIdOffset || template.FieldOffsets is null)
        {
            var clipId = timeline.AddExternalClip(audio.Name, audio.Path, audio.DurationMs, position);
            ScheduleWaveforms();
            SetStatus($"Track {targetTrack.Name} has no authored audio source to use as a Wwise storage template", GuiLogLevel.Warning);
            return clipId;
        }

        var newMediaId = AllocateReplacementMediaId(template.MediaId, audio.Path);
        scopeImports[newMediaId] = new StructuralImport(
            template.MediaId,
            newMediaId,
            audio.Path,
            audio.DurationMs);
        var addedClipId = timeline.AddExternalClip(
            audio.Name,
            audio.Path,
            audio.DurationMs,
            position,
            template.MediaId,
            sourceIdOffset,
            newMediaId,
            template.FieldOffsets,
            template.PlaylistIndex,
            trackObjectId: objectId,
            segmentObjectId: targetSegmentId,
            trackLengthMs: targetTrack.LengthMs);
        ScheduleWaveforms();
        SetStatus($"Added exportable media {newMediaId} to {targetTrack.Name} in segment {targetSegmentId}");
        return addedClipId;
    }

    private void CopySelectedClip()
    {
        if (timeline.SelectedClipId is not { } clipId)
        {
            SetStatus("Select a clip before copying", GuiLogLevel.Warning);
            return;
        }

        var (_, clip) = document.FindClip(clipId);
        copiedTimelineClip = new CopiedTimelineClip(clip, activeTimelineTab?.Title ?? "timeline");
        SetStatus($"Copied {clip.Name}; switch timelines and paste it onto a selected track");
    }

    private async void PasteClipAsync(object? sender, RoutedEventArgs e)
    {
        if (copiedTimelineClip is not { } copied)
        {
            SetStatus("Copy a timeline clip first", GuiLogLevel.Warning);
            return;
        }

        if (timeline.SelectedTrackId is null)
        {
            SetStatus("Select a target track before pasting", GuiLogLevel.Warning);
            return;
        }

        try
        {
            ImportedAudio audio;
            if (copied.Clip.SourcePath is { } sourcePath && File.Exists(sourcePath))
            {
                audio = await ImportAudioAsync(sourcePath, copied.Clip.Name);
            }
            else
            {
                var aesKey = await GetPreviewAesKeyAsync(required: true);
                if (string.IsNullOrWhiteSpace(aesKey))
                {
                    return;
                }

                using var operation = BeginPreviewOperation($"Preparing copied clip {copied.Clip.Name}", stopCurrentPlayback: false);
                try
                {
                    var wav = await PrepareClipWavAsync(copied.Clip, aesKey, operation.Token);
                    audio = await ImportAudioAsync(wav, copied.Clip.Name);
                }
                finally
                {
                    EndPreviewOperation(operation);
                }
            }

            if (AddImportedToTimeline(audio) is not { } newClipId)
            {
                return;
            }

            var duration = Math.Max(1, copied.Clip.DurationMs);
            document.SetClipArrangement(
                newClipId,
                timeline.PlayheadMs,
                Math.Max(0, copied.Clip.SourceOffsetMs),
                duration,
                copied.Clip.RepeatsSource,
                Math.Min(copied.Clip.FadeInMs, duration),
                Math.Min(copied.Clip.FadeOutMs, duration));
            timeline.SelectClip(newClipId);
            ScheduleWaveforms();
            SetStatus($"Pasted {copied.Clip.Name} from {copied.SourceTimeline} at {FormatMs(timeline.PlayheadMs)}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Clip paste cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Clip paste failed", exception);
        }
    }

    private void AssignStructuralImport(uint newMediaId, ImportedAudio audio)
    {
        if (!scopeImports.TryGetValue(newMediaId, out var existing))
        {
            throw new KeyNotFoundException($"Imported media {newMediaId} is not assigned to this composition.");
        }

        scopeImports[newMediaId] = existing with
        {
            Path = audio.Path,
            PhysicalDurationMs = audio.DurationMs
        };
        var count = document.ReplaceImportedMedia(newMediaId, audio.Path, audio.DurationMs);
        ScheduleWaveforms();
        SetStatus($"Updated imported media {newMediaId} in {count} playlist placement{(count == 1 ? string.Empty : "s")}");
    }

    private void AssignImportedReplacement(uint mediaId, ImportedAudio audio)
    {
        var replacement = new ScopeReplacement(
            scopeReplacements.GetValueOrDefault(mediaId)?.NewMediaId
                ?? AllocateReplacementMediaId(mediaId, audio.Path),
            audio.Path,
            audio.DurationMs);
        scopeReplacements[mediaId] = replacement;
        var count = document.ReplaceMediaReferences(
            mediaId,
            audio.Path,
            replacement.NewMediaId,
            audio.DurationMs);
        ScheduleWaveforms();
        ShowTimelineSelection();
        SetStatus($"New media {replacement.NewMediaId} ({FormatMs(audio.DurationMs)}) replaces {mediaId} in {count} visible composition placement{(count == 1 ? string.Empty : "s")}");
    }

    private void AssignStandaloneReplacement(uint mediaId, ImportedAudio audio)
    {
        var count = document.ReplaceMediaReferences(mediaId, audio.Path, mediaId, audio.DurationMs);
        ScheduleWaveforms();
        ShowTimelineSelection();
        SetStatus($"Replaced {count} sound block{(count == 1 ? string.Empty : "s")} with {audio.Name}; the edited lane will be rendered into media {mediaId}");
    }

    private async void PlayImportedAudioAsync(object? sender, RoutedEventArgs e)
    {
        if (importedAudioList.SelectedItem is not ImportedAudio item)
        {
            SetStatus("Select an imported clip first", GuiLogLevel.Warning);
            return;
        }

        using var operation = BeginPreviewOperation($"Preparing {item.Name}");
        try
        {
            var wav = await PrepareExternalWavAsync(item.Path, operation.Token, projectAudio: true);
            previewPlayer.Play(wav);
            StartTransport(followTimeline: false);
            SetStatus($"Playing imported clip: {item.Name}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Audio preview cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Imported clip preview failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private async void PreviewBrowserMediaAsync(object? sender, TappedEventArgs e)
    {
        if ((results.SelectedItem as BrowserNode)?.Media is not null)
        {
            await PreviewSelectedGameMediaCoreAsync();
        }
    }

    private async void PreviewSelectedGameMediaAsync(object? sender, RoutedEventArgs e) =>
        await PreviewSelectedGameMediaCoreAsync();

    private async Task PreviewSelectedGameMediaCoreAsync()
    {
        if ((results.SelectedItem as BrowserNode)?.Media is not { } media)
        {
            SetStatus("Select a Sound or Media node first", GuiLogLevel.Warning);
            return;
        }

        await PreviewMediaAsync(media);
    }

    private async Task PreviewMediaAsync(MediaRecord media)
    {

        if (!media.IsPlayableAudio)
        {
            SetStatus($"{media.SourceName} is Wwise MIDI timing/control data, not independently playable audio", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(required: true);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Preparing {media.SourceName}");
        try
        {
            var wav = await PrepareMediaWavAsync(media.Id, aesKey, operation.Token);
            previewPlayer.Play(wav);
            StartTransport(followTimeline: false);
            SetStatus($"Playing {media.SourceName} ({media.Id})");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Media preview cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Media preview failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private async void PlaySelectedClipAsync(object? sender, RoutedEventArgs e)
    {
        if (timeline.SelectedClipId is not { } clipId)
        {
            SetStatus("Select a timeline clip first", GuiLogLevel.Warning);
            return;
        }

        var (_, clip) = document.FindClip(clipId);
        if (!IsPreviewableClip(clip))
        {
            SetStatus($"{clip.Name} is Wwise MIDI/control data; it remains visible for timing but has no audio to play", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(clip.SourcePath is null);
        if (clip.SourcePath is null && string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Preparing {clip.Name}");
        try
        {
            var source = await PrepareClipWavAsync(clip, aesKey, operation.Token);
            var wav = Path.Combine(PreviewDirectory(), "clip-preview.wav");
            await Task.Run(() => TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(
                    source,
                    0,
                    clip.SourceOffsetMs,
                    clip.DurationMs,
                    clip.RepeatsSource,
                    clip.FadeInMs,
                    clip.FadeOutMs)],
                wav,
                operation.Token), operation.Token);
            var clipEnd = clip.StartMs + clip.DurationMs;
            var start = timeline.PlayheadMs >= clip.StartMs && timeline.PlayheadMs < clipEnd
                ? timeline.PlayheadMs
                : clip.StartMs;
            previewPlayer.Play(
                wav,
                0,
                clip.DurationMs,
                clip.StartMs,
                start);
            timeline.SetPlaybackPosition(start);
            StartTransport(followTimeline: true);
            SetStatus($"Playing clip {clip.Name} from {FormatMs(start)} for {FormatMs(clipEnd - start)}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Audio preview cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Clip preview failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private static double SegmentBpm(
        IReadOnlyDictionary<uint, double> segmentBpms,
        LoadedEventTimeline composition,
        uint? segmentId) => segmentId is { } id && segmentBpms.TryGetValue(id, out var bpm)
            ? bpm
            : AuthoredBpmForSegment(composition, segmentId);

    private static double AuthoredBpmForSegment(LoadedEventTimeline composition, uint? segmentId) =>
        segmentId is { } id
            ? composition.AllTimingScopes.FirstOrDefault(scope =>
                scope.Validation.Segments.Any(segment => segment.ObjectId == id))?.AuthoredBpm
                ?? composition.AuthoredBpm
            : composition.AuthoredBpm;

    private async void CalculateSelectedBpmAsync(object? sender, RoutedEventArgs e)
    {
        if (timeline.SelectedClipId is not { } clipId)
        {
            SetStatus("Select a playable timeline clip first", GuiLogLevel.Warning);
            return;
        }

        var (track, clip) = document.FindClip(clipId);
        if (!IsPreviewableClip(clip))
        {
            SetStatus($"{clip.Name} is Wwise MIDI/control data and has no audio BPM", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(clip.SourcePath is null);
        if (clip.SourcePath is null && string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation($"Preparing {clip.Name} for BPM calculation");
        try
        {
            var source = await PrepareClipWavAsync(clip, aesKey, operation.Token);
            var rendered = Path.Combine(PreviewDirectory(), $"bpm-clip-{clip.Id:N}.wav");
            await Task.Run(() => TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(
                    source,
                    0,
                    clip.SourceOffsetMs,
                    clip.DurationMs,
                    clip.RepeatsSource,
                    clip.FadeInMs,
                    clip.FadeOutMs)],
                rendered,
                operation.Token), operation.Token);
            var envelope = await AnalyzeWaveformAsync(rendered, operation.Token);
            EndPreviewOperation(operation);

            var segmentId = loadedTimeline is not null ? track.SegmentObjectId : null;
            var initialBpm = document.SegmentBpm(segmentId);
            var detected = await new BpmDetectionDialog(
                clip.Name,
                rendered,
                envelope,
                initialBpm,
                segmentId is not null).ShowDialog<BpmDetectionResult?>(this);
            if (detected is { } result && segmentId is { } id)
            {
                if (result.LeadingGapMs > 0.001)
                {
                    if (clip.MediaId is not { } templateMediaId)
                    {
                        throw new InvalidOperationException(
                            "The selected clip has no Wwise media template for the aligned audio.");
                    }

                    SetStatus($"Baking {FormatMs(result.LeadingGapMs)} of leading silence into {clip.Name}");
                    var aligned = await RenderAlignedAudioAsync(
                        clip,
                        rendered,
                        envelope.DurationMs,
                        result.LeadingGapMs,
                        operation.Token);
                    var duration = envelope.DurationMs + result.LeadingGapMs;
                    var newMediaId = AllocateReplacementMediaId(templateMediaId, aligned);
                    var previousImportId = clip.ReplacementMediaId is { } replacementId
                        && scopeImports.ContainsKey(replacementId)
                            ? replacementId
                            : (uint?)null;
                    scopeImports[newMediaId] = new StructuralImport(
                        templateMediaId,
                        newMediaId,
                        aligned,
                        duration);
                    try
                    {
                        ApplySegmentBpm(id, result.Bpm);
                        document.SetClipRenderedSource(clipId, aligned, newMediaId, duration);
                    }
                    catch
                    {
                        scopeImports.Remove(newMediaId);
                        throw;
                    }

                    if (previousImportId is { } oldId
                        && document.Tracks.SelectMany(track => track.Clips)
                            .All(item => item.ReplacementMediaId != oldId))
                    {
                        scopeImports.Remove(oldId);
                    }

                    timeline.SelectClip(clipId);
                    ScheduleWaveforms();
                    ShowTimelineSelection();
                    SetStatus($"Applied {result.Bpm:0.###} BPM; {FormatMs(result.LeadingGapMs)} of silence is now part of {clip.Name} and will be preserved when copied");
                }
                else
                {
                    ApplySegmentBpm(id, result.Bpm);
                }
            }
            else
            {
                SetStatus($"BPM calculation closed for {clip.Name}");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("BPM calculation cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("BPM calculation failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private async Task<string> RenderAlignedAudioAsync(
        MusicTimelineClip clip,
        string renderedSource,
        double sourceDurationMs,
        double leadingSilenceMs,
        CancellationToken cancellationToken)
    {
        var directory = ProjectAudioDirectory("Generated");
        var name = SafeFileName(clip.Name);
        var output = Path.Combine(
            directory,
            $"{(string.IsNullOrWhiteSpace(name) ? "audio" : name)}-{clip.Id:N}-{Guid.NewGuid():N}.wav");
        await Task.Run(() => TimelineAudioRenderer.Render(
            [new TimelineAudioPlacement(renderedSource, leadingSilenceMs, 0, sourceDurationMs)],
            output,
            cancellationToken), cancellationToken);
        return output;
    }

    private async Task CalculateCatalogBpmAsync(ClipCatalogItem item)
    {
        SelectCatalogItem(item);
        string? aesKey = null;
        if (item.Media is not null)
        {
            aesKey = await GetPreviewAesKeyAsync(required: true);
            if (string.IsNullOrWhiteSpace(aesKey))
            {
                return;
            }
        }

        using var operation = BeginPreviewOperation($"Preparing {item.Name} for BPM calculation");
        try
        {
            var wav = item.Imported is { } imported
                ? await PrepareExternalWavAsync(imported.Path, operation.Token, projectAudio: true)
                : item.Media is { } media
                    ? await PrepareMediaWavAsync(media.Id, aesKey, operation.Token)
                    : throw new InvalidOperationException("The selected catalog item has no audio source.");
            var envelope = await AnalyzeWaveformAsync(wav, operation.Token);
            EndPreviewOperation(operation);

            var initialBpm = document.SegmentBpm(timeline.AuditionSegmentId);
            await new BpmDetectionDialog(item.Name, wav, envelope, initialBpm, canApply: false)
                .ShowDialog<BpmDetectionResult?>(this);
            SetStatus($"BPM calculation closed for {item.Name}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("BPM calculation cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("BPM calculation failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private async void PlayTimelineAsync(object? sender, RoutedEventArgs e)
    {
        if (timelineContentLoading)
        {
            playWhenTimelineReady = true;
            SetStatus("Timeline audio is still loading; playback will start when it is ready");
            return;
        }

        var refreshStart = mixRefreshStartMs;
        var pauseAfterRefresh = pauseAfterMixRefresh;
        mixRefreshStartMs = null;
        pauseAfterMixRefresh = false;
        var tracks = timeline.AuditionSegmentId is { } segmentId
            ? document.Tracks.Where(track => track.SegmentObjectId == segmentId).ToArray()
            : timeline.VisibleTracks.ToArray();
        if (tracks.Length == 0)
        {
            SetStatus("Open a composition or create a timeline first", GuiLogLevel.Warning);
            return;
        }

        var clips = tracks.SelectMany(track => track.Clips).ToArray();
        var playableClips = clips.Where(IsPreviewableClip).ToArray();
        if (playableClips.Length == 0)
        {
            SetStatus("The selected segment contains only Wwise MIDI/control data and has no independently playable audio clips", GuiLogLevel.Warning);
            return;
        }

        var aesKey = await GetPreviewAesKeyAsync(playableClips.Any(clip => clip.SourcePath is null));
        if (playableClips.Any(clip => clip.SourcePath is null) && string.IsNullOrWhiteSpace(aesKey))
        {
            return;
        }

        using var operation = BeginPreviewOperation(
            loadedTimeline is null ? "Preparing timeline preview" : $"Preparing segment {timeline.AuditionSegmentId} preview",
            stopCurrentPlayback: false);
        try
        {
            var previewTracks = new List<TimelinePreviewTrack>();
            foreach (var track in tracks.Where(track => track.Clips.Length > 0))
            {
                var placements = new List<TimelineAudioPlacement>();
                foreach (var clip in track.Clips.Where(IsPreviewableClip))
                {
                    placements.Add(new TimelineAudioPlacement(
                        await PrepareClipWavAsync(clip, aesKey, operation.Token),
                        clip.StartMs,
                        clip.SourceOffsetMs,
                        clip.DurationMs,
                        clip.RepeatsSource,
                        clip.FadeInMs,
                        clip.FadeOutMs));
                }

                if (placements.Count == 0)
                {
                    continue;
                }

                previewTracks.Add(new TimelinePreviewTrack(
                    track.Id,
                    placements.ToArray(),
                    track.Gain,
                    track.IsMuted,
                    track.IsSolo));
            }

            if (previewTracks.Count == 0)
            {
                throw new InvalidOperationException("No decodable audio placements remain in the selected segment.");
            }

            var duration = tracks.SelectMany(track => track.Clips)
                .Select(clip => clip.StartMs + clip.DurationMs)
                .Concat(tracks.Select(track => track.LengthMs ?? 0))
                .DefaultIfEmpty(1)
                .Max();
            if (settings.MetronomeEnabled
                && timeline.AuditionSegmentId is { } metronomeSegment)
            {
                previewTracks.Add(CreateMetronomeTrack(metronomeSegment, duration) with
                {
                    IsSolo = previewTracks.Any(track => track.IsSolo)
                });
            }

            var selectionStart = timeline.SelectionStartMs ?? 0;
            var selectionEnd = timeline.SelectionEndMs ?? selectionStart;
            var hasSelection = refreshStart is null && selectionEnd - selectionStart > 1;
            var requestedStart = refreshStart ?? timeline.PlayheadMs;
            var start = hasSelection
                ? Math.Clamp(selectionStart, 0, duration)
                : requestedStart < duration ? requestedStart : 0;
            var rangeStart = hasSelection ? start : 0;
            var end = hasSelection ? Math.Clamp(selectionEnd, start, duration) : duration;
            previewPlayer.PlayTimeline(previewTracks, duration, rangeStart, end, start);
            previewSegmentId = timeline.AuditionSegmentId;
            timeline.SetPlaybackPosition(start);
            StartTransport(followTimeline: true);
            if (pauseAfterRefresh)
            {
                previewPlayer.TogglePause();
                UpdateTimelineControlAvailability();
            }
            var skipped = clips.Length - playableClips.Length;
            SetStatus(loadedTimeline is null
                ? $"Playing timeline preview from {FormatMs(start)}: {playableClips.Length} audio clips, {FormatMs(duration)} total"
                : $"Playing segment {timeline.AuditionSegmentId} from {FormatMs(start)}: {AudibleTrackCount(tracks)} audible tracks, {playableClips.Length} audio clips"
                    + (skipped == 0 ? string.Empty : $", {skipped} MIDI/control clip{(skipped == 1 ? string.Empty : "s")} skipped"));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Timeline preview cancelled");
        }
        catch (Exception exception)
        {
            SetFailure("Timeline preview failed", exception);
        }
        finally
        {
            EndPreviewOperation(operation);
        }
    }

    private CancellationTokenSource BeginPreviewOperation(string statusMessage, bool stopCurrentPlayback = true)
    {
        waveformOperation?.Cancel();
        previewOperation?.Cancel();
        previewOperation?.Dispose();
        if (stopCurrentPlayback)
        {
            transportTimer.Stop();
            previewPlayer.Stop();
            followPlaybackTimeline = false;
            previewSegmentId = null;
            UpdateTimelineControlAvailability();
        }

        previewOperation = new CancellationTokenSource();
        SetStatus(statusMessage);
        return previewOperation;
    }

    private void ResetTransportForTimelineNavigation()
    {
        previewOperation?.Cancel();
        transportTimer.Stop();
        previewPlayer.Stop();
        followPlaybackTimeline = false;
        previewSegmentId = null;
        mixRefreshStartMs = null;
        pauseAfterMixRefresh = false;
        playWhenTimelineReady = false;
        timeline.SetPlaybackPosition(0);
        transportTime.Text = FormatMs(0);
        UpdateTimelineControlAvailability();
    }

    private void EndPreviewOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(previewOperation, operation))
        {
            previewOperation = null;
        }
    }

    private void StopAudioPreview()
    {
        previewOperation?.Cancel();
        transportTimer.Stop();
        previewPlayer.Stop();
        followPlaybackTimeline = false;
        previewSegmentId = null;
        timeline.SetPlaybackPosition(0);
        transportTime.Text = FormatMs(0);
        UpdateTimelineControlAvailability();
        SetStatus("Audio preview stopped");
        ScheduleWaveforms();
    }

    private void StartTransport(bool followTimeline)
    {
        followPlaybackTimeline = followTimeline;
        transportTimer.Start();
        UpdateTransportPosition();
        UpdateTimelineControlAvailability();
    }

    private void ToggleAudioPause()
    {
        if (previewPlayer.State == AudioPreviewState.Stopped)
        {
            PlayTimelineAsync(null, new RoutedEventArgs());
            return;
        }

        previewPlayer.TogglePause();
        UpdateTransportPosition();
        UpdateTimelineControlAvailability();
        SetStatus(previewPlayer.State == AudioPreviewState.Paused ? "Audio preview paused" : "Audio preview resumed");
    }

    private void ToggleTimelinePlayStop()
    {
        if (timelineContentLoading)
        {
            playWhenTimelineReady = !playWhenTimelineReady;
            SetStatus(playWhenTimelineReady
                ? "Timeline audio is loading; playback is queued"
                : "Queued timeline playback cancelled");
            return;
        }

        if (previewPlayer.State == AudioPreviewState.Stopped)
        {
            PlayTimelineAsync(null, new RoutedEventArgs());
        }
        else
        {
            StopAudioPreview();
        }
    }

    private void RefreshPlayingTimelineMix()
    {
        if (!followPlaybackTimeline || !previewPlayer.HasSource)
        {
            return;
        }

        var tracks = timeline.AuditionSegmentId is { } segmentId
            ? document.Tracks.Where(track => track.SegmentObjectId == segmentId
                && track.Clips.Any(IsPreviewableClip)).ToArray()
            : timeline.VisibleTracks.Where(track => track.Clips.Any(IsPreviewableClip)).ToArray();
        var states = tracks.Select(track => new TimelinePreviewTrackState(
            track.Id,
            track.Gain,
            track.IsMuted,
            track.IsSolo)).ToList();
        if (settings.MetronomeEnabled
            && timeline.AuditionSegmentId is { } metronomeSegment)
        {
            states.Add(new TimelinePreviewTrackState(
                MetronomeTrackId,
                0.5,
                false,
                tracks.Any(track => track.IsSolo)));
        }

        if (previewSegmentId == timeline.AuditionSegmentId && previewPlayer.UpdateTrackMix(states))
        {
            return;
        }

        var requestedStart = timeline.PlayheadMs;
        pauseAfterMixRefresh = previewPlayer.State == AudioPreviewState.Paused;
        transportTimer.Stop();
        previewPlayer.Stop();
        followPlaybackTimeline = false;
        previewSegmentId = null;
        timeline.SetPlaybackPosition(requestedStart);
        transportTime.Text = FormatMs(requestedStart);
        mixRefreshStartMs = requestedStart;
        UpdateTimelineControlAvailability();
        PlayTimelineAsync(null, new RoutedEventArgs());
    }

    private static int AudibleTrackCount(IReadOnlyCollection<MusicTimelineTrack> tracks)
    {
        var anySolo = tracks.Any(track => track.IsSolo);
        return tracks.Count(track => anySolo ? track.IsSolo : !track.IsMuted);
    }

    private TimelinePreviewTrack CreateMetronomeTrack(uint segmentId, double durationMs)
    {
        var regular = MetronomeClickPath(accent: false);
        var accent = MetronomeClickPath(accent: true);
        var beatMs = document.BeatMillisecondsFor(segmentId);
        var placements = new List<TimelineAudioPlacement>();
        for (var beat = 0; beat * beatMs < durationMs; beat++)
        {
            placements.Add(new TimelineAudioPlacement(
                beat % document.BeatsPerBar == 0 ? accent : regular,
                beat * beatMs,
                0,
                55,
                false));
        }

        return new TimelinePreviewTrack(MetronomeTrackId, placements.ToArray(), 0.5, false, false);
    }

    private static string MetronomeClickPath(bool accent)
    {
        var path = Path.Combine(PreviewDirectory(), accent ? "metronome-accent.wav" : "metronome-beat.wav");
        if (File.Exists(path))
        {
            return path;
        }

        const int sampleRate = 48_000;
        const double durationSeconds = 0.055;
        var frequency = accent ? 1_760d : 1_180d;
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        for (var sample = 0; sample < sampleRate * durationSeconds; sample++)
        {
            var time = sample / (double)sampleRate;
            var envelope = Math.Exp(-time * 75);
            writer.WriteSample((float)(Math.Sin(2 * Math.PI * frequency * time) * envelope * 0.65));
        }

        return path;
    }

    private void SeekAudioPreview(double positionMs)
    {
        if (!previewPlayer.HasSource
            || followPlaybackTimeline && previewSegmentId != timeline.AuditionSegmentId)
        {
            return;
        }

        previewPlayer.Seek(positionMs);
        UpdateTransportPosition();
        UpdateTimelineControlAvailability();
        SetStatus($"Preview moved to {FormatMs(previewPlayer.PositionMs)}");
    }

    private void UpdateTransportPosition()
    {
        var position = previewPlayer.PositionMs;
        transportTime.Text = FormatMs(position);
        if (followPlaybackTimeline && previewPlayer.HasSource)
        {
            timeline.SetPlaybackPosition(position, previewPlayer.State == AudioPreviewState.Playing);
        }
    }

    private void PlaybackEnded()
    {
        transportTimer.Stop();
        previewPlayer.Stop();
        followPlaybackTimeline = false;
        previewSegmentId = null;
        timeline.SetPlaybackPosition(0);
        transportTime.Text = FormatMs(0);
        UpdateTimelineControlAvailability();
        SetStatus("Audio preview finished");
        ScheduleWaveforms();
    }

    private async Task<string?> GetPreviewAesKeyAsync(bool required)
    {
        if (!required)
        {
            return null;
        }

        var aesKey = CurrentAesKey();
        if (!string.IsNullOrWhiteSpace(aesKey))
        {
            return aesKey;
        }

        aesKey = await new PasswordPromptDialog(
            "Previewing original game audio requires extracting it from the encrypted game PAK.")
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            SetStatus("Audio preview cancelled");
            return null;
        }

        settings.AesKey = aesKey;
        return aesKey;
    }

    private void ScheduleWaveforms(bool clear = false)
    {
        waveformOperation?.Cancel();
        waveformOperation = new CancellationTokenSource();
        if (clear)
        {
            timeline.ClearWaveforms();
        }

        var clips = timeline.VisibleTracks
            .SelectMany(track => track.Clips)
            .Where(IsPreviewableClip)
            .ToArray();
        if (clips.Length == 0)
        {
            waveformOperation.Dispose();
            waveformOperation = null;
            SetTimelineContentLoading(false);
            return;
        }

        SetTimelineContentLoading(true, $"Loading {clips.Length:N0} timeline clip{(clips.Length == 1 ? string.Empty : "s")}...");
        _ = LoadWaveformsAsync(clips, CurrentAesKey(), waveformOperation);
    }

    private async Task LoadWaveformsAsync(
        IReadOnlyCollection<MusicTimelineClip> clips,
        string? aesKey,
        CancellationTokenSource operation)
    {
        try
        {
            foreach (var group in clips.GroupBy(AudioSourceIdentity, StringComparer.OrdinalIgnoreCase))
            {
                operation.Token.ThrowIfCancellationRequested();
                var clip = group.First();
                if (clip.SourcePath is null && string.IsNullOrWhiteSpace(aesKey))
                {
                    continue;
                }

                try
                {
                    var wav = await PrepareClipWavAsync(clip, aesKey, operation.Token);
                    var waveform = await AnalyzeWaveformAsync(wav, operation.Token);

                    foreach (var current in document.Tracks.SelectMany(track => track.Clips)
                                 .Where(item => AudioSourceIdentity(item).Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        timeline.SetWaveform(current, waveform);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    log.Write(GuiLogLevel.Warning, $"Waveform unavailable for {clip.Name}: {exception}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(waveformOperation, operation))
            {
                waveformOperation = null;
                SetTimelineContentLoading(false);
                if (playWhenTimelineReady)
                {
                    playWhenTimelineReady = false;
                    PlayTimelineAsync(null, new RoutedEventArgs());
                }
            }

            operation.Dispose();
        }
    }

    private async Task<WaveformEnvelope> AnalyzeWaveformAsync(string wavPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(wavPath);
        var cacheKey = $"{fullPath}|{File.GetLastWriteTimeUtc(fullPath).Ticks}";
        if (waveformCache.TryGetValue(cacheKey, out var waveform))
        {
            return waveform;
        }

        waveform = await Task.Run(
            () => WaveformAnalyzer.Analyze(fullPath, cancellationToken: cancellationToken),
            cancellationToken);
        waveformCache[cacheKey] = waveform;
        return waveform;
    }

    private void SetTimelineContentLoading(bool loading, string? message = null)
    {
        timelineContentLoading = loading;
        timelineLoadingOverlay.IsVisible = loading;
        if (!string.IsNullOrWhiteSpace(message))
        {
            timelineLoadingMessage.Text = message;
        }

        UpdateTimelineControlAvailability();
    }

    private static string AudioSourceIdentity(MusicTimelineClip clip) => clip.SourcePath is { } path
        ? $"file:{Path.GetFullPath(path)}"
        : $"media:{clip.MediaId?.ToString(CultureInfo.InvariantCulture) ?? "none"}";

    private async Task<string> PrepareClipWavAsync(
        MusicTimelineClip clip,
        string? aesKey,
        CancellationToken cancellationToken)
    {
        if (clip.SourcePath is { } sourcePath)
        {
            return await PrepareExternalWavAsync(sourcePath, cancellationToken, projectAudio: true);
        }

        if (clip.MediaId is not { } mediaId || index is null)
        {
            throw new InvalidOperationException("The clip has no playable audio source.");
        }

        return await PrepareMediaWavAsync(mediaId, aesKey, cancellationToken);
    }

    private bool IsPreviewableClip(MusicTimelineClip clip) => clip.SourcePath is not null
        || clip.MediaId is { } mediaId
        && index?.FindMedia(mediaId).Any(media => media.IsPlayableAudio) == true;

    private async Task<string> PrepareMediaWavAsync(
        uint mediaId,
        string? aesKey,
        CancellationToken cancellationToken)
    {
        if (index is null)
        {
            throw new InvalidOperationException("No index is loaded.");
        }

        var directory = PreviewDirectory();
        var wem = Path.Combine(directory, $"media-{mediaId}.wem");
        var cached = File.Exists(wem);
        if (!cached)
        {
            await MediaExtractor.ExtractAsync(
                index,
                mediaId,
                wem,
                settings.RepakPath,
                aesKey,
                cancellationToken);
        }

        try
        {
            return await PrepareExternalWavAsync(wem, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cached)
        {
            File.Delete(wem);
            await MediaExtractor.ExtractAsync(
                index,
                mediaId,
                wem,
                settings.RepakPath,
                aesKey,
                cancellationToken);
            return await PrepareExternalWavAsync(wem, cancellationToken);
        }
    }

    private async Task<string> PrepareExternalWavAsync(
        string sourcePath,
        CancellationToken cancellationToken,
        bool projectAudio = false)
    {
        var source = Path.GetFullPath(sourcePath);
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var stamp = File.GetLastWriteTimeUtc(source).Ticks;
        var id = WwiseHash.Fnv1($"{source}|{stamp}");
        var name = SafeFileName(Path.GetFileNameWithoutExtension(source));
        var output = Path.Combine(
            projectAudio ? ProjectAudioDirectory("Converted") : PreviewDirectory(),
            $"{(string.IsNullOrWhiteSpace(name) ? "audio" : name)}-{id}.wav");

        if (!File.Exists(output))
        {
            await VgmstreamClient.DecodeAsync(source, output, settings.VgmstreamPath, cancellationToken);
        }

        return output;
    }

    private string ProjectAudioDirectory(string category)
    {
        var root = currentProjectPath is { Length: > 0 } projectPath
            ? Path.Combine(
                Path.GetDirectoryName(projectPath)!,
                $"{Path.GetFileNameWithoutExtension(projectPath)}_audio")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HbkWwise",
                "unsaved-project-audio");

        var directory = Path.Combine(root, category);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string PreviewDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HbkWwise",
            "preview");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void AddTrack()
    {
        if (timeline.SelectedTrackId is null)
        {
            SetStatus("Select a track before adding a new track below it", GuiLogLevel.Warning);
            return;
        }

        timeline.AddTrackBelowSelected();
    }

    private void PlaceSegmentBpmEditor(uint? segmentId, Rect? bounds)
    {
        if (segmentId is null || bounds is null)
        {
            bpmEditorSegmentId = null;
            bpmInput.IsVisible = false;
            return;
        }

        bpmEditorSegmentId = segmentId;
        bpmInput.Margin = new Thickness(bounds.Value.Left, bounds.Value.Top, 0, 0);
        bpmInput.Width = bounds.Value.Width;
        bpmInput.Height = bounds.Value.Height;
        bpmInput.IsVisible = true;
        if (!bpmInput.IsFocused)
        {
            bpmInput.Text = document.SegmentBpm(segmentId).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private void ApplyBpmInput()
    {
        if (bpmEditorSegmentId is not { } segmentId)
        {
            return;
        }

        var text = bpmInput.Text ?? string.Empty;
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        if (!parsed || value is < 20 or > 400)
        {
            bpmInput.Text = document.SegmentBpm(segmentId)
                .ToString("0.##", CultureInfo.InvariantCulture);
            SetStatus("BPM must be between 20 and 400");
            return;
        }

        ApplySegmentBpm(segmentId, value);
    }

    private void ApplySegmentBpm(uint segmentId, double value)
    {
        document.SetSegmentBpmAndScale(segmentId, value);
        if (loadedTimeline is not null && segmentId == loadedTimeline.Segment.ObjectId)
        {
            loadedTimeline = loadedTimeline with { PreviewBpm = value };
        }

        bpmInput.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
        SetStatus($"Music Segment {segmentId} BPM: {value:0.##}; other segments were unchanged");
    }

    private void OnAuditionSegmentChanged()
    {
        UpdateSelectedSegmentTempoUi();
        RefreshPlayingTimelineMix();
    }

    private void UpdateSelectedSegmentTempoUi()
    {
        if (loadedTimeline is null)
        {
            bpmInput.IsVisible = false;
            return;
        }

        var segmentId = timeline.AuditionSegmentId ?? loadedTimeline.Segment.ObjectId;
        bpmEditorSegmentId = segmentId;
        bpmInput.Text = document.SegmentBpm(segmentId).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EventDetails(IReadOnlyDictionary<uint, string> mediaNames, EventRecord item)
    {
        var media = item.Media.Select(reference =>
        {
            mediaNames.TryGetValue(reference.Id, out var name);
            return $"{reference.Id}\t{name ?? "unknown"}";
        });
        return $"EVENT TRIGGER\n\nName: {item.Name}\nID: {item.Id}\nBank: {item.Bank}\nPath: {item.ObjectPath}\n"
            + $"Duration: {item.DurationType}\n\nAn Event runs Actions; it is not itself a timeline. "
            + "Selecting it automatically opens its Action tree and loads all Music Segments in its active timing scope."
            + $"\n\nREFERENCED MEDIA\n{string.Join('\n', media)}";
    }

    private static string MediaDetails(MediaRecord item)
    {
        var uses = item.Uses.Length == 0
            ? "  no generated Event use"
            : string.Join('\n', item.Uses.Select(use => $"  {use.EventName}\n    {string.Join("\n    ", use.StatePaths)}"));
        var kind = item.IsWwiseMidi ? "WWISE MIDI / CONTROL DATA" : "MEDIA / AUDIO PAYLOAD";
        var playback = item.IsWwiseMidi
            ? "This item stores MIDI timing/control data and is not independently audible. It remains visible because it affects composition timing."
            : "Double-click this Media node, or use Timeline > Preview selected Sound / media, to play it independently.";
        return $"{kind}\n\nID: {item.Id}\nSource: {item.SourceName}\nBank: {item.Bank}\nStorage: {item.Storage}\n"
            + $"Language: {item.Language}\n\n{playback}\n\nSelecting Media only inspects it; it is never inserted automatically. "
            + "Use its Event/segment context for structural editing. The Timeline menu's manual-insert command is an explicit manual-arrangement action."
            + $"\n\nUSED BY\n{uses}";
    }

    private static string BankDetails(WwiseIndex index, BankRecord item)
    {
        var media = index.Media.Count(candidate => candidate.Bank.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        var events = index.Events.Count(candidate => candidate.Bank.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        var assets = item.Assets is { Length: > 0 }
            ? string.Join('\n', item.Assets.OrderBy(asset => asset.Priority)
                .Select(asset => $"  {(asset.IsEffective ? "effective" : "overridden")}  {PakLabel(asset)}\n    {asset.EntryPath}"))
            : "  no indexed PAK asset";
        return $"SOUNDBANK CONTAINER\n\nName: {item.Name}\nID: {item.Id}\nLanguage: {item.Language}\nPath: {item.Path}\n"
            + $"Events: {events}\nMedia: {media}\n\nA bank is a storage container, not one editable timeline. "
            + "Selecting it opens its primary playable Event; choose another Event below it when the bank contains multiple independent compositions."
            + $"\n\nPAK ASSETS\n{assets}";
    }

    private void SetBusy(bool busy, string? message = null)
    {
        operationBusy = busy;
        progress.IsVisible = busy;
        cancelOperation.IsVisible = busy;
        UpdateTimelineControlAvailability();
        if (message is not null)
        {
            SetStatus(message);
        }
    }

    private void ShowInspector(string value)
    {
        details.Children.Clear();
        var lines = value.Replace("\r", string.Empty).Split('\n');
        var firstIndex = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var first = firstIndex < 0 ? "INSPECTOR" : lines[firstIndex];
        details.Children.Add(new SelectableTextBlock
        {
            Text = first.Trim(),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brushes.LightBlue,
            Margin = new Thickness(0, 0, 0, 4)
        });
        foreach (var line in lines.Skip(firstIndex < 0 ? lines.Length : firstIndex + 1))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var tab = text.IndexOf('\t');
            var colon = text.IndexOf(':');
            if (tab > 0)
            {
                AddInspectorRow(text[..tab], text[(tab + 1)..].Trim());
            }
            else if (colon > 0 && colon < 32)
            {
                AddInspectorRow(text[..colon], text[(colon + 1)..].Trim());
            }
            else if (text.All(character => !char.IsLetter(character) || char.IsUpper(character)) && text.Length < 40)
            {
                details.Children.Add(new SelectableTextBlock
                {
                    Text = text,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 7, 0, 1)
                });
            }
            else
            {
                AddInspectorRow(string.Empty, text);
            }
        }
    }

    private void AddInspectorRow(string key, string value)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("108,*"),
            Background = details.Children.Count % 2 == 0 ? ColorBrush("#151A20") : Brushes.Transparent
        };
        row.Children.Add(new SelectableTextBlock
        {
            Text = key,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4, 3),
            TextWrapping = TextWrapping.Wrap
        });
        var content = new SelectableTextBlock
        {
            Text = value,
            Margin = new Thickness(4, 3),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        details.Children.Add(row);
    }

    private void SetStatus(string value) => SetStatus(value, GuiLogLevel.Info);

    private void SetStatus(string value, GuiLogLevel level)
    {
        log.Write(level, value);
        status.Text = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        status.Foreground = level switch
        {
            GuiLogLevel.Error => Brushes.IndianRed,
            GuiLogLevel.Warning => Brushes.Orange,
            _ => Brushes.LightGray
        };
        ToolTip.SetTip(status, value);
    }

    private void SetFailure(string operation, Exception exception)
    {
        var value = $"{operation}: {FriendlyFailure(exception)}";
        log.Write(GuiLogLevel.Error, exception.ToString());
        log.Write(GuiLogLevel.Error, value);
        status.Text = value;
        status.Foreground = Brushes.IndianRed;
        ToolTip.SetTip(status, value);
    }

    private static string FriendlyFailure(Exception exception)
    {
        var message = exception.Message.Replace("\r", string.Empty);
        if (message.Contains("panicked at", StringComparison.OrdinalIgnoreCase)
            || message.Contains("index out of bounds", StringComparison.OrdinalIgnoreCase))
        {
            return "repak could not read the selected archive entry. Verify the AES key; the alternate extraction path also failed. See View > Log for details.";
        }

        return message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? exception.GetType().Name;
    }

    private string? CurrentAesKey() =>
        Environment.GetEnvironmentVariable("HBKWWISE_AES_KEY")
        ?? settings.AesKey;

    private static string MediaType(MediaRecord media) => media.IsWwiseMidi
        ? "MIDI"
        : media.IsEmbedded
        ? "EMBEDDED"
        : media.PrefetchSize is > 0 ? "PREFETCH" : "STREAMED";

    private static string TimelineStorage(MediaRecord media) => media.IsEmbedded
        ? "embedded in bank"
        : media.PrefetchSize is > 0 ? "streamed with one in-bank prefetch prefix" : "streamed externally";

    private static string PakLabel(PakAsset? asset) => asset is null
        ? "unknown"
        : Path.GetFileNameWithoutExtension(asset.PakPath)
            .Replace("Hibiki-WindowsNoEditor_0_P", "update", StringComparison.OrdinalIgnoreCase)
            .Replace("Hibiki-WindowsNoEditor", "base", StringComparison.OrdinalIgnoreCase);

    private static string FormatMs(double value) =>
        TimeSpan.FromMilliseconds(value).ToString("m\\:ss\\.fff", CultureInfo.InvariantCulture);

    private static MenuItem MenuAction(string header, EventHandler<RoutedEventArgs> handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private static SelectableTextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brushes.Gray
    };

    private static bool IsSelectableTextSource(object? source) => source is Visual visual
        && visual.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null;

    private static SolidColorBrush ColorBrush(string value) => new(Color.Parse(value));

    private sealed class BrowserNode
    {
        public BrowserNode(
            string name,
            string kind,
            string id,
            string type,
            string location,
            string detail,
            MediaRecord? Media = null,
            EventRecord? Event = null,
            BankRecord? Bank = null,
            SegmentContext? Segment = null)
        {
            Name = name;
            Kind = kind;
            Id = id;
            Type = type;
            Location = location;
            Detail = detail;
            this.Media = Media;
            this.Event = Event;
            this.Bank = Bank;
            this.Segment = Segment;
        }

        public string Name { get; }
        public string Kind { get; }
        public string Id { get; }
        public string Type { get; }
        public string Location { get; }
        public string Detail { get; }
        public MediaRecord? Media { get; }
        public EventRecord? Event { get; }
        public BankRecord? Bank { get; }
        public SegmentContext? Segment { get; }
        public ObservableCollection<BrowserNode> Children { get; } = [];
        public bool StructureLoaded { get; set; }

        public static BrowserNode Group(string name, IEnumerable<BrowserNode> children)
        {
            var node = new BrowserNode(name, "GROUP", string.Empty, "FOLDER", string.Empty, name);
            foreach (var child in children)
            {
                node.Children.Add(child);
            }

            return node;
        }

    }

    private sealed record SegmentContext(EventRecord Event, BankRecord Bank, string XmlPath, uint SegmentId);

    private sealed record BrowserSearchIndex(
        IReadOnlyDictionary<uint, string> MediaNames,
        IReadOnlyDictionary<uint, string> EventMediaNames,
        IReadOnlySet<uint> AudioEvents,
        IReadOnlySet<string> AudioBanks);

    private sealed record LoadedTimingScope(
        BnkTimingScope Scope,
        BnkTimelineValidation Validation,
        double AuthoredBpm);

    private sealed record LoadedEventTimeline(
        EventRecord Event,
        BnkTimingScope Scope,
        BnkTimelineValidation Validation,
        BnkTimelineSegment Segment,
        IReadOnlyDictionary<uint, string> MediaNames,
        double AuthoredBpm,
        double PreviewBpm,
        LoadedTimingScope[]? TimingScopes = null)
    {
        public IReadOnlyList<LoadedTimingScope> AllTimingScopes => TimingScopes is { Length: > 0 }
            ? TimingScopes
            : [new LoadedTimingScope(Scope, Validation, AuthoredBpm)];
    }

    private static TimelineSnapshot SnapshotForTimingScope(
        TimelineSnapshot snapshot,
        LoadedTimingScope timingScope)
    {
        var source = snapshot.LoadedTimeline
            ?? throw new InvalidOperationException("A scoped snapshot requires a loaded Event timeline.");

        var segmentIds = timingScope.Validation.Segments.Select(segment => segment.ObjectId).ToHashSet();
        var selected = segmentIds.Contains(source.Segment.ObjectId)
            ? timingScope.Validation.Segments.Single(segment => segment.ObjectId == source.Segment.ObjectId)
            : timingScope.Validation.Segments.First();

        var timeline = new LoadedEventTimeline(
            source.Event,
            timingScope.Scope,
            timingScope.Validation,
            selected,
            source.MediaNames,
            timingScope.AuthoredBpm,
            timingScope.AuthoredBpm,
            [timingScope]);

        var tracks = snapshot.Tracks
            .Where(track => track.SegmentObjectId is { } segmentId && segmentIds.Contains(segmentId))
            .ToArray();

        return snapshot with
        {
            LoadedTimeline = timeline,
            TimelineLengthMs = tracks.Select(track => track.LengthMs).OfType<double>().DefaultIfEmpty(1).Max(),
            Tracks = tracks,
            Markers = snapshot.Markers
                .Where(marker => marker.SegmentObjectId is { } segmentId && segmentIds.Contains(segmentId))
                .ToArray(),
            SegmentBpms = snapshot.SegmentBpms
                .Where(item => segmentIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value),
            MetronomeSegments = snapshot.MetronomeSegments.Where(segmentIds.Contains).ToHashSet(),
            VisibleSegmentIds = snapshot.VisibleSegmentIds?.Where(segmentIds.Contains).ToHashSet()
        };
    }

    private sealed record MediaOccurrenceTimeline(
        LoadedEventTimeline Timeline,
        uint[] SegmentIds,
        int Placements);

    private sealed record ScopeReplacement(uint NewMediaId, string Path, double PhysicalDurationMs);

    private sealed record StructuralImport(
        uint TemplateMediaId,
        uint NewMediaId,
        string Path,
        double PhysicalDurationMs);

    private sealed class TimelineTab(
        Guid id,
        string title,
        TimelineSnapshot snapshot,
        uint? occurrenceMediaId = null,
        uint? inspectionEventId = null,
        MediaRecord? standaloneMedia = null,
        bool isPreview = false)
    {
        private string cleanFingerprint = TimelineFingerprint(snapshot);
        private Dictionary<Guid, string> cleanTrackFingerprints = TrackFingerprints(snapshot);

        public event Action? VisualChanged;

        public Guid Id { get; } = id;
        public string Title { get; set; } = title;
        public TimelineSnapshot Snapshot { get; private set; } = snapshot;
        public bool IsDirty { get; private set; }
        public bool IsPreview { get; private set; } = isPreview;
        public IReadOnlySet<Guid> DirtyTrackIds { get; private set; } = new HashSet<Guid>();
        public uint? OccurrenceMediaId { get; } = occurrenceMediaId;
        public uint? InspectionEventId { get; } = inspectionEventId;
        public MediaRecord? StandaloneMedia { get; } = standaloneMedia;

        public bool UpdateSnapshot(TimelineSnapshot value)
        {
            var changed = TimelineFingerprint(value) != TimelineFingerprint(Snapshot);
            Snapshot = value;
            var dirtyTracks = value.Tracks
                .Where(track => !cleanTrackFingerprints.TryGetValue(track.Id, out var clean)
                    || clean != JsonSerializer.Serialize(track))
                .Select(track => track.Id)
                .ToHashSet();
            var dirty = TimelineFingerprint(value) != cleanFingerprint;
            if (dirty == IsDirty && dirtyTracks.SetEquals(DirtyTrackIds))
            {
                return changed;
            }

            IsDirty = dirty;
            DirtyTrackIds = dirtyTracks;
            VisualChanged?.Invoke();
            return changed;
        }

        public void MarkClean()
        {
            cleanFingerprint = TimelineFingerprint(Snapshot);
            cleanTrackFingerprints = TrackFingerprints(Snapshot);
            IsDirty = false;
            DirtyTrackIds = new HashSet<Guid>();
            VisualChanged?.Invoke();
        }

        public void Promote()
        {
            if (!IsPreview)
            {
                return;
            }

            IsPreview = false;
            VisualChanged?.Invoke();
        }

        private static Dictionary<Guid, string> TrackFingerprints(TimelineSnapshot value) => value.Tracks
            .ToDictionary(track => track.Id, track => JsonSerializer.Serialize(track));

        private static string TimelineFingerprint(TimelineSnapshot value) => JsonSerializer.Serialize(new
        {
            value.Bpm,
            value.BeatsPerBar,
            value.SubdivisionsPerBeat,
            value.SnapEnabled,
            value.TimelineLengthMs,
            value.Tracks,
            value.Markers,
            SegmentBpms = value.SegmentBpms.OrderBy(item => item.Key),
            Replacements = value.Replacements.OrderBy(item => item.Key),
            Imports = value.Imports.OrderBy(item => item.Key)
        });
    }

    private sealed record TimelineSnapshot(
        LoadedEventTimeline? LoadedTimeline,
        double Bpm,
        int BeatsPerBar,
        int SubdivisionsPerBeat,
        bool SnapEnabled,
        double? TimelineLengthMs,
        MusicTimelineTrack[] Tracks,
        MusicTimelineMarker[] Markers,
        Dictionary<uint, double> SegmentBpms,
        Dictionary<uint, ScopeReplacement> Replacements,
        Dictionary<uint, StructuralImport> Imports,
        HashSet<uint> MetronomeSegments,
        HashSet<uint>? VisibleSegmentIds,
        TimelineViewState View);

    private sealed record CopiedTimelineClip(MusicTimelineClip Clip, string SourceTimeline);

    private sealed record ScopedTimelineExportEdits(
        BnkTimelineClipEdit[]? FieldEdits,
        BnkTrackPlaylistEdit[]? PlaylistEdits);

    private sealed record ImportedAudio(Guid Id, string Name, string Path, MediaFormat Format)
    {
        public double DurationMs => Format.DurationSeconds * 1000;
    }

    private sealed record ClipCatalogItem(
        string Key,
        string Name,
        string Detail,
        ImportedAudio? Imported = null,
        MediaRecord? Media = null)
    {
        public static ClipCatalogItem FromImported(ImportedAudio audio) => new(
            $"imported:{audio.Id:N}",
            audio.Name,
            $"IMPORTED  |  {FormatMs(audio.DurationMs)}  |  {audio.Path}",
            Imported: audio);

        public static ClipCatalogItem FromMedia(MediaRecord media) => new(
            $"media:{media.Bank}:{media.Id}",
            Path.GetFileNameWithoutExtension(media.SourceName),
            $"{media.Id}  |  {media.Storage.ToUpperInvariant()}  |  {media.Bank}",
            Media: media);
    }
}
