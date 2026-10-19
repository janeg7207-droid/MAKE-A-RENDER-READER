using System;
using System.Globalization;
using System.IO;

namespace GorillaStudio.NameFolders
{
    /// <summary>Blocks rendering after a source load and its restoration both fail.</summary>
    public sealed class AttireSourceState
    {
        public string Failure { get; private set; }
        public bool CanRender { get { return Failure == null; } }

        public void RestoreFailed(string detail)
        {
            Failure = "The attire source could not be loaded or restored. Select the wrestler again before rendering."
                + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail);
        }

        // Only a native selection-completion hook calls this; a staging seed or
        // changing a dropdown cannot unlock a partly loaded character.
        public bool ConfirmLoaded(string expectedAlias, string actualFolder, int logicalId, string actualTarget)
        {
            if (string.IsNullOrWhiteSpace(expectedAlias) || string.IsNullOrWhiteSpace(actualFolder)
                || logicalId < 1 || logicalId > 65535
                || actualTarget != logicalId.ToString(CultureInfo.InvariantCulture)
                || !string.Equals(Path.GetFullPath(expectedAlias).TrimEnd('\\', '/'),
                    Path.GetFullPath(actualFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            Failure = null;
            return true;
        }

        public void RequireRender(string script)
        {
            if (Failure != null && AlternateAttires.IsRender(script)) throw new InvalidOperationException(Failure);
        }
    }
}
