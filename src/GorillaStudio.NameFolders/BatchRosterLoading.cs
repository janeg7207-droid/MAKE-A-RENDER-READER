using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    /// <summary>Loads current batch metadata without requiring complete localization or a model-tree scan.</summary>
    public static class BatchRosterLoading
    {
        public static RosterSessionData Open(string discoveryRoot, IEnumerable<RosterEntry> saved,
            string rosterFile, string bundledRosterFile, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the same current-file ownership and chosen-model validation as
            // the main picker. Its warning collector preserves good SDB entries
            // and real IDs when another localization row cannot be read.
            RosterSessionData session = RosterLaunch.OpenQuick(discoveryRoot, saved, rosterFile, bundledRosterFile);
            cancellationToken.ThrowIfCancellationRequested();
            session.Entries = session.Entries.Where(entry => entry != null && entry.Id > 0 && entry.Id <= 65535
                && !string.IsNullOrWhiteSpace(entry.Name))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }

        public static string LoadingStatus(RosterSessionData session)
        {
            return Count(session) + " roster entries loaded. Indexing remembered model locations…" + Warnings(session);
        }

        public static string ReadyStatus(RosterSessionData session, int modelCount)
        {
            if (modelCount < 0) throw new ArgumentOutOfRangeException("modelCount");
            return Count(session) + " roster entries · " + modelCount.ToString(CultureInfo.InvariantCulture)
                + " model folders indexed. Select wrestlers above." + Warnings(session);
        }

        private static string Count(RosterSessionData session)
        {
            if (session == null) throw new ArgumentNullException("session");
            return session.Entries.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string Warnings(RosterSessionData session)
        {
            if (session.Warnings == null || session.Warnings.Count == 0) return "";
            return "\n\nRoster name warnings (valid names and wrestler IDs were kept):\n"
                + string.Join("\n", session.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning))
                    .Distinct(StringComparer.Ordinal));
        }
    }
}
