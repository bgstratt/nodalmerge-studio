using System.Text.Json;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.AgentRuntime;

internal static class ActivityLabeler
{
    public static string Describe(string toolName, JsonElement input)
    {
        string? Field(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        return toolName switch
        {
            McpToolNames.WorkspaceRead   => Field("path") is { } p ? $"Reading {p}" : "Reading file",
            McpToolNames.WorkspaceWrite  => Field("path") is { } p ? $"Writing {p}" : "Writing file",
            McpToolNames.WorkspaceDelete => Field("path") is { } p ? $"Deleting {p}" : "Deleting file",
            McpToolNames.WorkspaceList   => "Listing files",
            McpToolNames.WorkspaceDiff   => "Reviewing changes",
            McpToolNames.WorkspaceExists => "Checking file",
            McpToolNames.WorkspaceSummary => "Reading workspace summary",
            McpToolNames.WorkspaceStatus => "Reading workspace status",
            McpToolNames.DocFetch => "Fetching external documentation",

            McpToolNames.TaskUpdate => "Updating task status",
            McpToolNames.TaskCreate => "Creating task",
            McpToolNames.TaskList   => "Listing tasks",
            McpToolNames.TaskAssign => "Assigning task",

            McpToolNames.WorkUnitGet    => "Reading work unit",
            McpToolNames.WorkUnitCreate => "Creating work unit",
            McpToolNames.WorkUnitUpdate => "Updating work unit",
            McpToolNames.WorkUnitList   => "Listing work units",

            McpToolNames.MergePropose  => "Proposing merge",
            McpToolNames.MergeValidate => "Validating merge",
            McpToolNames.MergeApply    => "Applying merge",
            McpToolNames.MergeReview   => "Reviewing merge",

            McpToolNames.SchedulerEnqueue => "Enqueuing worker",
            McpToolNames.ClarificationRequest => "Requesting clarification",
            McpToolNames.AgentSpawn       => "Spawning worker",
            McpToolNames.AgentStatus      => "Checking agent status",
            McpToolNames.AgentStop        => "Stopping agent",

            McpToolNames.ArtifactRecord => "Recording finding",
            McpToolNames.ArtifactQuery  => "Searching artifacts",
            McpToolNames.ArtifactList   => "Listing artifacts",

            McpToolNames.ProjectionGet => "Reading workspace state",
            McpToolNames.IntentRecord  => "Declaring intent",

            _ => $"Calling {toolName}",
        };
    }
}
