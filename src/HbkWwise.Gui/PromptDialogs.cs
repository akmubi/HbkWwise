using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace HbkWwise.Gui;

public sealed class PasswordPromptDialog : Window
{
    public PasswordPromptDialog(string message)
    {
        Title = "AES key required";
        Width = 620;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new SelectableTextBlock
        {
            Text = "Open Edit / Preferences, paste the game AES key into the AES key field, save the preferences, then try again.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.Gray
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var close = new Button { Content = "OK" };

        close.Click += (_, _) => Close(null);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
    }
}

public sealed class ChoiceDialog : Window
{
    private readonly ListBox choices;

    public ChoiceDialog(string title, string message, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(items));
        }

        Title = title;
        Width = 700;
        Height = 420;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        choices = new ListBox { ItemsSource = items, SelectedIndex = 0 };
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(18)
        };
        grid.Children.Add(new SelectableTextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        Grid.SetRow(choices, 1);
        grid.Children.Add(choices);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel" };

        cancel.Click += (_, _) => Close(null);
        var accept = new Button { Content = "Open" };
        accept.Click += (_, _) => Close(choices.SelectedIndex >= 0 ? choices.SelectedIndex : null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        choices.DoubleTapped += (_, _) =>
        {
            if (choices.SelectedIndex >= 0)
            {
                Close(choices.SelectedIndex);
            }
        };
    }
}
