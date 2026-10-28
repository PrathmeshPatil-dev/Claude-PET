using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ClaudePet;

// A transparent, click-through layer over the pet's screen, just under the pet: what it draws, its
// magic sparkles, the friends it summons, and its rival and the effects of a fight. Hidden again whenever there's nothing on it.
// Coordinates in and out are screen DIPs, like the pet's own.
public sealed class Overlay : Window
{
    private const int MaxStrokes = 40;

    private readonly Canvas _canvas = new();
    private readonly List<Polyline> _strokes = [];
    private readonly List<Spark> _sparks = [];
    private readonly List<Friend> _friends = [];
    private readonly Random _rng = new();
    private Polyline? _stroke;
    private Rect _area;

    // Kept up while the pet is drawing, even between strokes.
    public bool Held { get; set; }

    private sealed class Spark
    {
        public required TextBlock View;
        public double X, Y, Vx, Vy, Age, Life;
    }

    // A short-lived effect: k runs 0 -> 1 over its life (fades out unless Step says otherwise).
    private sealed class Fx
    {
        public required UIElement View;
        public double Age, Life;
        public Action<double>? Step;
    }

    private readonly List<Fx> _flashes = [];
    private Viewport3D? _rivalView;

    private sealed class Friend
    {
        public required Mascot3D Pet;
        public required FrameworkElement View;
        public required ScaleTransform Pop;
        public double X, Y, Vx, Vy, Age, Life, Phase, GoalX, Pause;
        public bool OnFloor;
    }

