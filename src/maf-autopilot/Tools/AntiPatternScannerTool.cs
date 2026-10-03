using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafScanAntiPatterns
///
/// Walks a MAF codebase looking for known anti-patterns AFTER migration is complete.
/// Rules canonical: see <c>.github/skills/maf-anti-pattern-scanner/SKILL.md</c>.
///
/// This is the **identity-unlock** tool — it materialises the "co-pilot beyond migration"
/// promise by running the constraints listed in `maf-constraints.instructions.md` as
/// actual scans instead of aspirational bullets.
/// </summary>
[McpServerToolType]
public sealed class AntiPatternScannerTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Scan a MAF codebase for known anti-patterns (security, concurrency,
        observability, identity, topology). Distinct from migration tooling —
        this checks idiom and configuration on code that's already on a recent MAF release.

        Input:
          - repoPath: absolute path to the repo root. All *.cs files are scanned recursively
            (excluding bin/obj/tests/samples by default).
          - format: "markdown" (default) for a human-readable report, or "sarif" for a
            SARIF v2.1.0 document suitable for GitHub Advanced Security ingestion.

        Returns a categorised report grouped by severity (error / warning / info).
        Rule list source: maf://rules.
        """)]
    public string MafScanAntiPatterns(
        [Description("Absolute path to the repository root (or any directory).")] string repoPath,
        [Description("Output format: \"markdown\" (default) or \"sarif\" (v2.1.0 JSON).")] string format = "markdown")
    {
        // REP-22: a machine consumer that asked for SARIF must get SARIF back on
        // failure too — a plain-text error would break its parser. Route validation
        // errors through the SARIF invocation channel when that's the requested format.
        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
            return format.Equals("sarif", StringComparison.OrdinalIgnoreCase)
                ? SarifExportTool.EmitAntiPatternsError(err)
                : err;

        var findings = new List<AntiPatternFinding>();
        var budget = new SourceFileWalker.ScanBudget();
        var skippedUnreadable = 0;
        foreach (var file in EnumerateScannableFiles(repoPath, budget))
        {
            string source, rel;
            try
            {
                // WM-18: one locked/raced/unreadable file must not abort the whole scan
                // (DoctorTool already guards this; the standalone tools did not).
                source = File.ReadAllText(file);
                rel = SourceFileWalker.MakeRelative(repoPath, file);
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or InvalidOperationException)
            {
                skippedUnreadable++;
                continue;
            }
            findings.AddRange(ScanFile(source, rel));
        }

        return format.Equals("sarif", StringComparison.OrdinalIgnoreCase)
            ? SarifExportTool.EmitAntiPatternsSarif(findings)
            : FormatReport(repoPath, findings, budget, skippedUnreadable);
    }

    // -------------------------------------------------------------------------
    // Pure-function core (testable without files)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all anti-pattern rules against a single source string. Pure: no I/O.
    /// </summary>
    public static IReadOnlyList<AntiPatternFinding> ScanFile(string source, string fileName = "<inline>")
        => ScanFile(source, SourceParsing.Parse(source).GetRoot(), fileName);

    /// <summary>
    /// WM-16 overload: accepts an already-parsed <paramref name="root"/> so a caller
    /// scanning the same file with several tools (e.g. <c>DoctorTool</c>) parses ONCE
    /// and shares the tree across all of them instead of re-parsing per sub-scanner.
    /// The RegexRules still need the raw <paramref name="source"/> for line/offset math,
    /// so both are passed. The string overload above is a thin parse-and-delegate wrapper.
    /// </summary>
    public static IReadOnlyList<AntiPatternFinding> ScanFile(
        string source, Microsoft.CodeAnalysis.SyntaxNode root, string fileName = "<inline>")
    {
        var isTestFile = IsTestFile(fileName);
        var findings = new List<AntiPatternFinding>();

        foreach (var rule in AllRules)
        {
            if (rule.SkipInTestFiles && isTestFile) continue;
            findings.AddRange(rule.Scan(source, root, fileName));
        }
        return findings;
    }

    internal static bool IsTestFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var normalized = fileName.Replace('\\', '/').ToLowerInvariant();

        // Any path segment that itself looks like test / sample infrastructure.
        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "tests" or "test" or "samples" or "unittests" or "integrationtests" or "e2e")
                return true;
            if (segment.EndsWith(".tests", StringComparison.Ordinal)
                || segment.EndsWith(".unittests", StringComparison.Ordinal)
                || segment.EndsWith(".integrationtests", StringComparison.Ordinal)
                || segment.EndsWith(".e2e", StringComparison.Ordinal))
                return true;
        }
        return normalized.EndsWith("tests.cs", StringComparison.Ordinal)
            || normalized.EndsWith("test.cs", StringComparison.Ordinal)
            || normalized.EndsWith("fixtures.cs", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateScannableFiles(string repoRoot, SourceFileWalker.ScanBudget budget)
        => SourceFileWalker.EnumerateCsFiles(repoRoot, excludes: null, budget);

    // -------------------------------------------------------------------------
    // Rules
    // -------------------------------------------------------------------------

    // Phase 5.9 — ReDoS hygiene on every rule pattern.
    //
    // All AntiPatternScanner regexes carry NonBacktracking + a 2s
    // MatchTimeout. The patterns are simple enough that catastrophic
    // backtracking is unlikely in practice, but `MAF-AP-SEC-002` (the
    // API-key matcher) and `MAF-AP-CONC-002` (the .Result/.Wait matcher)
    // both have unbounded character classes adjacent to alternation —
    // exactly the shape that can pin CPU on a crafted line in the
    // surface-scanning rewriter. The shared options constant keeps the
    // policy locally enforceable: future rules MUST be added with
    // `RegexHygiene` and the CI invariant (ci-invariants.yml Job 2)
    // confirms the contract.
    private const RegexOptions RegexHygiene =
        RegexOptions.Compiled | RegexOptions.NonBacktracking;
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The hard-coded-secret literal pattern (rule MAF-AP-SEC-002). Exposed as
    /// the single source of truth for "what is a secret" so other tools can
    /// redact any source line containing one — e.g. DoctorTool's snippet
    /// renderer redacts content-aware, regardless of which rule surfaced the line.
    /// </summary>
    internal static readonly Regex SecretLiteralPattern =
        new(@"""(?:sk-|api[-_]?key=)[A-Za-z0-9_\-]{16,}""", RegexHygiene | RegexOptions.IgnoreCase, RegexBudget);

    /// <summary>
    /// Broader, best-effort REDACTION pattern (decoupled from the precise SEC-002
    /// rule pattern). Used only to decide whether a source line is too risky to
    /// echo into a report — so it errs toward over-matching (redact-on-doubt is
    /// safe; it just hides a line). Covers OpenAI/api keys, quoted connection-string
    /// secrets (AccountKey/Password/Secret/Token=…), GitHub tokens, AWS access-key
    /// ids, JWTs, and PEM private-key headers. NOT exhaustive — high-entropy secrets
    /// in unrecognized shapes can still slip through; this is defense-in-depth, not
    /// the primary control.
    /// </summary>
    // Pattern text in a const so the multi-line literal doesn't push the
    // `new(...)` options outside the ci-invariants regex-hygiene detection window.
    private const string SecretRedactionRegexText =
        // Leading quote OPTIONAL (`"?`) so bare `sk-…` / `api-key=…` in a comment or
        // raw-string body is redacted too, not just the quoted finding-style line.
        @"""?(?:sk-|api[-_]?key\s*=\s*)[A-Za-z0-9_\-]{16,}"
        + @"|""[^""]*(?:accountkey|sharedaccesskey|password|pwd|secret|token)\s*=\s*[^""]{4,}[^""]*"""
        + @"|gh[opsru]_[A-Za-z0-9]{20,}"
        + @"|AKIA[0-9A-Z]{16}"
        + @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]+"
        + @"|-----BEGIN [A-Z ]*PRIVATE KEY-----";
    internal static readonly Regex SecretRedactionPattern = new(SecretRedactionRegexText, RegexHygiene | RegexOptions.IgnoreCase, RegexBudget);

    /// <summary>
    /// Best-effort: true if the text looks like it contains a secret, for REDACTION
    /// purposes. Broader than the SEC-002 finding rule (<see cref="SecretLiteralPattern"/>)
    /// on purpose — see <see cref="SecretRedactionPattern"/>. Not a guarantee.
    /// </summary>
    internal static bool LooksLikeSecret(string text) => SecretRedactionPattern.IsMatch(text);

    private static readonly string[] AiNamespaces =
        ["Microsoft.Agents.AI", "Microsoft.Extensions.AI", "Azure.AI", "OpenAI", "Microsoft.SemanticKernel"];

    /// <summary>True when the file imports an agent/AI stack (MAF, MEAI, Azure AI, OpenAI, SK).</summary>
    internal static bool UsesAiStack(Microsoft.CodeAnalysis.SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.NamespaceOrType?.ToString() ?? string.Empty)
            .Any(name => AiNamespaces.Any(ns => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal)));

    internal static readonly IReadOnlyList<AntiPatternRule> AllRules = new AntiPatternRule[]
    {
        new RegexRule(
            id: "MAF-AP-SEC-001",
            name: "DefaultAzureCredential in production code",
            // Warning, not error (decision 2026-10-01): it is hardening advice (Azure
            // Identity: prefer a deterministic credential in production), not a
            // defect. As an error it graded an official Azure OpenAI sample "F".
            severity: AntiPatternSeverity.Warning,
            pattern: new Regex(@"\bnew\s+DefaultAzureCredential\s*\(", RegexHygiene, RegexBudget),
            skipInTestFiles: true,
            // Q-04: the rule is about credentials for agent/AI clients. A false-
            // positive sweep of non-MAF repos (CleanArchitecture: a Key Vault
            // config source) graded a repo with no MAF code "C" and offered to
            // rewrite it. Only files that use an AI stack are in scope.
            appliesToFile: UsesAiStack),

        new RegexRule(
            id: "MAF-AP-SEC-002",
            name: "Hard-coded API key literal",
            severity: AntiPatternSeverity.Error,
            // Matches "sk-XXXXXXXXXXXXXXXX" or "api-key=XXXXXXXXXXXXXXXX" style strings.
            // Shared with DoctorTool's snippet redactor via SecretLiteralPattern.
            pattern: SecretLiteralPattern,
            skipInTestFiles: false,
            // This rule deliberately matches inside string literals (a hard-coded
            // key IS a string), so it must NOT be skipped by the string-literal filter.
            matchesStringContent: true),

        // Syntax-aware (RoslynRule) so it matches the actual assignment shapes the
        // EnableSensitiveDataRewriter fixes — object-initializer, dictionary/
        // collection-initializer (`["EnableSensitiveData"] = true`), member-access,
        // and element-access — and does NOT false-fire on comments / string
        // literals that merely mention it. A regex (`EnableSensitiveData\s*=\s*true`)
        // both missed the dictionary form (the `"]` breaks it) AND matched comments,
        // so its "auto-fixable" tag was a no-op. This keeps detector ⇆ rewriter in parity.
        new RoslynRule(
            id: "MAF-AP-SEC-003",
            name: "EnableSensitiveData = true outside development",
            severity: AntiPatternSeverity.Error,
            skipInTestFiles: true,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                // The migration guide blesses `EnableSensitiveData = true` inside a dev-only
                // guard (#if DEBUG / DEVELOPMENT). Suppress findings on those guarded lines.
                var devGuarded = CollectGuardedLineRanges(root, c =>
                    c.Contains("DEBUG", StringComparison.Ordinal)
                    || c.Contains("DEVELOPMENT", StringComparison.Ordinal));
                foreach (var assign in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (assign.Right is not LiteralExpressionSyntax rhs
                        || rhs.RawKind != (int)SyntaxKind.TrueLiteralExpression)
                        continue;

                    // Match ONLY the contexts the rewriter can actually fix, so every
                    // finding is genuinely auto-fixable (true detector ⇆ rewriter parity):
                    // initializer entries (VisitInitializerExpression) and statement-position
                    // assignments (VisitExpressionStatement — this DOES include a statement
                    // inside a block-bodied lambda), and the body of an expression-bodied
                    // lambda — the shape Microsoft documents,
                    // `UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)` —
                    // which the rewriter turns into `cfg => {}`. Nested/parenthesized
                    // assignments (`b = (o.X = true)`) stay excluded — the rewriter can't reach them.
                    var match = assign.Left switch
                    {
                        IdentifierNameSyntax id => id.Identifier.ValueText == "EnableSensitiveData"
                            && assign.Parent is InitializerExpressionSyntax,                                   // new O { EnableSensitiveData = true }
                        ImplicitElementAccessSyntax iea => IsEnableSensitiveDataKey(iea.ArgumentList)
                            && assign.Parent is InitializerExpressionSyntax,                                   // new D { ["EnableSensitiveData"] = true }
                        MemberAccessExpressionSyntax mae => mae.Name.Identifier.ValueText == "EnableSensitiveData"
                            && (assign.Parent is ExpressionStatementSyntax                                     // x.EnableSensitiveData = true;
                                || assign.Parent is LambdaExpressionSyntax { ExpressionBody: var body }        // cfg => cfg.EnableSensitiveData = true
                                    && body == assign),
                        ElementAccessExpressionSyntax eae => IsEnableSensitiveDataKey(eae.ArgumentList)
                            && assign.Parent is ExpressionStatementSyntax,                                     // dict["EnableSensitiveData"] = true;
                        _ => false,
                    };
                    if (!match) continue;

                    var loc = assign.GetLocation().GetLineSpan();
                    var line = loc.StartLinePosition.Line + 1;
                    if (devGuarded.Any(r => line >= r.Start && line <= r.End)) continue; // dev-only guarded — OK
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-SEC-003",
                        "EnableSensitiveData = true outside development",
                        AntiPatternSeverity.Error,
                        file,
                        line,
                        assign.ToString()));
                }
                return findings;
            }),

        // MAF-AP-CONC-002 — sync-over-async: blocking on a Task via `.Result` / `.Wait()`.
        // RoslynRule (was a regex) so it can exclude the look-alikes a text shape can't:
        //   (await X).Result    — AgentResponse<T>.Result etc. (already awaited; .Result is the payload)
        //   foo(x).Result("$1") — Regex `Match.Result(...)` substitution, NOT Task<T>.Result
        // Preserves the old `)`-anchor intent: only a CALL result (`Foo().Result` /
        // `Foo().Wait()`) is flagged — never a plain `x.Result` property access.
        new RoslynRule(
            id: "MAF-AP-CONC-002",
            name: "Sync-over-async (.Result / .Wait())",
            severity: AntiPatternSeverity.Warning,
            skipInTestFiles: true,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                foreach (var mae in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    var name = mae.Name.Identifier.ValueText;
                    if (name is not ("Result" or "Wait")) continue;

                    // Only `<call>().Result` / `<call>().Wait()` — receiver is an invocation.
                    // This preserves the old `)`-anchor scope AND excludes `(await X).Result`
                    // (receiver is a parenthesized await, not an invocation) and `x.Result`.
                    if (mae.Expression is not InvocationExpressionSyntax) continue;

                    if (name == "Result")
                    {
                        // `foo().Result(args)` is `Match.Result` substitution, not Task.Result — skip.
                        if (mae.Parent is InvocationExpressionSyntax ri && ri.ArgumentList.Arguments.Count > 0)
                            continue;
                    }
                    else // Wait — only the parameterless blocking `foo().Wait()`.
                    {
                        if (mae.Parent is not InvocationExpressionSyntax wi || wi.ArgumentList.Arguments.Count != 0)
                            continue;
                    }

                    var loc = mae.Name.GetLocation().GetLineSpan();
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-CONC-002",
                        "Sync-over-async (.Result / .Wait())",
                        AntiPatternSeverity.Warning,
                        file,
                        loc.StartLinePosition.Line + 1,
                        $".{name} on a call result blocks the calling thread"));
                }
                return findings;
            }),

        // MAF-AP-OBS-001 — file builds an agent but never chains UseOpenTelemetry.
        // RoslynRule (not a raw-text scan): detection walks syntax NODES, so an
        // `AIAgentBuilder` / `new ChatClientAgent(` / `UseOpenTelemetry` that appears
        // only inside a comment or a string literal no longer false-fires — comments
        // are trivia (never nodes) and a string body is a literal token, not an
        // identifier. The reported line is the real builder node, not the first
        // textual match.
        new RoslynRule(
            id: "MAF-AP-OBS-001",
            name: "Missing UseOpenTelemetry in file that builds an agent",
            severity: AntiPatternSeverity.Warning,
            scan: (root, file) =>
            {
                // An agent is BUILT via a `new AIAgentBuilder(...)` or a
                // `new ChatClientAgent(...)` CONSTRUCTION. Match construction (object
                // creation), NOT a bare type reference — a parameter type, field type,
                // `nameof(AIAgentBuilder)` or `typeof(...)` is not building an agent and
                // must not fire (the earlier identifier-match over-flagged those).
                Microsoft.CodeAnalysis.SyntaxNode? builderNode = null;
                foreach (var n in root.DescendantNodes())
                {
                    if (n is BaseObjectCreationExpressionSyntax oce
                        && ObjectCreationTypeName(oce) is "AIAgentBuilder" or "ChatClientAgent")
                    {
                        builderNode = n;
                        break;
                    }
                }
                if (builderNode is null) return Array.Empty<AntiPatternFinding>();

                var hasOTel = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.ValueText == "UseOpenTelemetry");
                if (hasOTel) return Array.Empty<AntiPatternFinding>();

                var line = builderNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                return new[]
                {
                    new AntiPatternFinding(
                        "MAF-AP-OBS-001",
                        "Missing UseOpenTelemetry in file that builds an agent",
                        AntiPatternSeverity.Warning,
                        file,
                        line,
                        "(agent built without telemetry chained)")
                };
            },
            skipInTestFiles: true),

        new RoslynRule(
            id: "MAF-AP-CONC-001",
            name: "Instance fields on AIContextProvider (must be ProviderSessionState<T>)",
            severity: AntiPatternSeverity.Error,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    // Exact simple-name match (not substring `Contains`) so an unrelated
                    // type whose name merely includes "AIContextProvider" doesn't qualify.
                    var derivesFromProvider = cls.BaseList?.Types.Any(t =>
                        SimpleTypeName(t.Type) is "AIContextProvider" or "ChatHistoryProvider") ?? false;
                    if (!derivesFromProvider) continue;

                    foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
                    {
                        var modifiers = field.Modifiers.Select(m => m.ValueText).ToHashSet();
                        if (modifiers.Contains("readonly") || modifiers.Contains("const") || modifiers.Contains("static"))
                            continue;
                        // A ProviderSessionState<T> field IS the prescribed per-session
                        // container — the canonical FIX, not the anti-pattern — so never
                        // flag it, even when it's (sub-optimally) not marked readonly.
                        if (SimpleTypeName(field.Declaration.Type) == "ProviderSessionState")
                            continue;
                        var loc = field.GetLocation().GetLineSpan();
                        var variable = field.Declaration.Variables.FirstOrDefault();
                        findings.Add(new AntiPatternFinding(
                            "MAF-AP-CONC-001",
                            "Instance fields on AIContextProvider (must be ProviderSessionState<T>)",
                            AntiPatternSeverity.Error,
                            file,
                            loc.StartLinePosition.Line + 1,
                            $"mutable field `{variable?.Identifier.ValueText}` on `{cls.Identifier.ValueText}`"));
                    }
                }
                return findings;
            }),

        // MAF-AP-AGENT-001 — Instructions at top-level of ChatClientAgentOptions.
        // No GA release has that property (it was removed before 1.0): the build fails with CS0117.
        new RoslynRule(
            id: "MAF-AP-AGENT-001",
            name: "Instructions outside ChatOptions — no such property since MAF 1.0 (CS0117)",
            severity: AntiPatternSeverity.Error,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                // Find object initializers on ChatClientAgentOptions where an Instructions
                // assignment appears at the OUTER level (not nested inside `ChatOptions = new { ... }`).
                // Handles both `new ChatClientAgentOptions { … }` AND target-typed
                // `ChatClientAgentOptions opts = new() { … }` (declared type visible).
                foreach (var oce in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                {
                    if (oce.Initializer is null) continue;
                    if (ObjectCreationTypeName(oce) != "ChatClientAgentOptions") continue;

                    foreach (var expr in oce.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                    {
                        var leftText = expr.Left.ToString();
                        if (leftText != "Instructions") continue;
                        var loc = expr.GetLocation().GetLineSpan();
                        findings.Add(new AntiPatternFinding(
                            "MAF-AP-AGENT-001",
                            "Instructions outside ChatOptions — no such property since MAF 1.0 (CS0117)",
                            AntiPatternSeverity.Error,
                            file,
                            loc.StartLinePosition.Line + 1,
                            $"Instructions assigned at top of `ChatClientAgentOptions` — move inside `ChatOptions = new() {{ ... }}`"));
                    }
                }
                return findings;
            }),

        // MAF-AP-WF-001 — an executor with [MessageHandler] methods must be `partial`, or
        // the workflow source generator cannot emit its handler wiring (MAFGENWF003; without
        // the generator package the build fails with CS0534 instead). `sealed` is NOT
        // required (Workflows.Generators 1.23; MessageHandlerAttribute's own example is
        // `public partial class MyExecutor : Executor`), so it is not checked.
        new RoslynRule(
            id: "MAF-AP-WF-001",
            name: "Executor with [MessageHandler] methods must be `partial`",
            severity: AntiPatternSeverity.Error,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    // Only consider classes that inherit from `Executor` (plain OR
                    // generic `Executor<…>`, qualified or not) and have [MessageHandler]
                    // methods. Shared predicate keeps this in lockstep with the rewriter.
                    if (!(cls.BaseList?.Types.Any(t => IsExecutorBaseType(t.Type)) ?? false))
                        continue;
                    if (!cls.Members.OfType<MethodDeclarationSyntax>().Any(IsMessageHandler))
                        continue;

                    // A static class can't derive from Executor; abstract ones need
                    // `partial` too (parity with ExecutorSealedRewriter).
                    var modifiers = cls.Modifiers.Select(m => m.ValueText).ToHashSet();
                    if (modifiers.Contains("static") || modifiers.Contains("partial")) continue;

                    var loc = cls.Identifier.GetLocation().GetLineSpan();
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-WF-001",
                        "Executor with [MessageHandler] methods must be `partial`",
                        AntiPatternSeverity.Error,
                        file,
                        loc.StartLinePosition.Line + 1,
                        $"`{cls.Identifier.ValueText}` is missing `partial`"));
                }
                return findings;
            }),

        // MAF-AP-DEVUI-001 — DevUI registered or mapped outside a development-only guard.
        // DevUI "exposes agent metadata that is sensitive in production contexts: system
        // instructions, tool definitions, model identifiers, and workflow structure"
        // (DevUIOptions, Microsoft.Agents.AI.DevUI 1.23.0-preview); it only accepts loopback
        // requests by default. Hosting (AddAIAgent, A2A, AG-UI, …) is not flagged: it is
        // production hosting, preview-only like DevUI but with no such warning.
        new RoslynRule(
            id: "MAF-AP-DEVUI-001",
            name: "DevUI enabled outside a development-only guard",
            severity: AntiPatternSeverity.Warning,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                var guardedLineRanges = CollectGuardedLineRanges(root, c =>
                    c.Contains("DEVUI_ENABLED", StringComparison.Ordinal)
                    || c.Contains("DEBUG", StringComparison.Ordinal)
                    || c.Contains("DEVELOPMENT", StringComparison.Ordinal));

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = InvokedName(invocation);
                    if (name is not ("AddDevUI" or "MapDevUI")) continue;

                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (guardedLineRanges.Any(r => line >= r.Start && line <= r.End)) continue;
                    // A runtime guard: `if (app.Environment.IsDevelopment()) { app.MapDevUI(); }`.
                    if (invocation.Ancestors().OfType<IfStatementSyntax>()
                            .Any(s => s.Condition.ToString().Contains("IsDevelopment", StringComparison.Ordinal)
                                && s.Statement.Span.Contains(invocation.Span)))
                        continue;

                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-DEVUI-001",
                        "DevUI enabled outside a development-only guard",
                        AntiPatternSeverity.Warning,
                        file,
                        line,
                        $"{name}(...) — keep DevUI out of production builds"));
                }
                return findings;
            }),

        // MAF-AP-MID-001 — `.Use(runFunc:, runStreamingFunc:)` with only runFunc. The
        // middleware still runs on the streaming path, but "if only one of the delegates is
        // provided, it will be used for both methods … RunStreamingAsync will employ limited
        // streaming, as it will be operating on the batch output produced by runFunc"
        // (AIAgentBuilder.Use, Microsoft.Agents.AI 1.23): the caller gets one batch update.
        new RoslynRule(
            id: "MAF-AP-MID-001",
            name: "Middleware Use() without runStreamingFunc — streaming arrives as one batch",
            severity: AntiPatternSeverity.Info,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
                    if (member.Name.Identifier.ValueText != "Use") continue;

                    // Match the NAMED-callback middleware form via the AST, not a raw
                    // substring of the argument text. The old `argText.Contains("runFunc")`
                    // false-fired on any `.Use(...)` whose text merely CONTAINED the
                    // substring — a positional local named `runFunc`, or an unrelated
                    // identifier like `computeMyrunFunc()`. NameColon pins the actual
                    // `runFunc:` / `runStreamingFunc:` argument labels.
                    var args = invocation.ArgumentList.Arguments;
                    ArgumentSyntax? Named(string n) =>
                        args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == n);

                    if (Named("runFunc") is null) continue;          // not the named middleware form
                    var streaming = Named("runStreamingFunc");
                    var streamingIsNull = streaming is { Expression: LiteralExpressionSyntax sl }
                        && sl.RawKind == (int)SyntaxKind.NullLiteralExpression;
                    if (streaming is not null && !streamingIsNull) continue; // both callbacks present

                    var loc = invocation.GetLocation().GetLineSpan();
                    var why = streamingIsNull
                        ? "runStreamingFunc: null"
                        : "runStreamingFunc not provided";
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-MID-001",
                        "Middleware Use() without runStreamingFunc — streaming arrives as one batch",
                        AntiPatternSeverity.Info,
                        file,
                        loc.StartLinePosition.Line + 1,
                        $".Use(...) — {why}; RunStreamingAsync returns runFunc's output as one batch"));
                }
                return findings;
            }),

        // MAF-AP-EXEC-001 — obsolete executor surface. In Workflows 1.23 all four are still
        // public but [Obsolete] (a CS0618 warning, not a build break): ReflectingExecutor<T>
        // and IMessageHandler<…> since 1.0.0, [StreamsMessage] ("does not do anything") and
        // [YieldsMessage] (use [YieldsOutput]) since 1.2.0. Detects the legacy executor
        // surface that should be migrated. Salvaged from the May-6 pre-Phase-O
        // sketch (tag: pre-phase-o-may6-sketch). The corresponding CS0618 patterns
        // are also in the registry (`MAF130-EXEC-001`, `MAF130-ATTR-001/002`), but a
        // syntax-only scan catches them BEFORE you run `dotnet build` — useful for
        // the auditor agent's pre-migration pass.
        // RoslynRule (was a regex). The bare token `IMessageHandler<` over-matched every
        // MediatR / NServiceBus / hand-rolled `IMessageHandler<T>` — an extremely common
        // name unrelated to the obsolete Microsoft.Agents.AI.Workflows.IMessageHandler<T>.
        // Now: the MAF-specific attributes ([StreamsMessage] / [YieldsMessage]) are flagged
        // wherever they appear (low collision), but the generic legacy types
        // (ReflectingExecutor<…> / IMessageHandler<…>) are flagged ONLY when the file
        // actually imports the MAF Workflows namespace.
        new RoslynRule(
            id: "MAF-AP-EXEC-001",
            name: "Obsolete executor surface (ReflectingExecutor / IMessageHandler / [StreamsMessage] / [YieldsMessage])",
            severity: AntiPatternSeverity.Warning,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();

                // [StreamsMessage] / [YieldsMessage] — obsolete MAF attributes; MAF-specific
                // names, so flag wherever they appear (and AST-matching never hits comments).
                foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
                {
                    var n = attr.Name.ToString();
                    var s = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
                    if (s is "StreamsMessage" or "StreamsMessageAttribute" or "YieldsMessage" or "YieldsMessageAttribute")
                    {
                        var loc = attr.GetLocation().GetLineSpan();
                        findings.Add(new AntiPatternFinding(
                            "MAF-AP-EXEC-001",
                            "Obsolete executor surface (ReflectingExecutor / IMessageHandler / [StreamsMessage] / [YieldsMessage])",
                            AntiPatternSeverity.Warning, file, loc.StartLinePosition.Line + 1, $"[{s}]"));
                    }
                }

                // Generic legacy types. `ReflectingExecutor<…>` is a MAF-specific name
                // (low collision) → flag wherever it appears. `IMessageHandler<…>` is an
                // extremely common name (MediatR / NServiceBus / hand-rolled buses), so it
                // is flagged ONLY when the file imports the MAF Workflows namespace.
                var importsWorkflows = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Any(u => u.Name?.ToString().Contains("Microsoft.Agents.AI.Workflows", StringComparison.Ordinal) == true);
                foreach (var g in root.DescendantNodes().OfType<GenericNameSyntax>())
                {
                    var id = g.Identifier.ValueText;
                    var isLegacy = id == "ReflectingExecutor"
                        || (id == "IMessageHandler" && importsWorkflows);
                    if (!isLegacy) continue;

                    var loc = g.GetLocation().GetLineSpan();
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-EXEC-001",
                        "Obsolete executor surface (ReflectingExecutor / IMessageHandler / [StreamsMessage] / [YieldsMessage])",
                        AntiPatternSeverity.Warning, file, loc.StartLinePosition.Line + 1, $"{id}<…>"));
                }

                // Collapse multiple legacy surfaces on the same line.
                return findings.GroupBy(f => (f.File, f.Line)).Select(grp => grp.First()).ToList();
            },
            skipInTestFiles: false),

        // MAF-AP-APPROVAL-001 — a tool-approval response sent with no session is ignored.
        // MAF binds each ToolApprovalResponseContent to the request it recorded in the
        // current AgentSession ("A response takes effect only when its matching request was
        // recorded in the current AgentSession", ChatClientAgentOptions.DisableApprovalResponseBinding,
        // 1.23); RunAsync with no session creates a new one, so the response matches nothing.
        new RoslynRule(
            id: "MAF-AP-APPROVAL-001",
            name: "Tool-approval response sent without the session — ignored",
            severity: AntiPatternSeverity.Warning,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                if (!UsesAiStack(root)) return findings;
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = InvokedName(invocation);
                    if (name is not ("RunAsync" or "RunStreamingAsync")) continue;
                    var args = invocation.ArgumentList.Arguments;
                    if (args.Count == 0 || PassesSession(args)) continue;
                    var scope = invocation.Ancestors().FirstOrDefault(a =>
                        a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax) ?? root;
                    if (!args.Any(a => CarriesApprovalResponse(a.Expression, scope, depth: 3))) continue;

                    var loc = invocation.GetLocation().GetLineSpan();
                    findings.Add(new AntiPatternFinding(
                        "MAF-AP-APPROVAL-001",
                        "Tool-approval response sent without the session — ignored",
                        AntiPatternSeverity.Warning,
                        file,
                        loc.StartLinePosition.Line + 1,
                        $"{name}(...) sends a tool-approval response but no session"));
                }
                return findings;
            }),

        // MAF-AP-WF-002 — two [MessageHandler] methods for the same message type. The
        // source generator accepts it, but running the workflow throws "A handler for
        // message type … is already registered" (Microsoft.Agents.AI.Workflows 1.23).
        new RoslynRule(
            id: "MAF-AP-WF-002",
            name: "Two [MessageHandler] methods for the same message type — throws at run time",
            severity: AntiPatternSeverity.Error,
            scan: (root, file) =>
            {
                var findings = new List<AntiPatternFinding>();
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var byMessageType = cls.Members.OfType<MethodDeclarationSyntax>()
                        .Where(m => IsMessageHandler(m) && m.ParameterList.Parameters.FirstOrDefault()?.Type is not null)
                        .GroupBy(m => MessageTypeKey(m.ParameterList.Parameters[0].Type!), StringComparer.Ordinal);
                    foreach (var handlers in byMessageType)
                    {
                        var first = handlers.First();
                        foreach (var duplicate in handlers.Skip(1))
                        {
                            var loc = duplicate.Identifier.GetLocation().GetLineSpan();
                            findings.Add(new AntiPatternFinding(
                                "MAF-AP-WF-002",
                                "Two [MessageHandler] methods for the same message type — throws at run time",
                                AntiPatternSeverity.Error,
                                file,
                                loc.StartLinePosition.Line + 1,
                                $"{cls.Identifier.ValueText}.{duplicate.Identifier.ValueText} handles {handlers.Key}, already handled by {first.Identifier.ValueText}"));
                        }
                    }
                }
                return findings;
            }),

        // MAF-AP-WF-003 — `async void` [MessageHandler]. The generator accepts a void
        // handler, so nothing flags it, but the workflow cannot await it: the handler counts
        // as finished at its first await, and an exception after that escapes the workflow.
        new RoslynRule(
            id: "MAF-AP-WF-003",
            name: "async void [MessageHandler] — the workflow cannot await it",
            severity: AntiPatternSeverity.Warning,
            scan: (root, file) => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => IsMessageHandler(m)
                    && m.Modifiers.Any(modifier => modifier.RawKind == (int)SyntaxKind.AsyncKeyword)
                    && m.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
                .Select(m => new AntiPatternFinding(
                    "MAF-AP-WF-003",
                    "async void [MessageHandler] — the workflow cannot await it",
                    AntiPatternSeverity.Warning,
                    file,
                    m.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    $"async void {m.Identifier.ValueText}(...) — return ValueTask instead"))
                .ToList()),
    };

    private static string? InvokedName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// <c>RunAsync(message, session, …)</c>: the session is the second positional argument
    /// or the one named <c>session:</c>; a <c>null</c>/<c>default</c> literal is no session.
    /// </summary>
    private static bool PassesSession(Microsoft.CodeAnalysis.SeparatedSyntaxList<ArgumentSyntax> args)
    {
        var session = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "session")
            ?? (args.Count > 1 && args[1].NameColon is null ? args[1] : null);
        return session is not null && session.Expression is not (
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression or (int)SyntaxKind.DefaultLiteralExpression }
            or DefaultExpressionSyntax);
    }

    private static readonly HashSet<string> ApprovalResponseFactories = new(StringComparer.Ordinal)
    {
        "CreateResponse", "CreateAlwaysApproveToolResponse", "CreateAlwaysApproveToolWithArgumentsResponse",
    };

    /// <summary>
    /// True when <paramref name="expression"/> builds a tool-approval response, directly or
    /// through a local in <paramref name="scope"/> that is initialized with one, assigned
    /// one, or given one with <c>Add</c>/<c>AddRange</c> (up to <paramref name="depth"/> hops).
    /// </summary>
    private static bool CarriesApprovalResponse(ExpressionSyntax expression, Microsoft.CodeAnalysis.SyntaxNode scope, int depth)
    {
        if (expression.DescendantNodesAndSelf().Any(IsApprovalResponse)) return true;
        if (depth == 0) return false;
        var names = expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(n => n.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in scope.DescendantNodes())
        {
            IEnumerable<ExpressionSyntax> sources = node switch
            {
                VariableDeclaratorSyntax { Initializer.Value: var value } d when names.Contains(d.Identifier.ValueText) => [value],
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } a when names.Contains(left.Identifier.ValueText) => [a.Right],
                InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target, Name.Identifier.ValueText: "Add" or "AddRange" },
                } add when names.Contains(target.Identifier.ValueText) => add.ArgumentList.Arguments.Select(a => a.Expression),
                _ => [],
            };
            if (sources.Any(source => CarriesApprovalResponse(source, scope, depth - 1))) return true;
        }
        return false;
    }

    // `request.CreateResponse(approved)` builds a ToolApprovalResponseContent; Azure Functions'
    // and ASP.NET's `req.CreateResponse()` / `CreateResponse(HttpStatusCode.OK)` do not.
    private static bool IsApprovalResponse(Microsoft.CodeAnalysis.SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax call when InvokedName(call) is { } name && ApprovalResponseFactories.Contains(name) =>
            name != "CreateResponse"
            || (call.ArgumentList.Arguments.Count > 0
                && !call.ArgumentList.Arguments[0].Expression.ToString().Contains("StatusCode", StringComparison.Ordinal)),
        BaseObjectCreationExpressionSyntax creation => ObjectCreationTypeName(creation) == "ToolApprovalResponseContent",
        _ => false,
    };

    private static bool IsMessageHandler(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
        {
            var name = attribute.Name.ToString();
            name = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
            return name is "MessageHandler" or "MessageHandlerAttribute";
        });

    /// <summary>
    /// The runtime message type a handler registers for, from syntax: qualifiers and
    /// <c>global::</c> dropped, keywords mapped to their CLR names (<c>string</c> and
    /// <c>System.String</c> collide, as they do at run time).
    /// </summary>
    private static string MessageTypeKey(TypeSyntax type) => type switch
    {
        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText switch
        {
            "string" => "String", "object" => "Object", "bool" => "Boolean", "char" => "Char",
            "byte" => "Byte", "sbyte" => "SByte", "short" => "Int16", "ushort" => "UInt16",
            "int" => "Int32", "uint" => "UInt32", "long" => "Int64", "ulong" => "UInt64",
            "float" => "Single", "double" => "Double", "decimal" => "Decimal",
            var keyword => keyword,
        },
        QualifiedNameSyntax qualified => MessageTypeKey(qualified.Right),
        AliasQualifiedNameSyntax alias => MessageTypeKey(alias.Name),
        GenericNameSyntax generic =>
            $"{generic.Identifier.ValueText}<{string.Join(",", generic.TypeArgumentList.Arguments.Select(MessageTypeKey))}>",
        // `string?` is the same runtime type; for value types `T?` is Nullable<T>, a different one.
        NullableTypeSyntax { ElementType: PredefinedTypeSyntax { Keyword.ValueText: "string" or "object" } } nullable =>
            MessageTypeKey(nullable.ElementType),
        NullableTypeSyntax nullable => MessageTypeKey(nullable.ElementType) + "?",
        ArrayTypeSyntax array => MessageTypeKey(array.ElementType) + string.Concat(array.RankSpecifiers.Select(r => r.ToString())),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    /// <summary>
    /// The constructed type's simple name for an object-creation, resolving the
    /// target-typed <c>new()</c> form only when the declared type is syntactically
    /// visible (a <c>T x = new() {…}</c> variable declaration). Pure-source — returns
    /// null when the type can't be determined without a semantic model.
    /// </summary>
    private static string? ObjectCreationTypeName(BaseObjectCreationExpressionSyntax oce)
    {
        static string Simple(string t) => t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;

        if (oce is ObjectCreationExpressionSyntax explicitOce)
            return Simple(explicitOce.Type.ToString());

        // Target-typed `new()` — only resolvable from a visible declared type.
        if (oce.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } })
            return Simple(vd.Type.ToString());

        return null;
    }

    /// <summary>
    /// True if a base-type syntax names the MAF workflow <c>Executor</c> base in any
    /// form: plain <c>Executor</c>, generic <c>Executor&lt;…&gt;</c>, namespace-qualified
    /// (<c>Microsoft.Agents.AI.Workflows.Executor</c>), or alias-qualified. Shared by
    /// the WF-001 scanner rule AND <see cref="Rewriters.ExecutorSealedRewriter"/> so
    /// detector and rewriter agree on which classes derive from Executor — without
    /// this, the scanner would flag a generic-base Executor as auto-fixable while the
    /// rewriter silently no-op'd it (a detector⇆rewriter parity break).
    /// </summary>
    internal static bool IsExecutorBaseType(TypeSyntax type) => SimpleTypeName(type) == "Executor";

    /// <summary>The rightmost simple identifier of a (possibly generic / qualified) type name.</summary>
    private static string? SimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        QualifiedNameSyntax q => SimpleTypeName(q.Right),
        AliasQualifiedNameSyntax a => SimpleTypeName(a.Name),
        _ => null,
    };

    /// <summary>True if a bracketed indexer argument list is the single string key "EnableSensitiveData".</summary>
    private static bool IsEnableSensitiveDataKey(BracketedArgumentListSyntax args)
        => args.Arguments.Count == 1
           && args.Arguments[0].Expression is LiteralExpressionSyntax keyLit
           && keyLit.RawKind == (int)SyntaxKind.StringLiteralExpression
           && keyLit.Token.ValueText == "EnableSensitiveData";

    /// <summary>
    /// Inclusive line ranges inside an <c>#if</c> whose condition satisfies
    /// <paramref name="conditionMatches"/>. Used to suppress findings on code that is
    /// intentionally fenced behind a build symbol — <c>DEVUI_ENABLED</c> for DevUI,
    /// <c>DEBUG</c>/<c>DEVELOPMENT</c> for dev-only diagnostics (e.g. SEC-003's
    /// <c>EnableSensitiveData</c>, which the migration guide blesses inside a dev guard).
    /// </summary>
    internal static IReadOnlyList<(int Start, int End)> CollectGuardedLineRanges(
        Microsoft.CodeAnalysis.SyntaxNode root, Func<string, bool> conditionMatches)
    {
        var ranges = new List<(int Start, int End)>();
        // Each stack entry = the open line of a guarded branch, or -1 for a non-guarded
        // branch (sentinel keeps #if/#endif nesting balanced). A guarded branch's range
        // ends at the NEXT directive at its level (#elif/#else/#endif) — crucially NOT
        // spanning into the alternate (#else/#elif) arm, which is the *unguarded*
        // (typically production) branch and must stay scannable.
        var openStack = new Stack<int>();

        static int LineOf(Microsoft.CodeAnalysis.SyntaxNode n)
            => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Close the current branch: pop it and, if it was guarded, record its range.
        static void CloseBranch(Stack<int> stack, List<(int Start, int End)> ranges, int closeLine)
        {
            if (stack.Count == 0) return;
            var openLine = stack.Pop();
            if (openLine > 0 && closeLine >= openLine) ranges.Add((openLine, closeLine));
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.HasStructure) continue;
            switch (trivia.GetStructure())
            {
                case IfDirectiveTriviaSyntax ifDir:
                    openStack.Push(conditionMatches(ifDir.Condition.ToString()) ? LineOf(ifDir) : -1);
                    break;

                case ElifDirectiveTriviaSyntax elifDir:
                    // End the prior branch at the line before this directive, then open
                    // the elif branch (guarded only if its own condition matches).
                    CloseBranch(openStack, ranges, LineOf(elifDir) - 1);
                    openStack.Push(conditionMatches(elifDir.Condition.ToString()) ? LineOf(elifDir) : -1);
                    break;

                case ElseDirectiveTriviaSyntax elseDir:
                    // End the prior branch; an #else arm is treated as NOT guarded — the
                    // common `#if DEBUG … #else <prod> … #endif` leaves the production arm
                    // scannable. (Exotic `#if !SYMBOL` inversions are out of scope for this
                    // substring heuristic.)
                    CloseBranch(openStack, ranges, LineOf(elseDir) - 1);
                    openStack.Push(-1);
                    break;

                case EndIfDirectiveTriviaSyntax endIf:
                    CloseBranch(openStack, ranges, LineOf(endIf));
                    break;
            }
        }

        return ranges;
    }

    // -------------------------------------------------------------------------
    // Report formatting
    // -------------------------------------------------------------------------

    private static string FormatReport(
        string repoPath,
        IReadOnlyList<AntiPatternFinding> findings,
        SourceFileWalker.ScanBudget? budget = null,
        int skippedUnreadable = 0)
    {
        var byLevel = findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.ToList());
        var errors = byLevel.GetValueOrDefault(AntiPatternSeverity.Error, []);
        var warnings = byLevel.GetValueOrDefault(AntiPatternSeverity.Warning, []);
        var infos = byLevel.GetValueOrDefault(AntiPatternSeverity.Info, []);

        var sb = new StringBuilder();
        sb.AppendLine($"## MAF anti-pattern scan — `{repoPath}`");
        sb.AppendLine();

        // SEC-04: never present a capped / partial walk as a complete scan.
        if (budget?.IncompleteNote(skippedUnreadable) is { } note)
        {
            sb.AppendLine($"> ⚠️ **Scan incomplete** — {note}. Findings below reflect a partial scan, not the whole repo.");
            sb.AppendLine();
        }
        sb.AppendLine($"| Severity | Count |");
        sb.AppendLine($"|---|---:|");
        sb.AppendLine($"| ❌ Error | {errors.Count} |");
        sb.AppendLine($"| ⚠️ Warning | {warnings.Count} |");
        sb.AppendLine($"| ℹ️ Info | {infos.Count} |");
        sb.AppendLine();

        AppendSection(sb, "❌ Errors (must fix)", errors);
        AppendSection(sb, "⚠️ Warnings (should fix)", warnings);
        AppendSection(sb, "ℹ️ Info (consider)", infos);

        if (findings.Count == 0)
            sb.AppendLine("✅ No anti-patterns detected. Codebase follows current MAF best practices for the scanned ruleset.");

        sb.AppendLine();
        sb.AppendLine("**Rule list source:** `maf://skills?name=maf-anti-pattern-scanner`");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IReadOnlyList<AntiPatternFinding> findings)
    {
        if (findings.Count == 0) return;
        sb.AppendLine($"### {heading}");
        sb.AppendLine();
        sb.AppendLine("| Rule | File | Line | Match |");
        sb.AppendLine("|---|---|---:|---|");
        foreach (var f in findings)
        {
            // The Match column echoes the matched source — redact it if it looks
            // like a secret (e.g. the SEC-002 key literal), and neutralize backticks
            // so the value can't break the markdown code span / table cell.
            // SEC-21: flatten newlines FIRST — a multi-line match (e.g. SEC-003's
            // `assign.ToString()`) would otherwise break the table row and inject the
            // remaining source lines as raw markdown. Secret check runs on the original.
            var flat = f.Match.Replace("\r", string.Empty).Replace('\n', ' ');
            var match = LooksLikeSecret(f.Match) ? "(redacted)" : flat.Replace('`', '\'').Replace('|', '\\');
            sb.AppendLine($"| `{f.RuleId}` — {f.RuleName} | `{f.File.Replace('`', '\'')}` | {f.Line} | `{match}` |");
        }
        sb.AppendLine();
    }
}

