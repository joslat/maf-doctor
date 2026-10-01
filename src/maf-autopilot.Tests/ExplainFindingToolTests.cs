using MafDoctor.Prompts;
using MafDoctor.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MafDoctor.Tests;

public class ExplainFindingToolTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-explainfinding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RealFinding_ReturnsGroundedSubstrate()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bad
                {
                    public Bad() { var c = new DefaultAzureCredential(); }
                }
                """);
            // Find the actual line of the offending code (robust to formatting).
            var lines = File.ReadAllLines(Path.Combine(dir, "Bad.cs"));
            var line = Array.FindIndex(lines, l => l.Contains("DefaultAzureCredential")) + 1;

            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", line);

            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);
            Assert.Contains("Code in context", output, StringComparison.Ordinal);
            Assert.Contains("**Why:**", output, StringComparison.Ordinal);
            Assert.Contains("**Fix:**", output, StringComparison.Ordinal);
            Assert.Contains("auto-fixable", output, StringComparison.Ordinal);   // SEC-001 is auto-fixable
            Assert.Contains("maf://constraints", output, StringComparison.Ordinal);
            Assert.Contains("new DefaultAzureCredential", output, StringComparison.Ordinal); // the code window shows the line
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SecretLine_RedactedInCodeWindow()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Keys.cs"),
                "public class Keys { const string K = \"sk-ABCDEF0123456789abcdef\"; }");
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Keys.cs", 1);

            Assert.Contains("MAF-AP-SEC-002", output, StringComparison.Ordinal);
            Assert.Contains("redacted", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal); // never echoed
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OversizedFile_RejectedWithoutReading()
    {
        // F-20 — a file over the 10 MB cap must be refused before
        // File.ReadAllText, not merely truncated after.
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "Huge.cs");
            using (var fs = new FileStream(path, FileMode.Create))
            {
                fs.SetLength(10 * 1024 * 1024 + 1);
            }

            var output = new ExplainFindingTool().MafExplainFinding(dir, "Huge.cs", 1);

            Assert.StartsWith("Error:", output, StringComparison.Ordinal);
            Assert.Contains("byte cap", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FileAtCap_StillProcessed()
    {
        // Boundary check: exactly at the cap must still succeed (only
        // strictly-over is rejected).
        //
        // Round-3 review fixup — the padding here previously landed 6 bytes
        // short of the actual 10 MB cap (a hand-computed "- 40" that didn't
        // account for the prefix/suffix lengths), and the assertion used
        // `<=` rather than exact equality, so this test never actually
        // reached the boundary it claims to pin. Compute padding from the
        // real prefix/suffix lengths and assert the exact byte count.
        var dir = NewTempDir();
        try
        {
            const string prefix = "public class C { int X() => 1; }";
            const string suffix = "\n";
            const int cap = 10 * 1024 * 1024;
            var path = Path.Combine(dir, "AtCap.cs");
            var content = prefix + new string(' ', cap - prefix.Length - suffix.Length) + suffix;
            File.WriteAllText(path, content);
            Assert.Equal(cap, new FileInfo(path).Length);

            var output = new ExplainFindingTool().MafExplainFinding(dir, "AtCap.cs", 1);

            Assert.DoesNotContain("byte cap", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoFindingAtLine_ReturnsGracefulMessage()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Clean.cs"),
                "public class Clean { public int Add(int a, int b) => a + b; }");
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Clean.cs", 1);

            Assert.Contains("No active finding", output, StringComparison.Ordinal);
            // Still shows the code window for orientation.
            Assert.Contains("Code in context", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NonexistentFile_ReturnsError()
    {
        var dir = NewTempDir();
        try
        {
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Nope.cs", 1);
            Assert.Contains("Error", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmptyRepoPath_ReturnsError()
    {
        var output = new ExplainFindingTool().MafExplainFinding("", "x.cs", 1);
        Assert.Contains("Error", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathTraversalInFile_ReturnsError()
    {
        var dir = NewTempDir();
        try
        {
            var output = new ExplainFindingTool().MafExplainFinding(dir, "../../etc/passwd", 1);
            Assert.Contains("Error", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Code in context", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void HugeContext_IsClampedToMax()
    {
        // 80-line file, finding in the middle, context far past the cap → the
        // window must stay bounded (a line >30 away must NOT appear).
        var dir = NewTempDir();
        try
        {
            var fileBody = new System.Text.StringBuilder();
            for (var i = 1; i <= 80; i++)
                fileBody.AppendLine(i == 1 ? "using Microsoft.Agents.AI; // FAR_MARKER_TOP"
                    : i == 40 ? "var c = new DefaultAzureCredential();"
                    : $"// filler {i}");
            File.WriteAllText(Path.Combine(dir, "Big.cs"), fileBody.ToString());

            var output = new ExplainFindingTool().MafExplainFinding(dir, "Big.cs", 40, context: 9999);

            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);   // finding surfaced
            Assert.DoesNotContain("FAR_MARKER_TOP", output, StringComparison.Ordinal); // line 1 is 39 away → clamped out
            Assert.Contains("// filler 39", output, StringComparison.Ordinal);     // line 39 is in-window
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NearbyFinding_WidensToNearest_AndMarksTheRealLine()
    {
        // Finding sits 2 lines off the requested line → ±2 widening fires.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), """
                using Microsoft.Agents.AI; // padding line 1
                // padding line 2
                // padding line 3
                var c = new DefaultAzureCredential();
                """);
            // Finding is on line 4; ask about line 2 (distance 2).
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", 2);

            Assert.Contains("showing the nearest within", output, StringComparison.Ordinal); // widened note
            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);
            // The marker (`>`) is on the real finding line (4), not the asked line (2).
            Assert.Contains("> 4 |", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindingMoreThanTwoLinesAway_ReturnsNoActiveFinding()
    {
        // Distance 3 is outside the ±2 widening window.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), """
                using Microsoft.Agents.AI; // padding line 1
                // padding line 2
                // padding line 3
                // padding line 4
                var c = new DefaultAzureCredential();
                """);
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", 2); // finding on line 5
            Assert.Contains("No active finding", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MultipleFindingsOnOneLine_RendersPluralHeader()
    {
        // SEC-001 (cred) + SEC-002 (key) both fire on the same physical line.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"),
                "using Microsoft.Agents.AI; public class Bad { public Bad() { var c = new DefaultAzureCredential(); var k = \"sk-ABCDEF0123456789abcdef\"; } }");
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", 1);

            Assert.Contains("2 findings at this line", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal); // window line redacted
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ContextZero_ShowsOnlyTheFindingLine()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), """
                using Microsoft.Agents.AI; // line before
                var c = new DefaultAzureCredential();
                // line after
                """);
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", 2, context: 0);

            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);
            Assert.Contains("> 2 |", output, StringComparison.Ordinal);     // the finding line, marked
            Assert.DoesNotContain("line before", output, StringComparison.Ordinal);
            Assert.DoesNotContain("line after", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SecretOnContextLine_IsRedacted()
    {
        // The secret is on a CONTEXT line (not the requested finding line) — it
        // must still be redacted across the whole window.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), """
                using Microsoft.Agents.AI; var c = new DefaultAzureCredential();
                const string K = "sk-ABCDEF0123456789abcdef";
                """);
            var output = new ExplainFindingTool().MafExplainFinding(dir, "Bad.cs", 1); // ask about line 1 (SEC-001)

            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);
            Assert.Contains("redacted", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // The companion prompt
    // -------------------------------------------------------------------------

    [Fact]
    public void ExplainFindingPrompt_BuildsGroundedInstruction()
    {
        var messages = MafPrompts.ExplainFinding("Executors/Foo.cs", "42", "/repo");
        var msg = Assert.Single(messages);
        Assert.Equal(Role.User, msg.Role);
        var text = Assert.IsType<TextContentBlock>(msg.Content).Text;

        Assert.Contains("MafExplainFinding", text, StringComparison.Ordinal);
        Assert.Contains("maf://constraints", text, StringComparison.Ordinal);
        Assert.Contains("maf://guide", text, StringComparison.Ordinal);
        Assert.Contains("Executors/Foo.cs", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
        Assert.Contains("Repository: /repo", text, StringComparison.Ordinal); // repoPath rendered when supplied
    }

    [Fact]
    public void ExplainFindingPrompt_StripsHtmlCommentsFromInputs()
    {
        // Echoed params are HTML-comment-stripped (prompt-injection defense, house style).
        var messages = MafPrompts.ExplainFinding("Foo.cs<!-- inject -->", "1", null);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(messages).Content).Text;
        Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inject", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository:", text, StringComparison.Ordinal); // null repoPath → line omitted
    }
}
