using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class EvidenceTools(IWorkspaceExecutionCommandService execCommands, IWorkUnitService workUnits)
{
    [McpServerTool(Name = McpToolNames.EvidenceAttach), Description("Attach build/test evidence to a work unit.")]
    public async Task<string> AttachAsync(
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (wu is null)
            return McpJson.Error(McpToolNames.EvidenceAttach, $"Work unit '{workUnitId}' not found.");

        var execResult = await execCommands.GetLatestAsync(wu.BranchId, cancellationToken).ConfigureAwait(false);
        if (execResult is null)
            return McpJson.Ok(new { evidence = Array.Empty<object>(), message = "No execution results available." });

        var evidence = new List<object>();
        foreach (var build in execResult.Builds)
        {
            evidence.Add(new
            {
                evidenceId = $"ev-bld-{Guid.NewGuid():N}",
                kind = "Build",
                summary = build.Success
                    ? $"{build.BuildSystem ?? "build"}: passed"
                    : $"{build.BuildSystem ?? "build"}: failed (exit {build.ExitCode})",
                attachedAt = DateTimeOffset.UtcNow
            });
        }
        foreach (var test in execResult.Tests)
        {
            evidence.Add(new
            {
                evidenceId = $"ev-tst-{Guid.NewGuid():N}",
                kind = "Test",
                summary = test.Success
                    ? $"{test.BuildSystem ?? "test"}: passed ({test.Passed}/{test.TotalTests})"
                    : $"{test.BuildSystem ?? "test"}: failed ({test.Passed}/{test.TotalTests})",
                attachedAt = DateTimeOffset.UtcNow
            });
        }

        return McpJson.Ok(new { evidence, allSucceeded = execResult.AllSucceeded });
    }

    [McpServerTool(Name = McpToolNames.EvidenceList), Description("List evidence entries for a work unit.")]
    public async Task<string> ListAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (wu is null)
            return McpJson.Error(McpToolNames.EvidenceList, $"Work unit '{workUnitId}' not found.");

        var execResult = await execCommands.GetLatestAsync(wu.BranchId, cancellationToken).ConfigureAwait(false);
        if (execResult is null)
            return McpJson.Ok(new { evidence = Array.Empty<object>() });

        var evidence = new List<object>();
        foreach (var build in execResult.Builds)
        {
            evidence.Add(new
            {
                evidenceId = $"ev-bld-{Guid.NewGuid():N}",
                kind = "Build",
                buildSystem = build.BuildSystem,
                command = build.Command,
                success = build.Success,
                exitCode = build.ExitCode,
                attachedAt = execResult.ExecutedAt
            });
        }
        foreach (var test in execResult.Tests)
        {
            evidence.Add(new
            {
                evidenceId = $"ev-tst-{Guid.NewGuid():N}",
                kind = "Test",
                buildSystem = test.BuildSystem,
                command = test.Command,
                success = test.Success,
                totalTests = test.TotalTests,
                passed = test.Passed,
                failed = test.Failed,
                skipped = test.Skipped,
                attachedAt = execResult.ExecutedAt
            });
        }

        return McpJson.Ok(new { evidence });
    }
}