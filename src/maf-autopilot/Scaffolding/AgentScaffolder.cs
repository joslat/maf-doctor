using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace MafDoctor.Scaffolding;

/// <summary>
/// Generates clean MAF agent code that passes the anti-pattern scanner
/// and fan-out validator out of the box. Templates are inlined as raw string
/// literals — no T4, no Razor. Substitution points: agent name, namespace.
///
/// The output of this generator is itself a forcing-function for the scanner:
/// any rule that fires against generated code is a generator bug.
/// </summary>
public static class AgentScaffolder
{
    /// <summary>
    /// Result of a scaffold operation — the relative path inside the target
    /// project and the file body.
    /// </summary>
    public sealed record ScaffoldedFile(string RelativePath, string Content);

    /// <summary>
    /// Generates an agent class + xUnit smoke test pair. The agent uses
    /// <c>ChatClientAgent</c> with <c>UseOpenTelemetry</c>, defaults to
    /// <c>ManagedIdentityCredential</c> (commented usage), and includes
    /// a <c>MaxTokens</c> setting so the cost auditor is happy.
    /// </summary>
    public static IReadOnlyList<ScaffoldedFile> ScaffoldAgent(
        string agentName,
        string projectNamespace,
        string? instructions = null)
    {
        if (!IsValidIdentifier(agentName))
            throw new ArgumentException(
                $"'{agentName}' is not a valid C# identifier for an agent name. " +
                "Use PascalCase, letters/digits only, must start with a letter.",
                nameof(agentName));

        // Security (Phase 1.3, C3): projectNamespace flows VERBATIM into the
        // generated file's `namespace {{ns}};` declaration. Without validation, a
        // crafted value like `MyApp; class Evil { static int x = Process.Start("..."); }`
        // produces a compilable file with attacker-controlled types in the user's
        // project tree. Mirrors the inputType/outputType defense in ScaffoldExecutor.
        // Empty/whitespace is permitted because the next line substitutes a safe default.
        if (!string.IsNullOrWhiteSpace(projectNamespace) && !IsValidNamespace(projectNamespace))
            throw new ArgumentException(
                $"'{projectNamespace}' is not a valid C# namespace.",
                nameof(projectNamespace));

        var className = NormalizeName(agentName);
        var ns = string.IsNullOrWhiteSpace(projectNamespace) ? "MyApp.Agents" : projectNamespace;
        var role = instructions ?? "You are a helpful assistant. Respond concisely.";

        return new[]
        {
            new ScaffoldedFile(
                RelativePath: $"Agents/{className}.cs",
                Content: AgentTemplate(className, ns, role)),
            new ScaffoldedFile(
                RelativePath: $"Tests/{className}Tests.cs",
                Content: AgentTestTemplate(className, ns)),
        };
    }

