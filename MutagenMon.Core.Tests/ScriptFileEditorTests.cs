using System.Text;
using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ScriptFileEditorTests
{
    private const string TwoScripts = "[a]\n# @host: h1\necho a\n\n[b]\n# @host: h2\necho b\n";
    private const string ThreeScripts = "[a]\nx\n\n[b]\ny\n\n[c]\nz\n";

    private static string NewTempPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "scripts.ini");
    }

    // ---- ReplaceBody ----

    [Fact]
    public void ReplaceBodyChangesOnlyTheNamedSection()
    {
        var result = ScriptFileEditor.ReplaceBodyInText(TwoScripts, "b", "# @host: h2\necho B2");

        Assert.Equal("[a]\n# @host: h1\necho a\n\n[b]\n# @host: h2\necho B2\n", result);
    }

    [Fact]
    public void ReplaceBodyKeepsTheBlankLineBeforeTheNextSection()
    {
        var result = ScriptFileEditor.ReplaceBodyInText(TwoScripts, "a", "new");

        Assert.Equal("[a]\nnew\n\n[b]\n# @host: h2\necho b\n", result);
    }

    [Fact]
    public void ReplaceBodyKeepsTextBeforeTheFirstHeader()
    {
        var result = ScriptFileEditor.ReplaceBodyInText("# top\n\n[a]\nx\n", "a", "y");

        Assert.Equal("# top\n\n[a]\ny\n", result);
    }

    [Fact]
    public void ReplaceBodyTrimsBlankEdgesOfTheNewBody()
    {
        var result = ScriptFileEditor.ReplaceBodyInText("[a]\nx\n", "a", "\n\necho z\n\n");

        Assert.Equal("[a]\necho z\n", result);
    }

    [Fact]
    public void ReplaceBodyPreservesCrLfLineEndings()
    {
        var crlf = TwoScripts.Replace("\n", "\r\n");

        var result = ScriptFileEditor.ReplaceBodyInText(crlf, "b", "new\nbody");

        Assert.Equal("[a]\n# @host: h1\necho a\n\n[b]\nnew\nbody\n".Replace("\n", "\r\n"), result);
    }

    [Fact]
    public void ReplaceBodyRenamesTheHeaderWhenANewNameIsGiven()
    {
        var result = ScriptFileEditor.ReplaceBodyInText(TwoScripts, "a", "# @host: h1\necho a", "renamed");

        Assert.Equal("[renamed]\n# @host: h1\necho a\n\n[b]\n# @host: h2\necho b\n", result);
    }

    [Fact]
    public void ReplaceBodyWithTheSameNameDoesNotRename()
    {
        var result = ScriptFileEditor.ReplaceBodyInText(TwoScripts, "a", "# @host: h1\necho a", "a");

        Assert.Equal(TwoScripts, result);
    }

    [Fact]
    public void ReplaceBodyRefusesANameAlreadyUsed()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.ReplaceBodyInText(TwoScripts, "a", "x", "b"));
    }

    [Fact]
    public void ReplaceBodyRefusesAnInvalidNewName()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.ReplaceBodyInText(TwoScripts, "a", "x", "bad]name"));
    }

    [Fact]
    public void ReplaceBodyThrowsForAnUnknownScript()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.ReplaceBodyInText(TwoScripts, "nope", "x"));
    }

    [Fact]
    public void ReplaceBodyThrowsWhenTheNameIsDuplicated()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.ReplaceBodyInText("[a]\nx\n\n[a]\ny\n", "a", "z"));
    }

    [Fact]
    public void ReplaceBodyOnAFileKeepsAUtf8Bom()
    {
        var path = NewTempPath();
        File.WriteAllText(path, TwoScripts, new UTF8Encoding(true));

        ScriptFileEditor.ReplaceBody(path, "a", "é");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains("é", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceBodyOnAFileWithoutBomWritesNoBom()
    {
        var path = NewTempPath();
        File.WriteAllText(path, TwoScripts, new UTF8Encoding(false));

        ScriptFileEditor.ReplaceBody(path, "a", "y");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'[', bytes[0]);
    }

    // ---- AddScript ----

    [Fact]
    public void AppendScriptToEmptyTextWritesJustTheSection()
    {
        var result = ScriptFileEditor.AppendScriptToText("", "n", "# @host: h\n# @description: TODO");

        var eol = Environment.NewLine;
        Assert.Equal($"[n]{eol}# @host: h{eol}# @description: TODO{eol}", result);
    }

    [Fact]
    public void AppendScriptSeparatesFromThePreviousSectionWithOneBlankLine()
    {
        Assert.Equal("[a]\nx\n\n[b]\ny\n", ScriptFileEditor.AppendScriptToText("[a]\nx\n", "b", "y"));
        Assert.Equal("[a]\nx\n\n[b]\ny\n", ScriptFileEditor.AppendScriptToText("[a]\nx", "b", "y"));
        Assert.Equal("[a]\nx\n\n[b]\ny\n", ScriptFileEditor.AppendScriptToText("[a]\nx\n\n\n\n", "b", "y"));
    }

    [Fact]
    public void AppendScriptUsesTheFilesLineEnding()
    {
        var result = ScriptFileEditor.AppendScriptToText("[a]\r\nx\r\n", "b", "y\nz");

        Assert.Equal("[a]\r\nx\r\n\r\n[b]\r\ny\r\nz\r\n", result);
    }

    [Fact]
    public void AddScriptCreatesAMissingFileAndItsDirectory()
    {
        var path = NewTempPath();
        var nested = Path.Combine(Path.GetDirectoryName(path)!, "sub", "scripts.ini");

        ScriptFileEditor.AddScript(nested, "n", "# @host: h");

        var scripts = ScriptFileParser.ParseFile(nested);
        Assert.Equal("n", Assert.Single(scripts).Name);
    }

    [Fact]
    public void AddScriptAppendsToAnExistingFile()
    {
        var path = NewTempPath();
        File.WriteAllText(path, TwoScripts);

        ScriptFileEditor.AddScript(path, "c", "# @host: h3");

        Assert.Equal(new[] { "a", "b", "c" }, ScriptFileParser.ParseFile(path).Select(s => s.Name));
    }

    // ---- RemoveScript ----

    [Fact]
    public void RemoveScriptDropsAMiddleSectionAndItsSeparator()
    {
        Assert.Equal("[a]\nx\n\n[c]\nz\n", ScriptFileEditor.RemoveScriptFromText(ThreeScripts, "b"));
    }

    [Fact]
    public void RemoveScriptDropsTheFirstSection()
    {
        Assert.Equal("[b]\ny\n\n[c]\nz\n", ScriptFileEditor.RemoveScriptFromText(ThreeScripts, "a"));
    }

    [Fact]
    public void RemoveScriptDropsTheLastSectionAndKeepsTheFinalNewline()
    {
        Assert.Equal("[a]\nx\n\n[b]\ny\n", ScriptFileEditor.RemoveScriptFromText(ThreeScripts, "c"));
    }

    [Fact]
    public void RemoveScriptOfTheOnlySectionLeavesAnEmptyFile()
    {
        Assert.Equal("", ScriptFileEditor.RemoveScriptFromText("[a]\nx\n", "a"));
    }

    [Fact]
    public void RemoveScriptKeepsTextBeforeTheFirstHeader()
    {
        Assert.Equal("# top\n[b]\ny\n", ScriptFileEditor.RemoveScriptFromText("# top\n[a]\nx\n\n[b]\ny\n", "a"));
    }

    [Fact]
    public void RemoveScriptThrowsForAnUnknownScript()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.RemoveScriptFromText(ThreeScripts, "nope"));
    }

    [Fact]
    public void RemoveScriptOnAFileRewritesIt()
    {
        var path = NewTempPath();
        File.WriteAllText(path, ThreeScripts);

        ScriptFileEditor.RemoveScript(path, "b");

        Assert.Equal(new[] { "a", "c" }, ScriptFileParser.ParseFile(path).Select(s => s.Name));
    }

    // ---- MoveScript ----

    [Fact]
    public void MoveScriptUpSwapsWithThePreviousSection()
    {
        Assert.Equal("[b]\ny\n\n[a]\nx\n\n[c]\nz\n", ScriptFileEditor.MoveScriptInText(ThreeScripts, "b", -1));
    }

    [Fact]
    public void MoveScriptDownSwapsWithTheNextSection()
    {
        Assert.Equal("[a]\nx\n\n[c]\nz\n\n[b]\ny\n", ScriptFileEditor.MoveScriptInText(ThreeScripts, "b", 1));
    }

    [Fact]
    public void MoveScriptKeepsTheFinalNewlineAtTheEndOfTheFile()
    {
        Assert.Equal("[a]\nx\n\n[c]\nz\n\n[b]\ny\n", ScriptFileEditor.MoveScriptInText(ThreeScripts, "c", -1));
    }

    [Fact]
    public void MoveScriptIsANoOpAtEitherEnd()
    {
        Assert.Equal(ThreeScripts, ScriptFileEditor.MoveScriptInText(ThreeScripts, "a", -1));
        Assert.Equal(ThreeScripts, ScriptFileEditor.MoveScriptInText(ThreeScripts, "c", 1));
    }

    [Fact]
    public void MoveScriptKeepsTextBeforeTheFirstHeader()
    {
        Assert.Equal("# top\n[b]\ny\n\n[a]\nx\n", ScriptFileEditor.MoveScriptInText("# top\n[a]\nx\n\n[b]\ny\n", "a", 1));
    }

    [Fact]
    public void MoveScriptPreservesCrLfLineEndings()
    {
        var result = ScriptFileEditor.MoveScriptInText(ThreeScripts.Replace("\n", "\r\n"), "a", 1);

        Assert.Equal("[b]\ny\n\n[a]\nx\n\n[c]\nz\n".Replace("\n", "\r\n"), result);
    }

    [Fact]
    public void MoveScriptThrowsForAnUnknownScript()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.MoveScriptInText(ThreeScripts, "nope", 1));
    }

    // ---- ReplaceBodies ----

    [Fact]
    public void ReplaceBodiesAppliesEveryEditInOneWrite()
    {
        var path = NewTempPath();
        File.WriteAllText(path, ThreeScripts);

        ScriptFileEditor.ReplaceBodies(path, new[] { new ScriptEdit("a", "x2", "a2"), new ScriptEdit("c", "z2") });

        Assert.Equal("[a2]\nx2\n\n[b]\ny\n\n[c]\nz2\n", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceBodiesWritesNothingWhenOneEditFails()
    {
        var path = NewTempPath();
        File.WriteAllText(path, ThreeScripts);

        Assert.Throws<InvalidOperationException>(
            () => ScriptFileEditor.ReplaceBodies(path, new[] { new ScriptEdit("a", "x2"), new ScriptEdit("nope", "q") }));

        Assert.Equal(ThreeScripts, File.ReadAllText(path));
    }

    // ---- Groups ----

    // g1 has a and c (with an ungrouped script between them), g2 has b, d is ungrouped.
    private const string Grouped =
        "[a]\n# @group: g1\nx\n\n[b]\n# @group: g2\ny\n\n[c]\n# @group: g1\nz\n\n[d]\nw\n";

    private static string[] SectionNames(string text) =>
        ScriptFileParser.ParseLines(text.Split('\n')).Select(s_ => s_.Name).ToArray();

    [Fact]
    public void MoveScriptStaysWithinItsGroup()
    {
        // c moves up past b (another group) and swaps order with a, the only other g1 script.
        var result = ScriptFileEditor.MoveScriptInText(Grouped, "c", -1);

        Assert.Equal(new[] { "c", "a", "b", "d" }, SectionNames(result));
    }

    [Fact]
    public void MoveScriptIsANoOpAtTheEndOfItsGroup()
    {
        Assert.Equal(Grouped, ScriptFileEditor.MoveScriptInText(Grouped, "a", -1));
        Assert.Equal(Grouped, ScriptFileEditor.MoveScriptInText(Grouped, "c", 1));
        Assert.Equal(Grouped, ScriptFileEditor.MoveScriptInText(Grouped, "b", 1));
    }

    [Fact]
    public void MoveScriptWithoutGroupMovesAmongUngroupedScriptsOnly()
    {
        var text = "[a]\nx\n\n[g]\n# @group: G\ny\n\n[b]\nz\n";

        Assert.Equal(new[] { "b", "a", "g" }, SectionNames(ScriptFileEditor.MoveScriptInText(text, "b", -1)));
    }

    [Fact]
    public void MoveGroupDownPlacesItsScriptsAfterTheNextGroup()
    {
        var result = ScriptFileEditor.MoveGroupInText(Grouped, "g1", 1);

        // g2, then g1 (contiguous), then the ungrouped script.
        Assert.Equal(new[] { "b", "a", "c", "d" }, SectionNames(result));
    }

    [Fact]
    public void MoveGroupUpSwapsWithThePreviousGroup()
    {
        var result = ScriptFileEditor.MoveGroupInText(Grouped, "g2", -1);

        Assert.Equal(new[] { "b", "a", "c", "d" }, SectionNames(result));
    }

    [Fact]
    public void MoveGroupIsANoOpAtEitherEnd()
    {
        Assert.Equal(Grouped, ScriptFileEditor.MoveGroupInText(Grouped, "g1", -1));
        Assert.Equal(Grouped, ScriptFileEditor.MoveGroupInText(Grouped, "g2", 1));
    }

    [Fact]
    public void MoveGroupKeepsBodiesSpacingAndFinalNewline()
    {
        var result = ScriptFileEditor.MoveGroupInText("[a]\n# @group: g1\nx\n\n[b]\n# @group: g2\ny\n", "g1", 1);

        Assert.Equal("[b]\n# @group: g2\ny\n\n[a]\n# @group: g1\nx\n", result);
    }

    [Fact]
    public void MoveGroupThrowsForAnUnknownGroup()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.MoveGroupInText(Grouped, "nope", 1));
    }

    [Fact]
    public void MoveGroupIsCaseSensitive()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptFileEditor.MoveGroupInText(Grouped, "G1", 1));
    }

    [Fact]
    public void MoveGroupOnAFileRewritesIt()
    {
        var path = NewTempPath();
        File.WriteAllText(path, Grouped);

        ScriptFileEditor.MoveGroup(path, "g1", 1);

        Assert.Equal(new[] { "b", "a", "c", "d" }, ScriptFileParser.ParseFile(path).Select(s => s.Name));
    }

    [Fact]
    public void MoveScriptOnAFileRewritesIt()
    {
        var path = NewTempPath();
        File.WriteAllText(path, ThreeScripts);

        ScriptFileEditor.MoveScript(path, "a", 1);

        Assert.Equal(new[] { "b", "a", "c" }, ScriptFileParser.ParseFile(path).Select(s => s.Name));
    }
}
