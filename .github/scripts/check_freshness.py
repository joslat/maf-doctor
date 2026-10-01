#!/usr/bin/env python3
"""Freshness SLO for MAF Doctor's migration knowledge (ROADMAP O-01).

The Drift Detector grades this repository's code; nothing measured what users
experience. From 2026-07-21 to 2026-10-01 the shipped registry was current on
one day out of 72 while every CI job stayed green. This check compares the MAF
release the knowledge covers with upstream stable MAF on nuget.org, on two
levels:

* main:    ``.maf-version`` on the default branch (what the next release ships)
* shipped: ``.maf-version`` at the latest ``vX.Y.Z`` tag (what users install)

SLO, per level: at most ``--max-lag`` stable MAF releases behind, and no
uncovered release older than ``--max-age-days``. Exit 1 on a breach.
"""
from __future__ import annotations

import argparse
import datetime as dt
import gzip
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable
from urllib.request import Request, urlopen

REGISTRATION = "https://api.nuget.org/v3/registration5-gz-semver2/microsoft.agents.ai/index.json"
MAX_BYTES = 8 * 1024 * 1024


def _key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("-", 1)[0].split("+", 1)[0].split("."))


def _get_json(url: str, opener: Callable = urlopen) -> dict:
    request = Request(url, headers={"User-Agent": "maf-doctor-freshness/1", "Accept-Encoding": "gzip"})
    with opener(request, timeout=30) as response:
        raw = response.read(MAX_BYTES + 1)
    if len(raw) > MAX_BYTES:
        raise ValueError(f"response from {url} exceeds {MAX_BYTES} bytes")
    if raw[:2] == b"\x1f\x8b":
        raw = gzip.decompress(raw)
    return json.loads(raw.decode("utf-8"))


def fetch_stable_releases(opener: Callable = urlopen) -> list[tuple[str, dt.date]]:
    """Stable Microsoft.Agents.AI versions with their publish dates, oldest first."""
    index = _get_json(REGISTRATION, opener)
    releases: list[tuple[str, dt.date]] = []
    for page in index.get("items", []):
        items = page.get("items")
        if items is None:
            items = _get_json(page["@id"], opener).get("items", [])
        for item in items:
            entry = item.get("catalogEntry", {})
            version = str(entry.get("version", ""))
            published = str(entry.get("published", ""))
            if not version or "-" in version or published.startswith("1900"):
                continue  # prerelease, or unlisted (NuGet marks unlisted with year 1900)
            releases.append((version, dt.date.fromisoformat(published[:10])))
    return sorted(releases, key=lambda r: _key(r[0]))


@dataclass(frozen=True)
class LevelStatus:
    name: str
    covered: str
    behind: list[tuple[str, dt.date]]
    oldest_uncovered_age_days: int
    ok: bool


def evaluate(
    name: str,
    covered: str,
    releases: Iterable[tuple[str, dt.date]],
    today: dt.date,
    max_lag: int,
    max_age_days: int,
) -> LevelStatus:
    behind = [r for r in releases if _key(r[0]) > _key(covered)]
    age = (today - behind[0][1]).days if behind else 0
    ok = len(behind) <= max_lag and age <= max_age_days
    return LevelStatus(name, covered, behind, age, ok)


def render(levels: list[LevelStatus], latest: str, max_lag: int, max_age_days: int) -> str:
    healthy = all(level.ok for level in levels)
    lines = [
        f"**MAF knowledge freshness: {'✅ within SLO' if healthy else '🟠 SLO breached'}**",
        "",
        f"Upstream latest stable MAF: **{latest}**. SLO: at most {max_lag} release(s) behind "
        f"and no uncovered release older than {max_age_days} days.",
        "",
        "| Level | Covers MAF | Releases behind | Oldest uncovered | Status |",
        "|---|---|---|---|---|",
    ]
    for level in levels:
        oldest = f"{level.behind[0][0]} ({level.oldest_uncovered_age_days} d)" if level.behind else "—"
        lines.append(
            f"| {level.name} | {level.covered} | {len(level.behind)} | {oldest} | {'✅' if level.ok else '🟠'} |"
        )
    lines += [
        "",
        "**What fixes it:** `main` behind → see the open `release-watcher/maf-*` PR or the "
        "watcher failure issue (`docs/runbooks/self-update.md`). Shipped behind → cut a "
        "maf-doctor release so users get the knowledge already on `main`.",
    ]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--main-version", required=True, help=".maf-version on the default branch")
    parser.add_argument("--shipped-version", help=".maf-version at the latest release tag")
    parser.add_argument("--max-lag", type=int, default=1)
    parser.add_argument("--max-age-days", type=int, default=10)
    parser.add_argument("--today", help="YYYY-MM-DD (default: today, UTC)")
    parser.add_argument("--releases-json", type=Path, help="offline [[version, date], ...] instead of nuget.org")
    parser.add_argument("--report-out", type=Path, required=True)
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    today = dt.date.fromisoformat(args.today) if args.today else dt.datetime.now(dt.timezone.utc).date()
    for label, value in (("--main-version", args.main_version), ("--shipped-version", args.shipped_version)):
        if value is None:
            continue
        try:
            _key(value.strip())
        except ValueError:
            print(f"error: {label} must be a X.Y.Z version, got {value!r}", file=sys.stderr)
            return 2
    try:
        if args.releases_json:
            releases = [(v, dt.date.fromisoformat(d)) for v, d in json.loads(args.releases_json.read_text(encoding="utf-8"))]
        else:
            releases = fetch_stable_releases()
    except Exception as exc:  # network or parse failure: fail loudly, never "fresh"
        args.report_out.write_text(f"Freshness unknown: could not read nuget.org ({exc}).\n", encoding="utf-8")
        print(f"error: {exc}", file=sys.stderr)
        return 2
    if not releases:
        args.report_out.write_text("Freshness unknown: nuget.org returned no stable MAF releases.\n", encoding="utf-8")
        return 2

    levels = [evaluate("main", args.main_version.strip(), releases, today, args.max_lag, args.max_age_days)]
    if args.shipped_version:
        levels.append(evaluate("shipped", args.shipped_version.strip(), releases, today, args.max_lag, args.max_age_days))
    report = render(levels, releases[-1][0], args.max_lag, args.max_age_days)
    args.report_out.write_text(report, encoding="utf-8")
    print(report)
    # Z-09: the workflow dispatches the watcher as soon as main is behind at
    # all, before the SLO is breached, so detection takes a day, not a week.
    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        with open(github_output, "a", encoding="utf-8") as handle:
            for level in levels:
                handle.write(f"{level.name}_behind={len(level.behind)}\n")
    return 0 if all(level.ok for level in levels) else 1


if __name__ == "__main__":
    raise SystemExit(main())
