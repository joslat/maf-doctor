---
name: maf-issue-reporter
description: "Procedures for filing an upstream issue against microsoft/agent-framework. Use when the toolkit's analysis cannot map a symptom to a known registry pattern — i.e., this might be a real upstream bug. Pairs with the `MafDraftIssue` MCP tool, which assembles the issue body."
version: "1.0"
---

# Skill: maf-issue-reporter

## Quick decision tree

You should file an upstream issue **only** when **all** of the following are true:

1. The symptom **does NOT match** any entry in the obsolete-API registry (`maf://registry`). If `MafDraftIssue` returns "Registry hits" — **apply the deterministic fix first**; don't file.
2. The constraint table at `maf://constraints` doesn't already cover the pattern (e.g., session state in instance fields is a known anti-pattern, not a bug).
3. You can reduce the symptom to a **minimal repro** (15-50 lines max, no unrelated app code).
4. The same symptom is **not already filed** at <https://github.com/microsoft/agent-framework/issues>.

If any of those is false → **don't file**. Apply the existing fix, or refine the repro.

## How to file

**Step 1.** Run `MafDraftIssue(repoPath, symptom, snippet?, expected?, actual?)` to assemble the body. The tool detects:
- The tracked MAF version (from `.maf-version`).
- Every `Microsoft.Agents.AI*` / `Microsoft.Extensions.AI*` `<PackageReference>` and its pinned version.
- The .NET TFM(s) in your csproj.
- Any registry entries that approximately match your symptom.

It outputs a markdown body ready to paste.

**Step 2.** Review the body. Strip:
- Internal file paths (e.g. `C:\Users\you\company-secret\…`).
- Internal identifiers (employee IDs, customer account numbers, internal team names).
- Any secrets that leaked into the stack trace.

**Step 3.** Post. Two routes:

- **Manual:** copy the body, navigate to <https://github.com/microsoft/agent-framework/issues/new>, paste, submit.
- **Via the GitHub MCP server:** if you have the official [github-mcp-server](https://github.com/github/github-mcp-server) installed, ask Copilot Chat to *"create an issue in `microsoft/agent-framework` titled `<title>` with this body."* The GitHub MCP server's `create_issue` tool handles the API call. **Never use a personal access token from inside `maf-autopilot` directly** — that's why we don't post; the github-mcp-server (or a manual paste) keeps the trust boundary right.

**Step 4.** After posting, add the issue URL to your team's local notes. If the issue is accepted and a fix lands, the `maf-release-watcher` workflow will surface the new MAF version automatically — file a follow-up to add the registry entry once the fix releases.

## What NOT to file

- **CS0618 warnings** — these are by-design Microsoft signals. Apply the registry's deterministic fix.
- **"Workflow exits cleanly but produces no output"** — this is the silent fan-in starvation pattern. The toolkit catches it via `MafValidateFanOut` / `MAF001` Roslyn analyzer / `MAF130-FAN-IN-001` registry entry. Not a bug.
- **`DefaultAzureCredential` not working in production** — the `MAF002` analyzer warns; the `MAF-AP-SEC-001` scanner rule flags. This is configuration, not a bug.
- **"My agent's `Instructions` is being ignored"** — check that it's inside `ChatClientAgentOptions.ChatOptions` (anti-pattern rule `MAF-AP-AGENT-001`; a top-level `Instructions` does not compile since MAF 1.0). Top-level `Instructions` is silently dropped in 1.3.0.
- **Vague reports without a repro.** Without code, the upstream team can't act.

## Examples of issues this skill helps with

✅ "`AddFanInBarrierEdge` argument order does not match the docs" — this turned out to be a known issue but the upstream surface bench-fix landed because someone reported it with a clean repro.

✅ "`ChatClientAgent.RunAsync` returns successfully but `AgentResponse.Text` is empty when the underlying chat client returns a tool call" — novel pattern; report it.

❌ "MAF is broken." (No repro, no version, no specifics.)

❌ "`new DefaultAzureCredential()` doesn't authenticate in production." (Already known — analyzer / scanner / constraint covers it.)

## Trust boundary

The `MafDraftIssue` tool **never** posts anything. We don't take a GitHub token, we don't make HTTP calls. The user reviews + routes. This is deliberate — posting to a public org-owned repo is a "modifies shared system" action that needs an explicit human gate.

If you want hands-off filing, install the official [`github-mcp-server`](https://github.com/github/github-mcp-server) and let it own the API call. It's a separate trust boundary with its own auth model.
