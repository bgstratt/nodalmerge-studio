using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class WorkspaceSemanticNavigationService(IFileWorkspaceService fileWorkspace)
    : IWorkspaceSemanticNavigationService
{
    public Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindDefinitionsAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default) =>
        FindAsync(branchId, query, SymbolLookupKind.Definitions, ct);

    public Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindReferencesAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default) =>
        FindAsync(branchId, query, SymbolLookupKind.References, ct);

    public Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindImplementationsAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default) =>
        FindAsync(branchId, query, SymbolLookupKind.Implementations, ct);

    private async Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        SymbolLookupKind lookupKind,
        CancellationToken ct)
    {
        var maxResults = Math.Clamp(query.MaxResults, 1, 1000);
        if (string.IsNullOrWhiteSpace(query.Symbol)
            && string.IsNullOrWhiteSpace(query.Path)
            && query.Line is null)
        {
            return ([], false);
        }

        var branchDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(branchDir) || !Directory.Exists(branchDir))
            return ([], false);

        var projectPaths = EnumerateProjectFiles(branchDir).ToList();
        if (projectPaths.Count == 0)
            return ([], false);

        using var workspace = new AdhocWorkspace();
        var solution = await BuildSolutionAsync(workspace, projectPaths, ct).ConfigureAwait(false);
        var symbols = await ResolveSymbolsAsync(solution, branchDir, query, ct).ConfigureAwait(false);
        if (symbols.Length == 0)
            return ([], false);

        return lookupKind switch
        {
            SymbolLookupKind.Definitions =>
                BuildFromDefinitionLocations(branchDir, symbols, maxResults),
            SymbolLookupKind.References =>
                await BuildFromReferencesAsync(branchDir, solution, symbols, maxResults, ct).ConfigureAwait(false),
            SymbolLookupKind.Implementations =>
                await BuildFromImplementationsAsync(branchDir, solution, symbols, maxResults, ct).ConfigureAwait(false),
            _ => ([], false)
        };
    }

    private static (IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated) BuildFromDefinitionLocations(
        string branchDir,
        ImmutableArray<ISymbol> symbols,
        int maxResults)
    {
        var locations = new List<WorkspaceSymbolLocation>();
        foreach (var symbol in symbols)
        {
            AddSymbolLocations(branchDir, symbol, symbol.Locations.Where(l => l.IsInSource), locations, maxResults);
            if (locations.Count >= maxResults)
                return (locations, true);
        }

        return (locations, false);
    }

    private static async Task<Solution> BuildSolutionAsync(
        AdhocWorkspace workspace,
        IReadOnlyList<string> projectPaths,
        CancellationToken ct)
    {
        var solution = workspace.CurrentSolution;
        var metadataRefs = ResolveMetadataReferences();

        foreach (var projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();

            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                continue;

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectId = ProjectId.CreateNewId(debugName: projectName);
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: projectName,
                assemblyName: projectName,
                language: LanguageNames.CSharp,
                filePath: projectPath,
                outputFilePath: null,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: metadataRefs);

            solution = solution.AddProject(projectInfo);

            foreach (var sourcePath in EnumerateCSharpFiles(projectDir))
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var documentId = DocumentId.CreateNewId(projectId, debugName: sourcePath);
                var documentName = Path.GetFileName(sourcePath);
                solution = solution.AddDocument(
                    documentId,
                    documentName,
                    SourceText.From(text),
                    filePath: sourcePath);
            }
        }

        return solution;
    }

    private static async Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> BuildFromReferencesAsync(
        string branchDir,
        Solution solution,
        ImmutableArray<ISymbol> symbols,
        int maxResults,
        CancellationToken ct)
    {
        var locations = new List<WorkspaceSymbolLocation>();
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false);
            foreach (var referenced in refs)
            {
                foreach (var location in referenced.Locations)
                {
                    AddSymbolLocations(branchDir, symbol, [location.Location], locations, maxResults);
                    if (locations.Count >= maxResults)
                        return (locations, true);
                }
            }
        }

        return (locations, false);
    }

    private static async Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> BuildFromImplementationsAsync(
        string branchDir,
        Solution solution,
        ImmutableArray<ISymbol> symbols,
        int maxResults,
        CancellationToken ct)
    {
        var locations = new List<WorkspaceSymbolLocation>();
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct)
                .ConfigureAwait(false);
            foreach (var implementation in implementations)
            {
                AddSymbolLocations(branchDir, implementation, implementation.Locations.Where(l => l.IsInSource), locations, maxResults);
                if (locations.Count >= maxResults)
                    return (locations, true);
            }
        }

        return (locations, false);
    }

    private static void AddSymbolLocations(
        string branchDir,
        ISymbol symbol,
        IEnumerable<Location> sourceLocations,
        List<WorkspaceSymbolLocation> target,
        int maxResults)
    {
        foreach (var location in sourceLocations)
        {
            if (!TryMapToRelativeLocation(branchDir, location, out var mapped))
                continue;

            target.Add(new WorkspaceSymbolLocation(
                mapped.Path,
                mapped.Line,
                mapped.Column,
                symbol.Name,
                symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Kind.ToString()));

            if (target.Count >= maxResults)
                return;
        }
    }

    private static bool TryMapToRelativeLocation(string branchDir, Location location, out (string Path, int Line, int Column) mapped)
    {
        mapped = default;
        if (!location.IsInSource || string.IsNullOrWhiteSpace(location.SourceTree?.FilePath))
            return false;

        var absolute = Path.GetFullPath(location.SourceTree!.FilePath);
        var root = Path.GetFullPath(branchDir);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var lineSpan = location.GetLineSpan();
        var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
        mapped = (relative, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
        return true;
    }

    private static async Task<ImmutableArray<ISymbol>> ResolveSymbolsAsync(
        Solution solution,
        string branchDir,
        WorkspaceSymbolQuery query,
        CancellationToken ct)
    {
        var comparer = SymbolEqualityComparer.Default;
        var set = new HashSet<ISymbol>(comparer);

        if (query.Path is { Length: > 0 } && query.Line is not null)
        {
            var symbolAtLocation = await FindSymbolAtLocationAsync(solution, branchDir, query, ct).ConfigureAwait(false);
            if (symbolAtLocation is not null)
                set.Add(symbolAtLocation);
        }

        if (set.Count == 0 && query.Symbol is { Length: > 0 })
        {
            var seenKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var declarations = await SymbolFinder.FindDeclarationsAsync(
                        project,
                        query.Symbol,
                        ignoreCase: true,
                        filter: SymbolFilter.TypeAndMember,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                foreach (var declaration in declarations)
                {
                    var key = declaration.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    if (seenKeys.TryAdd(key, 0))
                        set.Add(declaration);
                }
            }
        }

        return [.. set];
    }

    private static async Task<ISymbol?> FindSymbolAtLocationAsync(
        Solution solution,
        string branchDir,
        WorkspaceSymbolQuery query,
        CancellationToken ct)
    {
        if (query.Path is null || query.Line is null)
            return null;

        var absPath = Path.GetFullPath(Path.Combine(branchDir, query.Path));
        var doc = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null
                && string.Equals(Path.GetFullPath(d.FilePath), absPath, StringComparison.OrdinalIgnoreCase));

        if (doc is null)
            return null;

        var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);
        if (sourceText.Lines.Count == 0)
            return null;

        var lineIndex = Math.Clamp(query.Line.Value - 1, 0, sourceText.Lines.Count - 1);
        var line = sourceText.Lines[lineIndex];
        var columnIndex = Math.Clamp((query.Column ?? 1) - 1, 0, Math.Max(0, line.Span.Length - 1));
        var position = line.Start + columnIndex;

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
            return null;

        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null)
            return null;

        return semanticModel.GetDeclaredSymbol(node, ct)
               ?? semanticModel.GetSymbolInfo(node, ct).Symbol
               ?? semanticModel.GetSymbolInfo(node, ct).CandidateSymbols.FirstOrDefault();
    }

    private static IReadOnlyList<MetadataReference> ResolveMetadataReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(tpa))
            return refs;

        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // Best effort only.
            }
        }

        return refs;
    }

    private static IEnumerable<string> EnumerateProjectFiles(string branchDir)
    {
        var pending = new Stack<string>();
        pending.Push(branchDir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (IsIgnoredDirectory(name))
                    continue;
                pending.Push(child);
            }

            IEnumerable<string> csprojs;
            try
            {
                csprojs = Directory.EnumerateFiles(current, "*.csproj", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var csproj in csprojs)
                yield return csproj;
        }
    }

    private static IEnumerable<string> EnumerateCSharpFiles(string projectDir)
    {
        var pending = new Stack<string>();
        pending.Push(projectDir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (IsIgnoredDirectory(name))
                    continue;
                pending.Push(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private static bool IsIgnoredDirectory(string segment) =>
        segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("dist", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("build", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("target", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("venv", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private enum SymbolLookupKind
    {
        Definitions,
        References,
        Implementations
    }
}
