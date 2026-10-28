using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClaudePet;

// The frozen screenshot over every screen, dimmed: drag a box to pick the part to send. Enter (or
// a click without dragging) takes the whole screen; Esc cancels. The result is in the image's own
// pixels, so it crops the saved file exactly whatever the DPI.
public sealed class SnipWindow : Window
{
    private readonly TaskCompletionSource<Int32Rect?> _done = new();
    private readonly RectangleGeometry _hole = new();
    private readonly Rectangle _frame = new() { Stroke = new SolidColorBrush(Mascot3D.Clay), StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly BitmapImage _image = new();
    private Point? _from;

    private SnipWindow(string imageFile, Rect screens)
    {
        _image.BeginInit();
        _image.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked: it may be cropped next
        _image.UriSource = new Uri(imageFile);
        _image.EndInit();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        (Left, Top, Width, Height) = (screens.Left, screens.Top, screens.Width, screens.Height);

        var shade = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, screens.Width, screens.Height)), _hole),
        };
        var hint = Theme.Box(Theme.Line("drag to select · ⏎ whole screen · esc cancel", size: 12));
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.Margin = new Thickness(0, 24, 0, 0);
        var canvas = new Canvas { Children = { shade, _frame } };
        Content = new Grid { Children = { new Image { Source = _image, Stretch = Stretch.Fill }, canvas, hint } };

        MouseLeftButtonDown += (_, e) =>
        {
            _from = e.GetPosition(this);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_from is not { } from)
                return;
            var box = new Rect(from, e.GetPosition(this));
            _hole.Rect = box;
            Canvas.SetLeft(_frame, box.X);
            Canvas.SetTop(_frame, box.Y);
            (_frame.Width, _frame.Height) = (box.Width, box.Height);
            _frame.Visibility = Visibility.Visible;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_from is not { } from)
                return;
            ReleaseMouseCapture();
            var box = new Rect(from, e.GetPosition(this));
            Finish(box.Width < 8 || box.Height < 8 ? WholeImage : ToPixels(box));   // a click: the whole screen
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Finish(null);
            else if (e.Key == Key.Enter)
                Finish(WholeImage);
        };
        Deactivated += (_, _) => Finish(null);   // clicked away to another window
    }

    private Int32Rect WholeImage => new(0, 0, _image.PixelWidth, _image.PixelHeight);

    // From this window's DIPs to the screenshot's pixels.
    private Int32Rect ToPixels(Rect box)
    {
        var (sx, sy) = (_image.PixelWidth / ActualWidth, _image.PixelHeight / ActualHeight);
        var x = (int)Math.Clamp(Math.Round(box.X * sx), 0, _image.PixelWidth - 1);
        var y = (int)Math.Clamp(Math.Round(box.Y * sy), 0, _image.PixelHeight - 1);
        var w = (int)Math.Clamp(Math.Round(box.Width * sx), 1, _image.PixelWidth - x);
        var h = (int)Math.Clamp(Math.Round(box.Height * sy), 1, _image.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private void Finish(Int32Rect? area)
    {
        if (_done.TrySetResult(area))
            Close();
    }

    // The part of the screenshot to send, or null when cancelled.
    public static Task<Int32Rect?> SelectAsync(string imageFile, Rect screens)
    {
        var window = new SnipWindow(imageFile, screens);
        window.Show();
        window.Activate();
        return window._done.Task;
    }

    // Keeps only the selected part in the file.
    public static void Crop(string imageFile, Int32Rect area)
    {
        using var full = new System.Drawing.Bitmap(imageFile);
        if (area.X == 0 && area.Y == 0 && area.Width == full.Width && area.Height == full.Height)
            return;
        using var part = full.Clone(new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height), full.PixelFormat);
        full.Dispose();   // release the file before writing over it
        part.Save(imageFile, System.Drawing.Imaging.ImageFormat.Png);
    }
}
