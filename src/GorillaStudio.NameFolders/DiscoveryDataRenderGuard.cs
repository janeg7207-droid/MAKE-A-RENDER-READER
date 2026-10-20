using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        static partial void CheckDiscoveryDataRender(object owner, string script,
            IEnumerable<string> arguments, ref string failure);

        private static string DiscoveryDataProcessFailure(object owner, string script, IEnumerable<string> arguments)
        {
            string failure = null;
            CheckDiscoveryDataRender(owner, script, arguments, ref failure);
            return failure;
        }

        internal static bool DiscoveryDataPreparationRequest(string script, IEnumerable<string> arguments)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension((script ?? "").Replace('\\', '/')),
                "gorilla_render", StringComparison.OrdinalIgnoreCase) || arguments == null) return false;
            bool preparation = false;
            foreach (string argument in arguments)
            {
                if (argument == null) continue;
                if (argument == "quick" || argument.StartsWith("quick=", StringComparison.Ordinal)) return false;
                if (argument == "prepare" || argument.StartsWith("prepare=", StringComparison.Ordinal))
                {
                    if (argument != "prepare" && argument != "prepare=model" && argument != "prepare=taunt") return false;
                    preparation = true;
                }
            }
            return preparation;
        }
    }
}
