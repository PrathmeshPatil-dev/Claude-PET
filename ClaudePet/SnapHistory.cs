using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudePet;

// Your snap chats with Claude - folder, what you asked, the screenshot, and the replies the pet
// relayed - in %APPDATA%\ClaudePet\snaps.dat, encrypted with Windows DPAPI for your Windows login:
// other Windows users on this PC, and a copy of the file on another machine, can't read it.
public sealed class SnapHistory(string file)
{
    public sealed record Entry(Guid Id, DateTime At, string Folder, string Question, string Screenshot, List<string> Replies);

    public static string DefaultFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePet", "snaps.dat");

    public List<Entry> Load()
    {
        if (!File.Exists(file))
            return [];
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Entry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return [];   // not ours to read (another login), or damaged
        }
    }

    public void Add(Entry entry)
    {
        var all = Load();
        all.Add(entry);
        Save(all);
    }

    public void AddReply(Guid id, string reply)
    {
        var all = Load();
        var entry = all.Find(e => e.Id == id);
        if (entry == null)
            return;
        entry.Replies.Add(reply);
        Save(all);
    }

    private void Save(List<Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), null, DataProtectionScope.CurrentUser));
    }
}
