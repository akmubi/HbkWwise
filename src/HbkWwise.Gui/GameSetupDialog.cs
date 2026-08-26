using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace HbkWwise.Gui;

public sealed record GameSetupRequest(string PakDirectory, string AesKey);

public sealed class GameSetupDialog : Window
{
    private readonly TextBox pakDirectory;
    private readonly TextBox aesKey;
    private readonly SelectableTextBlock validation = new()
    {
        Foreground = Avalonia.Media.Brushes.OrangeRed,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };

    public GameSetupDialog(
        string defaultPakDirectory,
        string? configuredAesKey = null,
        string? initialError = null)
    {
        pakDirectory = new TextBox { Text = defaultPakDirectory };
        aesKey = new TextBox
        {
            Text = configuredAesKey ?? string.Empty,
            Watermark = "Hi-Fi RUSH AES-256 key"
        };

        Title = "Set up HBK Wwise";
        Width = 720;
        Height = 390;
        MinWidth = 580;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();

        if (!string.IsNullOrWhiteSpace(initialError))
        {
            validation.Text = initialError;
        }
    }

    private Control BuildContent()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(18)
        };

        panel.Children.Add(new SelectableTextBlock
        {
            Text = "Set up Hi-Fi RUSH game data",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });

        var explanation = new SelectableTextBlock
        {
            Text = "This is normally a one-time setup. HBK Wwise will verify the base and DLC PAKs with the AES key, then build its internal game index automatically.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Margin = new Thickness(0, 8, 0, 12)
        };
        Grid.SetRow(explanation, 1);
        panel.Children.Add(explanation);

        var fields = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("135,*,Auto")
        };
        AddDirectoryField(fields, 0, "Game PAK directory", pakDirectory);
        AddInput(fields, 1, "AES key", aesKey);
        Grid.SetRow(fields, 2);
        panel.Children.Add(fields);

        validation.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(validation, 3);
        panel.Children.Add(validation);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 6) };
        var setup = new Button { Content = "Verify and set up", Padding = new Thickness(16, 6) };

        cancel.Click += (_, _) => Close(null);
        setup.Click += (_, _) => Complete();
        buttons.Children.Add(cancel);
        buttons.Children.Add(setup);

        Grid.SetRow(buttons, 5);
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
                Title = "Select Hi-Fi RUSH PAK directory",
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
        if (string.IsNullOrWhiteSpace(pakDirectory.Text))
        {
            validation.Text = "Choose the Hi-Fi RUSH PAK directory.";
            return;
        }

        string directory;
        string[] paks;
        try
        {
            directory = Path.GetFullPath(pakDirectory.Text.Trim());
            paks = GameDataConfiguration.ResolvePakPaths(directory);
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

        validation.Foreground = Avalonia.Media.Brushes.LightGray;
        validation.Text = paks.Length == 2
            ? "Base and DLC PAKs found."
            : "Base PAK found; the DLC/update PAK is not installed.";

        Close(new GameSetupRequest(directory, aesKey.Text.Trim()));
    }
}
