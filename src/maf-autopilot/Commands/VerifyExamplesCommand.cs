using System.Text.Json;
using System.Text.RegularExpressions;
using MafDoctor.Data;
using MafDoctor.Tools;

namespace MafDoctor.Commands;

/// <summary>
/// CLI: <c>maf-doctor verify-examples --version X.Y.Z --old-refs FILE --new-refs FILE [--json]</c>.
///
/// Compiler oracle for registry entries (ROADMAP Q-02). For every entry
/// introduced in <c>--version</c>:
/// <list type="bullet">
///   <item><c>example_before</c> must compile against the OLD side of the train;</item>
///   <item><c>example_after</c> must compile against the NEW side;</item>
///   <item>when <c>cs_warning</c> names a compiler diagnostic (<c>CS0618</c>, <c>CS1061</c>, ...),
///   <c>example_before</c> compiled against the NEW side must produce it;</item>
///   <item>link check (tool oracle): <c>MafRunCs0618Hunt</c>, given those diagnostics as a
///   build reports them, must link one of them to this entry. That is how a user's
///   upgrade errors reach the entry's fix.</item>
/// </list>
/// The reference files list one assembly path per line; the Python wrapper
/// <c>.github/scripts/verify_registry_examples.py</c> restores the exact packages
/// recorded in the train lock and writes them. The registry is the one
/// <see cref="RegistryService"/> loads (set <c>MAF_REGISTRY_PATH</c> to check a PR).
///
/// Exit codes: 0 every checkable example holds; 1 a compile failure or a claim
/// mismatch; 2 usage error.
/// </summary>
public static class VerifyExamplesCommand
{
    private static readonly Regex CompilerDiagnostic = new(@"^CS\d{4}$", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static int Run(string[] args)
    {
        string? version = null, oldRefs = null, newRefs = null, show = null;
        var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version" when i + 1 < args.Length: version = args[++i]; break;
                case "--old-refs" when i + 1 < args.Length: oldRefs = args[++i]; break;
                case "--new-refs" when i + 1 < args.Length: newRefs = args[++i]; break;
                case "--json": json = true; break;
                case "--show" when i + 1 < args.Length: show = args[++i]; break;
                default:
                    Console.Error.WriteLine($"verify-examples: unknown or incomplete argument '{args[i]}'.");
                    return 2;
            }
        }
        if (version is null || oldRefs is null || newRefs is null || !File.Exists(oldRefs) || !File.Exists(newRefs))
        {
            Console.Error.WriteLine("usage: maf-doctor verify-examples --version X.Y.Z --old-refs FILE --new-refs FILE [--json]");
            return 2;
        }

        var registry = new RegistryService();
        var entries = registry.AllEntries
            .Where(e => e.VersionIntroduced == version && e.AppliesToCodebases != "pre-1.0.0")
            .Where(e => show is null || e.Id == show)
            .ToList();
        var oldPaths = ReadPaths(oldRefs);
        var newPaths = ReadPaths(newRefs);
        var oldProbe = new ExampleCompiler(oldPaths);
        var newCompiler = new ExampleCompiler(newPaths, oldProbe.TypeNames);
        var oldCompiler = new ExampleCompiler(oldPaths, newCompiler.TypeNames);

        var results = entries.Select(entry =>
        {
            // A package the train lock does not hold on a side (tracked later, or
            // externalized) cannot be compiled against there.
            ExampleResult Side(ExampleCompiler compiler, string code, string side) =>
                string.IsNullOrWhiteSpace(entry.Package) || compiler.AssemblyNames.Contains(entry.Package)
                    ? compiler.Compile(code, entry.Type)
                    : new("skipped", [], [$"{entry.Package} is not in the {side} train lock"], [], new HashSet<string>(), []);
            var before = Side(oldCompiler, entry.ExampleBefore, "old");
            var after = Side(newCompiler, entry.ExampleAfter, "new");
            string? claim = null, link = null;
            var expected = (entry.CsWarning ?? "").Trim();
            // A claimed "type not found" is about the namespaces the old code imports:
            // when the snippet states them, do not import the new namespaces for it.
            var usingsOnly = expected is "CS0246" or "CS0234" && ExampleCompiler.DeclaresUsings(entry.ExampleBefore);
            if (CompilerDiagnostic.IsMatch(expected) && before.Status == "ok")
            {
                var onNew = newCompiler.Compile(entry.ExampleBefore, entry.Type, before.Prelude, usingsOnly);
                claim = Observed(expected, onNew.DiagnosticIds)
                    ? "ok"
                    : $"cs_warning is {expected}, but example_before on the new packages gives "
                      + (onNew.DiagnosticIds.Count == 0 ? "no diagnostics" : string.Join(", ", onNew.DiagnosticIds.Order(StringComparer.Ordinal)));
                if (claim == "ok") link = LinkCheck(entry, onNew.Reported ?? [], registry);
            }
            if (show is not null)
            {
                // --show: the generated sources and every diagnostic, to see why an entry fails.
                var claimSource = claim is null ? null : newCompiler.Compile(entry.ExampleBefore, entry.Type, before.Prelude, usingsOnly);
                foreach (var (label, result) in new[] { ("example_before vs OLD", before), ("example_after vs NEW", after), ("example_before vs NEW (claim)", claimSource) })
                {
                    if (result is null) continue;
                    Console.WriteLine($"===== {entry.Id}: {label} → {result.Status}");
                    Console.WriteLine(result.Source);
                    foreach (var d in result.AllDiagnostics ?? []) Console.WriteLine($"  {d}");
                }
            }
            return new EntryResult(entry.Id, before, after, claim, link);
        }).ToList();

