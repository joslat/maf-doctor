"""Tests for the package-aware MAF release diff plan builder."""
from __future__ import annotations

import copy
import json
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import build_maf_package_plan as planner  # noqa: E402


MANIFEST_PATH = Path(__file__).resolve().parents[2] / "maf-package-surfaces.json"


def manifest() -> dict:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


def fetch_from(indexes: dict[str, list[str]]):
    def fetch(package: str) -> list[str]:
        return indexes[package]

    return fetch


def indexes_1_13_to_1_14() -> dict[str, list[str]]:
    return {
        "Microsoft.Agents.AI": ["1.13.0", "1.14.0"],
        "Microsoft.Agents.AI.Workflows": ["1.13.0", "1.14.0"],
        "Microsoft.Agents.AI.Harness": [
            "1.13.0-preview.260703.1",
            "1.14.0",
        ],
        "Microsoft.Agents.AI.Hosting": [
            "1.13.0-preview.260703.1",
            "1.14.0-preview.260721.1",
        ],
        "Microsoft.Agents.AI.Hosting.OpenAI": [
            "1.13.0-alpha.260703.1",
            "1.14.0-alpha.260721.1",
        ],
        "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore": [
            "1.13.0-preview.260703.1",
            "1.14.0-preview.260721.1",
        ],
        "Microsoft.Agents.AI.DurableTask": [
            "1.13.0-preview.260703.1",
            "1.14.0-preview.260721.1",
        ],
        "Microsoft.Agents.AI.Hosting.AzureFunctions": [
            "1.13.0-preview.260703.1",
            "1.14.0-preview.260721.1",
        ],
        "Microsoft.Agents.AI.GitHub.Copilot": [
            "1.13.0-rc1",
            "1.14.0-rc1",
        ],
        "Microsoft.Agents.AI.Tools.Shell": [
            "1.13.0-preview.260703.1",
            "1.14.0-preview.260721.1",
        ],
    }


def indexes_1_16_to_1_17() -> dict[str, list[str]]:
    return {
        "Microsoft.Agents.AI": ["1.16.0", "1.17.0"],
        "Microsoft.Agents.AI.Workflows": ["1.16.0", "1.17.0"],
        "Microsoft.Agents.AI.Harness": ["1.16.0", "1.17.0"],
        "Microsoft.Agents.AI.Hosting": [
            "1.16.0-preview.260730.1",
            "1.17.0-preview.260804.1",
        ],
        "Microsoft.Agents.AI.Hosting.OpenAI": [
            "1.16.0-alpha.260730.1",
            "1.17.0-alpha.260804.1",
        ],
        "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore": [
            "1.16.0-preview.260730.1",
            "1.17.0-preview.260804.1",
        ],
        "Microsoft.Agents.AI.DurableTask": ["1.16.0-preview.260730.1"],
        "Microsoft.Agents.AI.Hosting.AzureFunctions": [
            "1.16.0-preview.260730.1"
        ],
        "Microsoft.Agents.AI.GitHub.Copilot": ["1.16.0", "1.17.0"],
        "Microsoft.Agents.AI.Tools.Shell": [
            "1.16.0-preview.260730.1",
            "1.17.0-preview.260804.1",
        ],
    }


def test_checked_in_manifest_is_valid_and_ordered():
    loaded = planner.load_manifest(MANIFEST_PATH)
    surfaces = loaded["surfaces"]

    assert [surface["order"] for surface in surfaces] == list(range(10))
    assert [surface["slug"] for surface in surfaces] == [
        "core",
        "workflows",
        "harness",
        "hosting",
        "hosting-openai",
        "hosting-agui",
        "durable",
        "azure-functions",
        "github-copilot",
        "tools-shell",
    ]
    assert surfaces[0]["id_scope"] == ""
    assert all(surface["id_scope"] for surface in surfaces[1:])


def test_exact_stable_wins_over_aligned_prereleases():
    result = planner.resolve_train_version(
        ["1.14.0-preview.1", "1.14.0", "1.14.0-rc1"], "1.14.0"
    )
    assert result.state == "resolved"
    assert result.value == "1.14.0"


def test_single_aligned_prerelease_is_resolved():
    result = planner.resolve_train_version(
        ["1.13.0-preview.260703.1", "1.14.0"], "1.13.0"
    )
    assert result.state == "resolved"
    assert result.value == "1.13.0-preview.260703.1"


