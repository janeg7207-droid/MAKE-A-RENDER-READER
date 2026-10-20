"""Exercise the renderer's exact MCD provenance and gear-visibility hook.

The Blender and original importer's boundary is represented by small fixtures.
These checks exercise the production hook without rendering or claiming game
playback. The real native Blender import is validated separately.
"""

import argparse
import base64
import copy
from contextlib import contextmanager
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import types


class Object(dict):
    def __init__(self, name, kind="MESH", vertices=3, data=None):
        super().__init__()
        self.name, self.type, self.hide_render = name, kind, False
        self.hide_viewport = False
        self.data = data if data is not None else types.SimpleNamespace(
            vertices=list(range(vertices)), untouched="original mesh")

    def __bool__(self):
        return True


def mesh(name="M_Body", flags=128, vertices=3, numbering=None):
    return {"name": name, "flags": flags, "verts": vertices,
            "numbering": list(range(vertices)) if numbering is None else numbering}


class Fixture:
    def __init__(self, root, definitions=None, path=None):
        self.root = root
        self.path = path or root / "Character" / "Default_Attire" / "Attire.mcd"
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.bytes = b"MCD! exact test source bytes"
        self.path.write_bytes(self.bytes)
        self.definitions = definitions or [mesh()]
        self.model = types.SimpleNamespace(path=str(self.path), b=self.bytes,
                                           version=47, meshes=self.definitions)
        self.objects = [Object("Skeleton", "ARMATURE")]
        self.objects.extend(Object("Same material name", vertices=item["verts"])
                            for item in self.definitions)
        self.calls = []
        self.model_globals_unchanged = []
        self.failure = None
        self.seen = []
        self.events = []
        self.bpy = types.ModuleType("bpy")
        self.bpy.app = types.ModuleType("bpy.app")
        self.bpy.app.handlers = types.ModuleType("bpy.app.handlers")
        self.bpy.app.handlers.render_pre = []
        self.bpy.app.handlers.persistent = lambda function: function
        self.scene = types.SimpleNamespace(objects=self.objects)
        self.bpy.context = types.SimpleNamespace(scene=self.scene)
        self.bpy.data = types.SimpleNamespace(objects=self.objects)

        def model(path, *args, **kwargs):
            self.calls.append((path, args, kwargs))
            self.model_globals_unchanged.append(self.gm.Model is self.original_model)
            return self.model

        self.gm = types.SimpleNamespace(Model=model)
        self.original_model = model
        self.native = types.ModuleType("gorilla_blender")
        self.native.GM = self.gm
        self.native.CONTROL = self
        exec("""
def import_mcd(path, shape_keys=False, textures=True, editable=False):
    model = GM.Model(path, shapes=shape_keys)
    CONTROL.seen.append(model)
    CONTROL.events.append((shape_keys, textures, editable))
    if CONTROL.failure is not None:
        raise CONTROL.failure
    return CONTROL.objects
""", self.native.__dict__)
        self.original_import = self.native.import_mcd

    def options(self, **updates):
        result = {"char_dir": str(self.path.parent.parent),
                  "attire_mcd": "Default_Attire/Attire.mcd", "gorilla_gear": "ring"}
        result.update(updates)
        return result


@contextmanager
def modules(fixture):
    replacements = {"bpy": fixture.bpy, "bpy.app": fixture.bpy.app,
                    "bpy.app.handlers": fixture.bpy.app.handlers, "gorilla_blender": fixture.native,
                    "gs_paths": types.SimpleNamespace(STOCK_ROOT=str(fixture.root / "stock"))}
    previous = {key: sys.modules.get(key) for key in replacements}
    sys.modules.update(replacements)
    try:
        yield
    finally:
        for key, value in previous.items():
            if value is None:
                sys.modules.pop(key, None)
            else:
                sys.modules[key] = value


