using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/vision-punchlist-remediation.md — makes silent background failures loud.
///
/// `_ = SomethingAsync()` is used in a number of places to defer work off a request path. If such a
/// task throws, nobody observes it: the Task holds the exception, and when it is garbage-collected
/// unobserved the runtime raises TaskScheduler.UnobservedTaskException — which, since .NET 4.5, does
/// **nothing** by default. No crash, no log. The work simply did not happen.
///
/// That is precisely the shape of the flakes this suite kept producing: a background task dies, the
/// state it was supposed to write never lands, and the damage surfaces later as some unrelated test
/// failing. The failure gets attributed to whichever test happened to be next, not to the code that
/// actually broke.
///
/// This records every unobserved exception with its stack, prints it the moment it is detected (so it
/// lands near the responsible test in the log), and reports a summary once the assembly finishes.
///
/// KNOWN LIMIT, stated plainly: detection is garbage-collection-timed. The event fires when the dead
/// task is collected, which can be well after — and in a different test than — the failure. So treat
/// the stack trace as the truth and the surrounding log position as a hint, never the reverse.
/// </summary>
internal static class UnobservedTaskExceptionDetector
{
    internal sealed record Capture(DateTimeOffset At, AggregateException Exception);

    // Marks the self-test's intentional leak so it is captured (the test asserts on it) but never
    // printed as a finding.
    internal const string SelfTestMarker = "deliberate leak - detector self-test";

    // Real findings. Only ReportSummary drains this — nothing else may, or evidence is destroyed.
    private static readonly ConcurrentQueue<Capture> Captured = new();

    // The self-test's own deliberate leak, kept in a SEPARATE queue.
    //
    // This used to share one queue with the real findings, and the self-test called Drain() — which
    // empties it — both before and after its own leak. The shared queue is process-wide and the
    // self-test runs somewhere in the middle of 700+ others, so every real leak captured before it
    // was silently discarded and never reached the end-of-run summary. The detector was destroying
    // exactly the evidence it exists to collect, and "no leaks reported" meant nothing.
    //
    // Two queues instead of one filter: it makes the interference structurally impossible rather
    // than dependent on every future caller remembering to filter.
    private static readonly ConcurrentQueue<Capture> SelfTestCaptured = new();

    // Runs when the test assembly is loaded, before any test executes — earlier than any fixture
    // could, which matters because the leaks we care about can start during the very first test.
    [ModuleInitializer]
    internal static void Install()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Mark observed so this stays a diagnostic and can never change process behaviour —
            // the point is to report the leak, not to introduce a new failure mode.
            e.SetObserved();

            var capture = new Capture(DateTimeOffset.UtcNow, e.Exception);

            // The detector's own self-test leaks a task on purpose; reporting it would train readers
            // to ignore this output, which is the one thing that would make the detector useless.
            // It goes to its own queue so the self-test can consume it without touching real findings.
            if (capture.Exception.Flatten().InnerExceptions.Any(
                    x => x.Message.Contains(SelfTestMarker, StringComparison.Ordinal)))
            {
                SelfTestCaptured.Enqueue(capture);
                return;
            }

            Captured.Enqueue(capture);

