#!/usr/bin/env python3
"""Surface auto-discovery for the MAF watcher (ROADMAP Z-06).

Until 2026-10 the watcher diffed 11 of 35 Microsoft.Agents.AI* packages and
nothing noticed: breaking changes in the other 24 never reached the registry.
This lists every verified, Microsoft-owned package on nuget.org whose id starts
with ``Microsoft.Agents.AI`` and reports any that are not accounted for:
not a surface in ``.github/maf-package-surfaces.json``, not a package named by a
lifecycle event there, and not deliberately ignored (with a reason) in
``.github/maf-package-discovery.json``.

Exit 0 when everything is accounted for, 1 when new packages exist, 2 when
nuget.org cannot be read (never reported as "all tracked").
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Callable, Iterable
from urllib.parse import urlencode
from urllib.request import Request, urlopen

PREFIX = "microsoft.agents.ai"
SEARCH = "https://azuresearch-usnc.nuget.org/query"
PAGE = 1000
MAX_PAGES = 5
MAX_BYTES = 16 * 1024 * 1024
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MANIFEST = REPO_ROOT / ".github" / "maf-package-surfaces.json"
DEFAULT_DISCOVERY = REPO_ROOT / ".github" / "maf-package-discovery.json"


def fetch_search(opener: Callable = urlopen) -> list[dict]:
    results: list[dict] = []
    for page in range(MAX_PAGES):
        query = urlencode({
            "q": "Microsoft.Agents.AI", "prerelease": "true", "semVerLevel": "2.0.0",
            "skip": page * PAGE, "take": PAGE,
        })
        request = Request(f"{SEARCH}?{query}", headers={"User-Agent": "maf-doctor-discovery/1"})
        with opener(request, timeout=30) as response:
            raw = response.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise ValueError("nuget.org search response is oversized")
        document = json.loads(raw.decode("utf-8"))
        data = document.get("data", [])
        results.extend(data)
        if len(data) < PAGE or len(results) >= int(document.get("totalHits", 0)):
            return results
    raise ValueError("nuget.org search did not finish within the page limit")


def candidates(search_results: Iterable[dict]) -> dict[str, str]:
    """Verified, Microsoft-owned packages under the MAF prefix: id -> latest version."""
    found: dict[str, str] = {}
    for item in search_results:
        package = str(item.get("id", ""))
        owners = item.get("owners") or []
        if isinstance(owners, str):
            owners = [owners]
        if (
            package.lower().startswith(PREFIX)
            and item.get("verified") is True
            and "Microsoft" in owners
        ):
            found[package] = str(item.get("version", ""))
    return found


def accounted_for(manifest: dict, discovery: dict) -> set[str]:
    known = {str(s["package"]).lower() for s in manifest.get("surfaces", [])}
    for event in manifest.get("lifecycle_events", []):
        for key in ("source_packages", "target_packages"):
            for entry in event.get(key) or []:
                known.add(str(entry.get("package", "")).lower())
    ignored = discovery.get("ignored", {})
    if not isinstance(ignored, dict) or not all(isinstance(v, str) and v.strip() for v in ignored.values()):
        raise ValueError("maf-package-discovery.json: every ignored package needs a non-empty reason")
    known.update(package.lower() for package in ignored)
    return known


def untracked(found: dict[str, str], known: set[str]) -> dict[str, str]:
    return {package: version for package, version in sorted(found.items()) if package.lower() not in known}


def render(found: dict[str, str], new: dict[str, str]) -> str:
    if not new:
        return f"**MAF package discovery:** ✅ all {len(found)} `Microsoft.Agents.AI*` packages on nuget.org are tracked or deliberately ignored.\n"
    lines = [
        f"**MAF package discovery:** 🆕 {len(new)} package(s) on nuget.org are not tracked by the watcher:",
        "",
        "| Package | Latest version |",
        "|---|---|",
        *(f"| `{package}` | `{version}` |" for package, version in new.items()),
        "",
        "**What fixes it:** add each to `.github/maf-package-surfaces.json` (next `order`, a slug, an "
        "`id_scope`) so its API is diffed on every release, or list it in "
        "`.github/maf-package-discovery.json` with the reason it needs no diff.",
    ]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--discovery", type=Path, default=DEFAULT_DISCOVERY)
    parser.add_argument("--search-json", type=Path, help="offline search results (list of package objects)")
    parser.add_argument("--report-out", type=Path, required=True)
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        discovery = json.loads(args.discovery.read_text(encoding="utf-8"))
        known = accounted_for(manifest, discovery)
        results = (
            json.loads(args.search_json.read_text(encoding="utf-8")) if args.search_json else fetch_search()
        )
        found = candidates(results)
        if not found:
            raise ValueError("nuget.org returned no Microsoft.Agents.AI packages")
    except Exception as exc:  # unreadable upstream or config: never claim "all tracked"
        args.report_out.write_text(f"**MAF package discovery:** unknown ({exc}).\n", encoding="utf-8")
        print(f"error: {exc}", file=sys.stderr)
        return 2

    new = untracked(found, known)
    report = render(found, new)
    args.report_out.write_text(report, encoding="utf-8")
    print(report)
    return 1 if new else 0


if __name__ == "__main__":
    raise SystemExit(main())
