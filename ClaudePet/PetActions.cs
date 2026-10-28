using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudePet;

// The things the pet can do on the PC. Each returns what the pet says back.
public sealed class PetActions(Settings settings)
{
    private const string NoExplorer = "Open a folder in File Explorer first, or tell me one (like \"in mes3\").";

    public string Terminal(string target)
    {
        var folder = Folder(target);
        if (folder == null)
            return target.Length == 0 ? NoExplorer : $"I don't know the folder \"{target}\".";

        Start("wt.exe", "-d", folder);
        return $"Terminal in {folder}";
    }

    public string VsCode(string target)
    {
        var folder = Folder(target);
        if (folder == null)
            return target.Length == 0 ? NoExplorer : $"I don't know the folder \"{target}\".";
        if (!settings.Apps.TryGetValue("vs code", out var code))
            return "Add \"vs code\" to Apps in my settings first.";

        Start(code, folder);
        return $"VS Code in {folder}";
    }

    // An app, then a link, then a folder, by the name in settings.json.
    public string Open(string target)
    {
        if (target.Length > 0 && settings.Apps.TryGetValue(target, out var app))
        {
            Start(app);
            return $"Opening {target}";
        }
        if (target.Length > 0 && settings.Urls.TryGetValue(target, out var url))
        {
            Shell(url);
            return $"Opening {url}";
        }

        var folder = Folder(target);
        if (folder != null)
        {
            Start("explorer.exe", folder);
            return $"Opening {folder}";
        }
        return target.Length == 0 ? NoExplorer : $"I don't know \"{target}\". Add it to my settings.json.";
    }

    public string Screenshot() => $"Saved {CaptureScreen()}";

    // Snap screenshots stay on this PC: not in Pictures, which OneDrive syncs.
    public static string SnapsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudePet", "snaps");

    // Every monitor, saved under Pictures\Screenshots (or `dir`); returns the file. The caller hides the pet first.
    public string CaptureScreen(string? dir = null)
    {
        var area = System.Windows.Forms.SystemInformation.VirtualScreen;   // real pixels (per-monitor DPI aware)
        using var bitmap = new System.Drawing.Bitmap(area.Width, area.Height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);

        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"ClaudePet-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return file;
    }

    // The folders inside `folder`, by name - without hidden and system ones ($Recycle.Bin, System
    // Volume Information...) or ones you can't open. Empty when the folder itself can't be read.
    public static List<string> Subfolders(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    // The code folder for a Claude session, or why there isn't one.
    public (string? Folder, string? Problem) ClaudeFolder(string target)
    {
        var folder = Folder(target);
        return folder != null ? (folder, null) : (null, target.Length == 0 ? NoExplorer : $"I don't know the folder \"{target}\".");
    }

    // A new Windows Terminal window in the folder, running `claude` (your login, your settings, your
    // default model) with the first message. Its own window, so it never lands as a tab beside
    // another session.
    public static void StartClaude(string folder, Guid sessionId, string firstMessage)
    {
        var settings = Path.Combine(Path.GetDirectoryName(SnapsFolder)!, "snap-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, ClaudeSession.Settings);
        Start("wt.exe", "-w", "new", "-d", folder, "pwsh", "-NoExit", "-EncodedCommand",
            ClaudeSession.Encode(ClaudeSession.LaunchScript(sessionId, settings, firstMessage)));
    }

    public string Snip()
    {
        Shell("ms-screenclip:");
        return "Snip away!";
    }

    public string Lock()
    {
        Native.LockWorkStation();
        return "Locked. See you!";
    }

    private string? Folder(string target) =>
        CommandParser.ResolveFolder(target, settings.Folders, FrontMostExplorerFolder, Directory.Exists);

    private static void Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        Process.Start(info);
    }

    private static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // The folder shown in the File Explorer window nearest the front, or null when none is open.
    // Explorer windows come from Shell.Application; z-order decides which one is "this folder".
    public static string? FrontMostExplorerFolder()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null)
            return null;

        dynamic shell = Activator.CreateInstance(type)!;
        var byWindow = new Dictionary<IntPtr, string>();
        try
        {
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    string path = w.Document.Folder.Self.Path;
                    if (Directory.Exists(path))   // skips "This PC", Recycle Bin and other virtual folders
                        byWindow[new IntPtr(Convert.ToInt64(w.HWND))] = path;
                }
                catch (Exception)
                {
                    // not a folder window
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        foreach (var hWnd in Native.WindowsFrontToBack())
            if (byWindow.TryGetValue(hWnd, out var path))
                return path;
        return null;
    }
}
