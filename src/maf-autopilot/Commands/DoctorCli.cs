namespace MafDoctor.Commands;

/// <summary>
/// Argument parsing for the <c>doctor</c> CLI subcommand, extracted from the
/// Program.cs top-level statements so it can be unit-tested without spawning a
/// process (the analysis itself lives in <c>DoctorTool</c>).
///
/// Grammar: <c>doctor [path] [--exclude &lt;substr&gt;]... [--all|--full] [--json|--plan] [--fail-on G] [--baseline &lt;file&gt;]</c>
/// <list type="bullet">
///   <item>The first non-flag token is the path (default: current directory at
///         the call site — left null here so the caller decides the default).</item>
///   <item><c>--exclude &lt;substr&gt;</c> is repeatable; each skips files whose
///         repo-relative path contains the substring (used by the drift detector
///         to scan product code only).</item>
///   <item><c>--all</c> / <c>--full</c> lists every finding (grouped) vs the top 3.</item>
///   <item><c>--json</c> emits the flat machine-readable findings; <c>--plan</c> emits the
///         human checklist; <c>--plan --json</c> together emit the structured remediation
///         <em>manifest</em> (phased + per-finding confidence) for an automated fix loop.</item>
/// </list>
/// </summary>
internal static class DoctorCli
{
    public static (string? Path, string Format, List<string> Excludes, bool Full, string? FailOn, string? Error, string? Baseline) Parse(string[] args)
    {
        var excludes = new List<string>();
        string? path = null;
        var full = false;
        var json = false;
        var plan = false;
        string? failOn = null;
        string? baseline = null;
        var unknown = new List<string>();
        string? error = null;

        // args[0] is "doctor"; options start at index 1.
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            // REP-05: `--exclude` requires a value and must never swallow a following
            // flag (`doctor --exclude --json` used to silently disable --json).
            if (a == "--exclude")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) excludes.Add(args[++i]);
                else error ??= "--exclude requires a value (e.g. `--exclude tests`)";
                continue;
            }
            // REP-09: opt-in CI gate — exit 3 when the grade is at-or-below <grade>.
            if (a == "--fail-on")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) failOn = args[++i];
                else error ??= "--fail-on requires a grade (A|B|C|F)";
                continue;
            }
            // F-03: suppress findings an earlier `doctor --all --json` already reported.
            if (a == "--baseline")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) baseline = args[++i];
                else error ??= "--baseline requires a file (the output of `doctor --all --json`)";
                continue;
            }
            if (a is "--all" or "--full") { full = true; continue; }
            if (a is "--json") { json = true; continue; }
            if (a is "--plan") { plan = true; continue; }
            // WM-04: reject unknown flags and extra positionals loudly (exit 2 in
            // Program.cs) instead of silently ignoring them.
            if (a.StartsWith("--", StringComparison.Ordinal)) { unknown.Add(a); continue; }
            if (path is null) { path = a; continue; }
            unknown.Add(a); // a second positional is not expected
        }

        if (error is null && unknown.Count > 0)
            error = "unknown option(s) / extra argument(s): " + string.Join(", ", unknown);

        if (error is null && failOn is not null)
        {
            failOn = failOn.ToUpperInvariant();
            if (failOn is not ("A" or "B" or "C" or "F"))
                error = $"--fail-on must be one of A, B, C, F (got '{failOn}')";
        }

        // `--plan --json` (either order) = the machine-readable remediation MANIFEST
        // (a structured plan an automated remediation loop can iterate). `--plan` alone
        // is the human checklist; `--json` alone is the flat findings array.
        var format = (plan, json) switch
        {
            (true, true) => "plan-json",
            (true, false) => "plan",
            (false, true) => "json",
            _ => "markdown",
        };

        return (path, format, excludes, full, failOn, error, baseline);
    }
}
