namespace NodalMerge.Studio.Contracts.Domain;

// Stored as studio/comod-pattern/v1. Represents how often two paths are modified together
// across distinct work units — the signal that co-change is structurally likely.
public sealed record CoModificationPattern(
    string PatternId,
    string RepositoryId,
    string PathA,
    string PathB,
    int CoModificationCount,
    int TotalWorkUnitsScanned,
    double Confidence,
    DateTimeOffset ComputedAt);
