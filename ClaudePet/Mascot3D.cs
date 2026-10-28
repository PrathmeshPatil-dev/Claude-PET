using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ClaudePet;

// Eye expressions. Normal is the sprite's own 1-pixel eye; the rest are 5x5 glyphs, Claude Code style.
public enum EyeShape { Normal, Happy, Squint, Star, Dizzy, Sleepy, White }

// The Claude Code mascot as a real 3D model: the pixel sprite extruded into bevelled blocks, one
// unit per sprite pixel - a 9x6 body, two eyes on the front, an arm stub on each side, four legs.
// Front is +Z (the eyes); the back is plain clay. Feet stand on y = -2, the body spans y 0..6.
//
// Tricks set Target; Step() eases the shown pose toward it with springs, so every move - even an
// instant change of target - comes out smooth.
public sealed class Mascot3D
{
    public static readonly Color Clay = Color.FromRgb(0xD9, 0x77, 0x57);
    private static readonly Color Ink = Color.FromRgb(0x18, 0x14, 0x13);
    private const double Bevel = 0.14;

    public sealed class Pose
    {
        public double Yaw;           // turn around the vertical axis; 180 = back to the viewer
        public double Pitch;         // + tips the top back (looking up)
        public double Lean;          // sideways tilt
        public double ArmLeft;       // 0 = sticking out, 90 = straight up
        public double ArmRight;
        public double EyeX, EyeY;    // where it looks, in sprite pixels (about -0.6..0.6)
        public double Bob;           // whole-body hop
        public double Squash;        // + squashes (landing), - stretches (jumping)
        public double Flip;          // somersault around its own left-right axis; + = backward
        public readonly double[] LegLift = new double[4];
    }

    public Pose Target { get; } = new();
    public double EyeOpen = 1;       // not smoothed: blinks are fast
    public double Drowsy;            // 0 awake .. 1 lids almost shut (dozing)
    public double Sway;              // idle sway, added to the yaw as it is (already smooth)
    public double Hop => _shown.Bob;   // how high it is off the ground right now

    private readonly Pose _shown = new();
    public Model3DGroup Model { get; } = new();

    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _lean = new(new Vector3D(0, 0, 1), 0);
    private readonly ScaleTransform3D _squash = new(1, 1, 1, 0, -2, 0);
    private readonly AxisAngleRotation3D _flip = new(new Vector3D(1, 0, 0), 0);
    private readonly Model3DGroup _eyes = new();
    private readonly Dictionary<EyeShape, Model3D> _eyeShapes = [];
    private EyeShape _expression = EyeShape.Normal;
    private readonly TranslateTransform3D _bob = new();
    private readonly AxisAngleRotation3D _armLeft = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _armRight = new(new Vector3D(0, 0, 1), 0);
    private readonly TranslateTransform3D _eyeMove = new();
    private readonly ScaleTransform3D _eyeLid = new(1, 1, 1, 0, 3.5, 0);
    private readonly Model3D _gun;
    private readonly Model3D _blush;
    private double _sinceExpression = 10;   // seconds since the expression last changed
    private readonly TranslateTransform3D[] _legs = Enumerable.Range(0, 4).Select(_ => new TranslateTransform3D()).ToArray();

