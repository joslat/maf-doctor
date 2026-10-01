"""Tests for the registry signature oracle (ROADMAP Q-02, oracle 2a)."""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import verify_registry_signatures as vrs  # noqa: E402


class FakeInspector:
    """Package listings keyed by (type, package, version)."""

    def __init__(self, listings: dict[tuple[str, str, str], str]):
        self.listings = listings

    def members(self, type_name, package, version):
        return self.listings.get((type_name, package, version))


OLD = {"Microsoft.Agents.AI.Hosting": "1.21.0-preview.1", "Microsoft.Agents.AI.Abstractions": "1.21.0"}
NEW = {"Microsoft.Agents.AI.Hosting": "1.22.0-preview.1", "Microsoft.Agents.AI.Abstractions": "1.22.0"}
LISTINGS = {
    ("InMemoryAgentSessionStore", "Microsoft.Agents.AI.Hosting", "1.21.0-preview.1"):
        "InMemoryAgentSessionStore\n  ValueTask DeleteSessionAsync(AIAgent agent, string id)\n  ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string id)",
    ("InMemoryAgentSessionStore", "Microsoft.Agents.AI.Hosting", "1.22.0-preview.1"):
        "InMemoryAgentSessionStore\n  ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key)",
    ("AgentSessionStore", "Microsoft.Agents.AI.Abstractions", "1.22.0"):
        "AgentSessionStore\n  ValueTask<AgentSession> GetOrCreateSessionAsync(AIAgent agent, AgentSessionStoreKey key)\n  void .ctor()",
}


def _entry(**overrides):
    entry = {"id": "MAF122-TEST-001", "package": "Microsoft.Agents.AI.Hosting", "type": "InMemoryAgentSessionStore",
             "obsolete_signature": "ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string id)",
             "replacement_signature": "ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key)"}
    entry.update(overrides)
    return entry


def _check(entry):
    return vrs.check_entry(entry, OLD, NEW, FakeInspector(LISTINGS))


@pytest.mark.parametrize(("signature", "member", "owner"), [
    ("System.Threading.Tasks.ValueTask<AgentSession?> GetSessionAsync(AIAgent agent)", "GetSessionAsync", None),
    ("void .ctor(A2A.IA2AClient a2aClient)", ".ctor", None),
    ("Microsoft.Agents.AI.Hosting.A2A.AgentRunMode ReturnMessage { get; }", "ReturnMessage", None),
    ("AIFunction ShellExecutor.AsAIFunction(string name)", "AsAIFunction", "ShellExecutor"),
    ("ShellEnvironmentProvider(ShellExecutor, Options? o = null) and ShellExecutor.AsAIFunction(string n)", "ShellEnvironmentProvider", None),
    ("Microsoft.Agents.AI.AgentSessionStore (and DelegatingAgentSessionStore) base types", None, None),
    ("removed; no direct replacement", None, None),
])
def test_signature_parsing(signature, member, owner):
    assert vrs.member_name(signature) == member
    assert vrs.member_owner(signature) == owner


def test_correct_entry_passes():
    assert _check(_entry()) == []


def test_invented_replacement_member_is_reported():
    findings = _check(_entry(replacement_signature="ValueTask<AgentSession?> LoadSessionAsync(AIAgent agent, AgentSessionStoreKey key)"))
    assert [(f.side, "LoadSessionAsync" in f.message) for f in findings] == [("new", True)]


def test_obsolete_member_that_never_existed_is_reported():
    findings = _check(_entry(obsolete_signature="ValueTask FetchSessionAsync(AIAgent agent, string id)"))
    assert [f.side for f in findings] == ["old"]


def test_removed_replacement_is_not_checked():
    assert _check(_entry(obsolete_signature="ValueTask DeleteSessionAsync(AIAgent agent, string id)",
                         replacement_signature="removed; no direct replacement")) == []


def test_replacement_on_another_type_in_another_package_of_the_train():
    entry = _entry(replacement_signature="ValueTask<AgentSession> AgentSessionStore.GetOrCreateSessionAsync(AIAgent agent, AgentSessionStoreKey key)")
    assert _check(entry) == []


def test_package_missing_from_the_lock_is_skipped():
    assert _check(_entry(package="Microsoft.Agents.AI.Brand.New")) == []


def test_live_lock_and_registry_load(tmp_path):
    # The CLI must fail with 2 (cannot run) for a train the lock does not know.
    assert vrs.main(["--old-version", "0.0.1", "--version", "0.0.2", "--dotnet-inspect", "unused"]) == 2
