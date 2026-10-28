using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudePet;

// The pet's one look, terminal style: near-black panel, thin clay border, monospace text.
// Used by the bubble (TalkWindow.xaml via x:Static), the menus and the snip hint.
public static class Theme
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly Brush Panel = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    public static readonly Brush Text = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
    public static readonly Brush Dim = Frozen(Color.FromRgb(0x80, 0x80, 0x80));
    public static readonly Brush Accent = Frozen(Mascot3D.Clay);
    public static readonly Brush Selected = Frozen(Color.FromRgb(0x26, 0x26, 0x26));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // The panel every pet window sits in.
    public static Border Box(UIElement child) => new()
    {
        Background = Panel,
        BorderBrush = Accent,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(4),
        Child = child,
    };

    public static TextBlock Line(string text, Brush? brush = null, double size = 13) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = size,
        Foreground = brush ?? Text,
    };
}
