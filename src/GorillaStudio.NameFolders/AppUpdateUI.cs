using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, AppUpdateControls> updateWindows = new Dictionary<Window, AppUpdateControls>();

        private sealed class AppUpdateControls
        {
            internal Button Button;
            internal CancellationTokenSource Cancellation;
            internal Window Dialog;
            internal TextBlock Status;
            internal ProgressBar Progress;
            internal Process Updater;
            internal string PlanPath;
            internal bool Running;
            internal bool Closed;
            internal bool ClosingDialog;
            internal bool HandingOff;
        }

        private static void AttachUpdateControls(Window window)
        {
            if (updateWindows.ContainsKey(window)) return;
            Button reference = PlaybackButtons(window).FirstOrDefault(candidate =>
                string.Equals(candidate.Content as string, "Game setup", StringComparison.OrdinalIgnoreCase));
            var parent = reference == null ? null : reference.Parent as Panel;
            if (!(parent is StackPanel) && !(parent is WrapPanel))
            {
                reference = window.GetType().GetField("InstalledButton", InstanceMembers)?.GetValue(window) as Button;
                parent = reference == null ? null : reference.Parent as Panel;
            }
            if (reference == null || (!(parent is StackPanel) && !(parent is WrapPanel))) return;
            var button = new Button
            {
                Content = "Update app…", Style = reference.Style,
                FontFamily = reference.FontFamily, FontSize = reference.FontSize,
                Padding = reference.Padding, Margin = reference.Margin,
                ToolTip = "Check for an app update, download and install it, then restart Gorilla Studio. Your settings and renders are kept."
            };
            parent.Children.Insert(parent.Children.IndexOf(reference) + 1, button);
            var controls = new AppUpdateControls { Button = button };
            updateWindows.Add(window, controls);
            button.Click += async (sender, args) => await RunAppUpdateAsync(window, controls);
            window.Closing += (sender, args) =>
            {
                if (!controls.HandingOff) CancelAppUpdate(controls);
            };
            window.Closed += (sender, args) =>
            {
                controls.Closed = true;
                if (!controls.HandingOff) CancelAppUpdate(controls);
                CloseUpdateDialog(controls);
                updateWindows.Remove(window);
            };
        }

        private static string AppUpdateBlocked(Window window, Window progressDialog, bool ownsBusy)
        {
            Type type = window.GetType();
            foreach (string name in new[] { "Busy", "GameBusy" })
            {
                if (name == "Busy" && ownsBusy) continue;
                if (type.GetField(name, InstanceMembers)?.GetValue(window) is bool active && active)
                    return "Wait for the current model, preview or render to finish before updating.";
            }
            foreach (string name in new[] { "PrepTask", "BgTask" })
                if (type.GetField(name, InstanceMembers)?.GetValue(window) is Task task && !task.IsCompleted)
                    return "Wait for the model or render to finish preparing before updating.";
            object making = type.GetField("Making", InstanceMembers)?.GetValue(window);
            if (making?.GetType().GetProperty("Count")?.GetValue(making, null) is int previewCount && previewCount > 0)
                return "Wait for the current taunt preview to finish before updating.";
            if (rosterWindowsBusy.Contains(window) || pendingRosterSelection != null || pictureWork.Contains(window)
                || combinedGameSetups.Contains(window))
                return "Wait for the current roster, picture or game setup to finish before updating.";
            if (batchWindows.ContainsKey(window))
                return "Close Batch MAKE before updating Gorilla Studio.";
            if (window.OwnedWindows.Cast<Window>().Any(other => other != progressDialog && other.IsLoaded))
                return "Close the other Gorilla Studio windows, including Batch MAKE, before updating.";
            return null;
        }

        private static async Task RunAppUpdateAsync(Window owner, AppUpdateControls controls)
        {
            if (controls.Running || controls.Closed) return;
            string blocked = AppUpdateBlocked(owner, null, false);
            if (blocked != null) { SetModelStatus(owner, blocked); return; }
            FieldInfo busy = owner.GetType().GetField("Busy", InstanceMembers);
            if (busy == null || busy.FieldType != typeof(bool))
            { SetModelStatus(owner, "This app version cannot safely pause work for an update."); return; }

            var content = owner.Content as UIElement;
            bool enabled = content == null || content.IsEnabled;
            controls.Running = true;
            controls.HandingOff = false;
            controls.PlanPath = null;
            controls.Cancellation = new CancellationTokenSource();
            CancellationToken token = controls.Cancellation.Token;
            busy.SetValue(owner, true);
            controls.Button.IsEnabled = false;
            if (content != null) content.IsEnabled = false;
            string appRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                ShowUpdateDialog(owner, controls);
                UpdateAppProgress(owner, controls, "Checking for an app update…", null);
                var client = new AppUpdateClient();
                AppUpdateManifest manifest = await client.CheckAsync(token);
                Version current = typeof(FolderBridge).Assembly.GetName().Version;
                if (AppUpdatePackage.CompareVersions(manifest.Version, current) <= 0)
                {
                    UpdateAppProgress(owner, controls, "Gorilla Studio is up to date (" + current.ToString(3) + ").", null);
                    CloseUpdateDialog(controls);
                    if (!controls.Closed)
                        MessageBox.Show(owner, "You have the latest app version (" + current.ToString(3) + ").",
                            "App update", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string sourceUpdater = Path.Combine(appRoot, "GorillaStudio.Updater.exe");
                if (!File.Exists(sourceUpdater))
                    throw new FileNotFoundException("The app updater is missing. Extract the full Gorilla Studio download, then use Update app again.");
                ProbeUpdateFolder(appRoot);
                UpdateAppProgress(owner, controls, "Downloading app version " + manifest.VersionText + "…", 0);
                var progress = new Progress<AppUpdateProgress>(value =>
                {
                    if (!controls.Closed && !token.IsCancellationRequested)
                        UpdateAppProgress(owner, controls, "Downloading app version " + manifest.VersionText + "… "
                            + value.Percent.ToString(CultureInfo.InvariantCulture) + "%", value.Percent);
                });
                AppUpdatePayload payload = await client.DownloadAsync(manifest, AppUpdateCacheRoot(appRoot), progress, token);
                token.ThrowIfCancellationRequested();
                string stage = Path.GetDirectoryName(Path.GetFullPath(payload.PackagePath));
                string stagedUpdater = Path.Combine(stage, "GorillaStudio.Updater.exe");
                File.Copy(sourceUpdater, stagedUpdater, false);
                controls.PlanPath = Path.Combine(stage, "update-plan.json");
                AppUpdatePlan plan;
                using (Process currentProcess = Process.GetCurrentProcess())
                    plan = AppUpdateTransaction.CreatePlan(appRoot, payload.PackagePath, payload.Manifest,
                        currentProcess.Id, currentProcess.StartTime.ToUniversalTime().Ticks);
                UpdateAppProgress(owner, controls, "Checking the update and preparing to restart…", null);
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    AppUpdateTransaction.Preflight(plan, stage);
                    AppUpdateTransaction.WritePlan(plan, controls.PlanPath);
                }, token);
                token.ThrowIfCancellationRequested();
                controls.Updater = Process.Start(new ProcessStartInfo
                {
                    FileName = stagedUpdater, Arguments = "--plan " + UpdatePlanArgument(controls.PlanPath),
                    WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true
                });
                if (controls.Updater == null) throw new InvalidOperationException("The app updater could not start. Gorilla Studio will stay open.");
                await WaitForUpdateReadyAsync(controls, token);
                token.ThrowIfCancellationRequested();
                blocked = AppUpdateBlocked(owner, controls.Dialog, true);
                if (blocked != null) throw new InvalidOperationException(blocked + " The update has been canceled.");
                if (controls.Closed || !owner.IsLoaded) throw new OperationCanceledException(token);
                UpdateAppProgress(owner, controls, "Installing app version " + manifest.VersionText + ". Gorilla Studio will restart…", null);
                controls.HandingOff = true;
                try
                {
                    CloseUpdateDialog(controls);
                    // The original Closing handler may cancel this first close
                    // while it asynchronously disposes its licensing service,
                    // then closes the window again. Honor that lifecycle and
                    // the application's existing shutdown mode.
                    owner.Close();
                    await WaitForAppUpdateCloseAsync(controls, token);
                }
                catch { controls.HandingOff = false; throw; }
            }
            catch (OperationCanceledException)
            {
                CancelAppUpdate(controls);
                if (!controls.Closed && owner.IsLoaded) SetModelStatus(owner, "App update canceled.");
            }
            catch (Exception error)
            {
                CancelAppUpdate(controls);
                if (!controls.Closed && owner.IsLoaded)
                {
                    string message = error is TargetInvocationException && error.InnerException != null ? error.InnerException.Message : error.Message;
                    SetModelStatus(owner, "App update did not finish: " + message);
                    CloseUpdateDialog(controls);
                    MessageBox.Show(owner, message + "\n\nGorilla Studio is still open.", "App update",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                CloseUpdateDialog(controls);
                if (!controls.HandingOff)
                {
                    if (!controls.Closed) { busy.SetValue(owner, false); if (content != null) content.IsEnabled = enabled; controls.Button.IsEnabled = true; }
                    if (controls.Updater != null) { controls.Updater.Dispose(); controls.Updater = null; }
                }
                controls.Cancellation.Dispose(); controls.Cancellation = null;
                controls.Running = false;
            }
        }

        private static void ProbeUpdateFolder(string appRoot)
        {
            string path = Path.Combine(appRoot, ".gorilla-update-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } }
            catch (UnauthorizedAccessException)
            { throw new IOException("Gorilla Studio's folder cannot be updated. Extract the full app into a folder you can write to, then try again."); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static string AppUpdateCacheRoot(string appRoot)
        {
            string physicalApp = DirectoryLinks.Resolve(appRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string candidate in new[] {
                Path.Combine(Path.GetTempPath(), "GorillaStudio-AppUpdates"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GorillaStudio-AppUpdates") })
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(candidate));
                if (!Directory.Exists(parent)) continue;
                string physical = Directory.Exists(candidate) ? DirectoryLinks.Resolve(candidate)
                    : Path.Combine(DirectoryLinks.Resolve(parent), Path.GetFileName(candidate));
                if (string.Equals(physical, physicalApp, StringComparison.OrdinalIgnoreCase)
                    || physical.StartsWith(physicalApp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate;
            }
            throw new IOException("The updater needs a temporary folder outside the app folder. Extract Gorilla Studio into its own folder, then try again.");
        }

        private static string UpdatePlanArgument(string path)
        {
            if (path.IndexOf('"') >= 0 || path.IndexOf('\0') >= 0) throw new InvalidDataException("The update plan path is invalid.");
            return "\"" + path + "\"";
        }

        private static async Task WaitForUpdateReadyAsync(AppUpdateControls controls, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                string error = controls.PlanPath + ".error";
                if (File.Exists(error)) throw new IOException(File.ReadAllText(error));
                if (File.Exists(controls.PlanPath + ".ready")) return;
                if (controls.Updater.HasExited) throw new IOException("The updater closed before it was ready. Gorilla Studio will stay open.");
                await Task.Delay(100, token);
            }
            throw new TimeoutException("The updater did not become ready in time. Gorilla Studio will stay open.");
        }

        private static async Task WaitForAppUpdateCloseAsync(AppUpdateControls controls, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            while (!controls.Closed && watch.Elapsed < TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(controls.PlanPath + ".error")) throw new IOException(File.ReadAllText(controls.PlanPath + ".error"));
                if (controls.Updater.HasExited) throw new IOException("The updater closed before Gorilla Studio finished closing. The update was canceled.");
                await Task.Delay(100, token);
            }
            if (!controls.Closed)
                throw new TimeoutException("Gorilla Studio has not finished closing. The update was canceled; no app files were replaced.");
        }

        private static void CancelAppUpdate(AppUpdateControls controls)
        {
            if (controls.HandingOff) return;
            controls.Cancellation?.Cancel();
            if (controls.PlanPath != null)
                try { File.WriteAllText(controls.PlanPath + ".cancel", "canceled"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // The parent is still alive, so its own staged updater can only be
            // checking or waiting. Never terminate any unrelated process.
            if (controls.Updater != null)
                try { if (!controls.Updater.HasExited) controls.Updater.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }

        private static void ShowUpdateDialog(Window owner, AppUpdateControls controls)
        {
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Update Gorilla Studio", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            controls.Status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 46 };
            panel.Children.Add(controls.Status);
            controls.Progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 14) };
            panel.Children.Add(controls.Progress);
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(15, 5, 15, 5), HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Click += (sender, args) => CancelAppUpdate(controls);
            panel.Children.Add(cancel);
            controls.Dialog = new Window
            {
                Owner = owner, Title = "App update", Width = 440, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = owner.FontFamily, FontSize = 13, Content = panel, ShowInTaskbar = false
            };
            controls.Dialog.Closed += (sender, args) => { if (!controls.ClosingDialog && !controls.HandingOff) CancelAppUpdate(controls); };
            controls.Dialog.Show();
        }

        private static void UpdateAppProgress(Window owner, AppUpdateControls controls, string text, int? percent)
        {
            if (controls.Closed) return;
            if (controls.Status != null) controls.Status.Text = text;
            if (controls.Progress != null) { controls.Progress.IsIndeterminate = !percent.HasValue; if (percent.HasValue) controls.Progress.Value = percent.Value; }
            SetModelStatus(owner, text);
        }

        private static void CloseUpdateDialog(AppUpdateControls controls)
        {
            if (controls.Dialog == null) return;
            controls.ClosingDialog = true;
            try { controls.Dialog.Close(); }
            finally { controls.Dialog = null; controls.Status = null; controls.Progress = null; controls.ClosingDialog = false; }
        }
    }
}
