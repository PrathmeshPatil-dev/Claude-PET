using System.Windows;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace ClaudePet;

// Windows as ground. The top edge of an ordinary window is a ledge: thrown, the pet lands on the
// first one it falls onto (or the taskbar); on one, it walks along it, rides it when you drag the
// window, falls when the window goes (or something covers the spot), and sometimes teeters at the
// end. In a full-screen app it ducks out of sight below the screen and peeks up when Claude is done.
public partial class PetWindow
{
    private IntPtr _perchWindow;          // the window it stands on, while _perched
    private Rect _perchRect;              // where that window was last seen, DIPs
    private double _nextRideCheck, _nextCoverCheck;
    private readonly List<(double T, double X, double Y)> _dragTrail = [];
    private bool _peekPending;
    private double _peekAskedAt = double.MinValue;

    // --------------------------------------------------------------- ledges

    // An ordinary app window: visible, on this desktop, titled, not ours, not a tool or
    // always-on-top window, not the desktop or the taskbar.
    private bool IsOrdinaryWindow(IntPtr h)
    {
        if (h == _hwnd || h == new WindowInteropHelper(_talk).Handle || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h))
            return false;
        var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & 0x8) != 0 || Native.GetWindowTextLength(h) == 0)
            return false;   // tool windows, other always-on-top windows, untitled helpers
        return Native.ClassName(h) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    // Ordinary windows front to back with their frames in DIPs.
    private List<(IntPtr H, Rect R)> Windows() =>
        Native.WindowsFrontToBack().Where(IsOrdinaryWindow).Select(h => (h, ToDips(Native.FrameBounds(h)))).ToList();

    // The first window top the feet cross going down from `from` to `to` at `feetX`: wide enough,
    // with head room under the top of the screen, not maximized, and not hidden behind a window in
    // front of it at that spot.
    private (IntPtr H, Rect R)? LedgeBetween(double feetX, double from, double to)
    {
        var wa = WorkArea();
        var windows = Windows();
        (IntPtr H, Rect R)? best = null;
        for (var i = 0; i < windows.Count; i++)
        {
            var (h, r) = windows[i];
            if (Native.IsZoomed(h) || r.Top < from || r.Top > to || r.Top - 8 * Unit < wa.Top || r.Width < Size)
                continue;
            if (feetX < r.Left + 0.2 * Size || feetX > r.Right - 0.2 * Size)
                continue;
            if (windows.Take(i).Any(w => w.R.Contains(new Point(feetX, r.Top + 2))))
                continue;   // that bit of edge is under another window
            if (best == null || r.Top < best.Value.R.Top)
                best = (h, r);
        }
        return best;
    }

    private void PerchOn(IntPtr window, Rect r)
    {
        (_perched, _perchWindow, _perchRect) = (true, window, r);
        _userPlaced = false;
    }

    private void LeavePerch() => (_perched, _perchWindow) = (false, IntPtr.Zero);

    // Riding: the window moved - move with it; it went (closed, minimized, maximized, covered at
    // the feet, or slid out from under them) - fall.
    private void UpdatePerch(double now)
    {
        if (!_perched || _perchWindow == IntPtr.Zero || _dragging || now < _nextRideCheck)
            return;
        _nextRideCheck = now + 0.12;

        var gone = !IsOrdinaryWindow(_perchWindow) || Native.IsZoomed(_perchWindow);
        var r = gone ? Rect.Empty : ToDips(Native.FrameBounds(_perchWindow));
        if (!gone && (r.Left != _perchRect.Left || r.Top != _perchRect.Top))
        {
            _x += r.Left - _perchRect.Left;
            _y = r.Top - FeetFromTop;
            _perchRect = r;
        }
        var feetX = _x + Size / 2;
        var covered = false;
        if (!gone && now >= _nextCoverCheck)   // the whole window list: not every check
        {
            _nextCoverCheck = now + 0.5;
            covered = Windows().TakeWhile(w => w.H != _perchWindow).Any(w => w.R.Contains(new Point(feetX, r.Top + 2)));
        }
        if (gone || covered || feetX < r.Left || feetX > r.Right)
        {
            LeavePerch();
            Do(null);   // the ground is gone: recovering, it falls (FlyAsync) to the next ledge or the taskbar
        }
    }

    // --------------------------------------------------------------- flying and falling

    // Ballistic: thrown (or just falling), bouncing off the screen sides, until it lands on a
    // window top or the taskbar. A hard landing hurts.
    private async Task FlyAsync(double vx, double vy, CancellationToken ct)
    {
        LeavePerch();
        _userPlaced = false;
        _legsByTrick = true;
        var wa = WorkArea();
        var fast = Math.Abs(vx) + Math.Abs(vy) > 900;
        if (fast)
            _pet.Expression = EyeShape.Dizzy;
        var last = _clock.Elapsed.TotalSeconds;
        var spin = vx / 6;   // tumbles with the throw
        try
        {
            while (true)
            {
                await Task.Delay(15, ct);
                var now = _clock.Elapsed.TotalSeconds;
                var dt = Math.Min(now - last, 0.05);
                last = now;

                vy += 2600 * dt;
                vx *= Math.Exp(-0.4 * dt);
                var (nx, ny) = (_x + vx * dt, _y + vy * dt);
                if (nx < wa.Left - Size * 0.2 || nx > wa.Right - Size * 0.8)
                {
                    nx = Math.Clamp(nx, wa.Left - Size * 0.2, wa.Right - Size * 0.8);
                    vx = -0.5 * vx;   // off the screen side
                    spin = -spin;
                }
                if (ny < wa.Top - 6 * Unit)
                    (ny, vy) = (wa.Top - 6 * Unit, 0);   // the top of the screen

                _t.Flip += spin * dt * 10;
                _t.ArmLeft = 150 + 30 * Math.Sin(now * 25);   // flailing
                _t.ArmRight = 150 - 30 * Math.Sin(now * 25);
                for (var i = 0; i < 4; i++)
                    _t.LegLift[i] = 0.5 + 0.4 * Math.Sin(now * 30 + i);

                if (vy > 0 && LedgeBetween(nx + Size / 2, _y + FeetFromTop, ny + FeetFromTop) is { } ledge)
                {
                    (_x, _y) = (nx, ledge.R.Top - FeetFromTop);
                    PerchOn(ledge.H, ledge.R);
                    break;
                }
                if (ny >= FloorY)
                {
                    (_x, _y) = (nx, FloorY);
                    break;
                }
                (_x, _y) = (nx, ny);
            }
        }
        finally
        {
            _legsByTrick = false;
            _t.ArmLeft = _t.ArmRight = 0;
            for (var i = 0; i < 4; i++)
                _t.LegLift[i] = 0;
            _t.Flip = Math.Round(_t.Flip / 360) * 360;   // land on its feet
        }

        _t.Squash = vy > 1300 ? 0.32 : 0.2;   // splat
        await Wait(0.14, ct);
        _t.Squash = 0;
        _pet.WrapFlip();
        if (vy > 1300)
        {
            _talk.Say(this, "Ouch!", 2);
            _pet.Expression = EyeShape.Star;
            await Wait(1, ct);
            await ShakeOffAsync(ct);
        }
        _pet.Expression = EyeShape.Normal;
    }

    // How fast you were swinging it when you let go (DIPs per second); still for a moment = 0.
    private (double Vx, double Vy) ReleaseVelocity()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var recent = _dragTrail.Where(p => now - p.T < 0.12).ToList();
        if (recent.Count < 2 || now - recent[^1].T > 0.08)
            return (0, 0);
        var (a, b) = (recent[0], recent[^1]);
        var dt = Math.Max(b.T - a.T, 0.016);
        return ((b.X - a.X) / dt, (b.Y - a.Y) / dt);
    }

    // --------------------------------------------------------------- life on a window

    // Up on a window: mostly strolls along it, sometimes sits and looks down, teeters at the end,
    // or jumps down.
    private async Task PerchedAsync(CancellationToken ct)
    {
        var r = _perchRect;
        var u = Unit;
        var (minX, maxX) = (r.Left - Size / 2 + 5 * u, r.Right - Size / 2 - 5 * u);
        var roll = _rng.NextDouble();
        if (roll < 0.45 && maxX > minX)
        {
            await WalkToAsync(minX + _rng.NextDouble() * (maxX - minX), ct);
        }
        else if (roll < 0.62)
        {
            await Animate(0.4, k => _sit = k, ct);
            await LookAsync(0, -0.5, 1.5, ct);
            await LookAsync(0.5, -0.3, 1, ct);
            await Animate(0.35, k => _sit = 1 - k, ct);
        }
        else if (roll < 0.82)
        {
            await TeeterAsync(ct);
        }
        else
        {
            await JumpDownAsync(ct);
        }
    }

    // Walks out to the end of the window, wobbles over the drop, then scrambles back - or slips off.
    private async Task TeeterAsync(CancellationToken ct)
    {
        var r = _perchRect;
        var u = Unit;
        var side = _rng.Next(2) == 0 ? -1 : 1;
        var edge = side < 0 ? r.Left - Size / 2 + 2 * u : r.Right - Size / 2 - 2 * u;   // half over the drop
        await WalkToAsync(edge, ct, turnBack: false);

        _pet.Expression = EyeShape.Squint;
        _t.EyeY = -0.5;   // looking down at the drop
        for (var i = 0; i < 4; i++)
            await Animate(0.3, k =>
            {
                _t.Lean = -side * 22 * Math.Sin(Math.PI * k);
                _t.ArmLeft = 90 + 70 * Math.Sin(2 * Math.PI * k);   // windmilling
                _t.ArmRight = 90 - 70 * Math.Sin(2 * Math.PI * k);
            }, ct);

        if (_rng.NextDouble() < 0.55)
        {
            _t.Lean = side * 10;   // throws its weight back and scrambles to safety
            await WalkToAsync(edge - side * 5 * u, ct, speed: 160);
            _t.Lean = _t.ArmLeft = _t.ArmRight = 0;
            _t.EyeY = 0;
            _pet.Expression = EyeShape.Happy;
            _talk.Say(this, "Phew!", 1.5);
            await Wait(0.8, ct);
            _pet.Expression = EyeShape.Normal;
            return;
        }

        _talk.Say(this, "Whoa!", 1.5);
        _t.Lean = _t.EyeY = 0;
        await FlyAsync(side * 120, -200, ct);   // over the edge
    }

    // --------------------------------------------------------------- full screen

    // A full-screen app (video, game, presentation) in front on the pet's screen.
    private bool FullscreenInFront()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || Native.ClassName(h) is "Progman" or "WorkerW")
            return false;
        Native.GetWindowThreadProcessId(h, out var owner);
        if (owner == Environment.ProcessId)
            return false;   // its own windows (the snip screen covers everything)
        var screen = Screen.FromHandle(_hwnd);
        if (Screen.FromHandle(h).DeviceName != screen.DeviceName)
            return false;
        var r = Native.FrameBounds(h);
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // Claude finished a reply: peek, if it's hiding from a full-screen app.
    private void AskForPeek()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _peekAskedAt > 5)   // one reply can end on more than one transcript line
            _peekPending = true;
        _peekAskedAt = now;
    }

    // Ducks below the bottom of the screen while the full-screen app is in front; when Claude is
    // done it peeks up, waves, and ducks again. Back on the floor when the app leaves full screen.
    private async Task HideForFullscreenAsync(CancellationToken ct)
    {
        LeavePerch();
        var bottom = Screen.FromHandle(_hwnd).Bounds.Bottom / _scale;
        var (start, hidden, peek) = (_y, bottom + 4, bottom - 10 * Unit);   // peek: head and eyes over the edge
        _peekPending = false;
        await Animate(0.4, k => _y = start + (hidden - start) * k, ct);
        while (FullscreenInFront())
        {
            if (_peekPending)
            {
                _peekPending = false;
                await Animate(0.35, k => _y = hidden + (peek - hidden) * k, ct);
                _talk.Say(this, "claude's done", 3);
                await PeekWaveAsync(true, ct);
                await Wait(1.2, ct);
                await Animate(0.35, k => _y = peek + (hidden - peek) * k, ct);
            }
            await Wait(0.4, ct);
        }
        await Animate(0.4, k => _y = hidden + (FloorY - hidden) * k, ct);
    }
}
