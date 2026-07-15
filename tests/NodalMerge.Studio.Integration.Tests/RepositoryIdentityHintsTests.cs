using LibGit2Sharp;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2) — docs/STUDIO_ROOM_SCHEMA.md
// (b)'s frozen remote-URL normalization algorithm, pinned against its own worked-example table plus
// the extra vectors the 6.2 task brief calls for (scp-form with no user, trailing-slash+".git"
// combined), and a real-repository integration test for root-SHA/remote hint computation.
[Trait("Category", "Integration")]
public class RepositoryIdentityHintsTests
{
    private static readonly GitRepositoryIdentityHintsService Service = new();

    // docs/STUDIO_ROOM_SCHEMA.md (b) "Remote-URL normalization" worked-example table, verbatim.
    [Theory]
    [InlineData("https://github.com/acme/nodalmerge-studio.git", "github.com/acme/nodalmerge-studio")]
    [InlineData("https://x-access-token:ghp_xxx@github.com/acme/nodalmerge-studio.git", "github.com/acme/nodalmerge-studio")]
    [InlineData("git@github.com:acme/nodalmerge-studio.git", "github.com/acme/nodalmerge-studio")]
    [InlineData("ssh://git@github.com:22/acme/nodalmerge-studio.git", "github.com/acme/nodalmerge-studio")]
    [InlineData("ssh://git@example.com:2222/acme/repo.git", "example.com:2222/acme/repo")]
    [InlineData("https://github.com/acme/nodalmerge-studio/", "github.com/acme/nodalmerge-studio")]
    [InlineData("HTTPS://GitHub.com/Acme/Nodalmerge-Studio.git", "github.com/Acme/Nodalmerge-Studio")]
    public void NormalizeRemoteUrl_matches_the_frozen_worked_examples(string raw, string expected)
    {
        Assert.Equal(expected, Service.NormalizeRemoteUrl(raw));
    }

    // Extra vector #1 (task brief): scp-form with no user — same as the worked-example scp row
    // but without the "git@" prefix, confirming the user-segment is genuinely optional rather than
    // required for scp-form detection.
    [Fact]
    public void NormalizeRemoteUrl_handles_scp_form_with_no_user()
    {
        Assert.Equal(
            "github.com/acme/nodalmerge-studio",
            Service.NormalizeRemoteUrl("github.com:acme/nodalmerge-studio.git"));
    }

    // Extra vector #2 (task brief): trailing-slash+".git" combined. The frozen algorithm applies
    // step 4 (strip ".git") and step 5 (strip trailing "/") once each, IN ORDER — a path ending in
    // ".git/" doesn't end in literal ".git" (it ends in "/"), so step 4 doesn't fire; step 5 then
    // strips only the trailing slash, leaving ".git" in the result. This is the literal, specified
    // behavior of "steps applied once each, in order" — not a bug in this implementation.
    [Fact]
    public void NormalizeRemoteUrl_trailing_slash_after_dot_git_leaves_dot_git_per_frozen_step_order()
    {
        Assert.Equal(
            "github.com/acme/nodalmerge-studio.git",
            Service.NormalizeRemoteUrl("https://github.com/acme/nodalmerge-studio.git/"));
    }

    [Fact]
    public void NormalizeRemoteUrl_is_idempotent_on_its_own_output()
    {
        var once = Service.NormalizeRemoteUrl("https://github.com/acme/nodalmerge-studio.git");
        var twice = Service.NormalizeRemoteUrl(once);
        Assert.Equal(once, twice);
    }

    // Real-git integration test: builds an actual repository via LibGit2Sharp (the same library
    // ComputeAsync itself uses — see GitRepositoryIdentityHintsService's own class comment for why
    // this doesn't need an external `git` binary at all, so there is no "skip if git is absent"
    // branch here), commits once, adds a remote, and confirms ComputeAsync reports both the root
    // commit SHA and the normalized remote.
    [Fact]
    public async Task ComputeAsync_reports_root_sha_and_normalized_remote_for_a_real_repository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-identity-hints-{Guid.NewGuid():N}");
        Repository.Init(path);
        try
        {
            using (var repo = new Repository(path))
            {
                File.WriteAllText(Path.Combine(path, "README.md"), "hello");
                Commands.Stage(repo, "README.md");
                var author = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
                repo.Commit("initial commit", author, author);
                repo.Network.Remotes.Add("origin", "https://github.com/acme/nodalmerge-studio.git");
            }

            var hints = await Service.ComputeAsync(path);

            Assert.Single(hints.RootShas);
            Assert.Equal("github.com/acme/nodalmerge-studio", Assert.Single(hints.Remotes));
            Assert.False(hints.IsDegraded);
        }
        finally
        {
            DeleteGitDirectory(path);
        }
    }

    [Fact]
    public async Task ComputeAsync_degrades_to_empty_hints_for_a_non_git_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-identity-hints-nongit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var hints = await Service.ComputeAsync(path);

            Assert.Empty(hints.RootShas);
            Assert.Empty(hints.Remotes);
            Assert.True(hints.IsDegraded);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeAsync_degrades_to_empty_root_shas_for_a_repo_with_no_commits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-identity-hints-empty-{Guid.NewGuid():N}");
        Repository.Init(path);
        try
        {
            using (var repo = new Repository(path))
                repo.Network.Remotes.Add("origin", "git@github.com:acme/empty-repo.git");

            var hints = await Service.ComputeAsync(path);

            Assert.Empty(hints.RootShas);
            Assert.Equal("github.com/acme/empty-repo", Assert.Single(hints.Remotes));
        }
        finally
        {
            DeleteGitDirectory(path);
        }
    }

    // git marks files under .git/objects read-only on Windows; Directory.Delete throws
    // UnauthorizedAccessException on those unless the attribute is cleared first (same helper
    // pattern as RepositoryRegistryTests.DeleteGitDirectory).
    private static void DeleteGitDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
