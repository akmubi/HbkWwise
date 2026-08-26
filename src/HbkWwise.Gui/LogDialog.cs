using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HbkWwise.Gui;

public sealed class LogDialog : Window
{
    public LogDialog(GuiLog log)
    {
        Title = "HBK Wwise log";
        Width = 980;
        Height = 560;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = new TextBox
        {
            Text = log.Format(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(18, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
        layout.Children.Add(text);
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(close);
        Content = layout;
    }
}
