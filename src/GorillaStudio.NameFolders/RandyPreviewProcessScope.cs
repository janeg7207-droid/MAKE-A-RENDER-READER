using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GorillaStudio.NameFolders
{
    /// <summary>Separates a dedicated Randy preview from the selected wrestler's render state.</summary>
    public sealed class RandyPreviewProcessScope : IDisposable
    {
        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<object, RandyPreviewProcessScope> Active =
            new ConditionalWeakTable<object, RandyPreviewProcessScope>();
        private object owner;
        private readonly string folder, output;

        private RandyPreviewProcessScope(object owner, string folder, string output)
        { this.owner = owner; this.folder = Full(folder); this.output = Full(output); }

        public static RandyPreviewProcessScope Begin(object owner, string folder, string output)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var scope = new RandyPreviewProcessScope(owner, folder, output);
            lock (Gate)
            {
                if (Active.TryGetValue(owner, out RandyPreviewProcessScope previous))
                    throw new InvalidOperationException("A Randy preview is already running in this window.");
                Active.Add(owner, scope);
            }
            return scope;
        }

        public static bool IsRequest(object owner, string script, IEnumerable<string> arguments)
        {
            if (owner == null || string.IsNullOrWhiteSpace(script) || arguments == null) return false;
            try
            {
                if (!Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).Equals("gorilla_render", StringComparison.OrdinalIgnoreCase)) return false;
                string[] args = arguments.ToArray();
                if (args.Length < 3 || args.Skip(3).Any(value => value == "prepare" || (value ?? "").StartsWith("prepare=", StringComparison.Ordinal))) return false;
                string quick = null, slot = null;
                foreach (string arg in args.Skip(3))
                {
                    if ((arg ?? "").StartsWith("quick=", StringComparison.Ordinal)) quick = arg.Substring(6);
                    if ((arg ?? "").StartsWith("slot=", StringComparison.Ordinal)) slot = arg.Substring(5);
                }
                if (quick != "1" || slot != "161") return false;
                lock (Gate)
                    return Active.TryGetValue(owner, out RandyPreviewProcessScope scope)
                        && Same(scope.folder, Full(args[0])) && Same(scope.output, Full(args[2]));
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException)
            { return false; }
        }

        private static string Full(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Use an absolute Randy preview path.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool Same(string first, string second)
        { return string.Equals(first, second, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }

        public void Dispose()
        {
            lock (Gate)
            {
                if (owner == null) return;
                if (Active.TryGetValue(owner, out RandyPreviewProcessScope current) && ReferenceEquals(current, this)) Active.Remove(owner);
                owner = null;
            }
        }
    }
}
