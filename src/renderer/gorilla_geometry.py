"""Retain exact MCD mesh provenance and hide verified inactive gear pieces.

Only the native importer's returned source order establishes identity.  Material
names, vertex-count searches and the order of unrelated scene objects do not.
Geometry, skin weights, shape keys and materials are never modified here.
"""

import array
import base64
import functools
import hashlib
import importlib
import operator
import os
import sys
import types
from collections import namedtuple

REVISION = 1
MAXIMUM_VERTICES = 2_000_000
_FIELDS = {
    "revision": "gorilla_geometry_revision",
    "path": "gorilla_geometry_source",
    "sha256": "gorilla_geometry_source_sha256",
    "index": "gorilla_geometry_mesh",
    "name": "gorilla_geometry_mesh_name",
    "flags": "gorilla_geometry_flags",
    "vertices": "gorilla_geometry_vertices",
    "remap": "gorilla_geometry_remap_u32",
    "role": "gorilla_geometry_role",
}
_MANAGED_HIDE = "gorilla_geometry_gear_hidden"
ImportRecord = namedtuple("ImportRecord", "path sha256 role model objects")
_active_registration = None


def _canonical(path):
    return os.path.normcase(os.path.realpath(os.path.abspath(os.fspath(path))))


def _integer(value):
    if isinstance(value, bool):
        raise ValueError("Boolean values are not mesh indices.")
    return operator.index(value)


def _pack_remap(values, count):
    if len(values) != count:
        raise ValueError("The source REMAP count does not match its vertices.")
    packed = array.array("I")
    if packed.itemsize != 4:
        raise ValueError("This platform does not provide 32-bit unsigned arrays.")
    for value in values:
        value = _integer(value)
        if not 0 <= value <= 0xFFFFFFFF:
            raise ValueError("A source REMAP value is outside uint32.")
        packed.append(value)
    if sys.byteorder != "little":
        packed.byteswap()
    return base64.b64encode(packed.tobytes()).decode("ascii")


