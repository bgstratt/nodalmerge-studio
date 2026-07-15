using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

// plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — the harness-
// scoped tool surface mounted at "/mcp-harness", never at "/mcp" (see ServiceCollectionExtensions'
// own doc comment for why: the registration split that keeps the internal nm_v1_* surface off the
// general MCP endpoint). Deliberately NOT [McpServerToolType]/WithTools<T> — these tools are
// wired into a per-request McpServerOptions.ToolCollection built by
// HarnessMcpToolCollectionFactory, only when the request's path is "/mcp-harness", so they can
// never leak onto the default "/mcp" tool list the SDK's single global AddMcpServer() registration
// otherwise serves to every mount.
//
// Exactly the C.4 confirmed subset: workspace_symbol_definition/_references/_implementation
// (Roslyn semantic nav), doc_fetch (traceable web research), artifact_record/_query (live
// ancestor-knowledge access mid-run), clarification_request (held-open = true mid-turn pause).
// Not the file tools — the harness brings its own (per the plan's own C.4 note).
//
// Work-unit identity comes from the per-run bearer token (Authorization: Bearer <token>), resolved
// via IHarnessMcpTokenService — never from a caller-supplied workUnitId parameter, so a spoofed or
// stale id in the request body can't redirect a call at another work unit's data.
public sealed class HarnessWorkerTools(
    IHarnessMcpTokenService tokens,
    IHttpContextAccessor httpContextAccessor,
    IWorkUnitService workUnits,
    IWorkspaceSemanticNavigationService semanticNavigation,
    IDocFetchCommandService docFetch,
    IArtifactCommandService artifacts,
    IClarificationCommandService clarifications,
    IExecutionEventStream events,
    WorkspaceOptions workspaceOptions)
{
    // Shared by every tool method below — resolves and validates the bearer token before any
    // domain logic runs. Returns a ready-to-return McpJson.Error string when unauthorized so every
    // call site can `if (Authorize() is { } denied) return denied;`-style early-out.
    private HarnessMcpTokenContext? AuthorizeOrNull(string toolName, out string? error)
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        string? token = header;
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header["Bearer ".Length..].Trim();

        var context = tokens.Resolve(token);
        error = context is null
            ? McpJson.Error(toolName, "Missing, invalid, or revoked harness bearer token.")
            : null;
        return context;
    }

    private async Task<string?> ResolveBranchIdOrErrorAsync(string toolName, string workUnitId, CancellationToken ct)
    {
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        return wu?.BranchId;
    }

    [Description("Find symbol definition locations in this run's branch using compiler-backed semantic navigation.")]
    public async Task<string> WorkspaceSymbolDefinitionAsync(
        [Description("Symbol name to resolve (optional when path+line are supplied).")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).")] string? path = null,
        [Description("1-based line number for path-based lookup (optional).")] int? line = null,
        [Description("1-based column number for path-based lookup (optional).")] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.WorkspaceSymbolDefinition, out var denied) is not { } authCtx)
            return denied!;

        var branchId = await ResolveBranchIdOrErrorAsync(McpToolNames.WorkspaceSymbolDefinition, authCtx.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (branchId is null)
            return McpJson.Error(McpToolNames.WorkspaceSymbolDefinition, $"Work unit '{authCtx.WorkUnitId}' was not found.");

        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindDefinitionsAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }

    [Description("Find symbol reference locations in this run's branch using compiler-backed semantic navigation.")]
    public async Task<string> WorkspaceSymbolReferencesAsync(
        [Description("Symbol name to resolve (optional when path+line are supplied).")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).")] string? path = null,
        [Description("1-based line number for path-based lookup (optional).")] int? line = null,
        [Description("1-based column number for path-based lookup (optional).")] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.WorkspaceSymbolReferences, out var denied) is not { } authCtx)
            return denied!;

        var branchId = await ResolveBranchIdOrErrorAsync(McpToolNames.WorkspaceSymbolReferences, authCtx.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (branchId is null)
            return McpJson.Error(McpToolNames.WorkspaceSymbolReferences, $"Work unit '{authCtx.WorkUnitId}' was not found.");

        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindReferencesAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }

    [Description("Find symbol implementation locations in this run's branch using compiler-backed semantic navigation.")]
    public async Task<string> WorkspaceSymbolImplementationAsync(
        [Description("Symbol name to resolve (optional when path+line are supplied).")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).")] string? path = null,
        [Description("1-based line number for path-based lookup (optional).")] int? line = null,
        [Description("1-based column number for path-based lookup (optional).")] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.WorkspaceSymbolImplementation, out var denied) is not { } authCtx)
            return denied!;

        var branchId = await ResolveBranchIdOrErrorAsync(McpToolNames.WorkspaceSymbolImplementation, authCtx.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (branchId is null)
            return McpJson.Error(McpToolNames.WorkspaceSymbolImplementation, $"Work unit '{authCtx.WorkUnitId}' was not found.");

        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindImplementationsAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }

    [Description("Fetch constrained external documentation with provenance metadata and source artifact recording, scoped to this run's work unit.")]
    public async Task<string> DocFetchAsync(
        [Description("The URL to fetch. Must satisfy the workspace's DocFetch scheme/domain allowlist.")] string url,
        [Description("Why this fetch is needed — recorded for provenance.")] string reason,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.DocFetch, out var denied) is not { } authCtx)
            return denied!;

        if (!workspaceOptions.DocFetchTools)
            return McpJson.Error(McpToolNames.DocFetch, "Doc fetch tools are disabled by configuration.");

        try
        {
            var result = await docFetch.FetchAsync(url, reason, authCtx.WorkUnitId, authCtx.SessionId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(result);
        }
        catch (ArgumentException ex) { return McpJson.Error(McpToolNames.DocFetch, ex.Message); }
        catch (InvalidOperationException ex) { return McpJson.Error(McpToolNames.DocFetch, ex.Message); }
    }

    [Description("Record a durable knowledge note (Research, Decision, or Constraint) for this run's work unit so future work units don't have to rediscover it.")]
    public async Task<string> ArtifactRecordAsync(
        [Description("Artifact type: Research, Decision, or Constraint.")] string type,
        [Description("Short title for the artifact.")] string title,
        [Description("The artifact body.")] string body,
        [Description("Optional parent artifact id this one builds on.")] string? parentArtifactId = null,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.ArtifactRecord, out var denied) is not { } authCtx)
            return denied!;

        try
        {
            var recorded = await artifacts.RecordAsync(authCtx.WorkUnitId, type, title, body, parentArtifactId, ct: cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { artifactId = recorded.ArtifactId });
        }
        catch (ArgumentException ex) { return McpJson.Error(McpToolNames.ArtifactRecord, ex.Message); }
    }

    [Description("Search knowledge artifacts for this run's work unit and its ancestors by type and/or keyword.")]
    public async Task<string> ArtifactQueryAsync(
        [Description("Optional artifact type filter: Research, Decision, or Constraint.")] string? type = null,
        [Description("Optional keyword filter.")] string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.ArtifactQuery, out var denied) is not { } authCtx)
            return denied!;

        try
        {
            var filtered = await artifacts.QueryAsync(authCtx.WorkUnitId, type, keywords, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(filtered);
        }
        catch (ArgumentException ex) { return McpJson.Error(McpToolNames.ArtifactQuery, ex.Message); }
    }

    // plans/harness-hosting-architecture.md Phase C.4 — the "true mid-turn pause": the tool call
    // itself blocks (polling ClarificationResponded events) until a human answers or
    // WorkspaceOptions.HarnessClarificationHoldOpenSeconds elapses, whichever comes first. On
    // timeout it returns a "parked" result rather than an error — the .workspace/inbox/outbox
    // file-based fallback (kill-and-respawn) still resolves the clarification on the next attempt,
    // so a harness that gets the parked result and stops is still safe, just slower.
    [Description("Ask a blocking clarifying question and wait (up to a server-configured timeout) for the human's answer in this same tool call, instead of writing to .workspace/inbox/ and stopping. If the timeout elapses first, returns a 'parked' result — the answer will still arrive via .workspace/outbox/ on the next resume.")]
    public async Task<string> ClarificationRequestAsync(
        [Description("The question to ask.")] string question,
        [Description("Optional additional context for the human.")] string? context = null,
        [Description("Optional suggested answer options.")] string[]? options = null,
        CancellationToken cancellationToken = default)
    {
        if (AuthorizeOrNull(McpToolNames.ClarificationRequest, out var denied) is not { } authCtx)
            return denied!;

        var requestedAt = DateTimeOffset.UtcNow;
        var result = await clarifications.RequestAsync(
            authCtx.WorkUnitId,
            question,
            context: context,
            blocking: true,
            options: options,
            requestedByAgentId: authCtx.AgentId,
            sessionId: authCtx.SessionId,
            ct: cancellationToken).ConfigureAwait(false);

        var holdOpenDeadline = requestedAt.AddSeconds(Math.Max(1, workspaceOptions.HarnessClarificationHoldOpenSeconds));
        while (DateTimeOffset.UtcNow < holdOpenDeadline)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var responded = await events.GetEventsByKindAsync(
                [ExecutionEventKind.ClarificationResponded], since: requestedAt, ct: cancellationToken).ConfigureAwait(false);

            foreach (var ev in responded)
            {
                ClarificationRespondedPayload? payload;
                try { payload = System.Text.Json.JsonSerializer.Deserialize<ClarificationRespondedPayload>(ev.PayloadJson); }
                catch (System.Text.Json.JsonException) { continue; }
                if (payload is null || payload.RequestId != result.RequestId)
                    continue;

                return McpJson.Ok(new
                {
                    requestId = result.RequestId,
                    workUnitId = authCtx.WorkUnitId,
                    status = "answered",
                    response = payload.Response,
                    note = payload.Note,
                });
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return McpJson.Ok(new
        {
            requestId = result.RequestId,
            workUnitId = authCtx.WorkUnitId,
            status = "parked",
            message = "No answer arrived within the hold-open window. Stop now — the answer will " +
                "arrive via .workspace/outbox/ when this work unit resumes.",
        });
    }
}
