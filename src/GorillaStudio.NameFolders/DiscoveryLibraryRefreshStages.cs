using System;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    /// <summary>Publishes validated available motions before optional archive recovery.</summary>
    public static class DiscoveryLibraryRefreshStages
    {
        public static async Task<TLibrary> RunAsync<TLibrary, TArchive>(
            Func<CancellationToken, Task<TLibrary>> prepareAvailable,
            Func<TLibrary, CancellationToken, Task> publish,
            Func<TLibrary, bool> needsArchive,
            Func<TLibrary, CancellationToken, Task<TArchive>> readArchive,
            Func<TArchive, bool> hasRecovery,
            Func<TLibrary, TArchive, CancellationToken, Task<TLibrary>> enrich,
            CancellationToken token)
        {
            if (prepareAvailable == null || publish == null || needsArchive == null
                || readArchive == null || hasRecovery == null || enrich == null) throw new ArgumentNullException("stage");
            token.ThrowIfCancellationRequested();
            TLibrary available = await prepareAvailable(token);
            token.ThrowIfCancellationRequested();
            await publish(available, token);
            token.ThrowIfCancellationRequested();
            if (!needsArchive(available)) return available;
            TArchive archive = await readArchive(available, token);
            token.ThrowIfCancellationRequested();
            if (!hasRecovery(archive)) return available;
            TLibrary enriched = await enrich(available, archive, token);
            token.ThrowIfCancellationRequested();
            await publish(enriched, token);
            token.ThrowIfCancellationRequested();
            return enriched;
        }
    }
}
