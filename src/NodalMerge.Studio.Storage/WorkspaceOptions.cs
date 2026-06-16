namespace NodalMerge.Studio.Storage;

public sealed class WorkspaceOptions
{
    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "studio-workspace");
    public string? SeedRepositoryPath { get; set; }
    public long MaxReadBytes  { get; set; } = 524_288;   // 512 KB
    public long MaxWriteBytes { get; set; } = 2_097_152; // 2 MB
}
