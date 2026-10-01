using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: <c>MafDetectSourceFramework</c>.
///
/// **Phase 1 of the cross-framework migration assistant** (Semantic Kernel → MAF).
/// Scans a repo for Semantic Kernel usage — package references + source-level
/// constructs — and returns a *construct inventory* where every finding is tagged
/// with its migration strategy:
/// <list type="bullet">
///   <item><b>🌉 bridgeable</b> — reuse the SK code as-is via an interop shim
///         (e.g. <c>KernelFunction.AsAIFunction()</c>, <c>IChatCompletionService.AsChatClient()</c>).</item>
///   <item><b>🔁 rewrite</b> — a mechanical SK→MAF API swap (agent creation,
///         threads→sessions, invocation, options, DI).</item>
///   <item><b>🏗 re-architect</b> — no 1:1 mapping; needs MAF workflows/middleware
///         (group chat, planners, function-invocation filters).</item>
/// </list>
/// The inventory is the migration work-list the <c>maf-migrate-from</c> flow drives.
/// Grounded in <c>docs/migration/from-semantic-kernel-to-maf.md</c> (served at
/// <c>maf://migrate-from?source=semantic-kernel</c>). Detection is purely syntactic
/// (Roslyn) and scoped to files that import a <c>Microsoft.SemanticKernel</c> namespace —
/// either a local <c>using</c> or, repo-wide, a <c>global using</c> (the GlobalUsings.cs
/// pattern) — so it does not over-match unrelated code.
///
/// NOTE: the <c>source</c> dimension is intentionally NOT abstracted yet — this is the
/// only source framework today, so the detector is deliberately SK-specific (no
/// <c>ISourceFrameworkDetector</c> seam). When a second source (AutoGen) lands, extract
/// that seam and select an implementation by the <c>--source</c> value (whose supported
/// set already lives in <see cref="MigrationSources"/>); the plumbing is not a broken
/// multi-source contract, it is forward-compatible input validation.
/// </summary>
[McpServerToolType]
public sealed class SemanticKernelDetectorTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Detect Semantic Kernel usage in a repo and inventory every construct that a
        migration to Microsoft Agent Framework would touch, tagged by migration
        strategy (bridgeable / rewrite / re-architect) with file:line, plus an
        overall complexity verdict (EASY / MEDIUM / HARD). The work-list for the
        `maf-migrate-from` flow. Read-only; nothing is written.

        Input:
          - repoPath: absolute path to the repo, or a .csproj / .sln file (its
            containing directory is scanned).
          - format: "markdown" (default) for a human report, or "json".

        Returns the inventory. If no Semantic Kernel usage is found, says so.
        """)]
    public string MafDetectSourceFramework(
        [Description("Absolute path to the repo directory, or a .csproj / .sln file (its directory is scanned).")] string repoPath,
        [Description("Output format: \"markdown\" (default) or \"json\".")] string format = "markdown")
    {
        // Accept a project/solution FILE path by scanning its containing directory —
        // the advertised surface is "repo or project", but the scan walks a directory.
        if (TryNormalizeToDirectory(repoPath) is { } dir)
            repoPath = dir;

        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
            return format.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { error = err })
                : err;

        var report = Scan(repoPath);
        return format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(report.ToJson(), new JsonSerializerOptions { WriteIndented = true })
            : report.ToMarkdown();
    }

    /// <summary>
    /// If <paramref name="path"/> is an existing <c>.csproj</c> / <c>.sln</c> file, returns
    /// its containing directory (the scan walks a directory, not a file); otherwise null so
    /// the caller leaves the input untouched for <see cref="PathGuard"/> to validate/report.
    /// </summary>
    private static string? TryNormalizeToDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (File.Exists(path)
                && (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
                // GetFullPath first so a RELATIVE project path (e.g. `migrate-scan MyApp.csproj`)
                // resolves to its real containing directory instead of "" / null.
                return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch { /* fall through — let PathGuard report the malformed path */ }
        return null;
    }

    // -------------------------------------------------------------------------
    // Pure scan core (testable without MCP)
    // -------------------------------------------------------------------------

    /// <summary>Scans a repo: csproj package references + every .cs file's SK constructs.</summary>
    internal static SkScanResult Scan(string repoPath)
        => Scan(repoPath, new SourceFileWalker.ScanBudget(), new SourceFileWalker.ScanBudget());

    /// <summary>
    /// Budget-injectable overload — the production entry point above always
    /// passes two fresh default budgets; this overload exists so tests can
    /// pass small budgets and exercise the two-pass truncation interaction
    /// for real (see the caveat below) without needing a multi-hundred-MB
    /// fixture to actually trip the default 500 MB aggregate cap.
    /// </summary>
    internal static SkScanResult Scan(string repoPath, SourceFileWalker.ScanBudget pass1Budget, SourceFileWalker.ScanBudget pass2Budget)
    {
        var packages = DetectPackages(repoPath);

        // F-10 (2026-07-19 security assessment) — this previously read every
        // accepted .cs file into one in-memory list before analysis. With a
        // 50,000-file / 10 MB-per-file walker cap and no aggregate limit, the
        // theoretical retained set was unbounded (hundreds of GB). Two passes
        // bound this: pass 1 only checks for a global SK using (never retains
        // a source body); pass 2 re-enumerates and analyzes one file at a
        // time. Each pass gets its OWN ScanBudget (see the comment at pass 2
        // below for why) — reviewed correctness caveat: on a repo large
        // enough to hit the aggregate cap, if the file declaring the global
        // using sorts AFTER pass 1's budget is exhausted, pass 1 never sees
        // it and repoEstablishesSk stays false even though pass 2 may still
        // fully analyze in-budget files that were relying on that global
        // using for SK-context — those files can be silently under-detected,
        // not merely "not looked at." `Truncated` still fires in this case
        // (either budget hit its cap), so the result is flagged incomplete,
        // just not with this exact mechanism spelled out. Requires several
        // hundred MB of .cs source to trigger in production (see
        // SemanticKernelDetectorToolTests for a budget-injected repro at
        // ordinary file sizes); not fixed here as a two-pass redesign to
        // close it precisely would trade this narrow, flagged edge case for
        // meaningfully more complexity.
        var repoEstablishesSk = false;
        foreach (var file in SourceFileWalker.EnumerateCsFiles(repoPath, excludes: null, pass1Budget))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (HasGlobalSemanticKernelUsing(text))
            {
                repoEstablishesSk = true;
                break; // global using found — no need to keep reading for pass 1's purpose
            }
        }

        var constructs = new List<SkConstruct>();
        // Independent budget for pass 2: pass 1's early-break-on-global-using
        // means its FilesSeen/BytesAccepted don't reflect the full walk, and
        // pass 2 needs its own accounting to decide whether IT truncated.
        foreach (var file in SourceFileWalker.EnumerateCsFiles(repoPath, excludes: null, pass2Budget))
        {
            string rel;
            try { rel = SourceFileWalker.MakeRelative(repoPath, file); }
            catch (InvalidOperationException) { rel = file; }
            string text;
            // A file locked / deleted / access-denied mid-scan must not abort the whole
            // repo scan — skip it, consistent with the csproj/props IO handling.
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            constructs.AddRange(AnalyzeSource(text, rel, repoEstablishesSk));
        }

        return new SkScanResult(packages, constructs, Truncated: pass1Budget.Truncated || pass2Budget.Truncated);
    }

    // Shared with the MAF coverage-horizon check; see PackageVersionScanner.
    internal const long MaxProjectFileBytes = PackageVersionScanner.MaxProjectFileBytes;

    /// <summary>
    /// Reads every .csproj under the repo and returns the Semantic Kernel package
    /// references it finds (id + version). Pure XML — no build/restore.
    /// </summary>
    internal static IReadOnlyList<SkPackage> DetectPackages(string repoPath) =>
        PackageVersionScanner
            // NuGet package IDs are case-insensitive.
            .Detect(repoPath, id => id.StartsWith("Microsoft.SemanticKernel", StringComparison.OrdinalIgnoreCase))
            .Select(p => new SkPackage(p.Id, p.Version))
            .ToList();

    /// <summary>
    /// Inventories the Semantic Kernel constructs in one C# file. Returns at most one
    /// entry per construct KIND (first occurrence line + count), so a file with five
    /// <c>[KernelFunction]</c> methods reports one "[KernelFunction] plugin method" ×5.
    /// Scoped to files that import a <c>Microsoft.SemanticKernel</c> namespace.
    /// </summary>
    public static IReadOnlyList<SkConstruct> AnalyzeSource(
        string source, string fileName = "<inline>", bool repoEstablishesSkContext = false)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        // SK-context if the repo established it globally (a global using elsewhere) OR this
        // file imports a Microsoft.SemanticKernel namespace locally.
        var importsSk = repoEstablishesSkContext
            || root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(u => ImportsSemanticKernel(u.Name?.ToString()));
        if (!importsSk)
            return Array.Empty<SkConstruct>();

        // kind -> (strategy, note, firstLine, count)
        var hits = new Dictionary<string, (MigrationStrategy Strategy, string Note, int Line, int Count)>(StringComparer.Ordinal);

        void Record(string kind, MigrationStrategy strategy, string note, int line)
        {
            if (hits.TryGetValue(kind, out var cur))
                hits[kind] = (cur.Strategy, cur.Note, Math.Min(cur.Line, line), cur.Count + 1);
            else
                hits[kind] = (strategy, note, line, 1);
        }

        static int LineOf(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // [KernelFunction] plugin methods — 🌉 reuse via AsAIFunction(), or rewrite as a plain [Description] method.
                case AttributeSyntax attr when SimpleAttrName(attr) is "KernelFunction" or "KernelFunctionFromMethod":
                    Record("[KernelFunction] plugin method", MigrationStrategy.Bridgeable,
                        "Reuse via `KernelFunction.AsAIFunction()`, or rewrite as a plain `[Description]` method passed to `tools:`.",
                        LineOf(attr));
                    break;

                // A reference to a known SK type — but only in *type position*. Skip
                // member-access names (`agent.Kernel`), assignment / object-initializer
                // LHS (`new X { Kernel = … }`), and named-argument labels, so a property
                // called `Kernel` isn't miscounted as a usage of the `Kernel` TYPE.
                case GenericNameSyntax gen when IsTypeReference(gen):
                    Classify(gen.Identifier.ValueText, LineOf(gen));
                    break;
                case IdentifierNameSyntax id when IsTypeReference(id):
                    Classify(id.Identifier.ValueText, LineOf(id));
                    break;

                // SK invocation pattern. Split kernel-function invocation
                // (`kernel.InvokeAsync(fn)`) from agent invocation (`agent.InvokeAsync(input)`)
                // — they map to different MAF code. The kernel-vs-agent decision keys on the
                // chain's ROOT identifier (whole-word), not a substring, so `kernelAgent.X`
                // resolves as an agent (correct) rather than by accident. Matched on the
                // INVOCATION (a real call), not a bare member access / method-group reference.
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m }
                        when m.Name.Identifier.ValueText is "InvokeAsync" or "InvokeStreamingAsync":
                    if (IsKernelReceiver(m.Expression))
                        Record("Kernel function invocation (`kernel.Invoke*Async`)", MigrationStrategy.Rewrite,
                            "→ call the tool method directly, or let MAF's automatic function-calling loop invoke it (no manual `kernel.Invoke`).",
                            LineOf(m));
                    else
                        Record("Agent invocation (`Invoke*Async`)", MigrationStrategy.Rewrite,
                            "→ `RunAsync` / `RunStreamingAsync`; adapt to `AgentResponse` / `AgentResponseUpdate`.",
                            LineOf(m));
                    break;

                // Kernel-level OpenTelemetry wiring -> 🔁 .UseOpenTelemetry() on the IChatClient
                // chain. Gated on a kernel / kernel-builder ROOT receiver (whole-identifier
                // match) so host-level `services.AddOpenTelemetry()` — or a same-substring
                // var like `kernelMetricsServices` — is NOT flagged. Matched on the call site.
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ot }
                        when ot.Name.Identifier.ValueText == "AddOpenTelemetry" && IsKernelReceiver(ot.Expression):
                    Record("Observability (`AddOpenTelemetry`)", MigrationStrategy.Rewrite,
                        "→ `.UseOpenTelemetry()` on the `IChatClient` build chain (subscribe to source `Microsoft.Extensions.AI`).",
                        LineOf(ot));
                    break;
            }
        }

        // Routes a type-position name to its construct. ORDER IS LOAD-BEARING — do not
        // reorder the four checks below:
        //   1. exact filter interfaces (before the suffix checks, which they'd never match anyway)
        //   2. `*PromptExecutionSettings` suffix  — MUST run before the KnownTypes lookup
        //      (no PES type is in KnownTypes, but keep the suffix family together up front)
        //   3. `KnownTypes` exact-name lookup     — the tailored `*AgentThread` entries
        //      (AgentThread / OpenAIAssistantAgentThread / …) live here and MUST win over
        //   4. the `*AgentThread` suffix fallback  — generic catch for the rest
        //      (e.g. ChatHistoryAgentThread). If this ran before step 3, the tailored
        //      thread entries would be shadowed by the generic note.
        void Classify(string typeName, int line)
        {
            // Function-invocation filters → MAF agent middleware. A clean port (defined
            // target), so a 🔁 rewrite rather than a 🏗 re-architecture.
            if (typeName is "IFunctionInvocationFilter" or "IAutoFunctionInvocationFilter" or "IPromptRenderFilter")
            {
                Record($"Filter (`{typeName}`)", MigrationStrategy.Rewrite,
                    "→ MAF agent middleware (function-calling / agent-run) via `.AsBuilder().Use(...)`.", line);
                return;
            }
            // Provider execution settings (`OpenAIPromptExecutionSettings`,
            // `AzureOpenAIPromptExecutionSettings`, …). Distinctive SK suffix — safe to
            // match broadly inside an SK-importing file. This is also where structured
            // output lives (the `responseFormat` property).
            if (typeName.EndsWith("PromptExecutionSettings", StringComparison.Ordinal))
            {
                Record("Execution settings (`*PromptExecutionSettings`)", MigrationStrategy.Rewrite,
                    "→ `ChatOptions` / `ChatClientAgentRunOptions` (`MaxTokens` → `MaxOutputTokens`; structured-output `responseFormat` → `ChatResponseFormat.ForJsonSchema<T>()` or typed `RunAsync<T>`).", line);
                return;
            }
            if (KnownTypes.TryGetValue(typeName, out var rule))
            {
                Record(rule.Kind, rule.Strategy, rule.Note, line);
                return;
            }
            // Fallback: any other concrete `*AgentThread` subtype not tailored above
            // (e.g. `ChatHistoryAgentThread`). `AgentThread` is SK-distinctive enough to
            // suffix-match inside an SK-importing file; catches `var t = new XAgentThread()`.
            // Unlike the `*Planner` suffix (removed — `CapacityPlanner` was a real user-type
            // collision), `*AgentThread` is far more SK-specific, so a user type named
            // `…AgentThread` flagging here is an accepted, low-likelihood false positive
            // (pinned by UserTypeEndingInAgentThread_IsFlagged_AcceptedTradeoff).
            if (typeName.EndsWith("AgentThread", StringComparison.Ordinal) && typeName != "AgentThread")
                Record($"Thread (`{typeName}`)", MigrationStrategy.Rewrite,
                    "→ `AgentSession` via `await agent.CreateSessionAsync()` — the agent owns the session.", line);
        }

        // True when a simple name is used as a TYPE reference, not a member/property name
        // (`x.Kernel`), an assignment / object-initializer target (`{ Kernel = … }`), a
        // named-argument label (`Kernel: …`), or a `nameof(Kernel)` argument (a string, not a
        // type). Without this gate, a property *named* like an SK type — or a `nameof` of one
        // — inflates that type's occurrence count and can mis-tag its strategy.
        static bool IsTypeReference(SimpleNameSyntax name) => name.Parent switch
        {
            MemberAccessExpressionSyntax ma when ReferenceEquals(ma.Name, name) => false,
            AssignmentExpressionSyntax asn when ReferenceEquals(asn.Left, name) => false,
            NameColonSyntax => false,
            NameEqualsSyntax => false,
            ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } } => false,
            _ => true,
        };

        return hits
            .Select(kv => new SkConstruct(kv.Key, kv.Value.Strategy, fileName, kv.Value.Line, kv.Value.Note, kv.Value.Count))
            .OrderBy(c => (int)c.Strategy)
            .ThenBy(c => c.Line)
            .ToList();
    }

    private static string SimpleAttrName(AttributeSyntax attr)
    {
        var n = attr.Name.ToString();
        n = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
        return n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^"Attribute".Length] : n;
    }

    /// <summary>
    /// True when a using-directive name imports a <c>Microsoft.SemanticKernel</c> namespace,
    /// tolerating the <c>global::</c> qualifier (e.g. <c>using global::Microsoft.SemanticKernel;</c>).
    /// Namespaces are case-sensitive, so the comparison is Ordinal.
    /// </summary>
    private static bool ImportsSemanticKernel(string? usingName)
    {
        if (usingName is null) return false;
        if (usingName.StartsWith("global::", StringComparison.Ordinal))
            usingName = usingName["global::".Length..];
        return usingName.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the source declares a <c>global using</c> that imports a
    /// <c>Microsoft.SemanticKernel</c> namespace (the GlobalUsings.cs pattern) — which
    /// makes SK in-scope for every file in the project, not just this one.
    /// </summary>
    private static bool HasGlobalSemanticKernelUsing(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)
                      && ImportsSemanticKernel(u.Name?.ToString()));
    }

    /// <summary>
    /// Common identifier names for a Kernel / kernel-builder receiver. Matched
    /// whole-word (case-insensitive) so <c>kernel</c>/<c>Kernel</c>/<c>kernelBuilder</c>
    /// hit but a same-substring host variable like <c>kernelMetricsServices</c> does not.
    /// Purely syntactic (no SemanticModel) — a deliberately small, high-signal set; an
    /// idiosyncratic alias (<c>kb</c>, <c>sk</c>) is a tolerated false-negative.
    /// </summary>
    private static readonly HashSet<string> KernelReceiverNames =
        new(StringComparer.OrdinalIgnoreCase) { "kernel", "kernelBuilder", "_kernel", "_kernelBuilder" };

    /// <summary>True when the chain's root identifier names a Kernel / kernel-builder.</summary>
    private static bool IsKernelReceiver(ExpressionSyntax receiver)
    {
        var root = RootIdentifierName(receiver);
        return root is not null && KernelReceiverNames.Contains(root);
    }

    /// <summary>
    /// Walks to the left-most identifier of a member-access / invocation / element-access
    /// chain (the chain's "root receiver"), seeing through parentheses and casts. Returns
    /// null if the root isn't a plain identifier (e.g. <c>this</c>, a literal, a cast target).
    /// </summary>
    private static string? RootIdentifierName(ExpressionSyntax? expr)
    {
        while (expr is not null)
        {
            switch (expr)
            {
                case IdentifierNameSyntax id: return id.Identifier.ValueText;
                // `this.kernel` / `base.kernel` — the member name IS the meaningful receiver
                // identifier (a field/property access on the instance), so resolve to it.
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } self:
                    return self.Name.Identifier.ValueText;
                case MemberAccessExpressionSyntax ma: expr = ma.Expression; break;
                case InvocationExpressionSyntax inv: expr = inv.Expression; break;
                case ElementAccessExpressionSyntax ea: expr = ea.Expression; break;
                case ParenthesizedExpressionSyntax pe: expr = pe.Expression; break;
                case CastExpressionSyntax ce: expr = ce.Expression; break;
                case PostfixUnaryExpressionSyntax pue: expr = pue.Operand; break; // x!.Y
                default: return null;
            }
        }
        return null;
    }

    /// <summary>SK type name → migration construct + strategy. Distinctive SK names only.</summary>
    private static readonly IReadOnlyDictionary<string, (string Kind, MigrationStrategy Strategy, string Note)> KnownTypes =
        new Dictionary<string, (string, MigrationStrategy, string)>(StringComparer.Ordinal)
        {
            ["AgentGroupChat"]         = ("Multi-agent group chat (`AgentGroupChat`)", MigrationStrategy.Rearchitect, "→ a MAF group-chat workflow (`AgentWorkflowBuilder.CreateGroupChatBuilderWith(...)`)."),
            ["ChatCompletionAgent"]    = ("SK agent (`ChatCompletionAgent`)", MigrationStrategy.Rewrite, "→ `chatClient.AsAIAgent(instructions:, tools:)` (drop the `Kernel`)."),
            ["AzureAIAgent"]           = ("SK agent (`AzureAIAgent`)", MigrationStrategy.Rewrite, "→ `aiProjectClient.AsAIAgent(model:, instructions:)`."),
            ["OpenAIAssistantAgent"]   = ("SK agent (`OpenAIAssistantAgent`)", MigrationStrategy.Rewrite, "→ `assistantClient.CreateAIAgentAsync(instructions:)`."),
            ["IChatCompletionService"] = ("Chat service (`IChatCompletionService`)", MigrationStrategy.Bridgeable, "→ bridge with `.AsChatClient()`, or use an `IChatClient` provider directly."),
            // Threads → sessions (the agent owns the session in MAF). §3/§4 of the guide.
            ["AgentThread"]                = ("Thread (`AgentThread`)", MigrationStrategy.Rewrite, "→ `AgentSession` via `await agent.CreateSessionAsync()` — the agent creates the session."),
            ["OpenAIAssistantAgentThread"] = ("Thread (`OpenAIAssistantAgentThread`)", MigrationStrategy.Rewrite, "→ `await agent.CreateSessionAsync()`; for cleanup, track `session.ConversationId` and call the provider SDK."),
            ["AzureAIAgentThread"]         = ("Thread (`AzureAIAgentThread`)", MigrationStrategy.Rewrite, "→ `await agent.CreateSessionAsync()`."),
            ["OpenAIResponseAgentThread"]  = ("Thread (`OpenAIResponseAgentThread`)", MigrationStrategy.Rewrite, "→ `await agent.CreateSessionAsync()`."),
            ["KernelArguments"]        = ("Run options (`KernelArguments`)", MigrationStrategy.Rewrite, "→ `ChatOptions` / `ChatClientAgentRunOptions`."),
            ["AgentInvokeOptions"]     = ("Run options (`AgentInvokeOptions`)", MigrationStrategy.Rewrite, "→ `ChatClientAgentRunOptions` (wraps `ChatOptions`)."),
            // Planners were removed in SK — re-architect onto MAF's automatic function-calling
            // loop. Explicit names only (no `*Planner` suffix match — that flagged user types
            // like `CapacityPlanner`).
            ["FunctionCallingStepwisePlanner"] = ("Planner (`FunctionCallingStepwisePlanner`)", MigrationStrategy.Rearchitect, "Planners were removed — re-architect onto MAF's automatic function-calling loop."),
            ["StepwisePlanner"]        = ("Planner (`StepwisePlanner`)", MigrationStrategy.Rearchitect, "Planners were removed — re-architect onto MAF's automatic function-calling loop."),
            ["HandlebarsPlanner"]      = ("Planner (`HandlebarsPlanner`)", MigrationStrategy.Rearchitect, "Planners were removed — re-architect onto MAF's automatic function-calling loop."),
            ["SequentialPlanner"]      = ("Planner (`SequentialPlanner`)", MigrationStrategy.Rearchitect, "Planners were removed — re-architect onto MAF's automatic function-calling loop."),
            ["ActionPlanner"]          = ("Planner (`ActionPlanner`)", MigrationStrategy.Rearchitect, "Planners were removed — re-architect onto MAF's automatic function-calling loop."),
            ["KernelPlugin"]           = ("Plugin wrapper (`KernelPlugin`)", MigrationStrategy.Bridgeable, "→ no plugin concept; bridge each function via `.AsAIFunction()` or pass methods to `tools:`."),
            ["KernelPluginFactory"]    = ("Plugin factory (`KernelPluginFactory`)", MigrationStrategy.Rewrite, "→ pass methods directly to `tools:` (no plugin wrapper)."),
            ["KernelFunctionFactory"]  = ("Function factory (`KernelFunctionFactory`)", MigrationStrategy.Rewrite, "→ `AIFunctionFactory.Create(...)`."),
            // `Kernel` itself — distinctive enough inside an SK-importing file.
            ["Kernel"]                 = ("Kernel (`Kernel`)", MigrationStrategy.Rewrite, "Removed in MAF — create the agent straight from the chat client; delete the `Kernel`."),
            ["IKernelBuilder"]         = ("Kernel builder (`IKernelBuilder`)", MigrationStrategy.Rewrite, "Removed in MAF — no kernel to build."),
            // Chat history persistence — maps to a MAF provider / session serialization.
            ["ChatHistory"]            = ("Chat history (`ChatHistory`)", MigrationStrategy.Rewrite, "→ `ChatHistoryProvider` / `InMemoryChatHistoryProvider`, or `await agent.SerializeSessionAsync(session)` / `DeserializeSessionAsync(...)` for whole-session persistence (the sync `SerializeSession` shown in some docs does not exist on 1.3.0+)."),
            // Vector stores / RAG — re-architect onto a Context Provider.
            ["IVectorStore"]           = ("Vector store (`IVectorStore`)", MigrationStrategy.Rearchitect, "→ a `TextSearchProvider` attached via `ChatClientAgentOptions.AIContextProviders` (storage via `Microsoft.Extensions.VectorData`)."),
            ["ITextSearch"]            = ("Text search (`ITextSearch`)", MigrationStrategy.Rearchitect, "→ `TextSearchProvider` (RAG via Context Providers)."),
            ["VectorStoreTextSearch"]  = ("Vector text search (`VectorStoreTextSearch`)", MigrationStrategy.Rearchitect, "→ `TextSearchProvider` + `AIContextProviders`."),
            // Prompt templates — no built-in template engine in MAF.
            ["KernelPromptTemplate"]   = ("Prompt template (`KernelPromptTemplate`)", MigrationStrategy.Rearchitect, "No built-in MAF template engine — render the template yourself (e.g. Handlebars.NET / Scriban) and pass the result as `instructions`, or move dynamic data into a Context Provider."),
            ["PromptTemplateConfig"]   = ("Prompt template (`PromptTemplateConfig`)", MigrationStrategy.Rearchitect, "No built-in MAF template engine — render externally and pass as `instructions`."),
            ["HandlebarsPromptTemplateFactory"] = ("Prompt template (Handlebars)", MigrationStrategy.Rearchitect, "No built-in MAF template engine — render Handlebars yourself and pass the result as `instructions`."),
            ["LiquidPromptTemplateFactory"]     = ("Prompt template (Liquid)", MigrationStrategy.Rearchitect, "No built-in MAF template engine — render Liquid yourself and pass the result as `instructions`."),
            ["KernelFunctionFromPrompt"]        = ("Prompt-template function (`KernelFunctionFromPrompt`)", MigrationStrategy.Rearchitect, "No built-in template engine — render the prompt and expose it as a plain tool/method, or move dynamic context into a Context Provider."),
        };

    // -------------------------------------------------------------------------
    // Result model + rendering
    // -------------------------------------------------------------------------

    internal sealed record SkScanResult(IReadOnlyList<SkPackage> Packages, IReadOnlyList<SkConstruct> Constructs, bool Truncated = false)
    {
        public bool SemanticKernelDetected => Packages.Count > 0 || Constructs.Count > 0;

        /// <summary>Occurrence count above which a bridge-only repo still escalates EASY → MEDIUM.</summary>
        public const int LargeFootprintThreshold = 8;

        public int TotalOccurrences => Constructs.Sum(c => c.Count);
        public int Bridgeable => Constructs.Count(c => c.Strategy == MigrationStrategy.Bridgeable);
        public int Rewrite => Constructs.Count(c => c.Strategy == MigrationStrategy.Rewrite);
        public int Rearchitect => Constructs.Count(c => c.Strategy == MigrationStrategy.Rearchitect);

        /// <summary>
        /// Distinct re-architect construct KINDS — collapses the same kind appearing in
        /// multiple files (per-file records) to one, so the HARD threshold counts genuinely
        /// different re-architectures, matching the <see cref="Verdict"/> doc.
        /// </summary>
        private int DistinctRearchitectKinds =>
            Constructs.Where(c => c.Strategy == MigrationStrategy.Rearchitect)
                      .Select(c => c.Kind).Distinct(StringComparer.Ordinal).Count();

        /// <summary>
        /// EASY — only bridges / a tiny rewrite footprint.
        /// MEDIUM — mechanical rewrites, a single re-architecture, or a large footprint (&gt; <see cref="LargeFootprintThreshold"/> occurrences).
        /// HARD — two or more <em>distinct</em> re-architecture kinds (e.g. group chat + planners + vector store).
        /// A lone re-architecture — even if it appears in several files — is MEDIUM, not HARD:
        /// one redesign is tractable, several distinct ones compound.
        /// </summary>
        public string Verdict =>
            DistinctRearchitectKinds >= 2 ? "HARD"
            : (DistinctRearchitectKinds >= 1 || Rewrite > 0 || TotalOccurrences > LargeFootprintThreshold) ? "MEDIUM"
            : "EASY";

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Semantic Kernel → MAF — migration scan");
            sb.AppendLine();
            if (!SemanticKernelDetected)
            {
                sb.AppendLine(Truncated
                    ? "⚠️ No Semantic Kernel usage detected **in the files scanned before hitting the scan's " +
                      "file-count or aggregate-size budget** — this is a partial result, not a clean verdict."
                    : "✅ No Semantic Kernel usage detected (no `Microsoft.SemanticKernel` package reference or source import). Nothing to migrate from SK.");
                return sb.ToString();
            }

            if (Packages.Count > 0)
            {
                sb.AppendLine("**Detected packages:**");
                foreach (var p in Packages) sb.AppendLine($"- `{p.Id}` {p.Version}");
                sb.AppendLine();
            }

            sb.AppendLine($"**Migration complexity: {Verdict}** — {Constructs.Count} construct kind(s), {TotalOccurrences} occurrence(s): " +
                          $"🌉 {Bridgeable} bridgeable · 🔁 {Rewrite} rewrite · 🏗 {Rearchitect} re-architect.");
            sb.AppendLine();

            if (Truncated)
            {
                sb.AppendLine("> ⚠️ **Scan incomplete** — this repository exceeds the scan's file-count or " +
                              "aggregate-size budget. Results above are a partial inventory, not a complete one.");
                sb.AppendLine();
            }

            AppendGroup(sb, MigrationStrategy.Bridgeable, "🌉 Bridgeable — reuse the SK code via an interop shim (gradual / side-by-side)");
            AppendGroup(sb, MigrationStrategy.Rewrite, "🔁 Rewrite — mechanical SK→MAF API swap");
            AppendGroup(sb, MigrationStrategy.Rearchitect, "🏗 Re-architect — no 1:1; redesign onto MAF workflows / middleware");

            sb.AppendLine("> **Next:** run the `maf-migrate-from` prompt (source: `semantic-kernel`) in an MCP client to turn this into a `migration-plan.md` and a side-by-side MAF project. Mappings: `maf://migrate-from?source=semantic-kernel`.");
            return sb.ToString();
        }

        private void AppendGroup(StringBuilder sb, MigrationStrategy strategy, string heading)
        {
            var items = Constructs.Where(c => c.Strategy == strategy).ToList();
            if (items.Count == 0) return;
            sb.AppendLine($"### {heading}");
            sb.AppendLine();
            foreach (var c in items)
            {
                var count = c.Count > 1 ? $" ×{c.Count}" : "";
                sb.AppendLine($"- **{c.Kind}**{count} — `{c.File}:{c.Line}`");
                sb.AppendLine($"  - {c.Note}");
            }
            sb.AppendLine();
        }

        public object ToJson() => new
        {
            semantic_kernel_detected = SemanticKernelDetected,
            truncated = Truncated,
            verdict = Verdict,
            packages = Packages.Select(p => new { id = p.Id, version = p.Version }),
            counts = new { bridgeable = Bridgeable, rewrite = Rewrite, rearchitect = Rearchitect, occurrences = TotalOccurrences },
            constructs = Constructs.Select(c => new
            {
                kind = c.Kind,
                strategy = c.Strategy.ToString().ToLowerInvariant(),
                file = c.File,
                line = c.Line,
                count = c.Count,
                note = c.Note,
            }),
        };
    }
}

/// <summary>Migration strategy for a Semantic Kernel construct. Order = report order.</summary>
public enum MigrationStrategy
{
    /// <summary>Reuse as-is via an interop shim (AsAIFunction / AsChatClient).</summary>
    Bridgeable = 0,
    /// <summary>Mechanical SK→MAF API swap.</summary>
    Rewrite = 1,
    /// <summary>No 1:1 mapping — redesign onto MAF workflows / middleware.</summary>
    Rearchitect = 2,
}

/// <summary>One Semantic Kernel package reference found in a csproj.</summary>
public sealed record SkPackage(string Id, string Version);

/// <summary>One Semantic Kernel construct kind found in a file (first line + occurrence count).</summary>
public sealed record SkConstruct(
    string Kind,
    MigrationStrategy Strategy,
    string File,
    int Line,
    string Note,
    int Count);
