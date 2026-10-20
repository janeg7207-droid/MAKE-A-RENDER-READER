"""Embed reviewed geometry helpers in the existing render entry point.

Older installed updaters accept exactly eleven files. Keeping the entry point
self-contained lets them install this fix without changing that contract.
Canonical, readable helper sources live in src/renderer.
"""

import argparse
import base64
import hashlib
from pathlib import Path
import zlib


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    wrapper = repo / "runtime/engine/gr_render.py"
    begin = "# BEGIN GENERATED GEOMETRY BUNDLE"
    end = "# END GENERATED GEOMETRY BUNDLE"
    content = wrapper.read_text(encoding="utf-8")
    before, tail = content.split(begin, 1)
    old, after = tail.split(end, 1)
    lines = [begin, "_GEOMETRY_BUNDLE = {"]
    for name in ("gorilla_geometry", "gorilla_geometry_cache"):
        source = (repo / "src/renderer" / (name + ".py")).read_bytes()
        digest = hashlib.sha256(source).hexdigest()
        encoded = base64.b64encode(zlib.compress(source, 9)).decode("ascii")
        lines.append("    " + repr(name) + ": (" + repr(digest) + ",")
        for index in range(0, len(encoded), 100):
            lines.append("        " + repr(encoded[index:index + 100]))
        lines.append("    ),")
    lines.extend(["}", end])
    expected = before + "\n".join(lines) + after
    if args.check:
        if expected != content:
            raise SystemExit("The render helper bundle is stale. Run tools/build_renderer_bundle.py.")
        print("Geometry bundle matches both canonical helper sources.")
    else:
        wrapper.write_text(expected, encoding="utf-8")
        print("Updated the self-contained render helper bundle.")


if __name__ == "__main__":
    main()
