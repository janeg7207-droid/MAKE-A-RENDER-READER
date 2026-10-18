using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<string, string> renderTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void RememberRenderTable(string folder, string table)
        {
            if (!string.IsNullOrWhiteSpace(table)) renderTables[SelectionPath(folder)] = Path.GetFullPath(table);
        }

        private static Dictionary<ulong, string> PrepareRenderImages(CharacterFolderPlan plan)
        {
            var prepared = new Dictionary<ulong, string>();
            if (plan.DataRoot == null || plan.RosterDirectory == null) return prepared;
            string table = Path.Combine(plan.RosterDirectory, "CharProfileTable.jsfb");
            var links = DiscoveryRenderLinks.Read(table);
            var images = DiscoveryRenderImages.Find(plan.DataRoot, links.Values.SelectMany(value => value));
            if (!plan.SourceFolder.StartsWith(plan.DataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                foreach (var image in DiscoveryRenderImages.Find(plan.SourceFolder, links.Values.SelectMany(value => value)))
                    if (!images.ContainsKey(image.Key)) images.Add(image.Key, image.Value);
            foreach (var image in images)
            {
                try
                {
                    if (!string.Equals(Path.GetExtension(image.Value), ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        prepared[image.Key] = image.Value;
                        continue;
                    }
                    if (new FileInfo(image.Value).Length > 128 * 1024 * 1024) continue;
                    byte[] input = File.ReadAllBytes(image.Value);
                    if (input.Length < 24) continue;
                    uint width = BigEndian32(input, 16), height = BigEndian32(input, 20);
                    if (width == 0 || height == 0 || width > RosterPictureDds.MaximumDimension || height > RosterPictureDds.MaximumDimension
                        || (long)width * height * 4 > RosterPictureDds.MaximumPixelBytes) continue;
                    string key;
                    using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                    string output = Path.Combine(StorageDirectory, "picture-images", key + ".dds");
                    if (!File.Exists(output))
                    {
                        BitmapSource bitmap;
                        using (var stream = new MemoryStream(input, false))
                        {
                            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                        }
                        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1 || bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384
                            || (long)bitmap.PixelWidth * bitmap.PixelHeight > 16777216) continue;
                        byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                        bitmap.CopyPixels(pixels, checked(bitmap.PixelWidth * 4), 0);
                        RosterPictureDds.Write(output, bitmap.PixelWidth, bitmap.PixelHeight, pixels);
                    }
                    prepared[image.Key] = output;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                    || error is NotSupportedException || error is InvalidOperationException || error is OverflowException || error is FormatException)
                { /* An unreadable original picture must not prevent loading its wrestler. */ }
            }
            return prepared;
        }

        private static uint BigEndian32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
        }

        // Runs after the native renderer has returned a successful result, including
        // cached previews and completed background MAKE jobs, before it displays or installs it.
        public static void CompleteRenderResult(object owner, object value)
        {
            var result = value as IDictionary<string, object>;
            if (owner == null || result == null) return;
            string folder = owner.GetType().GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
            string table = null;
            bool intermediatePictures = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    renderTables.TryGetValue(SelectionPath(folder), out table);
                    DirectoryInfo parent = new DirectoryInfo(folder).Parent;
                    string root = parent != null && string.Equals(parent.Name, "Characters", StringComparison.OrdinalIgnoreCase)
                        ? parent.Parent?.FullName : null;
                    string context = root == null ? null : Path.Combine(root, "original-roster.txt");
                    intermediatePictures = context != null && File.Exists(context);
                    if (table == null) table = intermediatePictures ? File.ReadAllText(context).Trim()
                        : root == null ? null : CharacterFolders.FindDiscoveryTable(root);
                }
                if (table == null || !File.Exists(table))
                {
                    if (!intermediatePictures) return;
                    throw new IOException("The current Discovery profile table is unavailable.");
                }
                object idValue;
                int id;
                if (!result.TryGetValue("id", out idValue) || idValue == null || idValue is bool || idValue is string
                    || !int.TryParse(Convert.ToString(idValue, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out id)
                    || !DiscoveryRenderLinks.Read(table).ContainsKey(id))
                {
                    if (!intermediatePictures) return;
                    throw new IOException("The selected character is no longer in the current Discovery profiles.");
                }
                if (DiscoveryRenderResult.Complete(result, table)) return;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is NotSupportedException || error is OverflowException)
            { }

            // Never offer an intermediate picture key for installation if finalizing
            // its actual game keys failed. The completed video stays on disk for retry.
            if (!result.ContainsKey("gmr")) return;
            result.Remove("gmr");
            owner.GetType().GetField("LastGmr", InstanceMembers)?.SetValue(owner, null);
            var install = owner.GetType().GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
            if (install != null) install.IsEnabled = false;
            result["name"] = Convert.ToString(result.ContainsKey("name") ? result["name"] : "", CultureInfo.InvariantCulture)
                + "\nRoster picture links could not be saved. Make the render again before installing.";
            MethodInfo log = owner.GetType().GetMethod("Log", InstanceMembers, null, new[] { typeof(string) }, null);
            log?.Invoke(owner, new object[] { "Roster picture links could not be saved; installation is disabled for this result." });
        }
    }
}
