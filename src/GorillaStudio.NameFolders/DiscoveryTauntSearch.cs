using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Searches exact named variants with tolerant separators and indexed clip IDs.</summary>
    public static class DiscoveryTauntSearch
    {
        public static bool IsUnavailableKey(string key)
        {
            return key != null && key.StartsWith("discovery_unavailable_", StringComparison.Ordinal);
        }

        public static string BuildText(string name, IEnumerable<string> aliases, IEnumerable<string> tags, string clip)
        {
            var parts = new List<string> { name ?? "", clip ?? "", "taunt" };
            if (aliases != null) parts.AddRange(aliases.Where(value => value != null));
            if (tags != null) parts.AddRange(tags.Where(value => value != null));
            return Normalize(string.Join(" ", parts));
        }

        public static bool Matches(string text, string query)
        {
            string normalized = Normalize(query);
            if (normalized.Length == 0) return true;
            string[] words = Normalize(text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in normalized.Split(' '))
            {
                // A request for variant/name number 2 must not select 12, 20 or
                // another animation whose identifier merely contains that digit.
                bool numeric = token.All(char.IsDigit);
                if (!words.Any(word => numeric ? word == token : word.IndexOf(token, StringComparison.Ordinal) >= 0)) return false;
            }
            return true;
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var text = new StringBuilder();
            int previousKind = 0;
            foreach (char character in value.Normalize(NormalizationForm.FormKC))
            {
                int kind = char.IsDigit(character) ? 2 : char.IsLetter(character) ? 1 : 0;
                if (kind == 0)
                {
                    if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                    previousKind = 0;
                    continue;
                }
                if (previousKind != 0 && previousKind != kind && text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                text.Append(char.ToLowerInvariant(character)); previousKind = kind;
            }
            string[] words = text.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                switch (words[i])
                {
                    case "confidences": words[i] = "confidence"; break;
                    case "spirits": words[i] = "spirit"; break;
                    case "fighting": case "fights": words[i] = "fight"; break;
                    case "taunts": words[i] = "taunt"; break;
                }
            }
            return string.Join(" ", words);
        }
    }
}