            // Printed immediately as well as summarised at the end: the summary proves it happened,
            // but the interleaved position is the only clue about which work was in flight.
            Console.Error.WriteLine(Format(capture, "UNOBSERVED TASK EXCEPTION (detected)"));
        };

        // Runs after every test in the assembly. xunit v2 has no assembly-level fixture, and
        // TestFramework.Dispose is not virtual, so process exit is the reliable "everything is done"
        // hook. The forced collection matters: an unobserved exception is only raised once the dead
        // task is finalized, so without it a task that failed during the last few tests would never
        // be reported at all.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReportSummary();
    }

    private static void ReportSummary()
    {
        // Two passes — the first queues finalizers, WaitForPendingFinalizers runs them (raising the
        // event, which appends to the queue), and the second sweeps whatever they released in turn.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var leaks = Drain();
        if (leaks.Count == 0)
            return;

        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine($"##### {leaks.Count} UNOBSERVED TASK EXCEPTION(S) DURING THIS RUN #####");
        report.AppendLine("Fire-and-forget work threw and nobody observed it. This is a real defect even when");
        report.AppendLine("every test passed: the work did not happen, and the damage typically surfaces later");
        report.AppendLine("as an unrelated test failing.");
        foreach (var leak in leaks)
            report.Append(Format(leak, "UNOBSERVED TASK EXCEPTION (summary)"));

        Console.Error.WriteLine(report.ToString());

        // Also written to disk, because stderr at ProcessExit is not reliably relayed by
        // `dotnet test` — it did not appear even at `-l "console;verbosity=detailed"`. A summary
        // nobody can see is the same as no detector at all, so the file is the dependable channel
        // and the console copy is the convenience.
        TryWriteReportFile(report.ToString());
    }

    // Deliberately best-effort and never throwing: this runs on ProcessExit, where an exception
    // would be an unhelpful way to end an otherwise-green run. The console copy above is the
    // fallback if this fails.
    private static void TryWriteReportFile(string report)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ReportFileName);
            File.WriteAllText(path, report);
            Console.Error.WriteLine($"[UnobservedTaskExceptionDetector] Report written to {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UnobservedTaskExceptionDetector] Could not write report file: {ex.Message}");
        }
    }

    internal const string ReportFileName = "unobserved-task-exceptions.txt";

    // Private on purpose. This empties the real-findings queue, so only the end-of-run summary may
    // call it — a test that drains it destroys the evidence for every test that ran before it, which
    // is the exact defect this class previously had.
    private static IReadOnlyList<Capture> Drain()
    {
        var all = new List<Capture>();
        while (Captured.TryDequeue(out var c))
            all.Add(c);
        return all;
    }

    // Safe for the self-test: touches only the self-test queue.
    internal static IReadOnlyList<Capture> DrainSelfTestCaptures()
    {
        var all = new List<Capture>();
        while (SelfTestCaptured.TryDequeue(out var c))
            all.Add(c);
        return all;
    }

    // Diagnostic only — does NOT dequeue. Lets a test observe the real-findings count without
    // consuming it.
    internal static int PendingFindingCount => Captured.Count;

    internal static bool HasFindingMatching(Func<Capture, bool> match) => Captured.Any(match);

    /// <summary>
    /// Removes findings matching <paramref name="match"/> and returns how many were removed.
    ///
    /// Exists for ONE caller: the regression test that plants a real (unmarked) leak to prove the
    /// self-test does not consume it. That test has to plant a genuine finding — anything less is
    /// unfalsifiable — and must then clean it up so it does not show in the end-of-run report as a
    /// phantom defect. Do not use this to suppress findings.
    /// </summary>
    internal static int RemoveFindings(Func<Capture, bool> match)
    {
        // ConcurrentQueue has no removal, so cycle the whole queue once, re-enqueueing keepers.
        // Safe because ProcessExit is the only other consumer and it cannot run concurrently here.
        var removed = 0;
        var kept = new List<Capture>();
        while (Captured.TryDequeue(out var c))
        {
            if (match(c)) removed++;
            else kept.Add(c);
        }

        foreach (var c in kept)
            Captured.Enqueue(c);

        return removed;
    }

    internal static string Format(Capture capture, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"===== {header} @ {capture.At:O} =====");
        // The AggregateException wrapper is noise; the inner exceptions carry the real origin.
        foreach (var inner in capture.Exception.Flatten().InnerExceptions)
        {
            sb.AppendLine($"  {inner.GetType().FullName}: {inner.Message}");
            if (!string.IsNullOrWhiteSpace(inner.StackTrace))
                sb.AppendLine(inner.StackTrace);
        }
        sb.AppendLine("=====================================================");
        return sb.ToString();
    }
}

