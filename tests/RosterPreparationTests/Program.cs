using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    internal static void Equal<T>(T expected, T actual)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("Expected " + expected + "; received " + actual);
    }

    private static int Main()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "gorilla-roster-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", temporary);
        State.Root = temporary;
        try
        {
            using (var context = new PumpContext()) context.Run(CheckAsync);
            Console.WriteLine(checks + " roster preparation checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static async Task CheckAsync()
    {
        Equal(true, FolderBridge.StorageDirectory.StartsWith(State.Root, StringComparison.Ordinal));
        State.Reset();
        State.BlockPictures = true;
        State.MutateOnDispose = true;
        State.MetadataSourceOverride = Path.Combine(State.Root, "actual selected JSFB data");
        State.MetadataWarnings.Add("One invalid localized name was skipped; its roster ID is retained.");
        Task<string> loading = FolderBridge.PrepareRosterAsync(State.Root, "external-roster.json", null, actualRoot => {
            State.MetadataReadyCount++; State.MetadataReadyThread = Thread.CurrentThread.ManagedThreadId;
            State.MetadataReadySource = actualRoot;
            Equal(0, State.PickerCount); Equal(0, State.PictureCalls); Equal(0, State.Commits.Count);
        });
        await PicturesStarted();
        Equal(false, loading.IsCompleted);
        Equal(State.UiThread, Thread.CurrentThread.ManagedThreadId);
        // Reaching this continuation while the picture scan is held proves the
        // actual FolderBridge async path left the dispatcher's thread available.
        State.ReleasePictures.Set();
        string alias = await loading;
        Equal(Path.Combine(FolderBridge.StorageDirectory, "links", "test-plan-pictures-v2", "Characters", "1506_Selected_Mod"), alias);
        Equal(State.UiThread, State.PickerThread);
        Equal(State.UiThread, State.DisposeThread);
        Equal(1, State.PickerCount);
        Equal(1, State.QuickOpenCount);
        Equal(0, State.FullOpenCount);
        Equal(1, State.MetadataReadyCount);
        Equal(State.UiThread, State.MetadataReadyThread);
        Equal(State.UiThread, State.MetadataWarningsThread);
        Equal(State.MetadataWarnings.Single(), State.PickerMetadataWarnings.Single());
        Equal(1, State.MetadataReadyAtPicker);
        Equal(State.MetadataSourceOverride, State.MetadataReadySource);
        Equal(State.PickerSource, State.MetadataReadySource);
        Equal(false, string.Equals(State.Root, State.MetadataReadySource, StringComparison.Ordinal));
        Equal(5, State.Commits.Count);
        Equal(2, State.AttireInventoryCount);
        Equal(true, State.RegisteredAttireContext);
        Equal(State.Root, State.RegisteredAttireSource);
        Equal(State.Root, State.RegisteredAttireRoots.Single());
        Equal(State.RosterTable, State.RegisteredModelsContext);
        Equal(true, State.Commits.All(thread => thread == State.UiThread));
        Equal(true, State.DiskThreads.Count >= 8);
        Equal(true, State.DiskThreads.All(thread => thread != State.UiThread));
        Equal("Selected Mod", State.PlannedEntry.Name);
        Equal("Selected Mod", State.Registered.Name);
        Equal(4000, State.Registered.Id);
        Equal(1506, State.AttireId);
        Equal(State.RosterTable, State.Registered.RosterTable);
        Equal(true, State.Registered.IsMod);
        Equal("Chosen model folder", State.Registered.Source);
        Equal("1506_Original_Stock", State.Registered.StockFolder);
        Equal("mutated after close", State.Entry.Name);
        Equal(State.RosterTable, File.ReadAllText(Path.Combine(FolderBridge.StorageDirectory, "roster-source.txt")));
        List<RosterEntry> saved = JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(Path.Combine(FolderBridge.StorageDirectory, "roster.json")));
        Equal("Selected Mod", saved.Single().Name);
        Equal(4000, saved.Single().Id);
        Equal(1506, saved.Single().SourceId);
        Equal("1506_Original_Stock", saved.Single().StockFolder);

        // An older folder binding can point to an inactive mapped costume.
        // Resolve its unambiguous active default before creating native aliases.
        State.Reset();
        string activeSource = Directory.CreateDirectory(Path.Combine(State.Root, "Active default source")).FullName;
        State.Entry.Source = "Mapped model folder";
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = activeSource, SourceId = 1800, IsDefault = true });
        Equal(true, await FolderBridge.PrepareRosterAsync(State.Root, null) != null);
        Equal(activeSource, State.PlannedEntry.Folder);
        Equal(activeSource, State.RegisteredAttireSource);
        Equal(1800, State.AttireId);
        Equal(4000, State.Registered.Id);

        State.Reset();
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = activeSource, SourceId = 1800, IsDefault = true });
        Equal(true, await FolderBridge.PrepareRosterAsync(State.Root, null) != null);
        Equal(State.Root, State.PlannedEntry.Folder);
        Equal(1506, State.AttireId);

        State.Reset();
        State.ChooseCharacterFolder = false;
        State.Entry.Source = "Profile";
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = activeSource, SourceId = 1800, IsDefault = true });
        Equal(true, await FolderBridge.PrepareRosterAsync(State.Root, null) != null);
        Equal(State.Root, State.PlannedEntry.Folder);
        Equal(1506, State.AttireId);

        State.Reset();
        State.Entry.Source = "Mapped model folder";
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = activeSource, SourceId = 1800 });
        bool inactiveRejected = false;
        try { await FolderBridge.PrepareRosterAsync(State.Root, null); }
        catch (InvalidDataException) { inactiveRejected = true; }
        Equal(true, inactiveRejected);
        Equal(0, State.PictureCalls);
        Equal(0, State.Commits.Count);

        State.Reset();
        State.Entry.Source = "Mapped model folder";
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = activeSource, SourceId = 1800, IsDefault = true });
        State.DiscoverySources.Add(new DiscoveryAttireSource { SourceFolder = Path.Combine(State.Root, "Another default source"), SourceId = 1801, IsDefault = true });
        inactiveRejected = false;
        try { await FolderBridge.PrepareRosterAsync(State.Root, null); }
        catch (InvalidDataException) { inactiveRejected = true; }
        Equal(true, inactiveRejected);
        Equal(0, State.PictureCalls);
        Equal(0, State.Commits.Count);

        // A successfully refreshed picker invalidates animation lookup on the UI
        // thread even when the user cancels instead of committing a wrestler.
        State.Reset(); State.CancelPicker = true; State.Refreshed = true;
        Equal((string)null, await FolderBridge.PrepareRosterAsync(State.Root, null));
        Equal(1, State.Invalidations);
        Equal(State.UiThread, State.InvalidationThread);
        Equal(0, State.Commits.Count);
        Equal(0, State.PictureCalls);

        State.Reset();
        Equal((string)null, await FolderBridge.PrepareRosterAsync(State.Root, null, () => false,
            actualRoot => State.MetadataReadyCount++));
        Equal(0, State.PickerCount);
        Equal(0, State.PictureCalls);
        Equal(0, State.Commits.Count);
        Equal(0, State.MetadataReadyCount);

        State.Reset(); State.ThrowMetadata = true;
        bool metadataFailed = false;
        try { await FolderBridge.PrepareRosterAsync(State.Root, null, null, actualRoot => State.MetadataReadyCount++); }
        catch (InvalidDataException) { metadataFailed = true; }
        Equal(true, metadataFailed);
        Equal(0, State.MetadataReadyCount);
        Equal(0, State.PickerCount);
        Equal(0, State.PictureCalls);
        Equal(0, State.Commits.Count);

        State.Reset(); State.CancelPicker = true;
        State.MetadataWarnings.Add("Names are unavailable; wrestler IDs are still loaded.");
        Equal((string)null, await FolderBridge.PrepareRosterAsync(State.Root, null, null, actualRoot => State.MetadataReadyCount++));
        Equal(1, State.MetadataReadyCount);
        Equal(1, State.MetadataReadyAtPicker);
        Equal(State.MetadataWarnings.Single(), State.PickerMetadataWarnings.Single());
        Equal(0, State.PictureCalls);
        Equal(0, State.Commits.Count);

        State.Reset(); State.BlockPictures = true;
        bool current = true;
        loading = FolderBridge.PrepareRosterAsync(State.Root, null, () => current);
        await PicturesStarted(); current = false; State.ReleasePictures.Set();
        Equal((string)null, await loading);
        Equal(0, State.Commits.Count);
        Equal((RosterEntry)null, State.Registered);

        State.Reset(); State.ThrowPictures = true;
        bool failed = false;
        try { await FolderBridge.PrepareRosterAsync(State.Root, null); }
        catch (IOException) { failed = true; }
        Equal(true, failed);
        Equal(0, State.Commits.Count);
        Equal((RosterEntry)null, State.Registered);

        // Corrupt remembered-roster warnings remain on the UI thread, while the
        // failing read and fallback session construction run on a worker.
        State.Reset(); State.ThrowRead = true;
        Equal(true, await FolderBridge.PrepareRosterAsync(State.Root, null) != null);
        Equal(1, State.WarningCount);
        Equal(State.UiThread, State.WarningThread);
        Equal(true, State.DiskThreads.All(thread => thread != State.UiThread));

        // Preserve the existing synchronous entry points used by the native IL
        // hook; do not replace their modal contract with an async operation.
        State.Reset(); State.ExpectWorkers = false;
        State.MetadataWarnings.Add("An invalid SDB name was skipped; valid rows remain usable.");
        Equal(true, FolderBridge.PrepareRoster(State.Root, null) != null);
        Equal(1, State.QuickOpenCount);
        Equal(0, State.FullOpenCount);
        Equal(1, State.PickerMetadataWarnings.Count);
        Equal(State.UiThread, State.PickerThread);
        Equal(true, State.Commits.All(thread => thread == State.UiThread));
        Equal(true, State.DiskThreads.All(thread => thread == State.UiThread));
        State.Reset(); State.ExpectWorkers = false;
        State.MetadataWarnings.Add("A malformed SDB row does not block a dropped character folder.");
        Equal(true, FolderBridge.PrepareFolder(State.Root) != null);
        Equal(1, State.QuickOpenCount);
        Equal(0, State.FullOpenCount);
        Equal(State.MetadataWarnings.Single(), State.PickerMetadataWarnings.Single());
        Equal(State.UiThread, State.MetadataWarningsThread);
        State.Reset(); FolderBridge.SetPreparedForTest("already-prepared");
        Equal("already-prepared", FolderBridge.PrepareFolder("already-prepared"));
        Equal(0, State.PickerCount);
        Equal(0, State.DiskThreads.Count);
    }

    private static async Task PicturesStarted()
    {
        for (int i = 0; i < 500 && Volatile.Read(ref State.PictureCalls) == 0; i++) await Task.Delay(2);
        Equal(1, Volatile.Read(ref State.PictureCalls));
    }
}

