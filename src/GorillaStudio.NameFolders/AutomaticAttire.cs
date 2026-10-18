using System;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    public sealed class DirectAttirePlan
    {
        public bool WrapDirectModel { get; internal set; }
        public string BaseModelFolder { get; internal set; }
        public string CacheSuffix { get; internal set; }
    }

    /// <summary>
    /// Keeps the original engine's automatic default-attire policy. Its native
    /// scan sees MCDs one child directory deep, so a direct-MCD source needs a
    /// compatibility directory containing a Default_Attire link to that source.
    /// This class plans that layout without creating links or changing assets.
    /// </summary>
    public static class AutomaticAttire
    {
        public static bool HasNativeAttire(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) return false;
            return Directory.GetDirectories(source).Any(child =>
                !string.Equals(Path.GetFileName(child), "BaseModel", StringComparison.OrdinalIgnoreCase)
                && HasDirectMcd(child));
        }

        public static DirectAttirePlan Plan(string source, int sourceId)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                throw new DirectoryNotFoundException("Choose an existing wrestler model folder.");
            if (sourceId < 1 || sourceId > 65535)
                throw new ArgumentOutOfRangeException("sourceId", "The physical model ID must be between 1 and 65535.");

            string fullSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool wrap = !string.Equals(Path.GetFileName(fullSource), "BaseModel", StringComparison.OrdinalIgnoreCase)
                && !HasNativeAttire(fullSource)
                && Directory.GetFiles(fullSource).Any(path => IsMcd(path)
                    && !string.Equals(Path.GetFileName(path), "baseModel.mcd", StringComparison.OrdinalIgnoreCase));
            string body = null;
            if (wrap)
            {
                body = BodyFolder(fullSource);
                DirectoryInfo parent = Directory.GetParent(fullSource);
                // An explicit attire folder may sit next to its character's
                // BaseModel. Never borrow a neighbouring body merely because it
                // exists: the same physical model ID must prove ownership.
                if (body == null && parent != null && CharacterFolders.PhysicalId(parent.FullName) == sourceId)
                    body = BodyFolder(parent.FullName);
            }
            return new DirectAttirePlan
            {
                WrapDirectModel = wrap,
                BaseModelFolder = body,
                CacheSuffix = wrap ? "direct-v1" : ""
            };
        }

        private static bool HasDirectMcd(string folder)
        {
            return Directory.GetFiles(folder).Any(IsMcd);
        }

        private static bool IsMcd(string path)
        {
            return string.Equals(Path.GetExtension(path), ".mcd", StringComparison.OrdinalIgnoreCase);
        }

        private static string BodyFolder(string source)
        {
            return Directory.GetDirectories(source).FirstOrDefault(child =>
                string.Equals(Path.GetFileName(child), "BaseModel", StringComparison.OrdinalIgnoreCase)
                && Directory.GetFiles(child).Any(path =>
                    string.Equals(Path.GetFileName(path), "baseModel.mcd", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
