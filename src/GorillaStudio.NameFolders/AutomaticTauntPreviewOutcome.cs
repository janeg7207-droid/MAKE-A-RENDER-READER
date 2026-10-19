using System;
using System.Collections.Generic;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    /// <summary>The renderer's result, rather than completion of its UI Task.</summary>
    public sealed class AutomaticTauntPreviewOutcome
    {
        public bool Succeeded { get; private set; }
        public string Message { get; private set; }

        public static AutomaticTauntPreviewOutcome FromProcess(int exitCode, IEnumerable<string> lines, bool readableImage)
        {
            string[] output = (lines ?? new string[0]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            if (exitCode != 0)
            {
                string error = output.LastOrDefault(line => !line.StartsWith("RESULT ", StringComparison.Ordinal)
                    && !line.StartsWith("STAGE ", StringComparison.Ordinal) && !line.StartsWith("FRAMES ", StringComparison.Ordinal)
                    && !line.StartsWith(" ", StringComparison.Ordinal) && !line.StartsWith("Traceback", StringComparison.Ordinal));
                return Failure("Preview stopped" + (error == null ? " (exit " + exitCode + ")" : ": " + error));
            }
            if (!output.Any(line => line.StartsWith("RESULT ", StringComparison.Ordinal)))
                return Failure("The renderer returned no preview result. Press Preview to retry.");
            if (!readableImage) return Failure("The renderer returned no readable preview image. Press Preview to retry.");
            return new AutomaticTauntPreviewOutcome { Succeeded = true, Message = "Your wrestler preview is ready below." };
        }

        public static AutomaticTauntPreviewOutcome Failure(string message)
        { return new AutomaticTauntPreviewOutcome { Message = string.IsNullOrWhiteSpace(message) ? "Preview did not finish. Press Preview to retry." : message }; }
    }
}
