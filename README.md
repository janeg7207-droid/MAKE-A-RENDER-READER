# Gorilla Studio v1.0.2 — Discovery Roster Experiment

The updated loader reads the **full Discovery roster from CharProfileTable.jsfb and Sdb/ENG.sdb**, including newly added character IDs and names. **Scan models folder…** finds models already in your extracted mod collection and remembers the location. **Fast Preview**, **Fast MAKE**, and a preview cache reduce repeated rendering work. The helper is version **1.5.0**; the original application is Gorilla Studio v1.0.2.

## Download

- [Full program ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental.zip)
- [Small update ZIP for an existing v1.0.2 copy](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Update.zip)
- [Source ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Source.zip)

Extract the small update into the folder containing GorillaStudio.exe in a **separate copy of v1.0.2**. Replace the executable and helper DLL together, keeping the original engine, data and plugin folders. It also updates the previous experimental v1.0.2 build. It does not apply to v1.0.

The full program ZIP uses Git LFS. Use the download link above, or install Git LFS before cloning. SHA-256 checksums are in downloads.

## Use

1. Keep your extracted Discovery folders together:

       discovery_data/
         Roster/CharProfileTable.jsfb
         Characters/CharacterMapping.jsfb
         Characters/<your character folders>/
         Sdb/ENG.sdb

2. Click **Load Discovery roster…** and choose discovery_data. Selecting its Characters or Roster folder also works. Subsequent clicks open that roster directly; **Change…** chooses a different data folder.
3. Click **Scan models folder…** and choose the main folder containing your already extracted wrestler mods. It scans nested folders, attaches unique matching models, and remembers this location for the roster. You can paste a path, drag a folder into the location box, or browse using Explorer's address bar and search. Keep model files extracted; the renderer uses .mcd models.
4. Search by name, wrestler ID or model-folder name. **Models in data** filters to local models. Select a wrestler and click **Load selected wrestler**. Unique discovered models load directly; multiple matches open a searchable model list. **Choose model folder…** also opens the model list. Plain names need no numeric prefix, and unresolved models stay visible for you to choose their wrestler slot. **Refresh data** rescans remembered locations.
5. Continue with the original Preview / Make / Install controls. Windows, .NET Framework 4.8, Blender 5.2 or newer, and the original WWE 2K26/plugin setup are required.

## Faster previews and renders

Fast Preview and Fast MAKE are enabled initially. Click **Speed…** next to Preview to change either setting or restore the original quality.

| Operation | Original defaults | Faster mode | Calculated rendering work |
| --- | --- | --- | --- |
| Wrestler Preview | 384 × 384, 16 samples | 256 × 256, 8 samples | 77.8% fewer pixel-samples |
| MAKE GORILLA RENDER | 1024 × 1024, 64 samples | 1024 × 1024, 32 samples | 50% fewer samples |

These are work calculations, **not measured time savings**. Model loading, Blender startup, encoding, GPU load and other fixed costs still take time. Preview retains its animation frames and playback rate. Fast MAKE retains full output resolution and animation timing, with fewer lighting samples; untick it for the original shadow quality. Existing inherited renderer options remain in effect except for the faster mode's explicit overrides.

Unchanged previews reopen from an on-disk cache without starting Blender. Changes to character inputs, textures, roster context, taunt/settings, Blender/engine metadata or mode invalidate that cache. Cached images are separate copies, so another preview cannot overwrite them. Damaged or unreadable cache entries fall back to rendering. The cache retains at most 64 entries. Full MAKE always uses the original engine.

Automatic full rendering after Preview is off initially. This prevents a full render from occupying the GPU while browsing. Turn **Start MAKE automatically after Preview** back on in Speed… if preferred. Changing quality stops an existing automatic render before it can be reused with the wrong setting.

The small timer below Preview displays the actual request time, with initial model preparation separate. For an old/new comparison, load your wrestler, choose a taunt, click **Speed… → Compare old / new times**. Leave **Also compare full MAKE** checked to measure both operations. The comparison runs one warm-up, then original and faster previews and renders, bypassing preview cache hits. It reports seconds, seconds saved, percentage change and speed ratio; slower results are reported as slower. This takes several renders. Results and comparison files stay under `%LOCALAPPDATA%/GorillaStudio/NameFolders/benchmarks`; ordinary render output and installed game files are untouched. The latest text report is `performance-comparison.txt` in the NameFolders folder.

These are individual measurements in original-then-fast order, using a shared prepared model. The first full-resolution MAKE can require additional setup; GPU load and run order affect results. Real Windows/GPU timings have **not** been measured in this Linux build environment.

After adding characters, save your updated CharProfileTable.jsfb and Sdb/ENG.sdb, put their extracted models inside a remembered models folder, then click **Refresh data**. It rereads the current roster and names, rescans the model folders, and includes new IDs. For an imported JSON roster, Refresh data reloads that same JSON file during the current session. Renamed characters replace stale saved labels; conflicting saved model choices require selection. Reopening Discovery also rereads its current files.

A roster profile can exist without its model in the selected data. **Model not found · Scan models folder…** means that the name and ID were read, but no matching extracted model has been located. Point the scan at the parent folder containing your actual mod folders once, rather than browsing separately for PAC, AJ Styles and every other character. The supplied Discovery archive itself contains ten extracted character models; the other profiles need their models from your collection or the installed game.

