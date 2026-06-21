using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 9a — see plans/phase-9-workspace-profile.md. Detection groups marker files by their
// containing directory (recursively) instead of the single repo-root check
// WorkspaceExecutionService.ResolveBuildCommands does today, which is what lets a repo shaped
// "backend/Host.csproj" + "frontend/package.json" (no root-level markers at all) be detected
// correctly.
internal sealed class WorkspaceProfileService(IFileWorkspaceService fileWorkspace) : IWorkspaceProfileService
{
    private static readonly string[] IgnoredDirNames =
        ["node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv"];

    private readonly ConcurrentDictionary<string, WorkspaceProfile> _cache = new();

    public async Task<WorkspaceProfile> GetOrDetectAsync(string branchId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(branchId, out var cached))
            return cached;

        return await RescanAsync(branchId, ct).ConfigureAwait(false);
    }

    public async Task<WorkspaceProfile> RescanAsync(string branchId, CancellationToken ct = default)
    {
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
        if (workDir is null)
            throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        var profile = await Task.Run(() => Detect(branchId, workDir), ct).ConfigureAwait(false);
        _cache[branchId] = profile;
        return profile;
    }

    public void Invalidate(string branchId) => _cache.TryRemove(branchId, out _);

    // ── Detection ────────────────────────────────────────────────────────────

    private static WorkspaceProfile Detect(string branchId, string workDir)
    {
        var slnDirs    = new HashSet<string>();
        var csprojDirs = new HashSet<string>();
        var npmDirs    = new HashSet<string>();
        var cargoDirs  = new HashSet<string>();
        var goDirs     = new HashSet<string>();
        var pyDirs     = new HashSet<string>();
        var makeDirs   = new HashSet<string>();
        var cmakeDirs  = new HashSet<string>();

        foreach (var file in EnumerateRelevantFiles(workDir))
        {
            var relDir = RelDir(workDir, Path.GetDirectoryName(file)!);
            var name = Path.GetFileName(file);

            if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                slnDirs.Add(relDir);
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                csprojDirs.Add(relDir);
            else if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase))
                npmDirs.Add(relDir);
            else if (string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase))
                cargoDirs.Add(relDir);
            else if (string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase))
                goDirs.Add(relDir);
            else if (string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
                pyDirs.Add(relDir);
            else if (string.Equals(name, "Makefile", StringComparison.OrdinalIgnoreCase))
                makeDirs.Add(relDir);
            else if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                cmakeDirs.Add(relDir);
        }

        var roots = new List<ProjectRoot>();
        var claimed = new HashSet<string>();

        // 1. dotnet anchors — a .sln/.slnx directory owns every .csproj nested under it, so build/
        //    test run once at the solution root instead of once per project.
        foreach (var dir in slnDirs.OrderBy(d => d.Length))
        {
            if (!claimed.Add(dir)) continue;
            roots.Add(BuildDotnetRoot(workDir, dir));
        }

        // 2. remaining .csproj directories not already covered by an anchor above.
        foreach (var dir in csprojDirs)
        {
            if (claimed.Contains(dir) || IsUnder(dir, slnDirs)) continue;
            if (!claimed.Add(dir)) continue;
            roots.Add(BuildDotnetRoot(workDir, dir));
        }

        AddRoots(roots, claimed, npmDirs,   dir => BuildNpmRoot(workDir, dir));
        AddRoots(roots, claimed, cargoDirs, dir => new ProjectRoot(dir, "cargo", "cargo build", "cargo test", "cargo run", false));
        AddRoots(roots, claimed, goDirs,    dir => new ProjectRoot(dir, "go", "go build ./...", "go test ./...", null, false));
        AddRoots(roots, claimed, pyDirs,    dir => new ProjectRoot(dir, "python", null, "pytest", null, false));
        AddRoots(roots, claimed, makeDirs,  dir => new ProjectRoot(dir, "make", "make", "make test", null, false));
        AddRoots(roots, claimed, cmakeDirs, dir => new ProjectRoot(dir, "cmake", "cmake --build build", "ctest --test-dir build", null, false));

        // Phase 9h — attach each root's own rule file (AGENTS.md-equivalent), and make sure the
        // branch root itself is covered even when no buildable project sits there directly (e.g.
        // a repo with only "backend/" and "frontend/" roots can still have a top-level AGENTS.md
        // describing whole-repo conventions).
        for (var i = 0; i < roots.Count; i++)
        {
            var rule = DetectRuleFile(AbsDir(workDir, roots[i].RelativePath));
            if (rule is not null)
                roots[i] = roots[i] with { RuleFileContent = rule };
        }
        if (!claimed.Contains(""))
        {
            var rootRule = DetectRuleFile(workDir);
            if (rootRule is not null)
                roots.Add(new ProjectRoot("", "none", null, null, null, false, rootRule));
        }

        return new WorkspaceProfile(branchId, roots.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList(), DateTimeOffset.UtcNow);
    }

    private static readonly string[] RuleFileNames = ["AGENTS.md", "CLAUDE.md", ".clinerules", ".cursorrules"];
    private const int MaxRuleFileChars = 4000;

    private static string? DetectRuleFile(string absDir)
    {
        if (!Directory.Exists(absDir)) return null;
        foreach (var name in RuleFileNames)
        {
            var path = Path.Combine(absDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var content = File.ReadAllText(path);
                return content.Length > MaxRuleFileChars ? content[..MaxRuleFileChars] : content;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static void AddRoots(
        List<ProjectRoot> roots, HashSet<string> claimed, HashSet<string> dirs, Func<string, ProjectRoot> build)
    {
        foreach (var dir in dirs)
        {
            if (!claimed.Add(dir)) continue;
            roots.Add(build(dir));
        }
    }

    private static bool IsUnder(string dir, HashSet<string> anchors) =>
        anchors.Any(a => dir == a || (a.Length == 0) || dir.StartsWith(a + "/", StringComparison.Ordinal));

    private static ProjectRoot BuildDotnetRoot(string workDir, string relDir)
    {
        var absDir = AbsDir(workDir, relDir);

        // "dotnet run" is only unambiguous when exactly one .csproj sits directly in this
        // directory (not nested) — an anchor directory's projects live in subfolders, and a
        // directory with multiple sibling .csproj files can't be auto-run without picking one.
        var topLevelCsproj = Directory.Exists(absDir)
            ? Directory.GetFiles(absDir, "*.csproj", SearchOption.TopDirectoryOnly)
            : [];

        // Ambiguity is purely "how many .csproj sit directly in this directory" — an anchor
        // (.sln/.slnx) directory that also happens to hold its one project alongside the solution
        // file is just as runnable as a plain single-project directory.
        string? runCommand = null;
        var isLongRunning = false;
        if (topLevelCsproj.Length == 1)
        {
            runCommand = "dotnet run";
            isLongRunning = IsAspNetWebProject(topLevelCsproj[0]);
        }

        return new ProjectRoot(relDir, "dotnet", "dotnet build", "dotnet test", runCommand, isLongRunning);
    }

    private static bool IsAspNetWebProject(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var sdk = doc.Root?.Attribute("Sdk")?.Value;
            return sdk is not null && sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ProjectRoot BuildNpmRoot(string workDir, string relDir)
    {
        var packageJsonPath = Path.Combine(AbsDir(workDir, relDir), "package.json");

        string? build = null, test = null, run = null;
        var isLongRunning = false;

        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object)
            {
                build = ScriptOrNull(scripts, "build") is not null ? "npm run build" : null;
                test  = ScriptOrNull(scripts, "test")  is not null ? "npm test" : null;

                if (ScriptOrNull(scripts, "dev") is not null) { run = "npm run dev"; isLongRunning = true; }
                else if (ScriptOrNull(scripts, "start") is not null) { run = "npm run start"; isLongRunning = true; }
            }
        }
        catch
        {
            // Malformed package.json — still a valid npm root, just no resolved commands.
        }

        return new ProjectRoot(relDir, "npm", build, test, run, isLongRunning);
    }

    private static string? ScriptOrNull(JsonElement scripts, string name) =>
        scripts.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Filesystem walk ──────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateRelevantFiles(string workDir)
    {
        foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            if (!IsRelevant(Path.GetFileName(file))) continue;
            if (IsIgnored(workDir, file)) continue;
            yield return file;
        }
    }

    private static bool IsRelevant(string fileName) =>
        fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "go.mod", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnored(string workDir, string fullPath)
    {
        var relative = Path.GetRelativePath(workDir, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Last segment is the file itself — only directory segments matter here.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && segment[0] == '.') return true;
            if (IgnoredDirNames.Contains(segment, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string RelDir(string workDir, string absDir)
    {
        var relative = Path.GetRelativePath(workDir, absDir).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string AbsDir(string workDir, string relDir) =>
        relDir.Length == 0 ? workDir : Path.Combine(workDir, relDir);
}
