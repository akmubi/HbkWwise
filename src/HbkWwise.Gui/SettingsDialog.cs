using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using HbkWwise.Core;

namespace HbkWwise.Gui;

public sealed class SettingsDialog : Window
{
    private readonly GuiSettings current;
    private readonly TextBox pakDirectory;
    private readonly TextBox repak;
    private readonly TextBox wwiser;
    private readonly TextBox python;
    private readonly TextBox vgmstream;
    private readonly TextBox wwiseConsole;
    private readonly TextBox aesKey;
    private readonly SelectableTextBlock validation = new()
    {
        Foreground = Avalonia.Media.Brushes.OrangeRed,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };

    public SettingsDialog(GuiSettings current)
    {
        this.current = current;
        pakDirectory = Field(current.PakDirectory);
        repak = Field(current.RepakPath);
        wwiser = Field(current.WwiserPath);
        python = Field(current.PythonPath);
        vgmstream = Field(current.VgmstreamPath);
        wwiseConsole = Field(current.WwiseConsolePath);
        aesKey = Field(current.AesKey);

        aesKey.Watermark = "Required for encrypted game PAKs";
        wwiser.Watermark = "Leave empty to use the bundled copy";
        vgmstream.Watermark = "Leave empty to use the bundled copy";

        Title = "Preferences";
        Width = 860;
        Height = 560;
        MinWidth = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var fields = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("190,*,Auto")
        };
        AddDirectoryField(fields, 0, "Game PAK directory", pakDirectory);
        AddFileField(fields, 1, "repak.exe", repak, ["*.exe"]);
        AddFileField(fields, 2, "wwiser.pyz override", wwiser, ["*.pyz", "*.py"]);
        AddFileField(fields, 3, "Python", python, ["*.exe"]);
        AddFileField(fields, 4, "vgmstream override", vgmstream, ["*.exe"]);
        AddFileField(fields, 5, "WwiseConsole.exe", wwiseConsole, ["*.exe"]);
        AddInput(fields, 6, "AES key", aesKey);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 6) };
        var save = new Button { Content = "Save", Padding = new Thickness(16, 6) };

        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) => Complete();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(18)
        };
        panel.Children.Add(fields);

        Grid.SetRow(validation, 1);
        validation.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(validation);

        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);
        return panel;
    }

    private void AddDirectoryField(Grid grid, int row, string label, TextBox input)
    {
        AddInput(grid, row, label, input);

        var button = new Button
        {
            Content = "Browse",
            Margin = new Thickness(8, 4, 0, 4)
        };
        button.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Choose {label}",
                AllowMultiple = false
            });

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                input.Text = path;
            }
        };

        Grid.SetRow(button, row);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
    }

    private void AddFileField(Grid grid, int row, string label, TextBox input, string[] patterns)
    {
        AddInput(grid, row, label, input);

        var button = new Button
        {
            Content = "Browse",
            Margin = new Thickness(8, 4, 0, 4)
        };
        button.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Choose {label}",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(label) { Patterns = patterns }]
            });

            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                input.Text = path;
            }
        };

        Grid.SetRow(button, row);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
    }

    private static void AddInput(Grid grid, int row, string label, TextBox input)
    {
        var text = new SelectableTextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        input.Margin = new Thickness(0, 4);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private void Complete()
    {
        var paths = new[]
        {
            repak.Text,
            wwiser.Text,
            python.Text,
            vgmstream.Text,
            wwiseConsole.Text
        };
        var missing = paths.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && !File.Exists(path));
        if (missing is not null)
        {
            validation.Text = $"Configured file does not exist: {missing}";
            return;
        }

        if (string.IsNullOrWhiteSpace(pakDirectory.Text))
        {
            validation.Text = "Choose the Hi-Fi RUSH PAK directory.";
            return;
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(pakDirectory.Text.Trim());
            _ = GameDataConfiguration.ResolvePakPaths(gameDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException
                or DirectoryNotFoundException or FileNotFoundException)
        {
            validation.Text = exception.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(aesKey.Text))
        {
            validation.Text = "Enter the Hi-Fi RUSH AES key.";
            return;
        }

        var updated = current.Copy();
        updated.PakDirectory = gameDirectory;
        updated.RepakPath = Value(repak);
        updated.WwiserPath = Value(wwiser);
        updated.PythonPath = Value(python);
        updated.VgmstreamPath = Value(vgmstream);
        updated.WwiseConsolePath = Value(wwiseConsole);
        updated.AesKey = aesKey.Text.Trim();

        Close(updated);
    }

    private static TextBox Field(string? value) => new()
    {
        Text = value ?? string.Empty
    };

    private static string? Value(TextBox input) =>
        string.IsNullOrWhiteSpace(input.Text)
            ? null
            : Path.GetFullPath(input.Text.Trim());
}