def _read_identity(obj, inspect_remap):
    try:
        if obj.type != "MESH" or obj.get(_FIELDS["revision"]) != REVISION:
            return None
        # Blender copies a string custom property into Python on access.  A
        # render-frame visibility pass needs no vertex-sized REMAP allocation.
        value = {key: obj.get(prop) for key, prop in _FIELDS.items() if key != "remap"}
        if _integer(value["revision"]) != REVISION:
            return None
        for key in ("index", "flags", "vertices"):
            value[key] = _integer(value[key])
        if value["index"] < 0 or not 0 <= value["flags"] <= 0xFFFFFFFF:
            return None
        if not 0 <= value["vertices"] <= MAXIMUM_VERTICES:
            return None
        if len(obj.data.vertices) != value["vertices"]:
            return None
        if not isinstance(value["path"], str) or not value["path"]:
            return None
        if not isinstance(value["sha256"], str) or len(value["sha256"]) != 64 \
                or any(char not in "0123456789abcdef" for char in value["sha256"]):
            return None
        if not isinstance(value["name"], str) or value["role"] not in ("base", "attire", "other"):
            return None
        if _FIELDS["remap"] not in obj:
            return None
        if inspect_remap:
            remap = obj.get(_FIELDS["remap"])
            if not isinstance(remap, str) or len(remap) != 4 * ((4 * value["vertices"] + 2) // 3):
                return None
        return value
    except (AttributeError, KeyError, TypeError, ValueError, OverflowError, ReferenceError):
        return None


def read_provenance(obj):
    """Return validated source identity, or None for legacy/changed meshes."""
    return _read_identity(obj, True)


def validate_object(obj):
    return read_provenance(obj) is not None


def decode_remap(obj):
    """Decode the exact source uint32 REMAP, without changing a mesh datablock."""
    value = read_provenance(obj)
    if value is None:
        return None
    try:
        raw = base64.b64decode(obj[_FIELDS["remap"]], validate=True)
        if len(raw) != value["vertices"] * 4:
            return None
        packed = array.array("I")
        if packed.itemsize != 4:
            return None
        packed.frombytes(raw)
        if sys.byteorder != "little":
            packed.byteswap()
        return tuple(packed)
    except (TypeError, ValueError, OverflowError, ReferenceError):
        return None


def _source_paths(options):
    try:
        stock = os.environ.get("GORILLA_STOCK", importlib.import_module("gs_paths").STOCK_ROOT)
        character = options.get("char_dir")
        if character is None:
            character = os.path.join(os.fspath(stock), "Characters", options.get("char", "161_Randy_Orton_2019"))
        base = os.path.join(os.fspath(character), "BaseModel", "baseModel.mcd")
        if not os.path.isfile(base):
            base = os.path.join(os.fspath(stock), "Characters", os.path.basename(os.fspath(character)), "BaseModel", "baseModel.mcd")
        attire = os.path.join(os.fspath(character), options.get("attire_mcd", "161_Default_Attire/161_Attire.mcd"))
        return _canonical(base), _canonical(attire)
    except (AttributeError, TypeError, ValueError, OSError, ImportError):
        return None, None


class _ReaderProxy:
    def __init__(self, original, captured):
        self._original, self._captured = original, captured

    def __getattr__(self, name):
        if name != "Model":
            return getattr(self._original, name)
        def capture(*args, **kwargs):
            model = self._original.Model(*args, **kwargs)
            self._captured.append(model)
            return model
        return capture


class GeometryRegistration:
    def __init__(self, options, on_import, bpy):
        self.options = dict(options or {})
        self.on_import = on_import
        self.bpy = bpy
        self.module = self.original = self.wrapper = self.handler = None
        self.installed = False
        self.closed = False
        self._warned = set()
        self.base_path, self.attire_path = _source_paths(self.options)

    def _warn(self, message):
        if message not in self._warned:
            self._warned.add(message)
            print("  geometry visibility: " + message, flush=True)

    def _role(self, path):
        if self.base_path == self.attire_path:
            return "other"
        if path == self.base_path:
            return "base"
        if path == self.attire_path:
            return "attire"
        return "other"

    def _stamp(self, path, model, result):
        source = _canonical(path)
        if _canonical(model.path) != source:
            raise ValueError("The captured model belongs to a different source.")
        meshes = tuple(model.meshes)
        if not isinstance(result, (list, tuple)) or len(result) != len(meshes) + 1 \
                or getattr(result[0], "type", None) != "ARMATURE":
            raise ValueError("The native importer did not return its complete source mesh sequence.")
        objects = tuple(result[1:])
        if len({id(obj) for obj in objects}) != len(objects):
            raise ValueError("The native importer returned a mesh object more than once.")
        if not isinstance(model.b, (bytes, bytearray, memoryview)):
            raise ValueError("The captured model has no exact source bytes.")
        digest = hashlib.sha256(model.b).hexdigest()
        role = self._role(source)
        descriptors = []
        for index, (mesh, obj) in enumerate(zip(meshes, objects)):
            count, flags = _integer(mesh["verts"]), _integer(mesh["flags"])
            if not 0 <= count <= MAXIMUM_VERTICES or not 0 <= flags <= 0xFFFFFFFF:
                raise ValueError("A source mesh count or flag is unsupported.")
            if getattr(obj, "type", None) != "MESH" or len(obj.data.vertices) != count:
                raise ValueError("The returned mesh does not match its source vertex count.")
            if not isinstance(mesh["name"], str):
                raise ValueError("The source mesh has no exact name.")
            descriptors.append({"revision": REVISION, "path": source, "sha256": digest,
                                "index": index, "name": mesh["name"], "flags": flags,
                                "vertices": count, "remap": _pack_remap(mesh["numbering"], count), "role": role})
        changed = []
        try:
            for obj, descriptor in zip(objects, descriptors):
                for key, value in descriptor.items():
                    prop = _FIELDS[key]
                    present = prop in obj
                    previous = obj[prop] if present else None
                    obj[prop] = value
                    changed.append((obj, prop, present, previous))
        except Exception:
            for obj, prop, present, previous in reversed(changed):
                try:
                    if present:
                        obj[prop] = previous
                    else:
                        del obj[prop]
                except (AttributeError, TypeError, ValueError, RuntimeError, ReferenceError):
                    pass
            raise
        return ImportRecord(source, digest, role, model, objects)

    def _import(self, *args, **kwargs):
        # Use the unmodified function's current globals, replacing only its
        # private view of GM. Shared GM.Model and concurrent callers stay intact.
        captured = []
        globals_ = dict(self.original.__globals__)
        globals_["GM"] = _ReaderProxy(globals_["GM"], captured)
        importer = types.FunctionType(self.original.__code__, globals_, self.original.__name__,
                                      self.original.__defaults__, self.original.__closure__)
        importer.__kwdefaults__ = self.original.__kwdefaults__
        result = importer(*args, **kwargs)  # Original failures retain their contract.
        try:
            if len(captured) != 1:
                raise ValueError("The importer did not capture exactly one source model.")
            path = args[0] if args else kwargs["path"]
            record = self._stamp(path, captured[0], result)
            if self.on_import is not None:
                self.on_import(record)
        except Exception as error:
            # gr_render retries an exception with a different importer. Never
            # turn a successful native import into duplicated fallback meshes.
            self._warn("source proof or import callback skipped: " + str(error))
        return result

    def apply_visibility(self, scene):
        """Hide only inactive, source-proven attire objects; leave unknowns alone."""
        stats = {"hidden": 0, "restored": 0, "skipped": 0}
        if self.closed:
            return stats
        mode = self.options.get("gorilla_gear", os.environ.get("GORILLA_GEAR_CHOICE", ""))
        if mode not in ("ring", "entrance"):
            return stats
        explicit = set(str(self.options.get("hide", "")).split(",")) - {""}
        for obj in scene.objects:
            proof = _read_identity(obj, False)
            if proof is None or proof["role"] != "attire" or proof["flags"] not in (128, 129, 130) \
                    or proof["path"] != self.attire_path:
                stats["skipped"] += 1
                continue
            inactive = proof["flags"] == (129 if mode == "ring" else 130)
            try:
                if inactive:
                    if not obj.hide_render:
                        obj[_MANAGED_HIDE] = True
                        obj.hide_render = True
                        stats["hidden"] += 1
                elif obj.get(_MANAGED_HIDE, False):
                    if obj.name not in explicit and obj.name.split(":", 1)[0] not in explicit:
                        obj.hide_render = False
                        stats["restored"] += 1
                    del obj[_MANAGED_HIDE]
            except (AttributeError, TypeError, ValueError, RuntimeError, ReferenceError) as error:
                self._warn("a gear visibility setting could not be applied: " + str(error))
        return stats

    def unregister(self):
        if self.closed:
            return
        self.closed = True
        if self.module is not None and self.module.import_mcd is self.wrapper:
            self.module.import_mcd = self.original
        handlers = getattr(getattr(getattr(self.bpy, "app", None), "handlers", None), "render_pre", ())
        if self.handler in handlers:
            handlers.remove(self.handler)
        self.installed = False


def register(options=None, on_import=None):
    """Install a persistent provenance/visibility repair for native MCD imports."""
    global _active_registration
    if on_import is not None and not callable(on_import):
        raise TypeError("on_import must be callable.")
    bpy = importlib.import_module("bpy")
    if _active_registration is not None:
        _active_registration.unregister()
    state = GeometryRegistration(options, on_import, bpy)
    _active_registration = state
    handlers = getattr(getattr(bpy, "app", None), "handlers", None)
    if handlers is None or not hasattr(handlers, "render_pre"):
        return state
    module = importlib.import_module("gorilla_blender")
    original = module.import_mcd
    previous = getattr(original, "_gorilla_geometry_registration", None)
    if previous is not None:
        previous.unregister()
        original = module.import_mcd
    if not isinstance(original, types.FunctionType) or "GM" not in original.__globals__ \
            or not callable(getattr(original.__globals__["GM"], "Model", None)):
        state._warn("unsupported importer left unchanged.")
        return state
    state.module, state.original = module, original
    @functools.wraps(original)
    def imported(*args, **kwargs):
        return state._import(*args, **kwargs)
    imported._gorilla_geometry_registration = state
    state.wrapper = imported
    module.import_mcd = imported
    def render_pre(scene, unused_depsgraph=None):
        try:
            state.apply_visibility(scene)
        except Exception as error:
            state._warn("gear visibility pass skipped: " + str(error))
    render_pre._gorilla_geometry_visibility = True
    persistent = getattr(handlers, "persistent", None)
    state.handler = persistent(render_pre) if callable(persistent) else render_pre
    for previous in list(handlers.render_pre):
        if getattr(previous, "_gorilla_geometry_visibility", False):
            handlers.render_pre.remove(previous)
    handlers.render_pre.append(state.handler)
    state.installed = True
    return state
