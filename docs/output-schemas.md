# maf-doctor output schemas

Tools that emit structured output document their schemas here. JSON outputs are stable within a major version (`schema_version` field tracks evolution).

## `MafDoctor`

Pass `format: "json"` to get machine-readable output.

```json
{
  "schema_version": "1",
  "verdict": "C",
  "errors_count": 1,
  "warnings_count": 0,
  "silent_starvation_risks": 0,
  "unbounded_cost_sites": 0,
  "top_fixes": [
    {
      "rule_id": "MAF-AP-SEC-003",
      "severity": "error",
      "file": "src/MyAgent.cs",
      "line": 42,
      "issue": "EnableSensitiveData = true",
      "fix_description": "Remove EnableSensitiveData = true from non-dev configurations.",
      "auto_fixable": true,
      "why": "Sensitive-data logging writes prompts and responses (often PII / secrets) to your telemetry sink — acceptable in dev, a data-leak in production.",
      "confidence": "high"
    }
  ],
  "summary_md": "...the full markdown report that format:markdown would emit...",
  "scan_truncated": false,
  "files_scanned": 155
}
```

### Field definitions

- `schema_version`: `"1"` — increments only on breaking schema changes (new 1.x releases keep schema v1)
- `verdict`: `"A"` | `"B"` | `"C"` | `"F"` per the grading rubric in `DoctorTool` description
- `errors_count`: anti-pattern errors + prompt-lint errors (integer)
- `warnings_count`: anti-pattern warnings + prompt-lint warnings + info notes (integer)
- `silent_starvation_risks`: fan-out handler violations (integer)
- `unbounded_cost_sites`: agent call sites with no `MaxOutputTokens` cap (integer)
- The four counts above **reconcile with `top_fixes`** in full mode: `errors_count + warnings_count + silent_starvation_risks + unbounded_cost_sites == top_fixes.length`.
- `top_fixes`: array of finding objects — the top 3 by default, or **every** finding when `--all` / `full: true` is set (the key name is retained for schema-v1 stability)
  - `rule_id`: the canonical rule identifier (e.g., `MAF-AP-SEC-001`, `MAF001`, `COST-001`)
  - `severity`: `"error"` | `"warning"` | `"starvation_risk"` | `"cost"`
  - `file`: relative path to the file containing the finding (links directly to source)
  - `line`: line number within that file (1-based integer)
  - `issue`: short, human-readable title of the finding
  - `fix_description`: one-line actionable fix; `MafAutoFixAll` uses this as its rewrite spec
  - `auto_fixable`: `true` if `MafAutoFixAll` has a deterministic rewriter for this rule
  - `why`: one-line consequence / rationale for the finding. **Always present**; the empty string `""` when no rationale is defined for the rule.
  - `confidence`: detector trust level for **false-positive triage** — `"certain"` (compiler ground-truth) | `"high"` (structural AST, low FP) | `"heuristic"` (name-only / text / scope-limited — **verify before fixing**). Additive within schema v1; consumers that don't recognize it can ignore it. The `maf-remediate` prompt reads this to decide which findings to confirm before changing code.
  - `fingerprint`: a short, drift-stable finding identifier (first 12 hex of a SHA-256 over `rule_id | file | whitespace-collapsed, secret-redacted source line`). A **line move leaves it stable**; a content edit of the offending line changes it. Lets an automated remediation loop key its skip-set on the fingerprint instead of `file:line`. Additive within schema v1. **Degradation:** when the source line is unavailable or was secret-redacted, it falls back to a `rule_id | file | line` hash — always present, but that fallback is **not** drift-stable.
