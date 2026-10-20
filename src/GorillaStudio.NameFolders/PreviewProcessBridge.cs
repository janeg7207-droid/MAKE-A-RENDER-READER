using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        static partial void ObservePlaybackSetup(object owner, Process process, string script, IEnumerable<string> args);
        static partial void ConfigureAlternateAttireProcess(object owner, Process process, string script, IEnumerable<string> args);

        // Called immediately before the original engine process starts. The engine
        // appends GORILLA_EXTRA after its renderer arguments, so these last values
        // override the selected detail setting without changing animation timing.
        public static void ConfigurePreviewProcess(object owner, object process, string script, IEnumerable<string> args)
        {
            var nativeProcess = process as Process;
            if (nativeProcess == null) return;
            string loadingFailure = DiscoveryDataProcessFailure(owner, script, args);
            if (loadingFailure != null) throw new InvalidOperationException(loadingFailure);
            ConfigureAlternateAttireProcess(owner, nativeProcess, script, args);
            ObservePlaybackSetup(owner, nativeProcess, script, args);
            DiscoveryTauntBridge.ConfigureProcess(owner, nativeProcess, script, args);
            if (!string.IsNullOrWhiteSpace(script) && string.Equals(Path.GetFileNameWithoutExtension(script.Replace('\\', '/')),
                "gorilla_render", StringComparison.OrdinalIgnoreCase))
            {
                nativeProcess.StartInfo.EnvironmentVariables.Remove("GORILLA_HAIR_CHOICE");
                if (args != null)
                    foreach (string argument in args)
                        if (argument != null && argument.StartsWith("hair=", StringComparison.Ordinal))
                            nativeProcess.StartInfo.EnvironmentVariables["GORILLA_HAIR_CHOICE"] =
                                string.Equals(argument.Substring(5), "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "selected";
            }
            string setting;
            if (PreviewCache.IsPreviewRequest(script, args))
            {
                if (!FastPreviewEnabled) return;
                setting = "res=256 samples=8";
            }
            else
            {
                if (!FastRenderEnabled || !IsFullRenderRequest(script, args)) return;
                setting = RosterSpeedEnabled ? "samples=8" : "samples=32";
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
