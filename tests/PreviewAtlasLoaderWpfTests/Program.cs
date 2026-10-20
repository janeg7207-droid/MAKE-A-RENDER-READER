using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.Combine(Path.GetTempPath(), "Gorilla-PreviewAtlas-Wpf-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("These tests require Windows .NET Framework 4.8 and WPF; they cannot run on Linux.");
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "WPF runs on an STA thread");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "two-frame-atlas.png");
            WriteAtlas(path, Colors.Red, Colors.Blue);
            if (args.Contains("--runtime-expected-error"))
            {
                ReproduceOldNullKeyFailure(path);
                Console.WriteLine("PASS " + passed + " original WPF failure reproduction checks");
                return 0;
            }

            BitmapSource original = PreviewAtlasLoader.Load(path);
            Check(original != null && original.IsFrozen, "The loader returns a fully decoded frozen bitmap");
            Check(original.PixelWidth == 4 && original.PixelHeight == 2,
                "A two-frame atlas retains its complete pixel dimensions");
            Check(original.PixelWidth / original.PixelHeight == 2, "Both square frames remain available");
            Check(Pixel(original, 0, 0) == PixelValue(Colors.Red), "The first frame retains its red pixels");
            Check(Pixel(original, 3, 1) == PixelValue(Colors.Blue), "The second frame retains its blue pixels");

            var first = new CroppedBitmap(original, new Int32Rect(0, 0, 2, 2));
            var second = new CroppedBitmap(original, new Int32Rect(2, 0, 2, 2));
            first.Freeze(); second.Freeze();
            Check(first.IsFrozen && second.IsFrozen && first.PixelWidth == 2 && second.PixelHeight == 2,
                "Both frame crops can be frozen for playback");
            Check(Pixel(first, 1, 1) == PixelValue(Colors.Red) && Pixel(second, 0, 0) == PixelValue(Colors.Blue),
                "Frame crops display their own pixels");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length > 0, "OnLoad decoding releases the file handle before returning");

            // Keep the old bitmap/crops alive while replacing the same path.
            // A URI-cache regression would return their old pixels here.
            File.Delete(path);
            WriteAtlas(path, Colors.Lime, Colors.Yellow);
            BitmapSource replaced = PreviewAtlasLoader.Load(path);
            Check(replaced.IsFrozen && !ReferenceEquals(replaced, original),
                "Replacing an atlas at the same path creates a distinct frozen image");
            Check(Pixel(replaced, 0, 0) == PixelValue(Colors.Lime) && Pixel(replaced, 3, 1) == PixelValue(Colors.Yellow),
                "A repeated load reads replacement pixels without a stale URI cache entry");
            Check(Pixel(original, 0, 0) == PixelValue(Colors.Red) && Pixel(second, 1, 1) == PixelValue(Colors.Blue),
                "Previously decoded frames remain usable after replacement");
            File.Delete(path);
            Check(Pixel(replaced, 3, 1) == PixelValue(Colors.Yellow),
                "A decoded atlas remains usable after its source file is deleted");
            Console.WriteLine("PASS " + passed + " Windows .NET Framework WPF atlas checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static void ReproduceOldNullKeyFailure(string path)
    {
        bool failedAsExpected = false;
        try
        {
            var image = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.StreamSource = stream;
                image.EndInit();
            }
        }
        catch (ArgumentNullException error)
        {
            failedAsExpected = string.Equals(error.ParamName, "key", StringComparison.Ordinal);
            Console.WriteLine("Original failure: " + error.Message);
        }
        Check(failedAsExpected, "The original stream-only IgnoreImageCache code reproduces the null key error");
    }

    private static void WriteAtlas(string path, Color left, Color right)
    {
        var pixels = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                Color color = x < 2 ? left : right;
                int offset = (y * 4 + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R; pixels[offset + 3] = color.A;
            }
        BitmapSource source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
    }

    private static uint Pixel(BitmapSource source, int x, int y)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var value = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), value, 4, 0);
        return (uint)(value[0] | value[1] << 8 | value[2] << 16 | value[3] << 24);
    }

    private static uint PixelValue(Color color)
    { return (uint)(color.B | color.G << 8 | color.R << 16 | color.A << 24); }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++; Console.WriteLine("PASS " + message);
    }
}
