using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudePet;

// A small terminal-style menu by the pet: up/down to move, type to jump, Enter or a click to pick,
// Esc or clicking away to cancel. Returns the picked index, or null.
public sealed class ChoiceWindow : Window
{
    private readonly TaskCompletionSource<int?> _done = new();
    private readonly List<(Border Row, TextBlock Mark, TextBlock Label)> _rows = [];
    private int _selected = -1;

    private ChoiceWindow(string title, IReadOnlyList<(string Label, string Detail)> items)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        // One line per item: a mark (› on the selected one), the name, and the detail dimmed.
        var list = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var (label, detail) = items[i];
            var mark = Theme.Line(" ", Theme.Accent);
            var name = Theme.Line(label);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, name } };
            if (detail.Length > 0)
                line.Children.Add(new TextBlock { Text = "  " + detail, FontFamily = Theme.Mono, FontSize = 11, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
            var row = new Border
            {
                Child = line,
                Padding = new Thickness(4, 2, 8, 2),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,   // hit-testable everywhere on the line
            };
            var index = i;
            row.MouseEnter += (_, _) => Select(index);
            row.MouseLeftButtonUp += (_, _) => Finish(index);
            _rows.Add((row, mark, name));
            list.Children.Add(row);
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Finish(null);
            else if (e.Key == Key.Enter && _selected >= 0)
                Finish(_selected);
            else if (e.Key is Key.Down or Key.Up)
                Select(Math.Clamp(_selected + (e.Key == Key.Down ? 1 : -1), 0, _rows.Count - 1));
            else
                return;
            e.Handled = true;
        };
        var labels = items.Select(i => i.Label).ToList();
        var typed = "";
        var lastKey = DateTime.MinValue;
        TextInput += (_, e) =>
        {
            // Type to jump, like File Explorer: quick keys build up a name ("mes3"), a pause starts over
            // and the same letter again moves on to the next match.
            if (e.Text.Length != 1 || char.IsControl(e.Text[0]))
                return;
            var more = DateTime.Now - lastKey < TimeSpan.FromSeconds(1) && typed != e.Text;
            lastKey = DateTime.Now;
            typed = more ? typed + e.Text : e.Text;
            for (var step = more ? 0 : 1; step <= labels.Count; step++)
            {
                var i = (_selected + step) % labels.Count;
                if (labels[i].StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                {
                    Select(i);
                    return;
                }
            }
        };
        Deactivated += (_, _) => Finish(null);

        Content = Theme.Box(new StackPanel
        {
            MinWidth = 300,
            Children =
            {
                Theme.Line(title, Theme.Dim, 12),
                new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 460,
                    Margin = new Thickness(0, 5, 0, 5),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                Theme.Line("↑↓ move · a-z jump · ⏎ pick · esc", Theme.Dim, 10),
            },
        });
        Select(0);
    }

    private void Select(int index)
    {
        if (index == _selected || index < 0 || index >= _rows.Count)
            return;
        if (_selected >= 0)
        {
            var (oldRow, oldMark, oldLabel) = _rows[_selected];
            (oldRow.Background, oldRow.BorderBrush, oldMark.Text, oldLabel.Foreground) = (Brushes.Transparent, Brushes.Transparent, " ", Theme.Text);
        }
        _selected = index;
        var (row, mark, label) = _rows[index];
        (row.Background, row.BorderBrush, mark.Text, label.Foreground) = (Theme.Selected, Theme.Accent, "›", Theme.Accent);
        row.BringIntoView();
    }

    private void Finish(int? choice)
    {
        if (_done.TrySetResult(choice))
            Close();
    }

    // Shows the menu above `anchor` (like the bubble) with the first item selected.
    public static Task<int?> PickAsync(Window anchor, string title, IReadOnlyList<(string Label, string Detail)> items)
    {
        var window = new ChoiceWindow(title, items);
        window.Show();
        window.UpdateLayout();
        window.Left = Math.Clamp(anchor.Left + anchor.Width / 2 - window.ActualWidth / 2,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - window.ActualWidth);
        window.Top = Math.Max(SystemParameters.VirtualScreenTop, anchor.Top + anchor.Height * 0.2 - window.ActualHeight);
        window.Activate();
        return window._done.Task;
    }
}
