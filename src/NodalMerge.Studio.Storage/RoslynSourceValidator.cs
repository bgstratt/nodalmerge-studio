using Microsoft.CodeAnalysis.CSharp;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 10 — uses Roslyn to check whether merged C# content parses without errors.
// Only validates C# files (.cs extension); all other paths return true (valid).
public sealed class RoslynSourceValidator : ISourceValidator
{
    public bool IsValidSyntax(string content, string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return true;

        var tree = CSharpSyntaxTree.ParseText(content);
        var diagnostics = tree.GetDiagnostics();
        return !diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }
}
