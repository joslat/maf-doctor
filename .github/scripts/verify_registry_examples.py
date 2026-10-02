#!/usr/bin/env python3
"""Compiler oracle for registry entries (ROADMAP Q-02, oracle 2b).

For every registry entry introduced in ``--version``, ``maf-doctor
verify-examples`` compiles ``example_before`` against the OLD side of the train
and ``example_after`` against the NEW side, and checks that a ``cs_warning``
naming a compiler diagnostic really appears when the old code meets the new
packages. This script supplies the reference assemblies: it restores the exact
package versions the train lock records (the same ones the watcher diffed) in
an isolated temporary project and lists their compile-time assets plus the
framework reference packs. Restore only downloads packages; no package build
logic runs, and only ``Microsoft.Agents.AI*`` ids (a prefix reserved on
nuget.org) are accepted from the lock.

Exit 0 when every checkable example holds, 1 on a compile failure or a claim
mismatch, 2 when the oracle cannot run (missing lock entries, restore failure).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
REGISTRY = REPO / ".github" / "skills" / "maf-obsolete-api-registry" / "registry.yaml"
TRAIN_LOCK = REPO / ".github" / "maf-train-lock.json"
PACKAGE_ID = re.compile(r"Microsoft\.Agents\.AI(?:\.[A-Za-z0-9]+)*")
PACKAGE_VERSION = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?")
TFM_PREFERENCE = ("net10.0", "net9.0", "net8.0")


def dotnet_root() -> Path:
    root = os.environ.get("DOTNET_ROOT")
    if root and Path(root, "packs").is_dir():
        return Path(root)
    exe = shutil.which("dotnet")
    if exe is None:
        raise RuntimeError("dotnet is not on PATH")
    return Path(os.path.realpath(exe)).parent


def reference_pack(root: Path, framework: str, tfm: str) -> Path | None:
    """Newest ``packs/<framework>.Ref/<version>/ref/<tfm>`` for the TFM's major version."""
    major = tfm[3:].split(".")[0]
    base = root / "packs" / f"{framework}.Ref"
    if not base.is_dir():
        return None
    versions = [v for v in base.iterdir() if v.name.split(".")[0] == major and (v / "ref" / tfm).is_dir()]
    if not versions:
        return None
    newest = max(versions, key=lambda v: tuple(int(p) if p.isdigit() else 0 for p in re.split(r"[.-]", v.name)))
    return newest / "ref" / tfm


def pick_tfm(root: Path) -> str:
    for tfm in TFM_PREFERENCE:
        if reference_pack(root, "Microsoft.NETCore.App", tfm) is not None:
            return tfm
    raise RuntimeError(f"no Microsoft.NETCore.App reference pack for {', '.join(TFM_PREFERENCE)} under {root / 'packs'}")


def validate(packages: dict[str, str]) -> dict[str, str]:
    bad = [f"{k}@{v}" for k, v in packages.items() if not PACKAGE_ID.fullmatch(k) or not PACKAGE_VERSION.fullmatch(v)]
    if bad:
        raise ValueError(f"train lock holds ids or versions this oracle will not restore: {', '.join(bad)}")
    return packages


def project_xml(packages: dict[str, str], tfm: str) -> str:
    items = "\n".join(f'    <PackageReference Include="{pid}" Version="[{ver}]" />' for pid, ver in sorted(packages.items()))
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
    <!-- Preview packages of one train can disagree on a dependency's minimum version. -->
    <NoWarn>$(NoWarn);NU1603;NU1605;NU1608;NU1701;NU1902;NU1903;NU1904</NoWarn>
    <WarningsAsErrors></WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
{items}
  </ItemGroup>
