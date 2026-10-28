using ClaudePet;

namespace ClaudePet.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("open a terminal for me in this folder", PetCommandKind.Terminal, "")]
    [InlineData("Open terminal here", PetCommandKind.Terminal, "")]
    [InlineData("terminal", PetCommandKind.Terminal, "")]
    [InlineData("open terminal in mes3", PetCommandKind.Terminal, "mes3")]
    [InlineData(@"open powershell in E:\MES3\lib", PetCommandKind.Terminal, @"e:\mes3\lib")]
    [InlineData("open vs code in this folder", PetCommandKind.VsCode, "")]
    [InlineData("open code in mes3", PetCommandKind.VsCode, "mes3")]
    [InlineData("take a screenshot", PetCommandKind.Screenshot, "")]
    [InlineData("can you capture the screen please", PetCommandKind.Screenshot, "")]
    [InlineData("snip", PetCommandKind.Snip, "")]
    [InlineData("open antigravity", PetCommandKind.Open, "antigravity")]
    [InlineData("open mes dev", PetCommandKind.Open, "mes dev")]
    [InlineData("open folder downloads", PetCommandKind.Open, "downloads")]
    [InlineData("open this folder", PetCommandKind.Open, "")]
    [InlineData("lock the screen", PetCommandKind.Lock, "")]
    [InlineData("stay here", PetCommandKind.Stay, "")]
    [InlineData("stay", PetCommandKind.Stay, "")]
    [InlineData("go play", PetCommandKind.Roam, "")]
    [InlineData("wander around", PetCommandKind.Roam, "")]
    [InlineData("wave", PetCommandKind.Wave, "")]
    [InlineData("hi!", PetCommandKind.Wave, "")]
    [InlineData("turn around", PetCommandKind.TurnAround, "")]
    [InlineData("cheer", PetCommandKind.Cheer, "")]
    [InlineData("hide", PetCommandKind.Hide, "")]
    [InlineData("peek", PetCommandKind.PeekWindow, "")]
    [InlineData("look up", PetCommandKind.LookUp, "")]
    [InlineData("look down", PetCommandKind.LookDown, "")]
    [InlineData("dash", PetCommandKind.Dash, "")]
    [InlineData("run!", PetCommandKind.Dash, "")]
    [InlineData("run fast", PetCommandKind.Dash, "")]
    [InlineData("climb the window", PetCommandKind.Climb, "")]
    [InlineData("walk the four corners", PetCommandKind.Tour, "")]
    [InlineData("do a backflip", PetCommandKind.Backflip, "")]
    [InlineData("front flip", PetCommandKind.Frontflip, "")]
    [InlineData("flip", PetCommandKind.Frontflip, "")]
    [InlineData("go to sleep", PetCommandKind.Sleep, "")]
    [InlineData("sit down", PetCommandKind.Sit, "")]
    [InlineData("dance", PetCommandKind.Dance, "")]
    [InlineData("spin", PetCommandKind.Spin, "")]
    [InlineData("yawn", PetCommandKind.Stretch, "")]
    [InlineData("trip", PetCommandKind.Trip, "")]
    [InlineData("open hidden files", PetCommandKind.Open, "hidden files")]   // "hidden" is not "hide"
    [InlineData("gun", PetCommandKind.Gun, "")]
    [InlineData("fight", PetCommandKind.Fight, "")]
    [InlineData("snap", PetCommandKind.AskClaude, "")]
    [InlineData("snap history", PetCommandKind.SnapHistory, "")]
    [InlineData("my snaps", PetCommandKind.SnapHistory, "")]
    [InlineData("start a battle", PetCommandKind.Fight, "")]
    [InlineData("draw", PetCommandKind.Draw, "")]
    [InlineData("start drawing on the screen", PetCommandKind.Draw, "")]
    [InlineData("stop drawing", PetCommandKind.Roam, "")]
    [InlineData("summon", PetCommandKind.Summon, "")]
    [InlineData("summon 30", PetCommandKind.Summon, "30")]
    [InlineData("summion 20 friends", PetCommandKind.Summon, "20")]
    [InlineData("follow me", PetCommandKind.Follow, "")]
    [InlineData("come here", PetCommandKind.Follow, "")]
    [InlineData("stop following", PetCommandKind.Roam, "")]
    [InlineData("help", PetCommandKind.Help, "")]
    [InlineData("quit", PetCommandKind.Quit, "")]
    public void Sentences_map_to_commands(string text, PetCommandKind kind, string target)
    {
        Assert.Equal(new PetCommand(kind, target), CommandParser.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rm -rf everything")]
    [InlineData("format my disk")]
    [InlineData("this")]   // "hi" inside a word is not a greeting
    public void Anything_else_is_unknown(string text)
    {
        Assert.Equal(PetCommandKind.Unknown, CommandParser.Parse(text).Kind);
    }

    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase) { ["mes3"] = @"E:\MES3" };

    [Fact]
    public void This_folder_is_the_front_explorer_folder()
    {
        Assert.Equal(@"C:\Work", CommandParser.ResolveFolder("", Named, () => @"C:\Work", _ => true));
        Assert.Null(CommandParser.ResolveFolder("", Named, () => null, _ => true));
    }

    [Fact]
    public void Named_folders_then_existing_paths()
    {
        Assert.Equal(@"E:\MES3", CommandParser.ResolveFolder("MES3", Named, () => null, _ => false));
        Assert.Equal(@"d:\x", CommandParser.ResolveFolder(@"d:\x", Named, () => null, _ => true));
        Assert.Null(CommandParser.ResolveFolder(@"d:\missing", Named, () => null, _ => false));
        Assert.Null(CommandParser.ResolveFolder("somewhere", Named, () => null, _ => true));
    }
}