def load_module(path):
    spec = importlib.util.spec_from_file_location("gorilla_geometry_under_test", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


@contextmanager
def environment(name, value):
    original = os.environ.get(name)
    if value is None:
        os.environ.pop(name, None)
    else:
        os.environ[name] = value
    try:
        yield
    finally:
        if original is None:
            os.environ.pop(name, None)
        else:
            os.environ[name] = original


@contextmanager
def working_directory(path):
    original = Path.cwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(original)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--module", type=Path,
                        default=Path(__file__).resolve().parents[1] / "src/renderer/gorilla_geometry.py")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    geometry = load_module(args.module)
    checks = []

    def check(condition, message):
        assert condition, message
        checks.append(message)

    with tempfile.TemporaryDirectory(prefix="gorilla-geometry-import-") as temporary:
        root = Path(temporary)
        fixture = Fixture(root / "basic", [mesh("Source mesh A", 129), mesh("Source mesh B", 130)])
        records = []
        with modules(fixture):
            options = fixture.options()
            original_options = dict(options)
            state = geometry.register(options, on_import=records.append)
            try:
                returned = fixture.native.import_mcd(fixture.path, shape_keys=True, textures=False, editable=True)
                check(returned is fixture.objects, "The importer retains the exact original result list")
                check(len(fixture.calls) == 1, "Provenance uses the single model parse used by the actual importer")
                check(fixture.calls[0] == (fixture.path, (), {"shapes": True}),
                      "The original Model receives its unchanged path and shape-key argument")
                check(fixture.events == [(True, False, True)],
                      "All public import flags reach the original function unchanged")
                check(fixture.model_globals_unchanged == [True] and fixture.gm.Model is fixture.original_model,
                      "Capturing a model never mutates the shared native GM.Model callable")
                check(len(fixture.seen) == 1 and fixture.seen[0] is fixture.model,
                      "The original import uses the exact captured Model instance")
                check(options == original_options, "Registration does not rewrite caller options")
                first, second = (geometry.read_provenance(obj) for obj in fixture.objects[1:])
                check(first is not None and second is not None, "Every exactly matched source mesh gains validated provenance")
                check((first["index"], second["index"]) == (0, 1),
                      "Identically named imported objects keep different exact source indices")
                check((first["name"], second["name"]) == ("Source mesh A", "Source mesh B"),
                      "Provenance uses source mesh names rather than duplicate material/object names")
                check((first["flags"], second["flags"]) == (129, 130), "Each source mesh retains its exact gear flag")
                check(first["path"] == str(fixture.path.resolve()), "Provenance retains the canonical imported source path")
                check(first["sha256"] == hashlib.sha256(fixture.bytes).hexdigest(),
                      "Source identity hashes the actual imported byte buffer")
                check(first["role"] == "attire" and first["vertices"] == 3,
                      "The exact active outfit path identifies an attire and its vertex count")
                check(geometry.read_provenance(fixture.objects[0]) is None,
                      "An armature is never presented as a body or clothing mesh")
                check(len(records) == 1 and records[0].model is fixture.model,
                      "The callback receives the original parsed Model once")
                check(len(records[0].objects) == len(fixture.objects) - 1
                      and all(actual is expected for actual, expected in zip(records[0].objects, fixture.objects[1:])),
                      "The callback keeps the exact source-to-object ordering without the armature")
                check(records[0].path == first["path"] and records[0].sha256 == first["sha256"]
                      and records[0].role == "attire", "The callback and saved provenance share the same source identity")
            finally:
                state.unregister()
            check(fixture.native.import_mcd is fixture.original_import,
                  "Unregister restores the original importer rather than a cloned replacement")
            check(fixture.bpy.app.handlers.render_pre == [], "Unregister removes its render callback")

        fixture = Fixture(root / "uint32", [mesh(numbering=[0, 0x80000000, 0xFFFFFFFF])])
        with modules(fixture):
            state = geometry.register(fixture.options())
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.decode_remap(fixture.objects[1]) == (0, 0x80000000, 0xFFFFFFFF),
                      "Vertex REMAP storage preserves the entire unsigned 32-bit range")
                check(geometry.read_provenance(fixture.objects[1]) is not None,
                      "Large unsigned REMAP values remain validated without signed overflow")
                original = fixture.objects[1]["gorilla_geometry_remap_u32"]
                fixture.objects[1]["gorilla_geometry_remap_u32"] = "not-base64!"
                check(geometry.decode_remap(fixture.objects[1]) is None,
                      "A damaged cached REMAP is rejected instead of decoded as plausible vertex IDs")
                fixture.objects[1]["gorilla_geometry_remap_u32"] = base64.b64encode(struct.pack("<I", 1)).decode("ascii")
                check(geometry.decode_remap(fixture.objects[1]) is None,
                      "A truncated cached REMAP cannot match the recorded vertex count")
                fixture.objects[1]["gorilla_geometry_remap_u32"] = original
                check(geometry.decode_remap(fixture.objects[1]) == (0, 0x80000000, 0xFFFFFFFF),
                      "Valid cached REMAP data remains readable after a rejected damaged copy")
            finally:
                state.unregister()

        invalid_cases = {
            "missing object": lambda f: f.objects.pop(),
            "extra object": lambda f: f.objects.append(Object("Unexpected", vertices=3)),
            "wrong armature": lambda f: setattr(f.objects[0], "type", "MESH"),
            "nonmesh object": lambda f: setattr(f.objects[1], "type", "CURVE"),
            "wrong vertex count": lambda f: f.objects[2].data.vertices.append(3),
            "short numbering": lambda f: f.definitions[1].__setitem__("numbering", [0]),
            "negative numbering": lambda f: f.definitions[1].__setitem__("numbering", [0, 1, -1]),
            "overflow numbering": lambda f: f.definitions[1].__setitem__("numbering", [0, 1, 1 << 32]),
            "boolean numbering": lambda f: f.definitions[1].__setitem__("numbering", [0, 1, True]),
            "nonnumeric numbering": lambda f: f.definitions[1].__setitem__("numbering", [0, 1, "unknown"]),
            "different parsed source": lambda f: setattr(f.model, "path", str(f.path.parent / "Other.mcd")),
            "invalid raw bytes": lambda f: setattr(f.model, "b", "not imported bytes"),
            "nonnumeric flag": lambda f: f.definitions[1].__setitem__("flags", "unknown"),
            "duplicate returned object": lambda f: f.objects.__setitem__(2, f.objects[1]),
        }
        for label, damage in invalid_cases.items():
            fixture = Fixture(root / ("invalid-" + label.replace(" ", "-")), [mesh("First"), mesh("Second")])
            damage(fixture)
            records = []
            with modules(fixture):
                state = geometry.register(fixture.options(), on_import=records.append)
                try:
                    returned = fixture.native.import_mcd(fixture.path)
                    check(returned is fixture.objects, "Invalid metadata preserves native return: " + label)
                    check(all(geometry.read_provenance(obj) is None for obj in fixture.objects),
                          "No partial or guessed provenance survives: " + label)
                    check(records == [], "No body-mask callback accepts an unproved import: " + label)
                finally:
                    state.unregister()

        fixture = Fixture(root / "roles")
        with modules(fixture):
            state = geometry.register(fixture.options(attire_mcd="Another/Attire.mcd"))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "other",
                      "A nearby MCD is not identified as the active attire by its filename")
                state.apply_visibility(fixture.scene)
                check(not fixture.objects[1].hide_render,
                      "Meshes outside the exact active source cannot have their gear visibility changed")
            finally:
                state.unregister()
        fixture = Fixture(root / "base", path=root / "base" / "Character" / "BaseModel" / "baseModel.mcd")
        with modules(fixture):
            state = geometry.register(fixture.options())
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "base",
                      "The exact character BaseModel source is identified independently of its mesh names")
            finally:
                state.unregister()

        fixture = Fixture(root / "same-role", path=root / "same-role" / "Character" / "BaseModel" / "baseModel.mcd")
        with modules(fixture):
            state = geometry.register(fixture.options(attire_mcd="BaseModel/baseModel.mcd"))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "other",
                      "One file cannot be guessed into both the active body and clothing roles")
            finally:
                state.unregister()

        fixture_root = root / "stock-fallback"
        fixture = Fixture(fixture_root, path=fixture_root / "stock" / "Characters" / "Character" / "BaseModel" / "baseModel.mcd")
        local_character = fixture_root / "extracted" / "Character"
        local_character.mkdir(parents=True)
        with modules(fixture), environment("GORILLA_STOCK", None):
            state = geometry.register(fixture.options(char_dir=str(local_character)))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "base",
                      "The original renderer's exact stock BaseModel fallback retains a proven base role")
            finally:
                state.unregister()

        fixture_root = root / "stock-override"
        override = fixture_root / "CustomStock"
        selected_base = override / "Characters" / "Character" / "BaseModel" / "baseModel.mcd"
        default_base = fixture_root / "stock" / "Characters" / "Character" / "BaseModel" / "baseModel.mcd"
        default_base.parent.mkdir(parents=True)
        default_base.write_bytes(b"another installed stock source")
        local_character = fixture_root / "extracted" / "Character"
        local_character.mkdir(parents=True)
        fixture = Fixture(fixture_root, path=selected_base)
        with modules(fixture), environment("GORILLA_STOCK", str(override)):
            state = geometry.register(fixture.options(char_dir=str(local_character)))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "base",
                      "GORILLA_STOCK selects the same exact fallback body as the native renderer")
            finally:
                state.unregister()
        fixture = Fixture(fixture_root, path=default_base)
        with modules(fixture), environment("GORILLA_STOCK", str(override)):
            state = geometry.register(fixture.options(char_dir=str(local_character)))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "other",
                      "An overridden gs_paths stock body cannot be mistaken for the current fallback source")
            finally:
                state.unregister()
        fixture = Fixture(fixture_root, path=override / "Characters" / "Character" / "Default_Attire" / "Attire.mcd")
        options = fixture.options(char="Character")
        del options["char_dir"]
        with modules(fixture), environment("GORILLA_STOCK", str(override)):
            state = geometry.register(options)
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "attire",
                      "GORILLA_STOCK also resolves a stock character's active attire when char_dir is absent")
            finally:
                state.unregister()

        fixture_root = root / "empty-stock-override"
        fixture = Fixture(fixture_root, path=fixture_root / "Characters" / "Character" / "BaseModel" / "baseModel.mcd")
        local_character = fixture_root / "extracted" / "Character"
        local_character.mkdir(parents=True)
        with modules(fixture), environment("GORILLA_STOCK", ""), working_directory(fixture_root):
            state = geometry.register(fixture.options(char_dir=str(local_character)))
            try:
                fixture.native.import_mcd(fixture.path)
                check(geometry.read_provenance(fixture.objects[1])["role"] == "base",
                      "An explicitly empty GORILLA_STOCK retains the native relative-root override semantics")
            finally:
                state.unregister()

        fixture = Fixture(root / "visibility", [mesh("Both", 128), mesh("Entrance", 129), mesh("Ring", 130), mesh("Unknown", 131)])
        records = []
        with modules(fixture):
            state = geometry.register(fixture.options(), on_import=records.append)
            try:
                fixture.native.import_mcd(fixture.path)
                fixture.objects[1].hide_render = True
                fixture.objects[4].hide_render = True
                old_data = [obj.data for obj in fixture.objects]
                old_data_fields = [copy.deepcopy(vars(obj.data)) for obj in fixture.objects]
                legacy = Object("Same material name", vertices=3)
                fixture.scene.objects.append(legacy)
                state.apply_visibility(fixture.scene)
                check([obj.hide_render for obj in fixture.objects[1:5]] == [True, True, False, True],
                      "Ring gear hides entrance-only pieces and preserves manually hidden common/unknown pieces")
                check(not legacy.hide_render, "Legacy cache meshes without provenance are left unchanged")
                check(all(obj.data is original for obj, original in zip(fixture.objects[:5], old_data)),
                      "Gear visibility never replaces or modifies mesh datablocks")
                check([dict(vars(obj.data)) for obj in fixture.objects[:5]] == old_data_fields,
                      "Gear visibility keeps vertex data and unrelated mesh fields intact")
                state.apply_visibility(fixture.scene)
                check(fixture.objects[2].hide_render and not fixture.objects[3].hide_render,
                      "Repeated render callbacks do not alternate gear visibility")
                state.options["gorilla_gear"] = "entrance"
                stats = state.apply_visibility(fixture.scene)
                check(not fixture.objects[2].hide_render and fixture.objects[3].hide_render,
                      "Switching to entrance restores only the previously managed entrance layer")
                check(stats["restored"] == 1 and stats["hidden"] == 1,
                      "Visibility reports the current gear switch without touching common or unknown pieces")
                state.options["gorilla_gear"] = "ring"
                state.apply_visibility(fixture.scene)
                state.options["gorilla_gear"] = "entrance"
                fixture.objects[2].name = "M_Explicit:skinned"
                state.options["hide"] = "M_Explicit"
                state.apply_visibility(fixture.scene)
                check(fixture.objects[2].hide_render,
                      "A current explicit hide choice takes priority over restoring an old managed gear layer")
                state.options["hide"] = ""
                state.apply_visibility(fixture.scene)
                check(fixture.objects[2].hide_render,
                      "An explicitly retained hidden layer is not later mistaken for repair-owned visibility")
            finally:
                state.unregister()

        fixture = Fixture(root / "entrance", [mesh("Both", 128), mesh("Entrance", 129), mesh("Ring", 130), mesh("Unknown", 131)])
        with modules(fixture):
            state = geometry.register(fixture.options(gorilla_gear="entrance"))
            try:
                fixture.native.import_mcd(fixture.path)
                state.apply_visibility(fixture.scene)
                check([obj.hide_render for obj in fixture.objects[1:]] == [False, False, True, False],
                      "Entrance gear hides ring-only pieces using their exact recorded flags")
                fixture.objects[2].data.vertices.append(3)
                fixture.objects[2].hide_render = False
                check(geometry.read_provenance(fixture.objects[2]) is None,
                      "A vertex-count change invalidates its cached provenance")
                state.apply_visibility(fixture.scene)
                check(not fixture.objects[2].hide_render,
                      "Changed vertex counts cannot be hidden using stale source metadata")
                fixture.objects[1]["gorilla_geometry_revision"] = 999
                check(geometry.read_provenance(fixture.objects[1]) is None,
                      "A cached mesh from an unknown provenance revision is rejected")
            finally:
                state.unregister()

        class Unwritable(Object):
            def __setitem__(self, key, value):
                if key == "gorilla_geometry_flags":
                    raise RuntimeError("fixture refuses one Blender custom property")
                super().__setitem__(key, value)

        fixture = Fixture(root / "property-rollback", [mesh("First"), mesh("Second")])
        fixture.objects[1]["gorilla_geometry_revision"] = -1
        fixture.objects[1]["unrelated"] = "retained"
        fixture.objects[2] = Unwritable("Other", vertices=3)
        before = [dict(obj) for obj in fixture.objects]
        records = []
        with modules(fixture):
            state = geometry.register(fixture.options(), on_import=records.append)
            try:
                check(fixture.native.import_mcd(fixture.path) is fixture.objects,
                      "An unwritable custom property preserves the completed native import")
                check([dict(obj) for obj in fixture.objects] == before,
                      "A failed property transaction restores earlier meshes and all original custom properties")
                check(records == [], "A partially written metadata transaction cannot reach body-mask processing")
            finally:
                state.unregister()

        fixture = Fixture(root / "no-mode", [mesh("Entrance", 129), mesh("Ring", 130)])
        previous_mode = os.environ.pop("GORILLA_GEAR_CHOICE", None)
        try:
            with modules(fixture):
                state = geometry.register(fixture.options(gorilla_gear="unknown"))
                try:
                    fixture.native.import_mcd(fixture.path)
                    state.apply_visibility(fixture.scene)
                    check(not any(obj.hide_render for obj in fixture.objects),
                          "Missing or unknown gear mode never guesses which outfit pieces to hide")
                finally:
                    state.unregister()
        finally:
            if previous_mode is not None:
                os.environ["GORILLA_GEAR_CHOICE"] = previous_mode

        fixture = Fixture(root / "shared-data", [mesh("First", 129), mesh("Second", 128)])
        fixture.objects[2].data = fixture.objects[1].data
        with modules(fixture):
            state = geometry.register(fixture.options())
            try:
                fixture.native.import_mcd(fixture.path)
                shared = fixture.objects[1].data
                before = copy.deepcopy(vars(shared))
                state.apply_visibility(fixture.scene)
                check(fixture.objects[1].hide_render and not fixture.objects[2].hide_render,
                      "Objects sharing one mesh can retain different exact gear visibility")
                check(fixture.objects[1].data is shared and fixture.objects[2].data is shared
                      and vars(shared) == before,
                      "Object-local provenance and visibility preserve shared mesh data")
            finally:
                state.unregister()

        class DeferredLargeRemap(Object):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, **kwargs)
                self.block_remap = False
                self.remap_reads = 0

            def _guard(self, key):
                if self.block_remap and key == "gorilla_geometry_remap_u32":
                    self.remap_reads += 1
                    raise AssertionError("Per-frame gear visibility fetched the large REMAP string")

            def get(self, key, default=None):
                self._guard(key)
                return super().get(key, default)

            def __getitem__(self, key):
                self._guard(key)
                return super().__getitem__(key)

        fixture = Fixture(root / "deferred-remap", [mesh("Entrance only", 129)])
        fixture.objects[1] = DeferredLargeRemap("Entrance gear", vertices=3)
        with modules(fixture):
            state = geometry.register(fixture.options())
            try:
                fixture.native.import_mcd(fixture.path)
                fixture.objects[1].block_remap = True
                state.apply_visibility(fixture.scene)
                check(fixture.objects[1].hide_render and fixture.objects[1].remap_reads == 0,
                      "Per-frame gear visibility uses proven scalar identity without fetching the large REMAP property")
            finally:
                state.unregister()

        fixture = Fixture(root / "failures")
        failure = ValueError("original importer rejected this model")
        fixture.failure = failure
        records = []
        with modules(fixture):
            state = geometry.register(fixture.options(), on_import=records.append)
            try:
                try:
                    fixture.native.import_mcd(fixture.path)
                except ValueError as error:
                    check(error is failure, "The exact original importer exception escapes unchanged")
                else:
                    raise AssertionError("The original importer error was swallowed")
                check(records == [] and all(geometry.read_provenance(obj) is None for obj in fixture.objects),
                      "Failed imports do not tag objects or call body-mask processing")
                check(fixture.gm.Model is fixture.original_model,
                      "A failed import cannot leave the native reader globally monkeypatched")
            finally:
                state.unregister()
        fixture = Fixture(root / "callback-failure")

        def callback_failure(record):
            raise RuntimeError("optional body-mask failure")

        with modules(fixture):
            state = geometry.register(fixture.options(), on_import=callback_failure)
            try:
                check(fixture.native.import_mcd(fixture.path) is fixture.objects,
                      "An optional repair error cannot trigger a second native fallback import")
            finally:
                state.unregister()

        fixture = Fixture(root / "ambiguous-reader")
        exec("""
def import_mcd(path, shape_keys=False, textures=True, editable=False):
    first = GM.Model(path, shapes=shape_keys)
    second = GM.Model(path, shapes=shape_keys)
    CONTROL.seen.extend([first, second])
    return CONTROL.objects
""", fixture.native.__dict__)
        fixture.original_import = fixture.native.import_mcd
        records = []
        with modules(fixture):
            state = geometry.register(fixture.options(), on_import=records.append)
            try:
                check(fixture.native.import_mcd(fixture.path) is fixture.objects and len(fixture.calls) == 2,
                      "A changed native importer retains its original multiple-reader behavior")
                check(records == [] and all(geometry.read_provenance(obj) is None for obj in fixture.objects),
                      "Multiple captured models cannot be guessed into one mesh-provenance mapping")
            finally:
                state.unregister()

        fixture = Fixture(root / "registration")
        first_records, second_records = [], []
        with modules(fixture):
            first = geometry.register(fixture.options(), on_import=first_records.append)
            second = geometry.register(fixture.options(), on_import=second_records.append)
            try:
                check(len(fixture.bpy.app.handlers.render_pre) == 1,
                      "Registering again keeps one persistent render handler")
                check(fixture.native.import_mcd(fixture.path) is fixture.objects,
                      "A repeated registration keeps the original importer contract")
                check(len(fixture.calls) == 1 and first_records == [] and len(second_records) == 1,
                      "Repeated registration never nests model parsing or stale callbacks")
                first.unregister()
                check(fixture.native.import_mcd is not fixture.original_import,
                      "Unregistering an obsolete owner cannot remove a newer hook")
            finally:
                second.unregister()
            check(fixture.native.import_mcd is fixture.original_import and fixture.bpy.app.handlers.render_pre == [],
                  "The current owner can fully remove its importer and handler")
            state = geometry.register(fixture.options())
            foreign = lambda *args, **kwargs: "another importer"
            fixture.native.import_mcd = foreign
            state.unregister()
            check(fixture.native.import_mcd is foreign,
                  "Unregister never overwrites a later third-party importer")

    result = {"checks_passed": len(checks), "checks": checks,
              "test_boundary": "Mock Blender/native importer; no images rendered",
              "windows_or_gameplay_executed": False}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
