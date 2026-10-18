"""Create independent Pillow references for the portable DDS preview decoder.

Usage: python validate_roster_picture_preview.py OUTPUT_DIR [REAL_DDS]
Then run RosterPicturePreviewTests with OUTPUT_DIR as its first argument.
The random BC7 blocks cover all eight valid modes, not an encoder round trip.
"""

from pathlib import Path
import random
import shutil
import struct
import sys
from PIL import Image


def put(data, offset, value):
    struct.pack_into("<I", data, offset, value)


def main():
    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)
    rng = random.Random(713)
    # Eight modes, eight independently randomized blocks for each mode. Force
    # each unary mode prefix but leave endpoints, partitions and indices random.
    header = bytearray(148)
    for offset, value in ((0, 0x20534444), (4, 124), (8, 0x81007), (12, 32),
                          (16, 32), (20, 1024), (76, 32), (80, 4),
                          (84, 0x30315844), (108, 0x1000), (128, 99),
                          (132, 3), (140, 1)):
        put(header, offset, value)
    blocks = bytearray()
    for row in range(8):
        for mode in range(8):
            block = bytearray(rng.getrandbits(8) for _ in range(16))
            block[0] = (block[0] & ~((1 << (mode + 1)) - 1)) | (1 << mode)
            if row == 7 and mode == 6:
                # A valid mode-6 block with zero RGBA endpoints covers full
                # transparency in addition to random partially transparent blocks.
                block = bytearray(16)
                block[0] = 0x40
            blocks.extend(block)
    (output / "bc7-all-modes.dds").write_bytes(header + blocks)
    source = Image.new("RGBA", (7, 9))
    source.putdata([((x * 41 + y * 7) % 256, (x * 13 + y * 29) % 256,
                     (x * 19 + y * 37) % 256, (x * 23 + y * 31) % 256)
                    for y in range(9) for x in range(7)])
    for name, pixel_format in (("dxt1-odd", "DXT1"), ("dxt3-odd", "DXT3"), ("dxt5-odd", "DXT5")):
        source.save(output / (name + ".dds"), pixel_format=pixel_format)
    source.save(output / "raw-rgba.dds")
    if len(sys.argv) > 2:
        shutil.copyfile(sys.argv[2], output / "tripwire-real.dds")
    else:
        source.convert("RGB").save(output / "raw-rgb.dds")
    for path in sorted(output.glob("*.dds")):
        with Image.open(path) as image:
            pixels = image.convert("RGBA").tobytes("raw", "BGRA")
            Path(str(path) + ".bgra").write_bytes(pixels)
            print(f"{path.name}: {image.size}, {len(pixels)} reference BGRA bytes")


if __name__ == "__main__":
    main()
