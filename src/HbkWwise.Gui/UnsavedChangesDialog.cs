using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace HbkWwise.Gui;

internal enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

internal sealed class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog(string projectName)
    {
        Title = "Unsaved changes";
        Width = 470;
        Height = 180;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var message = new SelectableTextBlock
        {
            Text = $"Save changes to {projectName} before continuing?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        buttons.Children.Add(Button("Save", UnsavedChangesChoice.Save, true));
        buttons.Children.Add(Button("Discard", UnsavedChangesChoice.Discard));
        buttons.Children.Add(Button("Cancel", UnsavedChangesChoice.Cancel));
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(18),
            Children =
            {
                message,
                buttons
            }
        };
        Grid.SetRow(buttons, 1);
    }

    private Button Button(string text, UnsavedChangesChoice choice, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(18, 7),
            IsDefault = primary,
            IsCancel = choice == UnsavedChangesChoice.Cancel
        };
        button.Click += (_, _) => Close(choice);

        return button;
    }
}
