using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class WorkUnitTools(IWorkUnitService workUnits, IOrchestratorService orchestrator, IWorkUnitCommandService workUnitCommands)
{
    [McpServerTool(Name = McpToolNames.WorkUnitCreate), Description("Create a work unit from goal and branch.")]
    public async Task<string> CreateAsync(
        string goal,
        string? branchId = null,
        string? owner = "studio",
        string? successCriteria = null,
        string? repositoryPath = null,
        string? parentWorkUnitId = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? fileScope = null,
        string? repositoryId = null,
        [Description("Read-only pointers into other registered repositories for context (style/examples) — e.g. [{ \"repositoryId\": \"repo-abc\", \"path\": \"src/Foo.cs\" }]. Fetch content on demand via nm_v1_repository_read_file.")] IReadOnlyList<FileReferenceV1>? referenceFiles = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var workUnit = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand(goal, owner ?? "studio", branchId, successCriteria, repositoryPath, parentWorkUnitId, dependsOn, fileScope, RepositoryId: repositoryId, ReferenceFiles: referenceFiles),
                cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { workUnitId = workUnit.WorkUnitId, branchId = workUnit.BranchId });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.WorkUnitCreate, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.WorkUnitGet), Description("Get a work unit by id.")]
    public async Task<string> GetAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var workUnit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return workUnit is null
            ? McpJson.Error(McpToolNames.WorkUnitGet, $"Work unit '{workUnitId}' was not found.")
            : McpJson.Ok(workUnit);
    }

    [McpServerTool(Name = McpToolNames.WorkUnitUpdate), Description("Update work unit status, assignment, or file scope. fileScope amends the existing scope in place (only on non-terminal work units) — it does not fork a sibling the way steering does.")]
    public async Task<string> UpdateAsync(
        string workUnitId,
        string? status = null,
        string? assignedAgent = null,
        IReadOnlyList<string>? fileScope = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (status is not null &&
            Enum.TryParse<WorkUnitStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            var updated = await workUnits.UpdateStatusAsync(workUnitId, parsedStatus, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (assignedAgent is not null)
            {
                await orchestrator.AssignWorkAsync(workUnitId, assignedAgent, cancellationToken).ConfigureAwait(false);
            }

            if (fileScope is not null)
            {
                updated = await workUnits.SetFileScopeAsync(workUnitId, fileScope, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return McpJson.Ok(updated);
        }

        if (assignedAgent is not null || fileScope is not null)
        {
            if (assignedAgent is not null)
            {
                await orchestrator.AssignWorkAsync(workUnitId, assignedAgent, cancellationToken).ConfigureAwait(false);
            }

            if (fileScope is not null)
            {
                await workUnits.SetFileScopeAsync(workUnitId, fileScope, sessionId, cancellationToken).ConfigureAwait(false);
            }

            var workUnit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            return workUnit is null
                ? McpJson.Error(McpToolNames.WorkUnitUpdate, $"Work unit '{workUnitId}' was not found.")
                : McpJson.Ok(workUnit);
        }

        return McpJson.Error(McpToolNames.WorkUnitUpdate, "Provide status, assignedAgent, and/or fileScope.");
    }

    [McpServerTool(Name = McpToolNames.WorkUnitList), Description("List work units, optionally filtered by branch.")]
    public async Task<string> ListAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var items = await workUnits.ListAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { workUnitIds = items.Select(w => w.WorkUnitId).ToList() });
    }

    [McpServerTool(Name = McpToolNames.WorkUnitDependents), Description("List work units that depend on the given work unit (the reverse of its DependsOn list).")]
    public async Task<string> DependentsAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var items = await workUnits.GetDependentsAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { workUnitIds = items.Select(w => w.WorkUnitId).ToList() });
    }
}
