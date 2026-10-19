# Gorilla Studio v1.0.2 — Discovery Roster Experiment

Select a wrestler from the roster to load their matched model, default attire and linked roster picture automatically, then choose a taunt and use **Preview → MAKE → Install**. Picture lookup follows current Discovery references into extracted images or indexed game/mod CAK assets, and displays a decoded image without a Blender preview. The loader reads profiles, names and exact character asset mappings, including newly added IDs. Fast rendering is automatic. The helper is version **1.6.9**; the original application is Gorilla Studio v1.0.2.

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
         MoveData/CAMMoveData.jsfb
         Animation/<your animation assets>/

2. Set **Discovery data…** to your extracted discovery_data folder once, keeping its companion folders together. Selecting Characters or Roster also works. **Roster file…** opens an existing JSON/JSFB roster and remembers that file. **Select wrestler…** opens the current roster directly; it can also open saved or bundled names without asking for a model folder first.
3. If your models are stored outside Discovery, click **Set model library…** once and select the main folder containing your extracted wrestler mods. It scans nested folders, attaches unique matching models, and remembers this location for the roster. You can paste a path, drag a folder into the location box, or browse using Explorer's address bar and search. Keep model files extracted; the renderer uses .mcd models.
4. Search by name or ID, then **click the wrestler once**. The picker loads a verified mapped model, a saved choice, or a unique exact-name model, and closes. The renderer selects its default attire automatically. Multiple matching mod packs ask you to choose between those matches once and remember your choice. Missing files show a clear status; the normal roster path does not open a per-wrestler folder hunt. **Choose model folder…** remains available for an explicit override. Plain names need no numeric prefix. **Refresh data** rereads the roster and rescans remembered locations. Folder-to-wrestler manual assignment still requires an explicit choice.
5. Continue with the original Preview / Make / Install controls. Tick **Match card** and choose **Loop** for continuous moving match cards. On the first installation, use **Game setup**, then **bake once in Discovery** and restart WWE. Repeat setup and a bake after adding arena mods. **Check match-card setup…** checks the installed slot, screen setting, ending, plugin configuration and screen files; its **Run game setup** uses the remembered actual Discovery folder. Windows, .NET Framework 4.8, Blender 5.2 or newer, and the original WWE 2K26/plugin setup are required.

## Alternate attires

After loading a wrestler, use **Attire model** under Gear to choose an extracted alternate MCD. The list reads that character's attire folders, including nested folders, while excluding BaseModel. **Default (automatic)** keeps the normal default choice. The refresh button reads newly added attires. Choices are scanned in the background on wrestler changes or explicit refresh; selecting an already listed attire reuses the list. Stale scans cannot update another wrestler. Gear still selects entrance/ring pieces within the chosen model.

An attire change clears the previous preview/install result and queues preparation of the selected clothes before Preview or MAKE. The character's body, Discovery mapping and logical roster ID remain the same. A removed selected model produces an error rather than silently changing clothes. Attires that only exist in an unextracted game archive need to be extracted first.

## Make several wrestlers with one taunt

Choose the taunt and your hair, gear, ending and screen settings, then open **Batch MAKE…**. Search/select the wrestlers and click **MAKE selected** once. Jobs render one at a time with fast rendering always on. Missing or ambiguous models are reported; select their model once in the normal roster before retrying. Each wrestler uses its default attire, with the current wrestler's explicit attire retained if included.

Completed `.gmr` files are saved directly in your usual configured **Output folder**, which defaults to `Documents\Gorilla Studio\Renders`. Existing files are kept; another render with the same filename receives a unique name. **Open output folder** opens that same normal destination. Each job retains its working files and installation picture map separately under `%LOCALAPPDATA%/GorillaStudio/NameFolders/batches`; `batch-results.json` records the published file and its working source.

**Stop after current** finishes the active wrestler and keeps completed files. **Use selected result** restores a completed file for installation without rendering it again. **Install completed** installs the completed group with the game closed, keeps backups and runs game setup once. Bake in Discovery if setup reports changes, then restart WWE.

