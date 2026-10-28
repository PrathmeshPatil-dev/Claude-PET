using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ClaudePet;

// One pet at a time, a tray icon to control it, and "start with Windows".
public partial class App : System.Windows.Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private Mutex? _single;
    private Forms.NotifyIcon? _tray;
    private PetWindow? _pet;
    private Settings? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var restarted = e.Args.Contains(RestartArg);
        if (restarted)
            Thread.Sleep(3000);   // let whatever ran the machine out of memory settle first
        _single = new Mutex(true, "ClaudePet.SingleInstance", out var first);
        if (!first && restarted)
        {
            try
            {
                first = _single.WaitOne(TimeSpan.FromSeconds(10));   // the old one is on its way out
            }
            catch (AbandonedMutexException)
            {
                first = true;
            }
        }
        if (!first)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;   // a failed trick must not kill the pet
            if (args.Exception is OutOfMemoryException)
                Restart();   // WPF lost its drawing surface: the pet would stay frozen until restarted
        };

        _settings = Settings.Load();
        ApplyStartWithWindows(_settings.StartWithWindows);

        _pet = new PetWindow(_settings);
        _pet.Show();
        _tray = CreateTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pet?.SavePosition();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _single?.Dispose();
        base.OnExit(e);
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Talk to me  (Ctrl+Alt+Space)", null, (_, _) => _pet!.Talk());
        var visible = new Forms.ToolStripMenuItem("Hide pet  (Ctrl+Alt+P)");
        visible.Click += (_, _) => _pet!.ToggleVisible();
        menu.Items.Add(visible);

        var roam = new Forms.ToolStripMenuItem("Roam around") { Checked = _settings!.Roam, CheckOnClick = true };
        roam.Click += (_, _) => _pet!.SetRoaming(roam.Checked);
        menu.Items.Add(roam);
        menu.Items.Add("Stay here", null, (_, _) => _pet!.SetRoaming(false));

        var tricks = new Forms.ToolStripMenuItem("Tricks");
        tricks.DropDownItems.Add("Wave", null, (_, _) => _pet!.Trick(PetCommandKind.Wave));
        tricks.DropDownItems.Add("Turn around and watch", null, (_, _) => _pet!.Trick(PetCommandKind.TurnAround));
        tricks.DropDownItems.Add("Cheer", null, (_, _) => _pet!.Trick(PetCommandKind.Cheer));
        tricks.DropDownItems.Add("Hide and peek", null, (_, _) => _pet!.Trick(PetCommandKind.Hide));
        tricks.DropDownItems.Add("Peek behind a window", null, (_, _) => _pet!.Trick(PetCommandKind.PeekWindow));
        tricks.DropDownItems.Add("Look around", null, (_, _) => _pet!.Trick(PetCommandKind.LookAround));
        menu.Items.Add(tricks);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows, CheckOnClick = true };
        startup.Click += (_, _) =>
        {
            _settings.StartWithWindows = startup.Checked;
            _settings.Save();
            ApplyStartWithWindows(startup.Checked);
        };
        menu.Items.Add(startup);
        menu.Items.Add("Edit names (settings.json)", null, (_, _) =>
            Process.Start(new ProcessStartInfo(SettingsPath) { UseShellExecute = true }));
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        // The menu reflects changes made by talking to the pet ("stay", "go play").
        menu.Opening += (_, _) =>
        {
            roam.Checked = _pet!.Roaming;
            visible.Text = _pet.IsShown ? "Hide pet  (Ctrl+Alt+P)" : "Show pet  (Ctrl+Alt+P)";
        };

        var tray = new Forms.NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Claude Pet",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => _pet!.ToggleVisible();
        return tray;
    }

    private const string RestartArg = "--restart";
    private bool _restarting;

    private void Restart()
    {
        if (_restarting)
            return;
        _restarting = true;
        Process.Start(Environment.ProcessPath!, RestartArg);
        Shutdown();
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePet", "settings.json");

    private static void ApplyStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            key.SetValue("ClaudePet", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("ClaudePet", throwOnMissingValue: false);
    }

    // The mascot sprite, drawn for the tray (11 x 8 sprite pixels at 2.75 px each, in 32 x 32).
    private static Drawing.Icon DrawIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            const float u = 2.75f, ox = 1, oy = 5;
            using var clay = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xD9, 0x77, 0x57));
            void Px(float x, float y, float w, float h, Drawing.Brush b) => g.FillRectangle(b, ox + x * u, oy + y * u, w * u, h * u);
            Px(1, 0, 9, 6, clay);                                   // body
            Px(0, 2, 1, 2, clay); Px(10, 2, 1, 2, clay);            // arms
            foreach (var x in new[] { 1f, 3, 7, 9 }) Px(x, 6, 1, 2, clay);   // legs
            Px(2, 2, 1, 1, Drawing.Brushes.Black); Px(8, 2, 1, 1, Drawing.Brushes.Black);   // eyes
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:u} {ex}\n\n");
        }
        catch (IOException)
        {
            // nowhere to write; keep running
        }
    }
}
