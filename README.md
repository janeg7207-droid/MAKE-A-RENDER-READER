# Gorilla Studio v1.0.2 — Discovery Roster Experiment

Adds **Load Discovery roster…** beneath the supplied v1.0.2 app's Browse/Find controls. Choose an extracted Discovery folder, search your superstar roster, and load the selected character's actual model folder. The new button uses the original style. Native name search, stock-body extraction, sign-in, licence checks and rendering remain in the updated application.

## Download

- [Full experimental program ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental.zip)
- [Small update ZIP for an existing v1.0.2 copy](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Update.zip)
- [Source ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Source.zip)

The full program ZIP uses Git LFS. Download it through the link above; cloning the repository requires Git LFS to retrieve that file. SHA-256 checksums are in the downloads folder.

## Use

1. Extract the full experimental build into a separate folder. Keep GorillaStudio.NameFolders.dll beside GorillaStudio.exe.
2. Click **Load Discovery roster…** and choose your extracted discovery_data folder, containing Characters and Roster/CharProfileTable.jsfb. Selecting Characters or Roster directly also works.
3. Select a superstar and click **Load selected wrestler**. The original app remembers the selected Discovery root, so its **Find by name…** feature can locate those mod folders too.
4. Continue with Preview / Make / Install. The original v1.0.2 requirements still apply: Windows, Blender 5.2 or newer, and WWE 2K26 with its required plugin setup.

Dropping the Discovery root or an individual character folder also works. Direct character drops discover the sibling roster while keeping the dragged character as the source. Plain folder names require no numeric prefix; select the correct wrestler each time.

Only **JSON and JSFB** files are accepted by the picker's **Load roster file…** control. The included rosters/Discovery_roster.json contains the 10 named characters from the uploaded data. JSON supports arrays such as [{"id":1506,"name":"Flammer"}], common roster wrappers, and name-to-ID or ID-to-name maps. Actual wrestler IDs must be 1–65535; render-picture numbers and numeric localization keys cannot supply wrestler names.

JSFB import validates CharProfileTable.jsfb and indexes known names/IDs from its companion Characters/ID_Name folders. This experiment lists the available character folders; it does not decode every localized name from the entire binary profile table. The native finder retains its original stock-game search.

Saved manual or JSON-added entries remain when reopening the same Discovery roster. Entries from another explicit roster table stay separate. In collection mode, a manually selected character folder is remembered. Roster data and compatibility links are stored under %LOCALAPPDATA%/GorillaStudio/NameFolders.

Plain-name links require a local Windows drive supporting NTFS junctions. Original character folders are not renamed or copied. Apply the small update ZIP only to a **separate copy of v1.0.2**, keeping the original data/engine/plugin folders; it is not an update for v1.0.

## Validation

- The .NET Framework 4.8 helper builds with zero warnings/errors.
- **46** importer, name, path and session checks pass, including the actual JSFB, all 10 supplied characters, and reopening a roster with an added eleventh character.
- The **original v1.0.2 engine** loads all 10 IDs and attire models. Eight have roster-picture links; Hogan 2002 (109) and Rey Mysterio 2019 (123) have none in the supplied table.
- A separate copy named Flammer Experimental, selected as ID 1506, loads with the correct female flag, attire and render hash E4A5CD759AD62903. Original input hashes remain unchanged.
- A real render attempt reached v1.0.2's stock-body extraction, then stopped because the build environment has no installed WWE 2K26 files / oo2core_9_win64.dll. The updated extraction feature is retained; complete rendering was not validated here.
- The patcher verifies that only the window constructor and folder-loading method change. Other methods, authentication/licensing, WPF resources and native resources are preserved.
- Windows GUI interaction and junction creation still need a Windows smoke test. Engine data checks ran under Python 3.14 on Linux using equivalent directory links.

## Build

Requires the .NET 8 SDK and the supplied original v1.0.2 release. Building on Linux is supported; running requires Windows .NET Framework 4.8.

    dotnet build src/GorillaStudio.NameFolders/GorillaStudio.NameFolders.csproj -c Release
    dotnet build tools/GorillaStudioPatcher/GorillaStudioPatcher.csproj -c Release
    dotnet run --project tests/RosterTests -c Release -- /path/to/discovery_data
    dotnet tools/GorillaStudioPatcher/bin/Release/net8.0/GorillaStudioPatcher.dll /path/to/original/GorillaStudio.exe src/GorillaStudio.NameFolders/bin/Release/net48/GorillaStudio.NameFolders.dll /path/to/new/GorillaStudio.exe

Copy the original release's remaining files and the helper DLL into the new output folder. The patcher refuses to overwrite an existing output or remove a strong-name signature.

Run tests/validate_user_data.py --help for original-engine validation arguments. It requires Python 3.14, NumPy 2.4.3 and Pillow. It creates a separate test character, checks original input hashes, and records the actual render outcome. Missing-game/body blockers are reported as blocked, never as completed renders.
