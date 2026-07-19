using System.Collections.Concurrent;
using System.Linq;
using NodalMerge.Host.Abstractions.Providers;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 2.3 (plans/cas-distribution-and-storage.md Phase 2) test double for
/// <see cref="IRemoteBlobPushTarget"/> — a ConcurrentDictionary-backed fake "remote origin" that
/// CasReconcileServiceTests seeds as empty or partially populated to exercise the reconcile
/// sweep's Exists/Push split. <see cref="FailFirstN"/> lets a test simulate transient push
/// failures without a second implementation: the first N PushAsync calls (across all hashes, in
/// whatever order the bounded-concurrency sweep happens to issue them) throw; the sweep's own
/// Failed counter is what a test asserts against, not which specific hash failed.
/// </summary>
public sealed class FakeRemoteBlobPushTarget : IRemoteBlobPushTarget
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private int _pushAttempts;

    /// <summary>Number of PushAsync calls (in call order) that throw before succeeding. Default 0
    /// — every push succeeds.</summary>
    public int FailFirstN { get; set; }

    public ValueTask<bool> ExistsAsync(string hashHex, CancellationToken ct = default) =>
        ValueTask.FromResult(_blobs.ContainsKey(hashHex));

    public ValueTask PushAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _pushAttempts);
        if (attempt <= FailFirstN)
            throw new InvalidOperationException($"Simulated push failure for '{hashHex}' (attempt {attempt}).");

        _blobs[hashHex] = bytes;
        return ValueTask.CompletedTask;
    }

    /// <summary>Test hook — seeds the remote as already holding a hash, without going through
    /// PushAsync (e.g. to set up a "partial remote" scenario).</summary>
    public void Seed(string hashHex, byte[]? bytes = null) => _blobs[hashHex] = bytes ?? [];

    /// <summary>Every hash currently held by the fake remote.</summary>
    public IReadOnlyCollection<string> Hashes => _blobs.Keys.ToList();

    public int Count => _blobs.Count;
}
