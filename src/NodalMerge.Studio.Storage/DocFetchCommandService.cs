using System.Security.Cryptography;
using System.Text;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;

namespace NodalMerge.Studio.Storage;

// Slice 15g — constrained external documentation fetch with provenance snapshot and artifact lineage.
public sealed class DocFetchCommandService(
    IExternalDocFetcher fetcher,
    IArtifactLineageService artifactLineage,
    IWorkScheduler scheduler,
    IExecutionEventStream events,
    WorkspaceOptions options) : IDocFetchCommandService
{
    public async Task<DocFetchResult> FetchAsync(
        string url,
        string reason,
        string workUnitId,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (!options.DocFetchTools)
            throw new InvalidOperationException("Doc fetch tools are disabled by configuration.");
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.", nameof(reason));
        if (string.IsNullOrWhiteSpace(workUnitId))
            throw new ArgumentException("workUnitId is required.", nameof(workUnitId));

        var requested = url.Trim();
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var parsed))
            throw new ArgumentException("url must be an absolute URI.", nameof(url));
        if (!string.IsNullOrWhiteSpace(parsed.UserInfo))
            throw new ArgumentException("url must not contain credentials.", nameof(url));

        var normalized = Normalize(parsed);
        EnforcePolicy(normalized);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.DocFetchTimeoutSeconds));
        var maxBytes = Math.Max(256, options.DocFetchMaxContentBytes);
        var fetched = await fetcher.FetchAsync(normalized, maxBytes, timeout, ct).ConfigureAwait(false);

        var fetchedAt = DateTimeOffset.UtcNow;
        var snapshotBytes = Encoding.UTF8.GetBytes(fetched.Snapshot);
        var contentHash = Convert.ToHexString(SHA256.HashData(snapshotBytes)).ToLowerInvariant();

        var parentArtifactId = await ResolveParentArtifactIdAsync(workUnitId, ct).ConfigureAwait(false);
        var artifactId = $"SRC-{Guid.NewGuid():N}";
        var artifact = new ArtifactRef(
            ArtifactId: artifactId,
            Type: ArtifactType.Research,
            ParentArtifactId: parentArtifactId,
            Status: StudioArtifactStatus.Active,
            CreatedAt: fetchedAt,
            OwnedByWorkUnitId: workUnitId,
            OwnedByAgentId: null,
            Title: $"External source: {normalized}",
            Body: BuildArtifactBody(reason, fetchedAt, normalized.ToString(), contentHash, fetched.ContentType, fetched.Truncated, fetched.SnapshotBytes, fetched.Snapshot));
        await artifactLineage.RecordAsync(artifact, ct).ConfigureAwait(false);

        var resolvedSessionId = await ResolveSessionIdAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
        if (resolvedSessionId is not null)
        {
            await events.AppendAsync(
                resolvedSessionId,
                workUnitId,
                ExecutionEventKind.ExternalDocFetched,
                new ExternalDocFetchedPayload(
                    artifactId,
                    workUnitId,
                    normalized.ToString(),
                    contentHash,
                    fetched.Truncated,
                    fetched.SnapshotBytes,
                    fetchedAt),
                ct: ct).ConfigureAwait(false);
        }

        return new DocFetchResult(
            artifactId,
            workUnitId,
            requested,
            normalized.ToString(),
            reason,
            fetchedAt,
            contentHash,
            "sha256",
            fetched.ContentType,
            fetched.Snapshot,
            fetched.Truncated,
            fetched.SnapshotBytes,
            Summarize(fetched.Snapshot));
    }

    private void EnforcePolicy(Uri uri)
    {
        if (!Contains(options.DocFetchAllowedSchemes, uri.Scheme))
            throw new ArgumentException($"Scheme '{uri.Scheme}' is not allowed.");

        var host = uri.Host;
        if (MatchesDomainList(options.DocFetchDeniedDomains, host))
            throw new ArgumentException($"Domain '{host}' is denied by policy.");

        if (options.DocFetchAllowedDomains.Count > 0 && !MatchesDomainList(options.DocFetchAllowedDomains, host))
            throw new ArgumentException($"Domain '{host}' is not allowlisted.");
    }

    private async Task<string?> ResolveSessionIdAsync(string workUnitId, string? explicitSessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitSessionId))
            return explicitSessionId;

        var pending = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
        return pending.FirstOrDefault(i => i.WorkUnitId == workUnitId)?.SessionId;
    }

    private async Task<string> ResolveParentArtifactIdAsync(string workUnitId, CancellationToken ct)
    {
        var chain = await artifactLineage.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
        var task = chain.LastOrDefault(a => a.Type == ArtifactType.Task);
        return task?.ArtifactId ?? workUnitId;
    }

    private static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.Host.ToLowerInvariant(),
        };

        if (builder.Scheme == "https" && builder.Port == 443)
            builder.Port = -1;
        if (builder.Scheme == "http" && builder.Port == 80)
            builder.Port = -1;

        return builder.Uri;
    }

    private static bool Contains(IReadOnlyList<string> values, string candidate) =>
        values.Any(v => string.Equals(v?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesDomainList(IReadOnlyList<string> domains, string host)
    {
        foreach (var raw in domains)
        {
            var domain = raw?.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string? Summarize(string snapshot)
    {
        var maxChars = Math.Max(0, options.DocFetchSummaryMaxChars);
        if (maxChars == 0 || string.IsNullOrWhiteSpace(snapshot))
            return null;

        var singleLine = string.Join(' ', snapshot
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (singleLine.Length <= maxChars)
            return singleLine;

        return singleLine[..maxChars] + "...";
    }

    private static string BuildArtifactBody(
        string reason,
        DateTimeOffset fetchedAt,
        string normalizedUrl,
        string contentHash,
        string contentType,
        bool truncated,
        int snapshotBytes,
        string snapshot)
    {
        var lines = new List<string>
        {
            $"Reason: {reason}",
            $"FetchedAt: {fetchedAt:O}",
            $"Url: {normalizedUrl}",
            $"ContentHash: sha256:{contentHash}",
            $"ContentType: {contentType}",
            $"Truncated: {truncated}",
            $"SnapshotBytes: {snapshotBytes}",
            string.Empty,
            "Snapshot:",
            snapshot,
        };

        return string.Join(Environment.NewLine, lines);
    }
}