## Roster pictures and character loading

Custom roster pictures follow the selected character's actual picture references in CharProfileTable.jsfb during loading and in the completed GMR. The original renderer discarded references absent from its bundled stock picture list. PAC's current logical ID is **924**, picture `AF0F2CC3FC5CECF7`; physical model folder 1173 belongs to Chad Gable '16 and must not choose PAC's picture. AVERY STYLES, ID 122, has reference `1709AEF076130352`. Both custom references are absent from the bundled picture names. Loading a character now shows their actual link before rendering, instead of the native false “none found” message. Shared pictures retain their actual owners. New and changed profiles are reread from the current table.

Keep extracted **DDS or PNG** renders anywhere inside your Discovery data folder, including `UI/Assets/Roster/TypeA` or a `Renders` folder. An external selected model folder is also checked for matching images. Keep the original render filenames, or use an explicit sixteen-digit picture hash as the filename. Matching uses the profile's exact picture reference, rather than a wrestler's folder number. Conflicting images are left unresolved. TEX files are supported only when their contents are already DDS; packed game textures must be extracted as DDS first.

When a linked picture is not extracted, the loader checks CAK archives under the selected game and Discovery folders. It uses the bundled Bakery reader's extended indexes, validates the exact path hash, extracts only the requested TypeA texture and lets Bakery convert it to DDS. Indexes and textures are cached and refreshed when their sources change. Game and Discovery source files remain unchanged. Duplicate references across archives are reported as a load-order conflict; the loader does not choose a mod by file date. Actual Windows archive extraction still needs verification with the user's game files.

If Bakery's output option fails, indexing retries against an original-name link inside the private cache. When links are unavailable, temporary archive copies are capped at 512 MiB. Larger cross-drive archives need extracted TypeA DDS pictures instead; the loader reports this limitation and continues loading the wrestler.

Loading the wrestler displays a found picture in **Game picture (now)** immediately. A bounded managed DDS decoder handles BC7, DXT and common uncompressed pictures, including transparency, without launching Blender. Only the selected picture is decoded. The compatibility cache also supplies it to **Auto (from his render)** hair colour. Refresh and reselect after changing an image. Final GMR files and the installation map use the actual game references; temporary cache references are removed before installation. References, unavailable pixels and archive conflicts have separate status messages.

The uploaded Discovery copy contains **1,139 picture references**, including **653 absent from the bundled picture names**, but no original roster image files. Picture references alone can link the moving selection render; a static picture display requires the extracted image file. An empty **Game picture (now)** panel is therefore separate from the old false “no picture found” result.

Preview and MAKE now repair hair shaders that stayed white when strand textures used hash filenames or were missing. The repair uses the selected dark/light colours, including blond or white, and preserves available strand transparency and roughness. Auto retains meaningful natural hair colours. A fallback tints identified hair meshes without changing shared body materials, and also runs when a cached Blender model is loaded. Missing strand coverage cannot be reconstructed from colour alone. Dean Ambrose NXT (logical ID 699) is present in the supplied roster. His exact model was not located among the extracted assets inspected here, so his appearance still needs a preview check on the user's PC.

## App updates

Click **Update app…** beside Game setup when no preview, render or batch is running. The button checks for a newer release, downloads the small update package, verifies it, installs it and restarts Gorilla Studio. An up-to-date app stays open. The full ZIP installs this button and its updater once; later compatible releases can be installed through the button.

Updates preserve your settings, custom rosters, renders and configured output folder. The updater keeps backups of application files before changing them and restores them if installation fails. Download errors or cancellation leave the app open. Native sign-in and licensing cleanup still run during restart. Close other Gorilla Studio windows before updating, and keep the app in a folder your Windows account can write to.

## Moving match cards

