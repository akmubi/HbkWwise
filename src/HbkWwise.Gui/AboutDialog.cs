using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HbkWwise.Gui;

internal sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "development";
        Title = "About HBK Wwise";
        Width = 480;
        Height = 310;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#171C23");

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 6)
        };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 13,
            Children =
            {
                new SelectableTextBlock
                {
                    Text = "HBK Wwise",
                    FontSize = 24,
                    FontWeight = FontWeight.SemiBold
                },
                new SelectableTextBlock { Text = $"Version {version}" },
                new SelectableTextBlock
                {
                    Text = "A Wwise audio-modding workbench for Hi-Fi RUSH.",
                    TextWrapping = TextWrapping.Wrap
                },
                new SelectableTextBlock
                {
                    Text = "Copyright © 2026 akmubi\nLicensed under the MIT License.",
                    TextWrapping = TextWrapping.Wrap
                },
                new SelectableTextBlock
                {
                    Text = "Independent community software; not affiliated with or endorsed by the game or middleware publishers.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                close
            }
        };
    }
}
