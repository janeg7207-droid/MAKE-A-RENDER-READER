using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class RandyPreviewBuildItem
    {
        public string Key { get; private set; }
        public string Name { get; private set; }
        public RandyPreviewBuildItem(string key, string name)
        {
            Key = key;
            Name = string.IsNullOrWhiteSpace(name) ? key : name;
        }
    }

    public sealed class RandyPreviewBuildSummary
    {
        public int Total { get; private set; }
        public int Completed { get; private set; }
        public int Cached { get; private set; }
        public int Generated { get; private set; }
        public int Failed { get; private set; }
        public bool Canceled { get; private set; }
        internal RandyPreviewBuildSummary(int total, int cached, int generated, int failed, bool canceled)
        {
            Total = total; Cached = cached; Generated = generated; Failed = failed;
            Completed = cached + generated + failed; Canceled = canceled;
        }
    }

    public sealed class RandyPreviewBuildProgress
    {
        public int Total { get; private set; }
        public int Completed { get; private set; }
        public int Cached { get; private set; }
        public int Generated { get; private set; }
        public int Failed { get; private set; }
        public string Key { get; private set; }
        public string Name { get; private set; }
        public string Message { get; private set; }
        public bool Finished { get; private set; }
        public bool Canceled { get; private set; }
        internal RandyPreviewBuildProgress(RandyPreviewBuildSummary summary, RandyPreviewBuildItem item, string message, bool finished)
        {
            Total = summary.Total; Completed = summary.Completed; Cached = summary.Cached;
            Generated = summary.Generated; Failed = summary.Failed; Canceled = summary.Canceled;
            Key = item?.Key; Name = item?.Name; Message = message; Finished = finished;
        }
    }

    /// <summary>Builds only missing previews, serially, using the normal validated Randy cache.</summary>
    public static class RandyPreviewBuilder
    {
        public static IReadOnlyList<RandyPreviewBuildItem> Plan(IEnumerable<RandyPreviewBuildItem> items)
        {
            if (items == null) throw new ArgumentNullException("items");
            var result = new List<RandyPreviewBuildItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int rows = 0;
            foreach (var item in items)
            {
                if (++rows > 20000) throw new InvalidDataException("The taunt list exceeds the preview builder's size limit.");
                if (item == null || !RandyTauntPreviewState.ValidKey(item.Key) || !seen.Add(item.Key)) continue;
                if (result.Count >= 10000) throw new InvalidDataException("Too many playable taunts for one preview build.");
                result.Add(item);
            }
            return result.AsReadOnly();
        }

        public static async Task<RandyPreviewBuildSummary> RunAsync(IEnumerable<RandyPreviewBuildItem> items,
            Func<RandyPreviewBuildItem, CancellationToken, Task<bool>> cacheReady,
            Func<RandyPreviewBuildItem, CancellationToken, Task> generate,
            Action<RandyPreviewBuildProgress> progress, CancellationToken cancellationToken)
        {
            if (cacheReady == null) throw new ArgumentNullException("cacheReady");
            if (generate == null) throw new ArgumentNullException("generate");
            IReadOnlyList<RandyPreviewBuildItem> plan = Plan(items);
            int cached = 0, generated = 0, failed = 0;
            Func<bool, RandyPreviewBuildSummary> summary = canceled =>
                new RandyPreviewBuildSummary(plan.Count, cached, generated, failed, canceled);
            if (cancellationToken.IsCancellationRequested) return summary(true);
            Action<RandyPreviewBuildItem, string, bool> report = (item, message, finished) => {
                if (progress == null) return;
                var snapshot = new RandyPreviewBuildProgress(summary(cancellationToken.IsCancellationRequested), item, message, finished);
                try { progress(snapshot); }
                catch { /* Display failure must not abandon owned renderer cleanup. */ }
            };
            report(null, "Checking saved Randy previews…", false);
            foreach (var item in plan)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    report(item, "Checking saved preview…", false);
                    cancellationToken.ThrowIfCancellationRequested();
                    bool ready = await cacheReady(item, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ready) cached++;
                    else
                    {
                        report(item, "Rendering missing Randy preview…", false);
                        cancellationToken.ThrowIfCancellationRequested();
                        // The callback returns only after its owned processes
                        // have stopped and the validated cache was published.
                        await generate(item, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        generated++;
                    }
                    report(item, ready ? "Saved preview ready." : "New preview saved.", false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    failed++;
                    string message = new string((error.Message ?? "Preview could not be created.")
                        .Select(character => char.IsControl(character) ? ' ' : character).Take(480).ToArray());
                    report(item, "Preview failed: " + message, false);
                }
            }
            var result = summary(cancellationToken.IsCancellationRequested);
            report(null, result.Canceled ? "Stopped. Saved previews are kept; build again to continue."
                : "Finished. Saved previews are ready for browsing.", true);
            return result;
        }
    }
}