**Game setup** now reads the current Discovery matchup catalog, including arena/menu names, IDs and exact screen references. The supplied `CreateShow/MatchUpScreenAsset.jsfb` has 283 records: 282 link to 181 distinct projects. These include AEW Dynamite, Collision, Double or Nothing and Forbidden Door, plus Survivor Series single and double ring. Setup rereads the tables each time, so new or changed records are picked up without an application update.

The lookup follows those exact references into extracted Discovery/game projects and indexed game/mod CAKs. Available archive projects are copied into Discovery at their indexed original paths; existing extracted edits take precedence. It does not invent an arena filename or silently substitute another card. Conflicting archive sources, missing projects and unsupported structures are reported. The uploaded dataset contains the matchup records but lacks the referenced screen projects; lookup uses the installed game/mod assets on your PC.

Open **Check match-card setup… → Matchup cards…** to search by arena/show name or ID and see the current reference status. Use **Run game setup**, **bake once in Discovery**, then restart WWE. The check itself only reads Discovery/game files and writes a private lookup cache. Run it again after adding or updating arena mods. Reading a reference or preparing a layout does not verify playback in the running game.

The game needs the Moving Renders plugin and the prepared screen layouts/placeholder clips. A read-only check of the uploaded Discovery folder finds **188 required files missing** (88 screen layouts and 100 placeholder movies). This is evidence about the uploaded copy, rather than the user's current game installation. Run **Game setup** and bake once in Discovery before testing in game. New arena mods can supply layouts that also need setup and another bake.

**Check match-card setup…** reads the selected GMR's logical target, screen and ending settings, compares its installed copy, checks the plugin's enabled setting, and runs the original read-only screen check. It reports setup failures and explicit bake requirements. **Enable moving renders** is an explicit action that changes only the existing plugin enabled setting and preserves a backup, comments and other settings. Restart WWE after enabling it. **Play once + flash freeze** intentionally holds its final frame; choose **Loop** and make/install again for continuous playback. Configuration checks do not verify the running game, CakeHook loading or an actual Discovery bake.

**Run game setup** also completes missing movie layers in each supported player slot. The original setup covers only ten of twenty match types and skips a whole file when it finds any existing `GM_clip`. The repair preserves valid existing movie layers, fills the omitted types, and adds their 52 missing placeholder movies using the plugin's layout/player encoding. It discovers layouts by their player groups throughout `UI/Projects`, including renamed and nested mod files; an exact catalog reference also permits a project outside the stock folders. Each group must match a supported layout before changes are made. It keeps byte-identical backups under `Gorilla Studio Layout Backups` with `.bak` extensions. Unsupported custom structures and setup failures are listed explicitly. Bake once in Discovery after any changes, then restart WWE.

When selection moves but **every arena's match card stays still**, omitted match types alone do not establish the cause. Selection finds renders through picture references; match cards use logical wrestler IDs and a separate game hook. Check the installed GMR's **Match card** setting and target ID first. If a setup, bake and restart still leave a standard one-on-one card static, inspect `<game>/Plugins/WWE2K26_MovingRenders.log` and `GorillaRenders/_scan_ids.txt` for the ID lookup and game-build compatibility. The included plugin has not been modified or verified against the user's running game.

## Discovery taunts and previews

Keep the **entire extracted Discovery data folder together**, including MoveData and Animation. The loader reads actual taunt names and IDs from `MoveData/CAMMoveData.jsfb` and joins them to animation records in `.clips` containers and loose `.clip` files. It searches the entire selected Discovery data folder, including nested mod folders and exports outside the top-level Animation folder. A newer loose edited clip takes precedence over a container record. It converts supported body motion into the original engine's animation format automatically; no Python installation or separate animation browsing is required.

The uploaded data contains **1,584 named taunt variants**. It adds **194 playable motions**, for **1,088 available taunts** with the original library retained. This includes **Undisputed 2**, animation `62541a000`: 281 frames at 60 fps, about 4.68 seconds. The original library had excluded it through a pose-screening heuristic. Variations such as LA Parka 1, 2 and 3 keep their distinct animation files and renderer keys.

