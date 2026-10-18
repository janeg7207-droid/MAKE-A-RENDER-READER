using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    /// <summary>Reuses a completed wrestler preview for the taunt browser; starts no rendering work.</summary>
    public static class DiscoveryTauntPreviewCapture
    {
        public static string Capture(string engineRoot, string script, IEnumerable<string> arguments, IEnumerable<string> lines)
        {
            if (string.IsNullOrWhiteSpace(engineRoot) || arguments == null || lines == null) return null;
            string[] args = arguments.ToArray();
            if (args.Length < 2 || !PreviewCache.IsPreviewRequest(script, args)) return null;
            string key = args[1];
            if (key == null || !Regex.IsMatch(key, @"^[A-Za-z0-9_]+$") ||
                (!key.StartsWith("discovery", StringComparison.Ordinal) && !key.Contains("_discovery_"))) return null;
            try
            {
                string resultLine = lines.LastOrDefault(line => line != null && line.StartsWith("RESULT ", StringComparison.Ordinal));
                if (resultLine == null) return null;
                Dictionary<string, object> result = DiscoveryTauntLibrary.JsonObject(resultLine.Substring(7));
                object value;
                if (result.TryGetValue("gmr", out value) && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) return null;
                if (!result.TryGetValue("frames", out value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) <= 0) return null;
                string source = null;
                if (result.TryGetValue("play", out value) && value is string && File.Exists((string)value) &&
                    result.TryGetValue("play_fps", out object fps) && Math.Abs(Convert.ToDouble(fps, CultureInfo.InvariantCulture) - 15) < 0.001)
                    source = (string)value;
                if (source == null && result.TryGetValue("preview", out value) && value is string && File.Exists((string)value)) source = (string)value;
                if (source == null || !Png(source)) return null;
                string directory = Path.Combine(engineRoot, "data", "library", "previews_game");
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
                string target = Path.Combine(directory, key + ".png"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temporary, false);
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return key;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                error is ArgumentException || error is FormatException || error is OverflowException || error is InvalidOperationException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                ) { return null; }
        }

        private static bool Png(string path)
        {
            var expected = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            using (var file = File.OpenRead(path))
            {
                if (file.Length < 33 || file.Length > 512L * 1024 * 1024) return false;
                foreach (byte value in expected) if (file.ReadByte() != value) return false;
                return true;
            }
        }
    }
}
