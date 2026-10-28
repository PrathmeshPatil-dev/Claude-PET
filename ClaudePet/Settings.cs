using System.IO;
using System.Text.Json;

namespace ClaudePet;

// %APPDATA%\ClaudePet\settings.json. Written with defaults on first run; edit it to add folders,
// apps and links the pet knows by name ("open terminal in mes3", "open mes dev").
public sealed class Settings
{
    public bool Roam { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public double? FeetX { get; set; }       // where it stands (screen DIPs)
    public double? FeetY { get; set; }
    public int PixelSize { get; set; } = 10; // screen pixels per sprite pixel: 10 = about 110 px wide
    public string? LastClaudeFolder { get; set; }   // offered first by the Ctrl+Alt+C menu

    public Dictionary<string, string> Folders { get; set; } = new()
    {
        ["mes3"] = @"E:\MES3",
        ["home"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["downloads"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };

    public Dictionary<string, string> Apps { get; set; } = new()
    {
        ["antigravity"] = Path.Combine(LocalPrograms, "Antigravity IDE", "Antigravity IDE.exe"),
        ["vs code"] = Path.Combine(LocalPrograms, "Microsoft VS Code", "Code.exe"),
    };

    public Dictionary<string, string> Urls { get; set; } = new()
    {
        ["mes local"] = "http://localhost:5001",
        ["mes dev"] = "https://development.mesplatform.com",
    };

    private static string LocalPrograms =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePet", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        Settings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings()
                : new Settings();
        }
        catch (JsonException)
        {
            settings = new Settings();   // a broken file must not keep the pet from starting
        }

        // Names are matched case-insensitively whatever the file says.
        settings.Folders = new(settings.Folders, StringComparer.OrdinalIgnoreCase);
        settings.Apps = new(settings.Apps, StringComparer.OrdinalIgnoreCase);
        settings.Urls = new(settings.Urls, StringComparer.OrdinalIgnoreCase);
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