- `summary_md`: the same markdown that `format: "markdown"` would emit (for hybrid consumers). It includes the offending source line per finding, so it inherits the markdown report's **best-effort, content-aware secret redaction** — the structured `top_fixes` array carries no source text and is the safer channel for machine consumers.
- `scan_truncated`: `true` if the repo-size cap (file count or per-file size) meant the scan did NOT cover every file — the verdict/findings above reflect a partial repo, not the whole one. Additive within schema v1; consumers that don't recognize it can ignore it, but a CI gate reading only the typed fields (not `summary_md`) should check it before trusting a clean verdict.
- `files_scanned`: how many files were actually included in the scan (integer).
- `coverage_gap`: `null`, or an object when the repo references a newer MAF than the embedded registry covers: `registry_maf_version`, `project_maf_version` (the highest train any `Microsoft.Agents.AI*` package reference resolves to, including Central Package Management pins), `newer_packages` (`Id@Version` strings past the registry) and `update_command`. When non-null, findings for APIs changed after `registry_maf_version` are **unknown**, not clean: treat an A grade as "clean against what the registry knows". Additive within schema v1.
- `baseline_suppressed`: absent (`null`) unless `doctor --baseline <file>` was used; then the number of findings that matched the baseline and were dropped before grading. Counts, `verdict` and `--fail-on` then cover only new findings. The baseline file is the output of an earlier `maf-doctor doctor --all --json`; findings are matched by `fingerprint` (rule, file and source-line text), so a finding that only moves lines stays baselined. Additive within schema v1.

### Usage in CI

```bash
# Get JSON output and check verdict.
# CLI flag is `--json` (boolean); the MCP tool parameter is `format: "json"`.
OUTPUT=$(maf-doctor doctor . --json 2>/dev/null || \
  dotnet run --project src/maf-autopilot/ -- doctor . --json)

VERDICT=$(echo "$OUTPUT" | jq -r '.verdict')
if [ "$VERDICT" = "F" ]; then
  echo "MAF Doctor grade F — failing CI"
  exit 1
fi
```

