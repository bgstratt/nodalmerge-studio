namespace NodalMerge.Studio.Contracts.Domain;

public sealed record ChangeIntent(
    string IntentId,
    string WorkUnitId,
    string IntentType,         // modify | create | delete | rename
    string TargetPath,         // file path or logical id
    string RegionDescriptor,   // lines, AST node id, or semantic tag ("" / "*" means whole-file)
    string BaseSnapshotHash,
    IReadOnlyList<string>? FilesTouchedHint,
    DateTimeOffset CreatedAt);
