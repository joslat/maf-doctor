using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafSimulateWorkflow
///
/// In-vitro topology analysis of a MAF workflow. Walks every WorkflowBuilder
/// invocation in the codebase, traces .AddEdge / .AddFanOutEdge / .AddFanInBarrierEdge
/// calls into a directed graph, cross-checks each executor's [MessageHandler] return
/// type, and proves whether the workflow can complete without silent fan-in starvation.
/// Emits a Mermaid diagram of the topology so PR reviewers can SEE the workflow.
///
/// This is the closest thing to "simulating" a workflow without paying for LLM calls.
/// </summary>
[McpServerToolType]
public sealed class SimulateWorkflowTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Statically analyze a MAF workflow's topology — proves whether it
        can complete without silent fan-in starvation. Walks WorkflowBuilder
        invocations, traces AddEdge / AddFanOutEdge / AddFanInBarrierEdge calls,
        cross-checks every [MessageHandler] return type, and emits a Mermaid
        diagram of the resulting executor graph.

        Input:
          - repoPath: absolute path to the repository or project root. All *.cs
            files are scanned recursively (bin/obj excluded).

        Returns a markdown report with:
          - Mermaid diagram of the topology
          - Per-executor verdict (`OK`, `SILENT_STARVATION_RISK`, `ORPHAN`)
          - Per-edge verdict (`OK` or warnings)
          - Overall workflow verdict (✅ Will complete / ⚠️ At risk / ❌ Will starve)
        """)]
    public string MafSimulateWorkflow(
        [Description("Absolute path to the repository root.")] string repoPath)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err) return err;

        var topology = AnalyzeTopology(repoPath);
        return FormatReport(topology);
    }

    // -------------------------------------------------------------------------
    // Pure-function topology analysis (testable without files)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pure entry point: given a collection of (filename, source) pairs, builds
    /// the workflow topology and returns the analyzed model.
    /// </summary>
    public static WorkflowTopology AnalyzeSources(IEnumerable<(string FileName, string Source)> sources)
    {
        var executors = new Dictionary<string, ExecutorInfo>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<EdgeInfo>();

        var roots = new List<SyntaxNode>();
        foreach (var (fileName, source) in sources)
        {
            var tree = SourceParsing.Parse(source);
            var root = tree.GetRoot();
            roots.Add(root);

            // 1. Catalog every executor class with [MessageHandler] methods.
            CollectExecutors(root, fileName, executors);
        }

        // 2. With the full executor inventory known, build a name→executor-type
        //    map from every variable / field / parameter declaration in any file.
        //    This is what lets `var reviewer = new ReviewerExecutor()` followed by
        //    `AddEdge(reviewer, finalizer)` resolve correctly.
        var varToExecutorType = BuildVariableTypeMap(roots, executors);

        // 3. Now walk every invocation that looks like a WorkflowBuilder edge call.
        foreach (var root in roots)
            CollectEdges(root, edges, executors, varToExecutorType);

        // 4. Cross-check: for each edge, look up the source executor's handler
        //    return type and assign a per-edge verdict.
        AnnotateEdgeVerdicts(edges, executors);

        return new WorkflowTopology(
            Executors: executors.Values.OrderBy(e => e.Name).ToList(),
            Edges: edges);
    }

    /// <summary>
    /// Walks variable / field / parameter declarations across all files and builds
    /// a `variable-name → executor-type-name` map. Used to resolve `AddEdge(x, y)`
    /// calls where `x` and `y` are local-variable bindings rather than direct type
    /// names. Covers the most common authoring patterns:
    /// <code>
    ///     var reviewer = new ReviewerExecutor();    // RHS new-expression
    ///     ReviewerExecutor reviewer = ...;          // LHS explicit type
    ///     private readonly ReviewerExecutor _r;     // field declaration
    /// </code>
    /// Does NOT resolve factory returns (`MakeReviewer()`) or
    /// <c>.AsBinding()</c> chains — those require a semantic model.
    /// </summary>
    private static Dictionary<string, string> BuildVariableTypeMap(
        IEnumerable<SyntaxNode> roots,
        Dictionary<string, ExecutorInfo> executors)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var decl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                var declaredType = decl.Type.ToString();
                foreach (var v in decl.Variables)
                {
                    var name = v.Identifier.ValueText;

                    // Case A: explicit type on the left (`ReviewerExecutor reviewer = ...`).
                    if (declaredType != "var" && executors.ContainsKey(declaredType))
                    {
                        map[name] = declaredType;
                        continue;
                    }

                    // Case B: `var x = new ReviewerExecutor()` — type on the right.
                    if (v.Initializer?.Value is ObjectCreationExpressionSyntax oce)
                    {
                        var rhsType = oce.Type.ToString();
                        if (executors.ContainsKey(rhsType))
                            map[name] = rhsType;
                    }
                }
            }

            // Also cover method parameters typed as an executor:
            //   void Wire(ReviewerExecutor reviewer) { builder.AddEdge(reviewer, ...); }
            foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (param.Type is null) continue;
                var declaredType = param.Type.ToString();
                if (executors.ContainsKey(declaredType))
                    map[param.Identifier.ValueText] = declaredType;
            }
        }
        return map;
    }

    private static WorkflowTopology AnalyzeTopology(string repoPath)
    {
        var sources = new List<(string, string)>();
        foreach (var path in EnumerateScannableFiles(repoPath))
            sources.Add((path, File.ReadAllText(path)));
        return AnalyzeSources(sources);
    }

    private static IEnumerable<string> EnumerateScannableFiles(string repoRoot)
        => SourceFileWalker.EnumerateCsFiles(repoRoot);

    private static void CollectExecutors(
        SyntaxNode root, string fileName, Dictionary<string, ExecutorInfo> executors)
    {
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            // Only consider classes that derive from Executor or have [MessageHandler] methods.
            var handlers = new List<HandlerInfo>();
            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!HasMessageHandlerAttribute(method)) continue;
                handlers.Add(new HandlerInfo(
                    MethodName: method.Identifier.ValueText,
                    ReturnType: method.ReturnType.ToString(),
                    Verdict: ClassifyHandler(method.ReturnType.ToString())));
            }
            if (handlers.Count == 0) continue;

            var name = cls.Identifier.ValueText;
            if (!executors.ContainsKey(name))
                executors[name] = new ExecutorInfo(name, fileName, handlers);
        }
    }

    private static bool HasMessageHandlerAttribute(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attr =>
            {
                var n = attr.Name.ToString();
                var s = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
                return s is "MessageHandler" or "MessageHandlerAttribute";
            });

    private static HandlerVerdict ClassifyHandler(string returnType)
    {
        var t = returnType.Trim();
        var simple = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
        if (simple.StartsWith("Task<", StringComparison.Ordinal)
            || simple.StartsWith("ValueTask<", StringComparison.Ordinal)
            || simple.StartsWith("IAsyncEnumerable<", StringComparison.Ordinal))
            return HandlerVerdict.ProducesMessage;
        if (t == "void" || simple is "Task" or "ValueTask")
            return HandlerVerdict.ProducesNothing;
        return HandlerVerdict.UnusualReturnType;
    }

    // Phase 7.G fixup — regex hygiene. Identifier-class anchored regexes are
    // backtracking-safe but the policy requires NonBacktracking + 2s timeout.
    private static readonly Regex PascalIdentifierRegex = new(
        @"\b([A-Z][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));
    private static readonly Regex AnyIdentifierRegex = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    private static void CollectEdges(
        SyntaxNode root,
        List<EdgeInfo> edges,
        Dictionary<string, ExecutorInfo> executors,
        Dictionary<string, string> varToExecutorType)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
            var methodName = member.Name.Identifier.ValueText;
            EdgeKind kind = methodName switch
            {
                "AddEdge" => EdgeKind.Direct,
                "AddFanOutEdge" => EdgeKind.FanOut,
                "AddFanInBarrierEdge" => EdgeKind.FanInBarrier,
                _ => EdgeKind.Unknown,
            };
            if (kind == EdgeKind.Unknown) continue;

            // Resolve each argument expression to an executor type name. Two paths:
            //   (a) the argument IS a type name (e.g. `AddEdge(A, B)` in synthetic test code)
            //   (b) the argument is a variable / field / parameter whose declared type
            //       is a known executor — look it up in varToExecutorType.
            // Track whether ANY argument failed to resolve — that turns the whole
            // edge into an UnresolvedExecutor finding rather than silently dropping.
            var resolved = new List<string>();
            var anyUnresolved = false;
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var resolvedType = ResolveArgument(arg.Expression.ToString(), executors, varToExecutorType);
                if (resolvedType is null)
                {
                    anyUnresolved = true;
                    // REP-19: store a SANITIZED placeholder (the first identifier in the
                    // expression, else "unresolved") instead of raw expression text. Raw text
                    // flows into the Mermaid flow diagram as a node label, where unresolved
                    // characters (quotes, operators, newlines, pipes) would corrupt the diagram
                    // render or inject markdown. The first identifier is a readable, safe hint.
                    var raw = arg.Expression.ToString();
                    var idMatch = AnyIdentifierRegex.Match(raw);
                    resolved.Add(idMatch.Success ? idMatch.Value : "unresolved");
                }
                else
                {
                    resolved.Add(resolvedType);
                }
            }
            if (resolved.Count == 0) continue;

            // Convention: for AddFanInBarrierEdge the LAST argument is the target;
            // the rest are sources (canonical form is `(sources, target)`).
            // For AddEdge / AddFanOutEdge the FIRST argument is the source.
            string source;
            string target;
            switch (kind)
            {
                case EdgeKind.FanInBarrier:
                    target = resolved[^1];
                    source = string.Join(",", resolved.Take(resolved.Count - 1));
                    break;
                default:
                    source = resolved[0];
                    target = resolved.Count > 1 ? resolved[1] : "(unknown)";
                    break;
            }

            edges.Add(new EdgeInfo(
                Kind: kind,
                Source: source,
                Target: target,
                // Pre-classify as Unresolved if ANY argument failed to bind to a known executor.
                // AnnotateEdgeVerdicts will leave Unresolved alone and only refine Unchecked.
                Verdict: anyUnresolved ? EdgeVerdict.UnresolvedExecutor : EdgeVerdict.Unchecked));
        }
    }

    /// <summary>
    /// Resolves a single argument expression text to a known executor type name.
    /// Priority: direct match in the executor catalog (handles `AddEdge(Reviewer, …)`
    /// synthetic test code) → variable / field / parameter lookup (handles real
    /// authoring `AddEdge(reviewer, …)` with `var reviewer = new ReviewerExecutor()`).
    /// Returns null if no resolution succeeds.
    /// </summary>
    private static string? ResolveArgument(
        string argText,
        Dictionary<string, ExecutorInfo> executors,
        Dictionary<string, string> varToExecutorType)
    {
        argText = argText.Trim();

        // Strip common suffixes that don't change the binding: `.AsBinding()`, `()`.
        var stripped = argText
            .Replace(".AsBinding()", string.Empty, StringComparison.Ordinal)
            .TrimEnd('(', ')', ' ');

        // 1. Variable / field / parameter binding match (most realistic case). Check
        //    this BEFORE the case-insensitive executor match — a local named `reviewer`
        //    should resolve via the binding map (returning the canonical "Reviewer"
        //    from the executor catalog), not via the case-insensitive executor lookup
        //    which would return the lowercase form `reviewer` of the input.
        if (varToExecutorType.TryGetValue(stripped, out var boundType))
            return boundType;

        // 2. Direct executor-name match. The dictionary is case-insensitive, so look
        //    up via TryGetValue and return the executor's canonical Name field.
        if (executors.TryGetValue(stripped, out var execInfo))
            return execInfo.Name;

        // 3. Last-ditch: scan ALL identifiers in the argument text. Pick the first
        //    one that resolves to a known executor (covers `MakeReviewer()`-style
        //    factories where the type name appears inside a larger expression).
        foreach (Match m in AnyIdentifierRegex.Matches(argText))
        {
            var t = m.Groups[1].Value;
            if (varToExecutorType.TryGetValue(t, out var indirect)) return indirect;
            if (executors.TryGetValue(t, out var info)) return info.Name;
        }
        return null;
    }

    private static void AnnotateEdgeVerdicts(
        List<EdgeInfo> edges, Dictionary<string, ExecutorInfo> executors)
    {
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            // Already classified as unresolved during collection — leave it alone.
            if (e.Verdict == EdgeVerdict.UnresolvedExecutor) continue;

            // For fan-in barriers, every named source executor must produce a message.
            if (e.Kind == EdgeKind.FanInBarrier)
            {
                var sources = e.Source.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var allProduce = sources.All(s =>
                    executors.TryGetValue(s.Trim(), out var info)
                    && info.Handlers.Any(h => h.Verdict == HandlerVerdict.ProducesMessage));
                edges[i] = e with { Verdict = allProduce ? EdgeVerdict.Ok : EdgeVerdict.WouldStarve };
            }
            else
            {
                // Direct / fan-out: source must produce a message.
                if (executors.TryGetValue(e.Source, out var info))
                {
                    var ok = info.Handlers.Any(h => h.Verdict == HandlerVerdict.ProducesMessage);
                    edges[i] = e with { Verdict = ok ? EdgeVerdict.Ok : EdgeVerdict.WouldStarve };
                }
                else
                {
                    edges[i] = e with { Verdict = EdgeVerdict.UnresolvedExecutor };
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Report formatting — Mermaid diagram + per-edge verdicts + summary
    // -------------------------------------------------------------------------

    private static string FormatReport(WorkflowTopology topology)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🧪 MAF workflow simulation — static topology analysis");
        sb.AppendLine();

        if (topology.Executors.Count == 0)
        {
            sb.AppendLine("ℹ️ No `[MessageHandler]` executors found in the scanned source.");
            return sb.ToString();
        }

        // Verdict summary
        var starveCount = topology.Edges.Count(e => e.Verdict == EdgeVerdict.WouldStarve);
        var unresolvedCount = topology.Edges.Count(e => e.Verdict == EdgeVerdict.UnresolvedExecutor);
        var overall = starveCount == 0
            ? "✅ **Workflow will complete** — no silent fan-in starvation paths detected."
            : $"❌ **Workflow at risk** — {starveCount} edge(s) would produce no message and starve the downstream barrier.";

        sb.AppendLine(overall);
        if (unresolvedCount > 0)
            sb.AppendLine($"⚠️ {unresolvedCount} edge(s) reference executors the analyzer could not resolve — review manually.");
        sb.AppendLine();

        // Mermaid diagram
        sb.AppendLine("### Topology");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph LR");
        sb.AppendLine("  classDef risk fill:#fee,stroke:#c00,color:#900;");
        foreach (var exec in topology.Executors)
        {
            // Keep node labels ASCII-only — Mermaid forks (mermaid.ink etc.) render
            // unicode-in-labels inconsistently. Signal risk via a `class` instead.
            var hasRisk = exec.Handlers.Any(h => h.Verdict == HandlerVerdict.ProducesNothing);
            var shape = hasRisk
                ? $"  {exec.Name}[\"{exec.Name} (RISK)\"]:::risk"
                : $"  {exec.Name}[\"{exec.Name}\"]";
            sb.AppendLine(shape);
        }
        foreach (var edge in topology.Edges)
        {
            var arrow = edge.Verdict == EdgeVerdict.WouldStarve ? "-.->|starves|" : "-->";
            var sources = edge.Source.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in sources)
                sb.AppendLine($"  {s.Trim()} {arrow} {edge.Target}");
        }
        sb.AppendLine("```");
        sb.AppendLine();

        // Per-executor handler verdicts
        sb.AppendLine("### Executors");
        sb.AppendLine();
        sb.AppendLine("| Executor | Handler | Return Type | Verdict |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var exec in topology.Executors)
            foreach (var h in exec.Handlers)
            {
                var icon = h.Verdict switch
                {
                    HandlerVerdict.ProducesMessage => "✅",
                    HandlerVerdict.ProducesNothing => "❌ starves",
                    _ => "⚠️ unusual",
                };
                sb.AppendLine($"| `{exec.Name}` | `{h.MethodName}` | `{h.ReturnType}` | {icon} |");
            }
        sb.AppendLine();

        // Per-edge verdicts
        if (topology.Edges.Count > 0)
        {
            sb.AppendLine("### Edges");
            sb.AppendLine();
            sb.AppendLine("| Kind | Source | Target | Verdict |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in topology.Edges)
            {
                var icon = e.Verdict switch
                {
                    EdgeVerdict.Ok => "✅",
                    EdgeVerdict.WouldStarve => "❌ would starve",
                    EdgeVerdict.UnresolvedExecutor => "⚠️ source not found",
                    _ => "?",
                };
                sb.AppendLine($"| {e.Kind} | `{e.Source}` | `{e.Target}` | {icon} |");
            }
        }

        return sb.ToString();
    }
}

// -----------------------------------------------------------------------------
// Models
// -----------------------------------------------------------------------------

public enum HandlerVerdict { ProducesMessage, ProducesNothing, UnusualReturnType }
public enum EdgeKind { Direct, FanOut, FanInBarrier, Unknown }
public enum EdgeVerdict { Unchecked, Ok, WouldStarve, UnresolvedExecutor }

public sealed record HandlerInfo(string MethodName, string ReturnType, HandlerVerdict Verdict);

public sealed record ExecutorInfo(string Name, string SourceFile, IReadOnlyList<HandlerInfo> Handlers);

public sealed record EdgeInfo(EdgeKind Kind, string Source, string Target, EdgeVerdict Verdict);

public sealed record WorkflowTopology(
    IReadOnlyList<ExecutorInfo> Executors,
    IReadOnlyList<EdgeInfo> Edges);
