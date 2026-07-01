namespace NodalMerge.Studio.Contracts.Domain;

public sealed record BuildResult(
    bool Success,
    int ExitCode,
    string StdOut,
    string StdErr,
    string? BuildSystem,    // "dotnet", "cargo", "npm", "go", "pytest", "make", null if explicit command
    string Command,          // the actual command that ran
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Truncated = false,  // true if StdOut or StdErr exceeded MaxOutputBytes and was truncated
    string ProjectRoot = "", // Phase 9b — which WorkspaceProfile root this ran in; "" = branch root / explicit command override
    bool Running = false,    // Phase 9c — true if this is a long-running process still alive (dev server), not a completed one-shot build
    int? Pid = null);        // Phase 9c — process id when Running; lets callers target StopAsync

public sealed record TestResult(
    bool Success,
    int ExitCode,
    int TotalTests,
    int Passed,
    int Failed,
    int Skipped,
    string StdOut,
    string? BuildSystem,
    string Command,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Truncated = false, // true if StdOut exceeded MaxOutputBytes and was truncated
    string ProjectRoot = "");

public sealed record LintResult(
    string RuleId,
    string Severity,       // "error", "warning"
    string File,
    int? Line,
    string Message);

public sealed record BranchExecutionResult(
    string BranchId,
    IReadOnlyList<BuildResult> Builds,
    IReadOnlyList<TestResult> Tests,
    IReadOnlyList<LintResult> LintResults,
    bool AllSucceeded,
    DateTimeOffset ExecutedAt,
    string? NodeId = null);  // persisted ExecutionResultV1 node entity ID; used for output download

public sealed record WorkspaceExecutionRequest(
    bool Build = false,
    bool Test = false,
    bool Lint = false,
    string? BuildCommand = null,     // null = auto-detect or skip
    string? TestCommand = null,
    string? LintCommand = null,
    bool AllowAutoDetect = true,
    int TimeoutSeconds = 300,
    Dictionary<string, string>? EnvironmentVariables = null,
    string? RootPath = null);        // Phase 9f — limit auto-detected build/test to one WorkspaceProfile root (ProjectRoot.RelativePath); null/omitted = all roots. Ignored when BuildCommand/TestCommand is explicit.

/// <summary>
/// Lightweight execution summary for projection payloads — avoids embedding full BuildResult/TestResult output.
/// </summary>
public sealed record WorkspaceExecutionSummary(
    bool AllSucceeded,
    IReadOnlyList<string> BuildSystems,
    string? TestSummary,
    DateTimeOffset? ExecutedAt = null);

/// <summary>
/// Cached output from a persisted execution result, returned by the output-download endpoint.
/// </summary>
public sealed record ExecutionOutput(
    string BranchId,
    string ResultId,
    IReadOnlyList<ExecutionOutputEntry> Entries,
    DateTimeOffset ExecutedAt);

public sealed record ExecutionOutputEntry(
    string Kind,          // "build" or "test"
    string? BuildSystem,
    string Command,
    string StdOut,
    string StdErr,
    bool Truncated);
