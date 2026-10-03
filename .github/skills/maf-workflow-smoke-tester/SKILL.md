---
name: maf-workflow-smoke-tester
description: "Generates structural smoke tests for MAF 1.3.0 workflow patterns. Five categories: fan-out/fan-in completeness, session round-trip, streaming, structured output, tool invocation. Catches the silent runtime failures static analysis cannot."
---

# workflow-smoke-tester

## Purpose

`dotnet build` passing does not mean the code works. The fan-in starvation bug is the canonical example: builds green, runs silently wrong, no error. Smoke tests add a fast, credential-free runtime check for the class of failures that static analysis alone cannot catch.

These tests use **mock chat clients** — no Azure credentials required, no network calls. They verify structural correctness, not AI output quality.

## ⚡ The fastest path — use the MCP tools

The maf-autopilot MCP server now covers most of the smoke-test surface automatically. Prefer these:

| What you want to verify                              | Use this tool                                |
|------------------------------------------------------|----------------------------------------------|
| Every fan-out `[MessageHandler]` returns `ValueTask<T>` | `MafValidateFanOut(repoPath)`                |
| Workflow topology can complete (no silent starvation) | `MafSimulateWorkflow(repoPath)`             |
| New agent/executor scaffold ships with smoke test    | `MafNewAgent` / `MafNewExecutor`             |
| Agent prompt quality (injection, refusals, bloat)    | `MafLintAgentPrompt(repoPath)`               |
| Single-command health letter                         | `MafDoctor(repoPath)` — covers all of above  |

When you call `MafNewExecutor`, the generated `*Tests.cs` already includes the reflection-based "fan-out handler returns `ValueTask<T>`" structural assertion — that's the canonical smoke-test pattern. It's compile-validated against `AntiPatternScannerTool` and `FanOutValidatorTool` by the project's own dogfood test suite.

## When to use the manual templates below

Reach for them when:
- The MCP tools are unavailable.
- The codebase needs one of the patterns the tools don't yet cover (session round-trip, streaming, structured output, tool approval).
- You want to drop a snippet directly into a PR review.

## Pattern detection

```powershell
# Fan-out/fan-in pattern
Select-String -Path "src/**/*.cs" -Pattern "AddFanOutEdge" -Recurse -List

# Streaming
Select-String -Path "src/**/*.cs" -Pattern "RunStreamingAsync" -Recurse -List

# Structured output
Select-String -Path "src/**/*.cs" -Pattern "RunAsync<" -Recurse -List

# Tool invocation / approval
Select-String -Path "src/**/*.cs" -Pattern "ApprovalRequiredAIFunction|ToolApprovalRequestContent" -Recurse -List

# Session round-trip
Select-String -Path "src/**/*.cs" -Pattern "CreateSessionAsync|SerializeSessionAsync" -Recurse -List
```

Generate smoke tests only for patterns actually present.

## Smoke test templates

> ⚠️ **Templates below are illustrative.** Only Template 1 (fan-out shape) is compile-validated automatically — the rest are reference snippets you must adapt to your project's exact types. If a template fails to compile against a future MAF minor, file a registry-suggestion issue.

### Template 1 — Fan-out / fan-in structural smoke test (compile-validated equivalent: `MafNewExecutor` output)

```csharp
[Fact]
public void FanOutHandlers_AllReturnGenericTask()
{
    // Catches the silent-starvation pattern at build time via reflection.
    // For full coverage, also run `MafValidateFanOut(repoPath)`.
    var executorTypes = typeof(MyWorkflowMarker).Assembly.GetTypes()
        .Where(t => typeof(Executor).IsAssignableFrom(t))
        .ToList();

    foreach (var t in executorTypes)
    {
        foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<MessageHandlerAttribute>() is null) continue;
            var name = method.ReturnType.Name;
            Assert.True(
                name.StartsWith("ValueTask`"),
                $"{t.Name}.{method.Name} returns {method.ReturnType.FullName} — fan-out handlers must return ValueTask<T>.");
        }
    }
}
```

### Template 2 — Session round-trip

```csharp
[Fact]
public async Task Session_SerialisesAndRehydratesAsync()
{
    var agent = BuildAgentWithMockClient();
    var session = await agent.CreateSessionAsync();
    var blob = await agent.SerializeSessionAsync(session);
    Assert.False(string.IsNullOrEmpty(blob));
    var restored = await agent.DeserializeSessionAsync(blob);
    Assert.NotNull(restored);
}
```

### Template 3 — Streaming smoke test

```csharp
[Fact]
public async Task RunStreamingAsync_YieldsAtLeastOneUpdate()
{
    var agent = BuildAgentWithMockClient();
    var updates = new List<AgentResponseUpdate>();
    await foreach (var u in agent.RunStreamingAsync("hello"))
        updates.Add(u);
    Assert.NotEmpty(updates);
}
```

### Template 4 — Structured output

```csharp
[Fact]
public async Task RunAsyncT_ReturnsTypedResult()
{
    var agent = BuildAgentWithMockClient();
    var resp = await agent.RunAsync<MyStructuredResult>("query");
    Assert.NotNull(resp);
    Assert.NotNull(resp.Result);
}
```

### Template 5 — Tool approval flow

```csharp
[Fact]
public async Task ToolApproval_FlowsToApprovalContent()
{
    var agent = BuildAgentWithMockClient(toolRequiresApproval: true);
    var response = await agent.RunAsync("call the tool");
    var approvalRequest = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .FirstOrDefault();
    Assert.NotNull(approvalRequest);
}
```

## Wiring tips

- All templates assume an `IChatClient` mock — no Azure credentials needed. Reuse the `ScriptedChatClient` pattern emitted by `MafNewAgent`.
- Mark the test class `[Trait("Category", "Smoke")]` so CI can run smoke tests separately from end-to-end tests.
- Smoke tests should complete in &lt; 1 second each — they're structural, not behavioural.
