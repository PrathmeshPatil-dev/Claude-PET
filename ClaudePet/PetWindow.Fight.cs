using System.Windows;
using System.Windows.Media;

namespace ClaudePet;

// "fight": a dark twin drops in on a lightning bolt and the two have it out, stick-fight style.
// Every blow is wind-up -> snap -> a short freeze on contact (hit-stop) -> knockback, with a hit
// word, sparks and a shake to sell it. Rounds: face-off, rush and clash, a jab combo, a spinning
// flying kick, an uppercut juggle and slam, a thunder storm it dodges, a teleport back-kick, a beam
// struggle, and a thunder finisher.
public partial class PetWindow
{
    private static readonly Color RivalBody = Color.FromRgb(0x4B, 0x55, 0xC8);
    private static readonly Color PetBeam = Color.FromRgb(0xFF, 0x8A, 0x3D);
    private static readonly Color RivalBeam = Color.FromRgb(0x7C, 0x9C, 0xFF);

    private async Task FightAsync(CancellationToken ct)
    {
        var wa = WorkArea();
        var u = Unit;
        var floor = FloorY;
        if (_y < floor - 2)
            await FallToAsync(floor, ct);

        // Room for both: the rival comes in on the side with more space.
        var side = _x + Size / 2 < wa.Left + wa.Width / 2 ? 1 : -1;
        var start = side > 0
            ? Math.Clamp(_x, wa.Left + Size * 0.3, wa.Right - Size * 5)
            : Math.Clamp(_x, wa.Left + Size * 4, wa.Right - Size * 1.3);
        await WalkToAsync(start, ct, turnBack: false);

        double rx = start + side * 3.4 * Size, ry = floor;
        Mascot3D? rival = null;
        var r = new Mascot3D.Pose();   // the rival's target pose, once it's here

        double Fit(double x) => Math.Clamp(x, wa.Left, wa.Right - Size);
        int Facing() => rx > _x ? 1 : -1;   // which way the rival is from the pet
        Point PetChest() => new(_x + Size / 2, _y + 9 * u);
        Point RivalChest() => new(rx + Size / 2, ry + 9 * u);
        Point PetEyes() => new(_x + Size / 2 + Facing() * 2.5 * u, _y + 8.5 * u);
        Point RivalEyes() => new(rx + Size / 2 - Facing() * 2.5 * u, ry + 8.5 * u);
        Point Ground(double x) => new(x + Size / 2, floor + FeetFromTop);
        void PutRival() => _fx.RivalAt = new Point(rx, ry);

        // Arms stick out sideways, so the arm nearest the other one is the "front" (punching) arm.
        void PetArm(double a) { if (Facing() > 0) _t.ArmRight = a; else _t.ArmLeft = a; }
        void RivalArm(double a) { if (Facing() > 0) r.ArmLeft = a; else r.ArmRight = a; }
        void FaceOff()
        {
            _t.Yaw = 40 * Facing();
            r.Yaw = -40 * Facing();
        }
        void Guard()
        {
            _t.ArmLeft = _t.ArmRight = 110;
            r.ArmLeft = r.ArmRight = 110;
            _t.Lean = r.Lean = 0;
        }
        void RivalLegs(double phase, double lift)
        {
            for (var i = 0; i < 4; i++)
                r.LegLift[i] = Math.Max(0, Math.Sin(phase + (i % 2) * Math.PI)) * lift;
        }

        Task MovePet(double x, double y, double seconds, double arc = 0, Action<double>? also = null)
        {
            var (x0, y0, x1) = (_x, _y, Fit(x));
            return Animate(seconds, k =>
            {
                _x = x0 + (x1 - x0) * k;
                _y = y0 + (y - y0) * k - arc * 4 * k * (1 - k);
                also?.Invoke(k);
            }, ct, linear: arc > 0);
        }

        Task MoveRival(double x, double y, double seconds, double arc = 0, Action<double>? also = null)
        {
            var (x0, y0, x1) = (rx, ry, Fit(x));
            return Animate(seconds, k =>
            {
                rx = x0 + (x1 - x0) * k;
                ry = y0 + (y - y0) * k - arc * 4 * k * (1 - k);
                PutRival();
                also?.Invoke(k);
            }, ct, linear: arc > 0);
        }

        // Contact: the hit word and sparks, a freeze, then both bodies shake with the impact.
        async Task Hit(Point at, string word, double shake)
        {
            _fx.Burst(at, word);
            _fx.Sparkle(at, 16, 320);
            await Wait(0.08, ct);
            var (x0, rx0) = (_x, rx);
            await Animate(0.18, k =>
            {
                var o = Math.Sin(k * 50) * shake * (1 - k);
                _x = x0 + o;
                rx = rx0 - o;
                PutRival();
            }, ct, linear: true);
            (_x, rx) = (x0, rx0);
            PutRival();
        }

        _fx.Held = true;
        ShowOverlay();
        _legsByTrick = true;
        try
        {
            // ---- 1. A challenger appears: lightning splits the sky and the dark twin lands in it.
            _t.Yaw = 30 * side;
            _t.EyeX = 0.5 * side;
            await Wait(0.4, ct);
            _fx.Bolt(Ground(rx));
            _fx.Flash();
            await Wait(0.1, ct);
            rival = _fx.ShowRival(_settings.PixelSize, Size, RivalBody);
            r = rival.Target;
            PutRival();
            rival.Expression = EyeShape.Squint;
            r.Squash = 0.3;
            _fx.Ring(Ground(rx), RivalBeam);
            _fx.Sparkle(RivalChest(), 24, 300);
            await JumpAsync(0.8, ct);   // startled
            r.Squash = 0;
            FaceOff();
            _talk.Say(this, "A challenger appears!", 2.5);
            for (var i = 0; i < 2; i++)   // it beckons: come on, then
            {
                RivalArm(150);
                await Wait(0.2, ct);
                RivalArm(95);
                await Wait(0.2, ct);
            }
            _pet.Expression = EyeShape.Squint;
            Guard();
            await Wait(0.4, ct);

            // ---- 2. Both charge and meet in the middle.
            var mid = (_x + rx) / 2;
            _walking = _running = true;
            _stride = 1;
            await Task.WhenAll(
                MovePet(mid - Facing() * 0.62 * Size, floor, 0.32, also: _ => _t.Lean = -12 * Facing()),
                MoveRival(mid + Facing() * 0.62 * Size, floor, 0.32, also: k =>
                {
                    RivalLegs(k * 14, 0.8);
                    r.Lean = 12 * Facing();
                }));
            _walking = _running = false;
            _stride = 0;
            RivalLegs(0, 0);
            await Hit(new Point((_x + rx) / 2 + Size / 2, _y + 9 * u), "CLASH!", 8);
            await Task.WhenAll(
                MovePet(_x - Facing() * 0.4 * Size, floor, 0.25),
                MoveRival(rx + Facing() * 0.4 * Size, floor, 0.25));
            Guard();

            // ---- 3. Jab, jab - blocked - then the cross lands.
            for (var i = 0; i < 3; i++)
            {
                _t.Lean = 6 * Facing();   // wind-up
                PetArm(125);
                await Wait(0.1, ct);
                _t.Lean = -16 * Facing();   // snap
                PetArm(0);
                await MovePet(_x + Facing() * 0.24 * Size, floor, 0.08);
                if (i < 2)
                {
                    r.ArmLeft = r.ArmRight = 160;
                    r.Squash = 0.1;
                    await Hit(RivalChest(), "BLOCK!", 3);
                    r.Squash = 0;
                    await MovePet(_x - Facing() * 0.24 * Size, floor, 0.12);
                }
                else
                {
                    rival.Expression = EyeShape.Dizzy;
                    await Hit(RivalChest(), "POW!", 8);
                    await MoveRival(rx + Facing() * 0.9 * Size, floor, 0.3, also: k => r.Lean = -20 * Facing() * (1 - k));
                    rival.Expression = EyeShape.Squint;
                }
                Guard();
            }
            await Wait(0.3, ct);

            // ---- 4. It answers with a spinning flying kick and sends the pet rolling.
            rival.Expression = EyeShape.White;
            r.Squash = 0.18;
            await Wait(0.18, ct);
            r.Squash = -0.12;
            await MoveRival(_x + Facing() * 0.75 * Size, floor, 0.45, arc: 2.2 * Size, also: k =>
            {
                r.Flip = 360 * k;
                RivalArm(20);
            });
            rival.WrapFlip();
            r.Squash = 0.15;
            _pet.Expression = EyeShape.Dizzy;
            await Hit(PetChest(), "WHAM!", 10);
            r.Squash = 0;
            await MovePet(_x - Facing() * 1.3 * Size, floor, 0.35, also: k => _t.Lean = 22 * Facing() * (1 - k));
            await Animate(0.45, k => _t.Flip = 360 * k, ct);   // back roll onto its feet
            _pet.WrapFlip();
            _pet.Expression = EyeShape.Squint;
            Guard();
            FaceOff();
            await Wait(0.3, ct);

            // ---- 5. Dash in, uppercut it into the air, jump after it and slam it down.
            _walking = _running = true;
            _stride = 1;
            await MovePet(rx - Facing() * 0.7 * Size, floor, 0.22, also: _ =>
            {
                _t.Lean = -16 * Facing();
                _fx.Sparkle(PetChest(), 1, 40);   // afterimage
            });
            _walking = _running = false;
            _stride = 0;
            _t.Squash = 0.2;   // crouch
            PetArm(60);
            await Wait(0.12, ct);
            _t.Squash = -0.15;
            PetArm(170);
            rival.Expression = EyeShape.Dizzy;
            await Hit(RivalChest(), "UPPERCUT!", 6);
            var high = floor - 3.2 * Size;
            await Task.WhenAll(
                MoveRival(rx + Facing() * 0.3 * Size, high, 0.4, also: k => r.Flip = -200 * k),
                MovePet(_x, floor - 0.5 * Size, 0.2));
            _t.Squash = 0;
            await MovePet(rx - Facing() * 0.2 * Size, high - 0.8 * Size, 0.3, also: k => _t.Flip = -360 * k);
            _pet.WrapFlip();
            await Hit(RivalChest(), "SLAM!", 4);
            await Task.WhenAll(
                MoveRival(rx + Facing() * 0.4 * Size, floor, 0.16, also: k => r.Flip = -200 - 160 * k),
                MovePet(_x - Facing() * 0.5 * Size, floor, 0.45));
            rival.WrapFlip();
            r.Squash = 0.35;
            _fx.Ring(Ground(rx), RivalBeam, 200);
            await Hit(Ground(rx), "CRASH!", 12);
            await Wait(0.3, ct);
            r.Squash = 0;
            FaceOff();
            Guard();

            // ---- 6. Furious, it calls down thunder. Dodge, dodge, backflip - then vanish.
            rival.Expression = EyeShape.White;
            r.ArmLeft = r.ArmRight = 170;
            _talk.Say(this, "Thunder?!", 1.5);
            for (var i = 0; i < 3; i++)
            {
                var spot = Ground(_x);
                _fx.Ring(spot, Colors.Red, 60);   // where it's going to strike
                await Wait(0.3, ct);
                var away = i == 1 ? -Facing() : (i == 0 ? 1 : -1) * Facing();
                var dodge = MovePet(_x + away * 0.8 * Size, floor, 0.32, arc: 0.9 * Size,
                    also: k => _t.Flip = i == 2 ? 360 * k : 0);
                await Wait(0.1, ct);
                _fx.Bolt(spot);
                _fx.Flash(0.25);
                _fx.Ring(spot, RivalBeam);
                await dodge;
                _pet.WrapFlip();
            }
            r.ArmLeft = r.ArmRight = 110;

            _fx.Sparkle(PetChest(), 20, 200);   // teleport...
            await Animate(0.12, k => Opacity = 1 - k, ct);
            var behind = rx + Facing() * 0.85 * Size;
            if (behind < wa.Left || behind > wa.Right - Size)
                behind = rx - Facing() * 0.85 * Size;   // no room behind it: come in from the front
            _x = Fit(behind);
            _fx.Sparkle(PetChest(), 20, 200);
            await Animate(0.12, k => Opacity = k, ct);
            _t.Yaw = 40 * Facing();   // ...right behind it
            await Wait(0.12, ct);
            r.Yaw = -40 * Facing();   // it turns, too late
            _t.Squash = 0.12;
            await Wait(0.08, ct);
            _t.Squash = 0;
            await Animate(0.25, k => _t.Yaw = 40 * Facing() + 360 * k, ct);   // spinning back kick
            _pet.WrapYaw();
            rival.Expression = EyeShape.Dizzy;
            await Hit(RivalChest(), "KICK!", 8);
            await MoveRival(rx + Facing() * 1.4 * Size, floor, 0.4, arc: 0.6 * Size, also: k => r.Lean = -25 * Facing() * (1 - k));

            // ---- 7. Beam struggle: both charge up and fire; the beams meet and push.
            await Task.WhenAll(
                MovePet(_x - Facing() * 0.6 * Size, floor, 0.35, arc: 0.5 * Size),
                MoveRival(rx + Facing() * 0.4 * Size, floor, 0.35, arc: 0.5 * Size));
            FaceOff();
            _pet.Expression = EyeShape.White;
            rival.Expression = EyeShape.White;
            for (var i = 0; i < 8; i++)
            {
                _fx.Sparkle(PetEyes(), 3, 90);
                _fx.Sparkle(RivalEyes(), 3, 90);
                _t.Squash = r.Squash = i % 2 == 0 ? 0.06 : -0.04;
                await Wait(0.1, ct);
            }
            _t.Squash = r.Squash = 0;
            _fx.Orb(PetEyes(), 0.5, 16, PetBeam);
            _fx.Orb(RivalEyes(), 0.5, 16, RivalBeam);
            await Wait(0.3, ct);
            for (var i = 0; i < 20; i++)
            {
                var meet = 0.5 + 0.08 * Math.Sin(i * 0.9) + (i > 12 ? (i - 12) * 0.055 : 0);   // it pushes through
                var (a, b) = (PetEyes(), RivalEyes());
                var m = a + (b - a) * meet;
                _fx.Laser(a, m, PetBeam, 0.14);
                _fx.Laser(b, m, RivalBeam, 0.14);
                _fx.Orb(m, 0.14, 22 + i, Colors.White);
                _fx.Sparkle(m, 3, 260);
                _t.Lean = -5 * Facing() * (1 + Math.Sin(i * 1.3));
                r.Lean = 5 * Facing() * (1 + Math.Cos(i * 1.1));
                await Wait(0.09, ct);
            }
            _fx.Laser(PetEyes(), RivalChest(), PetBeam, 0.5);
            _fx.Flash(0.4);
            _fx.Ring(RivalChest(), PetBeam, 220);
            rival.Expression = EyeShape.Dizzy;
            await Hit(RivalChest(), "KABOOM!", 12);
            await MoveRival(rx + Facing() * 1.8 * Size, floor, 0.5, arc: 1.2 * Size, also: k => r.Flip = 360 * k);
            rival.WrapFlip();
            r.Lean = -80 * Facing();   // down on its side
            r.Squash = 0.15;
            _t.Lean = 0;

            // ---- 8. It tries to get up. Thunder finisher.
            _pet.Expression = EyeShape.Squint;
            _t.ArmLeft = _t.ArmRight = 170;
            _talk.Say(this, "This ends now!", 2);
            await Wait(0.5, ct);
            r.Lean = -35 * Facing();
            r.Squash = 0;
            await Wait(0.3, ct);
            for (var i = 0; i < 3; i++)
            {
                _fx.Bolt(Ground(rx + (i - 1) * 0.2 * Size));
                _fx.Flash(0.3);
                await Wait(0.12, ct);
            }
            _fx.Ring(Ground(rx), RivalBeam, 240);
            _fx.Ring(Ground(rx), Colors.White, 160);
            _fx.Sparkle(RivalChest(), 60, 420);
            _fx.Burst(RivalChest(), "K.O.!");
            _fx.HideRival();
            rival = null;
            await Wait(0.5, ct);

            // Victory.
            _t.ArmLeft = _t.ArmRight = 0;
            _t.Yaw = _t.Lean = 0;
            _pet.Expression = EyeShape.Happy;
            _talk.Say(this, "GG. Too easy.", 3);
            _legsByTrick = false;
            await CheerAsync(ct);
        }
        finally
        {
            _fx.HideRival();
            _fx.Held = false;
            _legsByTrick = _walking = _running = false;
            _stride = 0;
            Opacity = 1;
            _pet.WrapFlip();
            _pet.WrapYaw();
            _t.Flip = _t.Yaw = _t.Lean = _t.Squash = _t.ArmLeft = _t.ArmRight = _t.EyeX = 0;
        }
    }
}
