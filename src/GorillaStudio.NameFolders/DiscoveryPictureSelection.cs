using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GorillaStudio.NameFolders
{
    /// <summary>Shows the selected logical slot's current Discovery picture references before rendering.</summary>
    public static class DiscoveryPictureSelection
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // This is reference information, independent of whether image pixels are loose,
        // packed in a CAK, or currently decoded. Never equate a missing filename in the
        // native stock-name list with a missing Discovery picture reference.
        public static bool Apply(object owner, string actualTable)
        {
            if (owner == null || string.IsNullOrWhiteSpace(actualTable)) return false;
            Type type = owner.GetType();
            int target;
            if (!TryId(type.GetField("TargetSlot", Members)?.GetValue(owner) as string, out target)) return false;
            object render = type.GetField("WRender", Members)?.GetValue(owner);
            PropertyInfo text = render?.GetType().GetProperty("Text");
            if (text == null || !text.CanWrite || text.PropertyType != typeof(string)) return false;

            Dictionary<int, List<ulong>> index;
            try { index = DiscoveryRenderLinks.Read(actualTable); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException || error is OverflowException
                || error is System.Security.SecurityException)
            { return false; }

            PropertyInfo foreground = render.GetType().GetProperty("Foreground");
            object previousBrush = foreground != null && foreground.CanRead ? foreground.GetValue(render, null) : null;
            object normalBrush = Resource(owner, "Text") ?? previousBrush;
            object missingBrush = Resource(owner, "Amber") ?? previousBrush;

            // Native ShowTarget restores OwnRenderText when returning to the physical
            // model slot. Keep that cache about the physical slot, never the remapped one.
            int physical;
            if (TryId(type.GetField("FolderSlot", Members)?.GetValue(owner) as string, out physical))
            {
                type.GetField("OwnRenderText", Members)?.SetValue(owner, Describe(index, physical));
                SetField(owner, "OwnRenderBrush", HasLinks(index, physical) ? normalBrush : missingBrush);
            }

            text.SetValue(render, Describe(index, target), null);
            if (foreground != null && foreground.CanWrite)
            {
                object brush = HasLinks(index, target) ? normalBrush : missingBrush;
                if (brush == null || foreground.PropertyType.IsInstanceOfType(brush)) foreground.SetValue(render, brush, null);
            }
            return true;
        }

        private static string Describe(Dictionary<int, List<ulong>> index, int id)
        {
            List<ulong> hashes;
            if (!index.TryGetValue(id, out hashes))
                return "Wrestler " + id.ToString(CultureInfo.InvariantCulture) + " is not in the current Discovery roster.";
            if (hashes.Count == 0)
                return "No roster picture reference in Discovery for wrestler " + id.ToString(CultureInfo.InvariantCulture) + ".";

            string text = "Roster picture linked in Discovery\n" + string.Join(", ", hashes.OrderBy(hash => hash)
                .Select(hash => hash.ToString("X16", CultureInfo.InvariantCulture)));
            var selected = new HashSet<ulong>(hashes);
            int[] shared = index.Where(pair => pair.Key != id && pair.Value.Any(selected.Contains))
                .Select(pair => pair.Key).OrderBy(value => value).ToArray();
            if (shared.Length > 0) text += "\nAlso linked to wrestler " + string.Join(", ", shared.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            return text;
        }

        private static bool HasLinks(Dictionary<int, List<ulong>> index, int id)
        {
            List<ulong> hashes;
            return index.TryGetValue(id, out hashes) && hashes.Count > 0;
        }

        private static bool TryId(string value, out int id)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 1 && id <= 65535;
        }

        private static object Resource(object owner, string key)
        {
            MethodInfo find = owner.GetType().GetMethod("TryFindResource", Members, null, new[] { typeof(object) }, null);
            return find?.Invoke(owner, new object[] { key });
        }

        private static void SetField(object owner, string name, object value)
        {
            FieldInfo field = owner.GetType().GetField(name, Members);
            if (field != null && (value == null || field.FieldType.IsInstanceOfType(value))) field.SetValue(owner, value);
        }
    }
}
