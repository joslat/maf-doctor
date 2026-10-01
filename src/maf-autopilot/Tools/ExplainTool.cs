using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MafDoctor.Data;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafExplain
///
/// Paste a MAF snippet, get an annotated explanation. Walks every MAF API
/// touchpoint (type reference, invocation, attribute usage), cross-references
/// the obsolete-API registry, and emits a deterministic line-by-line report
/// citing the canonical guide section for each pattern.
///
/// This is the reverse direction of MafApiSafety: instead of "is X safe?",
/// it asks "given THIS snippet, explain everything MAF-specific in it."
/// </summary>
[McpServerToolType]
public sealed class ExplainTool
{
    private readonly RegistryService _registry;

    public ExplainTool(RegistryService registry)
    {
        _registry = registry;
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Annotate a MAF code snippet with line-by-line explanations.

        Walks every type reference, invocation, and attribute in the snippet,
        cross-references the obsolete-API registry, and emits a structured
        report: which pieces are canonical MAF patterns, which are
        deprecated, and which guide section explains each.

        Input:
          - snippet: any C# code that touches MAF. Doesn't need to compile;
            the analyzer reads syntax-only and is tolerant of incomplete code.

        Returns a markdown report with per-line findings, registry citations
        (when matched), and a summary of MAF surface contact.
        """)]
    public string MafExplain(
        [Description("C# code snippet to explain. Doesn't need to compile.")] string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            return "Error: snippet must not be empty.";

        // Phase 5.1 — DoS guard: a 1 GB snippet would otherwise flow into
        // Roslyn's parser without an upstream cap. 256 KB is the snippet-class
        // ceiling from §5.6 — well above any realistic paste.
        try { BoundedInput.Validate(snippet, BoundedInput.SnippetBytes, nameof(snippet)); }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }

        var findings = AnalyzeSnippet(snippet, _registry);
        return FormatReport(snippet, findings);
    }

    // -------------------------------------------------------------------------
    // Pure analysis core (testable without files / DI)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks the snippet's syntax tree and produces an ordered list of MAF
    /// touchpoint findings: type references, member access, invocations,
    /// attribute usage. Each finding carries a registry match (if any) and
    /// a canonical guide section citation (if known).
    /// </summary>
    public static IReadOnlyList<ExplainFinding> AnalyzeSnippet(string snippet, RegistryService registry)
    {
        var tree = SourceParsing.Parse(snippet);
        var root = tree.GetRoot();
        var findings = new List<ExplainFinding>();
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax oce:
                    AddIdentifier(oce.Type.ToString(), oce.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        FindingKind.TypeReference, "constructor call", registry, findings, seenIdentifiers);
                    break;

                case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax mae:
                    var methodName = mae.Name.Identifier.ValueText;
                    AddIdentifier(methodName, mae.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        FindingKind.MethodInvocation, "method call", registry, findings, seenIdentifiers);
                    break;

                case AttributeSyntax attr:
                    var attrName = attr.Name.ToString();
                    var simple = attrName.Contains('.') ? attrName[(attrName.LastIndexOf('.') + 1)..] : attrName;
                    AddIdentifier(simple, attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        FindingKind.AttributeUsage, "attribute", registry, findings, seenIdentifiers);
                    break;
            }
        }

        return findings;
    }

    private static void AddIdentifier(
        string identifier,
        int line,
        FindingKind kind,
        string contextDescription,
        RegistryService registry,
        List<ExplainFinding> findings,
        HashSet<string> seenIdentifiers)
    {
        // De-duplicate per identifier (same name at multiple lines collapses).
        if (!seenIdentifiers.Add($"{identifier}@{line}")) return;

        // Surface only identifiers we can confidently attribute to MAF: either
        // a registry entry matches the name, or it appears in the curated allowlist.
        // (The previous substring fallback created noise on customer codebases that
        // happened to use words like `Agent` / `Workflow` / `Message` in their own
        // domain types.)
        var registryMatch = registry.SearchByApiName(identifier).FirstOrDefault();
        if (registryMatch is null && !IsLikelyMafSurface(identifier)) return;
        var guideSection =
            !string.IsNullOrWhiteSpace(registryMatch?.GuideSection)
                ? registryMatch!.GuideSection
                : GuessGuideSection(identifier)?.ToString();
        var canonicalNote = GetCanonicalNote(identifier);

        findings.Add(new ExplainFinding(
            Line: line,
            Identifier: identifier,
            Kind: kind,
            ContextDescription: contextDescription,
            RegistryMatchId: registryMatch?.Id,
            RegistryMatchNote: registryMatch is null ? null : $"{registryMatch.FixDescription.Split('\n')[0]}",
            GuideSection: guideSection,
            CanonicalNote: canonicalNote));
    }

    /// <summary>
    /// True for identifiers we can confidently attribute to the MAF surface.
    /// The previous substring fallback (`Contains("agent")` etc.) generated
    /// noise on customer codebases that use `UserAgent` / `MessageQueue` /
    /// `WorkflowEngine` for their own domain types. We now allowlist only.
    /// </summary>
    private static bool IsLikelyMafSurface(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;
        return KnownMafIdentifiers.Contains(identifier);
    }

    private static readonly HashSet<string> KnownMafIdentifiers = new(StringComparer.Ordinal)
    {
        "ChatClientAgent", "AIAgentBuilder", "WorkflowBuilder",
        "InProcessExecution", "AgentSession", "AgentThread",
        "MessageHandler", "MessageHandlerAttribute",
        "StreamsMessage", "YieldsMessage",
        "AddFanOutEdge", "AddFanInBarrierEdge", "AddEdge",
        "RunAsync", "RunStreamingAsync", "StreamAsync",
        "CreateSessionAsync", "GetNewThread", "SerializeSession", "SerializeSessionAsync",
        "AgentRunUpdateEvent", "AgentResponseUpdateEvent",
        "UseOpenTelemetry", "AddA2AServer", "MapA2A", "MapA2AHttpJson", "MapA2AJsonRpc",
        "DefaultAzureCredential", "ManagedIdentityCredential",
        "EnableSensitiveData", "ChatClientAgentOptions", "ChatOptions",
        "AIContextProvider", "ChatHistoryProvider", "ProviderSessionState",
        "ApprovalRequiredAIFunction", "FunctionApprovalRequestContent", "ToolApprovalRequestContent",
    };

    private static int? GuessGuideSection(string identifier)
    {
        var lower = identifier.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("fanin") || lower.Contains("fanout") => 9,
            _ when lower.Contains("session") || lower.Contains("thread") => 6,
            _ when lower.Contains("stream") => 17,
            _ when lower.Contains("a2a") => 5,
            _ when lower.Contains("approval") => 12,
            _ when lower.Contains("telemetry") || lower.Contains("opentelemetry") => 13,
            _ when lower.Contains("contextprovider") || lower.Contains("memory") => 7,
            _ when lower.Contains("middleware") => 8,
            _ when lower.Contains("executor") || lower.Contains("messagehandler") => 9,
            _ when lower.Contains("chatclientagent") || lower.Contains("aiagent") => 3,
            _ when lower.Contains("instructions") || lower.Contains("chatoptions") => 4,
            _ => null,
        };
    }

    private static string? GetCanonicalNote(string identifier) => identifier switch
    {
        "AgentThread" or "GetNewThread" =>
            "Removed in 1.3.0. Use `AgentSession` via `await agent.CreateSessionAsync(ct)`.",
        "SerializeSession" =>
            "Now async — use `await agent.SerializeSessionAsync(session)`. (Official docs are wrong.)",
        "StreamAsync" =>
            "Renamed to `RunStreamingAsync()` in 1.3.0.",
        "AgentRunUpdateEvent" =>
            "Renamed to `AgentResponseUpdateEvent` in 1.3.0.",
        "StreamsMessage" or "YieldsMessage" =>
            "Both attributes were REMOVED in 1.3.0 — delete them, executor patterns no longer need them.",
        "DefaultAzureCredential" =>
            "Avoid in production. Use `ManagedIdentityCredential` for Azure-hosted, `WorkloadIdentityCredential` for K8s.",
        "ManagedIdentityCredential" =>
            "✅ The recommended production credential for Azure-hosted MAF agents.",
        "UseOpenTelemetry" =>
            "✅ Required for production observability. Wrap the `IChatClient` so every transitive call is traced.",
        "AddFanInBarrierEdge" =>
            "Argument order in 1.3.0: `(sources, target)`. The legacy `(target, sources)` overload is `[Obsolete]` and triggers CS0618.",
        "MessageHandler" or "MessageHandlerAttribute" =>
            "✅ Marks an executor method as a workflow handler. MUST return `Task<T>` or `ValueTask<T>` — `void` / non-generic causes silent fan-in starvation.",
        "ChatClientAgent" =>
            "✅ The canonical MAF agent base. `Instructions` lives inside `ChatOptions`, not at the options top level.",
        _ => null,
    };

    // -------------------------------------------------------------------------
    // Report formatting
    // -------------------------------------------------------------------------

    private static string FormatReport(string snippet, IReadOnlyList<ExplainFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 MAF code explanation");
        sb.AppendLine();

        if (findings.Count == 0)
        {
            sb.AppendLine("ℹ️ No MAF API surface contact detected in the snippet. (If you expected matches, the analyzer may not know about this identifier — propose an addition via `maf-obsolete-api-registry`.)");
            return sb.ToString();
        }

        sb.AppendLine($"Identified **{findings.Count}** MAF touchpoint(s):");
        sb.AppendLine();
        sb.AppendLine("| Line | Identifier | Kind | Registry | Guide §");
        sb.AppendLine("|---:|---|---|---|---:|");

        foreach (var f in findings.OrderBy(x => x.Line))
        {
            var registryCell = f.RegistryMatchId is null ? "—" : $"`{f.RegistryMatchId}`";
            var guideCell = f.GuideSection ?? "—";
            sb.AppendLine($"| {f.Line} | `{f.Identifier}` | {f.Kind} | {registryCell} | {guideCell} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Notes");
        sb.AppendLine();
        foreach (var f in findings.OrderBy(x => x.Line))
        {
            if (f.CanonicalNote is null && f.RegistryMatchNote is null) continue;
            sb.AppendLine($"**Line {f.Line} — `{f.Identifier}`:**");
            if (f.CanonicalNote is not null) sb.AppendLine($"- {f.CanonicalNote}");
            if (f.RegistryMatchNote is not null) sb.AppendLine($"- Registry says: {f.RegistryMatchNote}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public enum FindingKind { TypeReference, MethodInvocation, AttributeUsage }

public sealed record ExplainFinding(
    int Line,
    string Identifier,
    FindingKind Kind,
    string ContextDescription,
    string? RegistryMatchId,
    string? RegistryMatchNote,
    string? GuideSection,
    string? CanonicalNote);