        var failed = results.Count(IsFailure);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                version,
                entries = results.Select(r => new
                {
                    id = r.Id,
                    before = Describe(r.Before),
                    after = Describe(r.After),
                    claim = r.Claim,
                    link = r.Link,
                }),
                failed,
                links_missing = results.Count(r => r.Link is not null && !Linked(r)),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Compiler oracle: {results.Count} entries for MAF {version}; example_before against the old packages, example_after against the new ones.");
            foreach (var r in results)
            {
                var claimText = r.Claim is null ? "" : r.Claim == "ok" ? $" · {r.Claim} claim" : " · claim MISMATCH";
                claimText += r.Link is null ? "" : Linked(r) ? " · hunt links it" : " · LINK MISSING";
                Console.WriteLine($"  {Mark(r)} {r.Id}: before {r.Before.Status} · after {r.After.Status}{claimText}");
                foreach (var (side, result) in new[] { ("before", r.Before), ("after", r.After) })
                {
                    foreach (var error in result.Errors.Take(5)) Console.WriteLine($"      {side}: {error}");
                    if (result.Status == "unresolved") Console.WriteLine($"      {side} (unchecked): {string.Join("; ", result.Unresolved.Take(3))}");
                }
                if (r.Claim is not null && r.Claim != "ok") Console.WriteLine($"      claim: {r.Claim}");
                if (r.Link is not null && r.Link != "ok") Console.WriteLine($"      link: {r.Link}");
            }
            var ok = results.Count(r => !IsFailure(r) && r.Before.Status == "ok" && r.After.Status == "ok");
            var linkMissing = results.Count(r => r.Link is not null && !Linked(r));
            Console.WriteLine($"Summary: {ok} fully verified, {failed} with errors, {results.Count - ok - failed} partly unchecked (placeholders, user types, or no code); "
                + $"hunt links {results.Count(Linked)} of {results.Count(r => r.Link is not null)} claimed diagnostics, {linkMissing} missing (report-only).");
        }
        return failed > 0 ? 1 : 0;
    }

    private sealed record EntryResult(string Id, ExampleResult Before, ExampleResult After, string? Claim, string? Link);

    /// <summary>
    /// Link check (tool oracle, ROADMAP Q-02 part 3): passes the diagnostics old code
    /// gets on the new packages through the same filter and matcher <c>MafRunCs0618Hunt</c>
    /// applies to a real build log. Returns <c>"ok"</c> when one of them links to
    /// <paramref name="entry"/>, otherwise what the hunt does with them instead.
    /// </summary>
    internal static string LinkCheck(
        RegistryEntry entry,
        IReadOnlyList<(string Id, string Severity, string Message, string Line)> reported,
        RegistryService registry)
    {
        var diagnostics = reported
            .Select(d => new BuildDiagnostic("Example.cs", 1, d.Severity, d.Id, d.Message, d.Line))
            .ToList();
        var kept = Cs0618HuntTool.FilterRegistryRelevantDiagnostics(diagnostics, registry.AllEntries);
        var linked = kept
            .Select(d => Cs0618HuntTool.MatchToRegistry(d, registry)?.Id)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (linked.Contains(entry.Id, StringComparer.Ordinal)) return "ok";
        // The message names this entry exactly as well as the one the hunt picked (for
        // example the same member on two types): a build log cannot tell them apart.
        var tie = kept
            .Select(d => (d, best: Cs0618HuntTool.MatchToRegistry(d, registry)))
            .FirstOrDefault(x => x.best is not null
                && Cs0618HuntTool.CorrelationScore(x.d, entry) is > 0 and var score
                && score == Cs0618HuntTool.CorrelationScore(x.d, x.best));
        if (tie.best is not null) return $"ok (the message names {tie.best.Id} equally; the hunt shows that one)";
        if (linked.Count > 0) return $"the hunt links these diagnostics to {string.Join(", ", linked)}, not {entry.Id}";
        return kept.Count > 0
            ? $"the hunt keeps {string.Join(", ", kept.Select(d => d.Code).Distinct())} but links it to no registry entry"
            : $"the hunt drops {string.Join(", ", diagnostics.Select(d => d.Code).Distinct())}: no registry entry with that code names an identifier from the message";
    }

    private static object Describe(ExampleResult r) => new { status = r.Status, errors = r.Errors, unresolved = r.Unresolved, inferred = r.Inferred };

    // The same code groups the hunt accepts (Cs0618HuntTool.SameBreakCodes).
    private static bool Observed(string expected, IReadOnlySet<string> ids) =>
        ids.Any(id => Cs0618HuntTool.CodesMatch(expected, id));

    private static bool Linked(EntryResult r) => r.Link?.StartsWith("ok", StringComparison.Ordinal) == true;

    private static bool IsFailure(EntryResult r) =>
        r.Before.Status == "fail" || r.After.Status == "fail" || (r.Claim is not null && r.Claim != "ok");

    private static string Mark(EntryResult r) =>
        IsFailure(r) ? "✗" : r.Before.Status == "ok" && r.After.Status == "ok" ? "✓" : "·";

    private static List<string> ReadPaths(string file) =>
        File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
