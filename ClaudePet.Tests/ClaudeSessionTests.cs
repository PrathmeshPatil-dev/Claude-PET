using System.IO;
using System.Text;
using ClaudePet;

namespace ClaudePet.Tests;

public class ClaudeSessionTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Launch_script_survives_quotes_and_round_trips_through_base64()
    {
        var script = ClaudeSession.LaunchScript(Id, @"C:\Users\o'neil\s.json", "why isn't it 'working'; & | \"x\"");
        Assert.Equal("claude --session-id 11111111-2222-3333-4444-555555555555 --settings 'C:\\Users\\o''neil\\s.json' 'why isn''t it ''working''; & | \"x\"'", script);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(ClaudeSession.Encode(script))));
    }

    [Theory]
    [InlineData(@"E:\MES3", "E--MES3")]
    [InlineData(@"C:\Users\pprat\Downloads\New folder", "C--Users-pprat-Downloads-New-folder")]
    public void Transcript_path_uses_the_folder_name_claude_uses(string folder, string dir) =>
        Assert.Equal(Path.Combine("p", dir, $"{Id}.jsonl"), ClaudeSession.TranscriptPath("p", folder, Id));

    [Fact]
    public void Assistant_text_is_read_and_everything_else_skipped()
    {
        Assert.Equal("Hi there.\nSecond part.", ClaudeSession.AssistantText(
            """{"message":{"role":"assistant","content":[{"type":"text","text":"Hi there."},{"type":"tool_use","name":"Read"},{"type":"text","text":"Second part."}]},"type":"assistant"}"""));
        Assert.Null(ClaudeSession.AssistantText("""{"message":{"role":"user","content":[{"type":"text","text":"me"}]}}"""));
        Assert.Null(ClaudeSession.AssistantText("""{"message":{"role":"assistant","content":[{"type":"tool_use","name":"Read"}]}}"""));
        Assert.Null(ClaudeSession.AssistantText("""{"type":"summary"}"""));
        Assert.Null(ClaudeSession.AssistantText("""{"message":{"role":"assist"""));
    }

    [Theory]
    [InlineData("snap", "", "")]
    [InlineData("snap in mes3", "mes3", "")]
    [InlineData(@"snap in E:\MES3\api", @"E:\MES3\api", "")]
    [InlineData("snap: Why does the BUILD fail?", "", "Why does the BUILD fail?")]
    [InlineData("snap why is this red", "", "why is this red")]
    [InlineData("Ask Claude in mes3: what's this error?", "mes3", "what's this error?")]
    [InlineData("snap in this folder: why", "", "why")]
    [InlineData(@"snap in C:\Temp\claude\My Code: why", @"C:\Temp\claude\My Code", "why")]
    public void Snap_keeps_the_question_as_typed(string text, string folder, string question) =>
        Assert.Equal(new PetCommand(PetCommandKind.AskClaude, folder, question), CommandParser.Parse(text));

    [Fact]
    public void First_message_is_lesgo_with_the_screenshot_only_when_there_is_one()
    {
        Assert.Equal("/lesgo fix the build", ClaudeSession.FirstMessage("fix the build", null));
        Assert.Equal("/lesgo fix the build\n\nHere is a screenshot of my screen: C:\\s.png\nLook at that image first.", ClaudeSession.FirstMessage("fix the build", @"C:\s.png"));
    }

    [Fact]
    public void Skipping_the_prompt_sends_only_the_screenshot_or_nothing()
    {
        Assert.Equal("Here is a screenshot of my screen: C:\\s.png\nLook at that image first.", ClaudeSession.FirstMessage("", @"C:\s.png"));
        Assert.Equal("", ClaudeSession.FirstMessage("", null));
        Assert.Equal("claude --session-id 11111111-2222-3333-4444-555555555555 --settings 's.json'", ClaudeSession.LaunchScript(Id, "s.json", ""));
    }

    [Fact]
    public void Transcript_lines_give_tools_results_and_end_of_turn()
    {
        var call = ClaudeSession.Parse("""{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_1","name":"Agent"},{"type":"tool_use","id":"toolu_2","name":"Grep"}]}}""")!;
        Assert.True(call.FromClaude);
        Assert.Equal([("Agent", "toolu_1"), ("Grep", "toolu_2")], call.Tools);
        Assert.False(call.EndOfTurn);

        var result = ClaudeSession.Parse("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"done"}]}}""")!;
        Assert.False(result.FromClaude);
        Assert.Equal(["toolu_1"], result.Results);

        Assert.Equal("why is it red", ClaudeSession.Parse("""{"type":"user","message":{"role":"user","content":"why is it red"}}""")!.Text);
        Assert.True(ClaudeSession.Parse("""{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed."}]}}""")!.EndOfTurn);
    }

    [Fact]
    public void Request_copies_and_metadata_are_not_the_conversation()
    {
        const string copy = """{"type":"api-request-blob","message":{"role":"assistant","content":[{"type":"text","text":"old reply"}]}}""";
        Assert.Null(ClaudeSession.Parse(copy));
        Assert.Null(ClaudeSession.AssistantText(copy));
        Assert.Null(ClaudeSession.Parse("""{"type":"attachment","attachment":{}}"""));
    }

    [Fact]
    public void Bubble_text_drops_markdown_and_is_cut_short()
    {
        Assert.Equal("Use npm run build here.", ClaudeSession.ForBubble("Use `npm run build` **here**."));
        Assert.Equal(423, ClaudeSession.ForBubble(new string('a', 1000)).Length);
    }
}
