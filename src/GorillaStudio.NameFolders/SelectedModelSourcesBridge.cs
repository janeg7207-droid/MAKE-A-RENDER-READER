using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly object selectedModelSourcesLock = new object();
        private static string SelectedModelSourcesPath { get { return Path.Combine(StorageDirectory, "selected-model-sources.json"); } }

        internal static List<ModelSourceAssociation> ReadSelectedModelAssociations()
        {
            lock (selectedModelSourcesLock)
            {
                try { return ModelSourceAssociations.Load(SelectedModelSourcesPath); }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)
                { throw new InvalidDataException("The saved model-pack choices could not be read. Choose this wrestler's model folders again. " + error.Message, error); }
            }
        }

        internal static List<ModelCandidate> SelectedModelSources(RosterEntry entry, string context)
        { return ModelSourceAssociations.Sources(ReadSelectedModelAssociations(), entry, context); }

        internal static void RememberSelectedModelSources(RosterEntry entry, string context, IEnumerable<ModelCandidate> models)
        {
            lock (selectedModelSourcesLock)
            {
                List<ModelSourceAssociation> records;
                try { records = ReadSelectedModelAssociations(); }
                catch (InvalidDataException)
                {
                    // An explicit replacement can repair preferences; retain
                    // the unreadable file so no previous choices are destroyed.
                    if (File.Exists(SelectedModelSourcesPath)) File.Copy(SelectedModelSourcesPath,
                        SelectedModelSourcesPath + ".unreadable-" + Guid.NewGuid().ToString("N"));
                    records = new List<ModelSourceAssociation>();
                }
                ModelSourceAssociations.Save(SelectedModelSourcesPath, ModelSourceAssociations.Replace(records, entry, context, models));
            }
        }
    }
}