// ----------------------------------------------------------------------------
// Rule types
// ----------------------------------------------------------------------------

public enum AntiPatternSeverity { Error, Warning, Info }

public sealed record AntiPatternFinding(
    string RuleId,
    string RuleName,
    AntiPatternSeverity Severity,
    string File,
    int Line,
    string Match);

public abstract class AntiPatternRule
{
    public string Id { get; protected init; } = "";
    public string Name { get; protected init; } = "";
    public AntiPatternSeverity Severity { get; protected init; }
    public bool SkipInTestFiles { get; protected init; }

    public abstract IEnumerable<AntiPatternFinding> Scan(
        string source, Microsoft.CodeAnalysis.SyntaxNode root, string file);
}

internal sealed class RegexRule : AntiPatternRule
{
    private readonly Regex? _pattern;
    private readonly Func<string, Microsoft.CodeAnalysis.SyntaxNode, string, IEnumerable<AntiPatternFinding>>? _custom;
    private readonly bool _matchesStringContent;
    private readonly Func<Microsoft.CodeAnalysis.SyntaxNode, bool>? _appliesToFile;

    public RegexRule(
        string id, string name, AntiPatternSeverity severity,
        Regex? pattern, bool skipInTestFiles = false,
        Func<string, Microsoft.CodeAnalysis.SyntaxNode, string, IEnumerable<AntiPatternFinding>>? customScan = null,
        bool matchesStringContent = false,
        Func<Microsoft.CodeAnalysis.SyntaxNode, bool>? appliesToFile = null)
    {
        Id = id;
        Name = name;
        Severity = severity;
        SkipInTestFiles = skipInTestFiles;
        _pattern = pattern;
        _custom = customScan;
        _matchesStringContent = matchesStringContent;
        _appliesToFile = appliesToFile;
    }

