# CLAUDE.md

Read [`AGENTS.md`](AGENTS.md) first: it maps the codebase, the self-update loop, and
the coding standards. These are working notes on top of it.

## Before pushing

CI minutes and Copilot credits are limited. Run the gates locally so CI confirms
rather than debugs:

```bash
dotnet test src/maf-autopilot.Tests/maf-autopilot.Tests.csproj -c Release -f net10.0
python3 -m pytest .github/scripts/tests -q
python3 .github/scripts/verify_crossfile_consistency.py
MAF_REGISTRY_PATH="$PWD/.github/skills/maf-obsolete-api-registry/registry.yaml" \
  dotnet src/maf-autopilot/bin/Release/net10.0/maf-doctor.dll verify-registry
```

Batch commits on one branch and keep one PR open at a time.

## Gotchas

- **Build in Release.** A running maf-doctor MCP server locks the Debug output.
  On Windows add `-nodeReuse:false -p:UseSharedCompilation=false` to avoid idle
  MSBuild nodes.
- **`Doctor_ValidPath_ExitsZero`** can fail locally when `%TEMP%` is polluted;
  filter it out (`--filter "FullyQualifiedName!~Doctor_ValidPath_ExitsZero"`).
- **Git Bash:** prefix `git show ref:path` with `MSYS_NO_PATHCONV=1`.
- **Escapes:** writing C#, YAML or Python through a shell heredoc can turn `\n`
  and `\`-newline into literal line breaks. Use an editor tool for escape-heavy
  edits.
- **Unquoted YAML scalars** in `registry.yaml` must not contain `: `; quote them.
- **Diff from the merge-base** (`base...head`) in PR gates. A two-dot diff against
  an advanced `main` attributes main's changes to the PR.
- **Watcher scaffold PRs** (`release-watcher/maf-*`) must keep linear history and
  may only touch data paths; never merge `main` into them. To re-run their checks
  on a fresh merge ref, push a commit to the branch.
