using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public static class FolderNames
    {
        public static string DisplayName(string folderName)
        {
            Match match = Regex.Match(folderName, @"^([0-9]+)_(.+)$");
            return (match.Success ? match.Groups[2].Value : folderName).Replace('_', ' ').Trim();
        }

        public static int? ExistingId(string folderName)
        {
            Match match = Regex.Match(folderName, @"^([0-9]+)_(.+)$");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value)
                && value > 0 && value <= 65535 ? (int?)value : null;
        }

        public static string AliasName(int id, string name)
        {
            if (id < 1 || id > 65535) throw new ArgumentOutOfRangeException("id", "The wrestler ID must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter the wrestler's name.", "name");
            var output = new StringBuilder();
            const string invalid = "<>:\"/\\|?*";
            foreach (char c in name.Trim())
                output.Append(c < 32 || invalid.IndexOf(c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
            string safe = Regex.Replace(output.ToString(), "_+", "_").Trim('_', '.', ' ');
            if (safe.Length == 0) throw new ArgumentException("The wrestler's name contains no usable filename characters.", "name");
            if (safe.Length > 80) safe = safe.Substring(0, 80).TrimEnd('.', ' ');
            return id.ToString(CultureInfo.InvariantCulture) + "_" + safe;
        }

        public static string SourceKey(string fullPath)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(fullPath.TrimEnd('\\', '/').ToUpperInvariant()));
                return BitConverter.ToString(bytes, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
