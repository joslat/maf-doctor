# syntax=docker/dockerfile:1
#
# Containerised distribution of the maf-autopilot MCP server.
#
# Usage:
#   docker pull ghcr.io/joslat/maf-doctor:latest
#   docker run --rm -i ghcr.io/joslat/maf-doctor
#
# The container speaks MCP over stdio. Wire it up in your IDE's mcp.json with:
#   {
#     "servers": {
#       "maf-doctor": {
#         "type": "stdio",
#         "command": "docker",
#         "args": ["run", "--rm", "-i", "ghcr.io/joslat/maf-doctor:latest"]
#       }
#     }
#   }
#
# Image stays small because the registry YAML, migration guide, and skill
# documents are embedded into the .dll at build time (no extra COPY needed).

# ---------- Stage 1 — build ----------
# SDK 10.0 required because the csproj multi-targets net8.0/net9.0/net10.0
# (multi-version test parity with the analyzer NuGet). SDK 8.0 — which we used
# through the alphas — can't restore net9.0/net10.0 TFMs and fails NETSDK1045.
# F-27 (2026-07-19 security assessment) — digest-pinned, not a mutable `:10.0`
# tag: what builds today is guaranteed to build identically tomorrow, and a
# compromised/tampered upstream push under the same tag can't silently swap
# in a different image. Dependabot's `docker` ecosystem entry
# (.github/dependabot.yml) tracks and PRs digest bumps weekly, same pattern
# as the SHA-pinned GitHub Actions elsewhere in this repo. Resolve the
# current digest for a tag with: docker inspect <image>:<tag> --format '{{index .RepoDigests 0}}'
FROM mcr.microsoft.com/dotnet/sdk@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src

# Repo-root build-influencing files. CRITICAL: csproj uses Central Package
# Management (CPM) — package versions live in Directory.Packages.props, NOT
# in the csproj. Without these COPYs, restore fails NU1015 ("PackageReference
# items do not have a version specified"). global.json pins the SDK band.
COPY Directory.Build.props Directory.Packages.props global.json ./

# Copy the whole MCP server project in one shot — `.dockerignore` already
# excludes bin/obj/test-projects so this stays lean. Switched from per-folder
# COPYs after the docker-publish v1.0.0 run failed because the Commands/
# folder wasn't enumerated. Folder-by-folder COPYs are fragile — any new
# subfolder silently breaks the image build. Single COPY is robust.
COPY src/maf-autopilot/ src/maf-autopilot/

# Embedded resources required for `<EmbeddedResource>` in the csproj.
# The csproj embeds 4 files from docs/steering/ (init drops them into user repos);
# .dockerignore explicitly un-excludes that subfolder. Plus .github/skills,
# .github/instructions, guides/, and root README.md.
COPY .github/ .github/
COPY guides/ guides/
COPY docs/steering/ docs/steering/
# docs/migration/ holds the SK→MAF migration guide embedded as a resource in the
# csproj (LogicalName migrate-from/semantic-kernel.md, added in 1.5.0). Without
# this the in-Docker `dotnet publish` fails with CS1566 (missing resource file).
COPY docs/migration/ docs/migration/
COPY README.md ./

# Publish a single TFM (net10.0 — matches the runtime image below). Required
# because `dotnet publish` on a multi-TFM csproj without `--framework` fails.
RUN dotnet publish src/maf-autopilot/maf-autopilot.csproj \
    --configuration Release \
    --framework net10.0 \
    --output /app \
    --self-contained false \
    -p:UseAppHost=false

# ---------- Stage 2 — runtime ----------
# The runtime image is much smaller than the SDK image (~150 MB vs ~900 MB).
# Must match the --framework TFM published above. Digest-pinned — see the
# F-27 note on the build-stage FROM above.
FROM mcr.microsoft.com/dotnet/runtime@sha256:ed5d539b27842d656a06a5984dbcb5114d3e885fbada612a49a5a7c3c3a44e1c
WORKDIR /app

# Copy build output. Embedded resources (registry.yaml, constraints.md,
# guide.md, all 12 skill *.md files) are inside the dll, no extra COPY needed.
COPY --from=build /app .

# OCI labels — useful for `docker inspect` and registry browsing.
LABEL org.opencontainers.image.title="maf-doctor"
LABEL org.opencontainers.image.description="MCP server + Roslyn analyzers for Microsoft Agent Framework (MAF) — migration, audit, prompt-lint, cost auditor, scaffolder, simulator."
LABEL org.opencontainers.image.source="https://github.com/joslat/maf-doctor"
LABEL org.opencontainers.image.licenses="MIT"

# ---------------------------------------------------------------------------
# SEC-10 — container capability boundary.
# This is the RUNTIME image (no .NET SDK, by design — ~150 MB vs ~900 MB). Tools
# that shell out to the SDK are NOT available here; they detect the missing binary
# and return a clear "use the global tool" message at runtime rather than crashing:
#   * MafRunCs0618Hunt    — needs `dotnet build`      (SDK only)
#   * MafDiffPackage      — needs dotnet-inspect / dnx (SDK only)
#   * MafPreUpgradeDryRun — needs dotnet-inspect / dnx (SDK only)
# `git` IS installed below so MafAuditPullRequest works. For the SDK-dependent
# tools, install the .NET global tool instead: `dotnet tool install -g maf-doctor`.
# See docs/security.md -> "Container capability boundary".
# ---------------------------------------------------------------------------
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

# Security: drop root regardless. The MCP server speaks stdio only, and most
# tools are read-only, but auto-fix/scaffold/init tools DO write to a mounted
# workspace when invoked with dryRun: false — "writes nothing" was never
# accurate for those, and MafDoctorStatus/UpdateAdvisor also write a small
# update-check cache. A non-root UID limits what any of those writes (or a
# compromised dependency) can reach on the host. If you only want the
# read-only tools, mount the workspace `:ro` — see docs/security.md for the
# read-only vs. write-enabled run profiles.
#
# SEC-25: OpenShift "arbitrary uid, gid 0" convention — the user's PRIMARY group
# is 0 (uid 1001, gid 0), and `chmod -R g=u /app` grants group 0 the same access
# as the owner, so an arbitrary-uid host (which still runs with gid 0) can read
# default/group-0-permissioned workspace mounts without granting root.
RUN useradd -r -u 1001 -g 0 -G 0 -d /nonexistent -s /usr/sbin/nologin maf \
    && chown -R 1001:0 /app \
    && chmod -R g=u /app
USER maf

ENTRYPOINT ["dotnet", "/app/maf-doctor.dll"]
