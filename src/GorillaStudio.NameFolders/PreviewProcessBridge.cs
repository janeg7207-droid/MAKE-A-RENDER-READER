using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        // Called immediately before the original engine process starts. The engine
        // appends GORILLA_EXTRA after its renderer arguments, so these last values
        // override the selected detail setting without changing animation timing.
        public static void ConfigurePreviewProcess(object owner, object process, string script, IEnumerable<string> args)
        {
            var nativeProcess = process as Process;
            if (nativeProcess == null) return;
            string setting;
            if (PreviewCache.IsPreviewRequest(script, args))
            {
                if (!FastPreviewEnabled) return;
                setting = "res=256 samples=8";
            }
            else
            {
                if (!FastRenderEnabled || !IsFullRenderRequest(script, args)) return;
                setting = "samples=32";
            }

            string extra = nativeProcess.StartInfo.EnvironmentVariables["GORILLA_EXTRA"];
            nativeProcess.StartInfo.EnvironmentVariables["GORILLA_EXTRA"] =
                (string.IsNullOrWhiteSpace(extra) ? "" : extra.Trim() + " ") + setting;
        }

        private static bool IsFullRenderRequest(string script, IEnumerable<string> args)
        {
            if (string.IsNullOrWhiteSpace(script) || args == null ||
                !string.Equals(Path.GetFileNameWithoutExtension(script.Replace('\\', '/')), "gorilla_render", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (string argument in args)
                if (argument != null && (argument == "prepare" || argument.StartsWith("prepare=", StringComparison.Ordinal)))
                    return false;
            return !PreviewCache.IsPreviewRequest(script, args);
        }
    }
}