**Every named Discovery taunt now stays in the search list.** Search accepts names, aliases and exact animation IDs, as well as `confidences`, `fight spirits` and `Undisputed2`. Numeric variants remain distinct: searching Jeff Hardy 2 cannot select Jeff Hardy 1 or 12. An animation that could not be located or imported appears as a disabled row with its reason and exact clip ID; it does not vanish from search or become a broken render entry. Lookup misses show the searched folder and distinguish incomplete scans, source-read failures, decoder limitations and preview-cache errors. The tooltip includes the first scan warning when files could not be read. Unmatched taunts are looked up automatically in CAK archives beneath your configured game and Discovery folders. The reader follows actual indexed .clip/.clips paths and verifies the canonical animation record inside them; it does not substitute a similarly named move. Extracted edits and already playable bundled motions retain priority. Different archived versions of one clip, unreadable sources and incomplete searches remain unavailable with an explanation. An unresolved lookup does not establish that the move is absent from the game. The list shows how many matching entries are unavailable.

Selecting a wrestler no longer imports the full taunt library twice. The loaded library is reused while the game and Discovery sources remain unchanged. Source watchers invalidate reuse after relevant file changes; **Refresh data** and explicit Discovery loads also retry. Unavailable monitoring, replaced source folders and linked external source trees retain conservative rescans. Roster parsing, model planning, picture conversion and compatibility-folder preparation run off the UI thread; the picker and final selection remain on it. Native render controls are guarded during selection. Windows interaction timing has not been measured here.

The first archive motion scan can take time. It runs off the UI thread with progress and **Cancel load**, keeps requested records and verified record catalogs in a private cache, and discards temporary extracted containers. Later refreshes reuse unchanged catalogs, including lookups that found no matching record. Adding or changing an archive invalidates that source's cache. Canceling before the final library update keeps the current list. Game and Discovery source files remain unchanged.

Archive indexing and extraction now allow the original native setup's limits (five and three minutes per operation) instead of prematurely rejecting a slow readable source after 45 or 90 seconds. These are maximum waits, not delays added to successful reads; cancellation remains available.

Use **Animation details…** to read or copy the full diagnostic report when a taunt is unavailable. The report includes the current game and Discovery folders, archive counts, all retained scan warnings and exact clip IDs. Search matches appear first; no tooltip or selection of a disabled taunt is needed. Incomplete archive searches now also mark the overall load status incomplete. Index errors identify the archive's full path.

Identical requested motion records at the same indexed path in multiple CAKs no longer block the entire animation search. Every copy is checked. Different bytes or inconsistent record presence leave that requested clip unresolved; a duplicate path alone does not invalidate unrelated verified clips. Unreadable sources still prevent acceptance when the active motion cannot be established. This change does not supply Jeff Hardy 2–8's animation data: their actual game/mod records must be readable, and their playback on Windows remains unverified here.

Taunts refresh after **Browse folder**, drag-and-drop and roster selection. A folder load waits for model preparation or active rendering before refreshing the latest selected Discovery folder. Adding a missing supported animation and loading that data again turns its unavailable result into a playable entry.

Select your wrestler, search for **Undisputed 2**, then select the taunt by mouse or keyboard. A short pause starts Preview automatically on the loaded wrestler and attire. Rapid browsing keeps the latest pending selection; an active preview finishes before the next starts. Programmatic list refreshes and startup do not render the whole library. **Preview** remains available to retry manually, and MAKE still starts only when requested.

Successful previews supply the taunt browser's animated image and are cached for later browsing. This covers imported motions, archive-recovered taunts and the 29 bundled motions that lack a shipped browser image. Existing 865 bundled browser images are preserved. An animation that cannot be located or decoded cannot produce a real preview. Changed motion bytes receive new engine and preview cache keys; an old browser image is cleared while a fresh preview is prepared.