def test_multiple_aligned_prereleases_are_ambiguous_not_guessed():
    result = planner.resolve_train_version(
        ["1.14.0-preview.1", "1.14.0-preview.2"], "1.14.0"
    )
    assert result.state == "ambiguous"
    assert result.value is None
    assert "preview.1" in result.reason and "preview.2" in result.reason


def test_missing_train_is_not_guessed_from_neighbor_version():
    result = planner.resolve_train_version(["1.13.0", "1.15.0"], "1.14.0")
    assert result.state == "missing"
    assert result.value is None


def test_explicit_prerelease_requires_an_exact_match():
    result = planner.resolve_train_version(
        ["2.0.0-rc1.1", "2.0.0-rc1.2"], "2.0.0-rc1"
    )
    assert result.state == "missing"
    assert result.value is None


def test_1_14_plan_resolves_harness_preview_to_ga_and_selects_split_event():
    plan = planner.build_plan(
        manifest(),
        "1.13.0",
        "1.14.0",
        fetch_from(indexes_1_13_to_1_14()),
    )

    assert plan["release"] == {
        "old_version": "1.13.0",
        "new_version": "1.14.0",
    }
    assert all(surface["status"] == "diffable" for surface in plan["surfaces"])

    harness = next(s for s in plan["surfaces"] if s["slug"] == "harness")
    assert harness["old_package_version"] == "1.13.0-preview.260703.1"
    assert harness["new_package_version"] == "1.14.0"
    assert harness["id_scope"] == "HARNESS"

    hosting_openai = next(
        s for s in plan["surfaces"] if s["slug"] == "hosting-openai"
    )
    assert hosting_openai["old_package_version"] == "1.13.0-alpha.260703.1"
    assert hosting_openai["new_package_version"] == "1.14.0-alpha.260721.1"

    github_copilot = next(
        s for s in plan["surfaces"] if s["slug"] == "github-copilot"
    )
    assert github_copilot["old_package_version"] == "1.13.0-rc1"
    assert github_copilot["new_package_version"] == "1.14.0-rc1"
    assert github_copilot["id_scope"] == "GITHUB-COPILOT"

    tools_shell = next(s for s in plan["surfaces"] if s["slug"] == "tools-shell")
    assert tools_shell["old_package_version"] == "1.13.0-preview.260703.1"
    assert tools_shell["new_package_version"] == "1.14.0-preview.260721.1"
    assert tools_shell["id_scope"] == "TOOLS-SHELL"

    assert [event["id"] for event in plan["lifecycle_events"]] == [
        "maf-1.14.0-agui-split"
    ]
    event = plan["lifecycle_events"][0]
    assert event["kind"] == "package_split"
    assert event["impact"] == "breaking"
    assert event["related_surfaces"] == ["hosting-agui"]
    assert [item["package"] for item in event["target_packages"]] == [
        "AGUI.Client",
        "AGUI.Server",
        "AGUI.Abstractions",
        "AGUI.Protobuf",
        "AGUI.Formatting",
    ]
    assert {item["version"] for item in event["target_packages"]} == {"0.0.3"}


def test_1_15_hosting_base_and_openai_versions_are_diffable():
    indexes = {
        surface["package"]: ["1.14.0", "1.15.0"]
        for surface in manifest()["surfaces"]
    }
    indexes["Microsoft.Agents.AI.Hosting.OpenAI"] = [
        "1.14.0-alpha.260721.1",
        "1.15.0-alpha.260722.1",
    ]
    indexes["Microsoft.Agents.AI.Hosting"] = [
        "1.14.0-preview.260721.1",
        "1.15.0-preview.260722.1",
    ]
    indexes["Microsoft.Agents.AI.GitHub.Copilot"] = [
        "1.14.0-rc1",
        "1.15.0-rc1",
    ]
    indexes["Microsoft.Agents.AI.Tools.Shell"] = [
        "1.14.0-preview.260721.1",
        "1.15.0-preview.260722.1",
    ]
    plan = planner.build_plan(
        manifest(), "1.14.0", "1.15.0", fetch_from(indexes)
    )

    hosting_base = next(s for s in plan["surfaces"] if s["slug"] == "hosting")
    assert hosting_base["status"] == "diffable"
    assert hosting_base["old_package_version"] == "1.14.0-preview.260721.1"
    assert hosting_base["new_package_version"] == "1.15.0-preview.260722.1"

    hosting_openai = next(
        s for s in plan["surfaces"] if s["slug"] == "hosting-openai"
    )
    assert hosting_openai["status"] == "diffable"
    assert hosting_openai["old_package_version"] == "1.14.0-alpha.260721.1"
    assert hosting_openai["new_package_version"] == "1.15.0-alpha.260722.1"
    assert plan["lifecycle_events"] == []