Existing model associations remain authoritative. Library matching prefers exact names; duplicate matches require your choice. It does not use a custom wrestler's logical ID to guess a stock body. Verified installed-game models retain the original extractor. You can explicitly map any discovered model to another profile with **Use folder for another wrestler…**.

ENG.sdb supplies the readable character names. Keep your own current file in Sdb/ENG.sdb. Missing localized strings display as “Wrestler <ID>”; the loader never substitutes a localization key as a wrestler name.

Only **JSON and JSFB** are accepted by **Load roster file…**. SDB is read automatically as companion name data. JSON supports arrays such as [{"id":1506,"name":"Flammer"}], common roster wrappers, and name-to-ID or ID-to-name maps. Actual wrestler IDs must be 1–65535. The included rosters/Discovery_roster.json is a portable name/ID export; point the loader at your Discovery folder to discover its models.

Dropping a character folder also opens the picker, keeping that folder as the model source. The selected logical roster ID controls the target slot and render links; the physical source ID controls stock-body extraction. For example, the supplied mapping associates physical folder 312_CM_Punk with roster ID 1481.

Saved manual entries and model choices reopen with the same roster. Freshly scanned model mappings take precedence. Older saved folder-derived entries are refreshed to their actual mapped IDs. Settings and NTFS compatibility links are stored under %LOCALAPPDATA%/GorillaStudio/NameFolders. Original character folders keep their names and contents. Plain-name links require a local Windows drive supporting NTFS junctions.

## Validation

- The supplied data loads **1,457 roster profiles**, with **1,308 localized names**, and all **10 extracted model folders**. Video examples NICK JACKSON '14 (204), DAX HARWOOD (211), and CASH WHEELER (213) match the uploaded SDB.
- **411 portable checks pass**: 166 roster/model checks and 245 preview-cache, process-setting, bridge and benchmark checks. These include callback replay, model/texture changes, failed renders, full-MAKE cache bypass, original-quality opt-out, measured timing arithmetic, native access guards and benchmark output isolation. Binding 1,457 profiles against 5,000 indexed model folders takes about 20 ms in the build environment; this is separate from rendering performance.
- Growth tests append valid new profiles and SDB strings to a separate copy of the actual data: **1,457 → 1,458 → 1,459** profiles. A copied real model binds to new logical ID 4000 with physical source 1506; edited JSON files reload new characters; high new ID 65527 preserves existing reserved IDs. Uploaded originals remain unchanged.
- Portable importer/session, binary mapping, and extraction-output tests cover profile decoding, malformed data, ambiguous paths, saved settings, legacy entries and explicit slot selection. Both the .NET Framework 4.8 helper and patcher build without warnings or errors.
- A copied real character in a plain-name folder remains visible before assignment, then loads and reopens with an explicit added roster entry. Original MCD bytes stay unchanged.
- The original v1.0.2 engine verifies physical 312 → logical 1481 and physical 123 → logical 910. It retains the original model/body source while using the selected profile's render links and gender. The 1481 case selects 148100_CM_PUNK_GM.
- A real render attempt loads the added Flammer model and reaches original stock-body extraction, then stops because this environment lacks WWE 2K26 / oo2core_9_win64.dll. Complete rendering and Windows GUI/junction interaction remain unverified.
- The patcher verifies a five-method allowlist: the window constructor, folder-loading state machine, Go state machine, process-launch state machine and automatic-render entry point. All other method bodies, authentication/licensing and managed/native resources are preserved. Engine binaries remain unchanged; faster detail is passed only to each applicable render process.

Detailed results are included in TEST_RESULTS.json inside the ZIPs.

## Build

Requires the .NET 8 SDK and the supplied original v1.0.2 release. Building on Linux is supported; running requires Windows.

    dotnet build src/GorillaStudio.NameFolders/GorillaStudio.NameFolders.csproj -c Release
    dotnet build tools/GorillaStudioPatcher/GorillaStudioPatcher.csproj -c Release
    dotnet run --project tests/RosterTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/DiscoveryScannerTests -c Release -- /path/to/discovery_data /path/to/Sdb/ENG.sdb /path/to/original/data/gamepack/stock_index.json
    dotnet run --project tests/GameModelBridgeTests -c Release
    dotnet run --project tests/ModelLibraryTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/ModelBindingTests -c Release
    dotnet run --project tests/RosterGrowthTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/PreviewCacheTests -c Release
    dotnet run --project tests/PreviewProcessTests -c Release
    dotnet run --project tests/PreviewRenderBridgeTests -c Release
    dotnet run --project tests/RenderBenchmarkTests -c Release
    dotnet tools/GorillaStudioPatcher/bin/Release/net8.0/GorillaStudioPatcher.dll /path/to/original/GorillaStudio.exe src/GorillaStudio.NameFolders/bin/Release/net48/GorillaStudio.NameFolders.dll /path/to/new/GorillaStudio.exe

Copy the original release's remaining files and the helper DLL into the new output folder. The patcher requires a new output path and verifies the original executable's supported layout.

The Python validators in tests use the original engine bytecode with CPython 3.14, NumPy 2.4.3 and Pillow. Run each with --help for arguments. validate_user_data.py performs an actual render attempt; validate_mapped_engine.py observes logical-slot handling and stops deliberately before game extraction. Neither script tests the Windows GUI.
