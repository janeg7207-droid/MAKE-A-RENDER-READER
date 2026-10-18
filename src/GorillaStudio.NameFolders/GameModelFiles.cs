using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    internal static class GameModelFiles
    {
        internal static void ValidateKey(string key, IEnumerable<string> knownKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || key != key.Trim() || key == "." || key == ".."
                || key.Any(character => character < 32) || key.IndexOfAny(new[] { '/', '\\', ':', '"' }) >= 0)
                throw new InvalidDataException("This wrestler has no valid game-model key. Choose the wrestler's actual model folder instead.");
            if (knownKeys == null || !knownKeys.Contains(key, StringComparer.Ordinal))
                throw new InvalidDataException("This model is not in the supplied game's stock-model index. Choose the wrestler's actual mod folder instead.");
        }

        internal static string StockDirectory(int exitCode, IEnumerable<string> output)
        {
            List<string> lines = output == null ? new List<string>() : output.Where(line => line != null).ToList();
            string directory = lines.Where(line => line.StartsWith("STOCKDIR ", StringComparison.Ordinal))
                .Select(line => line.Substring(9).Trim()).LastOrDefault();
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return Path.GetFullPath(directory);
            string reason = lines.LastOrDefault(line => line.Length > 0 && !char.IsWhiteSpace(line[0])
                && !line.Contains("Traceback") && !line.Contains("File \"")
                && !line.StartsWith("EXTRACT ", StringComparison.Ordinal)
                && !line.StartsWith("STOCKDIR ", StringComparison.Ordinal));
            if (reason == null)
                reason = exitCode == 0 ? "The extractor did not return an existing model folder."
                    : "The game-model extractor stopped with code " + exitCode + ".";
            throw new InvalidOperationException("The model could not be loaded from the game files: " + reason);
        }
    }
}
