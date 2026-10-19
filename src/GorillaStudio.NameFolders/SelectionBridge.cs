using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        [ThreadStatic] private static PendingRosterSelection pendingRosterSelection;

        internal static void RegisterSelection(string prepared, RosterEntry selected)
        {
            if (string.IsNullOrWhiteSpace(prepared)) throw new ArgumentException("A prepared character folder is required.", "prepared");
            if (selected == null) throw new ArgumentNullException("selected");
            if (selected.Id < 1 || selected.Id > 65535) throw new ArgumentOutOfRangeException("selected", "The wrestler ID must be between 1 and 65535.");
            pendingRosterSelection = new PendingRosterSelection
            {
                Folder = SelectionPath(prepared),
                Id = selected.Id,
                Name = string.IsNullOrWhiteSpace(selected.Name) ? "Wrestler " + selected.Id.ToString(CultureInfo.InvariantCulture) : selected.Name.Trim(),
                RosterTable = selected.RosterTable
            };
        }

        // Called inside the original folder-loading method immediately before its final
        // Prepare call. The model folder keeps its physical ID; SetTarget supplies slot=.
        public static void ApplySelection(object owner)
        {
            PendingRosterSelection selected = pendingRosterSelection;
            if (selected == null || owner == null) return;
            Type type = owner.GetType();
            var folder = type.GetField("Folder", InstanceMembers);
            string current = folder == null ? null : folder.GetValue(owner) as string;
            if (string.IsNullOrWhiteSpace(current) ||
                !string.Equals(SelectionPath(current), selected.Folder, StringComparison.OrdinalIgnoreCase)) return;
            // Consume only a matching load, before invoking UI code that may raise events.
            pendingRosterSelection = null;
            AddNativeSlotName(owner, selected);

            var physicalField = type.GetField("FolderSlot", InstanceMembers);
            string physical = physicalField == null ? null : physicalField.GetValue(owner) as string;
            int physicalId;
            if (int.TryParse(physical, NumberStyles.None, CultureInfo.InvariantCulture, out physicalId) && physicalId == selected.Id)
            {
                // NameOf(id) prefers WName over SlotNames for a model in its own slot.
                // Refresh that heading from the selected roster before SetTarget redraws it.
                var headingField = type.GetField("WName", InstanceMembers);
                object heading = headingField == null ? null : headingField.GetValue(owner);
                PropertyInfo text = heading == null ? null : heading.GetType().GetProperty("Text");
                if (text != null && text.CanWrite && text.PropertyType == typeof(string))
                    text.SetValue(heading, selected.Name, null);
            }
            var setTarget = type.GetMethod("SetTarget", InstanceMembers, null, new[] { typeof(string) }, null);
            if (setTarget == null) throw new MissingMethodException("This Gorilla Studio version cannot select a mapped wrestler slot.");
            setTarget.Invoke(owner, new object[] { selected.Id.ToString(CultureInfo.InvariantCulture) });
            DiscoveryPictureSelection.Apply(owner, selected.RosterTable);
            SeedDiscoveryAttireForSelection(owner, current, selected.Id);
            OnRosterSelectionApplied(owner);
        }

        static partial void OnRosterSelectionApplied(object owner);
        static partial void SeedDiscoveryAttireForSelection(object owner, string current, int logicalId);

        private static void AddNativeSlotName(object owner, PendingRosterSelection selected)
        {
            var field = owner.GetType().GetField("SlotNames", InstanceMembers);
            var names = field == null ? null : field.GetValue(owner) as IList;
            if (names == null) throw new MissingFieldException("This Gorilla Studio version has no supported wrestler-name list.");
            Type[] arguments = field.FieldType.GetGenericArguments();
            if (arguments.Length != 1) throw new InvalidOperationException("The native wrestler-name list has an unsupported shape.");
            Type tuple = arguments[0];
            FieldInfo id = tuple.GetField("Item2");
            FieldInfo name = tuple.GetField("Item1");
            if (id == null || id.FieldType != typeof(int) || name == null || name.FieldType != typeof(string))
                throw new InvalidOperationException("The native wrestler-name tuple has an unsupported shape.");
            object entry = Activator.CreateInstance(tuple, new object[] { selected.Name, selected.Id });
            for (int index = 0; index < names.Count; index++)
            {
                if ((int)id.GetValue(names[index]) != selected.Id) continue;
                names[index] = entry;
                return;
            }
            names.Add(entry);
        }

        private static string SelectionPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private sealed class PendingRosterSelection
        {
            internal string Folder;
            internal int Id;
            internal string Name;
            internal string RosterTable;
        }
    }
}
