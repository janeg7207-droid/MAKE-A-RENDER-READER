using System.IO;
using System.Windows.Media.Imaging;

namespace GorillaStudio.NameFolders
{
    /// <summary>Decodes a fresh atlas without a URI-cache entry or retained file handle.</summary>
    public static class PreviewAtlasLoader
    {
        public static BitmapSource Load(string path)
        {
            var bitmap = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // IgnoreImageCache is a URI-cache operation. With StreamSource
                // alone, WPF passes a null URI to its cache Hashtable and throws
                // "Key cannot be null". A new stream already bypasses that cache.
                bitmap.CreateOptions = BitmapCreateOptions.None;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            return bitmap;
        }
    }
}
