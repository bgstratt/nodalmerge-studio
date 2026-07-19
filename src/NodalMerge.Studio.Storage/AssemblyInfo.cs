using System.Runtime.CompilerServices;

// Slice 1.1 (plans/cas-distribution-and-storage.md Phase 1) — CanonicalTreeSerializer,
// SnapshotTreeResolver, and the TreeDocument/TreeEntry/TreeEntryKind shapes are internal (this
// project is the only production writer/reader of tree-object bytes); Integration.Tests needs
// direct access to pin the golden-vector byte contract and exercise the resolver's CAS-miss/legacy
// paths without going through a full Host pipeline for every case.
[assembly: InternalsVisibleTo("NodalMerge.Studio.Integration.Tests")]
