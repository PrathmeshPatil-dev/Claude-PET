using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;
using Screen = System.Windows.Forms.Screen;
using SystemInformation = System.Windows.Forms.SystemInformation;

namespace ClaudePet;

// The pet: a small transparent always-on-top window holding the 3D mascot. It lives on its own
// (walks, looks around, waves, turns to watch the screen, hides and peeks) and does what it's told.
//
// Tricks only move the mascot's *target* pose; the mascot eases toward it every frame, so motion
// stays smooth even when a trick is interrupted half way.
public partial class PetWindow : Window
{
    // The view is 16 x 16 sprite pixels; its top edge sits at y = 12 so every sprite edge lands on
    // a whole screen pixel. Feet (y = -2) are 14 sprite pixels from the top, the shadow below them.
    private const double ViewUnits = 16, ViewTopY = 12, FeetUnits = 14;
    private const double WalkSpeed = 70;   // DIPs per second at full stride
    private const int HotkeyId = 1, ToggleHotkeyId = 2, ClaudeHotkeyId = 3;

    private readonly Mascot3D _pet;
    private readonly Mascot3D.Pose _t;
    private readonly Settings _settings;
    private readonly PetActions _actions;
    private readonly TalkWindow _talk = new();
    private readonly SnapHistory _snaps = new(SnapHistory.DefaultFile);
    private readonly Overlay _fx = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Tween> _tweens = [];
    private readonly Ellipse _shadow;
    private readonly TextBlock _zzz = new()
    {
        Text = "z",
        FontFamily = new FontFamily("Consolas"),
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Mascot3D.Clay),   // reads on dark and light backgrounds
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private CancellationTokenSource _act = new();
    private IntPtr _hwnd;
    private double _x, _y;                  // window top-left, DIPs
    private double _scale = 1;              // DPI scale
    private double _now, _walkPhase, _stride, _hop, _sway;
    private bool _walking, _running, _peeking, _dragging, _legsByTrick, _perched, _userPlaced;
    private double _sit;                    // 0 standing .. 1 sitting

    // Attention: the pointer inside its orbit gets watched, and (when it may roam) followed.
    private bool _idle, _attentive, _chasing;
    private double _lastNear = double.MinValue, _vx, _vy, _lastHello = -30;
    private Point _peekReturn;

    // The frame loop runs on a timer, not CompositionTarget.Rendering (which redraws the whole
    // transparent window at the screen's full rate for as long as anyone listens): full speed while
    // something moves, a slow tick while it only breathes and sways.
    private static readonly TimeSpan FastFrame = TimeSpan.FromMilliseconds(15), IdleFrame = TimeSpan.FromMilliseconds(50);
    private readonly DispatcherTimer _frames = new(DispatcherPriority.Render) { Interval = FastFrame };
    private double _busyUntil;
    private (int X, int Y) _placed;

    private sealed record Tween(double Start, double Seconds, Action<double> Step, TaskCompletionSource Done, CancellationToken Ct, bool Linear);

    public PetWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _actions = new PetActions(settings);
        _pet = new Mascot3D(settings.PixelSize);
        _t = _pet.Target;

