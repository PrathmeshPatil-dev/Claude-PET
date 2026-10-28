using System.IO;
using System.Text.RegularExpressions;

namespace ClaudePet;

public enum PetCommandKind
{
    Unknown, Help, Quit,
    Stay, Roam,
    Wave, TurnAround, Cheer, Hide, PeekWindow, LookUp, LookDown, LookAround,
    Dash, Climb, Tour, Sit, Sleep, Backflip, Frontflip, Spin, Dance, Stretch, Trip, Follow, Gun, Draw, Summon, Fight,
    Terminal, VsCode, Open, Screenshot, Snip, Lock, AskClaude, SnapHistory,
}

// Target: a folder for Terminal / VsCode / AskClaude ("" = the front-most Explorer folder), or the
// thing to open for Open (an app, a link or a folder, resolved by name in that order).
// Text: what to ask Claude, exactly as typed ("" = ask for it after the screenshot).
public sealed record PetCommand(PetCommandKind Kind, string Target = "", string Text = "");

// Plain sentences to commands. Only these commands exist: nothing typed is ever run as a shell
// command, it can only pick one of the actions below.
public static class CommandParser
{
    private static readonly Regex Filler = new(
        @"\b(please|pls|can you|could you|would you|will you|for me|hey|claude|buddy|pet|now|quickly|just)\b",
        RegexOptions.Compiled);

    // "this folder", "here", ... - the folder the user is looking at in Explorer.
    private static readonly Regex ThisFolder = new(
        @"^(this|the current|current|my current|that)?\s*(folder|directory|dir|place)?$|^here$|^this$",
        RegexOptions.Compiled);

