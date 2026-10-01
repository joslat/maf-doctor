# Self-update runbook

When the MAF Release Watcher fails, its tracking issue (label `maf-release`) names a **failure class** and links to the matching section below.
Each section gives the log signature, what it means, and the fix.
The classifier is `.github/scripts/classify_watcher_failure.py`; when you meet a new failure, add its signature there and a section here.

General rules:

- **Fail closed, but never silently.** The watcher refuses to advance `.maf-version` on incomplete evidence. Fix the cause; don't bypass the gate.
- **Process releases in order.** The watcher picks the oldest pending stable MAF release. After a fix, rerun it with no inputs (`gh workflow run maf-release-watcher.yml`).
- **Close the tracking issue** once a run succeeds.

## evidence-gap

**Signature:** `Release evidence is incomplete; refusing to advance .maf-version`, followed by `- <surface>: <reason>` lines.

**Meaning:** a package surface in `.github/maf-package-surfaces.json` could not be resolved for the old or new release train, so it couldn't be diffed. Typical causes:

- **Upstream moved the package** to another repository or cadence (MAF 1.17: Durable Task and Azure Functions moved to `microsoft/agent-framework-durable-extension`). Record a `repository_externalization` lifecycle event for the release where it happened. The planner then treats the surface as externalized for every later train.
- **Upstream split or renamed the package.** Record a `package_split` event (breaking) and add the successor surface.
- **Upstream republished a prerelease** for an already-processed train, so "exactly one aligned prerelease" became ambiguous. The old side is pinned by `.github/maf-train-lock.json`. If the train predates the lock, add its entry by hand, using the version that was actually diffed.
- **A brand-new surface** has no previous-train version yet. Add it to the manifest at the train it first ships in.

**Verify the fix offline:** run `python3 .github/scripts/build_maf_package_plan.py --old-version <old> --new-version <new> --dry-run`; it must show no `unverifiable` surfaces. Then run `pytest .github/scripts/tests/test_watcher_replay.py`, and refresh its NuGet snapshot if the new train isn't in it yet.

## credential

**Signature:** `COPILOT_ASSIGN_PAT is missing` / `is expired, revoked, or cannot read`, or `Bad credentials`.

**Meaning:** the token the watcher uses to push the scaffold branch and open the PR is missing or expired.

**Fix:** rotate the repository secret, then rerun. The target state is a GitHub App identity with per-run tokens and no expiry (ROADMAP Z-01).

## tool-version

**Signature:** `Expected dotnet-inspect <X>, got <Y>`.

**Meaning:** the installed `dotnet-inspect` doesn't match the pinned version, so the diff evidence can't be trusted.

**Fix:** check the install step and the pinned version in `maf-release-watcher.yml`, then rerun.

## nuget-unreachable

**Signature:** `NuGet API unreachable after 3 attempts`, `NuGet request failed`, or `Could not resolve host`.

**Meaning:** NuGet was unreachable, so the release state is unknown. The watcher failed closed on purpose.

**Fix:** usually transient, so rerun. If it persists, check nuget.org status.

## sequencing

**Signature:** `Process releases sequentially`, or a branch that `already exists; refusing` or `appeared after selection`.

**Meaning:** a manual dispatch requested a release out of order, or a watcher branch for this release already exists.

**Fix:** rerun without `maf_version`. If a stale `release-watcher/maf-<version>` branch exists, delete it only if its PR is closed and unneeded.

## registry-extraction

**Signature:** `Registry de-duplication failed` or `Cannot prove per-package registry extraction coverage`.

**Meaning:** the draft registry entries could not be extracted or de-duplicated for every diffable surface.

**Fix:** download the `maf-diff-<version>` artifact from the run and inspect `maf-registry-extraction-results.json`.

## unknown

**Meaning:** no known signature matched. The issue shows the last error lines from the failed job.

**Fix:** read the run log. Once it's understood, add the signature to `CLASSES` in `classify_watcher_failure.py` and a section to this runbook, so the next occurrence is diagnosed automatically.