internal sealed class PumpContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> actions = new BlockingCollection<Action>();
    public override void Post(SendOrPostCallback callback, object state) { actions.Add(() => callback(state)); }
    internal void Run(Func<Task> operation)
    {
        SetSynchronizationContext(this);
        Task task = operation();
        while (!task.IsCompleted)
        {
            Action action;
            if (actions.TryTake(out action, 100)) action();
        }
        task.GetAwaiter().GetResult();
        SetSynchronizationContext(null);
    }
    public void Dispose() { actions.Dispose(); }
}

internal static class State
{
    internal static string Root;
    internal static string RosterTable { get { return Path.Combine(Root, "Roster", "CharProfileTable.jsfb"); } }
    internal static int UiThread, PickerThread, DisposeThread, PickerCount, PictureCalls, AttireId;
    internal static int Invalidations, InvalidationThread, WarningCount, WarningThread;
    internal static int QuickOpenCount, FullOpenCount, MetadataReadyCount, MetadataReadyThread, MetadataReadyAtPicker, MetadataWarningsThread;
    internal static string MetadataSourceOverride, MetadataReadySource, PickerSource;
    internal static bool ExpectWorkers, BlockPictures, MutateOnDispose, CancelPicker, Refreshed, ThrowPictures, ThrowRead, ThrowMetadata;
    internal static bool ChooseCharacterFolder;
    internal static readonly List<string> MetadataWarnings = new List<string>();
    internal static readonly List<string> PickerMetadataWarnings = new List<string>();
    internal static readonly ManualResetEventSlim ReleasePictures = new ManualResetEventSlim(false);
    internal static readonly List<int> DiskThreads = new List<int>();
    internal static readonly List<int> Commits = new List<int>();
    internal static RosterEntry Entry, PlannedEntry, Registered;
    internal static int AttireInventoryCount;
    internal static DiscoveryAttireCatalogResult AttireResult;
    internal static bool RegisteredAttireContext;
    internal static string RegisteredAttireSource;
    internal static string RegisteredModelsContext;
    internal static string[] RegisteredAttireRoots;
    internal static readonly List<DiscoveryAttireSource> DiscoverySources = new List<DiscoveryAttireSource>();
    internal static void Reset()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        PickerThread = DisposeThread = PickerCount = PictureCalls = AttireId = 0;
        Invalidations = InvalidationThread = WarningCount = WarningThread = 0;
        QuickOpenCount = FullOpenCount = MetadataReadyCount = MetadataReadyThread = MetadataReadyAtPicker = MetadataWarningsThread = 0;
        MetadataSourceOverride = MetadataReadySource = PickerSource = null;
        MetadataWarnings.Clear(); PickerMetadataWarnings.Clear();
        ExpectWorkers = true; BlockPictures = MutateOnDispose = CancelPicker = Refreshed = ThrowPictures = ThrowRead = ThrowMetadata = false;
        ChooseCharacterFolder = true;
        ReleasePictures.Reset(); DiskThreads.Clear(); Commits.Clear(); Registered = PlannedEntry = null;
        AttireInventoryCount = 0; AttireResult = null; RegisteredAttireContext = false; RegisteredAttireSource = null; RegisteredModelsContext = null;
        DiscoverySources.Clear();
        Entry = new RosterEntry { Id = 4000, Name = "Selected Mod", Folder = Root, RosterTable = RosterTable,
            SourceId = 1506, IsMod = true, Source = "Chosen model folder", StockFolder = "1506_Original_Stock" };
    }
    internal static void Disk()
    {
        int thread = Thread.CurrentThread.ManagedThreadId;
        lock (DiskThreads) DiskThreads.Add(thread);
        if (ExpectWorkers && thread == UiThread) throw new InvalidOperationException("Disk work blocked the UI thread.");
    }
    internal static void Commit()
    {
        int thread = Thread.CurrentThread.ManagedThreadId;
        if (thread != UiThread) throw new InvalidOperationException("Selection registry escaped the UI thread.");
        Commits.Add(thread);
    }
}

