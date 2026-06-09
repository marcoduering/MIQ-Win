using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// Black canvas that hosts the interactive triplanar viewer (or a status
/// message). QuickLook owns the window and UI thread; we just hand it this.
/// </summary>
internal sealed class MiqPreviewControl : Grid
{
    private readonly TextBlock _message;
    private FrameworkElement? _content;

    internal MiqPreviewControl()
    {
        Background = Brushes.Black;

        _message = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24),
            Text = "Loading…",
        };

        Children.Add(_message);
    }

    internal void ShowContent(FrameworkElement content)
    {
        if (_content != null) Children.Remove(_content);
        _content = content;
        Children.Add(content);
        _message.Visibility = Visibility.Collapsed;
        content.Focus();
    }

    internal void ShowMessage(string text, bool error = false)
    {
        _message.Text = text;
        _message.Foreground = error ? Brushes.IndianRed : Brushes.Gray;
        _message.Visibility = Visibility.Visible;
        if (_content != null) _content.Visibility = Visibility.Collapsed;
    }
}