    public Overlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        IsHitTestVisible = false;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
        };
    }

    // Spread over this area and show; the caller then puts the pet back on top.
    public void Cover(Rect area)
    {
        _area = area;
        (Left, Top, Width, Height) = (area.Left, area.Top, area.Width, area.Height);
        if (!IsVisible)
            Show();
    }

    private Point Local(Point p) => new(p.X - _area.Left, p.Y - _area.Top);

    // ---------------------------------------------------------------- drawing

    public void PenDown(Color color)
    {
        _stroke = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _strokes.Add(_stroke);
        _canvas.Children.Add(_stroke);
        if (_strokes.Count > MaxStrokes)
        {
            _canvas.Children.Remove(_strokes[0]);
            _strokes.RemoveAt(0);
        }
    }

    public void Draw(Point p) => _stroke?.Points.Add(Local(p));

    public void PenUp() => _stroke = null;

    // Everything drawn fades out over a second.
    public void Erase()
    {
        _stroke = null;
        foreach (var s in _strokes)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(1));
            fade.Completed += (_, _) => _canvas.Children.Remove(s);
            s.BeginAnimation(OpacityProperty, fade);
        }
        _strokes.Clear();
    }

    // ---------------------------------------------------------------- magic

    private static readonly Color[] SparkColors =
    [
        Color.FromRgb(0xFF, 0xD7, 0x4A), Color.FromRgb(0xFF, 0x8A, 0xD8), Color.FromRgb(0x7C, 0xE7, 0xFF),
        Color.FromRgb(0xB8, 0x8C, 0xFF), Colors.White,
    ];

    public void Sparkle(Point at, int count, double speed = 160)
    {
        for (var i = 0; i < count; i++)
        {
            var a = _rng.NextDouble() * Math.PI * 2;
            var v = speed * (0.3 + _rng.NextDouble());
            var view = new TextBlock
            {
                Text = "✦",
                FontSize = 10 + _rng.Next(14),
                Foreground = new SolidColorBrush(SparkColors[_rng.Next(SparkColors.Length)]),
            };
            _canvas.Children.Add(view);
            var p = Local(at);
            _sparks.Add(new Spark { View = view, X = p.X, Y = p.Y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v - 40, Life = 0.6 + _rng.NextDouble() * 0.6 });
        }
    }

    // A friend pops out of thin air at `at`, drops to the floor and plays for a while, then poofs.
    // size: its window-equivalent side in DIPs; feet sit at feetRatio of that from its top.
    public void Summon(Point at, int pixelSize, double size, double feetRatio)
    {
        var pet = new Mascot3D(pixelSize);
        var pop = new ScaleTransform(0, 0, size / 2, size * feetRatio);
        var view = Mascot3D.CreateViewport(pet, 4, 16);   // the pet's own camera: centre y 4, 16 wide
        view.Width = view.Height = size;
        view.IsHitTestVisible = false;
        view.RenderTransform = pop;
        _canvas.Children.Add(view);
        var p = Local(at);
        _friends.Add(new Friend
        {
            Pet = pet, View = view, Pop = pop,
            X = p.X - size / 2, Y = p.Y - size * feetRatio,
            Life = 9 + _rng.NextDouble() * 5, Phase = _rng.NextDouble() * 6, Pause = 0.3,
            GoalX = p.X - size / 2,
        });
        Sparkle(at, 8, 120);
    }

    // ---------------------------------------------------------------- the fight

    // The rival: a second mascot standing on this layer, moved by the pet's fight choreography.
    public Mascot3D? Rival { get; private set; }
    public Point RivalAt { get; set; }   // its window-equivalent top-left, screen DIPs

    public Mascot3D ShowRival(int pixelSize, double size, Color body)
    {
        HideRival();
        Rival = new Mascot3D(pixelSize, body);
        _rivalView = Mascot3D.CreateViewport(Rival, 4, 16);   // the pet's own camera: centre y 4, 16 wide
        _rivalView.Width = _rivalView.Height = size;
        _rivalView.IsHitTestVisible = false;
        _canvas.Children.Add(_rivalView);
        PlaceRival();
        return Rival;
    }

    public void HideRival()
    {
        if (_rivalView != null)
            _canvas.Children.Remove(_rivalView);
        (Rival, _rivalView) = (null, null);
    }

    private void PlaceRival()
    {
        var p = Local(RivalAt);
        Canvas.SetLeft(_rivalView!, p.X);
        Canvas.SetTop(_rivalView!, p.Y);
    }

    private void AddFlash(UIElement view, double life, Action<double>? step = null)
    {
        _canvas.Children.Add(view);
        _flashes.Add(new Fx { View = view, Life = life, Step = step });
    }

    // A beam: a wide coloured glow round a white-hot core, flaring out as it fades.
    public void Laser(Point from, Point to, Color color, double life)
    {
        var (a, b) = (Local(from), Local(to));
        Line Beam(Brush brush, double width) => new()
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var glow = Beam(new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)), 18);
        var beam = new Canvas { Children = { glow, Beam(new SolidColorBrush(color), 10), Beam(Brushes.White, 5) } };
        AddFlash(beam, life, k =>
        {
            beam.Opacity = 1 - k * k;
            glow.StrokeThickness = 18 + 10 * k;
        });
    }

    // Lightning from the top of the screen down to `ground`, flickering, with a forked branch.
    public void Bolt(Point ground)
    {
        var g = Local(ground);
        var main = new PointCollection();
        var branch = new PointCollection();
        const int segments = 10;
        for (var i = 0; i <= segments; i++)
        {
            var k = (double)i / segments;
            var p = new Point(g.X + (i == segments ? 0 : (_rng.NextDouble() - 0.5) * 70 * (1 - k)), g.Y * k);
            main.Add(p);
            if (i >= 4 && i <= 7)
                branch.Add(i == 4 ? p : new Point(p.X + (i - 4) * 28, p.Y + (i - 4) * 10));
        }
        var glow = new SolidColorBrush(Color.FromArgb(140, 150, 190, 255));
        Polyline Line(PointCollection pts, Brush brush, double width) => new()
        {
            Points = pts, Stroke = brush, StrokeThickness = width, StrokeLineJoin = PenLineJoin.Bevel,
        };
        var bolt = new Canvas
        {
            Children = { Line(main, glow, 16), Line(branch, glow, 9), Line(main, Brushes.White, 4), Line(branch, Brushes.White, 2) },
        };
        AddFlash(bolt, 0.5, k => bolt.Opacity = k < 0.45 ? ((int)(k * 30) % 2 == 0 ? 1 : 0.35) : (1 - k) * 1.8);
        Sparkle(ground, 18, 320);
    }

    // The whole screen lights up for an instant.
    public void Flash(double life = 0.3)
    {
        var white = new Rectangle { Width = _area.Width, Height = _area.Height, Fill = Brushes.White };
        AddFlash(white, life, k => white.Opacity = 0.55 * (1 - k));
    }

    // A shockwave ring spreading out along the ground from `at`.
    public void Ring(Point at, Color color, double radius = 150)
    {
        var p = Local(at);
        var ring = new Ellipse { Stroke = new SolidColorBrush(color), StrokeThickness = 6 };
        AddFlash(ring, 0.45, k =>
        {
            var r = 10 + (radius - 10) * (1 - (1 - k) * (1 - k));
            (ring.Width, ring.Height) = (2 * r, 0.7 * r);
            Canvas.SetLeft(ring, p.X - r);
            Canvas.SetTop(ring, p.Y - 0.35 * r);
            ring.StrokeThickness = 6 * (1 - k) + 1;
            ring.Opacity = 1 - k;
        });
    }

    // A ball of light - charging up, or where two beams push against each other.
    public void Orb(Point at, double life, double radius, Color color)
    {
        var p = Local(at);
        var orb = new Ellipse
        {
            Width = 2 * radius,
            Height = 2 * radius,
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(color, 0.45),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            }),
        };
        Canvas.SetLeft(orb, p.X - radius);
        Canvas.SetTop(orb, p.Y - radius);
        AddFlash(orb, life, k => orb.Opacity = k < 0.7 ? 1 : (1 - k) / 0.3);
    }

    // A comic-book hit word ("POW!") that pops out of the contact point.
    public void Burst(Point at, string word)
    {
        var p = Local(at);
        var pop = new ScaleTransform(0.3, 0.3);
        var text = new TextBlock
        {
            Text = word,
            FontFamily = new FontFamily("Impact, Arial Black"),
            FontSize = 34,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x4A)),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 3, Opacity = 1 },
            RenderTransform = new TransformGroup { Children = { pop, new RotateTransform(_rng.Next(-14, 15)) } },
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(text, p.X - text.DesiredSize.Width / 2);
        Canvas.SetTop(text, p.Y - text.DesiredSize.Height - 10);
        AddFlash(text, 0.75, k =>
        {
            pop.ScaleX = pop.ScaleY = k < 0.15 ? 0.3 + k / 0.15 * 0.9 : 1.2 - Math.Min(1, (k - 0.15) / 0.2) * 0.2;
            text.Opacity = k < 0.7 ? 1 : (1 - k) / 0.3;
        });
    }

    // ---------------------------------------------------------------- helper agents

    private readonly List<(Mascot3D Pet, FrameworkElement View)> _orbiters = [];
    private double _orbitAngle;

    // One tiny clone per running helper agent, circling over `head` (screen DIPs); they pop in and
    // out with sparkles as agents start and finish.
    public void Orbit(int count, Point head, double size, int pixelSize, double dt)
    {
        while (_orbiters.Count < count)
        {
            var pet = new Mascot3D(pixelSize);
            var view = Mascot3D.CreateViewport(pet, 4, 16);   // the pet's own camera: centre y 4, 16 wide
            view.Width = view.Height = size;
            view.IsHitTestVisible = false;
            _canvas.Children.Add(view);
            _orbiters.Add((pet, view));
            Sparkle(head, 8, 140);
        }
        while (_orbiters.Count > count)
        {
            _canvas.Children.Remove(_orbiters[^1].View);
            _orbiters.RemoveAt(_orbiters.Count - 1);
            Sparkle(head, 8, 140);
        }

        _orbitAngle += dt * 1.8;
        var radius = size * 1.3;
        for (var i = 0; i < _orbiters.Count; i++)
        {
            var (pet, view) = _orbiters[i];
            var a = _orbitAngle + i * 2 * Math.PI / _orbiters.Count;
            var p = Local(new Point(head.X + radius * Math.Cos(a) - size / 2, head.Y - 0.35 * radius * Math.Sin(a) - size));
            Canvas.SetLeft(view, p.X);
            Canvas.SetTop(view, p.Y);
            pet.Target.Yaw = -60 * Math.Sin(a);   // facing the way it goes round
            for (var l = 0; l < 4; l++)
                pet.Target.LegLift[l] = Math.Max(0, Math.Sin(_orbitAngle * 6 + i + (l % 2) * Math.PI)) * 0.6;
            pet.Step(dt);
        }
    }

    public bool Orbiting => _orbiters.Count > 0;

    // ---------------------------------------------------------------- per frame

    public void Step(double dt)
    {
        if (!IsVisible)
            return;

        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Age += dt;
            s.Vy += 220 * dt;
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.View.Opacity = Math.Max(0, 1 - s.Age / s.Life);
            Canvas.SetLeft(s.View, s.X);
            Canvas.SetTop(s.View, s.Y);
            if (s.Age >= s.Life)
            {
                _canvas.Children.Remove(s.View);
                _sparks.RemoveAt(i);
            }
        }

        for (var i = _flashes.Count - 1; i >= 0; i--)
        {
            var f = _flashes[i];
            f.Age += dt;
            var k = Math.Min(1, f.Age / f.Life);
            if (f.Step != null)
                f.Step(k);
            else
                f.View.Opacity = 1 - k;
            if (k >= 1)
            {
                _canvas.Children.Remove(f.View);
                _flashes.RemoveAt(i);
            }
        }

        if (Rival != null)
        {
            Rival.Step(dt);
            PlaceRival();
        }

        for (var i = _friends.Count - 1; i >= 0; i--)
        {
            var f = _friends[i];
            StepFriend(f, dt);
            if (f.Age >= f.Life + 0.3)
            {
                _canvas.Children.Remove(f.View);
                _friends.RemoveAt(i);
            }
        }

        if (_canvas.Children.Count == 0 && !Held)
            Hide();   // nothing left: no full-screen layer to redraw
    }

    private void StepFriend(Friend f, double dt)
    {
        var size = f.View.Width;
        var floor = _area.Height - f.Pop.CenterY;   // CenterY is its feet
        var t = f.Pet.Target;
        f.Age += dt;

        // Pop in, and poof out at the end of its life.
        var s = f.Age < 0.25 ? f.Age / 0.25 : f.Age > f.Life ? Math.Max(0, 1 - (f.Age - f.Life) / 0.3) : 1;
        f.Pop.ScaleX = f.Pop.ScaleY = s;
        if (f.Age > f.Life && f.Age - dt <= f.Life)
            Sparkle(new Point(_area.Left + f.X + size / 2, _area.Top + f.Y + size / 2), 10, 140);

        if (!f.OnFloor)
        {
            f.Vy += 2200 * dt;   // gravity
            f.Y += f.Vy * dt;
            f.X += f.Vx * dt;
            t.ArmLeft = t.ArmRight = 150;
            if (f.Y >= floor)
            {
                (f.Y, f.Vy, f.Vx) = (floor, 0, 0);
                f.OnFloor = true;
                t.Squash = 0.25;
                t.ArmLeft = t.ArmRight = 0;
                f.Pet.Expression = EyeShape.Happy;
            }
        }
        else if (f.Pause > 0)
        {
            // Between runs: a hop, a wave or a cheer.
            f.Pause -= dt;
            t.Squash = 0;
            for (var l = 0; l < 4; l++)
                t.LegLift[l] = 0;
            t.Yaw = 0;
            if (f.Pause <= 0)
            {
                f.GoalX = Math.Clamp(f.X + (_rng.NextDouble() - 0.5) * 700, -size * 0.2, _area.Width - size * 0.8);
                f.Vx = Math.Sign(f.GoalX - f.X) * (140 + _rng.NextDouble() * 220);
                t.ArmLeft = t.ArmRight = 0;
                f.Pet.Expression = EyeShape.Normal;
            }
        }
        else
        {
            f.X += f.Vx * dt;
            f.Phase += dt * (Math.Abs(f.Vx) > 250 ? 22 : 12);
            for (var l = 0; l < 4; l++)
                t.LegLift[l] = Math.Max(0, Math.Sin(f.Phase + (l % 2) * Math.PI)) * 0.7;
            t.Yaw = 35 * Math.Sign(f.Vx);
            t.Bob = Math.Abs(Math.Sin(f.Phase)) * 0.35;
            if (Math.Sign(f.GoalX - f.X) != Math.Sign(f.Vx))
            {
                f.Pause = 0.5 + _rng.NextDouble() * 1.2;
                t.Bob = 0;
                switch (_rng.Next(3))
                {
                    case 0:
                        (f.Vy, f.OnFloor) = (-500 - _rng.NextDouble() * 300, false);   // hop
                        break;
                    case 1:
                        t.ArmRight = 140;
                        f.Pet.Expression = EyeShape.Happy;
                        break;
                    default:
                        t.ArmLeft = t.ArmRight = 160;
                        f.Pet.Expression = EyeShape.Squint;
                        break;
                }
            }
        }

        f.Pet.Step(dt);
        Canvas.SetLeft(f.View, f.X);
        Canvas.SetTop(f.View, f.Y);
    }
}
