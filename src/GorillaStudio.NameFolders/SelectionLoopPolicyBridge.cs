using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static void SaveSelectionLoopPolicy(object value)
        {
            var result = value as IDictionary<string, object>;
            object path, target;
            int id;
            if (result == null || !result.TryGetValue("gmr", out path) || !(path is string)
                || !result.TryGetValue("id", out target) || target == null || target is bool
                || !int.TryParse(Convert.ToString(target, CultureInfo.InvariantCulture), NumberStyles.None,
                    CultureInfo.InvariantCulture, out id)) return;
            SelectionLoopPolicy.TryWrite((string)path, id, result);
        }

        private static void AttachSelectionLoopPolicyHints(Window owner)
        {
            var freeze = owner.GetType().GetField("EndFreeze", InstanceMembers)?.GetValue(owner) as RadioButton;
            if (freeze != null) freeze.ToolTip = "Plays once, flashes and freezes. With the optional SelectionLoop companion installed, roster selection loops the shorter clean motion before the flash; the match card keeps this ending. MAKE again to create its verified companion policy.";
        }

        private static void InstallSelectionLoopPolicy(Window owner)
        {
            string source = owner.GetType().GetField("LastGmr", InstanceMembers)?.GetValue(owner) as string;
            string game = owner.GetType().GetField("Game", InstanceMembers)?.GetValue(owner) as string;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(game)) return;
            CopyInstalledSelectionLoopPolicy(source, game);
        }

        private static void CopyInstalledSelectionLoopPolicy(string source, string game)
        {
            // The native install/sign-in path has already run. A sidecar is
            // copied only if the installed movie is byte-for-byte the same
            // validated native freeze result; neither GMR is changed here.
            try
            {
                string name = Path.GetFileName(source);
                Match match = Regex.Match(name ?? "", @"\A(\d{1,5})_", RegexOptions.CultureInvariant);
                int id;
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out id)) return;
                string destination = Path.Combine(game, "Plugins", "GorillaRenders", name);
                SelectionLoopPolicy.TryCopy(source, destination, id);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            { /* A companion policy never changes the native install result. */ }
        }
    }
}
