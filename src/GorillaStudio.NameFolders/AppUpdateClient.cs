using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class AppUpdateProgress
    {
        public long BytesReceived { get; internal set; }
        public long TotalBytes { get; internal set; }
        public int Percent { get { return TotalBytes > 0 ? (int)Math.Min(100, BytesReceived * 100 / TotalBytes) : 0; } }
    }

    public sealed class AppUpdateResponse : IDisposable
    {
        public int StatusCode { get; private set; }
        public Stream Body { get; private set; }
        public long? ContentLength { get; private set; }
        public Uri ResponseUri { get; private set; }
        private readonly Action close;
        public AppUpdateResponse(int statusCode, Stream body, long? contentLength, Uri responseUri)
            : this(statusCode, body, contentLength, responseUri, null) { }
        internal AppUpdateResponse(int statusCode, Stream body, long? contentLength, Uri responseUri, Action close)
        { StatusCode = statusCode; Body = body; ContentLength = contentLength; ResponseUri = responseUri; this.close = close; }
        public void Dispose() { try { Body?.Dispose(); } finally { close?.Invoke(); } }
    }

    public delegate Task<AppUpdateResponse> AppUpdateTransport(Uri uri, CancellationToken token);

    /// <summary>Downloads a checked update into a new private cache directory. Never installs or executes it.</summary>
    public sealed class AppUpdateClient
    {
        public const string FeedUrl = "https://raw.githubusercontent.com/janeg7207-droid/MAKE-A-RENDER-READER/main/downloads/update.json";
        private readonly AppUpdateTransport transport;
        public AppUpdateClient() : this(HttpGetAsync) { }
        public AppUpdateClient(AppUpdateTransport transport) { this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); }

        public async Task<AppUpdateManifest> CheckAsync(CancellationToken token)
        {
            var uri = new Uri(FeedUrl);
            using (AppUpdateResponse response = await transport(uri, token).ConfigureAwait(false))
            {
                CheckResponse(response, uri, AppUpdatePackage.MaximumManifestBytes);
                using (var output = new MemoryStream())
                {
                    await CopyAsync(response.Body, output, AppUpdatePackage.MaximumManifestBytes, null, token).ConfigureAwait(false);
                    return AppUpdatePackage.ParseManifest(output.ToArray());
                }
            }
        }

        public async Task<AppUpdatePayload> DownloadAsync(AppUpdateManifest manifest, string privateCacheRoot,
            IProgress<AppUpdateProgress> progress, CancellationToken token)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest = AppUpdatePackage.CreateManifest(manifest.VersionText, manifest.DownloadUri?.OriginalString, manifest.Size, manifest.Sha256, manifest.MinimumUpdaterVersion);
            if (string.IsNullOrWhiteSpace(privateCacheRoot)) throw new ArgumentException("Choose a private per-user update cache.", nameof(privateCacheRoot));
            token.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(privateCacheRoot);
            EnsureUnlinked(root); Directory.CreateDirectory(root); EnsureUnlinked(root);
            string directory = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
            string partial = Path.Combine(directory, "update.zip.part"), complete = Path.Combine(directory, "update.zip");
            bool success = false;
            try
            {
                using (AppUpdateResponse response = await transport(manifest.DownloadUri, token).ConfigureAwait(false))
                {
                    CheckResponse(response, manifest.DownloadUri, manifest.Size);
                    if (response.ContentLength.HasValue && response.ContentLength.Value != manifest.Size) throw new InvalidDataException("The update response has a different size from its feed.");
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        progress?.Report(new AppUpdateProgress { TotalBytes = manifest.Size });
                        long size = await CopyAsync(response.Body, output, manifest.Size,
                            count => progress?.Report(new AppUpdateProgress { BytesReceived = count, TotalBytes = manifest.Size }), token).ConfigureAwait(false);
                        if (size != manifest.Size) throw new InvalidDataException("The update download is truncated.");
                        output.Flush(true);
                    }
                }
                token.ThrowIfCancellationRequested(); File.Move(partial, complete);
                AppUpdatePayload payload = AppUpdatePackage.Validate(complete, manifest, token);
                success = true; return payload;
            }
            finally
            {
                if (!success)
                {
                    Delete(partial); Delete(complete);
                    try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static async Task<long> CopyAsync(Stream source, Stream destination, long limit, Action<long> progress, CancellationToken token)
        {
            if (source == null) throw new InvalidDataException("The update server returned no body.");
            var buffer = new byte[65536]; long total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (count == 0) return total;
                if (count > limit - total) throw new InvalidDataException("The update response exceeds its permitted size.");
                await destination.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                total += count; progress?.Invoke(total);
            }
        }

        private static void CheckResponse(AppUpdateResponse response, Uri requested, long limit)
        {
            if (response == null || response.StatusCode != 200 || response.ResponseUri == null
                || !string.Equals(response.ResponseUri.AbsoluteUri, requested.AbsoluteUri, StringComparison.Ordinal))
                throw new InvalidDataException("The update server returned an unexpected response or redirect.");
            if (response.ContentLength.HasValue && (response.ContentLength.Value < 0 || response.ContentLength.Value > limit))
                throw new InvalidDataException("The update response is too large.");
        }

        private static async Task<AppUpdateResponse> HttpGetAsync(Uri uri, CancellationToken token)
        {
            // Default platform certificate and hostname checks remain enabled.
            // Automatic redirects are disabled so a feed cannot change repository/host.
#pragma warning disable SYSLIB0014
            var request = (HttpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
            request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = 30000; request.ReadWriteTimeout = 30000;
            request.UserAgent = "GorillaStudio-Updater/1";
            CancellationTokenRegistration registration = token.Register(request.Abort);
            try
            {
                var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                return new AppUpdateResponse((int)response.StatusCode, response.GetResponseStream(), response.ContentLength >= 0 ? (long?)response.ContentLength : null,
                    response.ResponseUri, () => { response.Dispose(); registration.Dispose(); });
            }
            catch
            {
                registration.Dispose(); token.ThrowIfCancellationRequested(); throw;
            }
        }

        private static void EnsureUnlinked(string path)
        {
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The update cache cannot use a linked path.");
        }
        private static void Delete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