/// <summary>
/// Proves the detector actually detects, rather than being dead code that quietly reports nothing
/// forever. Deliberately leaks a faulted task, forces it to be collected, and asserts it was caught.
/// </summary>
[Trait("Category", "Integration")]
public class UnobservedTaskExceptionDetectorTests
{
    [Fact]
    public void Detector_catches_a_faulted_fire_and_forget_task()
    {
        // Draining only the self-test queue. This used to call Drain(), which emptied the shared
        // real-findings queue and threw away every leak detected by the tests that ran earlier.
        UnobservedTaskExceptionDetector.DrainSelfTestCaptures();

        // Faulted inside its own scope so nothing holds a reference once it returns — a local would
        // keep it alive and it would never be finalized.
        static void LeakAFaultedTask() =>
            _ = Task.Run(() => throw new InvalidOperationException(UnobservedTaskExceptionDetector.SelfTestMarker));

        LeakAFaultedTask();

        // The UnobservedTaskException event only fires when the faulted task is *finalized*, and
        // finalizer timing is nondeterministic — markedly more so on Linux CI than on Windows. A
        // single fixed sleep + one GC pass loses that race and the queue comes back empty. Poll a
        // GC/finalize cycle until the capture lands (or a generous budget elapses) instead.
        static bool IsMarker(UnobservedTaskExceptionDetector.Capture c) =>
            c.Exception.Flatten().InnerExceptions
                .Any(e => e.Message.Contains(UnobservedTaskExceptionDetector.SelfTestMarker, StringComparison.Ordinal));

        UnobservedTaskExceptionDetector.Capture? found = null;
        for (var attempt = 0; attempt < 30 && found is null; attempt++)
        {
            Thread.Sleep(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // DrainSelfTestCaptures dequeues, so capture the match before the queue is emptied.
            found = UnobservedTaskExceptionDetector.DrainSelfTestCaptures().FirstOrDefault(IsMarker);
        }

        Assert.NotNull(found);
    }

    /// <summary>
    /// The self-test above leaks a task on purpose. This proves that doing so does not disturb the
    /// real-findings queue — the defect this class shipped with, where the self-test silently
    /// discarded every genuine leak recorded before it and made "no leaks reported" meaningless.
    /// </summary>
    [Fact]
    public void Draining_self_test_captures_preserves_a_real_finding()
    {
        const string realLeakMarker = "real-leak-sentinel-must-survive-the-self-test-drain";

        // Plant a GENUINE (unmarked) leak. This has to be a real finding in the real queue —
        // asserting on counts alone passes trivially when the queue happens to be empty, which is
        // how the first version of this test managed to pass even against the original bug.
        static void LeakARealFaultedTask() =>
            _ = Task.Run(() => throw new InvalidOperationException(realLeakMarker));

        LeakARealFaultedTask();

        static bool IsSentinel(UnobservedTaskExceptionDetector.Capture c) =>
            c.Exception.Flatten().InnerExceptions
                .Any(e => e.Message.Contains(realLeakMarker, StringComparison.Ordinal));

        // The UnobservedTaskException event only fires when the faulted task is *finalized*, and
        // finalizer timing is nondeterministic — markedly more so on Linux CI than on Windows. A
        // single fixed sleep + one GC pass loses that race, so poll a GC/finalize cycle until the
        // planted leak is recorded (HasFindingMatching does NOT consume, so re-checking is safe).
        for (var attempt = 0; attempt < 30 && !UnobservedTaskExceptionDetector.HasFindingMatching(IsSentinel); attempt++)
        {
            Thread.Sleep(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        try
        {
            Assert.True(
                UnobservedTaskExceptionDetector.HasFindingMatching(IsSentinel),
                "Planted leak was never recorded — the detector itself is not working.");

            // The operation the self-test performs. It must not consume real findings.
            UnobservedTaskExceptionDetector.DrainSelfTestCaptures();

            Assert.True(
                UnobservedTaskExceptionDetector.HasFindingMatching(IsSentinel),
                "DrainSelfTestCaptures() consumed a real finding — the detector is destroying "
                + "the evidence it exists to collect, exactly as it did before this was fixed.");
        }
        finally
        {
            // Clean up so this planted leak never appears in the end-of-run report as a phantom.
            UnobservedTaskExceptionDetector.RemoveFindings(IsSentinel);
        }
    }
}
