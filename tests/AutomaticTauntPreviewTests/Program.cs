using System;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static void Main()
    {
        var queue = new AutomaticTauntPreviewQueue(650);
        Equal("Startup attachment schedules no animation render", false, queue.HasWork);
        Equal("An untouched library cannot start a preview", null, queue.TryBegin("PAC/Undisputed2", false, 10000));
        queue.Enqueue("PAC/Undisputed2", 0);
        Equal("A newly selected taunt waits for the debounce interval", null, queue.TryBegin("PAC/Undisputed2", false, 649));
        Equal("Model preparation delays the preview even after debounce", null, queue.TryBegin("PAC/Undisputed2", true, 900));
        Equal("Busy work keeps the requested taunt queued", true, queue.HasWork);
        AutomaticTauntPreviewRequest first = queue.TryBegin("PAC/Undisputed2", false, 1000);
        Equal("Idle completion starts the actual selected request", "PAC/Undisputed2", first.Identity);
        Equal("An active native preview cannot start a second process", null, queue.TryBegin("PAC/Undisputed2", false, 2000));
        queue.Complete(first);
        Equal("One completed selection does not loop or render the library", false, queue.HasWork);

        queue.Enqueue("PAC/First", 3000);
        queue.Enqueue("PAC/Latest", 3100);
        Equal("An earlier rapid selection cannot win", null, queue.TryBegin("PAC/Latest", false, 3749));
        AutomaticTauntPreviewRequest latest = queue.TryBegin("PAC/Latest", false, 3750);
        Equal("Rapid browsing renders only the latest selected taunt", "PAC/Latest", latest.Identity);
        queue.Enqueue("PAC/Next", 3800);
        Equal("A new selection waits for the active native preview", null, queue.TryBegin("PAC/Next", false, 5000));
        queue.Complete(first);
        Equal("A stale completion cannot release another active request", true, queue.IsRunning);
        queue.Complete(latest);
        AutomaticTauntPreviewRequest next = queue.TryBegin("PAC/Next", false, 5000);
        Equal("A selection made during preview runs once afterwards", "PAC/Next", next.Identity);
        queue.Complete(next);

        queue.Enqueue("PAC/Same", 6000);
        queue.Enqueue("PAC/Same", 6200);
        AutomaticTauntPreviewRequest same = queue.TryBegin("PAC/Same", false, 6650);
        Equal("Duplicate mouse and selection events coalesce into one request", "PAC/Same", same.Identity);
        queue.Complete(same);
        queue.Enqueue("PAC/Same", 7000);
        AutomaticTauntPreviewRequest repeated = queue.TryBegin("PAC/Same", false, 7650);
        Equal("Re-selecting a taunt can use the original preview cache", "PAC/Same", repeated.Identity);
        queue.Complete(repeated);

        queue.Enqueue("PAC/OldModel", 8000);
        Equal("A request cannot start for a different selected wrestler", null, queue.TryBegin("Cole/OldModel", false, 9000));
        Equal("A stale model request is discarded", false, queue.HasWork);
        queue.Enqueue("PAC/OldAttire", 10000);
        queue.Enqueue("PAC/NewAttire", 10100);
        Equal("Changed attire/settings use a fresh debounce interval", null, queue.TryBegin("PAC/NewAttire", false, 10749));
        AutomaticTauntPreviewRequest attire = queue.TryBegin("PAC/NewAttire", false, 10750);
        Equal("A queued settings change cannot render old settings", "PAC/NewAttire", attire.Identity);
        queue.Complete(attire);

        queue.Enqueue("PAC/Refresh", 12000);
        queue.CancelPending();
        Equal("A programmatic library refresh cancels queued playback", false, queue.HasWork);
        Equal("A canceled selection cannot start later", null, queue.TryBegin("PAC/Refresh", false, 13000));
        queue.Enqueue("PAC/Manual", 14000);
        queue.CancelPending();
        Equal("A manual Preview or MAKE does not leave a duplicate queued job", false, queue.HasWork);
        queue.Enqueue("PAC/CanceledClose", 14000);
        queue.CancelPending();
        queue.Enqueue("PAC/AfterCanceledClose", 14010);
        AutomaticTauntPreviewRequest resumed = queue.TryBegin("PAC/AfterCanceledClose", false, 14660);
        Equal("A canceled window close permits a later explicit selection", "PAC/AfterCanceledClose", resumed.Identity);
        queue.Complete(resumed);

        queue.Enqueue("PAC/Closing", 15000);
        AutomaticTauntPreviewRequest closing = queue.TryBegin("PAC/Closing", false, 15650);
        queue.Enqueue("PAC/Abandoned", 16000);
        queue.Close();
        queue.Complete(closing);
        Equal("Closing drops queued work after the active renderer completes", false, queue.HasWork);
        queue.Enqueue("PAC/AfterClose", 17000);
        Equal("A closed window cannot restart automatic previews", null, queue.TryBegin("PAC/AfterClose", false, 18000));
        Equal("Closing prevents all later queue work", false, queue.HasWork);
        OutcomeTests();
        Console.WriteLine("PASS " + passed + " automatic taunt preview checks");
    }

    private static void OutcomeTests()
    {
        var stopped = AutomaticTauntPreviewOutcome.FromProcess(1, new[] { "STAGE 2/8 loading", "FileNotFoundError: selected model is unavailable" }, false);
        Equal("A native Go task that returns after renderer failure is not considered successful", false, stopped.Succeeded);
        Equal("The real renderer error replaces the preparation hint", true, stopped.Message.Contains("selected model is unavailable"));
        Equal("A failed exit never wins over an old RESULT line", false,
            AutomaticTauntPreviewOutcome.FromProcess(9, new[] { "RESULT old", "Render failed" }, true).Succeeded);
        var noResult = AutomaticTauntPreviewOutcome.FromProcess(0, new[] { "STAGE 6/6 done" }, true);
        Equal("An exit-zero process without a preview result is reported as unfinished", false, noResult.Succeeded);
        Equal("An incomplete process result has an actionable retry message", true, noResult.Message.Contains("Press Preview to retry"));
        var noImage = AutomaticTauntPreviewOutcome.FromProcess(0, new[] { "RESULT metadata" }, false);
        Equal("Missing output image pixels cannot be presented as a completed preview", false, noImage.Succeeded);
        Equal("Missing output images have a distinct message", true, noImage.Message.Contains("readable preview image"));
        var success = AutomaticTauntPreviewOutcome.FromProcess(0, new[] { "STAGE 9/9 ready", "RESULT metadata" }, true);
        Equal("A successful new preview with real image pixels reaches ready", true, success.Succeeded);
        Equal("A successful cache hit with real image pixels reaches ready", true,
            AutomaticTauntPreviewOutcome.FromProcess(0, new[] { "RESULT cached metadata" }, true).Succeeded);
        Equal("Successful completion never retains a preparation message", false, success.Message.Contains("Preparing"));
        var emptyError = AutomaticTauntPreviewOutcome.FromProcess(7, new string[0], false);
        Equal("An empty failed-process log still exposes its exit code", true, emptyError.Message.Contains("exit 7"));
        Equal("A caught Go exception terminates the preparation state", false, AutomaticTauntPreviewOutcome.Failure("Blender could not start").Succeeded);
        Equal("An empty error cannot create a permanently blank hint", true, AutomaticTauntPreviewOutcome.Failure(null).Message.Contains("Press Preview"));
    }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
}
