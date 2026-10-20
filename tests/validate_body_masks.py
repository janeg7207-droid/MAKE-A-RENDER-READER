"""Portable malformed-source and conservative body-mask selection checks."""
import importlib.util
import json
from pathlib import Path
import struct
import types


def main():
    module_path = Path(__file__).resolve().parents[1] / "src/renderer/gorilla_body_masks.py"
    spec = importlib.util.spec_from_file_location("gorilla_body_masks_test", module_path)
    masks = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(masks)
    checks = 0
    def check(condition, message):
        nonlocal checks
        if not condition:
            raise AssertionError(message)
        checks += 1
    def u32(n):
        return struct.pack("<I", n)
    def mesh(name, ids):
        encoded = name.encode("utf8")
        return b"MESH" + u32(len(encoded)) + encoded + b"\0" * (-len(encoded) % 4) \
            + u32(len(ids)) + b"".join(u32(value) for value in ids)
    def hpl(first=None, second=None, extra=b""):
        first = [("M_Body", [10, 20, 30, 40])] if first is None else first
        second = [("M_Body", [20, 30, 40, 50])] if second is None else second
        return b"HPL!LOD!" + u32(0) + b"DATA" + u32(0) + b"".join(mesh(*value) for value in first) \
            + b"DATA" + u32(1) + b"".join(mesh(*value) for value in second) + extra + b"END!" + u32(0)
    def model(raw, record=None):
        return types.SimpleNamespace(b=raw, rec={"HPL!": (0, len(raw))} if record is None else record)
    def rejected(raw, message, targets=None, record=None):
        try:
            masks.common_masks(model(raw, record), targets)
        except (ValueError, UnicodeDecodeError):
            check(True, message)
        else:
            raise AssertionError(message)
    check(masks.common_masks(model(hpl())) == {"M_Body": frozenset((20, 30, 40))}, "Only common mode IDs are returned")
    check(masks.common_masks(model(hpl([], [("M_Head", [1])]))) == {}, "One-mode-only meshes are left intact")
    check(masks.common_masks(model(hpl([], []))) == {}, "Empty valid masks remain empty")
    check(masks.common_masks(model(b"", {})) == {}, "Absent HPL leaves geometry unchanged")
    check(masks.masked_faces((10, 20, 30, 40), ((0, 1, 2), (1, 2, 3)), frozenset((20, 30, 40)))
          == (False, True), "Every corner must be in the common mask")
    check(masks.masked_faces((4, 4, 9, 3), ((0, 1, 2), (0, 2, 3)), frozenset((4, 9)))
          == (True, False), "Source REMAP handles duplicated positions without vertex-index guesses")
    rejected(hpl([( "M_Body", [1, 1])]), "Duplicate IDs reject the whole HPL")
    rejected(hpl([( "M_Body", [1]), ("M_Body", [2])]), "Duplicate mesh names reject the whole HPL")
    rejected(hpl(extra=b"DATA" + u32(1)), "Duplicate DATA groups reject the whole HPL")
    rejected(hpl(extra=b"DATA" + u32(2)), "Unknown DATA modes reject the whole HPL")
    rejected(hpl(extra=b"LOD!" + u32(0)), "Duplicate LOD groups reject the whole HPL")
    rejected(hpl().replace(b"DATA" + u32(1), b"DATA" + u32(5)), "Unsupported mode is never mapped by guess")
    rejected(hpl()[:-4], "Truncated terminator is rejected")
    rejected(hpl() + b"tail", "Trailing bytes are rejected")
    rejected(hpl(), "Out of bounds record is rejected", record={"HPL!": (1, len(hpl()))})
    rejected(hpl(), "Negative record is rejected", record={"HPL!": (-1, len(hpl()))})
    rejected(hpl(), "Stale IDs in either mode are rejected", targets={"M_Body": (20, 30, 40, 50)})
    rejected(hpl(), "Unmatched mask mesh names are rejected", targets={"Other": range(100)})
    check(masks.common_masks(model(hpl()), {"M_Body": (10, 20, 30, 40, 50)})["M_Body"] == frozenset((20,30,40)),
          "Complete paired target numbering validates both modes")
    for remap, faces, ids in (((1,2,3), ((0,1,2),), frozenset((4,))),
                              ((1,2,3), ((0,1,3),), frozenset((1,2,3))),
                              ((1,2,3), ((0,1),), frozenset((1,2,3)))):
        try:
            masks.masked_faces(remap, faces, ids)
        except ValueError:
            check(True, "Unsupported face/target is rejected")
        else:
            raise AssertionError("Invalid face/target was accepted")
    print(json.dumps({"checks_passed": checks, "native_blender_required": False}))


if __name__ == "__main__":
    main()