Or via the MCP tool from your AI client (note: the MCP parameter is `format: "json"`, not the CLI's `--json` flag):
```
MafDoctor(repoPath: ".", format: "json")
```

---

## `MafDoctor` plan output (`--plan` / `format: "plan"`)

A **markdown-only** remediation plan (no JSON/SARIF schema) — an ordered, checkboxed
punch list you can paste into a GitHub issue:

- **Phase 1 — Quick wins:** every auto-fixable finding, batched into one `autofix-all`
  step, with the cleared rules listed. (Note: `autofix-all` also applies build-surfaced
  rewriters such as the fan-in arg-order swap (`MAF130-FAN-IN-001`) that the scan-time
  grader doesn't flag — so it may change more than the plan enumerates.)
- **Phase 2 — Semantic fixes:** the non-auto-fixable findings as impact-ordered `- [ ]`
  tasks (why / fix / `file:line`), tagged for the `@maf-migration` agent.

It **always covers every finding**, ignoring `--all` / `full`, and emits **no source
snippets** — only `file:line` — so it never echoes a secret.

---

## `MafDoctor` remediation manifest (`--plan --json`)

The machine-readable sibling of `--plan` — the same phased plan, structured so an
automated remediation loop (the `maf-remediate` prompt) can iterate it and triage
false positives without parsing prose. Schema version `"1"`. CLI: `doctor <path> --plan --json`;
MCP: `MafDoctor(repoPath, format: "plan-json")`. (An unrecognized `format` returns a
structured error, not a silent markdown fallback.)

```jsonc
{
  "schema_version": "1",
  "verdict": "F",
  "repo": "C:/path/to/repo",
  "counts": { "total": 13, "auto_fixable": 3, "manual": 10, "heuristic": 3 },
  "phase1_autofix": {                         // null when nothing is auto-fixable
    "command": "maf-doctor autofix-all \"C:/path/to/repo\" --apply",  // targets the scanned repo, not cwd
    "finding_count": 3,
    "clears_rules": ["MAF-AP-SEC-001", "MAF-AP-SEC-003", "MAF-AP-WF-001"]
  },
  "phase2_semantic": [                         // grouped by rule, impact-ordered
    {
      "rule_id": "COST-001",
      "title": "uncapped agent call — no MaxOutputTokens",
      "severity": "cost",
      "confidence": "heuristic",              // certain | high | heuristic
      "verify_first": true,                   // true ⇔ confidence == "heuristic"
      "auto_fixable": false,
      "why": "An agent call with no MaxOutputTokens cap can emit an unbounded response…",
      "fix": "Set MaxOutputTokens on the nearest ChatOptions to cap the per-call cost.",
      "occurrences": [ { "file": "AGUIClient/Program.cs", "line": 90, "fingerprint": "a1b2c3d4e5f6" } ]
    }
  ],
  "scan_truncated": false,
  "files_scanned": 155
}
```

- `counts.heuristic` — how many Phase-2 findings are `heuristic` (likely false positives; confirm before fixing).
- `phase1_autofix` — `null` if there are no auto-fixable findings; otherwise the single command + the rules it clears.
- `phase2_semantic[].verify_first` — `true` for `heuristic` findings: confirm the finding is real (e.g. via `MafExplainFinding`) **before** editing.
- `phase2_semantic[].occurrences` — every `file`/`line` for that rule (a group of N hits is one entry with N occurrences). Each occurrence also carries a `fingerprint` (see the `--json` field definitions) so the loop can track a finding across edits.
- `scan_truncated` — `true` if the repo-size cap meant this plan does NOT cover the whole repo. An automated consumer (the `maf-remediate` loop) MUST check this before treating the plan as complete — clearing every listed finding would otherwise look like "fully remediated" when it isn't. Additive within schema v1.
- `files_scanned` — how many files were actually included in the scan (integer).
- Invalid path / scan failure returns the same `{ "schema_version": "1", "error": "…" }` shape as `--json` (the `DoctorJsonError` shape — see below).

> **Error shape (`DoctorJsonError`).** Every JSON-shaped `MafDoctor` surface (`--json`,
> `--plan --json`) returns `{ "schema_version": "1", "error": "…" }` on a validation/scan
> failure, so a machine consumer parses one uniform error shape regardless of the format it
> requested. `MafAutoFix` / `MafAutoFixAll` (below) reuse the same `schema_version` + `error` shape.

---

## `MafAutoFixAll` / `MafAutoFix` (machine-readable JSON)

Both apply-tools return JSON. Keys are **camelCase** (retained for backward compatibility);
every object carries `schema_version: "1"` (additive — existing consumers ignore it).

```jsonc
// MafAutoFixAll — success
{
  "schema_version": "1",
  "dryRun": true,
  "orderOfExecution": ["MAF-AP-SEC-001", "MAF-AP-WF-001"],
  "totalDistinctFilesChanged": 2,
  "affectedFiles": ["Agents/Prod.cs", "Workflows/Inv.cs"],
  "perRule": { "MAF-AP-SEC-001": { "filesChanged": 1, "changedFiles": ["Agents/Prod.cs"] } },
  "errors": [ { "file": "Locked.cs", "error": "could not be read (IOException)" } ], // previously-swallowed per-file failures
  "diffs":  [ { "file": "Agents/Prod.cs", "ruleIds": ["MAF-AP-SEC-001"], "diff": "@@ …" } ] // preview diffs (WM-24)
}

// MafAutoFix (single rule) — success
{ "schema_version": "1", "ruleId": "MAF-AP-SEC-001", "dryRun": true, "filesChanged": 1, "changedFiles": ["Agents/Prod.cs"], "errors": [] }

// Either tool — error (bad path, unknown ruleId, lock contention, …)
{ "schema_version": "1", "error": "…" }
```

- `dryRun` — `true` (preview, the default) or `false` (files written). Apply mode serializes concurrent applies on the same repo.
- `errors[]` — per-file failures that were previously swallowed; a non-empty array is what the CLI's non-zero exit code keys on.
- Both tools reuse the `schema_version` + `error` shape above so the failure shape is identical to `MafDoctor`.

---

## (Reserved) `MafAuditPullRequest`

Same shape as `MafDoctor` with an additional `pr_metadata` field. Implementation pending.

---

## `MafScanAntiPatterns` SARIF output

Documented separately — uses standard [SARIF v2.1.0](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html). Pass `format: "sarif"`.

## `MafValidateFanOut` SARIF output

Same standard SARIF v2.1.0. Pass `format: "sarif"`. Flags both `SilentStarvationRisk` and `LikelyInvalid` as `MAF001` errors.
