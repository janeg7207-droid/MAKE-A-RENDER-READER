using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public sealed class RandyPreviewAssetStatus
    {
        public string Directory { get; internal set; }
        public IReadOnlyList<string> MissingPaths { get; internal set; }
        public bool IsReady { get { return MissingPaths.Count == 0; } }
    }

    /// <summary>Exact stock Randy sources and fixed quick-preview options, independent of the selected wrestler.</summary>
    public static class RandyPreviewAssets
    {
        public const string StockFolderKey = "161_Randy_Orton_2019";
        public const string DefaultAttire = "161_Default_Attire/161_Attire.mcd";
        public const int LogicalId = 161;

        // Measured against the original v1.0.2 stock_index.json. The native
        // extractor converts every indexed .tex to a sibling .dds in its cache.
        private static readonly string[] IndexedPaths = {
            "161_Default_Attire/161_Attire.mcd", "161_Default_Attire/161_Attire.mtls",
            "161_Default_Attire/Materials/jacket.jmtl", "161_Default_Attire/Materials/l_a_acce.jmtl",
            "161_Default_Attire/Materials/l_kneepad.jmtl", "161_Default_Attire/Materials/r_a_acce.jmtl",
            "161_Default_Attire/Materials/r_kneepad.jmtl", "161_Default_Attire/Materials/shoes.jmtl",
            "161_Default_Attire/Materials/underwear.jmtl",
            "161_Default_Attire/Textures/jacket_color.tex", "161_Default_Attire/Textures/jacket_nrm.tex",
            "161_Default_Attire/Textures/l_a_acce_color.tex", "161_Default_Attire/Textures/l_a_acce_nrm.tex",
            "161_Default_Attire/Textures/l_kneepad_color.tex", "161_Default_Attire/Textures/l_kneepad_nrm.tex",
            "161_Default_Attire/Textures/r_a_acce_color.tex", "161_Default_Attire/Textures/r_a_acce_nrm.tex",
            "161_Default_Attire/Textures/r_kneepad_color.tex", "161_Default_Attire/Textures/r_kneepad_nrm.tex",
            "161_Default_Attire/Textures/shoes_color.tex", "161_Default_Attire/Textures/shoes_nrm.tex",
            "161_Default_Attire/Textures/underwear_color.tex", "161_Default_Attire/Textures/underwear_nrm.tex",
            "BaseModel/CAS/headhair_mask.tex", "BaseModel/Materials/body.jmtl",
            "BaseModel/Materials/eye_caruncle.jmtl", "BaseModel/Materials/eye_l.jmtl",
            "BaseModel/Materials/eye_r.jmtl", "BaseModel/Materials/eyelash.jmtl",
            "BaseModel/Materials/head.jmtl", "BaseModel/Materials/mouthbag.jmtl", "BaseModel/Materials/teeth.jmtl",
            "BaseModel/Textures/body_color.tex", "BaseModel/Textures/body_nrm.tex",
            "BaseModel/Textures/eye_color.tex", "BaseModel/Textures/eyelash_color.tex",
            "BaseModel/Textures/head_color.tex", "BaseModel/Textures/head_nrm.tex",
            "BaseModel/baseModel.mcd", "BaseModel/baseModel.mtls"
        };

        public static IReadOnlyList<string> StockIndexPaths { get; } = Array.AsReadOnly(IndexedPaths);
        public static IReadOnlyList<string> RequiredCachePaths { get; } = Array.AsReadOnly(IndexedPaths.Select(CachePath).ToArray());

        public static string DefaultDirectory
        {
            get
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrWhiteSpace(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return CacheDirectory(Environment.GetEnvironmentVariable("GS_USER"), local,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
        }

        public static string CacheDirectory(string gsUser, string localAppData, string userProfile)
        {
            string user = !string.IsNullOrEmpty(gsUser) ? gsUser : Path.Combine(
                !string.IsNullOrEmpty(localAppData) ? localAppData : userProfile ?? "", "GorillaStudio");
            return Path.GetFullPath(Path.Combine(user, "stock", "Characters", StockFolderKey));
        }

        public static RandyPreviewAssetStatus Inspect(string directory)
        {
            string full = null;
            var issues = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("No Randy model folder was returned.");
                full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetFileName(full), StockFolderKey, StringComparison.OrdinalIgnoreCase))
                    issues.Add("The extractor returned a different wrestler's model folder.");
                else if (!System.IO.Directory.Exists(full)) issues.Add("The Randy model folder does not exist.");
                else
                foreach (string relative in RequiredCachePaths)
                {
                    string file = Path.Combine(full, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!ValidAsset(file)) issues.Add(relative);
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is NotSupportedException || error is SecurityException)
            { issues.Add("The Randy model folder could not be read: " + error.Message); }
            return new RandyPreviewAssetStatus { Directory = full, MissingPaths = issues.AsReadOnly() };
        }

        public static bool IsReady(string directory) { return Inspect(directory).IsReady; }

        public static string Validate(string directory)
        {
            RandyPreviewAssetStatus status = Inspect(directory);
            if (!status.IsReady)
            {
                string details = string.Join("; ", status.MissingPaths.Take(4));
                if (status.MissingPaths.Count > 4) details += "; and "
                    + (status.MissingPaths.Count - 4).ToString(CultureInfo.InvariantCulture) + " more";
                throw new InvalidDataException("Randy's preview model is incomplete. Missing or unreadable: " + details
                    + ". Load the complete stock Randy model from the selected WWE game before generating this preview.");
            }
            return status.Directory;
        }

        public static string[] CreateArguments(string directory, string key, string outputDirectory)
        {
            return CreateArgumentsCore(directory, key, outputDirectory, null);
        }

        /// <summary>Use only for a deliberately isolated single-taunt job; native work= overrides both cache levels.</summary>
        public static string[] CreateArguments(string directory, string key, string outputDirectory, string workDirectory)
        {
            if (string.IsNullOrWhiteSpace(workDirectory)) throw new ArgumentException("Choose an isolated preview workspace.", "workDirectory");
            return CreateArgumentsCore(directory, key, outputDirectory, Path.GetFullPath(workDirectory));
        }

        private static string[] CreateArgumentsCore(string directory, string key, string outputDirectory, string workDirectory)
        {
            if (key == null || !Regex.IsMatch(key, @"\A[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant)
                || key.StartsWith("discovery_unavailable_", StringComparison.Ordinal))
                throw new ArgumentException("Choose a playable taunt for Randy's preview.", "key");
            string model = Validate(directory);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Choose Randy's private preview output.", "outputDirectory");
            var result = new List<string> { model, key, Path.GetFullPath(outputDirectory), "quick=1", "hair=black",
                "attire=" + DefaultAttire, "slot=" + LogicalId.ToString(CultureInfo.InvariantCulture),
                "end=loop" };
            // With no work override the native engine shares model.blend/rest
            // per Randy source, while each taunt keeps its own loop/capture.
            if (workDirectory != null) result.Add("work=" + workDirectory);
            return result.ToArray();
        }

        private static string CachePath(string indexed)
        { return indexed.EndsWith(".tex", StringComparison.Ordinal) ? indexed.Substring(0, indexed.Length - 4) + ".dds" : indexed; }

        private static bool ValidAsset(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= 0) return false;
                    if (path.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase))
                        return stream.Length >= 16 && Magic(stream, new byte[] { 77, 67, 68, 33 });
                    if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        return stream.Length >= 128 && Magic(stream, new byte[] { 68, 68, 83, 32 });
                    return true;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is NotSupportedException || error is SecurityException)
            { return false; }
        }

        private static bool Magic(Stream stream, byte[] expected)
        {
            foreach (byte value in expected) if (stream.ReadByte() != value) return false;
            return true;
        }
    }
}