def test_1_17_declared_externalization_is_informational_not_missing():
    plan = planner.build_plan(
        manifest(),
        "1.16.0",
        "1.17.0",
        fetch_from(indexes_1_16_to_1_17()),
    )

    by_slug = {surface["slug"]: surface for surface in plan["surfaces"]}
    assert by_slug["durable"]["status"] == "informational"
    assert by_slug["azure-functions"]["status"] == "informational"
    assert by_slug["durable"]["old_package_version"] == (
        "1.16.0-preview.260730.1"
    )
    assert by_slug["durable"]["new_package_version"] is None
    assert all(
        by_slug[slug]["status"] == "diffable"
        for slug in (
            "core",
            "workflows",
            "harness",
            "hosting-openai",
            "hosting-agui",
        )
    )

    assert [event["id"] for event in plan["lifecycle_events"]] == [
        "maf-1.17.0-durable-extension-externalization"
    ]
    event = plan["lifecycle_events"][0]
    assert event["impact"] == "informational"
    assert event["same_package_ids"] is True
    assert event["target_repository"] == (
        "microsoft/agent-framework-durable-extension"
    )


def test_missing_harness_evidence_is_unverifiable_while_other_surfaces_continue():
    indexes = indexes_1_13_to_1_14()
    indexes["Microsoft.Agents.AI.Harness"] = ["1.14.0"]
    plan = planner.build_plan(
        manifest(), "1.13.0", "1.14.0", fetch_from(indexes)
    )
    by_slug = {surface["slug"]: surface for surface in plan["surfaces"]}

    assert by_slug["harness"]["status"] == "unverifiable"
    assert by_slug["harness"]["old_package_version"] is None
    assert by_slug["harness"]["new_package_version"] == "1.14.0"
    assert by_slug["core"]["status"] == "diffable"


def test_ambiguous_package_train_is_unverifiable():
    indexes = indexes_1_13_to_1_14()
    indexes["Microsoft.Agents.AI.Harness"] = [
        "1.13.0-preview.260703.1",
        "1.13.0-preview.260703.2",
        "1.14.0",
    ]
    plan = planner.build_plan(
        manifest(), "1.13.0", "1.14.0", fetch_from(indexes)
    )
    harness = next(s for s in plan["surfaces"] if s["slug"] == "harness")

    assert harness["status"] == "unverifiable"
    assert harness["old_package_version"] is None
    assert "multiple aligned package versions" in harness["reason"]


def test_network_error_is_bounded_and_unverifiable_not_informational():
    indexes = indexes_1_16_to_1_17()

    def fetch(package: str) -> list[str]:
        if package == "Microsoft.Agents.AI.DurableTask":
            raise OSError("offline\n" + "x" * 1000)
        return indexes[package]

    plan = planner.build_plan(manifest(), "1.16.0", "1.17.0", fetch)
    durable = next(s for s in plan["surfaces"] if s["slug"] == "durable")

    assert durable["status"] == "unverifiable"
    assert durable["old_package_version"] is None
    assert durable["new_package_version"] is None
    assert "offline " in durable["reason"]
    assert "\n" not in durable["reason"]
    assert len(durable["reason"]) < 400


def test_plan_order_artifact_names_and_rendering_are_deterministic():
    args = (
        manifest(),
        "1.13.0",
        "1.14.0",
        fetch_from(indexes_1_13_to_1_14()),
    )
    first = planner.build_plan(*args)
    second = planner.build_plan(*args)

    assert first == second
    assert planner.render_plan(first) == planner.render_plan(second)
    assert planner.render_plan(first).endswith("\n")
    for expected_order, surface in enumerate(first["surfaces"]):
        assert surface["order"] == expected_order
        assert surface["diff_artifact"] == f"diff-{surface['slug']}.txt"
        assert surface["stderr_artifact"] == (
            f"diff-{surface['slug']}.stderr.txt"
        )
        assert "/" not in surface["diff_artifact"]


