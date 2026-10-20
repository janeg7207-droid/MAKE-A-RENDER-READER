"""Compare managed match-card repairs with the unmodified shipped Scre decoder.

Run with the engine's CPython 3.14 and the fixture directory exported by
MatchCardLayoutsTests. This checks screen structure, unchanged existing layers,
the exact native movie recipe and the plugin's placeholder identity encoding.
It does not start WWE 2K26 or establish in-game playback.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine-modules", type=Path, required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    app = args.app.resolve()
    os.environ["GS_ROOT"] = str(app)
    sys.path.insert(0, str(args.engine_modules.resolve()))
    import fbclone
    import make_matchups_all as native

    checks: list[str] = []

    def equal(name: str, expected, actual) -> None:
        if expected != actual:
            raise AssertionError(f"{name}: expected {expected!r}, got {actual!r}")
        checks.append(name)

    template = (app / "data/gamepack/screens/template_matchup.jsfb").read_bytes()
    tfb, tnodes = native.tree(template)
    tbg = next(node[0] for node in tnodes if node[1] == ["bg"])
    movie_recipe = fbclone.plain(tfb, tbg)
    native.MARKER = True
    expected, count = native.patch(template, tfb, tbg, list(native.LAYOUTS))
    expected_fb, expected_nodes = native.tree(expected)
    expected_by_path = {tuple(node[1]): node for node in expected_nodes}
    completed = (args.fixtures / "RAW24.completed.jsfb").read_bytes()
    completed_fb, completed_nodes = native.tree(completed)
    completed_by_path = {tuple(node[1]): node for node in completed_nodes}
    equal("The unmodified native full-layout recipe covers 101 player slots", 101, count)
    equal("Managed completion has the same layer paths as the native full-layout recipe",
          sorted(expected_by_path), sorted(completed_by_path))
    for path, node in expected_by_path.items():
        if len(path) == 4 and path[0] == "matchup" and path[2].endswith("P"):
            equal("Native recipe leaf equivalence: " + "/".join(path),
                  fbclone.plain(expected_fb, node[0]),
                  fbclone.plain(completed_fb, completed_by_path[path][0]))

    pack = app / "data/gamepack/screens/BakeMe"
    total_added = 0
    screens = 0
    for file in sorted((args.fixtures / "all").rglob("*.jsfb")):
        relative = file.relative_to(args.fixtures / "all")
        if "CharacterSelect" in str(relative):
            equal("Character-selection screens are unchanged: " + str(relative),
                  (pack / relative).read_bytes(), file.read_bytes())
            continue
        before = (pack / relative).read_bytes()
        after = file.read_bytes()
        fb, nodes = native.tree(before)
        fb2, nodes2 = native.tree(after)
        old = {tuple(node[1]): node for node in nodes}
        new = {tuple(node[1]): node for node in nodes2}
        old_slots = {path: node for path, node in old.items()
                     if len(path) == 3 and path[0] == "matchup"
                     and path[1] in native.LAYOUTS
                     and path[2] in {str(n) + "P" for n in range(1, 9)}}
        changed_fields = set()
        added = 0
        for path, group in old_slots.items():
            layout = native.LAYOUTS.index(path[1])
            player = int(path[2][0])
            new_movie = new[path + ("GM_clip",)]
            equal(str(relative) + " playing movie " + "/".join(path), 7, new_movie[2])
            equal(str(relative) + " correct placeholder " + "/".join(path),
                  native.fnv1a64(native.ph(path[1], player)), new_movie[3])
            if path + ("GM_clip",) in old:
                for child in ("CAS_dummy", "SS_dummy", "GM_clip"):
                    equal(str(relative) + " unchanged existing " + "/".join(path + (child,)),
                          fbclone.plain(fb, old[path + (child,)][0]),
                          fbclone.plain(fb2, new[path + (child,)][0]))
                continue
            added += 1
            field = group[0] + fb.slots(group[0])[0][55]
            changed_fields.update(range(field, field + 4))
            equal(str(relative) + " preserved CAS " + "/".join(path),
                  fbclone.plain(fb, old[path + ("CAS_dummy",)][0]),
                  fbclone.plain(fb2, new[path + ("CAS_dummy",)][0]))
            old_still = fbclone.plain(fb, old[path + ("SS_dummy",)][0])
            new_still = fbclone.plain(fb2, new[path + ("SS_dummy",)][0])
            equal(str(relative) + " preserved SS properties " + "/".join(path),
                  {k: v for k, v in old_still.items() if k != 5},
                  {k: v for k, v in new_still.items() if k != 5})
            want_marker = struct.pack("<f", native.mark(layout * 8 + player)).hex()
            equal(str(relative) + " exact slot marker " + "/".join(path),
                  [want_marker, want_marker], [new_still[5][0], new_still[5][1]])
            current = fbclone.plain(fb2, new_movie[0])
            equal(str(relative) + " native movie settings " + "/".join(path),
                  {k: v for k, v in movie_recipe.items() if k not in (0, 1, 3, 4, 5)},
                  {k: v for k, v in current.items() if k not in (0, 1, 3, 4, 5)})
            equal(str(relative) + " exact native movie transform " + "/".join(path),
                  ([0.0, -551.0], [1024.0, 1024.0], [1102.0 / 1024.0] * 2),
                  tuple(native.floats(fb2, new_movie[0], field) for field in (3, 4, 5)))
        equal(str(relative) + " only original child pointers change", [],
              [i for i, (a, b) in enumerate(zip(before, after)) if a != b and i not in changed_fields])
        equal(str(relative) + " exactly one layer added per omitted slot", len(nodes) + added, len(nodes2))
        equal(str(relative) + " all existing layer identities retained", [],
              [path for path, node in old.items() if path not in new or node[2:4] != new[path][2:4]])
        total_added += added
        screens += 1
    equal("All 86 shipped match-card projects decoded independently", 86, screens)
    equal("All 4,472 omitted slots follow the original recipe", 4472, total_added)

    repaired_existing = 0
    repairs = args.fixtures / "existing-repairs"
    if repairs.exists():
        for file in sorted(repairs.glob("*.jsfb")):
            if file.name.endswith(".original.jsfb"):
                continue
            original = file.with_name(file.stem + ".original.jsfb").read_bytes()
            fixed = file.read_bytes()
            old_fb, old_nodes = native.tree(original)
            fixed_fb, fixed_nodes = native.tree(fixed)
            old = {tuple(node[1]): node for node in old_nodes}
            current = {tuple(node[1]): node for node in fixed_nodes}
            equal(file.name + " retained existing hierarchy", sorted(completed_by_path), sorted(current))
            for path, expected_node in completed_by_path.items():
                if len(path) == 4 and path[0] == "matchup" and path[2].endswith("P"):
                    equal(file.name + " exact independently decoded native leaf " + "/".join(path),
                          fbclone.plain(completed_fb, expected_node[0]), fbclone.plain(fixed_fb, current[path][0]))
            group = old[("matchup", "1on1", "1P")][0]
            field = group + old_fb.slots(group)[0][55]
            equal(file.name + " only original child pointer changes", [],
                  [index for index, (before, after) in enumerate(zip(original, fixed))
                   if before != after and index not in range(field, field + 4)])
            equal(file.name + " no player or layer identity changes", [],
                  [path for path, node in old.items() if path not in current or node[2:4] != current[path][2:4]])
            repaired_existing += 1
        equal("Six incomplete existing-marker/movie-transform fixtures independently verified", 6, repaired_existing)

    basis = (pack / "UI/Assets/movies/gorilla_renders/ph_L00_1P.bk2").read_bytes()
    movie_count = 0
    for layout, slots in enumerate(native.LAYOUT_SLOTS):
        for player in range(1, slots + 1):
            file = args.fixtures / "movies" / Path(native.ph(native.LAYOUTS[layout], player)).name
            data = file.read_bytes()
            numerator, denominator = struct.unpack_from("<II", data, 28)
            equal(file.name + " unchanged encoded movie bytes", basis[:28] + basis[36:], data[:28] + data[36:])
            equal(file.name + " exact equivalent frame rate", 30000 * denominator, 1001 * numerator)
            multiplier = denominator // 1001
            equal(file.name + " plugin layout/player identity",
                  (layout, player), ((multiplier - 32) >> 3, ((multiplier - 32) & 7) + 1))
            movie_count += 1
    equal("101 correctly identified placeholders", 101, movie_count)
    # Decode one newly supported layout's movie with an independent video decoder.
    new_movie = args.fixtures / "movies/ph_L02_1P.bk2"
    process = subprocess.run(["ffmpeg", "-v", "error", "-i", str(new_movie), "-f", "null", "-"], capture_output=True, timeout=30)
    equal("A newly generated placeholder decodes independently with FFmpeg", 0, process.returncode)
    report = {"checks": len(checks), "screen_files": screens, "new_slots": total_added,
              "placeholder_movies": movie_count, "native_full_recipe_slots": count,
              "repaired_existing_slot_fixtures": repaired_existing,
              "runtime_playback_tested": False,
              "completed_RAW24_sha256": hashlib.sha256(completed).hexdigest()}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
