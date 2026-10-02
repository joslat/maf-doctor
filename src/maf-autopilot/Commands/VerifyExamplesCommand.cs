using System.Text.Json;
using System.Text.RegularExpressions;
using MafDoctor.Data;

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
///   <c>example_before</c> compiled against the NEW side must produce it.</item>
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

        var entries = new RegistryService().AllEntries
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
            string? claim = null;
            var expected = (entry.CsWarning ?? "").Trim();
            if (CompilerDiagnostic.IsMatch(expected) && before.Status == "ok")
            {
                var onNew = newCompiler.Compile(entry.ExampleBefore, entry.Type, before.Prelude);
                claim = Observed(expected, onNew.DiagnosticIds)
                    ? "ok"
                    : $"cs_warning is {expected}, but example_before on the new packages gives "
                      + (onNew.DiagnosticIds.Count == 0 ? "no diagnostics" : string.Join(", ", onNew.DiagnosticIds.Order(StringComparer.Ordinal)));
            }
            if (show is not null)
            {
                // --show: the generated sources and every diagnostic, to see why an entry fails.
                var claimSource = claim is null ? null : newCompiler.Compile(entry.ExampleBefore, entry.Type, before.Prelude);
                foreach (var (label, result) in new[] { ("example_before vs OLD", before), ("example_after vs NEW", after), ("example_before vs NEW (claim)", claimSource) })
                {
                    if (result is null) continue;
                    Console.WriteLine($"===== {entry.Id}: {label} → {result.Status}");
                    Console.WriteLine(result.Source);
                    foreach (var d in result.AllDiagnostics ?? []) Console.WriteLine($"  {d}");
                }
            }
            return new EntryResult(entry.Id, before, after, claim);
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
                }),
                failed,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Compiler oracle: {results.Count} entries for MAF {version}; example_before against the old packages, example_after against the new ones.");
            foreach (var r in results)
            {
                var claimText = r.Claim is null ? "" : r.Claim == "ok" ? $" · {r.Claim} claim" : " · claim MISMATCH";
                Console.WriteLine($"  {Mark(r)} {r.Id}: before {r.Before.Status} · after {r.After.Status}{claimText}");
                foreach (var (side, result) in new[] { ("before", r.Before), ("after", r.After) })
                {
                    foreach (var error in result.Errors.Take(5)) Console.WriteLine($"      {side}: {error}");
                    if (result.Status == "unresolved") Console.WriteLine($"      {side} (unchecked): {string.Join("; ", result.Unresolved.Take(3))}");
                }
                if (r.Claim is not null && r.Claim != "ok") Console.WriteLine($"      claim: {r.Claim}");
            }
            var ok = results.Count(r => !IsFailure(r) && r.Before.Status == "ok" && r.After.Status == "ok");
            Console.WriteLine($"Summary: {ok} fully verified, {failed} with errors, {results.Count - ok - failed} partly unchecked (placeholders, user types, or no code).");
        }
        return failed > 0 ? 1 : 0;
    }

    private sealed record EntryResult(string Id, ExampleResult Before, ExampleResult After, string? Claim);

    private static object Describe(ExampleResult r) => new { status = r.Status, errors = r.Errors, unresolved = r.Unresolved, inferred = r.Inferred };

    // Diagnostics that report the same break in different words: a conversion
    // with or without an explicit cast available; a member missing on an
    // instance, on a type, or for an extension receiver; a missing type or name.
    private static readonly string[][] SameBreak =
    [
        ["CS0029", "CS0266"],
        ["CS1061", "CS1929", "CS0117"],
        ["CS0246", "CS0234", "CS0103"],
    ];

    private static bool Observed(string expected, IReadOnlySet<string> ids) =>
        ids.Contains(expected) || SameBreak.Any(group => group.Contains(expected) && group.Any(ids.Contains));

    private static bool IsFailure(EntryResult r) =>
        r.Before.Status == "fail" || r.After.Status == "fail" || (r.Claim is not null && r.Claim != "ok");

    private static string Mark(EntryResult r) =>
        IsFailure(r) ? "✗" : r.Before.Status == "ok" && r.After.Status == "ok" ? "✓" : "·";

    private static List<string> ReadPaths(string file) =>
        File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
