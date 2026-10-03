---
description: "Always-loaded testing patterns for MAF 1.3.0 code. Auto-applies to test files. Covers hermetic IChatClient mocking, the ScriptedChatClient idiom, fan-out shape verification, and the AAA conventions this repo follows."
applyTo: "**/*Tests.cs"
---

# MAF Testing — Hermetic Patterns

When you write tests for MAF code in this repo, follow these patterns. They exist because we've been bitten by their absence — every entry has a "why" behind it.

## Hard rules

- **No live LLM calls in tests.** Ever. Cost is one reason; flakiness, throttling, and "the prompt drifted in production but tests still passed" are the bigger ones.
- **AAA structure with explicit comment markers:** `// Arrange`, `// Act`, `// Assert`. Three blocks, separated by a blank line. A test that needs more than 3 blocks is doing too much.
- **One assertion subject per test.** Multiple `Assert.X(...)` calls is fine when they describe one fact (e.g., "this finding has the right ID *and* the right line").
- **Test name follows `Method_Scenario_Outcome`.** Example: `ParseGitDiffOutput_IgnoresBinAndObjPaths_FiltersThem`.
- **Hermetic > integration.** Prefer in-memory string sources over temp dirs. Prefer temp dirs over real network. Real network needs explicit justification.
- **No `Thread.Sleep`, no wall-clock dependencies, no GUIDs in assertions.** Each is a known source of CI flake.

## The `ScriptedChatClient` pattern (THE canonical mock)

For any test that needs an `IChatClient`, never instantiate `AzureOpenAIClient.AsChatClient(...)`. Use a scripted mock — same shape as the scaffolder generates:

```csharp
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
```

Extend the scripted mock with a queue of responses when you need turn-by-turn behaviour:

```csharp
private sealed class QueuedChatClient : IChatClient
{
    private readonly Queue<string> _responses;
    public QueuedChatClient(params string[] responses) => _responses = new(responses);
    // GetResponseAsync dequeues; tests assert exhaustion.
}
```

**Never use `Moq` for `IChatClient`** — the scripted class is shorter, clearer, and zero-dependency.

## Fan-out shape verification (the canonical contract test)

Every fan-out `[MessageHandler]` method MUST return `ValueTask<T>` or a synchronous `T` (the source generator rejects `Task<T>` (MAFGENWF002), and `IAsyncEnumerable<T>` fails at run time). Verify structurally with reflection — no DI, no execution, no chat client at all:

```csharp
[Fact]
public void HandleAsync_HasFanOutSafeReturnType()
{
    // Arrange
    var handler = typeof(MyExecutor)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == "HandleAsync");

    // Act
    var returnTypeName = handler.ReturnType.Name;

    // Assert
    Assert.True(
        returnTypeName.StartsWith("ValueTask`", StringComparison.Ordinal),
        $"Handler must return ValueTask<T>, got {handler.ReturnType.FullName}.");
}
```

This catches the silent-starvation regression at build time — the single most-important test you can write against an executor.

## Workflow execution tests

When you need to run an actual workflow (e.g., to assert message ordering or fan-in completion), build the workflow in the test with scripted executors that emit deterministic messages. Avoid wall-clock timeouts — the workflow runner accepts a `CancellationTokenSource` for ceiling control.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));   // ceiling, not expectation
var result = await runner.RunAsync(input, cts.Token);
```

A workflow that takes longer than 2 seconds in a hermetic test is doing too much — split the test.

## When you DO need a temp directory

Use the `IDisposable` pattern, GUID-suffix the directory name, best-effort cleanup:

```csharp
private sealed class TempProjectDir : IDisposable
{
    public string Path { get; }
    public TempProjectDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "maf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
}
```

Wrap test usage with `using var fixture = new TempProjectDir();`. Never share a temp dir across tests — that's how parallel-runner flake hides.

## Test signal hygiene

- A failing test message should answer *what* broke, not *that* something broke. Use `Assert.True(condition, $"...")` with a one-sentence summary that names the file, line, and expectation.
- Don't write `Assert.Equal(0, x.Count)` — write `Assert.Empty(x)`. Same for `Assert.Single`, `Assert.Contains`. Better failure messages out of the box.
- When asserting collections, prefer `Assert.Equal(expected, actual)` over loops — xUnit prints the diff.

## Anti-patterns we've already paid for

- **Tests that depend on the test running order.** xUnit parallelizes — order is undefined. Hidden inter-test state has caused four separate CI flake incidents in this repo's history.
- **`Assert.NotNull(x)` followed by accessing `x.Field`.** Use `Assert.NotNull(x); x = x!;` or pattern-match — the compiler will catch the null deref. Better yet, use `x` directly after `Assert.NotNull` (CS8602 is suppressed by xUnit).
- **`Task.Delay` for "timing."** If the assertion needs a delay, the production code is racy — fix the race, not the test.
- **`.Result` / `.Wait()` on async tasks.** Test methods can be `async Task` — no excuses.

## What the scanners enforce on test files

The anti-pattern scanner skips files matching the test-file heuristic (`*Tests.cs`, `tests/` dir, etc.) for *most* rules — but NOT for prompt-lint rules. PROMPT-004 (untrusted-input concatenation) fires in test code too because the pattern is just as dangerous there. The fan-out validator runs on test code as well (test executors must also be fan-out safe). The Roslyn analyzers respect the `[ExcludeFromCodeCoverage]` and project-level `<RoslynAnalyzerExcludeTestFiles>` properties.
