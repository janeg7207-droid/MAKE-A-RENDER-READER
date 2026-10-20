using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static WeakReference gameModelWindow;

        private static void SetGameModelWindow(Window window)
        {
            gameModelWindow = new WeakReference(window);
            window.Closed += (sender, args) =>
            {
                if (gameModelWindow != null && ReferenceEquals(gameModelWindow.Target, window)) gameModelWindow = null;
            };
        }

        // The caller supplies a physical model key verified against character mapping.
        // A logical profile ID alone must never choose a different wrestler's stock body.
        public static Task<string> RequestGameModelAsync(RosterEntry entry, string stockFolderKey, Action<string> progress)
        { return RequestGameModelAsync(entry, stockFolderKey, progress, null); }

        internal static Task<string> RequestGameModelAsync(RosterEntry entry, string stockFolderKey, Action<string> progress, Action<Process> started)
        {
            var window = gameModelWindow == null ? null : gameModelWindow.Target as Window;
            return RequestGameModelAsync(window, entry, stockFolderKey, progress, started);
        }

        internal static async Task<string> RequestGameModelAsync(Window window, RosterEntry entry, string stockFolderKey, Action<string> progress, Action<Process> started)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (window == null) throw new InvalidOperationException("Open Gorilla Studio before loading a model from the game files.");
            if (!window.Dispatcher.CheckAccess())
                return await window.Dispatcher.InvokeAsync(new Func<Task<string>>(() => RequestGameModelAsync(window, entry, stockFolderKey, progress, started))).Task.Unwrap();

            Type type = window.GetType();
            var gameField = type.GetField("Game", InstanceMembers);
            string game = gameField == null ? null : gameField.GetValue(window) as string;
            if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
                throw new InvalidOperationException("Choose your WWE 2K26 game folder in Gorilla Studio before loading this model.");
            foreach (string name in new[] { "Busy", "GameBusy" })
            {
                var busy = type.GetField(name, InstanceMembers);
                if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window))
                    throw new InvalidOperationException("Wait for the current model or render to finish, then load this wrestler again.");
            }

            var studioField = type.GetField("Studio", InstanceMembers);
            string studio = studioField == null ? null : studioField.GetValue(window) as string;
            if (string.IsNullOrWhiteSpace(studio)) throw new MissingFieldException("This Gorilla Studio version has no supported engine location.");
            var baseField = type.GetField("BaseDir", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string baseDirectory = baseField == null ? studio : baseField.GetValue(null) as string;
            string indexPath = Path.Combine(baseDirectory ?? studio, "data", "gamepack", "stock_index.json");
            if (!File.Exists(indexPath)) throw new FileNotFoundException("Keep the original v1.0.2 data folder beside GorillaStudio.exe.", indexPath);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            var index = serializer.DeserializeObject(File.ReadAllText(indexPath)) as IDictionary<string, object>;
            GameModelFiles.ValidateKey(stockFolderKey, index == null ? null : index.Keys);

            MethodInfo run = type.GetMethod("RunPy", InstanceMembers, null,
                new[] { typeof(string), typeof(IEnumerable<string>), typeof(Action<string>), typeof(Action<Process>) }, null);
            if (run == null) throw new MissingMethodException("This Gorilla Studio version has no supported game-model extractor.");
            var gameBusy = type.GetField("GameBusy", InstanceMembers);
            if (gameBusy == null || gameBusy.FieldType != typeof(bool))
                throw new MissingFieldException("This Gorilla Studio version has no supported model-loading state.");
            string nameForDisplay = string.IsNullOrWhiteSpace(entry.Name) ? "this wrestler" : entry.Name;
            string startMessage = "Loading " + nameForDisplay + " from your game files…";
            var noteField = type.GetField("GamePickNote", InstanceMembers);
            var note = noteField == null ? null : noteField.GetValue(window) as TextBlock;
            string previousNote = note == null ? null : note.Text;
            bool reportProgress = true;
            Action<string> report = message =>
            {
                if (!reportProgress) return;
                SetModelStatus(window, message);
                try { if (progress != null) progress(message); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
            gameBusy.SetValue(window, true);
            try
            {
                report(startMessage);
                Action<string> onLine = line =>
                {
                    Match stage = Regex.Match(line ?? "", @"^EXTRACT (\d+)/(\d+)");
                    if (stage.Success) report(startMessage + " " + stage.Groups[1].Value + " of " + stage.Groups[2].Value);
                };
                var task = run.Invoke(window, new object[]
                {
                    Path.Combine(studio, "pipeline", "extract_stock.py"),
                    new[] { stockFolderKey }, onLine, started
                }) as Task;
                if (task == null) throw new InvalidOperationException("The game's extractor did not return a completion task.");
                await task;
                var resultProperty = task.GetType().GetProperty("Result");
                object result = resultProperty == null ? null : resultProperty.GetValue(task, null);
                if (result == null) throw new InvalidOperationException("The game's extractor returned no result.");
                var code = result.GetType().GetField("Item1");
                var lines = result.GetType().GetField("Item2");
                if (code == null || lines == null || code.FieldType != typeof(int))
                    throw new InvalidOperationException("The game's extractor returned an unsupported result.");
                int exitCode = (int)code.GetValue(result);
                var output = lines.GetValue(result) as List<string>;
                try { return GameModelFiles.StockDirectory(exitCode, output); }
                catch
                {
                    var saveError = type.GetMethod("SaveErrorReport", InstanceMembers, null, new[] { typeof(List<string>) }, null);
                    try { if (saveError != null && output != null) saveError.Invoke(window, new object[] { output }); }
                    catch (TargetInvocationException) { }
                    throw;
                }
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException("The model could not be loaded from the game files: " + (error.InnerException ?? error).Message,
                    error.InnerException ?? error);
            }
            finally
            {
                reportProgress = false;
                gameBusy.SetValue(window, false);
                if (note != null) note.Text = previousNote;
            }
        }

        private static void SetModelStatus(Window window, string message)
        {
            foreach (string name in new[] { "StageText", "GamePickNote" })
            {
                var field = window.GetType().GetField(name, InstanceMembers);
                var text = field == null ? null : field.GetValue(window) as TextBlock;
                if (text != null) text.Text = message;
            }
        }
    }
}