    // "snap", "snap in mes3", "snap: why is this failing", "ask claude in mes3: what's this error" -
    // read before the text is normalized, so the question goes to Claude as typed. After "in", the
    // folder runs to a ':' or the end (a drive's ':' is part of it).
    private static readonly Regex AskClaude = new(
        @"^\s*(snap|ask claude)\b\s*(in\s+(?<folder>([a-z]:)?[^:]+?)\s*(:|$))?\s*:?\s*(?<question>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static PetCommand Parse(string text)
    {
        if (Regex.IsMatch(text, @"^\s*(snap history|my snaps|snaps)\s*$", RegexOptions.IgnoreCase))
            return new(PetCommandKind.SnapHistory);
        var ask = AskClaude.Match(text);
        if (ask.Success)
        {
            var folder = ask.Groups["folder"].Value.Trim();   // a path stays as typed ("claude" is a filler word); a name is normalized
            return new(PetCommandKind.AskClaude, FolderWord(Path.IsPathRooted(folder) ? folder : Normalize(folder)), ask.Groups["question"].Value.Trim());
        }

        var s = Normalize(text);
        if (s.Length == 0)
            return new(PetCommandKind.Unknown);

        if (Has(s, "quit|exit|close yourself|goodbye|bye bye")) return new(PetCommandKind.Quit);
        if (Has(s, "help|what can you do|commands")) return new(PetCommandKind.Help);
        if (Has(s, "screenshot|screen shot|capture (the )?screen|print screen")) return new(PetCommandKind.Screenshot);
        if (Has(s, "snip")) return new(PetCommandKind.Snip);
        if (Has(s, "lock")) return new(PetCommandKind.Lock);
        if (Has(s, "terminal|cmd|powershell|console|command prompt")) return new(PetCommandKind.Terminal, Where(s));
        if (Has(s, "vs code|vscode|visual studio code|code")) return new(PetCommandKind.VsCode, Where(s));
        if (Has(s, "stop following|unfollow|stop chasing|leave it|stop drawing|erase|clean up|clear")) return new(PetCommandKind.Roam);
        if (Has(s, "draw|drawing|paint|doodle|scribble")) return new(PetCommandKind.Draw);
        var summon = Regex.Match(s, @"\bsumm?i?ons?\b\D*(\d+)?");
        if (summon.Success) return new(PetCommandKind.Summon, summon.Groups[1].Value);
        if (Has(s, "follow|follow me|chase|come here|come")) return new(PetCommandKind.Follow);
        if (Has(s, "stay|stop|freeze|don't move|dont move")) return new(PetCommandKind.Stay);

        // Tricks before "open ...": "go to sleep" is a nap, not a folder called "sleep".
        if (Has(s, "dash|sprint|zoomies|zoom|run fast|run around|race")) return new(PetCommandKind.Dash);
        if (Has(s, "tour|corners|patrol|go around")) return new(PetCommandKind.Tour);
        if (Has(s, "climb|climbing")) return new(PetCommandKind.Climb);
        if (Has(s, "backflip|back flip")) return new(PetCommandKind.Backflip);
        if (Has(s, "frontflip|front flip|flip|somersault")) return new(PetCommandKind.Frontflip);
        if (Has(s, "sleep|nap|snooze|rest")) return new(PetCommandKind.Sleep);
        if (Has(s, "gun")) return new(PetCommandKind.Gun);
        if (Has(s, "fight|battle|duel|spar|versus|vs")) return new(PetCommandKind.Fight);
        if (Has(s, "sit|sit down|take a seat")) return new(PetCommandKind.Sit);
        if (Has(s, "dance|dancing|boogie")) return new(PetCommandKind.Dance);
        if (Has(s, "spin|twirl")) return new(PetCommandKind.Spin);
        if (Has(s, "stretch|yawn")) return new(PetCommandKind.Stretch);
        if (Has(s, "trip|fall|fail|slip")) return new(PetCommandKind.Trip);
        if (Has(s, "roam|wander|walk around|move around|go play|play|walk|explore|be free")) return new(PetCommandKind.Roam);
        if (Has(s, "look up")) return new(PetCommandKind.LookUp);
        if (Has(s, "look down")) return new(PetCommandKind.LookDown);
        if (Has(s, "turn|watch|look at (the )?screen")) return new(PetCommandKind.TurnAround);
        if (Has(s, "cheer|celebrate|yay|party|hooray")) return new(PetCommandKind.Cheer);
        if (Has(s, "peek")) return new(PetCommandKind.PeekWindow);
        if (Has(s, "hide")) return new(PetCommandKind.Hide);

        var open = Regex.Match(s, @"^(open|launch|start|run|go to|browse)\s+(.*)$");
        if (open.Success)
            return new(PetCommandKind.Open, FolderWord(open.Groups[2].Value));

        if (Has(s, "run|running")) return new(PetCommandKind.Dash);
        if (Has(s, "look|look around")) return new(PetCommandKind.LookAround);
        if (Has(s, "wave|hi|hello|hiya|yo")) return new(PetCommandKind.Wave);

        return new(PetCommandKind.Unknown);
    }

    // Resolve a folder target: "" = the front-most Explorer folder, then a named folder, then a path.
    // null when nothing matches; the caller says why.
    public static string? ResolveFolder(string target, IReadOnlyDictionary<string, string> named,
        Func<string?> frontMostExplorerFolder, Func<string, bool> exists)
    {
        if (target.Length == 0)
            return frontMostExplorerFolder();
        if (named.TryGetValue(target, out var path))
            return path;
        return Path.IsPathRooted(target) && exists(target) ? target : null;
    }

    // Lower case, no punctuation (path characters kept), no politeness, single spaces.
    private static string Normalize(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[^\w\s:\\/.\-~']", " ");
        s = Filler.Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.TrimEnd('.');
    }

    private static bool Has(string s, string alternatives) => Regex.IsMatch(s, $@"\b({alternatives})\b");

    // The folder after "in" / "at" / "inside" ("open a terminal in this folder" -> "").
    private static string Where(string s)
    {
        var m = Regex.Match(s, @"\b(in|at|inside|from)\s+(.+)$");
        return m.Success ? FolderWord(m.Groups[2].Value) : "";
    }

    private static string FolderWord(string target)
    {
        var t = Regex.Replace(target.Trim(), @"^(the|a|an|my)\s+", "");
        t = Regex.Replace(t, @"^(folder|directory)\s+", "");   // "open folder mes3" -> "mes3"
        return ThisFolder.IsMatch(t) ? "" : t;
    }
}
