using System.Reflection;
using MafDoctor;
using MafDoctor.Data;
using MafDoctor.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// CLI mode: short-circuit before the host builder so stdin/stdout remain clean.
// Supported subcommands:
//   maf-doctor init                       — scaffold .vscode/mcp.json + copilot-instructions
//   maf-doctor new agent <Name>           — generate a MAF agent + smoke test
//   maf-doctor new executor <Name> [In] [Out]  — generate a workflow executor + smoke test
//   maf-doctor doctor [path]              — one-command repo health report (A/B/C/F grade)
// --version / --help — short-circuit before anything else (and before the
// no-subcommand fall-through to MCP-server mode, which would otherwise hang
// waiting on stdio when a user runs `maf-doctor --version`).
// WM-26: pin invariant culture for BOTH CLI and MCP modes (and every tool/thread,
// including the update-notice Task.Run) so dates use the Gregorian calendar and
// numbers use '.'-decimals regardless of the host locale — matching the docs/examples.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

// REP-06: emoji + typographic punctuation mojibake to '?' on cmd / Windows PowerShell
// 5.1 and in redirected files unless the console is UTF-8. CLI mode only (args present);
// MCP stdio mode (no args) is left untouched — the MCP SDK owns its stream encoding.
if (args.Length > 0)
{
    // UTF8Encoding(false): NO BOM — `Encoding.UTF8` emits a BOM that some Windows
    // consoles prepend to redirected stdout, which would corrupt `--json` output.
    try { Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
    catch (IOException) { /* output redirected / no console — ignore */ }
    catch (System.Security.SecurityException) { /* restricted host — ignore */ }
}

if (args.Length > 0 && (args[0] is "--version" or "-v" or "version"))
{
    // REP-20: single source of truth for the version (build metadata already trimmed).
    Console.WriteLine($"maf-doctor {MafDoctor.ToolVersionInfo.Current}");
    Environment.Exit(0);
    return;
}
if (args.Length > 0 && (args[0] is "--help" or "-h" or "help"))
{
    Console.WriteLine(
        """
        maf-doctor — a toolkit for Microsoft Agent Framework (MAF)

        Usage: maf-doctor [command] [options]

        With NO command, runs as an MCP server over stdio (for VS Code / Claude Code / Cursor).

        Commands:
          init [--with-cursor]              Wire the MCP server + steering into the current repo
                                            (.vscode/mcp.json, .mcp.json, instructions, agents).
          doctor [path] [--exclude <s>] [--all|--full] [--json] [--plan] [--fail-on A|B|C|F] [--baseline <file>]
                                            A/B/C/F health report. --all = every finding (grouped),
                                            not just the top 3; --json = machine-readable findings;
                                            --plan = ordered, checkboxed remediation plan;
                                            --plan --json = structured remediation manifest (for an automated fix loop);
                                            --exclude <substr> = skip files whose repo-relative path
                                              contains <substr> (repeatable; substring match, NOT a glob);
                                            --fail-on = exit 3 if the grade is at/below the given letter (CI gate);
                                            --baseline <file> = ignore findings already in an earlier
                                              `doctor --all --json` output (gate new findings only).
          autofix-all [path] [--apply] [--json]
                                            Deterministic Roslyn fixes. Preview only by default
                                            (no writes); --apply = actually write the changes;
                                            --json = machine-readable output.
          new agent <Name>                  Scaffold a MAF agent + smoke test.
          new executor <Name> [In] [Out]    Scaffold a workflow executor + smoke test.
          migrate-scan [path] [--json]      Inventory Semantic Kernel usage and tag each construct
                                            by migration strategy (bridgeable / rewrite / re-architect)
                                            + a complexity verdict. (--source semantic-kernel)
          badge [path]                      Emit a shields.io health-badge JSON payload.
          verify-registry                   Validate the obsolete-API registry (CI gate).
          registry-extract <package> <old-package-version> <new-package-version>
              [--diff-file <path>] [--release-version <X.Y.Z>] [--id-scope <UPPER-HYPHEN>]
                                            Extract registry entries (CI helper). A captured UTF-8
                                            diff avoids rerunning dotnet-inspect; release version
                                            and ID scope separate package evidence from release IDs.
          --version, -v                     Print the installed version.
          --help, -h                        Show this help.

        Exit codes: 0 success · 1 invalid path / failed run · 2 usage error (bad flag or value)
                    · 3 doctor --fail-on gate not met (grade too low or scan incomplete).

        Docs: https://github.com/joslat/maf-doctor
        """);
    Environment.Exit(0);
    return;
}
if (args.Length > 0 && args[0] == "init")
{
    var exitCode = await InitCommand.RunAsync(args);
    Environment.Exit(exitCode);
    return; // unreachable; satisfies compiler
}
if (args.Length >= 1 && args[0] == "new")
{
    // Round-4 review fixup — this used to require `args.Length >= 2`, so a
    // bare `maf-doctor new` (no kind) fell through every check below and
    // reached MCP-server startup instead of NewCommand's own usage message.
    // NewCommand.Run already handles `args.Length < 3` (covers both "new"
    // alone and "new agent" with no name) by printing usage and returning 1.
    var exitCode = NewCommand.Run(args);
    Environment.Exit(exitCode);
    return;
}

// #148: PathGuard now requires repoPath to be absolute (an MCP caller's
// relative path resolves against the wrong cwd). A human typing a relative
// path at their own shell is fine and expected — resolve it here so CLI
// usage like `maf-doctor doctor .` keeps working against every command below
// that ultimately hits PathGuard (doctor, badge, autofix-all, migrate-scan).
static string ResolveCliPath(string? parsedPath)
    => Path.GetFullPath(parsedPath ?? Directory.GetCurrentDirectory());

// REP-09: `doctor --fail-on <grade>` — fail (exit 3) when the graded result is at or
// below the threshold, or when the scan was incomplete. Grade ranking A(best) → F(worst).
static bool GradeFailsGate(char grade, bool scanIncomplete, string failOn)
{
    if (scanIncomplete) return true; // a partial scan cannot be trusted to pass a gate
    static int Rank(char g) => char.ToUpperInvariant(g) switch { 'A' => 0, 'B' => 1, 'C' => 2, _ => 3 };
    return Rank(grade) >= Rank(failOn[0]);
}

if (args.Length >= 1 && args[0] == "doctor")
{
    // Usage: maf-doctor doctor [path] [--exclude <substr>]... [--all|--full] [--json]
    // Parsing lives in DoctorCli.Parse (extracted so it's unit-testable without a
    // process spawn). `--exclude` skips files by repo-relative substring (drift
    // detector scopes to product code); `--all`/`--full` lists every finding
    // (grouped); `--json` emits machine-readable output.
    var (parsedPath, format, excludes, full, failOn, parseError, baselinePath) = MafDoctor.Commands.DoctorCli.Parse(args);
    if (parseError is { } dpe)
    {
        // WM-04 / REP-05: unknown flag, extra positional, or a bad --exclude/--fail-on value.
        Console.Error.WriteLine($"doctor: {dpe}");
        Console.Error.WriteLine("Run 'maf-doctor --help' for usage.");
        Environment.Exit(2);
        return;
    }
    IReadOnlySet<string>? baseline = null;
    if (baselinePath is not null)
    {
        var (fingerprints, baselineError) = MafDoctor.Commands.DoctorBaseline.Load(baselinePath);
        if (baselineError is not null)
        {
            Console.Error.WriteLine($"doctor: {baselineError}");
            Environment.Exit(2);
            return;
        }
        baseline = fingerprints;
    }
    var path = ResolveCliPath(parsedPath);
    var report = new MafDoctor.Tools.DoctorTool().Run(path, format, excludes, full, baseline, out var summary);
    // `summary` is null ONLY when PathGuard rejected the path (no scan ran).
    var pathValid = summary is not null;
    if (pathValid || format is "json" or "plan-json")
        Console.WriteLine(report);        // success, or JSON error → stdout (machine channel)
    else
        Console.Error.WriteLine(report);  // REP-08: human-format error → stderr
    // REP-09: opt-in CI gate — exit 3 when the grade is at-or-below the threshold, or
    // when the scan was incomplete (a partial scan must not silently pass a gate).
    if (pathValid && failOn is not null && GradeFailsGate(summary!.Grade, summary.ScanIncomplete, failOn))
    {
        Console.Error.WriteLine($"doctor: grade {summary.Grade}{(summary.ScanIncomplete ? " (scan incomplete)" : "")} does not pass --fail-on {failOn}");
        Environment.Exit(3);
        return;
    }
    Environment.Exit(pathValid ? 0 : 1);
    return;
}
if (args.Length >= 1 && args[0] == "badge")
{
    // Phase W.13 — emit a shields.io endpoint-badge JSON payload. Consumers
    // host the JSON output (X.4) and reference it from shields.io URLs like
    // https://img.shields.io/endpoint?url=<your-hosted-json>.
    // Resolved via ResolveCliPath too: BadgeCommand.Build runs through
    // DoctorTool.MafDoctor, which hits the same PathGuard absolute-path
    // requirement as every other repoPath-taking tool (#148 finding 1).
    var path = ResolveCliPath(args.Length >= 2 ? args[1] : null);
    var badge = MafDoctor.Commands.BadgeCommand.Build(path);
    Console.WriteLine(badge);
    // Round-4 review fixup — same exit-code gap as `doctor` above. BadgeCommand.Build
    // runs through DoctorTool.MafDoctor, whose sole failure source is the same
    // PathGuard check, so an invalid path previously still exited 0 with a "?"-grade
    // badge instead of signaling failure.
    Environment.Exit(PathGuard.ValidateRepoPath(path) is null ? 0 : 1);
    return;
}
if (args.Length >= 1 && args[0] == "autofix-all")
{
    // Phase W bonus + #8 enabler — surface MafAutoFixAll on the CLI so the
    // migration-cast.tape can drive the auto-fix flow without going through
    // Copilot Chat. Usage: `maf-doctor autofix-all [path] [--apply] [--json]`.
    // F-11 — preview-only unless --apply is given (--dry-run remains a no-op
    // alias for the default). Default output is human-readable; `--json`
    // emits the machine-readable (MCP-tool) shape. Parsing lives in
    // AutoFixCli so it's unit-testable.
    var (parsedPath, json, dryRun) = MafDoctor.Commands.AutoFixCli.Parse(args);
    var path = ResolveCliPath(parsedPath);
    var tool = new MafDoctor.Tools.AutoFixTool();
    // Round-4 review fixup — this unconditionally exited 0 below, even on the
    // `error is not null` branch that already prints "❌ {error}" — the exit
    // code contradicted the tool's own visible error indicator.
    // Compute the report ONCE, then render it as JSON or human text. Success is
    // derived from that same report — a top-level error (bad path/specificFile)
    // OR, on an actual apply, any per-file failure (WM-21) that used to be
    // swallowed. Dry-run per-file errors are shown but don't fail the preview.
    var report = tool.RunAll(path, specificFile: null, dryRun: dryRun, out var error);
    if (report is null)
    {
        // REP-08: JSON error → stdout (parsers read the machine channel); human error
        // → stderr. On the error path RunAll returned null fast (bad path/specificFile);
        // re-running MafAutoFixAll for --json re-fails the same way, cheaply.
        if (json) Console.WriteLine(tool.MafAutoFixAll(path, dryRun: dryRun));
        else Console.Error.WriteLine($"❌ {error}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine(json
        ? MafDoctor.Tools.AutoFixTool.SerializeReport(report)
        : MafDoctor.Commands.AutoFixCli.Format(report));

    var applyErrors = !dryRun && report.Errors is { Count: > 0 };
    Environment.Exit(applyErrors ? 1 : 0);
    return;
}
if (args.Length >= 1 && args[0] == "migrate-scan")
{
    // Cross-framework migration — Phase 1. Inventory Semantic Kernel usage and tag
    // each construct by migration strategy (bridgeable / rewrite / re-architect).
    // Usage: `maf-doctor migrate-scan [path] [--source semantic-kernel] [--json]`.
    // Parsing lives in MigrateScanCli so the flag handling is unit-testable and so the
    // `--source` value is never mistaken for the path (in either `--source v` form).
    var (parsedPath, source, json) = MafDoctor.Commands.MigrateScanCli.Parse(args);
    var path = ResolveCliPath(parsedPath);
    // --source is currently always semantic-kernel (AutoGen is a later phase); validate
    // the flag so the surface is forward-compatible and an unknown source fails loudly.
    if (!MafDoctor.Commands.MigrateScanCli.IsSupportedSource(source))
    {
        Console.Error.WriteLine($"Only --source semantic-kernel (alias sk) is supported today (got '{source}').");
        Environment.Exit(2);
        return;
    }
    var fmt = json ? "json" : "markdown";
    var mReport = new MafDoctor.Tools.SemanticKernelDetectorTool().MafDetectSourceFramework(path, fmt);
    var mValid = PathGuard.ValidateRepoPath(path) is null;
    // REP-08: markdown (human) errors → stderr, matching doctor/autofix-all; the JSON
    // shape stays on stdout for parsers. (Its sole failure source is the same PathGuard check.)
    if (mValid || fmt == "json") Console.WriteLine(mReport);
    else Console.Error.WriteLine(mReport);
    Environment.Exit(mValid ? 0 : 1);
    return;
}
if (args.Length >= 1 && args[0] == "verify-registry")
{
    // #5 polish (2026-05-13) — verification gate for the AI-fill flow. The
    // PR-triggered workflow maf-ai-fill-verify.yml runs this command against
    // the AI-filled registry.yaml. Exit code 1 blocks the PR's auto-merge.
    var exitCode = MafDoctor.Commands.VerifyRegistryCommand.Run();
    Environment.Exit(exitCode);
    return;
}
if (args.Length >= 1 && args[0] == "registry-extract")
{
    var exitCode = RegistryExtractCommand.Run(args);
    Environment.Exit(exitCode);
    return;
}

// Round-4 review fixup — confirmed via real execution: a non-empty but
// unrecognized args[0] (a typo, e.g. `doctro`) previously fell through every
// check above all the way to MCP-server startup below. With stdin left open
// (any normal interactive terminal), the process just sits there — no
// stdout, no error, indistinguishable from a hang. The doc comment at the
// top of this file documents "no command = MCP server" as the ONLY
// intentional fall-through case; args.Length == 0 is the only one that
// should reach WorkspacePolicy.EnableMcpMode() below.
if (args.Length > 0)
{
    Console.Error.WriteLine($"Unknown command: '{args[0]}'.");
    Console.Error.WriteLine("Run 'maf-doctor --help' for usage.");
    Environment.Exit(1);
    return;
}

// F-04 — every branch above this point is a CLI subcommand and returns before
// reaching here. Only the MCP-server path below falls through, so this is the
// one and only place WorkspacePolicy's containment check turns on; CLI usage
// (a human directly invoking `maf-doctor doctor <any path>`) is unaffected.
WorkspacePolicy.EnableMcpMode();

// All logs must go to stderr — stdout is reserved for the MCP JSON-RPC protocol.
// CreateEmptyApplicationBuilder, not CreateApplicationBuilder: the server reads no
// appsettings/host config, and the defaults register a reloadOnChange file watcher
// on the content root (= cwd). MCP hosts spawn stdio servers with cwd = the user's
// home directory, where that watcher (recursive FSEvents on macOS) allocates
// unboundedly under normal filesystem activity — GBs while idle (#146).
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Load the registry once at startup and register as a singleton shared by all tools.
builder.Services
    .AddSingleton<RegistryService>()
    .AddHostedService<UpdateNoticeHostedService>()
    .AddMcpServer(options =>
    {
        // Advertise the server's identity + icon (MCP 2025-11 `icons` on the
        // Implementation). Clients that support it render the icon; older clients
        // ignore it. Source points at the asset on `main` so it tracks the brand.
        options.ServerInfo = new Implementation
        {
            Name = "maf-doctor",
            Title = "MAF Doctor",
            Version = MafDoctor.ToolVersionInfo.Current, // REP-20: single source of truth
            WebsiteUrl = "https://github.com/joslat/maf-doctor",
            Icons =
            [
                new Icon
                {
                    Source = "https://raw.githubusercontent.com/joslat/maf-doctor/main/assets/MAFDoctorIcon.png",
                    MimeType = "image/png",
                    Sizes = ["any"],
                },
            ],
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly)
    .WithResourcesFromAssembly(typeof(Program).Assembly)
    .WithPromptsFromAssembly(typeof(Program).Assembly);

await builder.Build().RunAsync();
