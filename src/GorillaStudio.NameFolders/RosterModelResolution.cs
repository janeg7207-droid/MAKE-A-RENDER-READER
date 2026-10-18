using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    public enum RosterModelState { Ready, ChooseMatchingModel, InstalledGame, Missing }

    public sealed class RosterModelResolution
    {
        public RosterModelState State { get; private set; }
        public string Folder { get; private set; }
        public int SourceId { get; private set; }
        public bool ExistingAssociation { get; private set; }
        public List<ModelCandidate> Choices { get; private set; }

        private RosterModelResolution() { Choices = new List<ModelCandidate>(); }

        // Resolves the roster row before opening any file picker. In particular, the
        // profile's logical ID is never treated as proof of its physical stock body.
        public static RosterModelResolution Resolve(RosterEntry entry,
            IEnumerable<ModelCandidate> inventory, string rosterTable)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.Id < 1 || entry.Id > 65535)
                throw new ArgumentException("Choose a wrestler from the roster first.", "entry");
            if (HasModel(entry.Folder) && !CharacterFolders.HasAmbiguousChildren(entry.Folder))
                return Ready(entry.Folder, CharacterFolders.PhysicalId(entry.Folder) ?? entry.SourceId, true);

            List<ModelCandidate> mapped;
            if (MappedModelLibrary.FindMatches(new[] { entry }, inventory, rosterTable).TryGetValue(entry, out mapped))
            {
                mapped = mapped.Where(candidate => HasModel(candidate.Folder)
                    && !CharacterFolders.HasAmbiguousChildren(candidate.Folder)).ToList();
                if (mapped.Count == 1) return Ready(mapped[0].Folder,
                    CharacterFolders.PhysicalId(mapped[0].Folder) ?? mapped[0].SourceId, false);
                if (mapped.Count > 1)
                    return new RosterModelResolution { State = RosterModelState.ChooseMatchingModel, Choices = mapped };
                // A mapping without available extracted files is still stronger than
                // an unrelated plain-name or inherited physical-ID fallback.
                return new RosterModelResolution {
                    State = string.IsNullOrWhiteSpace(entry.StockFolder) ? RosterModelState.Missing : RosterModelState.InstalledGame
                };
            }

            var copy = new RosterEntry {
                Id = entry.Id, Name = entry.Name, SourceId = entry.SourceId,
                Source = entry.Source, StockFolder = entry.StockFolder,
                RosterTable = entry.RosterTable, IsMod = entry.IsMod
            };
            ModelLibraryBindingResult binding = ModelLibraryBinding.Apply(new List<RosterEntry> { copy }, inventory, rosterTable, false);
            if (binding.BoundCount == 1 && HasModel(copy.Folder) && !CharacterFolders.HasAmbiguousChildren(copy.Folder))
                return Ready(copy.Folder, CharacterFolders.PhysicalId(copy.Folder) ?? copy.SourceId, false);

            List<ModelCandidate> choices;
            if (binding.Choices.TryGetValue(copy, out choices))
            {
                choices = choices.Where(candidate => HasModel(candidate.Folder)
                    && !CharacterFolders.HasAmbiguousChildren(candidate.Folder)).ToList();
                if (choices.Count == 1) return Ready(choices[0].Folder,
                    CharacterFolders.PhysicalId(choices[0].Folder) ?? choices[0].SourceId, false);
                if (choices.Count > 1)
                    return new RosterModelResolution { State = RosterModelState.ChooseMatchingModel, Choices = choices };
            }
            return new RosterModelResolution {
                State = string.IsNullOrWhiteSpace(entry.StockFolder) ? RosterModelState.Missing : RosterModelState.InstalledGame
            };
        }

        private static RosterModelResolution Ready(string folder, int sourceId, bool existing)
        {
            return new RosterModelResolution { State = RosterModelState.Ready, Folder = Path.GetFullPath(folder),
                SourceId = sourceId > 0 && sourceId <= 65535 ? sourceId : 0, ExistingAssociation = existing };
        }

        private static bool HasModel(string folder)
        {
            if (!Directory.Exists(folder)) return false;
            try { return ModelLibrary.Scan(folder).Count > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
    }
}
