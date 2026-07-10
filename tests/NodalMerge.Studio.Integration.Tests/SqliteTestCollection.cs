namespace NodalMerge.Studio.Integration.Tests;

// SqliteConnection.ClearAllPools() (called in Dispose() by every class below) is a process-wide
// static call — it force-closes every pooled SQLite connection in the whole test process, not just
// the caller's own. xUnit runs different test classes in parallel by default, so without this,
// one test's teardown can yank a connection out from under an unrelated SQLite-backed test still
// mid-flight on another thread — the intermittent, "different test each run" failures this was
// causing (see [[nodalmerge_worker_scope_bleed]] and the local-dev-flow memory's "1 rotating
// failure per run" note, same root cause in the dotnet host suite). Tests in the same xUnit
// collection never run concurrently with each other, so putting every SQLite-touching class here
// serializes exactly the classes capable of tripping this race — everything else in the assembly
// keeps running fully in parallel.
[CollectionDefinition("Sqlite")]
public class SqliteTestCollection;
