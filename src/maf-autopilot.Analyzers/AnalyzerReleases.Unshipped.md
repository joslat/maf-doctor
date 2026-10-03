; Unshipped analyzer release.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category      | Severity | Notes
--------|---------------|----------|------------------------------------------------------------------
MAF001  | MAF.Workflow  | Warning  | Fan-out handler must return ValueTask<T>
MAF002  | MAF.Security  | Warning  | Avoid DefaultAzureCredential in production code
MAF003  | MAF.Security  | Warning  | EnableSensitiveData = true outside test code
