using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class RandyPreviewBuildControls
        {
            internal Window Dialog;
            internal Button Launch, Action;
            internal TextBlock Counts, Status;
            internal ProgressBar Progress;
            internal DispatcherTimer Timer;
            internal CancellationTokenSource Cancellation;
            internal TaskCompletionSource<bool> Drained;
            internal bool Active, Closed, OwnerClosing, CloseRequested;
            internal string Context, CancelReason;
        }

        private static readonly Dictionary<Window, RandyPreviewBuildControls> randyPreviewBuildWindows =
            new Dictionary<Window, RandyPreviewBuildControls>();

        private static void AttachRandyPreviewBuildControls(Window owner)
        {
            if (randyPreviewBuildWindows.ContainsKey(owner)) return;
            var search = AutomaticTauntField(owner, "SearchBox") as FrameworkElement;
            if (search == null) return;
            var reference = AutomaticTauntField(owner, "PreviewButton") as Button;
            var state = new RandyPreviewBuildControls();
            state.Launch = new Button { Content = "Build Randy previews…", Style = reference?.Style,
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 5),
                ToolTip = "Build missing Randy previews for the entire loaded taunt library once. Saved previews are skipped. You can cancel and continue later." };
            // The native search hint shares a Grid with the TextBox. Keep the
            // whole search row together and append the button below it.
            var hint = AutomaticTauntField(owner, "SearchHint") as FrameworkElement;
            FrameworkElement anchor = search;
            if (search.Parent is Grid && ReferenceEquals(search.Parent, hint?.Parent)) anchor = (FrameworkElement)search.Parent;
            if (!InsertRandyPreviewBuildButton(anchor, state.Launch)) return;
            randyPreviewBuildWindows.Add(owner, state);
            state.Launch.Click += async (sender, args) => {
                if (state.Dialog != null) { state.Dialog.Activate(); return; }
                OpenRandyPreviewBuildDialog(owner, state);
                await RunRandyPreviewBuildAsync(owner, state);
            };
            state.Timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };
            state.Timer.Tick += (sender, args) => {
                if (state.Active && !RandyPreviewBuildContextCurrent(owner, state))
                    CancelRandyPreviewBuild(owner, "Stopped because the Discovery, game or Blender context changed. Build again when it is ready.");
            };
            owner.Closing += (sender, args) => {
                state.OwnerClosing = true;
                CancelRandyPreviewBuild(owner, "Closing Studio. Completed previews are saved.");
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                    // Native licensing may cancel this close after our handler.
                    // A later dialog close must still wait for process drainage.
                    if (!state.Closed && owner.IsLoaded) state.OwnerClosing = false;
                }));
            };
            owner.Closed += (sender, args) => {
                state.Closed = true; state.Timer.Stop();
                CancelRandyPreviewBuild(owner, "Studio closed. Completed previews are saved.");
                randyPreviewBuildWindows.Remove(owner);
                // Active callbacks retain their own source until the producer
                // has exited. Never dispose a token while it is still in use.
                if (!state.Active) state.Cancellation?.Dispose();
            };
        }

        private static bool InsertRandyPreviewBuildButton(FrameworkElement anchor, Button button)
        {
            if (anchor.Parent is StackPanel)
            {
                var parent = (StackPanel)anchor.Parent;
                parent.Children.Insert(parent.Children.IndexOf(anchor) + 1, button);
                return true;
            }
            // Other supported native layouts use an auto-height Grid row.
            // Preserve the anchor's placement and its original search content.
            var panel = anchor.Parent as Panel;
            if (panel == null) return false;
            int index = panel.Children.IndexOf(anchor);
            if (index < 0) return false;
            var wrapper = new DockPanel { LastChildFill = true };
            Grid.SetRow(wrapper, Grid.GetRow(anchor)); Grid.SetColumn(wrapper, Grid.GetColumn(anchor));
            Grid.SetRowSpan(wrapper, Grid.GetRowSpan(anchor)); Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(anchor));
            DockPanel.SetDock(wrapper, DockPanel.GetDock(anchor));
            panel.Children.RemoveAt(index); panel.Children.Insert(index, wrapper);
            DockPanel.SetDock(button, Dock.Bottom); wrapper.Children.Add(button); wrapper.Children.Add(anchor);
            return true;
        }

        private static void OpenRandyPreviewBuildDialog(Window owner, RandyPreviewBuildControls state)
        {
            var dialog = new Window { Owner = owner, Title = "Build Randy previews", Width = 560, Height = 310,
                MinWidth = 440, MinHeight = 285, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = owner.FontFamily, FontSize = 13 };
            state.Dialog = dialog; state.CloseRequested = false;
            var grid = new Grid { Margin = new Thickness(18) };
            for (int row = 0; row < 5; row++) grid.RowDefinitions.Add(new RowDefinition {
                Height = row == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            dialog.Content = grid;
            var description = new TextBlock { Text = "Build missing Randy previews for every playable taunt in your loaded library. Saved previews are skipped. Your wrestler and attire stay selected.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            grid.Children.Add(description);
            state.Counts = new TextBlock { Text = "Checking the complete taunt library…", Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(state.Counts, 1); grid.Children.Add(state.Counts);
            state.Progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 10, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(state.Progress, 2); grid.Children.Add(state.Progress);
            state.Status = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(162, 187, 208)) };
            Grid.SetRow(state.Status, 3); grid.Children.Add(state.Status);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            state.Action = new Button { Content = "Cancel", MinWidth = 110, Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "Close", MinWidth = 75, Padding = state.Action.Padding };
            footer.Children.Add(state.Action); footer.Children.Add(close); Grid.SetRow(footer, 4); grid.Children.Add(footer);
            state.Action.Click += async (sender, args) => {
                if (state.Active) CancelRandyPreviewBuild(owner, "Stopping… Completed previews are saved.");
                else await RunRandyPreviewBuildAsync(owner, state);
            };
            close.Click += (sender, args) => dialog.Close();
            dialog.Closing += (sender, args) => {
                if (!state.Active || state.OwnerClosing) return;
                args.Cancel = true; state.CloseRequested = true;
                CancelRandyPreviewBuild(owner, "Stopping before closing… Completed previews are saved.");
            };
            dialog.Closed += (sender, args) => {
                if (ReferenceEquals(state.Dialog, dialog)) state.Dialog = null;
            };
            dialog.Show();
        }

        private static async Task RunRandyPreviewBuildAsync(Window owner, RandyPreviewBuildControls state)
        {
            if (state.Active || state.Closed || !owner.IsLoaded) return;
            state.Active = true; state.CancelReason = null;
            state.Cancellation = new CancellationTokenSource();
            var cancellation = state.Cancellation;
            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Drained = drained;
            state.Context = RandyPreviewBuildContext(owner);
            state.Action.Content = "Cancel"; state.Action.IsEnabled = true;
            state.Launch.Content = "Building Randy previews…";
            state.Counts.Text = "Checking the complete taunt library…";
            state.Progress.Maximum = 1; state.Progress.Value = 0;
            state.Status.Text = "Waiting for the current Randy preview to stop…";
            state.Timer.Start();
            try
            {
                AutomaticTauntPreview automatic;
                if (automaticTauntPreviews.TryGetValue(owner, out automatic) && automatic.ForegroundRequested)
                    throw new InvalidOperationException("Wait for MAKE or Batch MAKE to start before building previews.");
                // This explicit build also drains a manually requested Randy
                // preview. Exclude this build from its own cancellation path.
                await CancelAndDrainAutomaticRandyPreviewAsync(owner, false, true);
                EnsureRandyPreviewBuildContext(owner, state, cancellation);
                if (!RandyProducerIdle(owner))
                    throw new InvalidOperationException("Wait for the current model or render to finish, then build missing previews.");
                string blender = AutomaticTauntField(owner, "BlenderPath") as string;
                if (string.IsNullOrWhiteSpace(blender) || !File.Exists(blender))
                    throw new InvalidOperationException("Choose Blender 5.2 or newer in Studio before building Randy previews.");
                var all = AutomaticTauntField(owner, "AllTaunts") as IEnumerable;
                if (all == null) throw new InvalidOperationException("The complete taunt library is not available.");
                var items = new List<RandyPreviewBuildItem>();
                foreach (object item in all)
                {
                    if (items.Count >= 20000) throw new InvalidOperationException("The taunt library exceeds the preview build limit.");
                    items.Add(new RandyPreviewBuildItem(AutomaticTauntKey(item),
                        Convert.ToString(item?.GetType().GetProperty("Name")?.GetValue(item, null))));
                }
                await RandyPreviewBuilder.RunAsync(items,
                    async (item, token) => await RunRandyPreviewBuildItemAsync(owner, state, cancellation, item, false) != null,
                    async (item, token) => {
                        var result = await RunRandyPreviewBuildItemAsync(owner, state, cancellation, item, true);
                        if (result == null) throw new InvalidOperationException("No Randy preview was saved.");
                    }, progress => {
                        if (state.Closed || !ReferenceEquals(state.Drained, drained)) return;
                        state.Counts.Text = progress.Completed + " / " + progress.Total + " · "
                            + progress.Cached + " cached · " + progress.Generated + " generated · " + progress.Failed + " failed";
                        state.Progress.Maximum = Math.Max(1, progress.Total); state.Progress.Value = progress.Completed;
                        state.Status.Text = (string.IsNullOrWhiteSpace(progress.Name) ? "" : progress.Name + "\n")
                            + (progress.Canceled && state.CancelReason != null ? state.CancelReason : progress.Message);
                    }, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!state.Closed) state.Status.Text = state.CancelReason ?? "Stopped. Completed previews are saved; build again to continue.";
            }
            catch (Exception error)
            {
                if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                if (!state.Closed) state.Status.Text = "Preview build stopped: " + error.Message;
            }
            finally
            {
                // All coordinator callbacks have completed, including the
                // producer's process drainage and restoration of native Busy.
                try
                {
                    state.Timer.Stop(); cancellation.Dispose();
                    if (ReferenceEquals(state.Cancellation, cancellation)) state.Cancellation = null;
                    if (ReferenceEquals(state.Drained, drained))
                    {
                        state.Active = false;
                        if (!state.Closed)
                        {
                            state.Launch.Content = "Build Randy previews…";
                            state.Action.Content = "Build missing"; state.Action.IsEnabled = true;
                            if (state.CloseRequested && state.Dialog != null) state.Dialog.Close();
                        }
                    }
                }
                finally { drained.TrySetResult(true); }
            }
        }

        private static async Task<RandyTauntPreviewResult> RunRandyPreviewBuildItemAsync(Window owner,
            RandyPreviewBuildControls state, CancellationTokenSource cancellation, RandyPreviewBuildItem item, bool render)
        {
            EnsureRandyPreviewBuildContext(owner, state, cancellation);
            try
            {
                var result = await PrepareRandyTauntPreviewAsync(owner, item.Key, message => {
                    if (!state.Closed && ReferenceEquals(state.Cancellation, cancellation) && !cancellation.IsCancellationRequested)
                        state.Status.Text = item.Name + "\n" + message;
                }, cancellation.Token, render, true);
                EnsureRandyPreviewBuildContext(owner, state, cancellation);
                return result;
            }
            catch (OperationCanceledException)
            {
                // A changed producer context can throw before the periodic UI
                // watcher notices. Stop the complete build, rather than count
                // every remaining taunt as a failed item in the old context.
                cancellation.Cancel();
                throw;
            }
            catch (Exception error)
            {
                // Extraction and validation use one shared Randy source. A
                // missing/broken source cannot be repaired by retrying it for
                // every remaining taunt in the library.
                if (render && !await Task.Run(() => RandyPreviewAssets.IsReady(RandyPreviewAssets.DefaultDirectory)))
                {
                    CancelRandyPreviewBuild(owner, "The shared Randy model could not be prepared: " + error.Message
                        + " Fix the model/game setup, then build missing previews again.");
                    throw new OperationCanceledException("The shared Randy source is unavailable.", error, cancellation.Token);
                }
                throw;
            }
        }

        private static string RandyPreviewBuildContext(Window owner)
        {
            return (DiscoveryEngineRoot ?? "") + "\n" + string.Join("\n", new[] { "Library", "Game", "BlenderPath" }
                .Select(name => AutomaticTauntField(owner, name) as string ?? ""));
        }

        private static bool RandyPreviewBuildContextCurrent(Window owner, RandyPreviewBuildControls state)
        {
            return !state.Closed && owner.IsLoaded && !DiscoveryDataRenderBlocked(owner)
                && !combinedGameSetups.Contains(owner) && !batchWindows.ContainsKey(owner)
                && string.Equals(state.Context, RandyPreviewBuildContext(owner), StringComparison.Ordinal);
        }

        private static void EnsureRandyPreviewBuildContext(Window owner, RandyPreviewBuildControls state,
            CancellationTokenSource cancellation)
        {
            if (!RandyPreviewBuildContextCurrent(owner, state))
                CancelRandyPreviewBuild(owner, "Stopped because the Discovery, game or Blender context changed. Build again when it is ready.");
            cancellation.Token.ThrowIfCancellationRequested();
            var unlock = owner.GetType().GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
            if (unlock == null || !(unlock.Invoke(owner, null) is bool allowed) || !allowed)
            {
                CancelRandyPreviewBuild(owner, "Sign in to build Randy previews. Completed previews are saved.");
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }

        private static bool RandyPreviewBuildActive(Window owner)
        {
            RandyPreviewBuildControls state;
            return randyPreviewBuildWindows.TryGetValue(owner, out state) && state.Active;
        }

        private static void CancelRandyPreviewBuild(Window owner, string reason)
        {
            RandyPreviewBuildControls state;
            if (!randyPreviewBuildWindows.TryGetValue(owner, out state) || !state.Active) return;
            state.CancelReason = reason;
            state.Cancellation?.Cancel();
            if (!state.Closed)
            { state.Status.Text = reason; state.Action.IsEnabled = false; }
        }

        private static async Task CancelAndDrainRandyPreviewBuildAsync(Window owner)
        {
            RandyPreviewBuildControls state;
            if (!randyPreviewBuildWindows.TryGetValue(owner, out state) || !state.Active) return;
            Task drainage = state.Drained.Task;
            CancelRandyPreviewBuild(owner, "Stopped for your next action. Completed previews are saved.");
            await drainage;
        }
    }
}