    // pixelSize: screen pixels per sprite pixel - the eye glyphs are laid on whole screen pixels.
    // body: its colour (clay unless it's someone else, like the rival in a fight).
    public Mascot3D(int pixelSize, Color? body = null)
    {
        var clay = new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(body ?? Clay)),
                new SpecularMaterial(new SolidColorBrush(Color.FromArgb(70, 255, 236, 220)), 28),   // soft sheen
            },
        };
        var ink = new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(Ink)),
                new SpecularMaterial(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 60),  // glossy eyes
            },
        };

        Model.Children.Add(Block(-4.5, 0, -2, 4.5, 6, 2, clay));                     // body

        Model.Children.Add(Part(Block(-5.5, 2, -1, -4.4, 4, 1, clay),                // arms, hinged at the body
            new RotateTransform3D(_armLeft, -4.5, 3.5, 0)));
        Model.Children.Add(Part(Block(4.4, 2, -1, 5.5, 4, 1, clay),
            new RotateTransform3D(_armRight, 4.5, 3.5, 0)));

        var steel = new MaterialGroup                                                 // assault rifle, held level across the front
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E))),
                new SpecularMaterial(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 40),
            },
        };
        var black = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21)));
        _gun = new Model3DGroup
        {
            Children =
            {
                Block(4.6, 0.6, 2.2, 7.6, 2.2, 3.2, black),            // stock
                Block(3.6, 1.4, 2.4, 4.8, 2.1, 3.0, black),            // buffer tube
                Block(0.4, 1.2, 2.2, 3.8, 2.5, 3.2, steel),            // receiver
                Block(1.0, 2.5, 2.4, 3.2, 3.0, 3.0, steel),            // carry handle
                Block(2.9, 0.1, 2.3, 3.6, 1.2, 3.1, black),            // pistol grip
                Block(1.0, 0.0, 2.3, 1.9, 1.2, 3.1, black),            // magazine
                Block(0.6, -0.9, 2.3, 1.5, 0.0, 3.1, black),           //   curving forward
                Block(-3.4, 1.3, 2.15, 0.4, 2.3, 3.25, black),         // handguard
                Block(-3.0, 2.3, 2.5, -2.6, 2.9, 2.9, steel, 0.05),    // front sight
                Block(-6.4, 1.6, 2.5, -3.4, 2.0, 2.9, steel, 0.05),    // barrel
                Block(-7.2, 1.5, 2.4, -6.4, 2.1, 3.0, black, 0.05),    // flash hider
            },
        };

        var normal = new Model3DGroup();                                              // eyes, front face only
        normal.Children.Add(Block(-3.5, 3, 1.9, -2.5, 4, 2.18, ink, 0.06));
        normal.Children.Add(Block(2.5, 3, 1.9, 3.5, 4, 2.18, ink, 0.06));
        _eyeShapes[EyeShape.Normal] = normal;
        var white = new Model3DGroup();                                               // the same square eyes, in white
        white.Children.Add(Block(-3.5, 3, 1.9, -2.5, 4, 2.18, new DiffuseMaterial(Brushes.White), 0.06));
        white.Children.Add(Block(2.5, 3, 1.9, 3.5, 4, 2.18, new DiffuseMaterial(Brushes.White), 0.06));
        _eyeShapes[EyeShape.White] = white;
        var pink = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0xA3)));   // cheeks, under the eyes
        _blush = new Model3DGroup
        {
            Children = { Block(-4.2, 2.0, 1.9, -2.6, 2.5, 2.14, pink, 0.04), Block(2.6, 2.0, 1.9, 4.2, 2.5, 2.14, pink, 0.04) },
        };
        foreach (var (shape, left, right) in Glyphs)
            _eyeShapes[shape] = GlyphEyes(left, right, pixelSize, ink);
        _eyes.Children.Add(normal);
        _eyes.Transform = new Transform3DGroup { Children = { _eyeLid, _eyeMove } };
        Model.Children.Add(_eyes);

        double[] legX = [-4.5, -2.5, 1.5, 3.5];                                      // legs, sprite columns 0, 2, 6, 8
        for (var i = 0; i < 4; i++)
            Model.Children.Add(Part(Block(legX[i], -2, -0.9, legX[i] + 1, 0.1, 0.9, clay), _legs[i]));

        // Local pose first (squash, pitch, lean), then the turn, then the hop.
        Model.Transform = new Transform3DGroup
        {
            Children =
            {
                _squash,
                new RotateTransform3D(_flip, 0, 2, 0),
                new RotateTransform3D(_pitch, 0, 2, 0),
                new RotateTransform3D(_lean, 0, -2, 0),
                new RotateTransform3D(_yaw, 0, 0, 0),
                _bob,
            },
        };
    }

    // Ease the shown pose toward Target (critically damped, frame-rate independent) and draw it.
    public void Step(double dt)
    {
        double Ease(double shown, double target, double rate) => shown + (target - shown) * (1 - Math.Exp(-rate * dt));

        var t = Target;
        _shown.Yaw = Ease(_shown.Yaw, t.Yaw, 9);
        _shown.Pitch = Ease(_shown.Pitch, t.Pitch, 9);
        _shown.Lean = Ease(_shown.Lean, t.Lean, 10);
        _shown.ArmLeft = Ease(_shown.ArmLeft, t.ArmLeft, 18);
        _shown.ArmRight = Ease(_shown.ArmRight, t.ArmRight, 18);
        _shown.EyeX = Ease(_shown.EyeX, t.EyeX, 45);   // fast: eyes jump (saccades) rather than glide
        _shown.EyeY = Ease(_shown.EyeY, t.EyeY, 45);
        _shown.Bob = Ease(_shown.Bob, t.Bob, 22);
        _shown.Squash = Ease(_shown.Squash, t.Squash, 20);
        _shown.Flip = Ease(_shown.Flip, t.Flip, 16);
        for (var i = 0; i < 4; i++)
            _shown.LegLift[i] = Ease(_shown.LegLift[i], t.LegLift[i], 26);

        _yaw.Angle = _shown.Yaw + Sway;
        _pitch.Angle = -_shown.Pitch;
        _lean.Angle = _shown.Lean;
        _bob.OffsetY = _shown.Bob;
        _flip.Angle = -_shown.Flip;
        _squash.ScaleY = 1 - _shown.Squash;
        _squash.ScaleX = _squash.ScaleZ = 1 + _shown.Squash * 0.5;   // keeps its volume
        _armLeft.Angle = -_shown.ArmLeft;   // the left stub points to -X: raising it is clockwise
        _armRight.Angle = _shown.ArmRight;
        // A new expression pops open from a squint and wiggles a moment.
        _sinceExpression += dt;
        var s = _sinceExpression;
        var pop = s < 0.12 ? 0.3 + 0.7 * s / 0.12 : 1;
        var wiggle = s < 0.45 ? 0.18 * Math.Sin(s * 45) * Math.Exp(-s * 9) : 0;
        _eyeMove.OffsetX = _shown.EyeX + wiggle;
        _eyeMove.OffsetY = _shown.EyeY;
        _eyeLid.ScaleY = Math.Max(0.1, EyeOpen * pop * (1 - 0.7 * Drowsy));
        for (var i = 0; i < 4; i++)
            _legs[i].OffsetY = _shown.LegLift[i];
    }

    // After a full turn, take the whole turns off both the shown and the target angle together, so
    // a spring still a few degrees short of 360 settles forward to 0 instead of spinning back.
    public void WrapYaw()
    {
        var turns = Math.Round(Target.Yaw / 360) * 360;
        _shown.Yaw -= turns;
        Target.Yaw -= turns;
    }

    public void WrapFlip()
    {
        var turns = Math.Round(Target.Flip / 360) * 360;
        _shown.Flip -= turns;
        Target.Flip -= turns;
    }

    public bool Gun
    {
        get => Model.Children.Contains(_gun);
        set
        {
            if (value == Gun) return;
            if (value) Model.Children.Add(_gun);
            else Model.Children.Remove(_gun);
        }
    }

    public bool Blush
    {
        get => Model.Children.Contains(_blush);
        set
        {
            if (value == Blush) return;
            if (value) Model.Children.Add(_blush);
            else Model.Children.Remove(_blush);
        }
    }

    public EyeShape Expression
    {
        get => _expression;
        set
        {
            if (value == _expression)
                return;
            _expression = value;
            _sinceExpression = 0;
            _eyes.Children.Clear();
            _eyes.Children.Add(_eyeShapes[value]);
        }
    }

    // 5x5 glyphs, left eye then right eye ('#' = ink).
    private static readonly (EyeShape, string[], string[])[] Glyphs =
    [
        (EyeShape.Happy,
            [".....", "..#..", ".#.#.", "#...#", "....."],
            [".....", "..#..", ".#.#.", "#...#", "....."]),
        (EyeShape.Squint,
            ["##...", "..##.", "....#", "..##.", "##..."],
            ["...##", ".##..", "#....", ".##..", "...##"]),
        (EyeShape.Star,
            ["..#..", "#.#.#", ".###.", "#.#.#", "..#.."],
            ["..#..", "#.#.#", ".###.", "#.#.#", "..#.."]),
        (EyeShape.Dizzy,
            ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"],
            ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"]),
        (EyeShape.Sleepy,
            [".....", ".....", "#####", ".....", "....."],
            [".....", ".....", "#####", ".....", "....."]),
    ];

    // Each glyph cell is a whole number of screen pixels (about 3/7 of a sprite pixel, so the glyphs
    // grow with the pet), placed on whole screen pixels around the eye's centre (x = -3 / +3,
    // y = 3.5), so the shapes stay crisp at any pixel size.
    private static Model3DGroup GlyphEyes(string[] left, string[] right, int pixelSize, Material ink)
    {
        var cellPx = Math.Max(3, (int)Math.Round(pixelSize * 3.0 / 7));
        var cell = (double)cellPx / pixelSize;
        var rightX = Math.Floor(3 * pixelSize - 2.5 * cellPx) / pixelSize;   // left edge of the right glyph
        var leftX = -rightX - 5 * cell;
        var topY = (Math.Floor(3.5 * pixelSize - 2.5 * cellPx) + 5 * cellPx) / pixelSize;

        var group = new Model3DGroup();
        foreach (var (rows, x0) in new[] { (left, leftX), (right, rightX) })
            for (var r = 0; r < 5; r++)
                for (var c = 0; c < 5; c++)
                    if (rows[r][c] == '#')
                        group.Children.Add(Block(x0 + c * cell, topY - (r + 1) * cell, 1.95,
                            x0 + (c + 1) * cell, topY - r * cell, 2.2, ink, 0.02));
        return group;
    }

    // Key light from above and in front (so the face stays lit whichever way it turns), a weak fill
    // from the right: the front reads as the sprite's own colour, the sides fall off - that's the 3D.
    // Orthographic and centred on whole sprite pixels, so at rest every edge lands on a screen pixel.
    public static Viewport3D CreateViewport(Mascot3D pet, double cameraCenterY, double cameraWidth)
    {
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(116, 114, 112)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(176, 172, 166), new Vector3D(-0.2, -0.9, -1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(38, 40, 46), new Vector3D(-0.7, -0.2, -0.3)));
        scene.Children.Add(pet.Model);

        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera(new Point3D(0, cameraCenterY, 40), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), cameraWidth),
            ClipToBounds = false,
            IsHitTestVisible = true,
        };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        return viewport;
    }

    private static Model3D Part(Model3D model, Transform3D transform)
    {
        model.Transform = transform;
        return model;
    }

    // A box with chamfered edges: 6 inset faces, 12 bevel strips, 8 corner triangles, each flat
    // shaded with its own normal. The bevels catch the light along every edge.
    private static GeometryModel3D Block(double x0, double y0, double z0, double x1, double y1, double z1,
        Material material, double bevel = Bevel)
    {
        var mesh = new MeshGeometry3D();
        double[] lo = [x0, y0, z0], hi = [x1, y1, z1];

        // The corner of the face on `axis` (at side s[axis]), inset by the bevel on the other two axes.
        Point3D P(int axis, int sx, int sy, int sz)
        {
            int[] s = [sx, sy, sz];
            var c = new double[3];
            for (var a = 0; a < 3; a++)
                c[a] = a == axis ? (s[a] > 0 ? hi[a] : lo[a]) : (s[a] > 0 ? hi[a] - bevel : lo[a] + bevel);
            return new Point3D(c[0], c[1], c[2]);
        }

        static Vector3D Axis(int a, int s) => a == 0 ? new(s, 0, 0) : a == 1 ? new(0, s, 0) : new(0, 0, s);

        void Tri(Point3D a, Point3D b, Point3D c, Vector3D n)
        {
            if (Vector3D.DotProduct(Vector3D.CrossProduct(b - a, c - a), n) < 0)
                (b, c) = (c, b);   // wind it to face outward
            var i = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
            mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n);
            mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
        }

        void Quad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
        {
            n.Normalize();
            Tri(a, b, c, n);
            Tri(a, c, d, n);
        }

        int[] signs = [-1, 1];

        // Faces.
        for (var a = 0; a < 3; a++)
            foreach (var s in signs)
            {
                var (u, v) = ((a + 1) % 3, (a + 2) % 3);
                Point3D F(int su, int sv)
                {
                    var k = new int[3];
                    k[a] = s; k[u] = su; k[v] = sv;
                    return P(a, k[0], k[1], k[2]);
                }
                Quad(F(-1, -1), F(1, -1), F(1, 1), F(-1, 1), Axis(a, s));
            }

        // Edge strips between the faces of axes a and b, running along the third axis c.
        for (var a = 0; a < 3; a++)
            for (var b = a + 1; b < 3; b++)
            {
                var c = 3 - a - b;
                foreach (var sa in signs)
                    foreach (var sb in signs)
                    {
                        Point3D E(int face, int sc)
                        {
                            var k = new int[3];
                            k[a] = sa; k[b] = sb; k[c] = sc;
                            return P(face, k[0], k[1], k[2]);
                        }
                        Quad(E(a, -1), E(a, 1), E(b, 1), E(b, -1), Axis(a, sa) + Axis(b, sb));
                    }
            }

        // Corner triangles.
        foreach (var sx in signs)
            foreach (var sy in signs)
                foreach (var sz in signs)
                {
                    var n = new Vector3D(sx, sy, sz);
                    n.Normalize();
                    Tri(P(0, sx, sy, sz), P(1, sx, sy, sz), P(2, sx, sy, sz), n);
                }

        mesh.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
}
