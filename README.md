# Gorilla Studio v1.0.2 — Discovery Roster Experiment

The updated loader reads the **full Discovery roster from CharProfileTable.jsfb and Sdb/ENG.sdb**, including custom wrestler names and IDs. **Scan models folder…** finds models already in your extracted mod collection and remembers the location. The helper is version **1.4.0**; the original application is Gorilla Studio v1.0.2.

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

A roster profile can exist without its model in the selected data. **Model not found · Scan models folder…** means that the name and ID were read, but no matching extracted model has been located. Point the scan at the parent folder containing your actual mod folders once, rather than browsing separately for PAC, AJ Styles and every other character. The supplied Discovery archive itself contains ten extracted character models; the other profiles need their models from your collection or the installed game.

Existing model associations remain authoritative. Library matching prefers exact names; duplicate matches require your choice. It does not use a custom wrestler's logical ID to guess a stock body. Verified installed-game models retain the original extractor. You can explicitly map any discovered model to another profile with **Use folder for another wrestler…**.

ENG.sdb supplies the readable character names. Keep your own current file in Sdb/ENG.sdb. Missing localized strings display as “Wrestler <ID>”; the loader never substitutes a localization key as a wrestler name.

Only **JSON and JSFB** are accepted by **Load roster file…**. SDB is read automatically as companion name data. JSON supports arrays such as [{"id":1506,"name":"Flammer"}], common roster wrappers, and name-to-ID or ID-to-name maps. Actual wrestler IDs must be 1–65535. The included rosters/Discovery_roster.json is a portable name/ID export; point the loader at your Discovery folder to discover its models.

Dropping a character folder also opens the picker, keeping that folder as the model source. The selected logical roster ID controls the target slot and render links; the physical source ID controls stock-body extraction. For example, the supplied mapping associates physical folder 312_CM_Punk with roster ID 1481.

Saved manual entries and model choices reopen with the same roster. Freshly scanned model mappings take precedence. Older saved folder-derived entries are refreshed to their actual mapped IDs. Settings and NTFS compatibility links are stored under %LOCALAPPDATA%/GorillaStudio/NameFolders. Original character folders keep their names and contents. Plain-name links require a local Windows drive supporting NTFS junctions.

## Validation

- The supplied data loads **1,457 roster profiles**, with **1,308 localized names**, and all **10 extracted model folders**. Video examples NICK JACKSON '14 (204), DAX HARWOOD (211), and CASH WHEELER (213) match the uploaded SDB.
- **156 portable checks pass**, including extracted PAC/AJ discovery, nested collections, ambiguous matches, moved model folders, physical-ID safety, and the original importer and slot-selection checks. Binding 1,457 profiles against 5,000 indexed model folders takes about 20 ms in the build environment.
- Portable importer/session, binary mapping, and extraction-output tests cover profile decoding, malformed data, ambiguous paths, saved settings, legacy entries and explicit slot selection. Both the .NET Framework 4.8 helper and patcher build without warnings or errors.
- A copied real character in a plain-name folder remains visible before assignment, then loads and reopens with an explicit added roster entry. Original MCD bytes stay unchanged.
- The original v1.0.2 engine verifies physical 312 → logical 1481 and physical 123 → logical 910. It retains the original model/body source while using the selected profile's render links and gender. The 1481 case selects 148100_CM_PUNK_GM.
- A real render attempt loads the added Flammer model and reaches original stock-body extraction, then stops because this environment lacks WWE 2K26 / oo2core_9_win64.dll. Complete rendering and Windows GUI/junction interaction remain unverified.
- The patcher verifies that only the window constructor and folder-loading state machine change; authentication, licensing and other methods/resources are preserved.

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
    dotnet tools/GorillaStudioPatcher/bin/Release/net8.0/GorillaStudioPatcher.dll /path/to/original/GorillaStudio.exe src/GorillaStudio.NameFolders/bin/Release/net48/GorillaStudio.NameFolders.dll /path/to/new/GorillaStudio.exe

Copy the original release's remaining files and the helper DLL into the new output folder. The patcher requires a new output path and verifies the original executable's supported layout.

The Python validators in tests use the original engine bytecode with CPython 3.14, NumPy 2.4.3 and Pillow. Run each with --help for arguments. validate_user_data.py performs an actual render attempt; validate_mapped_engine.py observes logical-slot handling and stops deliberately before game extraction. Neither script tests the Windows GUI.