@pytest.mark.parametrize(
    "mutation, expected",
    [
        (
            lambda doc: doc["surfaces"][1].update(slug="core"),
            "duplicate surface slug",
        ),
        (
            lambda doc: doc["surfaces"][1].update(package="microsoft.agents.ai"),
            "duplicate surface package",
        ),
        (
            lambda doc: doc["surfaces"][2].update(package="bad;package"),
            "must contain only",
        ),
        (
            lambda doc: doc["surfaces"][2].update(id_scope="lowercase"),
            "uppercase ID segment",
        ),
        (
            lambda doc: doc["surfaces"][2].update(order=99),
            "surface order must be contiguous",
        ),
        (
            lambda doc: doc["lifecycle_events"][0].update(
                related_surfaces=["does-not-exist"]
            ),
            "unknown slug",
        ),
        (
            lambda doc: doc["lifecycle_events"][0].update(
                related_surfaces=[{"slug": "hosting-agui"}]
            ),
            r"related_surfaces\[0\] must be a non-empty string",
        ),
        (
            lambda doc: doc["lifecycle_events"][0]["source_packages"][0].update(
                version="not-a-version"
            ),
            "not a supported SemVer",
        ),
        (
            lambda doc: doc["lifecycle_events"][1]["target_packages"][0].update(
                package="Different.Package"
            ),
            "identical source/target package IDs",
        ),
    ],
)
def test_manifest_validation_rejects_invalid_or_dangling_data(mutation, expected):
    doc = copy.deepcopy(manifest())
    mutation(doc)
    with pytest.raises(planner.ManifestValidationError, match=expected):
        planner.validate_manifest(doc)


def test_fetch_nuget_versions_uses_flat_container_and_validates_json():
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self, size):
            assert size == planner.MAX_NUGET_INDEX_BYTES + 1
            return b'{"versions":["1.13.0","1.14.0-preview.1"]}'

    def opener(request, timeout):
        assert request.full_url.endswith("/microsoft.agents.ai.harness/index.json")
        assert timeout == 7
        return Response()

    assert planner.fetch_nuget_versions(
        "Microsoft.Agents.AI.Harness", opener=opener, timeout=7
    ) == ["1.13.0", "1.14.0-preview.1"]


def test_fetch_nuget_versions_rejects_oversized_index():
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self, size):
            return b"x" * size

    with pytest.raises(planner.NuGetIndexError, match="exceeds"):
        planner.fetch_nuget_versions(
            "Microsoft.Agents.AI", opener=lambda *_args, **_kwargs: Response()
        )


@pytest.mark.parametrize(
    ("old_version", "new_version"),
    [
        ("1.14.0", "1.14.0"),
        ("1.15.0", "1.14.0"),
        ("2.0.0", "1.99.0"),
        ("1.14.0-preview.1", "1.14.0"),
    ],
)
def test_plan_rejects_equal_or_reverse_release_trains(old_version, new_version):
    with pytest.raises(ValueError, match="numerically earlier release train"):
        planner.build_plan(manifest(), old_version, new_version, lambda _package: [])


def test_plan_compares_release_trains_numerically_not_lexically():
    plan = planner.build_plan(
        manifest(),
        "1.9.0",
        "1.10.0",
        lambda _package: ["1.9.0", "1.10.0"],
    )

    assert all(surface["status"] == "diffable" for surface in plan["surfaces"])


