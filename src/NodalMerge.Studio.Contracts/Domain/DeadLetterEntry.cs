using System.Text.Json.Serialization;

namespace NodalMerge.Studio.Contracts.Domain;

public sealed record DeadLetterEntry(
    string EntryId,
    string WorkUnitId,
    string AgentId,
    PipelineStage Stage,
    string ProfileId,
    string Reason,
    string? LastProjectionSnapshot,
    int AttemptCount,
    DateTimeOffset OccurredAt,
    string? TaskId = null,
    bool MaxAttemptsReached = false,
    // Captured from whatever credentials the failed run actually used, so retry doesn't have to
    // re-derive them from the in-memory orchestrator registry — that registry is ephemeral (lost
    // on Host restart, or once the orchestrator's own loop has completed) and may simply be gone
    // by the time a human gets around to retrying a dead-lettered item.
    string? Model = null,
    string? BaseUrl = null,
    // Never persisted (see [JsonIgnore]) — the live in-memory value survives for this process's
    // lifetime (so an immediate retry, no restart involved, is unaffected), but RehydrateAsync
    // deserializes this back as null after a restart. ResolveRetryCredentials falls back to
    // IRuntimeCredentialCache via CredentialRef, then the live orchestrator registry, in that order.
    [property: JsonIgnore] string? ApiKey = null,
    string? Provider = null,
    // Structured failure classification — see FailureKind for the two-track recovery model this
    // enables. Defaults to Exception for any recording path not yet updated to pass a real value.
    FailureKind Kind = FailureKind.Exception,
    // Opaque cache key the client derived for the credentials this run used (e.g. its VS Code
    // SecretStorage apiKeyRef) — never a secret itself.
    string? CredentialRef = null);
