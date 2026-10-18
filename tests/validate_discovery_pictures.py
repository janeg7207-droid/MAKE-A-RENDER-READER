"""Check Discovery picture integration with the unmodified packaged engine.

Requires the fixtures exported by DiscoveryRenderOverlayTests (second argument)
and DiscoveryRenderResultTests (second argument), CPython 3.14, NumPy and Pillow.
The image and one-frame Bink prefix are structural test fixtures, not user art or
a completed render. This does not test Windows, Blender or the running game.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("engine-modules", "application", "discovery", "overlay", "render", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    os.environ["GS_ROOT"] = str(args.application.resolve())
    # Load platform-native packages before exposing the packaged Windows modules.
    import numpy as np
    from PIL import Image
    sys.path.append(str(args.engine_modules.resolve()))
    import gmr
    import gorilla_render
    import roster_lookup

    checks = 0

    def check(condition):
        nonlocal checks
        assert condition
        checks += 1

    actual = "1709AEF076130352"
    original_table = args.discovery / "Roster/CharProfileTable.jsfb"
    before = hashlib.sha256(original_table.read_bytes()).hexdigest()
    table, native = roster_lookup.renders_for(122, str(args.discovery))
    check(Path(table).samefile(original_table))
    check(native == [])  # Reproduce the original custom-picture whitelist bug.
    table, pictures = roster_lookup.renders_for(122, str(args.overlay))
    check(Path(table).samefile(args.overlay / "Roster/CharProfileTable.jsfb"))
    check(len(pictures) == 1)
    picture = pictures[0]
    check(picture["name"] == "discovery_" + actual.lower())
    check(Path(picture["file"]).is_file())
    with Image.open(picture["file"]) as image:
        check(image.size == (256, 256))
        check(image.convert("RGBA").getpixel((0, 0)) == (60, 40, 20, 255))
    colour = gorilla_render.hair_colour_from_render(picture["file"])
    check(np.allclose(colour, np.array([60, 40, 20]) / 255.0))
    ramp = gorilla_render.hair_ramp("auto", picture["file"])
    check(len(ramp) == 2 and all(np.isfinite(value).all() for value in ramp))

    metadata, video, parsed = gmr.read(str(args.render))
    check(metadata["wrestler_id"] == 122)
    check(metadata["render_hashes"] == [actual])
    check("render_names" not in metadata)
    check(metadata["screens"] == "both")
    check(metadata["extra_ids"] == [] and metadata["shared_hashes"] == [])
    check(parsed["end"] == len(video) == 56)
    lines = (args.render.parent / "_roster_pictures.txt").read_text().splitlines()
    check(len(lines) == 1139)
    check(actual + " 122" in lines)
    check(not any(line.startswith(picture["hash"] + " ") for line in lines))
    check(before == hashlib.sha256(original_table.read_bytes()).hexdigest())
    report = {
        "checks_passed": checks,
        "actual_wrestler_id": 122,
        "actual_picture_hash": actual,
        "native_image_lookup_passed": True,
        "native_auto_hair_image_sampling_passed": True,
        "native_gmr_reader_passed": True,
        "installation_map_hashes": len(lines),
        "temporary_picture_keys_removed_from_installation": True,
        "original_profile_table_unchanged": True,
        "image_is_synthetic_test_fixture": True,
        "video_is_structural_fixture_not_decoded": True,
        "windows_blender_game_executed": False,
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