def test_dry_run_prints_plan_and_does_not_touch_output(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(
        planner,
        "fetch_nuget_versions",
        lambda _package: ["1.13.0", "1.14.0"],
    )
    output = tmp_path / "must-not-exist.json"

    rc = planner.main(
        [
            "--manifest",
            str(MANIFEST_PATH),
            "--old-version",
            "1.13.0",
            "--new-version",
            "1.14.0",
            "--output",
            str(output),
            "--dry-run",
        ]
    )

    assert rc == 0
    assert not output.exists()
    rendered = capsys.readouterr().out
    parsed = json.loads(rendered)
    assert parsed["release"]["new_version"] == "1.14.0"
    assert len(parsed["surfaces"]) == 10


def test_non_dry_run_writes_selected_output(tmp_path, monkeypatch):
    monkeypatch.setattr(
        planner,
        "fetch_nuget_versions",
        lambda _package: ["1.14.0", "1.15.0"],
    )
    output = tmp_path / "nested" / "plan.json"

    rc = planner.main(
        [
            "--manifest",
            str(MANIFEST_PATH),
            "--old-version",
            "1.14.0",
            "--new-version",
            "1.15.0",
            "--output",
            str(output),
        ]
    )

    assert rc == 0
    assert json.loads(output.read_text(encoding="utf-8"))["release"] == {
        "old_version": "1.14.0",
        "new_version": "1.15.0",
    }


# ---------------------------------------------------------------------------
# P-01 / P-05: externalization is lasting state; the train lock pins the old side
# ---------------------------------------------------------------------------


def _indexes_after_externalization(old: str, new: str) -> dict[str, list[str]]:
    """Every in-train surface resolves for old/new; durable packages stop at 1.16."""

    def aligned(train: str, tag: str = "preview.260901.1") -> str:
        return f"{train}-{tag}"

    durable = ["1.16.0-preview.260730.1", "1.16.0-preview.260922.1"]
    return {
        "Microsoft.Agents.AI": [old, new],
        "Microsoft.Agents.AI.Workflows": [old, new],
        "Microsoft.Agents.AI.Harness": [old, new],
        "Microsoft.Agents.AI.Hosting": [aligned(old), aligned(new)],
        "Microsoft.Agents.AI.Hosting.OpenAI": [aligned(old, "alpha.1"), aligned(new, "alpha.1")],
        "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore": [aligned(old), aligned(new)],
        "Microsoft.Agents.AI.DurableTask": list(durable),
        "Microsoft.Agents.AI.Hosting.AzureFunctions": list(durable),
        "Microsoft.Agents.AI.GitHub.Copilot": [old, new],
        "Microsoft.Agents.AI.Tools.Shell": [aligned(old), aligned(new)],
    }


@pytest.mark.parametrize(("old", "new"), [("1.17.0", "1.18.0"), ("1.22.0", "1.23.0")])
def test_externalization_persists_for_every_later_train(old, new):
    plan = planner.build_plan(manifest(), old, new, fetch_from(_indexes_after_externalization(old, new)))
    by_slug = {s["slug"]: s for s in plan["surfaces"]}

    for slug in ("durable", "azure-functions"):
        assert by_slug[slug]["status"] == "informational"
        assert "Externalized since MAF 1.17.0" in by_slug[slug]["reason"]
        assert "microsoft/agent-framework-durable-extension" in by_slug[slug]["reason"]
    assert sum(s["status"] == "diffable" for s in plan["surfaces"]) == 8
    # Plan events stay scoped to the train itself (evidence-contract invariant).
    assert plan["lifecycle_events"] == []


def test_externalized_surface_that_realigns_is_diffed_normally():
    indexes = _indexes_after_externalization("1.18.0", "1.19.0")
    indexes["Microsoft.Agents.AI.DurableTask"] += ["1.18.0-preview.1", "1.19.0-preview.1"]
    plan = planner.build_plan(manifest(), "1.18.0", "1.19.0", fetch_from(indexes))
    durable = next(s for s in plan["surfaces"] if s["slug"] == "durable")
    assert durable["status"] == "diffable"
    assert (durable["old_package_version"], durable["new_package_version"]) == (
        "1.18.0-preview.1",
        "1.19.0-preview.1",
    )


def test_externalization_does_not_relax_other_surfaces():
    indexes = _indexes_after_externalization("1.17.0", "1.18.0")
    indexes["Microsoft.Agents.AI.Harness"] = ["1.17.0"]
    plan = planner.build_plan(manifest(), "1.17.0", "1.18.0", fetch_from(indexes))
    harness = next(s for s in plan["surfaces"] if s["slug"] == "harness")
    assert harness["status"] == "unverifiable"


def test_externalization_never_applies_before_its_release():
    indexes = _indexes_after_externalization("1.15.0", "1.16.0")
    indexes["Microsoft.Agents.AI.DurableTask"] = ["1.15.0-preview.1"]
    plan = planner.build_plan(manifest(), "1.15.0", "1.16.0", fetch_from(indexes))
    durable = next(s for s in plan["surfaces"] if s["slug"] == "durable")
    assert durable["status"] == "unverifiable"


def test_transition_train_uses_event_pinned_source_after_upstream_republish():
    indexes = _indexes_after_externalization("1.16.0", "1.17.0")
    plan = planner.build_plan(manifest(), "1.16.0", "1.17.0", fetch_from(indexes))
    durable = next(s for s in plan["surfaces"] if s["slug"] == "durable")
    assert durable["status"] == "informational"
    assert durable["old_package_version"] == "1.16.0-preview.260730.1"


def test_train_lock_pins_old_side_over_ambiguous_alignment():
    indexes = _indexes_after_externalization("1.18.0", "1.19.0")
    indexes["Microsoft.Agents.AI.Hosting"] = [
        "1.18.0-preview.260818.1",
        "1.18.0-preview.260930.1",
        "1.19.0-preview.260901.1",
    ]
    lock = {"schema_version": 1, "trains": {"1.18.0": {
        "Microsoft.Agents.AI.Hosting": "1.18.0-preview.260818.1"}}}
    without = planner.build_plan(manifest(), "1.18.0", "1.19.0", fetch_from(indexes))
    with_lock = planner.build_plan(
        manifest(), "1.18.0", "1.19.0", fetch_from(indexes), train_lock=lock
    )
    hosting = lambda plan: next(s for s in plan["surfaces"] if s["slug"] == "hosting")  # noqa: E731
    assert hosting(without)["status"] == "unverifiable"
    assert hosting(with_lock)["status"] == "diffable"
    assert hosting(with_lock)["old_package_version"] == "1.18.0-preview.260818.1"


def test_train_lock_does_not_pin_new_side_unless_replaying():
    indexes = _indexes_after_externalization("1.18.0", "1.19.0")
    indexes["Microsoft.Agents.AI.Hosting"] += ["1.19.0-preview.260930.1"]
    lock = {"schema_version": 1, "trains": {"1.19.0": {
        "Microsoft.Agents.AI.Hosting": "1.19.0-preview.260901.1"}}}
    live = planner.build_plan(manifest(), "1.18.0", "1.19.0", fetch_from(indexes), train_lock=lock)
    replay = planner.build_plan(
        manifest(), "1.18.0", "1.19.0", fetch_from(indexes), train_lock=lock, lock_new_side=True
    )
    hosting = lambda plan: next(s for s in plan["surfaces"] if s["slug"] == "hosting")  # noqa: E731
    assert hosting(live)["status"] == "unverifiable"
    assert hosting(replay)["new_package_version"] == "1.19.0-preview.260901.1"


def test_lock_version_missing_from_index_falls_back_to_alignment():
    indexes = _indexes_after_externalization("1.18.0", "1.19.0")
    lock = {"schema_version": 1, "trains": {"1.18.0": {"Microsoft.Agents.AI": "1.18.0-gone"}}}
    plan = planner.build_plan(manifest(), "1.18.0", "1.19.0", fetch_from(indexes), train_lock=lock)
    core = next(s for s in plan["surfaces"] if s["slug"] == "core")
    assert core["old_package_version"] == "1.18.0"


@pytest.mark.parametrize(
    "payload",
    [
        {"schema_version": 2, "trains": {}},
        {"schema_version": 1, "trains": []},
        {"schema_version": 1, "trains": {"1.18.0-rc.1": {"A": "1.0.0"}}},
        {"schema_version": 1, "trains": {"1.18.0": {}}},
        {"schema_version": 1, "trains": {"1.18.0": {"bad id!": "1.0.0"}}},
        {"schema_version": 1, "trains": {"1.18.0": {"A": "not-a-version"}}},
    ],
)
def test_train_lock_validation_rejects_malformed_documents(tmp_path, payload):
    path = tmp_path / "lock.json"
    path.write_text(json.dumps(payload), encoding="utf-8")
    with pytest.raises(planner.TrainLockError):
        planner.load_train_lock(path)


def test_missing_train_lock_file_is_an_empty_lock(tmp_path):
    assert planner.load_train_lock(tmp_path / "absent.json")["trains"] == {}