    /// <summary>
    /// Generates a workflow executor class with a <c>[MessageHandler]</c>
    /// method that returns <c>Task&lt;TOutput&gt;</c> — the fan-out-safe pattern.
    /// Pass <paramref name="outputType"/> as the downstream message type
    /// (e.g. <c>"FraudReport"</c> or <c>"string"</c>).
    /// </summary>
    public static IReadOnlyList<ScaffoldedFile> ScaffoldExecutor(
        string executorName,
        string projectNamespace,
        string inputType = "string",
        string outputType = "string")
    {
        if (!IsValidIdentifier(executorName))
            throw new ArgumentException(
                $"'{executorName}' is not a valid C# identifier.", nameof(executorName));

        // Security (Phase 1.3, C3): see ScaffoldAgent above for rationale. Same
        // injection class — projectNamespace splices into a file-scoped namespace
        // declaration that compiles into the user's project tree.
        if (!string.IsNullOrWhiteSpace(projectNamespace) && !IsValidNamespace(projectNamespace))
            throw new ArgumentException(
                $"'{projectNamespace}' is not a valid C# namespace.",
                nameof(projectNamespace));

        // Security: inputType / outputType flow VERBATIM into the generated C# source
        // (Task<{{outputType}}>, default({{outputType}})!, etc.). Without validation, a
        // crafted value like `string> { Process.Start("..."); } class X { Task<string`
        // would inject arbitrary code into the file the developer is about to compile.
        // Validate via Roslyn — must parse as a TypeSyntax with zero diagnostics AND
        // roundtrip identically (no comments, no extra tokens, no nested statements).
        if (!IsValidTypeExpression(inputType))
            throw new ArgumentException(
                $"'{inputType}' is not a valid C# type expression for an executor input.",
                nameof(inputType));
        if (!IsValidTypeExpression(outputType))
            throw new ArgumentException(
                $"'{outputType}' is not a valid C# type expression for an executor output.",
                nameof(outputType));

        var className = NormalizeName(executorName);
        if (!className.EndsWith("Executor", StringComparison.Ordinal))
            className += "Executor";

        var ns = string.IsNullOrWhiteSpace(projectNamespace) ? "MyApp.Workflows" : projectNamespace;

        return new[]
        {
            new ScaffoldedFile(
                RelativePath: $"Workflows/{className}.cs",
                Content: ExecutorTemplate(className, ns, inputType, outputType)),
            new ScaffoldedFile(
                RelativePath: $"Tests/{className}Tests.cs",
                Content: ExecutorTestTemplate(className, ns, inputType, outputType)),
        };
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    private static string AgentTemplate(string className, string ns, string instructions)
    {
        var safeInstructions = EscapeForCSharp(instructions);
        return $$"""
            // <auto-generated by maf-doctor — best-practice defaults: OpenTelemetry wired, MaxTokens set, ManagedIdentityCredential preferred>
            using System.Threading;
            using System.Threading.Tasks;
            using Azure.AI.OpenAI;
            using Azure.Identity;
            using Microsoft.Agents.AI;
            using Microsoft.Extensions.AI;

            namespace {{ns}};

            /// <summary>
            /// {{className}} — a MAF chat-client agent.
            /// Generated by maf-doctor. Tweak the Instructions string + ChatOptions
            /// to fit your domain; keep the OpenTelemetry wiring and MaxTokens cap.
            /// </summary>
            public sealed class {{className}}
            {
                /// <summary>OpenTelemetry source name — register this with your tracer provider via `.AddSource("{{className}}")`.</summary>
                public const string TelemetrySourceName = "{{className}}";

                private readonly ChatClientAgent _agent;

                public {{className}}(IChatClient chatClient)
                {
                    // Wrap the IChatClient with OpenTelemetry FIRST so every call —
                    // including transitive tool invocations — is captured in traces.
                    // sourceName is what shows up in the OpenTelemetry source list;
                    // your tracer provider's .AddSource() call must reference this name.
                    var observableClient = chatClient.AsBuilder()
                        .UseOpenTelemetry(sourceName: TelemetrySourceName)
                        .Build();

                    _agent = new ChatClientAgent(observableClient, new ChatClientAgentOptions
                    {
                        Name = "{{className}}",
                        ChatOptions = new ChatOptions
                        {
                            // Always cap MaxTokens — production cost governance.
                            MaxOutputTokens = 1024,
                            Instructions = "{{safeInstructions}}",
                        },
                    });
                }

                /// <summary>
                /// Run a single user turn. For multi-turn conversations create a
                /// session via <c>await _agent.CreateSessionAsync()</c> and pass it
                /// to <c>RunAsync</c>.
                /// </summary>
                public Task<AgentResponse> RunAsync(string userMessage, CancellationToken ct = default)
                    => _agent.RunAsync(userMessage, ct);

                // -------------------------------------------------------------
                // Production deployment notes — see maf://constraints
                //
                // Register IChatClient in your DI container like this:
                //
                //     services.AddSingleton<IChatClient>(sp =>
                //     {
                //         var endpoint = new Uri(config["Azure:OpenAI:Endpoint"]!);
                //         // Production: ManagedIdentityCredential — NEVER DefaultAzureCredential.
                //         var credential = new ManagedIdentityCredential();
                //         return new AzureOpenAIClient(endpoint, credential)
                //             .AsChatClient(deploymentName: "gpt-4o");
                //     });
                //
                // -------------------------------------------------------------
            }
            """;
    }

    private static string AgentTestTemplate(string className, string ns) => $$"""
        // <auto-generated by maf-doctor — smoke test stub for {{className}}>
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.AI;
        using Xunit;

        namespace {{ns}}.Tests;

        public sealed class {{className}}Tests
        {
            [Fact]
            public async Task RunAsync_EchoesViaMockChatClient()
            {
                // A scripted IChatClient that returns a fixed response — no LLM calls,
                // no API key, no money. Replace with a richer mock as your contract grows.
                var mockClient = new ScriptedChatClient("ok");
                var agent = new {{className}}(mockClient);

                var response = await agent.RunAsync("hello");

                Assert.NotNull(response);
                Assert.Contains("ok", response.Text, StringComparison.Ordinal);
            }

            /// <summary>
            /// Minimal IChatClient that returns a fixed completion. Avoids any external
            /// LLM call so tests are hermetic, fast, and free. Extend with a queue of
            /// responses as your contract grows.
            /// </summary>
            private sealed class ScriptedChatClient : IChatClient
            {
                private readonly string _response;
                public ScriptedChatClient(string response) => _response = response;

                public Task<ChatResponse> GetResponseAsync(
                    IEnumerable<ChatMessage> messages,
                    ChatOptions? options = null,
                    CancellationToken cancellationToken = default)
                    => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));

                public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                    IEnumerable<ChatMessage> messages,
                    ChatOptions? options = null,
                    [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, _response);
                    await Task.CompletedTask;
                }

                public object? GetService(Type serviceType, object? serviceKey = null) => null;
                public void Dispose() { }
            }
        }
        """;

    private static string ExecutorTemplate(string className, string ns, string inputType, string outputType) => $$"""
        // <auto-generated by maf-doctor — fan-out-safe executor with ValueTask<{{outputType}}> return>
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Agents.AI;
        using Microsoft.Agents.AI.Workflows;

        namespace {{ns}};

        /// <summary>
        /// {{className}} — a MAF workflow executor. The [MessageHandler] returns
        /// ValueTask&lt;TOutput&gt;, so on a fan-out edge the returned value is the
        /// downstream message. A void / non-generic return would silently starve the
        /// fan-in barrier, and the source generator rejects Task&lt;TOutput&gt;
        /// (MAFGENWF002). The generator ships in Microsoft.Agents.AI.Workflows.Generators,
        /// a development dependency each project must reference itself.
        ///
        /// Generated by maf-doctor. The fan-out validator pre-clears this shape.
        /// </summary>
        public sealed partial class {{className}} : Executor
        {
            public {{className}}() : base(nameof({{className}})) { }

            [MessageHandler]
            public async ValueTask<{{outputType}}> HandleAsync({{inputType}} input, IWorkflowContext context, CancellationToken cancellationToken)
            {
                // TODO: replace with your transformation. The contract is:
                //   - input arrives on the incoming fan-out edge
                //   - returned value goes downstream to the next executor (or fan-in barrier)
                await Task.Yield(); // keep the method async-shaped
                return default({{outputType}})!;
            }
        }
        """;

    private static string ExecutorTestTemplate(string className, string ns, string inputType, string outputType) => $$"""
        // <auto-generated by maf-doctor — fan-out shape verification for {{className}}>
        using System;
        using System.Linq;
        using System.Reflection;
        using Xunit;

        namespace {{ns}}.Tests;

        public sealed class {{className}}Tests
        {
            [Fact]
            public void HandleAsync_HasFanOutSafeReturnType()
            {
                // Structural smoke test: the handler must return ValueTask<T> — never
                // void / ValueTask non-generic (silent fan-in starvation), and not
                // Task<T>, which the source generator rejects (MAFGENWF002).
                var handler = typeof({{className}})
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Single(m => m.Name == "HandleAsync");

                Assert.True(
                    handler.ReturnType.Name.StartsWith("ValueTask`", StringComparison.Ordinal),
                    $"Handler must return ValueTask<T>, but returns {handler.ReturnType.FullName}.");
            }
        }
        """;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Phase 7.G fixup — regex hygiene. Pattern is structurally backtracking-safe
    // (anchored char-class) but the project-wide invariant requires
    // NonBacktracking + MatchTimeout on every Regex declaration.
    private static readonly Regex IdentifierRegex = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    private static bool IsValidIdentifier(string s) =>
        !string.IsNullOrWhiteSpace(s) && IdentifierRegex.IsMatch(s);

    private static string NormalizeName(string raw)
    {
        // Title-case the first character; preserve the rest.
        var trimmed = raw.Trim();
        return char.IsUpper(trimmed[0]) ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>
    /// Escapes a string for safe embedding inside a C# regular double-quoted string literal
    /// (i.e. <c>"…"</c>, NOT verbatim <c>@"…"</c> or raw <c>"""…"""</c>).
    ///
    /// Replacement order is load-bearing: backslash MUST be doubled first, otherwise a later
    /// escape (e.g. <c>\"</c>) would be re-doubled. The four replacements below are necessary
    /// AND sufficient for plain string literals — see the security note for the false-positive
    /// guard, below.
    ///
    /// <para>
    /// <b>Security — Unicode-escape false positive.</b> A reviewer might flag that this method
    /// does NOT handle Unicode escapes (e.g. <c>"</c> for <c>"</c>) and conclude an attacker
    /// can inject <c>"</c> into <paramref name="s"/> to break out of the generated string
    /// literal. They cannot. Trace: if the runtime value of <paramref name="s"/> contains the
    /// six characters <c>"</c>, the first <see cref="string.Replace(string, string)"/> call
    /// doubles every backslash, producing the seven characters <c>\\u0022</c>. The generated
    /// C# source therefore contains the literal token <c>"\\u0022"</c>. The C# compiler reads
    /// <c>\\</c> as an escape-for-backslash and the remaining <c>u0022</c> as ordinary text;
    /// the runtime string value is <c>"</c> (six characters), <i>not</i> a quote. There is
    /// no path through which an attacker-supplied Unicode escape reaches the C# compiler as a
    /// pre-escape-processing <c>"</c> token. <c>StringEscape_UnicodeEscapeAttempt_RoundTripsAsText</c>
    /// in <c>ScaffolderSecurityTests</c> enforces this invariant.
    /// </para>
    /// </summary>
    internal static string EscapeForCSharp(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "\\r")
         .Replace("\n", "\\n");

    /// <summary>
    /// True if <paramref name="s"/> parses as a single C# type expression with zero diagnostics
    /// and roundtrips identically. Used to gate <see cref="ScaffoldExecutor"/>'s
    /// <c>inputType</c>/<c>outputType</c> parameters before they're spliced into the template.
    /// Reject obvious injection characters BEFORE invoking Roslyn so a tortured payload that
    /// happens to parse cleanly (e.g. a long generic with embedded statements) still fails the
    /// roundtrip check.
    /// </summary>
    internal static bool IsValidTypeExpression(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 200) return false;            // sanity cap; real type names fit
        // Pre-reject characters that have no business in a type expression.
        if (s.IndexOfAny(['{', '}', '(', ')', ';', '=', '\n', '\r', '\0', '"', '\'', '`']) >= 0)
            return false;

        var typeSyntax = SyntaxFactory.ParseTypeName(s);
        if (typeSyntax.ContainsDiagnostics) return false;
        // Roundtrip: the parsed syntax printed back must equal the input (whitespace-trimmed).
        // If Roslyn silently dropped extra tokens (e.g. comments, statements after the type),
        // the printed form will differ. This is the actual injection gate.
        return typeSyntax.ToString().Trim() == s.Trim();
    }

    /// <summary>
    /// True if <paramref name="s"/> parses as a single C# qualified-or-dotted name
    /// with zero diagnostics and roundtrips identically. Used to gate
    /// <see cref="ScaffoldAgent"/> and <see cref="ScaffoldExecutor"/>'s
    /// <c>projectNamespace</c> parameter before it's spliced into a
    /// file-scoped <c>namespace {{ns}};</c> declaration. Same shape as
    /// <see cref="IsValidTypeExpression"/> — pre-reject obvious injection
    /// characters, then require the parsed tree to print back identically.
    ///
    /// The pre-reject list includes <c>&lt;</c> and <c>&gt;</c> in addition to
    /// the type-expression list: namespaces are never generic. Anything that
    /// passes those filters and still wants to be malicious has to break the
    /// roundtrip check.
    /// </summary>
    internal static bool IsValidNamespace(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 256) return false;            // sanity cap; real namespaces fit
        if (s.IndexOfAny(['{', '}', '(', ')', ';', '=', '<', '>', '\n', '\r', '\0', '"', '\'', '`', ' ', '\t']) >= 0)
            return false;

        // ParseName accepts qualified names (e.g. `Foo.Bar.Baz`) and simple
        // identifiers. Anything richer (statement, expression, type with
        // generics) is rejected by the pre-reject filter above, but the
        // roundtrip check is the actual injection gate.
        var nameSyntax = SyntaxFactory.ParseName(s);
        if (nameSyntax.ContainsDiagnostics) return false;
        return nameSyntax.ToString().Trim() == s.Trim();
    }
}