        _shadow = new Ellipse
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new RadialGradientBrush(Color.FromArgb(90, 40, 20, 10), Color.FromArgb(0, 40, 20, 10)),
        };
        var viewport = Mascot3D.CreateViewport(_pet, ViewTopY - ViewUnits / 2, ViewUnits);
        Content = new Grid { Children = { _shadow, viewport, _zzz } };

        _talk.CommandEntered += OnCommand;
        _talk.Dismissed += () => _answer = null;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, e) => Resize(e.NewDpi.DpiScaleX);
        Loaded += (_, _) => Start();
        Closed += (_, _) =>
        {
            Native.UnregisterHotKey(_hwnd, HotkeyId);
            Native.UnregisterHotKey(_hwnd, ToggleHotkeyId);
            Native.UnregisterHotKey(_hwnd, ClaudeHotkeyId);
        };
        PreviewMouseLeftButtonDown += OnMouseDown;
        MouseEnter += (_, _) => _hoverSince ??= _clock.Elapsed.TotalSeconds;
        MouseLeave += (_, _) => _hoverSince = null;
        LocationChanged += (_, _) =>
        {
            if (!_dragging)
                return;
            _dragTrail.Add((_clock.Elapsed.TotalSeconds, Left, Top));   // for the throw on release
            if (_dragTrail.Count > 12)
                _dragTrail.RemoveAt(0);
        };
        MouseRightButtonUp += (_, _) => Talk();
    }

    public bool Roaming => _settings.Roam;

    private double Unit => _settings.PixelSize / _scale;   // one sprite pixel, in DIPs
    private double FeetFromTop => FeetUnits * Unit;

    // The window's side, from the pixel size. Not Width/Height: those still hold WPF's default
    // (3/4 of the screen) until the first layout pass, which once put the pet ~500 px off.
    private double Size => ViewUnits * Unit;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // No Alt+Tab entry, and clicking the pet never takes focus from the window you're working in.
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        Resize(VisualTreeHelper.GetDpi(this).DpiScaleX);

        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        if (!Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE))
            Dispatcher.BeginInvoke(() => _talk.Say(this, "Ctrl+Alt+Space is taken by another app; click me to talk instead.", 6));
        if (!Native.RegisterHotKey(_hwnd, ToggleHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_P))
            Dispatcher.BeginInvoke(() => _talk.Say(this, "Ctrl+Alt+P is taken by another app; use the tray icon to hide me.", 6));
        if (!Native.RegisterHotKey(_hwnd, ClaudeHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_C))
            Dispatcher.BeginInvoke(() => _talk.Say(this, "Ctrl+Alt+C is taken by another app; say \"snap\" instead.", 6));
    }

    // A whole number of screen pixels per sprite pixel, whatever the DPI: that's what keeps it crisp.
    private void Resize(double scale)
    {
        _scale = scale;
        Width = Height = ViewUnits * Unit;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Talk();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            ToggleVisible();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == ClaudeHotkeyId)
        {
            if (IsVisible)   // only while the pet is out
                _ = ClaudeMenuAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Start()
    {
        var wa = WorkArea();
        _x = (_settings.FeetX ?? wa.Right - 60) - Size / 2;
        _y = (_settings.FeetY ?? wa.Bottom) - FeetFromTop;
        if (!OnAnyScreen())   // saved spot is on a monitor that's gone
            (_x, _y) = (wa.Right - 60 - Size / 2, wa.Bottom - FeetFromTop);
        Place();

        _frames.Tick += OnFrame;
        _frames.Start();
        _ = BlinkAsync();
        Do(WaveAsync);
        _talk.Say(this, "hi! right-click me or Ctrl+Alt+Space to talk", 5);
    }

    // One SetWindowPos per frame, on whole pixels (setting Left then Top moves it twice and judders).
    private void Place() =>
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(_x * _scale), (int)Math.Round(_y * _scale), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    // ---------------------------------------------------------------- talking

    public void Talk()
    {
        if (!IsVisible)
            ToggleVisible();
        _talk.Ask(this);
    }

    public bool IsShown => IsVisible;

    // Ctrl+Alt+P: off stops it completely (nothing animates while hidden); on, it comes back waving.
    // The window keeps its handle while hidden, so both hotkeys keep working.
    public void ToggleVisible()
    {
        if (IsVisible)
        {
            _act.Cancel();
            _talk.Hide();
            SavePosition();
            Hide();
        }
        else
        {
            Show();
            Place();
            Do(WaveAsync);
        }
    }

    private async void OnCommand(string text)
    {
        if (_answer != null)
        {
            var answer = _answer;   // the bubble asked a question: this is the reply, not a command
            _answer = null;
            answer(text);
            return;
        }

        var cmd = CommandParser.Parse(text);
        try
        {
            if (cmd.Kind == PetCommandKind.AskClaude)
            {
                await AskClaudeAsync(cmd);   // does its own talking
                return;
            }

            var reply = cmd.Kind == PetCommandKind.Screenshot ? await ScreenshotAsync() : Execute(cmd);
            if (reply != null)
                _talk.Say(this, reply, cmd.Kind == PetCommandKind.Help ? 12 : 4);
            else
                _talk.Hide();   // a trick needs no words, and the bubble would cover it
        }
        catch (Exception ex)
        {
            _talk.Say(this, $"Oops: {ex.Message}", 6);
        }
    }

    private string? Execute(PetCommand cmd)
    {
        switch (cmd.Kind)
        {
            case PetCommandKind.Help: return HelpText;
            case PetCommandKind.Quit: System.Windows.Application.Current.Shutdown(); return null;
            case PetCommandKind.Stay: SetRoaming(false); return "Okay, I'll stay right here.";
            case PetCommandKind.Roam: SetRoaming(true); return "Off I go!";
            case PetCommandKind.Wave: Do(WaveAsync); return "Hi there!";
            case PetCommandKind.TurnAround: Do(WatchScreenAsync); return "Ooh, what are we watching?";
            case PetCommandKind.Cheer: Do(CheerAsync); return "Yay!";
            case PetCommandKind.Hide: Do(HideAndPeekAsync); return "You can't see me...";
            case PetCommandKind.PeekWindow:
                if (FrontWindow() == IntPtr.Zero) return "No window to hide behind (maximized ones don't count).";
                Do(PeekBehindWindowAsync);
                return "Peekaboo!";
            case PetCommandKind.Dash: Do(DashAsync); return "Zoom!";
            case PetCommandKind.Gun: Do(GunAsync); return null;
            case PetCommandKind.Fight: Do(FightAsync); return null;
            case PetCommandKind.SnapHistory: return SnapHistoryText();
            case PetCommandKind.Draw: Do(DrawAsync); return "Art time! (say \"stop drawing\" to clean up)";
            case PetCommandKind.Summon:
                var count = int.TryParse(cmd.Target, out var n) ? Math.Clamp(n, 1, 50) : 20 + _rng.Next(11);
                Do(ct => SummonAsync(count, ct));
                return null;
            case PetCommandKind.Follow: Do(FollowAsync); return "Right behind you! (say \"stop following\")";
            case PetCommandKind.Climb:
                Do(ClimbAsync);
                return "Up I go!";
            case PetCommandKind.Tour: Do(TourAsync); return "Grand tour!";
            case PetCommandKind.Sit: Do(ct => SitAsync(6, ct)); return null;
            case PetCommandKind.Sleep: Do(SleepAsync); return "Just five minutes...";
            case PetCommandKind.Backflip: Do(ct => FlipAsync(1, ct)); return null;
            case PetCommandKind.Frontflip: Do(ct => FlipAsync(-1, ct)); return null;
            case PetCommandKind.Spin: Do(SpinAsync); return null;
            case PetCommandKind.Dance: Do(DanceAsync); return "\u266A";
            case PetCommandKind.Stretch: Do(StretchAsync); return null;
            case PetCommandKind.Trip: Do(TripAsync); return null;
            case PetCommandKind.LookUp: Do(ct => LookAsync(0, 0.5, 2, ct)); return null;
            case PetCommandKind.LookDown: Do(ct => LookAsync(0, -0.5, 2, ct)); return null;
            case PetCommandKind.LookAround: Do(LookAroundAsync); return null;
            case PetCommandKind.Terminal: Do(HopAsync); return _actions.Terminal(cmd.Target);
            case PetCommandKind.VsCode: Do(HopAsync); return _actions.VsCode(cmd.Target);
            case PetCommandKind.Open: Do(HopAsync); return _actions.Open(cmd.Target);
            case PetCommandKind.Snip: return _actions.Snip();
            case PetCommandKind.Lock: return _actions.Lock();
            default: return "I don't know that one yet. Type \"help\" to see what I can do.";
        }
    }

    private const string HelpText =
        "open terminal [in this folder / mes3 / a path]\n" +
        "open vs code [in ...]  ·  open antigravity\n" +
        "open mes dev / mes local  ·  open folder downloads\n" +
        "take a screenshot  ·  snip  ·  lock the screen\n" +
        "stay here  ·  go play  ·  wave  ·  turn around\n" +
        "cheer  ·  hide  ·  peek  ·  look up / down / around\n" +
        "dash  ·  climb  ·  tour  ·  sit  ·  sleep  ·  backflip  ·  front flip\n" +
        "spin  ·  dance  ·  stretch  ·  trip\n" +
        "follow me  ·  stop following  ·  gun\n" +
        "draw  ·  stop drawing  ·  summon [20 / 30]  ·  fight\n" +
        "snap [in mes3]  (drag to pick, then type: /lesgo + it -> Claude)  ·  snap history\n" +
        "Ctrl+Alt+C  Claude or Snap menu, then folder, then your text\n" +
        "Names live in %APPDATA%\\ClaudePet\\settings.json";

    private async Task<string> ScreenshotAsync() => $"Saved {await CaptureScreenAsync()}";

    private async Task<string> CaptureScreenAsync(string? dir = null)
    {
        _talk.Hide();
        Visibility = Visibility.Hidden;
        await Task.Delay(250);   // let the pet actually leave the screen
        try
        {
            return _actions.CaptureScreen(dir);
        }
        finally
        {
            Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- Claude

    private Action<string>? _answer;               // set while the bubble waits for a reply to its question
    private CancellationTokenSource _claudeWatch = new();

    // "snap": screenshot, you drag to pick the part to send, the bubble asks what to do, then a
    // Claude Code session in the code folder starts on /lesgo + that + the picture. "snap: question"
    // skips the asking.
    private async Task AskClaudeAsync(PetCommand cmd)
    {
        var (folder, problem) = _actions.ClaudeFolder(cmd.Target);
        if (folder == null)
        {
            _talk.Say(this, problem!, 5);
            return;
        }

        await SnapAsync(folder, cmd.Text);
    }

    // Screenshot, drag to pick the part, then (unless `question` is given) the bubble asks what to do.
    private async Task SnapAsync(string folder, string question)
    {
        var shot = await CaptureScreenAsync(PetActions.SnapsFolder);
        var screens = SystemInformation.VirtualScreen;   // real pixels; the snip window is in DIPs
        var area = await SnipWindow.SelectAsync(shot,
            new Rect(screens.Left / _scale, screens.Top / _scale, screens.Width / _scale, screens.Height / _scale));
        if (area is not { } part)
        {
            File.Delete(shot);   // nothing goes to Claude
            _talk.Say(this, "snap cancelled", 3);
            return;
        }
        SnipWindow.Crop(shot, part);

        if (question.Length > 0)
        {
            StartClaude(folder, question, shot);
            return;
        }
        _answer = typed => StartClaude(folder, typed, shot);
        _talk.Ask(this, "snap ok · prompt (⏎ to skip):", skippable: true);
    }

    // Ctrl+Alt+C: Claude or Snap, then the folder, then the usual flow.
    private async Task ClaudeMenuAsync()
    {
        if (_answer != null)
            return;   // already asking something
        _talk.Hide();
        var mode = await ChoiceWindow.PickAsync(this, "pet ›",
        [
            ("claude", "new session"),
            ("snap", "screenshot + area"),
        ]);
        if (mode == null)
            return;

        var folder = await PickFolderAsync();
        if (folder == null)
            return;

        if (mode == 1)
        {
            await SnapAsync(folder, "");
            return;
        }
        _answer = typed => StartClaude(folder, typed, null);
        var name = Path.GetFileName(folder.TrimEnd('\\'));   // "" for a drive root
        _talk.Ask(this, $"claude @ {(name.Length > 0 ? name : folder)} · prompt (⏎ to skip):", skippable: true);
    }

    // Last used first, then the folder open in File Explorer, your named folders, every drive, and
    // Browse. Picking one opens it: use it, go up, or go into a subfolder.
    private async Task<string?> PickFolderAsync()
    {
        var folders = new List<(string Label, string Path)>();
        void Offer(string label, string? path)
        {
            if (path != null && Directory.Exists(path) && !folders.Exists(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                folders.Add((label, path));
        }
        Offer("Last used", _settings.LastClaudeFolder);
        Offer("Open in File Explorer", PetActions.FrontMostExplorerFolder());
        foreach (var (name, path) in _settings.Folders)
            Offer(name, path);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            Offer(drive.VolumeLabel.Length > 0 ? $"{drive.Name.TrimEnd('\\')} {drive.VolumeLabel}" : $"{drive.Name.TrimEnd('\\')} drive", drive.RootDirectory.FullName);

        var items = folders.Select(f => (f.Label, f.Path)).Append(("browse...", "")).ToList();
        var pick = await ChoiceWindow.PickAsync(this, "cd ›", items);
        if (pick == null)
            return null;
        if (pick < folders.Count)
            return await OpenFolderAsync(folders[pick.Value].Path);

        var browse = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.LastClaudeFolder ?? "" };
        return browse.ShowDialog() == true ? browse.FolderName : null;
    }

    // Inside a folder: use it, go up, or go into one of its subfolders - until one is used.
    private async Task<string?> OpenFolderAsync(string folder)
    {
        while (true)
        {
            var parent = Directory.GetParent(folder)?.FullName;
            var subfolders = PetActions.Subfolders(folder);
            var items = new List<(string, string)> { ("./", "use this folder") };
            if (parent != null)
                items.Add(("../", "up"));
            items.AddRange(subfolders.Select(s => (Path.GetFileName(s) + "/", "")));

            var pick = await ChoiceWindow.PickAsync(this, subfolders.Count == 0 ? $"{folder} (empty)" : folder, items);
            if (pick is not { } i)
                return null;
            if (i == 0)
                return folder;
            folder = parent != null && i == 1 ? parent : subfolders[i - (parent != null ? 2 : 1)];
        }
    }

    private void StartClaude(string folder, string question, string? shot)
    {
        var id = Guid.NewGuid();
        PetActions.StartClaude(folder, id, ClaudeSession.FirstMessage(question, shot));
        _snaps.Add(new SnapHistory.Entry(id, DateTime.Now, folder, question, shot ?? "", []));
        _settings.LastClaudeFolder = folder;
        _settings.Save();
        _talk.Say(this, $"claude @ {folder} · replies show here", 4);
        Do(WatchScreenAsync);

        _claudeWatch.Cancel();   // only the newest session talks through the pet
        _agents.Clear();
        (_mood, _moodAt) = (ClaudeMood.Thinking, _clock.Elapsed.TotalSeconds);
        _claudeWatch = new CancellationTokenSource();
        _ = RelayClaudeAsync(folder, id, _claudeWatch.Token);
    }

    private readonly HashSet<string> _agents = [];   // tool_use ids of helper agents still running

    // One line of the snap session: its mood (thinking, reading), helper agents starting and
    // finishing, and Claude's words in the bubble.
    private void OnClaudeLine(Guid session, ClaudeSession.Line said)
    {
        _moodAt = _clock.Elapsed.TotalSeconds;
        if (said.FromClaude)
        {
            foreach (var (name, toolId) in said.Tools)
                if (ClaudeSession.IsAgent(name))
                    _agents.Add(toolId);
            if (said.Tools.Any(t => ClaudeSession.IsReading(t.Name)))
                _mood = ClaudeMood.Reading;
            else if (said.Tools.Count > 0)
                _mood = ClaudeMood.Thinking;

            if (said.Text != null)
            {
                _snaps.AddReply(session, said.Text);
                var say = ClaudeSession.ForBubble(said.Text);
                _talk.Say(this, say, Math.Clamp(say.Length / 14.0, 5, 25));
            }
            if (said.EndOfTurn)
            {
                _mood = ClaudeMood.None;
                _agents.Clear();
                AskForPeek();   // peeks up if it's hiding from a full-screen app
            }
        }
        else
        {
            foreach (var result in said.Results)
                _agents.Remove(result);
            _mood = ClaudeMood.Thinking;   // your prompt, or tool results coming back: Claude thinks
        }
    }

    // The last few snaps, newest first.
    private string SnapHistoryText()
    {
        var recent = _snaps.Load().OrderByDescending(e => e.At).Take(6).ToList();
        if (recent.Count == 0)
            return "No snaps yet. Say \"snap\".";
        return string.Join("\n", recent.Select(e =>
            $"{e.At:dd MMM HH:mm}  {Path.GetFileName(e.Folder)}: {Short(e.Question)}  ({e.Replies.Count} replies)"));

        static string Short(string s) => s.Length <= 50 ? s : s[..50] + "...";
    }

    // Follows the session's transcript and says each reply in the bubble. Gives up when the session
    // never shows up, or goes quiet for half an hour.
    private async Task RelayClaudeAsync(string folder, Guid id, CancellationToken ct)
    {
        var file = ClaudeSession.TranscriptPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"), folder, id);
        var started = DateTime.Now;
        var heard = DateTime.Now;
        long read = 0;
        var partial = "";
        try
        {
            while (true)
            {
                await Task.Delay(700, ct);
                if (!File.Exists(file))
                {
                    if (DateTime.Now - started > TimeSpan.FromMinutes(10))   // time to answer "trust this folder?"
                    {
                        _talk.Say(this, "Claude hasn't started - check its terminal window.", 6);
                        return;
                    }
                    continue;
                }

                string chunk;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= read)
                    {
                        if (DateTime.Now - heard > TimeSpan.FromMinutes(30))
                            return;
                        continue;
                    }
                    stream.Seek(read, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    chunk = await reader.ReadToEndAsync(ct);
                    read = stream.Length;
                }
                heard = DateTime.Now;

                var lines = (partial + chunk).Split('\n');
                partial = lines[^1];   // a line still being written
                foreach (var line in lines[..^1])
                {
                    if (ClaudeSession.Parse(line) is { } said)
                        OnClaudeLine(id, said);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // a newer session took over, or the pet is closing
        }
    }

    public void SetRoaming(bool roam)
    {
        _settings.Roam = roam;
        SavePosition();
        Do(roam ? WalkSomewhereAsync : null);
    }

    public void Trick(PetCommandKind kind) => OnCommand(kind switch
    {
        PetCommandKind.Wave => "wave",
        PetCommandKind.TurnAround => "turn around",
        PetCommandKind.Cheer => "cheer",
        PetCommandKind.Hide => "hide",
        PetCommandKind.PeekWindow => "peek",
        _ => "look around",
    });

    public void SavePosition()
    {
        if (_peeking)
            return;
        _settings.FeetX = _x + Size / 2;
        _settings.FeetY = _y + FeetFromTop;
        _settings.Save();
    }

    // ---------------------------------------------------------------- dragging

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _act.Cancel();
        _walking = false;
        var before = new Point(Left, Top);
        _t.Squash = -0.08;   // picked up: a little stretch
        _dragTrail.Clear();
        if (_hoverSince != null)
            _hoverSince = _clock.Elapsed.TotalSeconds + 1.5;   // touched: fully visible again for a few seconds
        _dragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // the button was already released
        }
        finally
        {
            _dragging = false;
        }
        (_x, _y) = (Left, Top);

        e.Handled = true;
        if (Math.Abs(Left - before.X) + Math.Abs(Top - before.Y) < 4)
        {
            Poke(e.ClickCount);
            return;
        }

        LeavePerch();
        var (vx, vy) = ReleaseVelocity();
        if (Math.Sqrt(vx * vx + vy * vy) <= 450)
            (vx, vy) = (0, 0);   // put down gently: just drops
        _userPlaced = true;   // so recovering doesn't drop it first; the flight takes over
        Do(ct => FlyAsync(vx, vy, ct));   // never left floating: lands on a window top or the taskbar
    }

    private readonly List<double> _pokes = [];

    // A click looks over at you, a double-click jumps, five pokes in a row annoy it. (Talking is
    // right-click or Ctrl+Alt+Space.)
    private void Poke(int clicks)
    {
        if (clicks >= 2)
        {
            Do(async ct =>
            {
                _pet.Expression = EyeShape.Happy;
                _t.ArmLeft = _t.ArmRight = 160;
                await JumpAsync(2.2, ct);
                _t.ArmLeft = _t.ArmRight = 0;
                await Wait(0.3, ct);
            });
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        _pokes.RemoveAll(t => now - t > 4);
        _pokes.Add(now);
        if (_pokes.Count >= 5)
        {
            _pokes.Clear();
            Do(AnnoyedAsync);
            return;
        }
        string[] words = ["boop!", "hm?", "hey", "again?"];   // it gets less amused each poke
        var word = words[Math.Min(_pokes.Count - 1, words.Length - 1)];
        _talk.Say(this, word, 1.4);   // straight away, on the click; the hop follows the short reset
        Do(async ct =>   // jumps a little and looks over at you
        {
            _t.Yaw = _t.EyeX = _t.EyeY = 0;
            _pet.Expression = _pokes.Count >= 4 ? EyeShape.Squint : EyeShape.Happy;
            _t.Lean = 10;
            await JumpAsync(0.9, ct);
            await Wait(0.6, ct);
            _t.Lean = 0;
            _pet.Expression = EyeShape.Normal;
        });
    }

    private async Task AnnoyedAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Squint;
        _talk.Say(this, "hey! stop poking me", 2.5);
        for (var i = 0; i < 2; i++)   // stomp, stomp
        {
            _t.ArmLeft = _t.ArmRight = -25;
            await JumpAsync(0.5, ct);
            _t.Squash = 0.3;
            await Wait(0.12, ct);
            _t.Squash = 0;
        }
        _t.Yaw = 140;   // turns its back on you, in a huff
        await Wait(1.4, ct);
        _t.Yaw = 0;
        _t.ArmLeft = _t.ArmRight = 0;
        await Wait(0.4, ct);
        _pet.Expression = EyeShape.Normal;
    }

    // ---------------------------------------------------------------- behaviour

    // Interrupt whatever it's doing, do this, then carry on living.
    private void Do(Func<CancellationToken, Task>? trick)
    {
        _act.Cancel();
        var cts = new CancellationTokenSource();
        _act = cts;
        _ = RunAsync(trick, cts.Token);
    }

    private async Task RunAsync(Func<CancellationToken, Task>? trick, CancellationToken ct)
    {
        try
        {
            await RecoverAsync(ct);
            if (trick != null)
                await trick(ct);
            await LifeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // interrupted by the next command or a drag
        }
    }

    private async Task LifeAsync(CancellationToken ct)
    {
        while (true)
        {
            _idle = true;
            try
            {
                await Wait(_settings.Roam ? 1 + _rng.NextDouble() * 2 : 2.5 + _rng.NextDouble() * 3.5, ct);
            }
            finally
            {
                _idle = false;
            }
            if (FullscreenInFront())
            {
                await HideForFullscreenAsync(ct);
                continue;
            }
            if (_settings.Roam && _perched)
            {
                await PerchedAsync(ct);   // up on a window: life up there
                continue;
            }
            if (_mood != ClaudeMood.None)
            {
                if (_settings.Roam)
                    await WalkSomewhereAsync(ct);   // Claude's working: no tricks, but it keeps pacing
                continue;
            }
            if (Native.IdleSeconds() > 45)
            {
                await DozeAsync(ct);   // you're away
                continue;
            }
            if (_settings.Roam && !_userPlaced && _y < FloorY - 2)
            {
                await FlyAsync(0, 0, ct);   // a climb or a jump was interrupted mid-air: fall
                continue;
            }

            await Pick(ct, _settings.Roam
                ?
                [
                    (30, WalkSomewhereAsync), (12, TourAsync), (6, DashAsync), (9, LookAroundAsync), (6, WaveAsync),
                    (6, WatchScreenAsync), (4, CheerAsync), (6, HideAndPeekAsync), (6, PeekBehindWindowAsync),
                    (8, ClimbAsync), (5, c => SitAsync(4 + _rng.NextDouble() * 4, c)), (3, SleepAsync),
                    (3, c => FlipAsync(1, c)), (3, c => FlipAsync(-1, c)), (3, SpinAsync),
                    (3, DanceAsync), (3, StretchAsync), (2, TripAsync),
                ]
                :
                [
                    // Staying: tricks on the spot only.
                    (14, LookAroundAsync), (6, WaveAsync), (6, WatchScreenAsync), (4, CheerAsync),
                    (7, c => SitAsync(4 + _rng.NextDouble() * 4, c)), (4, SleepAsync),
                    (3, c => FlipAsync(1, c)), (3, c => FlipAsync(-1, c)), (3, SpinAsync),
                    (3, DanceAsync), (3, StretchAsync),
                ]);
        }
    }

    private Task Pick(CancellationToken ct, (int Weight, Func<CancellationToken, Task> Trick)[] tricks)
    {
        var roll = _rng.Next(tricks.Sum(t => t.Weight));
        foreach (var (weight, trick) in tricks)
        {
            if (roll < weight)
                return trick(ct);
            roll -= weight;
        }
        return Task.CompletedTask;
    }

    // Back to standing still, facing you, on top of everything - whatever the last trick left behind.
    private async Task RecoverAsync(CancellationToken ct)
    {
        _walking = _running = _legsByTrick = false;
        _zzz.Opacity = 0;
        _sit = 0;
        _pet.Drowsy = 0;
        _pet.Expression = EyeShape.Normal;
        if (_peeking)
        {
            Topmost = true;
            (_x, _y) = (_peekReturn.X, _peekReturn.Y);
            _peeking = false;
        }
        Opacity = 1;
        if (!OnAnyScreen())   // interrupted while hiding off the edge: come back in
        {
            var wa = WorkArea();
            _x = Math.Clamp(_x, wa.Left, wa.Right - Size);
        }
        Place();

        _pet.WrapYaw();
        _pet.WrapFlip();
        _t.Yaw = _t.Flip = 0;
        _t.Pitch = _t.Lean = _t.ArmLeft = _t.ArmRight = _t.EyeX = _t.EyeY = _t.Squash = 0;
        _hop = 0;
        if (_y > FloorY + 2)
            _y = FloorY;   // interrupted while ducking below the screen
        if (!_userPlaced && !_perched && _y < FloorY - 2)
            await FlyAsync(0, 0, ct);   // left in mid-air (drawing, hanging on the pointer, a climb, its window gone): fall
        await Wait(0.35, ct);
    }

    private async Task BlinkAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5 + _rng.NextDouble() * 2.5));   // about every 3-4 s
            for (var times = _rng.NextDouble() < 0.2 ? 2 : 1; times > 0; times--)
            {
                await BlinkOnceAsync();
                await Task.Delay(110);
            }
        }
    }

    // A hop with anticipation: crouch, jump up stretched, land squashed, settle.
    private async Task HopAsync(CancellationToken ct) => await JumpAsync(1.1, ct);

    private async Task JumpAsync(double height, CancellationToken ct)
    {
        _t.Squash = 0.12;
        await Wait(0.09, ct);
        _t.Squash = -0.1;
        await Animate(0.17, k => _hop = height * k, ct);
        await Animate(0.17, k => _hop = height * (1 - k), ct);
        _t.Squash = 0.14;
        await Wait(0.08, ct);
        _t.Squash = 0;
    }

    private async Task WaveAsync(CancellationToken ct)
    {
        _t.ArmRight = 135;
        _t.EyeY = 0.15;
        _pet.Expression = EyeShape.Happy;
        await Wait(0.25, ct);
        for (var i = 0; i < 4; i++)
        {
            await Animate(0.22, k => _t.ArmRight = 135 + 30 * Math.Sin(k * Math.PI * 2), ct);
        }
        _t.ArmRight = 0;
        _t.EyeY = 0;
        _pet.Expression = EyeShape.Normal;
        await Wait(0.3, ct);
    }

    private async Task CheerAsync(CancellationToken ct)
    {
        _t.ArmLeft = _t.ArmRight = 160;
        _t.EyeY = 0.25;
        _pet.Expression = EyeShape.Squint;
        await Wait(0.2, ct);
        for (var i = 0; i < 3; i++)
        {
            _t.ArmLeft = _t.ArmRight = 172;
            await JumpAsync(1.2, ct);
            _t.ArmLeft = _t.ArmRight = 145;
        }
        await Wait(0.2, ct);
        _t.ArmLeft = _t.ArmRight = 0;
        _t.EyeY = 0;
        _pet.Expression = EyeShape.Normal;
        await Wait(0.35, ct);
    }

    // Only on command: stares straight out, rifle held level in both hands, and warns you off the screen.
    private async Task GunAsync(CancellationToken ct)
    {
        _t.Yaw = _t.Pitch = _t.Lean = 0;
        _t.EyeX = _t.EyeY = 0;
        _pet.Expression = EyeShape.White;
        try
        {
            await Wait(0.3, ct);
            _pet.Gun = true;
            _t.ArmLeft = _t.ArmRight = -15;   // both hands down on the rifle
            _t.Squash = 0.1;                  // a little crouch as it shoulders it
            await Wait(0.15, ct);
            _t.Squash = 0;
            _talk.Say(this, "Stay off the screen. Stay away from the screen... or I will find you.", 4.5);
            await Wait(4.2, ct);
            _pet.Gun = false;
            _t.ArmLeft = _t.ArmRight = 0;
            _pet.Expression = EyeShape.Normal;
            await Wait(0.4, ct);
        }
        finally
        {
            _pet.Gun = false;
            _t.ArmLeft = _t.ArmRight = 0;
        }
    }

    // The overlay over this screen, just under the pet.
    private void ShowOverlay()
    {
        _fx.Cover(WorkArea());
        Topmost = false;   // back on top of it
        Topmost = true;
    }

    // Unit-sized shapes to draw, traced as k goes 0 -> 1.
    private static readonly Func<double, Vector>[] Doodles =
    [
        k => new(Math.Cos(2 * Math.PI * k), Math.Sin(2 * Math.PI * k)),                               // circle
        k => new(k * Math.Cos(6 * Math.PI * k), k * Math.Sin(6 * Math.PI * k)),                       // spiral
        k =>                                                                                         // star
        {
            var i = Math.Min((int)(k * 5), 4);
            var f = k * 5 - i;
            static Vector V(int j) => new(Math.Cos((-90 + 144 * j) * Math.PI / 180), Math.Sin((-90 + 144 * j) * Math.PI / 180));
            return V(i) + (V(i + 1) - V(i)) * f;
        },
        k =>                                                                                         // heart
        {
            var a = 2 * Math.PI * k;
            return new(16 * Math.Pow(Math.Sin(a), 3) / 17,
                -(13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a)) / 17);
        },
        k => new(2 * k - 1 - 0.3 * Math.Sin(10 * Math.PI * k), 0.3 * Math.Cos(10 * Math.PI * k)),     // loops
        k => new(2 * k - 1, 0.45 * Math.Sin(6 * Math.PI * k)),                                        // wave
        k => new(Math.Cos(6 * Math.PI * k) * Math.Cos(2 * Math.PI * k), Math.Cos(6 * Math.PI * k) * Math.Sin(2 * Math.PI * k)),   // flower
    ];

    private static readonly Color[] Inks =
    [
        Mascot3D.Clay, Color.FromRgb(0x4F, 0x9D, 0xFF), Color.FromRgb(0x3D, 0xD6, 0x8C), Color.FromRgb(0xFF, 0xC8, 0x3D),
        Color.FromRgb(0xC0, 0x7C, 0xFF), Color.FromRgb(0xFF, 0x5C, 0x8A),
    ];

    // Only on command: flies all over the screen drawing with its right hand - circles, stars,
    // hearts, spirals - until the next command, which fades the drawing away.
    private async Task DrawAsync(CancellationToken ct)
    {
        _fx.Held = true;
        ShowOverlay();
        var wa = WorkArea();
        var u = Unit;
        var hand = new Vector(Size / 2 + 6 * u, 9 * u);   // the right hand, from the window's top-left
        void HandAt(Point p) => (_x, _y) = (p.X - hand.X, p.Y - hand.Y);

        _legsByTrick = true;
        _pet.Expression = EyeShape.Happy;
        try
        {
            for (var n = 0; ; n++)
            {
                var r = 60 + _rng.NextDouble() * 140;
                var c = new Point(
                    wa.Left + 40 + r + _rng.NextDouble() * Math.Max(1, wa.Width - 80 - 2 * r),
                    wa.Top + 100 + r + _rng.NextDouble() * Math.Max(1, wa.Height - 130 - 2 * r));
                var doodle = Doodles[_rng.Next(Doodles.Length)];

                // Pen up: fly over to where the shape starts.
                var from = new Point(_x, _y) + hand;
                var to = c + doodle(0) * r;
                _t.ArmRight = 0;
                _t.EyeX = Math.Clamp((to.X - from.X) / 300, -0.6, 0.6);
                await Animate(0.35 + (to - from).Length / 1400, k => HandAt(from + (to - from) * k), ct);

                // Pen down: trace it.
                _t.ArmRight = 15;
                _fx.PenDown(Inks[n % Inks.Length]);
                await Animate(1.6 + r / 120, k =>
                {
                    var p = c + doodle(k) * r;
                    HandAt(p);
                    _fx.Draw(p);
                    _t.EyeX = Math.Clamp((p.X - c.X) / r, -1, 1) * 0.5;
                    _t.EyeY = -Math.Clamp((p.Y - c.Y) / r, -1, 1) * 0.4;
                }, ct, linear: true);
                _fx.PenUp();
            }
        }
        finally
        {
            _fx.Erase();
            _fx.Held = false;
            _legsByTrick = false;
            _t.ArmRight = _t.EyeX = _t.EyeY = 0;
        }
    }

    // Only on command: gathers magic (arms up, star eyes, sparkles), then summons friends all over
    // the screen; they drop in, play, and poof away on their own.
    private async Task SummonAsync(int count, CancellationToken ct)
    {
        ShowOverlay();
        var wa = WorkArea();
        var u = Unit;
        var hands = new Point(_x + Size / 2, _y + 4 * u);   // above its head, between the raised hands
        _pet.Expression = EyeShape.Star;
        _t.ArmLeft = _t.ArmRight = 165;
        _talk.Say(this, "Abracadabra!", 2.5);
        for (var i = 0; i < 12; i++)
        {
            _fx.Sparkle(hands, 6, 200);
            _t.Squash = i % 2 == 0 ? 0.08 : -0.06;
            await Wait(0.1, ct);
        }
        _t.Squash = 0;
        await JumpAsync(1.4, ct);
        _fx.Sparkle(hands, 40, 420);

        for (var i = 0; i < count; i++)
        {
            ShowOverlay();   // still up (a short summon can empty it in between)
            var at = new Point(wa.Left + 60 + _rng.NextDouble() * (wa.Width - 120), wa.Top + 80 + _rng.NextDouble() * (wa.Height * 0.6));
            _fx.Summon(at, _settings.PixelSize, Size * 0.6, FeetUnits / ViewUnits);
            _t.ArmLeft = _t.ArmRight = i % 2 == 0 ? 175 : 150;
            await Wait(0.06, ct);
        }

        _t.ArmLeft = _t.ArmRight = 0;
        _pet.Expression = EyeShape.Happy;
        await CheerAsync(ct);
    }

    // The full 180: turns its back to you, looks up at the screen with you, cheers, turns back.
    private async Task WatchScreenAsync(CancellationToken ct)
    {
        await Animate(1.0, k => _t.Yaw = 180 * k, ct);
        _t.Pitch = 14;
        await Wait(1.0, ct);
        await Animate(1.2, k => _t.Lean = 7 * Math.Sin(Math.PI * 2 * k), ct);   // head tilt, as if reading
        await Wait(0.5, ct);
        _t.ArmLeft = _t.ArmRight = 160;
        for (var i = 0; i < 3; i++)
        {
            _t.ArmLeft = _t.ArmRight = 172;
            await JumpAsync(1.0, ct);
            _t.ArmLeft = _t.ArmRight = 145;
        }
        await Wait(0.6, ct);
        _t.ArmLeft = _t.ArmRight = 0;
        _t.Pitch = 0;
        await Wait(0.3, ct);
        await Animate(1.0, k => _t.Yaw = 180 + 180 * k, ct);
        _pet.WrapYaw();
        await Wait(0.35, ct);
    }

    private async Task LookAsync(double x, double y, double hold, CancellationToken ct)
    {
        if (Math.Abs(x - _t.EyeX) > 0.4)
            Blink();   // a big look takes a blink with it
        _t.EyeX = x;
        _t.EyeY = y;
        _t.Yaw = 24 * x;
        _t.Pitch = 22 * y;
        await Wait(0.3 + hold, ct);
    }

    private async Task LookAroundAsync(CancellationToken ct)
    {
        for (var i = 2 + _rng.Next(3); i > 0; i--)
            await LookAsync(_rng.Next(-1, 2) * 0.6, _rng.Next(-1, 2) * 0.4, 0.5 + _rng.NextDouble() * 0.8, ct);
        await LookAsync(0, 0, 0.1, ct);
    }

    private Task WalkSomewhereAsync(CancellationToken ct)
    {
        var wa = WorkArea();
        var from = wa.Left - Size * 0.2;
        var to = wa.Right - Size * 0.8;
        return WalkToAsync(from + _rng.NextDouble() * (to - from), ct);
    }

    // Turns a little toward where it's going, walks with eased start and stop, turns back to face you.
    private async Task WalkToAsync(double x, CancellationToken ct, bool turnBack = true, double speed = WalkSpeed)
    {
        var start = _x;
        var dir = Math.Sign(x - start);
        if (dir == 0)
            return;

        _t.Yaw = 35 * dir;
        _t.EyeX = 0.5 * dir;
        await Wait(0.3, ct);
        _walking = true;
        try
        {
            var seconds = Math.Abs(x - start) / speed + 0.4;
            await Animate(seconds, k =>
            {
                _x = start + (x - start) * k;
                _stride = Math.Min(1, Math.Min(k, 1 - k) * 8);   // legs speed up and slow down with the body
            }, ct, linear: false);
        }
        finally
        {
            _walking = false;
            _stride = 0;
        }
        if (turnBack)
        {
            _t.Yaw = 0;
            _t.EyeX = 0;
            await Wait(0.35, ct);
        }
    }

    // Walks off the edge of the screen, waits, peeks back in with one eye, waves, comes back.
    private async Task HideAndPeekAsync(CancellationToken ct)
    {
        var (freeLeft, freeRight) = FreeEdges();
        if (!freeLeft && !freeRight)
            return;   // monitors on both sides: it would just walk onto the next one

        var left = freeLeft && (!freeRight || _rng.Next(2) == 0);
        var wa = WorkArea();
        var home = _x;
        var hidden = left ? wa.Left - Size : wa.Right;
        var peek = left ? wa.Left - Size * 0.6 : wa.Right - Size * 0.4;
        var side = left ? -1 : 1;   // where the edge is

        await WalkToAsync(hidden, ct, turnBack: false);
        await Wait(1.5 + _rng.NextDouble() * 1.5, ct);

        for (var times = _rng.NextDouble() < 0.4 ? 2 : 1; times > 0; times--)
        {
            _t.Yaw = -22 * side;   // face back into the screen
            _t.EyeX = -0.6 * side;
            await Glide(hidden, peek, 0.7, ct);
            await Wait(0.4, ct);
            await LookAsync(-0.6 * side, 0.4, 0.5, ct);
            _t.Yaw = -22 * side;
            await PeekWaveAsync(left, ct);
            await Glide(peek, hidden, 0.4, ct);
            await Wait(1 + _rng.NextDouble(), ct);
        }

        await WalkToAsync(home, ct);
    }

    // Slips behind the front-most window and peeks out from its edge.
    private async Task PeekBehindWindowAsync(CancellationToken ct)
    {
        var target = FrontWindow();
        if (target == IntPtr.Zero)
            return;

        var r = ToDips(Native.FrameBounds(target));
        var wa = WorkArea();
        var roomLeft = r.Left - wa.Left > Size * 0.5;
        var roomRight = wa.Right - r.Right > Size * 0.5;
        if ((!roomLeft && !roomRight) || r.Width < Size * 1.2)
            return;

        var left = roomLeft && (!roomRight || _rng.Next(2) == 0);
        var hidden = left ? r.Left + 2 : r.Right - Size - 2;
        var peek = left ? r.Left - Size * 0.42 : r.Right - Size * 0.58;
        var top = Math.Clamp(r.Top + r.Height * 0.45 - FeetFromTop, wa.Top, wa.Bottom - Size);

        _peekReturn = new Point(_x, _y);
        _peeking = true;
        await Animate(0.25, k => Opacity = 1 - k, ct);

        // Just below that window in z-order: it covers the pet, everything behind it doesn't.
        Topmost = false;
        Native.SetWindowPos(_hwnd, target, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        (_x, _y) = (hidden, top);
        Place();
        _t.Yaw = left ? -20 : 20;
        _t.EyeX = left ? -0.6 : 0.6;
        Opacity = 1;

        for (var times = _rng.NextDouble() < 0.5 ? 2 : 1; times > 0; times--)
        {
            await Wait(0.4, ct);
            await Glide(hidden, peek, 0.6, ct);
            await Wait(0.3, ct);
            await LookAsync(left ? -0.6 : 0.6, 0.3, 0.6, ct);
            await PeekWaveAsync(!left, ct);
            await Glide(peek, hidden, 0.4, ct);
        }

        await Animate(0.2, k => Opacity = 1 - k, ct);
        Topmost = true;
        (_x, _y) = (_peekReturn.X, _peekReturn.Y);
        Place();
        _t.Yaw = 0;
        _t.EyeX = 0;
        _peeking = false;
        await Animate(0.25, k => Opacity = k, ct);
    }

    // ---------------------------------------------------------------- more tricks

    private double FloorY => WorkArea().Bottom - FeetFromTop;   // window top when standing on the taskbar

    // Sprints to the other half of the screen, leaning in, and skids to a stop (sometimes trips).
    private async Task DashAsync(CancellationToken ct)
    {
        var wa = WorkArea();
        var onLeft = _x + Size / 2 < wa.Left + wa.Width / 2;
        var to = onLeft
            ? wa.Right - Size * (0.6 + _rng.NextDouble() * 0.25)
            : wa.Left + Size * (_rng.NextDouble() * 0.25 - 0.4);
        await RunToAsync(to, ct);
        if (_rng.NextDouble() < 0.15)
            await TripAsync(ct);
    }

    private async Task RunToAsync(double x, CancellationToken ct, bool turnBack = true)
    {
        var dir = Math.Sign(x - _x);
        _pet.Expression = EyeShape.Squint;
        _t.Pitch = -10;   // lean into it
        _t.Squash = 0.12;
        await Wait(0.12, ct);
        _t.Squash = 0;
        _running = true;
        try
        {
            await WalkToAsync(x, ct, turnBack: false, speed: 480);
        }
        finally
        {
            _running = false;
        }
        _t.Pitch = 12;   // skid
        _t.Squash = 0.12;
        await Wait(0.22, ct);
        _t.Pitch = 0;
        _t.Squash = 0;
        _pet.Expression = EyeShape.Normal;
        if (turnBack)
        {
            _t.Yaw = 0;
            _t.EyeX = 0;
            await Wait(0.3, ct);
        }
    }

    // Falls flat on its face, sees stars, gets up and shakes it off.
    private async Task TripAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Squint;
        _t.Yaw = _rng.Next(2) == 0 ? 60 : -60;
        await Wait(0.15, ct);
        _t.Pitch = -88;
        _t.ArmLeft = _t.ArmRight = 120;
        await Animate(0.25, k => _hop = -2 * k, ct);
        _t.Squash = 0.15;
        _pet.Expression = EyeShape.Dizzy;
        await Wait(1.1, ct);
        _t.Squash = 0;
        _t.Pitch = 0;
        _t.ArmLeft = _t.ArmRight = 0;
        await Animate(0.3, k => _hop = -2 * (1 - k), ct);
        _pet.Expression = EyeShape.Star;
        await ShakeOffAsync(ct);
    }

    private async Task ShakeOffAsync(CancellationToken ct)
    {
        _t.Yaw = 0;
        for (var i = 0; i < 3; i++)
            await Animate(0.22, k => _t.Lean = 9 * Math.Sin(k * Math.PI * 2), ct);
        _t.Lean = 0;
        await Wait(0.5, ct);
        _pet.Expression = EyeShape.Normal;
    }

    private async Task SitAsync(double seconds, CancellationToken ct)
    {
        await Animate(0.4, k => _sit = k, ct);
        var until = _clock.Elapsed.TotalSeconds + seconds;
        while (_clock.Elapsed.TotalSeconds < until)
            await LookAsync(_rng.Next(-1, 2) * 0.5, _rng.Next(-1, 1) * 0.4, 0.8 + _rng.NextDouble() * 1.5, ct);
        await LookAsync(0, 0, 0, ct);
        await Animate(0.35, k => _sit = 1 - k, ct);
    }

    // Sits down, nods off with floating z's, wakes with a start and stretches.
    private async Task SleepAsync(CancellationToken ct)
    {
        await Animate(0.4, k => _sit = k, ct);
        _pet.Expression = EyeShape.Sleepy;
        _t.Pitch = -6;
        using var dreams = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var zzz = ZzzAsync(dreams.Token);
        await Wait(6 + _rng.NextDouble() * 6, ct);
        dreams.Cancel();
        try { await zzz; } catch (OperationCanceledException) { }
        _zzz.Opacity = 0;

        _pet.Expression = EyeShape.Normal;
        _t.Pitch = 0;
        _hop = 0.6;
        await Wait(0.18, ct);
        _hop = 0;
        await Wait(0.4, ct);
        await Animate(0.35, k => _sit = 1 - k, ct);
        await StretchAsync(ct);
    }

    private async Task ZzzAsync(CancellationToken ct)
    {
        var size = new[] { 0.9, 1.25, 1.6 };
        for (var i = 0; ; i++)
        {
            var u = Unit;
            _zzz.FontSize = size[i % 3] * 3.2 * u;
            var x0 = Size / 2 + 3 * u;
            var y0 = (ViewTopY - 7.5) * u;
            await Animate(1.3, k =>
            {
                _zzz.Opacity = Math.Sin(Math.PI * k);
                _zzz.Margin = new Thickness(x0 + 2.5 * u * k, y0 - 4.5 * u * k, 0, 0);
            }, ct, linear: true);
        }
    }

    private async Task StretchAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Sleepy;
        await Animate(0.6, k => { _t.ArmLeft = _t.ArmRight = 170 * k; _t.Squash = -0.12 * k; _t.Pitch = 8 * k; }, ct);
        await Wait(0.9, ct);
        await Animate(0.45, k => { _t.ArmLeft = _t.ArmRight = 170 * (1 - k); _t.Squash = -0.12 * (1 - k); _t.Pitch = 8 * (1 - k); }, ct);
        _pet.Expression = EyeShape.Normal;
        await Wait(0.2, ct);
    }

    // A full somersault in the air: dir 1 = backflip, -1 = front flip. Turned 3/4 so you see it tumble.
    private async Task FlipAsync(int dir, CancellationToken ct)
    {
        _t.Yaw = _rng.Next(2) == 0 ? 40 : -40;
        _t.Squash = 0.18;   // crouch
        _t.ArmLeft = _t.ArmRight = 40;
        await Wait(0.18, ct);
        _t.Squash = -0.12;
        _t.ArmLeft = _t.ArmRight = 160;
        _pet.Expression = EyeShape.Squint;

        var y0 = _y;
        var height = 11 * Unit;
        await Animate(0.8, k =>
        {
            _y = y0 - height * 4 * k * (1 - k);
            _t.Flip = 360 * dir * EaseInOut(k);
        }, ct, linear: true);
        _y = y0;
        _pet.WrapFlip();

        _t.Squash = 0.2;   // stick the landing
        _t.ArmLeft = _t.ArmRight = 90;
        _pet.Expression = EyeShape.Happy;
        await Wait(0.14, ct);
        _t.Squash = 0;
        await Wait(0.5, ct);
        _t.ArmLeft = _t.ArmRight = 0;
        _t.Yaw = 0;
        _pet.Expression = EyeShape.Normal;
        await Wait(0.3, ct);
    }

    private async Task SpinAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Happy;
        _t.ArmLeft = _t.ArmRight = 35;
        _t.Squash = 0.1;
        await Wait(0.12, ct);
        _t.Squash = -0.06;
        await Animate(0.75, k => { _t.Yaw = 360 * k; _hop = 1.4 * Math.Sin(Math.PI * k); }, ct);
        _pet.WrapYaw();
        _t.Squash = 0.12;
        await Wait(0.12, ct);
        _t.Squash = 0;
        _t.ArmLeft = _t.ArmRight = 0;
        await Wait(0.4, ct);
        _pet.Expression = EyeShape.Normal;
    }

    private async Task DanceAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Happy;
        for (var beat = 0; beat < 8; beat++)
        {
            var side = beat % 2 == 0 ? 1 : -1;
            _t.Lean = 10 * side;
            _t.Yaw = 18 * side;
            _t.ArmLeft = side > 0 ? 150 : 20;
            _t.ArmRight = side > 0 ? 20 : 150;
            await Animate(0.32, k => _hop = 0.7 * Math.Sin(Math.PI * k), ct);
        }
        _t.Lean = _t.Yaw = _t.ArmLeft = _t.ArmRight = 0;
        await Wait(0.3, ct);
        _pet.Expression = EyeShape.Normal;
    }

    // Walks to the front window, climbs its side hand over hand, hops onto the top, strolls, sits
    // and looks down at you, then jumps off. With no window to climb (maximized, or no side free) it
    // climbs the edge of the screen instead, all the way up. About a third of the time it slips half
    // way and falls.
    private async Task ClimbAsync(CancellationToken ct)
    {
        var wa = WorkArea();
        var u = Unit;
        var target = FrontWindow();
        var r = target == IntPtr.Zero ? Rect.Empty : ToDips(Native.FrameBounds(target));
        var roomLeft = !r.IsEmpty && r.Left - wa.Left > 8 * u;
        var roomRight = !r.IsEmpty && wa.Right - r.Right > 8 * u;
        var wall = r.IsEmpty || r.Top < wa.Top + 14 * u || r.Width < Size * 1.3 || (!roomLeft && !roomRight);

        bool left;
        double climbX;
        if (wall)
        {
            left = _x + Size / 2 > wa.Left + wa.Width / 2;   // the nearer screen edge
            climbX = left ? wa.Right - Size / 2 - 5.3 * u : wa.Left - Size / 2 + 5.3 * u;
        }
        else
        {
            left = roomLeft && (!roomRight || _rng.Next(2) == 0);
            climbX = left ? r.Left - Size / 2 - 5.3 * u : r.Right - Size / 2 + 5.3 * u;
        }
        var dir = left ? 1 : -1;   // which way the wall is from the climber

        _userPlaced = false;
        if (Math.Abs(_y - FloorY) > 2)
            await FallToAsync(FloorY, ct);
        if (Math.Abs(climbX - _x) > 450)
            await RunToAsync(climbX, ct, turnBack: false);
        else
            await WalkToAsync(climbX, ct, turnBack: false);

        _t.Yaw = 70 * dir;   // side on to the window, face still showing
        _t.EyeX = 0.6 * dir;
        _t.EyeY = 0.5;
        await Wait(0.35, ct);

        var start = _y;
        var end = wall
            ? wa.Top + 9 * u - FeetFromTop   // head just under the top of the screen
            : r.Top + 4 * u - FeetFromTop;   // hands over the top edge
        var steps = Math.Max(4, (int)Math.Ceiling((start - end) / Math.Max(14, (start - end) / 22)));
        var slipAt = _rng.NextDouble() < 0.33 ? steps / 2 + _rng.Next(Math.Max(1, steps / 3)) : -1;

        _legsByTrick = true;
        try
        {
            for (var i = 0; i < steps; i++)
            {
                var odd = i % 2 == 1;
                _t.ArmLeft = odd ? 165 : 75;   // hand over hand
                _t.ArmRight = odd ? 75 : 165;
                for (var leg = 0; leg < 4; leg++)
                    _t.LegLift[leg] = (leg % 2 == 0) == odd ? 0.7 : 0;
                var (y0, y1) = (_y, start + (end - start) * (i + 1) / steps);
                await Animate(0.26, k => _y = y0 + (y1 - y0) * k, ct);

                if (i == slipAt)
                {
                    await SlipAsync(ct);
                    return;
                }
            }
        }
        finally
        {
            _legsByTrick = false;
        }

        if (wall)
        {
            // Nothing to stand on up here: hang on, take in the view, push off and drop.
            _pet.Expression = EyeShape.Happy;
            _talk.Say(this, "Made it!", 1.5);
            await LookAsync(-0.6 * dir, -0.6, 1.4, ct);
            _t.ArmLeft = _t.ArmRight = 0;
            await JumpDownAsync(ct, toLeft: dir > 0);
            return;
        }

        // Mantle onto the top.
        _t.ArmLeft = _t.ArmRight = 170;
        _pet.Expression = EyeShape.Squint;
        await Wait(0.15, ct);
        var (xa, ya) = (_x, _y);
        var (xb, yb) = (climbX + dir * 10 * u, r.Top - FeetFromTop);
        await Animate(0.42, k =>
        {
            _x = xa + (xb - xa) * k;
            _y = ya + (yb - ya) * k - 3 * u * 4 * k * (1 - k);
        }, ct, linear: true);
        PerchOn(target, r);
        _t.ArmLeft = _t.ArmRight = 0;
        _t.Yaw = _t.EyeX = _t.EyeY = 0;
        _t.Squash = 0.15;
        _pet.Expression = EyeShape.Happy;
        await Wait(0.15, ct);
        _t.Squash = 0;
        await WaveAsync(ct);   // up there now: PerchedAsync walks, sits, teeters or jumps down from here
    }

    // The grand tour of all four corners: along the floor to the nearer bottom corner, up that edge
    // of the screen, hand over hand along the top, down the other edge to the far bottom corner.
    private async Task TourAsync(CancellationToken ct)
    {
        var wa = WorkArea();
        var u = Unit;
        var leftX = wa.Left - Size / 2 + 5.3 * u;   // hugging each edge, as ClimbAsync does
        var rightX = wa.Right - Size / 2 - 5.3 * u;
        var topY = wa.Top + 9 * u - FeetFromTop;   // head just under the top of the screen
        var side = _x + Size / 2 < wa.Left + wa.Width / 2 ? -1 : 1;   // the first wall: -1 left, 1 right
        var (first, second) = side < 0 ? (leftX, rightX) : (rightX, leftX);

        _userPlaced = false;
        if (Math.Abs(_y - FloorY) > 2)
            await FallToAsync(FloorY, ct);
        await WalkToAsync(first, ct, turnBack: false);

        _t.Yaw = 70 * side;   // side on to the wall
        _t.EyeX = 0.6 * side;
        _t.EyeY = 0.5;
        await Wait(0.3, ct);
        await ClimbWallAsync(topY, 0.26, ct);

        // Along the top, hanging by the hands, legs dangling.
        _t.Yaw = -35 * side;
        _t.EyeX = -0.5 * side;
        _t.EyeY = -0.3;
        _legsByTrick = true;
        try
        {
            var span = second - first;
            await Animate(Math.Abs(span) / (WalkSpeed * 1.3), k =>
            {
                _x = first + span * k;
                var swing = Math.Sin(k * Math.Abs(span) / 12);
                _t.ArmLeft = 160 + 15 * swing;
                _t.ArmRight = 160 - 15 * swing;
                for (var leg = 0; leg < 4; leg++)
                    _t.LegLift[leg] = 0.3 + (leg % 2 == 0 ? 0.3 : -0.3) * swing;
            }, ct, linear: true);
        }
        finally
        {
            _legsByTrick = false;
        }

        _t.Yaw = -70 * side;   // the other wall, and down
        _t.EyeX = -0.6 * side;
        _t.EyeY = -0.5;
        await ClimbWallAsync(FloorY, 0.2, ct);

        _t.ArmLeft = _t.ArmRight = 0;
        _t.Yaw = _t.EyeX = _t.EyeY = 0;
        _pet.Expression = EyeShape.Happy;
        await HopAsync(ct);
        _pet.Expression = EyeShape.Normal;
    }

    // Hand over hand up (or down) a screen edge to y1, a step every `stepSeconds`.
    private async Task ClimbWallAsync(double y1, double stepSeconds, CancellationToken ct)
    {
        var start = _y;
        var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(y1 - start) / 40));
        _legsByTrick = true;
        try
        {
            for (var i = 0; i < steps; i++)
            {
                var odd = i % 2 == 1;
                _t.ArmLeft = odd ? 165 : 75;
                _t.ArmRight = odd ? 75 : 165;
                for (var leg = 0; leg < 4; leg++)
                    _t.LegLift[leg] = (leg % 2 == 0) == odd ? 0.7 : 0;
                var (a, b) = (_y, start + (y1 - start) * (i + 1) / steps);
                await Animate(stepSeconds, k => _y = a + (b - a) * k, ct);
            }
        }
        finally
        {
            _legsByTrick = false;
        }
    }

    // Loses its grip: hangs a moment, falls, crashes, sees stars.
    private async Task SlipAsync(CancellationToken ct)
    {
        _pet.Expression = EyeShape.Dizzy;
        _t.Yaw = 0;
        _t.EyeX = _t.EyeY = 0;
        _t.ArmLeft = _t.ArmRight = 170;
        await Wait(0.4, ct);
        _pet.Expression = EyeShape.Dizzy;
        _legsByTrick = false;
        using (var falling = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            _ = Animate(10, k => { _t.ArmLeft = 150 + 25 * Math.Sin(k * 120); _t.ArmRight = 150 - 25 * Math.Sin(k * 120); }, falling.Token, linear: true);
            await FallToAsync(FloorY, ct);
            falling.Cancel();   // stop flailing on impact
        }
        _t.ArmLeft = _t.ArmRight = 0;
        _t.Squash = 0.32;   // splat
        await Wait(0.14, ct);
        _t.Squash = 0;
        _talk.Say(this, "Ouch!", 2);
        await Animate(0.3, k => _sit = k, ct);
        _pet.Expression = EyeShape.Star;
        await Wait(1.2, ct);
        await ShakeOffAsync(ct);
        await Animate(0.35, k => _sit = 1 - k, ct);
    }

    // Gravity: accelerates all the way down.
    private Task FallToAsync(double y1, CancellationToken ct)
    {
        var y0 = _y;
        var seconds = Math.Clamp(Math.Sqrt(Math.Abs(y1 - y0) / 1600), 0.2, 0.9);
        return Animate(seconds, k => _y = y0 + (y1 - y0) * k * k, ct, linear: true);
    }

    // Off the window it's sitting on, down to the taskbar - sometimes with a flip.
    private async Task JumpDownAsync(CancellationToken ct, bool? toLeft = null)
    {
        var wa = WorkArea();
        var u = Unit;
        toLeft ??= _x + Size / 2 < wa.Left + wa.Width / 2;
        var x1 = Math.Clamp(_x + (toLeft.Value ? -16 : 16) * u, wa.Left - Size * 0.2, wa.Right - Size * 0.8);
        var flip = _rng.NextDouble() < 0.35;

        _t.Yaw = toLeft.Value ? -40 : 40;
        _t.Squash = 0.16;
        await Wait(0.16, ct);
        _t.Squash = -0.1;
        _pet.Expression = EyeShape.Squint;

        var (x0, y0, y1) = (_x, _y, FloorY);
        var seconds = Math.Clamp(0.45 + Math.Sqrt(Math.Abs(y1 - y0) / 1600), 0.5, 1.2);
        await Animate(seconds, k =>
        {
            _x = x0 + (x1 - x0) * k;
            _y = y0 + (y1 - y0) * k * k - 4 * u * 4 * k * (1 - k);
            if (flip)
                _t.Flip = -360 * EaseInOut(k);
        }, ct, linear: true);
        _pet.WrapFlip();
        LeavePerch();

        _t.Squash = 0.22;
        _pet.Expression = EyeShape.Happy;
        await Wait(0.14, ct);
        _t.Squash = 0;
        _t.Yaw = 0;
        await Wait(0.5, ct);
        _pet.Expression = EyeShape.Normal;
    }

    private Task Glide(double from, double to, double seconds, CancellationToken ct) =>
        Animate(seconds, k => _x = from + (to - from) * k, ct);

    // Waves the arm that's sticking out of the hiding place.
    private async Task PeekWaveAsync(bool rightArm, CancellationToken ct)
    {
        void Arm(double a)
        {
            if (rightArm) _t.ArmRight = a;
            else _t.ArmLeft = a;
        }

        Arm(130);
        await Wait(0.2, ct);
        for (var i = 0; i < 3; i++)
            await Animate(0.2, k => Arm(130 + 25 * Math.Sin(k * Math.PI * 2)), ct);
        Arm(0);
        await Wait(0.25, ct);
    }

    // ---------------------------------------------------------------- frame loop

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible)
            return;   // switched off

        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _now, 0.1);
        _now = now;

        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var t = _tweens[i];
            if (t.Ct.IsCancellationRequested)
            {
                _tweens.RemoveAt(i);
                t.Done.TrySetCanceled(t.Ct);
                continue;
            }

            var p = Math.Clamp((now - t.Start) / t.Seconds, 0, 1);
            t.Step(t.Linear ? p : EaseInOut(p));
            if (p >= 1)
            {
                _tweens.RemoveAt(i);
                t.Done.TrySetResult();
            }
        }

        UpdateAttention(now, dt);
        UpdateEyes(now);
        UpdateHover(now, dt);
        UpdatePerch(now);
        if (_agents.Count > 0 || _fx.Orbiting)
        {
            if (!_fx.IsVisible)
                ShowOverlay();
            _fx.Orbit(_agents.Count, new Point(_x + Size / 2, _y + 6 * Unit), Size * 0.4, _settings.PixelSize, dt);
        }

        if (_walking)
        {
            var (speed, lift) = _running ? (24.0, 0.8) : (9.0, 0.55);
            _walkPhase += dt * speed * Math.Max(_stride, 0.3);
            for (var i = 0; i < 4; i++)
                _t.LegLift[i] = Math.Max(0, Math.Sin(_walkPhase + (i % 2) * Math.PI)) * lift * _stride;
            _t.Lean = Math.Sin(_walkPhase) * (_running ? 1.5 : 2.5) * _stride;
            _t.Bob = _hop + Math.Abs(Math.Sin(_walkPhase)) * (_running ? 0.45 : 0.3) * _stride;
        }
        else
        {
            // Sitting folds the legs away: the body drops, the legs stay on the ground.
            if (!_legsByTrick)
                for (var i = 0; i < 4; i++)
                    _t.LegLift[i] = 1.6 * _sit;
            _t.Bob = _hop - 1.6 * _sit + Math.Sin(now * (_sit > 0.5 ? 1.4 : 2.1)) * 0.1;   // breathing
        }

        // A slow idle sway, so you always see it's a solid thing, not a flat sprite.
        _sway += ((_walking || _peeking || _legsByTrick ? 0 : 1) - _sway) * (1 - Math.Exp(-2 * dt));
        _pet.Sway = Math.Sin(now * 0.7) * 9 * _sway;
        _pet.Step(dt);
        _fx.Step(dt);
        UpdateShadow();
        var at = ((int)Math.Round(_x * _scale), (int)Math.Round(_y * _scale));
        if (!_dragging && at != _placed)
        {
            _placed = at;
            Place();   // only when it actually moved
        }
        if (_walking)
            _talk.Follow();

        // Full speed while anything moves (tweens, walking, following, a trick posing it, effects on
        // screen), and a moment after, so the springs settle smoothly; then the slow tick.
        if (_tweens.Count > 0 || _walking || _chasing || _dragging || _legsByTrick || _fx.IsVisible || Hovering || _agents.Count > 0)
            _busyUntil = now + 0.8;
        var interval = now < _busyUntil ? FastFrame : IdleFrame;
        if (_frames.Interval != interval)
            _frames.Interval = interval;
    }

    // ---------------------------------------------------------------- the pointer

    // Inside its orbit the pointer gets watched - eyes, head turn, looking up and down - between
    // tricks (a trick in progress finishes first). It only follows when told to ("follow me"),
    // and then it follows the pointer anywhere until another command.
    private void UpdateAttention(double now, double dt)
    {
        if (_dragging || _peeking)
            return;

        Native.GetCursorPos(out var p);
        var dx = p.X / _scale - (_x + Size / 2);
        var dy = p.Y / _scale - (_y + (ViewTopY - 3.5) * Unit);   // from its eyes
        var near = _chasing || Math.Sqrt(dx * dx + dy * dy) < 1.25 * Size;
        if (near)
            _lastNear = now;

        if (!_idle && !_chasing)
        {
            _attentive = false;
            return;
        }

        if (near && !_attentive && _idle && _mood == ClaudeMood.None && now - _lastHello > 30)
        {
            _lastHello = now;   // you came close: hello (at most every 30 s)
            Do(WaveAsync);
        }
        _attentive |= near;
        if (!_attentive)
            return;

        if (now - _lastNear > 1.2)
        {
            _attentive = false;   // gone: back to its own business
            _t.EyeX = _t.EyeY = _t.Yaw = _t.Pitch = 0;
            return;
        }

        // Watch it: eyes first, then the head turns and tilts up or down.
        static double Clamp1(double v) => Math.Clamp(v, -1, 1);
        _t.EyeX = 0.6 * Clamp1(dx / (Size * 0.8));
        _t.EyeY = 0.5 * Clamp1(-dy / (Size * 0.8));
        _t.Pitch = 18 * Clamp1(-dy / Size);
        _t.Yaw = Math.Abs(_vx) > 25 ? 35 * Math.Sign(_vx) : 32 * Clamp1(dx / (Size * 1.2));

        if (_chasing)
            Chase(dx, p.Y / _scale, dt);
    }

    // Trots after the pointer along the ground, runs if it's far, eases to a stop a little way off -
    // and climbs up to it when it's above the floor (hangs there, arms up), dropping when it goes lower.
    private void Chase(double dx, double pointerY, double dt)
    {
        var gap = 0.85 * Size;
        var want = Math.Abs(dx) > gap ? dx - Math.Sign(dx) * gap : 0;
        var top = Math.Abs(want) > 500 ? 520 : 240;
        var desired = Math.Clamp(want * 4, -top, top);
        _vx += (desired - _vx) * (1 - Math.Exp(-6 * dt));

        var wa = WorkArea();
        _x = Math.Clamp(_x + _vx * dt, wa.Left - Size * 0.2, wa.Right - Size * 0.8);

        var (high, low) = (wa.Top - 5 * Unit, FloorY);
        var dy = Math.Clamp(pointerY - 4 * Unit, high, low) - _y;   // head just under the pointer
        var wantVy = Math.Abs(dy) > 6 ? Math.Clamp(dy * 5, -260, 900) : 0;   // climbs up, drops fast
        _vy += (wantVy - _vy) * (1 - Math.Exp(-8 * dt));
        _y = Math.Clamp(_y + _vy * dt, high, low);

        var climbing = _vy < -20;
        if (climbing)
        {
            var hand = Math.Sin(_now * 12);   // hand over hand
            _t.ArmLeft = 120 + 45 * hand;
            _t.ArmRight = 120 - 45 * hand;
        }
        else
        {
            _t.ArmLeft = _t.ArmRight = _y < low - 2 ? 155 : 0;   // hanging up there / on the ground
        }

        var speed = Math.Max(Math.Abs(_vx), climbing ? -_vy : 0);
        _walking = speed > 12;
        _running = Math.Abs(_vx) > 300;
        _stride = Math.Min(1, speed / 200);
    }

    // Until the next command ("stop following", "stay", anything) cancels it.
    private async Task FollowAsync(CancellationToken ct)
    {
        _chasing = true;
        _attentive = true;
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
            _chasing = _walking = _running = false;
            _vx = _vy = 0;
            _t.ArmLeft = _t.ArmRight = 0;
            _stride = 0;
        }
        _t.Yaw = _t.Pitch = _t.EyeX = _t.EyeY = 0;
        await Wait(0.4, ct);
    }

    // The ground shadow: shrinks and fades as it leaves the floor.
    private void UpdateShadow()
    {
        var lift = Math.Clamp(_pet.Hop / 2.5, 0, 0.6);
        var w = 11.5 * Unit * (1 - lift * 0.5);
        var h = 1.6 * Unit * (1 - lift * 0.5);
        _shadow.Width = w;
        _shadow.Height = h;
        _shadow.Opacity = _peeking ? 0 : 1 - lift;
        _shadow.Margin = new Thickness((Size - w) / 2, FeetFromTop - h / 2, 0, 0);
    }

    private static double EaseInOut(double p) => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    private Task Animate(double seconds, Action<double> step, CancellationToken ct, bool linear = false)
    {
        var done = new TaskCompletionSource();
        _tweens.Add(new Tween(_clock.Elapsed.TotalSeconds, Math.Max(seconds, 0.001), step, done, ct, linear));
        return done.Task;
    }

    private static Task Wait(double seconds, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct);

    // ---------------------------------------------------------------- screen geometry

    // ponytail: one DPI scale (this window's) for every monitor; mixed-DPI setups may peek a bit off.
    private Rect ToDips(Native.RECT r) => new(r.Left / _scale, r.Top / _scale, (r.Right - r.Left) / _scale, (r.Bottom - r.Top) / _scale);

    private Rect WorkArea()
    {
        var s = _hwnd == IntPtr.Zero ? Screen.PrimaryScreen! : Screen.FromHandle(_hwnd);
        var w = s.WorkingArea;
        return new Rect(w.Left / _scale, w.Top / _scale, w.Width / _scale, w.Height / _scale);
    }

    private (bool Left, bool Right) FreeEdges()
    {
        var s = Screen.FromHandle(_hwnd).Bounds;
        var all = SystemInformation.VirtualScreen;
        return (s.Left <= all.Left, s.Right >= all.Right);
    }

    private bool OnAnyScreen() =>
        Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)((_x + Size / 2) * _scale), (int)((_y + FeetFromTop - 1) * _scale)));

    // The front-most ordinary app window that has an edge to hide behind (not maximized, not ours).
    private IntPtr FrontWindow()
    {
        foreach (var h in Native.WindowsFrontToBack())
            if (IsOrdinaryWindow(h))
                return Native.IsZoomed(h) ? IntPtr.Zero : h;
        return IntPtr.Zero;
    }
}
