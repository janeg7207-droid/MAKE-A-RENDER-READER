using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class PreparedRenderSelection
        {
            internal CharacterFolderPlan Plan;
            internal int InitialId;
            internal Dictionary<ulong, string> Pictures;
            internal readonly HashSet<int> Resolved = new HashSet<int>();
            internal readonly Dictionary<int, string> Notes = new Dictionary<int, string>();
            internal string Table { get { return Path.Combine(Plan.RosterDirectory, "CharProfileTable.jsfb"); } }
        }

        private static readonly Dictionary<string, PreparedRenderSelection> preparedPictures =
            new Dictionary<string, PreparedRenderSelection>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Window> pictureWindows = new HashSet<Window>();
        private static readonly HashSet<Window> pictureWork = new HashSet<Window>();
        private static readonly Dictionary<Window, string> shownPictures = new Dictionary<Window, string>();
        private static readonly Dictionary<Window, DispatcherTimer> pictureRetries = new Dictionary<Window, DispatcherTimer>();

        private static void RememberPreparedRender(string alias, CharacterFolderPlan plan, RosterEntry entry, Dictionary<ulong, string> pictures)
        {
            if (plan.RosterDirectory == null) return;
            preparedPictures[SelectionPath(alias)] = new PreparedRenderSelection {
                Plan = plan, InitialId = entry.Id, Pictures = pictures
            };
        }

        private static void AttachPictureSelection(Window window)
        {
            if (!pictureWindows.Add(window)) return;
            var find = window.GetType().GetField("SlotFind", InstanceMembers)?.GetValue(window) as TextBox;
            if (find != null) find.TextChanged += (sender, args) => QueueSelectedPicture(window);
            window.Closed += (sender, args) =>
            {
                pictureWindows.Remove(window); shownPictures.Remove(window);
                DispatcherTimer retry;
                if (pictureRetries.TryGetValue(window, out retry)) { retry.Stop(); pictureRetries.Remove(window); }
            };
        }

        static partial void OnRosterSelectionApplied(object owner)
        {
            var window = owner as Window;
            if (window != null) QueueSelectedPicture(window);
        }

        private static void QueueSelectedPicture(Window window)
        {
            if (pendingRosterSelection != null || !window.IsLoaded) return;
            window.Dispatcher.BeginInvoke(new Action(() => { _ = RefreshSelectedPictureAsync(window); }));
        }

        private static async Task PrepareSelectedRenderAsync(Window window, string alias)
        {
            PreparedRenderSelection selection;
            if (!preparedPictures.TryGetValue(SelectionPath(alias), out selection)) return;
            ClearSelectedPicture(window);
            var body = window.GetType().GetField("BodyGrid", InstanceMembers)?.GetValue(window) as UIElement;
            bool enabled = body != null && body.IsEnabled;
            if (body != null) body.IsEnabled = false;
            try
            {
                StopAutomaticMake(window);
                var preparation = window.GetType().GetField("PrepTask", InstanceMembers)?.GetValue(window) as Task;
                if (preparation != null) try { await preparation; } catch { }
                await ResolveSelectedPictureAsync(window, alias, selection, selection.InitialId);
            }
            catch (Exception error) when (PictureError(error))
            {
                selection.Notes[selection.InitialId] = "Picture reference is linked; its image could not be loaded: " + error.Message;
                selection.Resolved.Add(selection.InitialId);
            }
            finally { if (body != null) body.IsEnabled = enabled; }
        }

        private static async Task ResolveSelectedPictureAsync(Window window, string alias, PreparedRenderSelection selection, int id)
        {
            if (selection.Resolved.Contains(id)) return;
            List<ulong> required;
            if (!DiscoveryRenderLinks.Read(selection.Table).TryGetValue(id, out required)) return;
            if (required.Any(hash => !selection.Pictures.ContainsKey(hash) || !File.Exists(selection.Pictures[hash])))
                foreach (var image in await Task.Run(() => PrepareRenderImages(selection.Plan, id))) selection.Pictures[image.Key] = image.Value;
            ulong[] missing = required.Where(hash => !selection.Pictures.ContainsKey(hash) || !File.Exists(selection.Pictures[hash])).ToArray();
            if (missing.Length > 0)
            {
                string game = window.GetType().GetField("Game", InstanceMembers)?.GetValue(window) as string;
                DiscoveryArchiveRenderResult result;
                bool progressActive = true;
                try
                {
                    result = await DiscoveryArchiveRenders.FindAsync(
                        AppDomain.CurrentDomain.BaseDirectory, game, selection.Plan.DataRoot, missing,
                        Path.Combine(StorageDirectory, "picture-archives"), message => window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            bool loading = pendingRosterSelection != null && string.Equals(pendingRosterSelection.Folder, SelectionPath(alias), StringComparison.OrdinalIgnoreCase);
                            if (progressActive && window.IsLoaded && (loading || SamePictureSelection(window, alias, id.ToString(CultureInfo.InvariantCulture))))
                                SetModelStatus(window, message);
                        })));
                }
                finally { progressActive = false; }
                foreach (var picture in result.Images) selection.Pictures[picture.Key] = picture.Value;
                selection.Notes[id] = result.Status;
            }
            if (required.Any(hash => selection.Pictures.ContainsKey(hash) && File.Exists(selection.Pictures[hash])))
                selection.Notes[id] = "";
            DirectoryInfo characters = Directory.GetParent(alias);
            string root = characters?.Parent?.FullName;
            if (root != null) await Task.Run(() => DiscoveryRenderOverlay.Prepare(root, selection.Table, selection.Pictures));
            selection.Resolved.Add(id);
        }

        private static async Task RefreshSelectedPictureAsync(Window window)
        {
            if (!window.IsLoaded || pendingRosterSelection != null || pictureWork.Contains(window)) return;
            string folder = window.GetType().GetField("Folder", InstanceMembers)?.GetValue(window) as string;
            string target = window.GetType().GetField("TargetSlot", InstanceMembers)?.GetValue(window) as string;
            int id;
            PreparedRenderSelection selection;
            if (string.IsNullOrWhiteSpace(folder) || !int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out id)
                || !preparedPictures.TryGetValue(SelectionPath(folder), out selection)) return;
            DiscoveryPictureSelection.Apply(window, selection.Table);
            var busy = window.GetType().GetField("Busy", InstanceMembers);
            var body = window.GetType().GetField("BodyGrid", InstanceMembers)?.GetValue(window) as UIElement;
            bool bodyEnabled = body != null && body.IsEnabled;
            bool ownsBody = false;
            bool ownsBusy = false;
            pictureWork.Add(window);
            try
            {
                if (!selection.Resolved.Contains(id))
                {
                    if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window))
                    { ClearSelectedPicture(window); RetrySelectedPictureWhenIdle(window); return; }
                    if (busy != null) { busy.SetValue(window, true); ownsBusy = true; }
                    if (body != null) { body.IsEnabled = false; ownsBody = true; }
                    ClearSelectedPicture(window);
                    StopAutomaticMake(window);
                    var prep = window.GetType().GetField("PrepTask", InstanceMembers)?.GetValue(window) as Task;
                    if (prep != null) try { await prep; } catch { }
                    await ResolveSelectedPictureAsync(window, folder, selection, id);
                }
                if (!SamePictureSelection(window, folder, target)) return;
                DiscoveryPictureSelection.Apply(window, selection.Table);
                string note;
                var label = window.GetType().GetField("WRender", InstanceMembers)?.GetValue(window) as TextBlock;
                if (label != null && selection.Notes.TryGetValue(id, out note) && !string.IsNullOrWhiteSpace(note))
                    label.Text += "\n" + note;
                List<ulong> hashes;
                if (!DiscoveryRenderLinks.Read(selection.Table).TryGetValue(id, out hashes)) return;
                string source = hashes.Where(hash => selection.Pictures.ContainsKey(hash)).Select(hash => selection.Pictures[hash]).FirstOrDefault(File.Exists);
                if (source == null) { ClearSelectedPicture(window); return; }
                var file = new FileInfo(source);
                string signature = folder + "\n" + target + "\n" + source + "\n" + file.Length + "\n" + file.LastWriteTimeUtc.Ticks;
                string shown;
                if (shownPictures.TryGetValue(window, out shown) && shown == signature) return;
                var decoded = await Task.Run(() => RosterPicturePreview.Decode(source));
                if (!SamePictureSelection(window, folder, target)) return;
                var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, checked(decoded.Width * 4));
                bitmap.Freeze();
                var image = window.GetType().GetField("GamePicture", InstanceMembers)?.GetValue(window) as Image;
                if (image != null) { image.Source = bitmap; shownPictures[window] = signature; }
            }
            catch (Exception error) when (PictureError(error))
            {
                if (SamePictureSelection(window, folder, target))
                {
                    ClearSelectedPicture(window);
                    var label = window.GetType().GetField("WRender", InstanceMembers)?.GetValue(window) as TextBlock;
                    if (label != null) label.Text += "\nPicture reference is linked; its image could not be loaded: " + error.Message;
                }
            }
            finally
            {
                if (ownsBusy) busy.SetValue(window, false);
                if (ownsBody) body.IsEnabled = bodyEnabled;
                pictureWork.Remove(window);
                if (!SamePictureSelection(window, folder, target)) QueueSelectedPicture(window);
            }
        }

        private static bool PictureError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is NotSupportedException || error is FormatException
                || error is OverflowException || error is System.ComponentModel.Win32Exception || error is TimeoutException;
        }

        private static void RetrySelectedPictureWhenIdle(Window window)
        {
            if (pictureRetries.ContainsKey(window)) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            pictureRetries[window] = timer;
            timer.Tick += (sender, args) =>
            {
                var busy = window.GetType().GetField("Busy", InstanceMembers);
                if (window.IsLoaded && busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window)) return;
                timer.Stop(); pictureRetries.Remove(window);
                if (window.IsLoaded) QueueSelectedPicture(window);
            };
            timer.Start();
        }

        private static bool SamePictureSelection(Window window, string folder, string target)
        {
            return window.IsLoaded && string.Equals(window.GetType().GetField("Folder", InstanceMembers)?.GetValue(window) as string, folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(window.GetType().GetField("TargetSlot", InstanceMembers)?.GetValue(window) as string, target, StringComparison.Ordinal);
        }

        private static void ClearSelectedPicture(Window window)
        {
            var image = window.GetType().GetField("GamePicture", InstanceMembers)?.GetValue(window) as Image;
            if (image != null) image.Source = null;
            shownPictures.Remove(window);
        }
    }
}
