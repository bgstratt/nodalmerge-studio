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

    private static readonly ConcurrentQueue<Capture> Captured = new();

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
            if (capture.Exception.Flatten().InnerExceptions.Any(
                    x => x.Message.Contains(SelfTestMarker, StringComparison.Ordinal)))
            {
                Captured.Enqueue(capture);
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
    }

    internal static IReadOnlyList<Capture> Drain()
    {
        var all = new List<Capture>();
        while (Captured.TryDequeue(out var c))
            all.Add(c);
        return all;
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
        // Drain first so an unrelated leak from another test cannot make this pass spuriously.
        UnobservedTaskExceptionDetector.Drain();

        // Faulted inside its own scope so nothing holds a reference once it returns — a local would
        // keep it alive and it would never be finalized.
        static void LeakAFaultedTask() =>
            _ = Task.Run(() => throw new InvalidOperationException(UnobservedTaskExceptionDetector.SelfTestMarker));

        LeakAFaultedTask();

        // Give the task a moment to actually fault before forcing collection.
        Thread.Sleep(200);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var caught = UnobservedTaskExceptionDetector.Drain();
        Assert.Contains(caught, c => c.Exception.Flatten().InnerExceptions
            .Any(e => e.Message.Contains(UnobservedTaskExceptionDetector.SelfTestMarker, StringComparison.Ordinal)));
    }
}