A scan of the supplied extracted files did not locate matching body motion records for **455 variants** that also lack a bundled fallback. **45** more use older additional channel sections that this experimental decoder does not yet support. Existing bundled versions remain playable where present; **498** definitions have no playable fallback and stay visible as unavailable. For the requested families, Confidence 2, Fighting Spirit 1/3/4 and Jeff Hardy 1 use bundled motion. No matching motion was located for Confidence 1, Fighting Spirit 2 or Jeff Hardy 2–8 in the uploaded extracted animation indexes or the bundled decoded library. Their distinct names and variants remain searchable. Enigma 1–4 also lack matching animation records in the uploaded copy, so they require an actual matching game/mod source. The upload contains no game CAKs with which to demonstrate their recovery; Windows archive extraction and their exact animations remain unverified here. The importer never substitutes another taunt. Discovery setup reports available, missing and unsupported counts and rereads current files when loading the roster or a character folder.

Generated animation data, working captures and previews live in a writable folder under `%LOCALAPPDATA%/GorillaStudio/NameFolders`, alongside the preview cache. The original Discovery files and application assets remain intact. Unchanged refreshes reuse decoded motion and keep the selected taunt.

## Faster previews and renders

Preview and MAKE always use fast rendering. There is no speed selector. Saved settings from an older build cannot turn it off.

| Operation | Original defaults | Faster mode | Calculated rendering work |
| --- | --- | --- | --- |
| Wrestler Preview | 384 × 384, 16 samples | 256 × 256, 8 samples | 77.8% fewer pixel-samples |
| MAKE GORILLA RENDER | 1024 × 1024, 64 samples | 1024 × 1024, 8 samples | 87.5% fewer samples |

These are work calculations, **not measured time savings**. Model loading, Blender startup, encoding, GPU load and other fixed costs still take time. Preview retains its animation frames and playback rate. MAKE retains full output resolution and animation timing, with fewer lighting samples; shadows may look noisier. Close WWE while rendering to leave the GPU available.

The original engine's normal parallel MAKE path bypassed `GORILLA_EXTRA`, so older fast settings did not reliably reach full rendering. The new `engine/gr_render.py` entry point forwards those settings to the unchanged compiled renderer. Native command and renderer-setting checks confirm all full-render branches now receive the sample setting.

Unchanged previews reopen from an on-disk cache without starting Blender. Changes to character inputs, textures, roster context, taunt/settings, Blender/engine metadata or mode invalidate that cache. Cached images are separate copies, so another preview cannot overwrite them. Damaged or unreadable cache entries fall back to rendering. The cache retains at most 64 entries. Full MAKE always uses the original engine.

Full rendering after Preview stays off so browsing previews leaves the GPU available. MAKE starts when you request it or start a batch.

The small timer below Preview displays the actual request time, with initial model preparation separate. The supplied screenshot records an original MAKE at 229.5 seconds; a new elapsed time must be measured on the same PC and job. Windows/GPU timings have **not** been measured in this Linux build environment, so no exact seconds saved or speed multiplier is claimed.

Output remains **30 fps**. The current GMR writer and Moving Renders plugin explicitly reject 60 fps. Supporting 60 fps requires coordinated exporter, timing and plugin changes; changing only Blender's FPS setting would not produce a compatible game render.

After adding characters, save your updated CharProfileTable.jsfb and Sdb/ENG.sdb, put their extracted models inside a remembered models folder, then click **Refresh data**. It rereads the current roster and names, rescans the model folders, and includes new IDs. For an imported JSON roster, Refresh data reloads that same JSON file during the current session. Renamed characters replace stale saved labels; conflicting saved model choices require selection. Reopening Discovery also rereads its current files.

A roster profile can exist without its model in the selected data. **Model files missing** means that the name and ID were read, but no matching extracted model has been located. Point the model library at the parent folder containing your actual mod folders once. The supplied Discovery archive itself contains ten extracted character models; the other profiles need their models from your collection or a verified installed-game mapping.

