using System.Text.Json;

namespace MafDoctor.Commands;

/// <summary>
/// F-03: loads a <c>doctor --baseline</c> file, which is the output of an earlier
/// <c>maf-doctor doctor --all --json</c>. Its <c>top_fixes[].fingerprint</c>
/// values identify findings that already existed, so a CI gate fails only on
/// new ones. Fingerprints are drift-stable (rule | file | line text), so a
/// finding that merely moves lines stays baselined.
/// </summary>
internal static class DoctorBaseline
{
    internal const long MaxBytes = 16 * 1024 * 1024;

    public static (IReadOnlySet<string>? Fingerprints, string? Error) Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return (null, $"--baseline file not found: {path}");
            if (info.Length > MaxBytes)
                return (null, $"--baseline file is larger than {MaxBytes / (1024 * 1024)} MB: {path}");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("top_fixes", out var fixes) || fixes.ValueKind != JsonValueKind.Array)
                return (null, "--baseline must be the output of `maf-doctor doctor --all --json` (no top_fixes array)");

            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fix in fixes.EnumerateArray())
            {
                if (fix.ValueKind == JsonValueKind.Object
                    && fix.TryGetProperty("fingerprint", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && IsFingerprint(value.GetString()))
                {
                    fingerprints.Add(value.GetString()!);
                }
            }
            return (fingerprints, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"--baseline could not be read: {ex.Message}");
        }
    }

    /// <summary>12 lowercase hex characters, the shape DoctorTool emits.</summary>
    internal static bool IsFingerprint(string? value) =>
        value is { Length: 12 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
