using System.Windows;

namespace ClaudePet;

// Eyes and moods. Between tricks the eyes hold a spot and jump to the next (with a one-pixel
// jitter while holding, and a blink riding on a big look). While a snap session works they roll
// up thinking, or dart along the lines while Claude reads files. Under the mouse it looks up and
// blushes, then fades so it never blocks what's behind. When you're away it dozes off.
public partial class PetWindow
{
    private double _gazeNext, _jitterNext, _gazeX, _gazeY, _jitterX, _jitterY;
    private bool _gazing, _blinking;

    // What the snap session is doing, from its transcript (RelayClaudeAsync).
    private enum ClaudeMood { None, Thinking, Reading }
    private ClaudeMood _mood;
    private double _moodAt;

    private double? _hoverSince;
    private double _fade = 1;
    private bool _hoverFace;

    private void UpdateEyes(double now)
    {
        if (_mood != ClaudeMood.None && now - _moodAt > 90)
            _mood = ClaudeMood.None;   // the session went quiet

        if (!_idle || _attentive || _chasing || _dragging)
        {
            if (_gazing)
            {
                _gazing = false;
                _t.EyeX = _t.EyeY = 0;
            }
            return;
        }
        _gazing = true;

        var px = 1.0 / _settings.PixelSize;   // one screen pixel, in sprite pixels: keeps the eyes crisp
        if (now >= _jitterNext)
        {
            (_jitterX, _jitterY) = (_rng.Next(-1, 2) * px, _rng.Next(-1, 2) * px);
            _jitterNext = now + 0.25 + 0.35 * _rng.NextDouble();
        }

        switch (_mood)
        {
            case ClaudeMood.Reading:   // along the lines and back, looking down at the page
                _t.EyeX = (int)(now / 0.32) % 2 == 0 ? -0.5 : 0.5;
                _t.EyeY = -0.3;
                _t.Pitch = -6;
                _t.Yaw = 0;
                return;
            case ClaudeMood.Thinking:   // eyes rolled up and aside
                _t.EyeX = 0.35 + _jitterX;
                _t.EyeY = 0.5 + _jitterY;
                _t.Pitch = 8;
                _t.Yaw = 6;
                return;
        }

        if (now >= _gazeNext)
        {
            var big = _rng.NextDouble() < 0.35;
            var (x, y) = big
                ? ((_rng.Next(2) == 0 ? -1 : 1) * (0.45 + 0.15 * _rng.NextDouble()), -0.15 + 0.4 * _rng.NextDouble())
                : _rng.NextDouble() < 0.4 ? (0, 0) : (-0.2 + 0.4 * _rng.NextDouble(), -0.1 + 0.25 * _rng.NextDouble());
            if (Math.Abs(x - _gazeX) > 0.35)
                Blink();   // a big look takes a blink with it
            (_gazeX, _gazeY) = (x, y);
            _gazeNext = now + (big ? 0.6 + 0.8 * _rng.NextDouble() : 1.2 + 2 * _rng.NextDouble());
            _t.Yaw = big ? 12 * Math.Sign(x) : 0;   // the head follows a big look a little
            _t.Pitch = 0;
        }
        _t.EyeX = _gazeX + _jitterX;
        _t.EyeY = _gazeY + _jitterY;
    }

    // Under the mouse: looks up at it and blushes; after a moment it fades to 20%.
    private void UpdateHover(double now, double dt)
    {
        var hovering = _hoverSince != null && !_dragging;
        var want = hovering && now - _hoverSince > 1.2 ? 0.2 : 1;
        _fade += (want - _fade) * (1 - Math.Exp(-8 * dt));
        if (Math.Abs(_fade - want) < 0.005)
            _fade = want;
        ((UIElement)Content).Opacity = _fade;
        _pet.Blush = hovering;

        if (hovering && _idle && !_chasing)
        {
            _t.EyeX = 0;
            _t.EyeY = 0.5;
            _t.Pitch = 12;
            if (!_hoverFace && _pet.Expression == EyeShape.Normal)
            {
                _pet.Expression = EyeShape.Happy;
                _hoverFace = true;
            }
        }
        else if (_hoverFace)
        {
            _hoverFace = false;
            if (_pet.Expression == EyeShape.Happy)
                _pet.Expression = EyeShape.Normal;
            _t.Pitch = 0;
        }
    }

    private bool Hovering => _hoverSince != null || _fade < 1;

    private void Blink()
    {
        if (!_blinking)
            _ = BlinkOnceAsync();
    }

    private async Task BlinkOnceAsync()
    {
        _blinking = true;
        try
        {
            await Animate(0.06, k => _pet.EyeOpen = 1 - k, CancellationToken.None);
            await Animate(0.1, k => _pet.EyeOpen = k, CancellationToken.None);
        }
        finally
        {
            _blinking = false;
        }
    }

    // You've been away (no keyboard or mouse for a while): it sits, its lids droop and it nods; away
    // long enough, it's fast asleep with Z's. Any input wakes it: a start, up, and a stretch.
    private async Task DozeAsync(CancellationToken ct)
    {
        await Animate(0.6, k => _sit = k, ct);
        using var dreams = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? zzz = null;
        try
        {
            while (Native.IdleSeconds() >= 1)
            {
                if (Native.IdleSeconds() < 180)
                {
                    await Animate(1.6, k =>   // a slow nod
                    {
                        _pet.Drowsy = 0.55 + 0.3 * Math.Sin(Math.PI * k);
                        _t.Pitch = -7 * Math.Sin(Math.PI * k);
                    }, ct);
                    continue;
                }
                if (zzz == null)
                {
                    _pet.Drowsy = 0;
                    _pet.Expression = EyeShape.Sleepy;
                    _t.Pitch = -6;
                    zzz = ZzzAsync(dreams.Token);
                }
                await Wait(0.5, ct);
            }
        }
        finally
        {
            dreams.Cancel();
            _zzz.Opacity = 0;
            _pet.Drowsy = 0;
        }
        if (zzz != null)
            try { await zzz; } catch (OperationCanceledException) { }

        _pet.Expression = EyeShape.Normal;
        _t.Pitch = 0;
        _hop = 0.6;   // oh! you're back
        await Wait(0.18, ct);
        _hop = 0;
        await Animate(0.35, k => _sit = 1 - k, ct);
        await StretchAsync(ct);
    }
}
