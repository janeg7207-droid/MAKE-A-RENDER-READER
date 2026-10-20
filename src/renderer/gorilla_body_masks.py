"""Apply source-proven common MCD body-hide faces to the evaluated render only.

The two HPL DATA modes are deliberately not assigned entrance/ring meanings.
Only IDs present in both modes are used, and every corner of a removed face
must be listed.  Original source files and original mesh topology stay intact.
Mode-specific clothing areas remain unchanged. This does not simulate YCL
cloth/hair, repair skin weights, or infer collisions for unmasked surfaces.
"""

import hashlib
import importlib
import os
import struct

REVISION = 1
MAXIMUM_SOURCE_BYTES = 512 * 1024 * 1024
MAXIMUM_IDS = 2_000_000
ATTRIBUTE = "gorilla_body_mask_common_v1"
OWNER = "gorilla_body_masks_revision"
FINGERPRINT = "gorilla_body_masks_fingerprint"
_active_registration = None


def _canonical(path):
    return os.path.normcase(os.path.realpath(os.path.abspath(os.fspath(path))))


def common_masks(model, targets=None):
    """Strictly decode bounded outer HPL records; return common LOD0 IDs.

    Unknown modes/tags, duplicate lists/IDs and incomplete groups are rejected
    together.  A missing mesh in either mode means its common mask is empty.
    """
    data = model.b
    if not isinstance(data, (bytes, bytearray)) or len(data) > MAXIMUM_SOURCE_BYTES:
        raise ValueError("Unsupported MCD source bytes.")
    record = model.rec.get("HPL!")
    if record is None:
        return {}
    start, length = record
    if not isinstance(start, int) or not isinstance(length, int) or start < 0 \
            or length < 12 or start + length > len(data):
        raise ValueError("HPL lies outside the source.")
    end = start + length
    p = start

    def uint32():
        nonlocal p
        if p + 4 > end:
            raise ValueError("Truncated HPL value.")
        value = struct.unpack_from("<I", data, p)[0]
        p += 4
        return value

    if data[p:p + 4] != b"HPL!":
        raise ValueError("Missing HPL header.")
    p += 4
    groups, lod, mode = {}, None, None
    finished = False
    while p + 4 <= end:
        tag = data[p:p + 4]
        p += 4
        if tag == b"LOD!":
            lod, mode = uint32(), None
            if lod > 31 or lod in groups:
                raise ValueError("Unsupported or duplicate HPL LOD.")
            groups[lod] = {}
        elif tag == b"DATA":
            mode = uint32()
            if lod is None or mode not in (0, 1) or mode in groups[lod]:
                raise ValueError("Unsupported or duplicate HPL DATA group.")
            groups[lod][mode] = {}
        elif tag == b"MESH":
            count = uint32()
            if mode is None or not 0 < count <= 4096 or p + ((count + 3) // 4) * 4 > end:
                raise ValueError("Invalid HPL mesh name.")
            name = data[p:p + count].decode("utf8")
            if "\0" in name or name in groups[lod][mode]:
                raise ValueError("Duplicate or invalid HPL mesh name.")
            p += ((count + 3) // 4) * 4
            count = uint32()
            if count > MAXIMUM_IDS or p + count * 4 > end:
                raise ValueError("Invalid HPL mask count.")
            values = frozenset(value[0] for value in struct.iter_unpack("<I", data[p:p + count * 4]))
            p += count * 4
            if len(values) != count:
                raise ValueError("Duplicate HPL mask ID.")
            groups[lod][mode][name] = values
        elif tag == b"END!":
            if uint32() != 0 or p != end:
                raise ValueError("Invalid HPL terminator.")
            finished = True
            break
        else:
            raise ValueError("Unsupported HPL tag.")
    if not finished or 0 not in groups or any(set(group) != {0, 1} for group in groups.values()):
        raise ValueError("Incomplete HPL groups.")
    if targets is not None:
        known = {name: frozenset(map(int, values)) for name, values in targets.items()}
        for group in groups.values():
            for meshes in group.values():
                for name, ids in meshes.items():
                    if name not in known or not ids <= known[name]:
                        raise ValueError("A hide mask does not fit the selected base mesh.")
    first, second = groups[0][0], groups[0][1]
    return {name: first[name] & second[name] for name in first.keys() & second.keys()
            if first[name] & second[name]}


def masked_faces(remap, faces, ids):
    """Return a boolean for each face, requiring every original REMAP corner."""
    if len(remap) > MAXIMUM_IDS:
        raise ValueError("Unsupported REMAP size.")
    known = frozenset(int(value) for value in remap)
    if not ids <= known:
        raise ValueError("The hide mask does not fit the selected base REMAP.")
    result = []
    for face in faces:
        corners = tuple(face)
        if len(corners) != 3 or any(not isinstance(i, int) or not 0 <= i < len(remap) for i in corners):
            raise ValueError("Unsupported source face.")
        result.append(all(int(remap[index]) in ids for index in corners))
    return tuple(result)


class BodyMaskRegistration:
    def __init__(self, options, geometry_state, geometry, bpy):
        self.options = dict(options or {})
        self.geometry_state, self.geometry, self.bpy = geometry_state, geometry, bpy
        paths = (geometry_state.base_path, geometry_state.attire_path) if geometry_state is not None \
            else geometry._source_paths(self.options)
        self.base_path, self.attire_path = paths
        self.records = {}
        self.prepared = False
        self.closed = False
        self._warned = set()
        self.handler = self.load_handler = self.import_callback = self.previous_callback = None
        self.stats = {"masked_objects": 0, "masked_faces": 0, "reused_objects": 0, "skipped": 0}

    def _warn(self, message):
        if message not in self._warned:
            self._warned.add(message)
            print("  body hide masks: " + message, flush=True)

    def capture_import(self, record):
        if record.role in ("base", "attire") and record.path == \
                (self.base_path if record.role == "base" else self.attire_path):
            self.records[record.role] = record
            self.prepared = False

    def _model(self, role, path):
        if not path or _canonical(path) != path or not os.path.isfile(path):
            raise ValueError("The selected source is unavailable.")
        before = os.stat(path)
        if not 0 < before.st_size <= MAXIMUM_SOURCE_BYTES:
            raise ValueError("Unsupported source size.")
        with open(path, "rb") as stream:
            raw = stream.read(MAXIMUM_SOURCE_BYTES + 1)
        if len(raw) > MAXIMUM_SOURCE_BYTES:
            raise ValueError("Source exceeded its size limit.")
        digest = hashlib.sha256(raw).hexdigest()
        captured = self.records.get(role)
        if captured is not None and captured.sha256 == digest:
            model = captured.model
        else:
            model = importlib.import_module("gorilla_mcd").Model(path)
        after = os.stat(path)
        if (before.st_size, before.st_mtime_ns, before.st_ino) != \
                (after.st_size, after.st_mtime_ns, after.st_ino) \
                or hashlib.sha256(model.b).hexdigest() != digest:
            raise ValueError("The selected source changed during preparation.")
        return model, digest

    def _objects(self, scene, role, path, model, digest):
        found = {}
        names = [mesh["name"] for mesh in model.meshes]
        if role == "base" and len(set(names)) != len(names):
            raise ValueError("Duplicate source mesh names are unsupported.")
        for obj in scene.objects:
            proof = self.geometry.read_provenance(obj)
            if proof is None or proof["role"] != role or proof["path"] != path:
                continue
            index = proof["index"]
            if index in found or not 0 <= index < len(model.meshes):
                raise ValueError("Ambiguous source mesh objects.")
            mesh = model.meshes[index]
            if proof["sha256"] != digest or proof["name"] != mesh["name"] \
                    or proof["vertices"] != int(mesh["verts"]) \
                    or proof["flags"] != int(mesh["flags"]) \
                    or self.geometry.decode_remap(obj) != tuple(map(int, mesh["numbering"])):
                raise ValueError("Stale or changed source provenance.")
            found[index] = obj
        if set(found) != set(range(len(model.meshes))):
            raise ValueError("The complete selected source mesh sequence is unavailable.")
        return found

    def _install(self, obj, selection, fingerprint):
        bpy = self.bpy
        # Most native modifiers do not support ID properties in Blender4.3.
        # The private GeometryNodeTree is an ID datablock and owns the proof.
        owned = [modifier for modifier in obj.modifiers if modifier.type == "NODES"
                 and modifier.node_group is not None and modifier.node_group.get(OWNER) == REVISION]
        if owned:
            if len(owned) != 1 or owned[0].node_group.get(FINGERPRINT) != fingerprint:
                raise ValueError("An incompatible managed body mask exists.")
            attribute = obj.data.attributes.get(ATTRIBUTE)
            if attribute is None or attribute.domain != "FACE" or attribute.data_type != "BOOLEAN" \
                    or tuple(item.value for item in attribute.data) != selection \
                    or not self._valid_group(owned[0].node_group, fingerprint) or not owned[0].show_render:
                raise ValueError("The existing body mask changed.")
            self.stats["reused_objects"] += 1
            return
        if obj.data.attributes.get(ATTRIBUTE) is not None:
            raise ValueError("An unmanaged body-mask attribute exists.")
        original = obj.data
        copied = group = modifier = None
        try:
            # A private mesh prevents an unrelated object sharing the datablock
            # from acquiring even the helper's inert face attribute.
            copied = original.copy()
            attribute = copied.attributes.new(ATTRIBUTE, "BOOLEAN", "FACE")
            attribute.data.foreach_set("value", selection)
            group = bpy.data.node_groups.new("Gorilla source body hide", "GeometryNodeTree")
            group[OWNER], group[FINGERPRINT] = REVISION, fingerprint
            group.interface.new_socket(name="Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
            group.interface.new_socket(name="Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
            inputs = group.nodes.new("NodeGroupInput")
            output = group.nodes.new("NodeGroupOutput")
            named = group.nodes.new("GeometryNodeInputNamedAttribute")
            named.data_type = "BOOLEAN"
            named.inputs["Name"].default_value = ATTRIBUTE
            delete = group.nodes.new("GeometryNodeDeleteGeometry")
            delete.domain, delete.mode = "FACE", "ONLY_FACE"
            group.links.new(inputs.outputs["Geometry"], delete.inputs["Geometry"])
            group.links.new(named.outputs["Attribute"], delete.inputs["Selection"])
            group.links.new(delete.outputs["Geometry"], output.inputs["Geometry"])
            modifier = obj.modifiers.new("Gorilla source body hide", "NODES")
            modifier.node_group = group
            obj.data = copied
            self.stats["masked_objects"] += 1
            self.stats["masked_faces"] += sum(selection)
        except Exception:
            if obj.data is copied:
                obj.data = original
            if modifier is not None:
                obj.modifiers.remove(modifier)
            if copied is not None and copied.users == 0:
                bpy.data.meshes.remove(copied)
            if group is not None and group.users == 0:
                bpy.data.node_groups.remove(group)
            raise

    @staticmethod
    def _valid_group(group, fingerprint):
        if group is None or group.get(OWNER) != REVISION or group.get(FINGERPRINT) != fingerprint:
            return False
        nodes = {node.bl_idname: node for node in group.nodes}
        expected = {"NodeGroupInput", "NodeGroupOutput", "GeometryNodeInputNamedAttribute", "GeometryNodeDeleteGeometry"}
        if len(group.nodes) != 4 or set(nodes) != expected or any(node.mute for node in group.nodes):
            return False
        named, delete = nodes["GeometryNodeInputNamedAttribute"], nodes["GeometryNodeDeleteGeometry"]
        if named.data_type != "BOOLEAN" or named.inputs["Name"].default_value != ATTRIBUTE \
                or delete.domain != "FACE" or delete.mode != "ONLY_FACE":
            return False
        links = {(link.from_node.bl_idname, link.from_socket.name, link.to_node.bl_idname, link.to_socket.name)
                 for link in group.links}
        return len(group.links) == 3 and links == {
            ("NodeGroupInput", "Geometry", "GeometryNodeDeleteGeometry", "Geometry"),
            ("GeometryNodeInputNamedAttribute", "Attribute", "GeometryNodeDeleteGeometry", "Selection"),
            ("GeometryNodeDeleteGeometry", "Geometry", "NodeGroupOutput", "Geometry"),
        }

    def apply(self, scene):
        """Prepare once per imported/loaded scene; frame callbacks do no source IO."""
        if self.closed or self.prepared:
            return dict(self.stats)
        self.prepared = True
        try:
            if not self.base_path or self.base_path == self.attire_path:
                raise ValueError("An exact selected base/attire pair is required.")
            base, base_sha = self._model("base", self.base_path)
            attire, attire_sha = self._model("attire", self.attire_path)
            names = [source["name"] for source in base.meshes]
            if len(set(names)) != len(names):
                raise ValueError("Duplicate base mesh names are unsupported.")
            masks = common_masks(attire, {source["name"]: source["numbering"] for source in base.meshes})
            # Never apply the base's duplicate HPL.
            if not masks:
                self._disable_owned(scene)
                return dict(self.stats)
            base_objects = self._objects(scene, "base", self.base_path, base, base_sha)
            self._objects(scene, "attire", self.attire_path, attire, attire_sha)
            if not base.lods or not isinstance(base.lods[0], dict):
                raise ValueError("The selected base has no supported LOD0.")
            plans = []
            for index, source in enumerate(base.meshes):
                ids = masks.get(source["name"])
                if not ids:
                    continue
                if index not in base.lods[0]:
                    raise ValueError("A masked mesh has no exact LOD0 faces.")
                obj = base_objects[index]
                faces = tuple(tuple(map(int, face)) for face in base.lods[0][index][0])
                if tuple(tuple(poly.vertices) for poly in obj.data.polygons) != faces:
                    raise ValueError("The source LOD0 topology changed.")
                selection = masked_faces(tuple(map(int, source["numbering"])), faces, ids)
                if any(selection):
                    fingerprint = hashlib.sha256((str(REVISION) + base_sha + attire_sha + str(index)).encode("ascii")
                                                  + bytes(selection)).hexdigest()
                    plans.append((obj, selection, fingerprint))
            # Complete every source validation before changing any object.
            for obj, selection, fingerprint in plans:
                self._install(obj, selection, fingerprint)
        except Exception as error:
            self.stats["skipped"] += 1
            # An old cached modifier must not keep evaluating after its
            # source or node proof fails. Only this helper's owned groups are
            # disabled; unrelated user modifiers and source meshes stay intact.
            self._disable_owned(scene)
            self._warn("skipped; managed masks disabled: " + str(error))
        return dict(self.stats)

    def _disable_owned(self, scene):
        for obj in scene.objects:
            for modifier in obj.modifiers:
                if modifier.type == "NODES" and modifier.node_group is not None \
                        and modifier.node_group.get(OWNER) == REVISION:
                    modifier.show_render = False
                    modifier.show_viewport = False

    def unregister(self):
        if self.closed:
            return
        self.closed = True
        if self.geometry_state is not None and self.geometry_state.on_import is self.import_callback:
            self.geometry_state.on_import = self.previous_callback
        handlers = self.bpy.app.handlers
        for name, callback in (("render_pre", self.handler), ("load_post", self.load_handler)):
            if callback in getattr(handlers, name):
                getattr(handlers, name).remove(callback)


def register(options=None, geometry_state=None):
    """Chain exact import capture and install persistent cached-scene support."""
    global _active_registration
    if _active_registration is not None:
        _active_registration.unregister()
    bpy, geometry = importlib.import_module("bpy"), importlib.import_module("gorilla_geometry")
    state = BodyMaskRegistration(options, geometry_state, geometry, bpy)
    _active_registration = state
    if geometry_state is not None:
        state.previous_callback = geometry_state.on_import
        def captured(record):
            if state.previous_callback is not None:
                state.previous_callback(record)
            state.capture_import(record)
        state.import_callback = captured
        geometry_state.on_import = captured
    def render_pre(scene, unused_depsgraph=None):
        state.apply(scene)
    def load_post(unused=None):
        state.records.clear()
        state.prepared = False
        state.stats = {"masked_objects": 0, "masked_faces": 0, "reused_objects": 0, "skipped": 0}
    handlers = bpy.app.handlers
    state.handler, state.load_handler = handlers.persistent(render_pre), handlers.persistent(load_post)
    state.handler._gorilla_body_masks = True
    state.load_handler._gorilla_body_masks = True
    for name in ("render_pre", "load_post"):
        for previous in list(getattr(handlers, name)):
            if getattr(previous, "_gorilla_body_masks", False):
                getattr(handlers, name).remove(previous)
    handlers.render_pre.append(state.handler)
    handlers.load_post.append(state.load_handler)
    return state