    public override IEnumerable<AntiPatternFinding> Scan(string source, Microsoft.CodeAnalysis.SyntaxNode root, string file)
    {
        if (_appliesToFile is not null && !_appliesToFile(root))
            return [];

        if (_custom is not null)
            return _custom(source, root, file);

        if (_pattern is null) return [];

        var findings = new List<AntiPatternFinding>();
        var lines = source.Split('\n');
        var lineStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            // Inspect EVERY match on the line, not just the first: a real code
            // match can follow a comment/string match on the same physical line.
            // Emit the first match that isn't in skipped trivia (one per line,
            // preserving prior behavior); only skip the line if all are filtered.
            foreach (Match m in _pattern.Matches(lines[i]))
            {
                if (IsInCommentOrSkippedLiteral(root, lineStart + m.Index, _matchesStringContent))
                    continue;
                findings.Add(new AntiPatternFinding(Id, Name, Severity, file, i + 1, m.Value));
                break;
            }
            lineStart += lines[i].Length + 1; // +1 for the '\n' that Split removed
        }
        return findings;
    }

    /// <summary>
    /// True if the matched span lies in a comment (skip for ALL rules — a
    /// commented-out / documented pattern is not a real finding) or, for rules
    /// that don't target string content, inside a string-literal token (a pattern
    /// inside a string is data, not code). The hard-coded-secret rule sets
    /// <paramref name="matchesStringContent"/> so it keeps matching string bodies.
    /// Offset-based (not line-based), so it's robust to CRLF/LF/CR differences.
    /// </summary>
    private static bool IsInCommentOrSkippedLiteral(
        Microsoft.CodeAnalysis.SyntaxNode root, int offset, bool matchesStringContent)
    {
        if (offset < 0 || offset >= root.FullSpan.End) return false;

        var triviaKind = (SyntaxKind)root.FindTrivia(offset).RawKind;
        if (triviaKind is SyntaxKind.SingleLineCommentTrivia
            or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia
            or SyntaxKind.MultiLineDocumentationCommentTrivia)
            return true;

        if (matchesStringContent) return false;

        var token = root.FindToken(offset);
        var tokenKind = (SyntaxKind)token.RawKind;
        return token.Span.Contains(offset)
            && tokenKind is SyntaxKind.StringLiteralToken
                or SyntaxKind.InterpolatedStringTextToken
                or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8StringLiteralToken;
    }
}

internal sealed class RoslynRule : AntiPatternRule
{
    private readonly Func<Microsoft.CodeAnalysis.SyntaxNode, string, IEnumerable<AntiPatternFinding>> _scan;

    public RoslynRule(
        string id, string name, AntiPatternSeverity severity,
        Func<Microsoft.CodeAnalysis.SyntaxNode, string, IEnumerable<AntiPatternFinding>> scan,
        bool skipInTestFiles = false)
    {
        Id = id;
        Name = name;
        Severity = severity;
        SkipInTestFiles = skipInTestFiles;
        _scan = scan;
    }

    public override IEnumerable<AntiPatternFinding> Scan(string _, Microsoft.CodeAnalysis.SyntaxNode root, string file)
        => _scan(root, file);
}