</Project>
"""


def assets_references(assets: dict, tfm: str, root: Path) -> list[str]:
    target = next((v for k, v in assets["targets"].items() if k.split("/")[0] == tfm), None)
    if target is None:
        raise RuntimeError(f"project.assets.json has no {tfm} target")
    folders = list(assets.get("packageFolders", {}))
    libraries = assets["libraries"]
    frameworks = {"Microsoft.NETCore.App"}
    frameworks.update(assets.get("project", {}).get("frameworks", {}).get(tfm, {}).get("frameworkReferences", {}))
    paths: list[str] = []
    for key, info in target.items():
        if info.get("type") != "package":
            continue
        frameworks.update(info.get("frameworkReferences", []))
        lib_path = libraries[key]["path"]
        for asset in info.get("compile", {}):
            if asset.endswith("/_._"):
                continue
            for folder in folders:
                candidate = Path(folder) / lib_path / asset
                if candidate.is_file():
                    paths.append(str(candidate))
                    break
    for framework in sorted(frameworks):
        pack = reference_pack(root, framework, tfm)
        if pack is None:
            print(f"warning: no {framework} reference pack for {tfm}; types from it will not resolve", file=sys.stderr)
            continue
        paths.extend(str(p) for p in sorted(pack.glob("*.dll")))
    return paths


def restore(packages: dict[str, str], work: Path, tfm: str, root: Path) -> list[str]:
    work.mkdir(parents=True, exist_ok=True)
    # Stop MSBuild from importing the repository's props (central package
    # management, locked restore) when the work dir sits inside a checkout.
    (work / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    (work / "Directory.Build.targets").write_text("<Project />\n", encoding="utf-8")
    (work / "Directory.Packages.props").write_text("<Project />\n", encoding="utf-8")
    project = work / "refs.csproj"
    project.write_text(project_xml(packages, tfm), encoding="utf-8")
    run = subprocess.run(["dotnet", "restore", str(project), "--nologo", "-v", "q"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    if run.returncode != 0:
        raise RuntimeError(f"restore failed for {work.name}:\n{run.stdout[-3000:]}\n{run.stderr[-2000:]}")
    assets = json.loads((work / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    return assets_references(assets, tfm, root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="registry version_introduced to check (the train's new side)")
    parser.add_argument("--old-version", required=True, help="the train's old side")
    parser.add_argument("--registry", type=Path, default=REGISTRY)
    parser.add_argument("--train-lock", type=Path, default=TRAIN_LOCK)
    parser.add_argument("--maf-doctor", default="maf-doctor", help='command that runs maf-doctor, e.g. "dotnet path/maf-doctor.dll"')
    parser.add_argument("--work-dir", type=Path, help="where to restore (default: a temporary directory)")
    parser.add_argument("--report-out", type=Path)
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    lock = json.loads(args.train_lock.read_text(encoding="utf-8"))["trains"]
    if args.version not in lock or args.old_version not in lock:
        print(f"error: train lock has no entry for {args.old_version} and/or {args.version}", file=sys.stderr)
        return 2
    work = args.work_dir or Path(tempfile.mkdtemp(prefix="maf-examples-"))
    try:
        root = dotnet_root()
        tfm = pick_tfm(root)
        lists = {}
        for side, version in (("old", args.old_version), ("new", args.version)):
            refs = restore(validate(lock[version]), work / f"{side}-{version}", tfm, root)
            lists[side] = work / f"{side}-{version}.refs.txt"
            lists[side].write_text("\n".join(refs) + "\n", encoding="utf-8")
    except (RuntimeError, ValueError, OSError, subprocess.TimeoutExpired) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    env = {**os.environ, "MAF_REGISTRY_PATH": str(args.registry.resolve())}
    run = subprocess.run([*args.maf_doctor.split(), "verify-examples", "--version", args.version,
                          "--old-refs", str(lists["old"]), "--new-refs", str(lists["new"])],
                         capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=900)
    report = f"Packages: MAF {args.old_version} → {args.version} from the train lock ({tfm}).\n{run.stdout}"
    print(report)
    if run.stderr.strip():
        print(run.stderr, file=sys.stderr)
    if args.report_out:
        args.report_out.write_text(report, encoding="utf-8")
    return run.returncode if run.returncode in (0, 1) else 2


if __name__ == "__main__":
    raise SystemExit(main())