namespace System.Windows.Forms
{
    public enum DialogResult { Cancel, OK }
    public enum MessageBoxButtons { OK }
    public enum MessageBoxIcon { Error, Warning }
    public static class MessageBox
    {
        public static void Show(string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        { State.WarningCount++; State.WarningThread = Thread.CurrentThread.ManagedThreadId; }
    }
}

namespace System.Web.Script.Serialization
{
    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }
        public string Serialize(object value) { State.Disk(); return JsonSerializer.Serialize(value); }
    }
}

namespace GorillaStudio.NameFolders
{
    public sealed class RosterEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public string RosterTable { get; set; }
        public int SourceId { get; set; }
        public bool IsMod { get; set; }
        public string Source { get; set; }
        public string StockFolder { get; set; }
    }
    public sealed class ModelCandidate
    {
        public string Folder { get; set; }
        public string Name { get; set; }
        public int SourceId { get; set; }
    }
    public sealed class DiscoveryAttireSource
    {
        public string SourceFolder { get; set; }
        public bool IsDefault { get; set; }
        public int SourceId { get; set; }
    }
    public sealed class DiscoveryAttireReference
    {
        public int LogicalId, MappingId, CostumeIndex;
        public uint CostumeKey;
        public ulong ModelHash;
        public string DisplayName, Issue;
        public bool IsDefault;
    }
    public sealed class AlternateAttire
    {
        public string DiscoveryKey, DisplayName, UnavailableReason, RelativePath;
        public ulong DiscoveryModelHash;
        public bool IsDefault;
        public bool IsAvailable { get { return string.IsNullOrWhiteSpace(UnavailableReason); } }
    }
    internal static class DiscoveryAttireOptions
    { internal static bool IsLocalChoice(string alias, string source, string relative) { return false; } }
    public sealed class DiscoveryAttireCatalogResult
    {
        public List<DiscoveryAttireSource> Sources { get; } = new List<DiscoveryAttireSource>();
        public List<DiscoveryAttireReference> References { get; } = new List<DiscoveryAttireReference>();
    }
    internal static class DiscoveryAttireCatalog
    {
        internal static DiscoveryAttireCatalogResult Find(RosterEntry entry, IEnumerable<ModelCandidate> models, string table, CancellationToken token)
        {
            State.Disk(); State.AttireInventoryCount = models.Count();
            State.AttireResult = new DiscoveryAttireCatalogResult();
            State.AttireResult.Sources.AddRange(State.DiscoverySources);
            return State.AttireResult;
        }
    }
    public sealed class RosterSessionData
    {
        public List<RosterEntry> Entries;
        public bool ChooseCharacterFolder;
        public string DiscoveryTable;
        public List<string> Warnings = new List<string>();
    }
    internal static class RosterImport
    {
        internal static List<RosterEntry> LoadFile(string path)
        {
            State.Disk();
            if (State.ThrowRead) throw new IOException("Unreadable saved roster");
            return JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(path));
        }
    }
    internal static class RosterSession
    {
        internal static RosterSessionData Open(string source, List<RosterEntry> saved)
        { return RosterLaunch.Open(source, saved, null, null); }
        internal static RosterSessionData OpenQuick(string source, List<RosterEntry> saved)
        { return RosterLaunch.OpenQuick(source, saved, null, null); }
    }
    internal static class RosterLaunch
    {
        internal static RosterSessionData Open(string root, IEnumerable<RosterEntry> saved, string file, string bundled)
        {
            State.FullOpenCount++;
            State.Disk(); return new RosterSessionData { Entries = new List<RosterEntry> { State.Entry },
                ChooseCharacterFolder = State.ChooseCharacterFolder, DiscoveryTable = State.RosterTable };
        }
        internal static RosterSessionData OpenQuick(string root, IEnumerable<RosterEntry> saved, string file, string bundled)
        {
            State.QuickOpenCount++; State.Disk();
            if (State.ThrowMetadata) throw new InvalidDataException("Invalid profile metadata");
            return new RosterSessionData { Entries = new List<RosterEntry> { State.Entry },
                ChooseCharacterFolder = State.ChooseCharacterFolder, DiscoveryTable = State.RosterTable, Warnings = State.MetadataWarnings.ToList() };
        }
    }
    internal static class RosterAccess
    {
        internal static string RootFor(string path)
        { State.Disk(); return path == State.RosterTable ? State.MetadataSourceOverride ?? State.Root : State.Root; }
    }
    internal sealed class WrestlerPicker : IDisposable
    {
        public List<RosterEntry> Entries;
        public RosterEntry SelectedEntry { get { return State.Entry; } }
        public string SelectedFolder { get { return State.Root; } }
        public string CurrentRosterFile { get { return State.RosterTable; } }
        internal List<string> CurrentModelSearchRoots { get { return new List<string> { State.Root }; } }
        internal string CurrentModelsContext { get { return State.RosterTable ?? Path.GetFullPath(State.Root); } }
        public bool DataRefreshed { get { return State.Refreshed; } }
        internal List<ModelCandidate> CurrentModelInventory { get { return new List<ModelCandidate> {
            new ModelCandidate { Folder = State.Root, Name = "Default", SourceId = 1506 },
            new ModelCandidate { Folder = Path.Combine(State.Root, "Second costume source"), Name = "Alternate", SourceId = 1506 }
        }; } }
        public WrestlerPicker(string source, List<RosterEntry> entries, bool chooseFolder, string file = null)
        { Entries = entries; State.PickerSource = source; }
        internal void SetMetadataWarnings(IEnumerable<string> warnings)
        {
            State.MetadataWarningsThread = Thread.CurrentThread.ManagedThreadId;
            State.PickerMetadataWarnings.AddRange(warnings ?? Enumerable.Empty<string>());
        }
        public System.Windows.Forms.DialogResult ShowDialog()
        {
            State.MetadataReadyAtPicker = State.MetadataReadyCount;
            State.PickerThread = Thread.CurrentThread.ManagedThreadId; State.PickerCount++;
            return State.CancelPicker ? System.Windows.Forms.DialogResult.Cancel : System.Windows.Forms.DialogResult.OK;
        }
        public void Dispose()
        {
            State.DisposeThread = Thread.CurrentThread.ManagedThreadId;
            if (State.MutateOnDispose) State.Entry.Name = "mutated after close";
        }
    }
    internal static class DirectoryLinks
    {
        internal static string Resolve(string path) { State.Disk(); return path; }
        internal static void EnsureJunction(string path, string target) { State.Disk(); Directory.CreateDirectory(path); }
    }
    internal static class FolderNames
    {
        internal static int? ExistingId(string name) { return null; }
        internal static string DisplayName(string name) { return name; }
    }
    internal sealed class CharacterFolderPlan
    {
        public string RosterDirectory, SourceFolder, DataRoot, CacheKey, AliasName;
        public bool UseOriginal = false;
    }
    internal static class CharacterFolders
    {
        internal static CharacterFolderPlan Plan(string source, RosterEntry entry)
        {
            State.Disk(); State.PlannedEntry = entry;
            return new CharacterFolderPlan { SourceFolder = source, DataRoot = State.Root,
                RosterDirectory = Path.Combine(State.Root, "Roster"), CacheKey = "test-plan", AliasName = "1506_Selected_Mod" };
        }
        internal static int? PhysicalId(string source) { State.Disk(); return null; }
    }
    internal sealed class DirectAttirePlan { public bool WrapDirectModel = false; public string CacheSuffix = null, BaseModelFolder = null; }
    internal static class AutomaticAttire
    {
        internal static string CharacterRoot(string source, int id) { return source; }
        internal static DirectAttirePlan Plan(string source, int id)
        { State.Disk(); State.AttireId = id; return new DirectAttirePlan(); }
    }
    internal static class DiscoveryRenderOverlay
    {
        internal static void Prepare(string root, string table, Dictionary<ulong, string> pictures) { State.Disk(); }
    }
    public static partial class FolderBridge
    {
        private static string preparedForLoad, lastDiscoveryRoot;
        internal static void SetPreparedForTest(string folder) { preparedForLoad = folder; }
        private static Dictionary<ulong, string> PrepareRenderImages(CharacterFolderPlan plan, int id)
        {
            State.Disk(); Interlocked.Increment(ref State.PictureCalls);
            if (State.BlockPictures && !State.ReleasePictures.Wait(5000)) throw new TimeoutException("Picture scan was not released.");
            if (State.ThrowPictures) throw new IOException("Picture preparation failed");
            return new Dictionary<ulong, string>();
        }
        private static void RememberRenderTable(string alias, string table) { State.Commit(); }
        private static void RememberPreparedRender(string alias, CharacterFolderPlan plan, RosterEntry entry, Dictionary<ulong, string> pictures) { State.Commit(); }
        private static void RegisterDiscoveryAttires(string alias, RosterEntry entry, IEnumerable<ModelCandidate> inventory,
            DiscoveryAttireCatalogResult result, string sourceFolder = null, IEnumerable<string> modelRoots = null, string rosterContext = null)
        {
            State.Commit(); State.RegisteredAttireContext = ReferenceEquals(result, State.AttireResult);
            State.RegisteredAttireSource = sourceFolder;
            State.RegisteredAttireRoots = modelRoots?.ToArray() ?? new string[0];
            State.RegisteredModelsContext = rosterContext;
        }
        private static void ResolveDiscoveryAttireSources(DiscoveryAttireCatalogResult result, CancellationToken token)
        { State.Disk(); }
        private static void RememberDirectAttireSource(string alias, string source) { }
        private static void RegisterSelection(string alias, RosterEntry entry) { State.Commit(); State.Registered = entry; }
        private static void QueueModelDiscoveryTaunts(string alias, string root) { State.Commit(); }
        internal static void InvalidateTauntContexts() { State.Invalidations++; State.InvalidationThread = Thread.CurrentThread.ManagedThreadId; }
    }
}