Existing explicit model associations remain authoritative. Fresh matches prefer the exact CharacterMapping asset-path hash, then exact names and proven stock mappings. Duplicate matches require your choice. A physical folder number is not enough to override a conflicting localized profile: the uploaded physical 109 Hollywood Hogan folder no longer loads as Karl Anderson's logical slot 109. Unassigned physical models remain in the separate model list. Verified installed-game models retain the original extractor. You can explicitly map any discovered model to another profile with **Use folder for another wrestler…**.

ENG.sdb supplies the readable character names. Keep your own current file in Sdb/ENG.sdb. Missing localized strings display as “Wrestler <ID>”; the loader never substitutes a localization key as a wrestler name.

Only **JSON and JSFB** are accepted by **Load roster file…**. SDB is read automatically as companion name data. JSON supports arrays such as [{"id":1506,"name":"Flammer"}], common roster wrappers, and name-to-ID or ID-to-name maps. Actual wrestler IDs must be 1–65535. The included rosters/Discovery_roster.json is a portable name/ID export; point the loader at your Discovery folder to discover its models.

Dropping a character folder also opens the picker, keeping that folder as the model source. The selected logical roster ID controls the target slot and render links; the physical source ID controls stock-body extraction. For example, the supplied mapping associates physical folder 312_CM_Punk with roster ID 1481.

Saved manual entries and model choices reopen with the same roster. Freshly scanned model mappings take precedence. Older saved folder-derived entries are refreshed to their actual mapped IDs. Settings and NTFS compatibility links are stored under %LOCALAPPDATA%/GorillaStudio/NameFolders. Original character folders keep their names and contents. Plain-name links require a local Windows drive supporting NTFS junctions.

## Validation

