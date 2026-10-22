# Gorilla Studio v1.0.2 — Discovery Roster Experiment

Select a wrestler from the roster to load their matched model, default attire and linked roster picture automatically, then choose a taunt on Randy and use **MAKE → Install** for your selected wrestler. Picture lookup follows current Discovery references into extracted images or indexed game/mod CAK assets, and displays a decoded image without a Blender preview. The loader reads profiles, names and exact character asset mappings, including newly added IDs. Fast rendering is automatic. The helper is version **1.7.17**; the original application is Gorilla Studio v1.0.2.

## Download

- [Full program ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental.zip)
- [Small update ZIP for an existing v1.0.2 copy](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Update.zip)
- [Optional SelectionLoop companion ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_SelectionLoop_Experimental.zip)
- [Source ZIP](https://github.com/janeg7207-droid/MAKE-A-RENDER-READER/raw/refs/heads/main/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Source.zip)

Extract the small update into the folder containing GorillaStudio.exe in a **separate copy of v1.0.2**. Replace the executable and helper DLL together, keeping the original engine, data and plugin folders. It also updates the previous experimental v1.0.2 build. It does not apply to v1.0.

The full program ZIP uses Git LFS. Use the download link above, or install Git LFS before cloning. SHA-256 checksums are in downloads.

## Use

1. Keep your extracted Discovery folders together:

       discovery_data/
         Roster/CharProfileTable.jsfb
         Characters/CharacterMapping.jsfb
         Characters/CostumesTable.jsfb
         Characters/<your character folders>/
         Sdb/ENG.sdb
         MoveData/CAMMoveData.jsfb
         Animation/<your animation assets>/

2. Set **Discovery data…** to your extracted discovery_data folder once, keeping its companion folders together. The roster opens after profile and name metadata loads; model indexing and animation/archive work continue separately in the background. The interface stays available while that work runs. Extracted, bundled and verified animations become available before the remaining game/mod CAK lookup. Preview and MAKE wait for that first validated matching library, so another roster's motions cannot be used by accident; they can then run while additional archive recovery continues. Progress and **Cancel load** remain available; completion details stay in **Animation details…** without a blocking popup. Selecting Characters or Roster also works. **Roster file…** opens an existing JSON/JSFB roster and remembers that file. **Select wrestler…** opens the current roster directly; it can also open saved or bundled names without asking for a model folder first.
3. If your models are stored outside Discovery, click **Set model library…** once and select the main folder containing your extracted wrestler mods. It scans nested folders, attaches unique matching models, and remembers this location for the roster. You can paste a path, drag a folder into the location box, or browse using Explorer's address bar and search. Keep model files extracted; the renderer uses .mcd models.
4. The roster opens in **A–Z order**, ignoring opening quotation marks. Click **Wrestler name** or **Wrestler ID** to sort by that column; click again to reverse it. Search by name or ID, then **click the wrestler once**. The picker loads a verified mapped model, a saved choice, or a unique exact-name model, and closes. The renderer selects its default attire automatically. Multiple matching mod packs offer checkboxes: choose one or more complete packs and remember those choices. Their outfits appear under Attire model and can share one batch taunt. Missing files show a clear status; the normal roster path does not open a per-wrestler folder hunt. **Choose model folder…** remains available for an explicit override. Plain names need no numeric prefix. **Refresh data** rereads the roster and rescans remembered locations. Folder-to-wrestler manual assignment still requires an explicit choice.
5. Continue with the original Preview / Make / Install controls. Tick **Match card** and choose **Loop** for continuous moving match cards. On the first installation, use **Game setup**, then **bake once in Discovery** and restart WWE. Repeat setup and a bake after adding arena mods. **Check match-card setup…** checks the installed slot, screen setting, ending, plugin configuration and screen files; its **Run game setup** uses the remembered actual Discovery folder. Windows, .NET Framework 4.8, Blender 5.2 or newer, and the original WWE 2K26/plugin setup are required.

## Alternate attires

After loading a wrestler, use **Attire model** under Gear to choose an extracted attire. Discovery choices follow the selected profile's active costume keys through **CharProfileTable.jsfb → CostumesTable.jsfb → CharacterMapping.jsfb**, then match each exact MCD path hash across your indexed model folders. Labels come from ENG.sdb. Version **1.7.10** also keeps extra MCD outfits inside the loaded character folder selectable as **(local)** choices, even when Discovery links only one of that character's attires. Mapped labels and the mapped default are retained. A local choice keeps the current wrestler ID, body/model source and roster-picture links.

The uploaded CM Punk data has two profiles: ID **312** lists eleven active costumes, while custom ID **1481** links one MCD in the physical `312_CM_Punk` folder. Studio previously hid that folder's additional outfits after finding the one mapping. The list now combines mapped outfits with actual local files. Jeff Hardy **’02–’03, ID 695**, has eleven active entries; unavailable or zero-hash model mappings remain reported instead of being guessed. For **“DEMON” FINN BÁLOR, ID 626**, original and **CARNAGE** remain his active cross-folder choices. Another persona's source is not inferred from a similar name or shared costume key.

Version **1.7.12** keeps every active saved Discovery costume visible, including CM Punk 312's eleven references. An unresolved model appears with its exact lookup reason and stays unavailable for rendering; its name no longer disappears. The attire refresh button rescans remembered model locations, including newly added separate packs, and matches their virtual MCD paths to the saved costume hashes. Selecting a child attire folder expands only to a proven character owner so valid sibling outfits remain visible. Batch attire choices follow the same availability rules and reject unresolved models.

Version **1.7.15** fixes a refresh error that could lose an explicitly chosen or previously remembered model folder beneath a collection wrapper skipped by the broad scan, such as `_SeparatePack`, `.SeparatePack` or `cache`. A broad root no longer suppresses that specific source merely because it is a parent directory. Refresh still deduplicates discovered character roots and requires an exact active outfit mapping before enabling a mapped attire. The new upload resolves both logical 420 costume references to the same supplied MCD; it does not supply the distinct 1320 outfit or the missing CM Punk 312/Jeff Hardy ’02 files.

An attire can live in another mod folder. Selecting it loads that complete model source, including its body and textures, while preserving the selected logical wrestler slot and roster picture links. The full attire list stays available after the switch. A change clears the previous result and prepares the exact selected MCD for MAKE. Taunt previews always use Randy, independently of that attire. Distinct active costumes remain separate choices even when they share one MCD. Gear still selects entrance/ring pieces within the chosen model.

The list reports active and available counts, with explanations for missing mapped models. **↻** rereads mappings and attire files inside already indexed character source folders. After adding a new character/model folder, use the roster's **Refresh data** to rescan remembered locations; use **Set model library…** first for a new collection location. Reads run in the background, and selecting an already listed attire reuses the catalog. A removed selected model produces an error instead of silently changing clothes. Characters without resolved Discovery attire mappings retain their local-folder choices and **Default (automatic)**. Models that exist only in an archive must be extracted first.

Version **1.7.13** lets you check both sources in **Choose model**, including separate packs such as **Will ospreay all in 26** and **WillOspreay DEATHRIDERS**. The first pack loads initially; **Attire model** lists the selected packs by name. Each outfit uses its own complete model, body and textures. Choices are tied to the exact wrestler and current roster; another persona does not inherit them. Missing or changed selected sources are reported.

The supplied **468_Jeff_Hardy_7_Blue_JustBringIt** pack was checked through the current readers. It contains one actual attire MCD, which is selectable as a **(selected pack)** outfit under logical Jeff Hardy ’02–’03 **695**. Its renamed virtual path differs from the saved **BLUE JUST BRING IT** mapping, so that old mapped row remains unavailable. To add separate renamed packs, choose the roster wrestler, add their collection with **Set model library…**, then use **Choose model → Show all**, check the intended packs and **Load checked models**. The choices survive reopening and appear in batch attire selection. A physical folder number or similar outfit name alone cannot establish a Discovery mapping.

## Make several wrestlers with one taunt

Choose the taunt and your hair, gear, ending and screen settings, then open **Batch MAKE…**. Search/select the wrestlers and click **MAKE selected** once. Jobs render one at a time with fast rendering always on. Missing or ambiguous models are reported; select their model once in the normal roster before retrying. Each wrestler uses its default attire, with the current wrestler's explicit attire retained if included. To make two or more outfits with one taunt, use **Choose attires…** beside that wrestler, tick the outfits, then **MAKE selected**. Each checked attire becomes a separate job using the same taunt and settings. Its normal output filename includes an attire label, and each job keeps its own workspace. Your current wrestler is preselected when available. If you checked several model packs, batch defaults to one outfit from each selected pack. **Choose attires…** can replace those defaults with the exact outfits you check.

**Choose attires…** includes mapped Discovery outfits and local extra outfits from their complete character folders. Both Demon Finn's default and CARNAGE can be checked together, and multiple local CM Punk or Jeff Hardy outfits can share one taunt. Every job rechecks its source and MCD path before loading. Cross-folder local choices must retain a current exact Discovery association for that character root. The selected logical wrestler ID stays fixed, and each checked attire receives its own output file.

Completed `.gmr` files are saved directly in your usual configured **Output folder**, which defaults to `Documents\Gorilla Studio\Renders`. Existing files are kept; another render with the same filename receives a unique name. **Open output folder** opens that same normal destination. Each job retains its working files and installation picture map separately under `%LOCALAPPDATA%/GorillaStudio/NameFolders/batches`; `batch-results.json` records the published file and its working source.

**Cancel batch** or closing the batch window stops its active render and prevents the remaining jobs from starting. The window waits for its render processes to exit, then restores the main controls so you can choose another wrestler. Earlier completed files are kept; the interrupted job is not published or installed. **Use selected result** restores a completed file for installation without rendering it again. **Install completed** installs a group containing one result per wrestler with the game closed, keeps backups and runs game setup once. When multiple attires were rendered for the same wrestler, select the version you want and use **Use selected result → Install into the game**. The current plugin installs one moving render per wrestler slot; two saved outfit files are not two simultaneous in-game slots. Bake in Discovery if setup reports changes, then restart WWE.

## Install previously completed renders

Version **1.7.17** adds **Completed renders…** beneath **Install into the game**. It can install movies saved in an earlier session or a closed batch without loading a model, choosing a taunt or rendering again.

1. Close WWE, open **Completed renders…**, and let the background scan finish. It reads your current configured Output folder and saved batch workspaces, including valid finished movies left by canceled batches. **Add files…** and **Add folder…** can include results saved elsewhere.
2. Search by wrestler, ID, taunt or filename. Check the movies you want and use **Install selected**. Choose one outfit per wrestler ID; several different wrestlers can be installed together.
3. The existing installer verifies the selected completed files, keeps replacement backups and installs their actual logical IDs. It does not automatically run screen/layout setup. **Check match-card setup…** diagnoses the highlighted or checked saved render without loading its character; layout changes and Discovery baking remain separate actions.

Saved batch manifests recover the original per-job workspace and its picture map when the recorded result is still valid; a verified published copy can be used when the workspace is gone. Movies remain in their original output locations. Valid matching SelectionLoop sidecars follow installed movie bytes. Conflicting historical picture maps are reported, so choose results from a matching roster context instead of combining incompatible maps.

Installation status compares actual movie bytes within a 128 MiB read budget. **Installed** means an identical copy was found, not that the running game accepted it. Unfinished comparisons remain explicit rather than being guessed from filenames, dates or sizes. Partial, malformed or mismatched-ID results cannot be installed. Scanning is cancellable; closing during installation waits for the current copy to finish and then restores the main controls. The original sign-in/unlock check and game-closed requirement remain in force. An explicitly disabled MovingRenders configuration stays disabled, including when an outdated plugin is replaced; installing saved movies does not silently enable it.

## Optional selection loop with match-card flash freeze

The separate **SelectionLoop experimental companion** targets the exact bundled **MovingRenders 4.21** plugin. Extract its ZIP and run **GorillaSelectionLoopInstaller.exe** with WWE closed. Select `WWE2K26_x64.exe`, then **Install**. The installer verifies compatibility and keeps backups; **Uninstall** removes or restores only its unchanged owned files. This companion is optional and is not installed by the regular app updater.

In Studio 1.7.13, tick both **Roster selection** and **Match card**, choose **Play once + flash freeze**, then **MAKE** and **Install into the game** again. New native freeze results get a verified `.gmr.selection-loop.json` beside their movie; batch output and installation copy that policy with matching movie bytes. Restart WWE after installing. Selection loops the clean motion before the flash, while the match card retains its original flash-freeze ending. A freeze render contains a shortened taunt: the companion cannot recover the missing remainder or guarantee a seamless loop. Old movies need a fresh MAKE; duplicate-ID aliases retain native behavior.

The companion checks the original plugin, active source, movie hash and exact frame range before applying a policy. Unsupported versions or ambiguous mappings leave native behavior in place and write `Plugins/WWE2K26_SelectionLoop.log`. Changes to the render folder during gameplay disable overrides until restart. Existing match-card setup and Discovery bake requirements still apply. Portable policy and actual assembly checks pass; Windows/CakeHook loading and in-game playback remain untested.

## Roster pictures and character loading

Custom roster pictures follow the selected character's actual picture references in CharProfileTable.jsfb during loading and in the completed GMR. The original renderer discarded references absent from its bundled stock picture list. PAC's current logical ID is **924**, picture `AF0F2CC3FC5CECF7`; physical model folder 1173 belongs to Chad Gable '16 and must not choose PAC's picture. AVERY STYLES, ID 122, has reference `1709AEF076130352`. Both custom references are absent from the bundled picture names. Loading a character now shows their actual link before rendering, instead of the native false “none found” message. Shared pictures retain their actual owners. New and changed profiles are reread from the current table.

Keep extracted **DDS or PNG** renders anywhere inside your Discovery data folder, including `UI/Assets/Roster/TypeA` or a `Renders` folder. An external selected model folder is also checked for matching images. Keep the original render filenames, or use an explicit sixteen-digit picture hash as the filename. Matching uses the profile's exact picture reference, rather than a wrestler's folder number. Conflicting images are left unresolved. TEX files are supported only when their contents are already DDS; packed game textures must be extracted as DDS first.

When a linked picture is not extracted, the loader checks CAK archives under the selected game and Discovery folders. It uses the bundled Bakery reader's extended indexes, validates the exact path hash, extracts only the requested TypeA texture and lets Bakery convert it to DDS. Indexes and textures are cached and refreshed when their sources change. Game and Discovery source files remain unchanged. Duplicate references across archives are reported as a load-order conflict; the loader does not choose a mod by file date. Actual Windows archive extraction still needs verification with the user's game files.

If Bakery's output option fails, indexing retries against an original-name link inside the private cache. When links are unavailable, temporary archive copies are capped at 512 MiB. Larger cross-drive archives need extracted TypeA DDS pictures instead; the loader reports this limitation and continues loading the wrestler.

Loading the wrestler displays a found picture in **Game picture (now)** immediately. A bounded managed DDS decoder handles BC7, DXT and common uncompressed pictures, including transparency, without launching Blender. Only the selected picture is decoded. The compatibility cache also supplies it to **Auto (from his render)** hair colour. Refresh and reselect after changing an image. Final GMR files and the installation map use the actual game references; temporary cache references are removed before installation. References, unavailable pixels and archive conflicts have separate status messages.

The uploaded Discovery copy contains **1,139 picture references**, including **653 absent from the bundled picture names**, but no original roster image files. Picture references alone can link the moving selection render; a static picture display requires the extracted image file. An empty **Game picture (now)** panel is therefore separate from the old false “no picture found” result.

Preview and MAKE now repair hair shaders that stayed white when strand textures used hash filenames or were missing. The repair uses the selected dark/light colours, including blond or white, and preserves available strand transparency and roughness. Auto retains meaningful natural hair colours. A fallback tints identified hair meshes without changing shared body materials, and also runs when a cached Blender model is loaded. Missing strand coverage cannot be reconstructed from colour alone. Dean Ambrose NXT (logical ID 699) is present in the supplied roster. His exact model was not located among the extracted assets inspected here, so his appearance still needs a preview check on the user's PC.

For Andrade, the native Auto sampler can mistake an opaque white picture background for hair; this failure was reproduced with controlled images. **Hair colour → Black → MAKE again** bypasses that sampler and is the next diagnostic. Andrade EL IDOLO (968) and his three attire mappings exist in the supplied metadata, but the actual model and sampled picture are unavailable here. This update does not claim a verified Andrade-specific fix or replace uncertain automatic colours with a guessed palette.

Version **1.7.14** checks that a hair image actually decodes before Auto preserves or restores it. A cached image entry with zero pixels no longer takes priority over meaningful primary hair vertex colours or the existing selected-colour fallback. Reading dimensions lets Blender load valid external and packed images; an initially false `has_data` flag alone does not reject them. Usable textures retain priority, and existing coverage, roughness, normals and shared body materials stay unchanged. This fixes a reproduced cached-image failure, rather than proving every white-hair cause. The image check cannot recover genuinely missing texture files. Previously completed renders need a new MAKE and installation.

Version **1.7.15** restores meaningful authored hair and facial-hair colours in **Auto** for supported source materials, including a native colour pass that had replaced those colours with a white sampled palette. It uses the original MCD mesh identity and the current material’s referenced face colours, so a hair mesh imported under its accessory’s name can still be recognized. Separate accessory materials, valid colour textures, RAI coverage/roughness, body materials and explicit manual colour choices stay intact. Cached manual/native tints can return to Auto. Use **Hair colour → Auto**, then **MAKE** and install again; completed movies do not change automatically.

The supplied **Xavier Woods 2019, physical model 420**, includes dark primary hair and beard colours plus valid named RAI textures. Testing uses those complete source assets and the original native texture-conversion/material routines. A white incoming Auto palette reproduces the visible failure; authored-colour restoration is tested separately from exact Windows/game playback. The pack is not the separate **Xavier Woods ’17, logical ID 1320**, outfit referenced by Discovery. The supplied Kofi pack also lacks its referenced combination flow/RAI files; another wrestler’s textures are not substituted.

The shared renderer also checks **In-ring gear / Entrance gear** against the exact flags on each imported attire mesh. Source and mesh identities come from the original importer, so duplicate material names or changes in object order cannot select the wrong layer. Inactive pieces are hidden without changing vertices, weights, shape keys or source files. Existing manual hides remain in force. Older private model caches are rebuilt once with a retained backup; completed preview caches recognize the new renderer. The first MAKE after this update may therefore prepare the selected model again. Interrupted rebuilds recover their previous cache on the next attempt, and native mesh metadata is published atomically.

Version **1.7.9** also reads the selected attire's HPL body-hide data. It verifies the complete base/outfit pair, original mesh identities, REMAP IDs and LOD0 faces before hiding covered skin in the rendered result. Only mask IDs common to both source modes are used, and every corner of a hidden triangle must be covered. This conservative rule avoids guessing how an unfamiliar outfit assigns its modes. Original model files, vertices, weights and shape keys are retained. Cached scenes receive the same check once when loaded, without scanning source files every frame.

Bobby Lashley's supplied complete model reproduces skin poking through his pants with the actual Jeff Hardy 2 motion; its skeleton and weights load correctly. The common mask removes 11,091 evaluated body faces and eliminates the prominent pants patches across six sampled poses in ring and entrance gear. The entrance shirt still clips at the shoulders/arms because its extra mask is not common to both modes. A diagnostic using the additional mask removes those patches on Bobby, but its numeric mode-to-gear rule is not established for other models and is not enabled globally. Other clothing can still intersect skin when it requires a mode-specific mask, has poor fit or has incorrect animation weights. YCL collision volumes alone do not provide a cloth simulation. Bobby's supplied YCL contains 26 collision shapes and no hair/cloth simulation objects or render bindings. The current renderer does not run a YCL physics solver. Adam Cole's exact full body/outfit pair remains untested.

The uploaded **DISCOVERY Yukes Cloth Tools 0.19.2** add-on is useful reference material for YCL hair/cloth bindings, colliders and behavior settings. Its Windows Blender 5.2 extension and embedded templates do not demonstrate a playback solver. This release does not add YCL simulation; hair and clothing movement still need a validated simulation/bake stage.

The complete Jeff Hardy pack does contain genuine YCL hair simulation data. An isolated experiment binds its source triangles to 143 named hair bones and produces secondary motion, but both solver trials stretch and spike the strands. All 13 matched comparisons preserve non-hair geometry exactly, and all 43 uploaded files remain unchanged. These preservation checks do not make the hair motion usable. The experiment is not enabled in Studio; native binding, extended constraints, force timing and collisions remain unresolved.

A fresh native preparation and Jeff Hardy 2 capture on that pack preserve its green/hazel irises and pupils in the inspected head render. Solid white eyes were not reproduced in this neutral-face baseline. This is not an eye fix or proof about a different Jeff outfit, taunt or existing cached render.

Finger movement depends on the selected motion’s actual bone tracks. The supplied Jeff Hardy 1–6 motions retain all 38 non-stub finger joints; actual native Blender pose evaluation on Bobby confirms Jeff Hardy 2’s changing finger rotations. The supplied Undisputed motions, including Undisputed 2 for Adam Cole, contain only 26 body tracks and no finger tracks. The native fallback repeats a relaxed-hand pose. No matching companion hand-animation reference has been proven from these uploads, and this release does not fabricate or claim to restore Adam Cole’s in-game gesture.

## App updates

Click **Update app…** beside Game setup when no preview, render or batch is running. The button checks for a newer release, downloads the small update package, verifies it, installs it and restarts Gorilla Studio. An up-to-date app stays open. The full ZIP installs this button and its updater once; later compatible releases can be installed through the button.

Updates preserve your settings, custom rosters, renders and configured output folder. The updater keeps backups of application files before changing them and restores them if installation fails. Download errors or cancellation leave the app open. Native sign-in and licensing cleanup still run during restart. Close other Gorilla Studio windows before updating, and keep the app in a folder your Windows account can write to.

## Moving match cards

**Game setup** now reads the current Discovery matchup catalog, including arena/menu names, IDs and exact screen references. The supplied `CreateShow/MatchUpScreenAsset.jsfb` has 283 records: 282 link to 181 distinct projects. These include AEW Dynamite, Collision, Double or Nothing and Forbidden Door, plus Survivor Series single and double ring. Setup rereads the tables each time, so new or changed records are picked up without an application update.

The lookup follows those exact references into extracted Discovery/game projects and indexed game/mod CAKs. Available archive projects are copied into Discovery at their indexed original paths; existing extracted edits take precedence. It does not invent an arena filename or silently substitute another card. Conflicting archive sources, missing projects and unsupported structures are reported. The uploaded dataset contains the matchup records but lacks the referenced screen projects; lookup uses the installed game/mod assets on your PC. The bundled pack supplies 85 of the 181 referenced project hashes, covering 177 of the 282 linked records. The other 96 project references need their actual game or mod files. This count describes the supplied pack and metadata; it does not measure your installed game. Wrapped extracted packs such as `Mods/AEW/UI/Projects/...` are matched by their exact virtual `UI/...` path hash. Setup copies a proven wrapped source to its canonical Discovery path and leaves the export unchanged. An existing canonical Discovery edit remains authoritative; conflicting exports remain unresolved.

Open **Check match-card setup… → Matchup cards…** to search by arena/show name or ID and see the current reference status. Use **Run game setup**, **bake once in Discovery**, then restart WWE. The check itself only reads Discovery/game files and writes a private lookup cache. Run it again after adding or updating arena mods. Reading a reference or preparing a layout does not verify playback in the running game.

The game needs the Moving Renders plugin and the prepared screen layouts/placeholder clips. A read-only check of the uploaded Discovery folder finds **188 required files missing** (88 screen layouts and 100 placeholder movies). This is evidence about the uploaded copy, rather than the user's current game installation. Run **Game setup** and bake once in Discovery before testing in game. New arena mods can supply layouts that also need setup and another bake.

**Check match-card setup…** reads the selected GMR's logical target, screen and ending settings, compares its installed copy, checks the plugin's enabled setting, and runs the original read-only screen check. It reports setup failures and explicit bake requirements. **Enable moving renders** is an explicit action that changes only the existing plugin enabled setting and preserves a backup, comments and other settings. Restart WWE after enabling it. **Play once + flash freeze** intentionally holds its final frame; choose **Loop** and make/install again for continuous playback. Configuration checks do not verify the running game, CakeHook loading or an actual Discovery bake.

Version **1.7.16** also audits the installed **Plugins/GorillaRenders** library in the background. For the exact bundled MovingRenders 4.21 binary, the report explains nested files outside its top-level scan, the **256 accepted-file limit**, duplicate primary/alias wrestler IDs, alias capacity and incompatible GMR/video headers. Files for two attires of one logical wrestler compete for that slot even if their screen settings differ. Keep the version you want installed; retain other attire movies in the normal output folder. The audit never moves, deletes or edits GMRs. It cannot identify the native overflow order or decode every movie. Other plugin versions receive clearly scoped reference notes. Closing the check cancels its managed inspection and stops follow-on checks.

**Run game setup** also checks each supported player slot, completes missing movie layers, and repairs existing layers whose exact plugin marker or movie size/position/scale is wrong. A movie layer can exist while the still-picture layer remains visible; checking the marker is necessary as well. Unknown movie assets and incompatible custom structures are reported and left unchanged. The original setup covers only ten of twenty match types and skips a whole file when it finds any existing `GM_clip`. The repair preserves valid existing movie layers, fills the omitted types, and adds their 52 missing placeholder movies using the plugin's layout/player encoding. It discovers supported match-card player groups throughout the selected Discovery data folder, including custom export folders outside `UI/Projects`. Exact catalog links separately authorize supported custom outer containers. A project appearing in the folder scan alone does not authorize changes to unrelated menus. Each group must match a supported layout before changes are made. It keeps byte-identical backups under `Gorilla Studio Layout Backups` with `.bak` extensions. Unsupported custom structures and setup failures are listed explicitly. Bake once in Discovery after any changes, then restart WWE.

When selection moves but **every arena's match card stays still**, omitted match types alone do not establish the cause. Selection finds renders through picture references; match cards use logical wrestler IDs and a separate game hook. Check the installed GMR's **Match card** setting and target ID first. If a setup, bake and restart still leave a standard one-on-one card static, inspect `<game>/Plugins/WWE2K26_MovingRenders.log` and `GorillaRenders/_scan_ids.txt` for the ID lookup and game-build compatibility. The included plugin has not been modified or verified against the user's running game.

## Discovery taunts and previews

Keep the **entire extracted Discovery data folder together**, including MoveData and Animation. The loader reads actual taunt names and IDs from `MoveData/CAMMoveData.jsfb` and joins them to animation records in `.clips` containers and loose `.clip` files. It searches the entire selected Discovery data folder, including nested mod folders and exports outside the top-level Animation folder. A newer loose edited clip takes precedence over a container record. It converts supported body motion into the original engine's animation format automatically; no Python installation or separate animation browsing is required.

The uploaded data contains **1,584 named taunt variants**. With the verified Animation archive recovery pack, the supplied Discovery data loads **1,515 playable library entries**, up from 1,088; 427 use recovered records. The pack contains 617 supported records absent from the original bundled catalog and checks each record's exact ID, length and SHA-256. It is embedded in the helper, so the full 2.78 GB split archive does not need to be loaded just to recover these motions. Extracted edits and existing bundled entries retain priority. A custom motion stored only in a CAK must be extracted into Discovery to override a verified shipped fallback.

**Undisputed 2**, animation `62541a000`, retains its 281 frames at 60 fps (about 4.68 seconds). Jeff Hardy 2–6, Enigma 1–4, Confidence 1 and Fighting Spirit 2 are supported in the recovery pack. Jeff Hardy 7 and 8 are present in the archive but use additional animation sections that the current decoder cannot read; their rows explain this instead of claiming their files were not located. **6-7** is named in CAM with the unusual exact reference `100000a255` (DataMoveId 26550404); that record is absent from all 169,665 checked archive entries. It stays searchable with its exact missing reference, while normal game/Discovery archive lookup can still find an active copy on your PC. Variations keep separate animation files and renderer keys.

**Every named Discovery taunt now stays in the search list.** Search accepts names, aliases and exact animation IDs, as well as `confidences`, `fight spirits` and `Undisputed2`. Numeric variants remain distinct: searching Jeff Hardy 2 cannot select Jeff Hardy 1 or 12. An animation that could not be located or imported appears as a disabled row with its reason and exact clip ID; it does not vanish from search or become a broken render entry. Lookup misses show the searched folder and distinguish incomplete scans, source-read failures, decoder limitations and preview-cache errors. The tooltip includes the first scan warning when files could not be read. Unmatched taunts are looked up automatically in CAK archives beneath your configured game and Discovery folders. The reader follows actual indexed .clip/.clips paths and verifies the canonical animation record inside them; it does not substitute a similarly named move. Extracted edits and already playable bundled motions retain priority. Different archived versions of one clip, unreadable sources and incomplete searches remain unavailable with an explanation. An unresolved lookup does not establish that the move is absent from the game. The list shows how many matching entries are unavailable.

Selecting a wrestler no longer imports the full taunt library twice. The loaded library is reused while the game and Discovery sources remain unchanged. Source watchers invalidate reuse after relevant file changes; **Refresh data** and explicit Discovery loads also retry. Unavailable monitoring, replaced source folders and linked external source trees retain conservative rescans. Roster parsing, model planning, picture conversion and compatibility-folder preparation run off the UI thread; the picker and final selection remain on it. Native render controls are guarded during selection. Windows interaction timing has not been measured here.

Each data load now indexes extracted animation containers once and reuses that index for lookup and library construction. Lightweight checks detect changed membership, file metadata and linked targets; source watcher revisions also reject observed archive changes during a load. Rejected attempts keep the previously active library. These checks do not provide a simultaneous filesystem snapshot. Loading remains cancellable before the final library commit.

The first archive motion scan can take time. It starts after roster metadata is available, without disabling the whole Studio. Game/mod CAK recovery waits for interactive roster and picture work to become idle before starting. Progress and **Cancel load** remain available during scanning and private library construction; a newer data load supersedes the old one. Each short library switch waits for native preparation, rendering and batches to finish. Canceling later archive recovery keeps the already loaded matching library available. The switch invalidates preparation from the previous animation context and warms only the selected model for MAKE. Lookup keeps requested records and verified record catalogs in a private cache and discards temporary extracted containers. Later refreshes reuse unchanged catalogs, including lookups that found no matching record. Adding or changing an archive invalidates that source's cache. Canceling before the final library update keeps the current list. Game and Discovery source files remain unchanged.

Archive indexing and extraction now allow the original native setup's limits (five and three minutes per operation) instead of prematurely rejecting a slow readable source after 45 or 90 seconds. These are maximum waits, not delays added to successful reads; cancellation remains available.

Use **Animation details…** to read or copy the full diagnostic report when a taunt is unavailable. The report includes the current game and Discovery folders, archive counts, all retained scan warnings and exact clip IDs. Search matches appear first; no tooltip or selection of a disabled taunt is needed. Incomplete archive searches now also mark the overall load status incomplete. Index errors identify the archive's full path.

Identical requested motion records at the same indexed path in multiple CAKs no longer block the entire animation search. Every copy is checked. Different bytes or inconsistent record presence leave that requested clip unresolved; a duplicate path alone does not invalidate unrelated verified clips. Unreadable sources still prevent acceptance when the active motion cannot be established. Reading and decoding the recovery pack does not verify Blender images or gameplay on Windows.

Taunts refresh after **Browse folder**, drag-and-drop and roster selection. A folder load waits for model preparation or active rendering before refreshing the latest selected Discovery folder. Adding a missing supported animation and loading that data again turns its unavailable result into a playable entry.

Version **1.7.7** fixes **“Randy preview stopped: Key cannot be null. Parameter name: key”**. The PNG loader incorrectly combined a stream-only image with a URI-cache eviction flag. It now opens a fresh stream, loads the full image before closing the file and freezes it, without calling the URI cache. The fix covers both Randy and the older Discovery preview path. Retry **Preview** after updating; a successfully generated Randy atlas already saved in the private cache can be reused. That hotfix changes image display, not animation speed or rendering quality.

All taunt previews now use **Randy Orton**, including the main **Preview** button. Selecting a wrestler or alternate attire does not prepare that wrestler's taunt just to browse animations. **MAKE** and batches still render the selected wrestler and exact selected attire. A Randy preview is labelled clearly and cannot be installed or saved under the selected wrestler's name.

**860 verified stock Randy preview strips** can play without Blender or model preparation. Version **1.7.8** restores reuse after Discovery rewrites a matching animation: ZIP packaging, JSON formatting and the decoder's measured tiny quaternion rounding no longer force a new render. Actual pose, scale, timing, cut and facial-motion changes still reject an old stock preview. **Undisputed 3** was reproduced with the uploaded motion and now opens its existing Randy image without Blender.

Selecting a playable taunt automatically starts its Randy preview. Ready stock and saved previews open immediately. An uncached taunt queues generation after a short pause on that selection; switching taunts cancels the superseded request. MAKE and Batch MAKE take priority over automatic generation. The first uncached preview still requires real model preparation and rendering, then its saved image can be reused. Changed or unsupported motion is not replaced with another taunt. Previously captured previews of other wrestlers are not presented as Randy.

Randy's private preview cache tracks the current motion, fixed model/attire and renderer inputs. It does not rescan the entire selected wrestler's Discovery roster or model collection for every preview. Successful cached Randy previews can display while the selected wrestler prepares for MAKE. New private previews also survive an unchanged Discovery refresh, even though its temporary folder and copied-file timestamps change. **Preview** retries the current taunt manually. Programmatic startup and list refreshes do not render the whole library.

Generated animation data, working captures and previews live in a writable folder under `%LOCALAPPDATA%/GorillaStudio/NameFolders`, alongside the preview cache. The original Discovery files and application assets remain intact. Unchanged refreshes reuse decoded motion and keep the selected taunt.

Studio reads the saved Discovery files, not the current selection in Discovery's running window. The existing stock-game extractor uses its bundled asset list and a default attire; it does not implement arbitrary mod CAK model recovery. Exact MCD references alone do not establish a complete body/material/texture dependency set or the game's active mod precedence. This release supports complete extracted mapped sources across remembered locations and does not claim general game-style CAK loading.

## Prepare Randy previews ahead of time

Click **Build Randy previews…** below taunt search to prepare the currently loaded library once. The builder checks every playable taunt, including entries hidden by the search filter, skips valid stock or saved Randy previews, and renders missing ones one at a time. New Discovery taunts are included after that data loads; unavailable motions remain unavailable.

The progress window shows saved, generated and failed counts. **Cancel** stops the current owned preview processes and keeps completed cache entries. Click **Build Randy previews…** again to continue: already saved previews are skipped. Browsing a taunt, manual Preview, MAKE and Batch MAKE take priority and stop the build. Closing Studio or changing animation/game/Blender context also stops queued work. Your selected wrestler, attire and render output are retained. The builder only creates Randy browsing previews; MAKE and installation remain separate.

Fresh previews still require Blender and the fixed Randy model from the installed game. Building hundreds of missing taunts can take substantial time once; after they are saved, ordinary browsing reuses those images. The app does not generate the entire library automatically at startup or claim a fixed first-build time.

## Mouth movement

Version **1.7.11** preserves an existing matching facial track when Discovery imports a changed body motion under a new private taunt key. Reuse requires the same exact clip and variant, matching frame count and frame rate, no body cut, and valid original indexed facial files. The original files stay unchanged. Randy preview caches now include the effective facial track, so an old image is rejected if that track changes.

The original facial renderer was exercised with the supplied complete Bobby Lashley model and All Fed Up: its mouth shapes and jaw move with the supplied facial curve. This does not establish a facial association for every newly discovered taunt. The Animation archive contains facial records, but the uploaded metadata does not provide the necessary taunt-to-face links; a matching face is never guessed from a neighboring clip. Hair and cloth YCL physics are still unsupported. Existing GMR files contain their old rendered frames; **MAKE and install again** to apply a rendering fix.

The new complete Jeff pack also passes the original **Jeff Hardy 1** indexed facial track: 762 morph instances across five meshes and both jaws respond in the native production-rest test. The sample reaches about 9.84° of jaw motion. This verifies that exact existing association, rather than adding faces to all Discovery variants. Further inspection of all 8,446 decoded CaeAdvanced definitions found no exact link to the checked raw facial records or canonical body-taunt hashes; unproven associations remain unresolved.

## PNG preview folder

A successful **MAKE** saves one PNG pose in **your configured Output folder → Previews**. **Previews folder** opens that destination. **Save PNG** also exports a pose from the completed selected-wrestler MAKE. Filenames include wrestler ID, name, attire and taunt; repeated exports keep earlier files. Randy taunt previews use their separate private cache and cannot be exported as the selected wrestler.

PNG export saves one frame at its original pixel dimensions and alpha. An opaque source stays opaque. MAKE movies and batch files keep their normal output destination.

## Missing cached model or skeleton error

Version 1.7.6 fixes a cache validation bug that rejected valid compressed Blender models. It could remove the freshly saved `model.blend` after the importer had printed success, causing the later skeleton step to report **“the skeleton could not be read”**. Cache validation now supports compressed files, holds the success marker until commit, and restores model/mesh-manifest pairs together on failure. Retry the affected wrestler after updating; a missing model file triggers a fresh preparation automatically.

Your three supplied logs report a missing cached `model.blend`; they do not establish damaged bones. Failed skeleton steps now include the actual fresh log error and location when ownership can be verified. Logs normally live under **Output folder → work → <model cache> → rest.log**. Native import and dump tests pass in Linux Blender 4.3.2; Brody King's full model and Windows Blender 5.2 rendering were not available for a visual check.

## Faster previews and renders

Preview and MAKE always use fast rendering. There is no speed selector. Saved settings from an older build cannot turn it off.

| Operation | Original defaults | Faster mode | Calculated rendering work |
| --- | --- | --- | --- |
| Randy taunt Preview | 384 × 384, 16 samples | 256 × 256, 8 samples | 77.8% fewer pixel-samples |
| MAKE GORILLA RENDER | 1024 × 1024, 64 samples | 1024 × 1024, 8 samples | 87.5% fewer samples |

These are work calculations, **not measured time savings**. Model loading, Blender startup, encoding, GPU load and other fixed costs still take time. Preview retains its animation frames and playback rate. MAKE retains full output resolution and animation timing, with fewer lighting samples; shadows may look noisier. Close WWE while rendering to leave the GPU available.

Version 1.7.8 removes unexpected Blender preview jobs during browsing, leaving the renderer available for MAKE. MAKE's own quality, 1024 × 1024 resolution and complete animation frames are unchanged. A Linux preparation benchmark found approximately 0.20 seconds of combined hair/gear callback work over 200 frames; a rest-inverse prototype saved only 0.07 seconds over a 90-frame pose phase. Neither establishes a meaningful full-MAKE speedup, so no fragile pose hook or extra quality reduction was shipped. Windows GPU/render/encoding time remains unmeasured.

The original engine's normal parallel MAKE path bypassed `GORILLA_EXTRA`, so older fast settings did not reliably reach full rendering. The new `engine/gr_render.py` entry point forwards those settings to the unchanged compiled renderer. Native command and renderer-setting checks confirm all full-render branches now receive the sample setting.

Unchanged Randy taunt previews reopen from verified stock strips or the dedicated private cache without starting Blender. Changes to that motion, Randy assets or renderer inputs invalidate the private entry. Cached images are separate copies, and corrupted entries are regenerated. Full MAKE continues through the original engine on your selected wrestler.

Full rendering after Preview stays off so browsing previews leaves the GPU available. MAKE starts when you request it or start a batch.

Discovery's initial load has a **five-second target for roster handoff**. The supplied 1,457-profile dataset measured **68–131 ms** for quick metadata calls in this Linux environment; filesystem caches were not cleared, and no Windows GUI timing was measured. A controlled eight-second archive stall did not delay metadata handoff. These checks do not promise every animation, model, picture or archive will finish in five seconds. The uploaded copy contains ten extracted models; loading time on your PC and larger collection still needs measuring.

The small timer below Preview records native rendering requests, with initial model preparation separate. Prebuilt Randy strips do not launch a native render. The supplied screenshot records an original MAKE at 229.5 seconds; a new elapsed time must be measured on the same PC and job. Windows/GPU timings have **not** been measured in this Linux build environment, so no exact seconds saved or speed multiplier is claimed.

Output remains **30 fps**. The current GMR writer and Moving Renders plugin explicitly reject 60 fps. Supporting 60 fps requires coordinated exporter, timing and plugin changes; changing only Blender's FPS setting would not produce a compatible game render.

After adding characters, save your updated CharProfileTable.jsfb and Sdb/ENG.sdb, put their extracted models inside a remembered models folder, then click **Refresh data**. It rereads the current roster and names, rescans the model folders, and includes new IDs. For an imported JSON roster, Refresh data reloads that same JSON file during the current session. Renamed characters replace stale saved labels; conflicting saved model choices require selection. Reopening Discovery also rereads its current files.

A roster profile can exist without its model in the selected data. **Model files missing** means that the name and ID were read, but no matching extracted model has been located. Point the model library at the parent folder containing your actual mod folders once. The supplied Discovery archive itself contains ten extracted character models; the other profiles need their models from your collection or a verified installed-game mapping.

Existing explicit model associations remain authoritative. Fresh matches prefer the exact CharacterMapping asset-path hash, then exact names and proven stock mappings. Duplicate matches require your choice. A physical folder number is not enough to override a conflicting localized profile: the uploaded physical 109 Hollywood Hogan folder no longer loads as Karl Anderson's logical slot 109. Unassigned physical models remain in the separate model list. Verified installed-game models retain the original extractor. You can explicitly map any discovered model to another profile with **Use folder for another wrestler…**.

ENG.sdb supplies readable character names. Keep your current file in Sdb/ENG.sdb. Roster loading validates each string independently: an invalid offset, missing declared terminator, invalid UTF-8 text or conflicting duplicate key leaves that key unavailable while validated names remain usable. Warnings appear inside the roster window. Missing localized strings display as “Wrestler <ID>”; actual roster IDs stay unchanged. An unreadable SDB header or index produces explicit warnings and ID-based names. The loader does not guess string offsets, substitute localization keys as names, or rewrite your source SDB.

**Batch MAKE**, Browse/drop and the older roster entry point use that same name recovery. Batch names appear before model indexing finishes, and MAKE becomes available after indexing. Invalid localized names produce inline warnings while valid names and real wrestler IDs remain selectable. Closing the batch dialog cancels its pending roster/model lookup.

Only **JSON and JSFB** are accepted by **Load roster file…**. SDB is read automatically as companion name data. JSON supports arrays such as [{"id":1506,"name":"Flammer"}], common roster wrappers, and name-to-ID or ID-to-name maps. Actual wrestler IDs must be 1–65535. The included rosters/Discovery_roster.json is a portable name/ID export; point the loader at your Discovery folder to discover its models.

Dropping a character folder also opens the picker, keeping that folder as the model source. The selected logical roster ID controls the target slot and render links; the physical source ID controls stock-body extraction. For example, the supplied mapping associates physical folder 312_CM_Punk with roster ID 1481.

Saved manual entries and model choices reopen with the same roster. Freshly scanned model mappings take precedence. Older saved folder-derived entries are refreshed to their actual mapped IDs. Settings and NTFS compatibility links are stored under %LOCALAPPDATA%/GorillaStudio/NameFolders. Original character folders keep their names and contents. Plain-name links require a local Windows drive supporting NTFS junctions.

## Validation

- The original Discovery fixture loads **1,457 roster profiles**, with **1,308 localized names**, and finds all **10 extracted model folders** in that fixture. Eight model associations are proven by exact mapping hashes, Brock Lesnar matches by name, and one unmatched physical Hollywood Hogan model remains unassigned. Video examples NICK JACKSON '14 (204), DAX HARWOOD (211), and CASH WHEELER (213) match the uploaded SDB. The later complete Xavier/Kofi packs are tested separately.
- **10,155 portable checks** are recorded across 84 suites/modes. Version 1.7.17 validates 6 suites/modes (425 checks); the other 78 retain their earlier passing validation versions. This includes 54 completed-render recovery checks and 38 installation checks, covering canceled batches, large generated caches, exact movie/map/policy preservation and disabled plugin settings. Independent source review passes and the helper builds with zero warnings/errors. No Windows GUI, live game playback or matchup-hang fix is claimed.
- Actual Discovery tables confirm Demon Finn 626's two active costume mappings, their default and CARNAGE labels, and exclusion of regular Finn 576 and inactive variant five. Fixtures cover separate complete model sources, exact costume identity, stale mappings and batch output isolation. The supplied ten extracted model folders do not include either Demon model; their actual Windows loading and rendered images remain unverified.
- Growth tests append valid new profiles and SDB strings to a separate copy of the actual data: **1,457 → 1,458 → 1,459** profiles. A copied real model binds to new logical ID 4000 with physical source 1506; edited JSON files reload new characters; high new ID 65527 preserves existing reserved IDs. Uploaded originals remain unchanged.
- Portable importer/session, binary mapping, and extraction-output tests cover profile decoding, malformed data, ambiguous paths, saved settings, legacy entries and explicit slot selection. Both the .NET Framework 4.8 helper and patcher build without warnings or errors.
- A copied real character in a plain-name folder remains visible before assignment, then loads and reopens with an explicit added roster entry. Original MCD bytes stay unchanged.
- The original v1.0.2 engine verifies physical 312 → logical 1481 and physical 123 → logical 910. It retains the original model/body source while using the selected profile's render links and gender. The 1481 case selects 148100_CM_PUNK_GM.
- A real render attempt loads the added Flammer model and reaches original stock-body extraction, then stops because this environment lacks WWE 2K26 / oo2core_9_win64.dll. Complete Flammer rendering and Windows GUI/junction interaction remain unverified.
- The original default-attire function is exercised on the original ten model folders and direct-MCD compatibility layouts. The independent motion decoder matches a bundled animation's translations exactly and its quaternions within 0.000001. The unmodified original capture engine consumes the imported Undisputed 2 motion and produces 281 frames for a 175-bone skeleton, including 74 H helper bones. That imported-motion fixture does not establish a full animated movie render; the later Xavier material test renders matched Blender images separately.
- The original engine finds the prepared image for Avery's actual profile, samples its automatic hair colour, and accepts a finalized GMR with the actual picture hash and all 1,139 installation-map entries. These checks use a synthetic image and structural video fixture; they do not claim an in-game visual test.
- DDS decoding is compared with independent Pillow pixels for every BC7 mode, transparent alpha and DXT formats, and loads the uploaded real 2048 × 1024 Tripwire DDS. Archive tests exercise the shipped command vectors using fixture indexes and DDS outputs, plus real process cancellation and timeouts on Linux. They do not execute Bakery's Windows binary or prove PAC's missing texture pixels are present in the uploaded files. Match-card checks verify the actual 188-file screen pack and explicit setup result handling. Windows GUI, native game archive extraction, PAC's archive-picture result and match-card gameplay remain unverified.
- Match-card repairs are checked across all 86 supplied match-card projects and 20 supported layouts: 4,472 missing layers are completed, valid existing layers are preserved, malformed owned markers/transforms are repaired, and repeat setup changes nothing. Independent original-engine checks compare all 101 slots in RAW 24 with the native full-layout recipe and inspect the repaired corpus. All 101 placeholder movies carry the plugin's layout/player encoding; a newly generated movie decodes with FFmpeg. The current Discovery audit follows all 282 linked card records and identifies 96 project references absent from the uploads. These are file-format checks, not observed match-card gameplay.
- The unchanged native pipeline and Blender renderer settings execute in **171 structural checks**: full renders receive 8 samples at 1024 × 1024, 30 fps with the full motion. **633 hair-material checks** cover hash-named textures, missing-map fallback, selected and natural colours, material isolation, transparency and cached scenes, including switching a cached manual tint back to Auto. **116 native attire checks** confirm explicit clothes selection and separate model caches across preparation, Preview and MAKE. Bulk tests validate IDs, output isolation, malformed movies, shared picture maps, backups and repeat installs. Actual Windows batch rendering, GPU timings and distinct alternate-attire images remain unverified.
- The patcher verifies a five-method allowlist: the window constructor, folder-loading state machine, Go state machine, process-launch state machine and automatic-render entry point. All other method bodies, authentication/licensing and managed/native resources are preserved. Engine binaries remain unchanged; faster detail is passed only to each applicable render process.

Detailed results are included in TEST_RESULTS.json inside the ZIPs.

## Build

Requires the .NET 8 SDK and the supplied original v1.0.2 release. Building on Linux is supported; running requires Windows.

    python tools/build_renderer_bundle.py
    python tools/build_renderer_bundle.py --check
    python tests/validate_geometry_import.py
    python tests/validate_geometry_cache.py
    python tests/validate_renderer_bundle.py --blender /path/to/blender --output /path/to/bundle-report.json
    dotnet run --project tests/LocalAttireDiscoveryTests -c Release -- /path/to/discovery_data
    python tests/validate_body_masks.py
    blender --background --factory-startup --python-exit-code 1 --python tests/validate_body_masks_blender.py -- --application /path/to/original/GorillaStudio --character /path/to/297_Bobby_Lashley_AEW --output /path/to/body-mask-report.json
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
    dotnet run --project tests/BatchRosterLoadingTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/RosterQuickLoadTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/DiscoverySdbTests -c Release -- /path/to/Sdb/ENG.sdb
    dotnet run --project tests/ModelInventoryBindingTests -c Release
    dotnet run --project tests/DiscoveryDataLoadCoordinatorTests -c Release
    dotnet run --project tests/DiscoveryLibraryRefreshStagesTests -c Release
    dotnet run --project tests/DiscoveryTauntCancellationTests -c Release
    dotnet run --project tests/RosterModelResolutionTests -c Release
    dotnet run --project tests/MappedModelLibraryTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/AutomaticAttireTests -c Release
    dotnet run --project tests/DiscoveryAttireCatalogTests -c Release -- /path/to/discovery_data
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
    dotnet run --project tests/RandyPreviewAssetsTests -c Release -- /path/to/original/GorillaStudio/data/gamepack/stock_index.json
    dotnet run --project tests/RandyTauntPreviewCacheTests -c Release -- /path/to/original/GorillaStudio
    dotnet run --project tests/RandyTauntPreviewStateTests -c Release
    dotnet run --project tests/SkeletonFailureDetailsTests -c Release
    dotnet run --project tests/DiscoveryAnimationReportTests -c Release
    dotnet run --project tests/DiscoveryMatchupSetupTests -c Release -- /path/to/original/GorillaStudio /path/to/discovery_data
    dotnet run --project tests/AlternateAttireTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/BatchRenderPlanTests -c Release -- /path/to/discovery_data
    dotnet run --project tests/BatchAttirePlanTests -c Release
    dotnet run --project tests/BatchMappedAttireTests -c Release
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
