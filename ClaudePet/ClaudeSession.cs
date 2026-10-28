using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudePet;

// A Claude Code session the pet starts for you ("snap: why is this failing"): the same `claude`
// CLI and login as your terminal, opened in Windows Terminal in the code folder, with your text
// and a screenshot as the first message. The pet follows the session's transcript and says the
// replies in its bubble.
public static class ClaudeSession
{
    // The PowerShell that Windows Terminal runs: `claude` with a known session id, the extra settings
    // file (Remote Control off) and the first message. Passed base64-encoded (-EncodedCommand), so
    // nothing in your text needs quoting for Windows Terminal or the command line; inside the
    // single-quoted strings only ' is special.
    public static string LaunchScript(Guid sessionId, string settingsFile, string prompt) =>
        $"claude --session-id {sessionId} --settings {Quote(settingsFile)}" + (prompt.Length > 0 ? $" {Quote(prompt)}" : "");

    private static string Quote(string s) => $"'{s.Replace("'", "''")}'";

    // Loaded on top of your own settings for every snap session: never reachable over Remote
    // Control, whatever the account-wide setting says.
    public const string Settings = """{ "remoteControlAtStartup": false }""";

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    // Your /lesgo skill with what you typed, and the screenshot when there is one. Skipped the
    // typing: just the screenshot, or nothing (an empty session in the folder) - /lesgo needs a goal.
    public static string FirstMessage(string question, string? screenshot)
    {
        var shot = screenshot == null ? "" : $"Here is a screenshot of my screen: {screenshot}\nLook at that image first.";
        if (question.Length == 0)
            return shot;
        return shot.Length == 0 ? $"/lesgo {question}" : $"/lesgo {question}\n\n{shot}";
    }

    // Where Claude Code keeps a session's transcript: ~/.claude/projects/<folder with every
    // non-alphanumeric character as '-'>/<session id>.jsonl ("E:\MES3" -> "E--MES3").
    public static string TranscriptPath(string projectsDir, string folder, Guid sessionId) =>
        Path.Combine(projectsDir, Regex.Replace(folder, "[^A-Za-z0-9]", "-"), $"{sessionId}.jsonl");

    // One transcript line, as far as the pet cares: who said it, the text, the tools Claude called
    // (name and id), the ids of tool results coming back, and whether Claude's turn ended.
    public sealed record Line(bool FromClaude, string? Text, IReadOnlyList<(string Name, string Id)> Tools,
        IReadOnlyList<string> Results, bool EndOfTurn);

    // Only the conversation itself: lines whose top-level type is "user" or "assistant". Everything
    // else (metadata, attachments, the "api-request-blob" copies of requests) and half-written
    // lines give null.
    public static Line? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() is not ("user" or "assistant")
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            var fromClaude = message.TryGetProperty("role", out var role) && role.GetString() == "assistant";
            var endOfTurn = message.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String && stop.GetString() == "end_turn";
            if (!message.TryGetProperty("content", out var content))
                return new(fromClaude, null, [], [], endOfTurn);
            if (content.ValueKind == JsonValueKind.String)   // a typed prompt
                return new(fromClaude, content.GetString(), [], [], endOfTurn);
            if (content.ValueKind != JsonValueKind.Array)
                return new(fromClaude, null, [], [], endOfTurn);

            var texts = new List<string>();
            var tools = new List<(string, string)>();
            var results = new List<string>();
            foreach (var c in content.EnumerateArray())
            {
                string? Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                switch (Str("type"))
                {
                    case "text" when Str("text") is { } t: texts.Add(t); break;
                    case "tool_use" when Str("name") is { } n: tools.Add((n, Str("id") ?? "")); break;
                    case "tool_result" when Str("tool_use_id") is { } id: results.Add(id); break;
                }
            }
            var text = string.Join("\n", texts).Trim();
            return new(fromClaude, text.Length == 0 ? null : text, tools, results, endOfTurn);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The text of an assistant message on one transcript line, or null for anything else.
    public static string? AssistantText(string line) => Parse(line) is { FromClaude: true, Text: { } text } ? text : null;

    // Claude's tools that read code (the pet's eyes dart along the lines) and that start helper agents.
    public static bool IsReading(string tool) => tool is "Read" or "Grep" or "Glob";
    public static bool IsAgent(string tool) => tool is "Agent" or "Task";

    // Short enough for the bubble: markdown marks dropped, cut at about 420 characters.
    public static string ForBubble(string text)
    {
        var s = text.Replace("**", "").Replace("`", "");
        return s.Length <= 420 ? s : s[..420].TrimEnd() + "...";
    }
}