- The supplied data loads **1,457 roster profiles**, with **1,308 localized names**, and finds all **10 extracted model folders**. Eight model associations are proven by exact mapping hashes, Brock Lesnar matches by name, and one unmatched physical Hollywood Hogan model remains unassigned. Video examples NICK JACKSON '14 (204), DAX HARWOOD (211), and CASH WHEELER (213) match the uploaded SDB.
- **3,546 portable checks pass** across 42 suites, including verified update downloads, backup/rollback and parent-process handoff, complete named-taunt search coverage, folder-load refresh queues, batch output publication, current Discovery matchup references, exact project lookup, new and changed arena records, read-only checks, alternate attire discovery/preparation queues, bulk planning and installation, archive reads, DDS/BC7 decoding, match-card setup, logical IDs, model loading, preview caching and render settings. This release ran nine affected/new archive-motion, picture-archive, matchup-archive, import, animation-report, refresh, update, attire and roster-preparation suites (2,029 checks); the 33 unchanged suites retain their preceding passing results. Detailed counts are included in TEST_RESULTS.json. Binding 1,457 profiles against 5,000 indexed model folders takes about 20 ms in the build environment; this is separate from rendering performance.
- Growth tests append valid new profiles and SDB strings to a separate copy of the actual data: **1,457 → 1,458 → 1,459** profiles. A copied real model binds to new logical ID 4000 with physical source 1506; edited JSON files reload new characters; high new ID 65527 preserves existing reserved IDs. Uploaded originals remain unchanged.
- Portable importer/session, binary mapping, and extraction-output tests cover profile decoding, malformed data, ambiguous paths, saved settings, legacy entries and explicit slot selection. Both the .NET Framework 4.8 helper and patcher build without warnings or errors.
- A copied real character in a plain-name folder remains visible before assignment, then loads and reopens with an explicit added roster entry. Original MCD bytes stay unchanged.
- The original v1.0.2 engine verifies physical 312 → logical 1481 and physical 123 → logical 910. It retains the original model/body source while using the selected profile's render links and gender. The 1481 case selects 148100_CM_PUNK_GM.
- A real render attempt loads the added Flammer model and reaches original stock-body extraction, then stops because this environment lacks WWE 2K26 / oo2core_9_win64.dll. Complete rendering and Windows GUI/junction interaction remain unverified.
- The original default-attire function is exercised on all ten uploaded model folders and direct-MCD compatibility layouts. The independent motion decoder matches a bundled animation's translations exactly and its quaternions within 0.000001. The unmodified original capture engine consumes the imported Undisputed 2 motion and produces 281 frames for a 175-bone skeleton, including 74 H helper bones. Full Blender image rendering remains unverified here.
- The original engine finds the prepared image for Avery's actual profile, samples its automatic hair colour, and accepts a finalized GMR with the actual picture hash and all 1,139 installation-map entries. These checks use a synthetic image and structural video fixture; they do not claim an in-game visual test.
- DDS decoding is compared with independent Pillow pixels for every BC7 mode, transparent alpha and DXT formats, and loads the uploaded real 2048 × 1024 Tripwire DDS. Archive tests exercise the shipped command vectors using fixture indexes and DDS outputs, plus real process cancellation and timeouts on Linux. They do not execute Bakery's Windows binary or prove PAC's missing texture pixels are present in the uploaded files. Match-card checks verify the actual 188-file screen pack and explicit setup result handling. Windows GUI, native game archive extraction, Blender images and match-card gameplay remain unverified.
- Match-card repairs are checked across all 86 supplied match-card projects: 4,472 missing layers are completed, existing layers are preserved, and repeat setup changes nothing. Independent original-engine checks compare all 101 slots in RAW 24 with the native full-layout recipe and inspect the repaired corpus. All 101 placeholder movies carry the plugin's layout/player encoding; a newly generated movie decodes with FFmpeg. These are file-format checks, not observed match-card gameplay.
- The unchanged native pipeline and Blender renderer settings execute in **171 structural checks**: full renders receive 8 samples at 1024 × 1024, 30 fps with the full motion. **239 hair-material checks** cover hash-named textures, missing-map fallback, selected and natural colours, material isolation, transparency and cached scenes, including switching a cached manual tint back to Auto. **116 native attire checks** confirm explicit clothes selection and separate model caches across preparation, Preview and MAKE. Bulk tests validate IDs, output isolation, malformed movies, shared picture maps, backups and repeat installs. Actual Windows batch rendering, GPU timings and alternate-attire images remain unverified.
- The patcher verifies a five-method allowlist: the window constructor, folder-loading state machine, Go state machine, process-launch state machine and automatic-render entry point. All other method bodies, authentication/licensing and managed/native resources are preserved. Engine binaries remain unchanged; faster detail is passed only to each applicable render process.

Detailed results are included in TEST_RESULTS.json inside the ZIPs.

## Build

Requires the .NET 8 SDK and the supplied original v1.0.2 release. Building on Linux is supported; running requires Windows.

    dotnet build src/GorillaStudio.NameFolders/GorillaStudio.NameFolders.csproj -c Release
    dotnet build tools/GorillaStudioPatcher/GorillaStudioPatcher.csproj -c Release
    dotnet build tools/GorillaStudioUpdater/GorillaStudioUpdater.csproj -c Release
    dotnet run --project tests/AppUpdateTests -c Release
    dotnet run --project tests/AppUpdateTransactionTests -c Release -- /path/to/dotnet
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
    dotnet run --project tests/RosterLaunchTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/RosterPreparationTests -c Release
    dotnet run --project tests/RosterModelResolutionTests -c Release
    dotnet run --project tests/MappedModelLibraryTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/AutomaticAttireTests -c Release
    dotnet run --project tests/DiscoveryTauntsTests -c Release -- /path/to/discovery_data/MoveData/CAMMoveData.jsfb
    dotnet run --project tests/DiscoveryMotionTests -c Release
    dotnet run --project tests/DiscoveryTauntLibraryTests -c Release -- /path/to/original/GorillaStudio /path/to/discovery_data /path/to/disposable-import-output
    dotnet run --project tests/DiscoveryTauntRefreshTests -c Release
    dotnet run --project tests/DiscoveryRenderLinksTests -c Release -- /path/to/discovery_data/Roster/CharProfileTable.jsfb
    dotnet run --project tests/DiscoveryRenderImagesTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/DiscoveryRenderOverlayTests -c Release -- /path/to/discovery_data/Roster/CharProfileTable.jsfb /path/to/new-image-fixture
    dotnet run --project tests/DiscoveryRenderResultTests -c Release -- /path/to/discovery_data/Roster/CharProfileTable.jsfb /path/to/render-fixture
    dotnet run --project tests/GorillaRenderMetadataTests -c Release
    dotnet run --project tests/RosterPictureDdsTests -c Release
    dotnet run --project tests/DiscoveryPictureSelectionTests -c Release -- /path/to/discovery_data/Roster/CharProfileTable.jsfb
    dotnet run --project tests/DiscoveryArchiveRendersTests -c Release
    python tests/validate_roster_picture_preview.py /path/to/new-dds-fixtures /path/to/real.dds
    dotnet run --project tests/RosterPicturePreviewTests -c Release -- /path/to/new-dds-fixtures
    dotnet run --project tests/MatchCardReadinessTests -c Release -- /path/to/original/GorillaStudio /path/to/discovery_data
    dotnet run --project tests/MatchCardLayoutsTests -c Release -- /path/to/original/GorillaStudio /path/to/new-layout-fixtures
    dotnet run --project tests/DiscoveryMatchupCatalogTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/DiscoveryArchiveMatchupsTests -c Release -- /path/to/original/GorillaStudio
    dotnet run --project tests/DiscoveryArchiveMotionsTests -c Release
    dotnet run --project tests/AutomaticTauntPreviewTests -c Release
    dotnet run --project tests/DiscoveryAnimationReportTests -c Release
    dotnet run --project tests/DiscoveryMatchupSetupTests -c Release -- /path/to/original/GorillaStudio /path/to/discovery_data
    dotnet run --project tests/AlternateAttireTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/BatchRenderPlanTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/BatchRenderInstallerTests -c Release
    dotnet run --project tests/BatchRenderOutputTests -c Release
    python tests/validate_matchcard_layouts.py --help
    python tests/validate_alternate_attires.py --help
    python tests/validate_render_speed.py --help
    python tests/validate_hair_materials.py --help
    dotnet tools/GorillaStudioPatcher/bin/Release/net8.0/GorillaStudioPatcher.dll /path/to/original/GorillaStudio.exe src/GorillaStudio.NameFolders/bin/Release/net48/GorillaStudio.NameFolders.dll /path/to/new/GorillaStudio.exe

Copy the original release's remaining files and the helper DLL into the new output folder. The patcher requires a new output path and verifies the original executable's supported layout.

Also ship `Pfim.dll` from the helper build output and the MIT license/notice under `third_party/Pfim`. Copy `runtime/engine/gr_render.py` to the program's `engine/gr_render.py`; the fast full-render forwarding fix requires this entry point alongside the original compiled renderer. The full and small-update ZIPs include these files.

Ship `tools/GorillaStudioUpdater/bin/Release/net48/GorillaStudio.Updater.exe` beside the application. The small update contains exactly eleven approved files, including this updater and the renderer entry point. `BUILD_SHA256.json` must include hashes for the app, helper, updater, Pfim, bundled roster and renderer entry point. Publish the artifact commit first, then update `downloads/update.json` with schema 1, the helper version, minimumUpdaterVersion 1, the immutable artifact commit's raw GitHub Update.zip URL, its byte size and SHA-256. This avoids a mutable download changing after a user reads the feed. Run `tests/AppUpdateTests -- --validate <Update.zip> <update.json>` locally and `-- --live <private-cache>` after publication; neither mode executes the downloaded app.

The Python validators in tests use the original engine bytecode with CPython 3.14, NumPy 2.4.3 and Pillow. Run each with --help for arguments. validate_user_data.py performs an actual render attempt; validate_mapped_engine.py observes logical-slot handling and stops deliberately before game extraction. Neither script tests the Windows GUI.
