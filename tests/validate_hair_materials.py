"""Check hair repair against the original CPython 3.13 Blender material code.

Run with CPython 3.13. Tests use a small Blender node API substitute and execute
the original bytecode's hair constructors and recolour gate. They verify graph
connections and values, without rendering images or claiming Blender/gameplay
validation. The supplied application files are read only.
"""

import argparse
import contextlib
import copy
import hashlib
import io
import json
import marshal
import runpy
from pathlib import Path
import sys
import types
import warnings


class Socket:
    def __init__(self, node, name):
        self.node, self.name, self.links = node, name, []
        self.default_value = 0

    @property
    def is_linked(self):
        return bool(self.links)


class Sockets:
    def __init__(self, node, names):
        self.items = [Socket(node, name) for name in names]

    def __getitem__(self, key):
        if isinstance(key, int):
            return self.items[key]
        return next(item for item in self.items if item.name == key)

    def __contains__(self, key):
        return any(item.name == key for item in self.items)


class Node:
    layouts = {
        "ShaderNodeBsdfPrincipled": ("BSDF_PRINCIPLED", ["Base Color", "Alpha", "Roughness", "Specular IOR Level"], ["BSDF"]),
        "ShaderNodeOutputMaterial": ("OUTPUT_MATERIAL", ["Surface"], []),
        "ShaderNodeVertexColor": ("VERTEX_COLOR", [], ["Color", "Alpha"]),
        "ShaderNodeTexImage": ("TEX_IMAGE", [], ["Color", "Alpha"]),
        "ShaderNodeSeparateColor": ("SEPARATE_COLOR", ["Color"], ["Red", "Green", "Blue"]),
        "ShaderNodeValToRGB": ("VALTORGB", ["Fac"], ["Color", "Alpha"]),
        "ShaderNodeMath": ("MATH", ["Value", "Value_001", "Value_002"], ["Value"]),
        "ShaderNodeVectorMath": ("VECT_MATH", ["Vector", "Vector_001", "Vector_002", "Scale"], ["Vector", "Value"]),
        "ShaderNodeRGBToBW": ("RGBTOBW", ["Color"], ["Val"]),
    }

    def __init__(self, kind):
        self.type, inputs, outputs = self.layouts[kind]
        self.inputs, self.outputs = Sockets(self, inputs), Sockets(self, outputs)
        self.mode = "RGB"
        self.is_active_output = True
        self.image = None
        self.layer_name = ""
        self.label = ""
        self.color_ramp = types.SimpleNamespace(elements=[types.SimpleNamespace(color=None), types.SimpleNamespace(color=None)])


class Links:
    def __init__(self):
        self.items = []

    def __iter__(self):
        return iter(self.items)

    def new(self, output, input):
        for previous in list(input.links):
            self.remove(previous)
        link = types.SimpleNamespace(from_node=output.node, to_node=input.node, from_socket=output, to_socket=input)
        output.links.append(link)
        input.links.append(link)
        self.items.append(link)
        return link

    def remove(self, link):
        link.from_socket.links.remove(link)
        link.to_socket.links.remove(link)
        self.items.remove(link)


class Nodes:
    def __init__(self, tree):
        self.items, self.tree = [], tree

    def __iter__(self):
        return iter(self.items)

    def __len__(self):
        return len(self.items)

    def new(self, kind):
        node = Node(kind)
        self.items.append(node)
        return node

    def remove(self, node):
        for link in list(self.tree.links):
            if link.from_node == node or link.to_node == node:
                self.tree.links.remove(link)
        self.items.remove(node)

    def clear(self):
        for node in list(self.items):
            self.remove(node)


class Material:
    def __init__(self, name):
        self.name, self.use_nodes = name, True
        self.node_tree = types.SimpleNamespace(links=Links())
        self.node_tree.nodes = Nodes(self.node_tree)
        bsdf = self.node_tree.nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.inputs["Base Color"].default_value = (0.8, 0.8, 0.8, 1.0)
        output = self.node_tree.nodes.new("ShaderNodeOutputMaterial")
        self.node_tree.links.new(bsdf.outputs[0], output.inputs[0])

    def as_pointer(self):
        return id(self)

    def copy(self):
        result = copy.deepcopy(self)
        result.name += " (isolated hair)"
        return result


class MaterialSlot:
    def __init__(self, obj, index):
        self.obj, self.index, self.link, self.override = obj, index, "DATA", None

    @property
    def material(self):
        return self.override if self.link == "OBJECT" else self.obj.data.materials[self.index]

    @material.setter
    def material(self, value):
        if self.link == "OBJECT":
            self.override = value
        else:
            self.obj.data.materials[self.index] = value


class MeshObject:
    def __init__(self, name, material, colours=None, shared_data=None):
        self.type, self.name, self.hide_render = "MESH", name, False
        self.data = shared_data or types.SimpleNamespace(materials=[material], color_attributes={})
        if colours is not None:
            self.data.color_attributes["ColorMap"] = types.SimpleNamespace(data=[
                types.SimpleNamespace(color_srgb=tuple(value) + (1.0,)) for value in colours])
        self.material_slots = [MaterialSlot(self, index) for index in range(len(self.data.materials))]

    def as_pointer(self):
        return id(self)


def shader(material):
    return next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")


def image(name):
    return types.SimpleNamespace(name=name, colorspace_settings=types.SimpleNamespace())


def mesh(material):
    return types.SimpleNamespace(type="MESH", name="Dean test hair", data=types.SimpleNamespace(materials=[material]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application", type=Path, required=True)
    parser.add_argument("--wrapper", type=Path, default=Path(__file__).resolve().parents[1] / "runtime/engine/gr_render.py")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 13):
        raise SystemExit("Use CPython 3.13 to execute the original Blender bytecode safely.")
    root = args.application / "engine/blender"
    paths = [root / "gr_render.pyc", root / "gorilla_blender.pyc"]
    before = {path: hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
    render_code, importer_code = [marshal.loads(path.read_bytes()[16:]) for path in paths]
    checks = 0

    def check(condition, message):
        nonlocal checks
        assert condition, message
        checks += 1

    def function(module, name, globals):
        code = next(item for item in module.co_consts if isinstance(item, types.CodeType) and item.co_name == name)
        return types.FunctionType(code, globals)

    source_image = image("F4876B85A3CDA006.dds")
    native_bind = function(importer_code, "hair_material", {
        "game_picture": lambda path: source_image,
        "find_principled": lambda tree: next(node for node in tree.nodes if node.type == "BSDF_PRINCIPLED"),
    })

    def hair_material(name="Hair"):
        result = Material(name)
        check(native_bind(result, "hash-named-RAI.dds"), "Original importer did not bind its hair material")
        return result

    class Captured(BaseException):
        pass

    bpy = types.ModuleType("bpy")
    handlers = types.SimpleNamespace(render_pre=[])

    def persistent(callback):
        callback._bpy_persistent = True
        return callback

    handlers.persistent = persistent
    bpy.app = types.SimpleNamespace(handlers=handlers)
    bpy.data = types.SimpleNamespace(images=types.SimpleNamespace(load=lambda *unused, **options: source_image))
    sys.modules["bpy"] = bpy
    sys.modules["file_audit"] = types.ModuleType("file_audit")
    sys.modules["numpy"] = types.ModuleType("numpy")
    sys.modules["mathutils"] = types.SimpleNamespace(Matrix=object, Vector=object)
    sys.modules["render_kit"] = types.SimpleNamespace()
    sys.modules["gs_paths"] = types.SimpleNamespace(STOCK_ROOT=root, ENGINE=str(root.parent))

    # Reproduce the original filename gate with the actual untouched module code.
    material = hair_material()
    obj = mesh(material)
    for filename, expected in (("hair_rai.dds", True), ("F4876B85A3CDA006.dds", False)):
        source_image.name = filename
        reached = False
        sys.argv = ["blender", "--", "unused.npz", "unused", "clip", "hair_dark=0.005,0.002,0.001", "hair_light=0.05,0.025,0.01"]

        def trace(frame, event, unused):
            nonlocal reached
            if frame.f_code is render_code and event == "line":
                if frame.f_lineno == 24:
                    frame.f_globals["meshes"] = [obj]
                    frame.f_lineno = 464
                elif frame.f_lineno == 482:
                    reached = True
                    raise Captured()
                elif frame.f_lineno == 500:
                    raise Captured()
            return trace

        sys.settrace(trace)
        try:
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", RuntimeWarning)
                exec(render_code, {"__file__": str(root / "gr_render.pyc"), "__name__": "hair_gate_probe"})
        except Captured:
            pass
        finally:
            sys.settrace(None)
        check(reached == expected, "Original hair-image filename gate was not reproduced")

    # Importing the original renderer is substituted here; the actual native
    # material constructors and gate above execute independently and unchanged.
    sys.modules["gr_render"] = types.ModuleType("gr_render")

    def load_wrapper(dark, light, extra="samples=8", quick=False, choice="selected"):
        import os
        os.environ["GORILLA_EXTRA"] = extra
        os.environ["GORILLA_HAIR_CHOICE"] = choice
        sys.argv = ["blender", "--factory-startup", "-b", "--", "capture.npz", "output path", "clip",
                    "hair_dark=" + ",".join(map(str, dark)), "hair_light=" + ",".join(map(str, light)), "fps=30", "res=1024"]
        if quick:
            sys.argv.append("quick=1")
        namespace = runpy.run_path(str(args.wrapper), run_name="__main__")
        check(len(handlers.render_pre) == 1, "Exactly one render callback must be installed")
        check(handlers.render_pre[0]._bpy_persistent, "Cached .blend loading must retain the callback")
        check(sys.argv[-len(extra.split()):] == extra.split(), "Speed options were lost")
        return handlers.render_pre[0], namespace

    results = []
    for label, dark, light in (
        ("dark brown", (0.006, 0.004, 0.002), (0.05, 0.035, 0.02)),
        ("blond", (0.22, 0.16, 0.08), (0.8, 0.65, 0.4)),
        ("white", (0.65, 0.65, 0.65), (1.0, 1.0, 1.0)),
    ):
        callback, unused = load_wrapper(dark, light, quick=label == "blond")
        material = hair_material(label)
        source_image.name = "F4876B85A3CDA006.dds"
        native_shader = shader(material)
        alpha = native_shader.inputs["Alpha"].links[0]
        rough = native_shader.inputs["Roughness"].links[0]
        nodes_before = len(material.node_tree.nodes)
        scene = types.SimpleNamespace(objects=[mesh(material), mesh(material)])
        with contextlib.redirect_stdout(io.StringIO()):
            callback(scene)
        check(len(material.node_tree.nodes) == nodes_before + 3, "Repair must run once for a shared material")
        check(native_shader.inputs["Alpha"].links[0] is alpha, "Coverage link changed")
        check(native_shader.inputs["Roughness"].links[0] is rough, "Roughness link changed")
        colour = native_shader.inputs["Base Color"].links[0].from_node
        check(colour.type == "VECT_MATH" and colour.operation == "SCALE", "Hair tint did not reach Base Color")
        ramp = colour.inputs[0].links[0].from_node
        variation = colour.inputs["Scale"].links[0].from_node
        check(ramp.color_ramp.elements[0].color == dark + (1.0,), "Selected dark colour was changed")
        check(ramp.color_ramp.elements[1].color == light + (1.0,), "Selected light colour was changed")
        separate = alpha.from_node
        check(ramp.inputs[0].links[0].from_socket is separate.outputs[0], "Strand shade must use RAI red")
        check(variation.inputs[0].links[0].from_socket is separate.outputs[2], "Strand variation must use RAI blue")
        check(variation.operation == "MULTIPLY_ADD" and variation.inputs[1].default_value == 0.7
              and variation.inputs[2].default_value == 0.65, "Native strand variation formula changed")
        # A second frame and a dependency-graph argument cannot add more nodes.
        with contextlib.redirect_stdout(io.StringIO()):
            callback(scene, object())
        check(len(material.node_tree.nodes) == nodes_before + 3, "Repair repeated on a later frame")
        results.append({"colour": label, "dark": dark, "light": light, "alpha_unchanged": True})

    callback, namespace = load_wrapper((0.006, 0.004, 0.002), (0.05, 0.035, 0.02))
    # Simulate the clearing that open_mainfile performs. The persistent handler
    # repairs the newly restored material rather than only imported objects.
    handlers.render_pre[:] = [handler for handler in handlers.render_pre if getattr(handler, "_bpy_persistent", False)]
    restored = hair_material("Restored cached blend")
    with contextlib.redirect_stdout(io.StringIO()):
        handlers.render_pre[0](types.SimpleNamespace(objects=[mesh(restored)]))
    check(shader(restored).inputs["Base Color"].links[0].from_node.type == "VECT_MATH", "Cached scene was not repaired")

    # Preserve a material already rebuilt by the original renderer itself.
    corrected = Material("Native corrected hair")
    native_rebuild = function(render_code, "hair_material", {"bpy": bpy, "os": __import__("os")})
    with contextlib.redirect_stdout(io.StringIO()):
        native_rebuild(mesh(corrected), "native_rai.dds", (0.01, 0.02, 0.03), (0.1, 0.2, 0.3), 0.6, 1.0, 0.12, 0.06)
    native_base = shader(corrected).inputs["Base Color"].links[0]
    native_alpha = shader(corrected).inputs["Alpha"].links[0]
    native_nodes = len(corrected.node_tree.nodes)
    callback(types.SimpleNamespace(objects=[mesh(corrected)]))
    check(shader(corrected).inputs["Base Color"].links[0] is native_base, "Already-correct native colour was replaced")
    check(shader(corrected).inputs["Alpha"].links[0] is native_alpha, "Already-correct native alpha was replaced")
    check(len(corrected.node_tree.nodes) == native_nodes, "Already-correct native graph was modified")

    # Close-but-different shader graphs do not establish native hair semantics.
    for case in ("no RAI", "image alpha", "red coverage", "no roughness", "other vertex layer", "texture colour", "inactive output", "HSV channels", "missing image"):
        other = hair_material(case)
        tree, bsdf = other.node_tree, shader(other)
        alpha = bsdf.inputs["Alpha"].links[0]
        separate = alpha.from_node
        vertex = bsdf.inputs["Base Color"].links[0].from_node
        texture = separate.inputs[0].links[0].from_node
        if case == "no RAI":
            tree.links.remove(alpha)
        elif case == "image alpha":
            tree.links.new(texture.outputs["Alpha"], bsdf.inputs["Alpha"])
        elif case == "red coverage":
            tree.links.new(separate.outputs[0], bsdf.inputs["Alpha"])
        elif case == "no roughness":
            tree.links.remove(bsdf.inputs["Roughness"].links[0])
        elif case == "other vertex layer":
            vertex.layer_name = "BodyColour"
        elif case == "texture colour":
            tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
        elif case == "inactive output":
            next(node for node in tree.nodes if node.type == "OUTPUT_MATERIAL").is_active_output = False
        elif case == "HSV channels":
            separate.mode = "HSV"
        elif case == "missing image":
            texture.image = None
        base_link = bsdf.inputs["Base Color"].links[0]
        count = len(tree.nodes)
        callback(types.SimpleNamespace(objects=[mesh(other)]))
        check(bsdf.inputs["Base Color"].links[0] is base_link, "A non-native graph was recoloured: " + case)
        check(len(tree.nodes) == count, "A non-native graph gained repair nodes: " + case)

    # A partial API failure cannot leave a material with its old colour detached.
    failed = hair_material("Unwritable shader fixture")
    original_new = failed.node_tree.nodes.new
    def fail_vector(kind):
        if kind == "ShaderNodeVectorMath":
            raise RuntimeError("Cannot create the final colour node")
        return original_new(kind)
    failed.node_tree.nodes.new = fail_vector
    original_base = shader(failed).inputs["Base Color"].links[0]
    original_nodes = len(failed.node_tree.nodes)
    original_alpha = shader(failed).inputs["Alpha"].links[0]
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[mesh(failed)]))
    check(len(failed.node_tree.nodes) == original_nodes, "Partial repair nodes were not rolled back")
    check(shader(failed).inputs["Base Color"].links[0] is original_base, "Failed repair disconnected the original colour")
    check(shader(failed).inputs["Alpha"].links[0] is original_alpha, "Failed repair changed coverage")

    # The native empty material constructor models the other failure gate:
    # missing RAI pictures can leave the importer with an ordinary white BSDF.
    registry = {}
    def new_material(name):
        material = Material(name)
        registry[name] = material
        return material
    bpy.data.materials = types.SimpleNamespace(get=registry.get, new=new_material)
    native_empty = function(importer_code, "_material", {"bpy": bpy})
    def generic(name, vertex=False, alpha=False, colour_texture=False):
        material = native_empty(name)
        check(material.use_nodes and shader(material).inputs["Base Color"].default_value == (0.8, 0.8, 0.8, 1.0),
              "The original generic material constructor changed")
        tree, bsdf = material.node_tree, shader(material)
        if vertex:
            node = tree.nodes.new("ShaderNodeVertexColor")
            node.layer_name = "ColorMap"
            tree.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
        if alpha or colour_texture:
            texture = tree.nodes.new("ShaderNodeTexImage")
            texture.image = image("Actual model texture.dds")
            if alpha:
                tree.links.new(texture.outputs["Alpha"], bsdf.inputs["Alpha"])
            if colour_texture:
                tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
        bsdf.inputs["Roughness"].default_value = 0.73
        return material

    def connection(socket):
        return [(link.from_node.type, link.from_socket.name,
                 getattr(getattr(link.from_node, "image", None), "name", None)) for link in socket.links]

    fallback_results = []
    for label, dark, light in (
        ("dark brown", (0.006, 0.004, 0.002), (0.05, 0.035, 0.02)),
        ("blond", (0.22, 0.16, 0.08), (0.8, 0.65, 0.4)),
        ("white", (0.65, 0.65, 0.65), (1.0, 1.0, 1.0)),
    ):
        callback, unused = load_wrapper(dark, light)
        for vertex in (False, True):
            original = generic("Generic " + label + str(vertex), vertex=vertex, alpha=vertex)
            obj = MeshObject("M_Hair_ALPHA:skinned", original, colours=[(1.0, 1.0, 1.0)])
            # Use the exact same mesh datablock for another object to prove
            # that a DATA-linked material assignment cannot tint its body.
            body = MeshObject("body:skinned", original, shared_data=obj.data)
            body_shader = shader(original)
            old_base = list(body_shader.inputs["Base Color"].links)
            old_count = len(original.node_tree.nodes)
            alpha = connection(body_shader.inputs["Alpha"])
            rough = connection(body_shader.inputs["Roughness"])
            with contextlib.redirect_stdout(io.StringIO()):
                callback(types.SimpleNamespace(objects=[obj, body]))
            repaired = obj.material_slots[0].material
            check(repaired is not original, "Missing-map repair reused a shared body material")
            check(obj.material_slots[0].link == "OBJECT", "Hair copy was not assigned per object")
            check(body.material_slots[0].material is original and obj.data.materials[0] is original,
                  "A shared body/mesh material was replaced")
            check(list(body_shader.inputs["Base Color"].links) == old_base and len(original.node_tree.nodes) == old_count,
                  "Body shader nodes or colour were modified")
            fixed = shader(repaired)
            check(connection(fixed.inputs["Alpha"]) == alpha, "Fallback changed available alpha coverage")
            check(connection(fixed.inputs["Roughness"]) == rough and fixed.inputs["Roughness"].default_value == 0.73,
                  "Fallback changed the original roughness")
            ramp = fixed.inputs["Base Color"].links[0].from_node
            check(ramp.type == "VALTORGB" and ramp.color_ramp.elements[0].color == dark + (1.0,),
                  "Missing-map dark tint was not selected")
            check(ramp.color_ramp.elements[1].color == light + (1.0,), "Missing-map light tint was not selected")
            if vertex:
                shade = ramp.inputs[0].links[0].from_node
                check(shade.type == "RGBTOBW" and shade.inputs[0].links[0].from_node.type == "VERTEX_COLOR",
                      "Fallback discarded available vertex shade variation")
            else:
                check(ramp.inputs[0].default_value == 0.5, "Missing-map constant shade changed unexpectedly")
            with contextlib.redirect_stdout(io.StringIO()):
                callback(types.SimpleNamespace(objects=[obj, body]))
            check(obj.material_slots[0].material is repaired, "Later frames created another isolated material")
            fallback_results.append({"colour": label, "generic_vertex_colour": vertex,
                                     "body_material_unchanged": True, "existing_alpha_unchanged": True})

    callback, unused = load_wrapper((0.006, 0.004, 0.002), (0.05, 0.035, 0.02), choice="auto")
    natural = [(0.64, 0.45, 0.21), (0.73, 0.58, 0.31)]
    blond = generic("Natural blond in white empty shader")
    blond_obj = MeshObject("M_Hair_ALPHA:skinned", blond, colours=natural)
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[blond_obj]))
    check(shader(blond_obj.material_slots[0].material).inputs["Base Color"].links[0].from_node.type == "VERTEX_COLOR",
          "Auto ignored meaningful primary hair colours")
    check(blond_obj.data.color_attributes["ColorMap"].data[0].color_srgb[:3] == natural[0], "Natural blond colours were changed")
    existing_natural = generic("Existing natural ColorMap", vertex=True)
    existing_obj = MeshObject("M_Hair_ALPHA:skinned", existing_natural, colours=natural)
    callback(types.SimpleNamespace(objects=[existing_obj]))
    check(existing_obj.material_slots[0].material is existing_natural, "Auto tinted an already-coloured primary hair shader")

    for name, colours in (("M_Hair_ALPHA:skinned", [(1.0, 1.0, 1.0)]),
                          ("S_Hair_ALPHA:skinned", [(1.0, 0.0, 0.0), (1.0, 1.0, 1.0)]),
                          ("M_Combination_ALPHA:skinned", [(1.0, 1.0, 1.0)]),
                          ("M_Hair_ALPHA.001", [(1.0, 1.0, 1.0)])):
        original = generic("Default white " + name, vertex=True)
        obj = MeshObject(name, original, colours=colours)
        with contextlib.redirect_stdout(io.StringIO()):
            callback(types.SimpleNamespace(objects=[obj]))
        check(obj.material_slots[0].material is not original, "Auto left a verified control-white hair shader untreated")
        check(shader(obj.material_slots[0].material).inputs["Base Color"].links[0].from_node.type == "VALTORGB",
              "Control colours were mistaken for a natural hair tint")

    # Preserve real albedo in Auto, but honour a manual shade while keeping its
    # texture connected as variation and retaining its existing alpha channel.
    textured = generic("Natural textured hair", colour_texture=True, alpha=True)
    textured_obj = MeshObject("M_Hair_ALPHA:skinned", textured)
    callback(types.SimpleNamespace(objects=[textured_obj]))
    check(textured_obj.material_slots[0].material is textured, "Auto replaced an existing natural hair texture")
    callback, unused = load_wrapper((0.2, 0.1, 0.03), (0.8, 0.6, 0.3))
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[textured_obj]))
    copied = textured_obj.material_slots[0].material
    ramp = shader(copied).inputs["Base Color"].links[0].from_node
    check(ramp.inputs[0].links[0].from_node.inputs[0].links[0].from_node.type == "TEX_IMAGE",
          "Explicit hair tint disconnected the available albedo texture")
    check(connection(shader(copied).inputs["Alpha"]) == connection(shader(textured).inputs["Alpha"]),
          "Explicit textured hair tint disconnected coverage")

    for name in ("body_ALPHA", "beard_ALPHA", "M_HairBand_ALPHA", "M_Hair_Tie_ALPHA", "Hair without native suffix"):
        other = generic("Unrelated " + name)
        obj = MeshObject(name, other)
        callback(types.SimpleNamespace(objects=[obj]))
        check(obj.material_slots[0].material is other, "An unrelated/accessory mesh received hair tint: " + name)
    hidden = generic("Hidden hair")
    hidden_obj = MeshObject("M_Hair_ALPHA:skinned", hidden)
    hidden_obj.hide_render = True
    callback(types.SimpleNamespace(objects=[hidden_obj]))
    check(hidden_obj.material_slots[0].material is hidden, "A hidden mesh received unnecessary material changes")

    # A copied material restored from a saved scene must take the next selected
    # colour, rather than freezing the previous process's private ramp values.
    previous = copied
    callback, unused = load_wrapper((0.01, 0.01, 0.01), (0.07, 0.07, 0.07))
    callback(types.SimpleNamespace(objects=[textured_obj]))
    check(textured_obj.material_slots[0].material is previous, "A restored private ramp created another copy")
    check(ramp.color_ramp.elements[0].color == (0.01, 0.01, 0.01, 1.0)
          and ramp.color_ramp.elements[1].color == (0.07, 0.07, 0.07, 1.0), "Cached private ramp retained an old colour")
    callback, unused = load_wrapper((0.006, 0.004, 0.002), (0.05, 0.035, 0.02), choice="auto")
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[blond_obj]))
    check(shader(blond_obj.material_slots[0].material).inputs["Base Color"].links[0].from_node.type == "VERTEX_COLOR",
          "Auto stopped respecting natural hair after a cached material load")
    textured_alpha = connection(shader(previous).inputs["Alpha"])
    textured_rough = connection(shader(previous).inputs["Roughness"])
    textured_rough_value = shader(previous).inputs["Roughness"].default_value
    handlers.render_pre[:] = [handler for handler in handlers.render_pre if getattr(handler, "_bpy_persistent", False)]
    handlers.render_pre[0](types.SimpleNamespace(objects=[textured_obj]))
    check(textured_obj.material_slots[0].material is previous, "Restoring Auto albedo replaced its isolated material")
    restored_colour = shader(previous).inputs["Base Color"].links[0]
    check(restored_colour.from_node.type == "TEX_IMAGE" and restored_colour.from_socket.name == "Color"
          and restored_colour.from_node.image.name == "Actual model texture.dds",
          "Auto retained a previous manual tint instead of the original albedo texture")
    check(connection(shader(previous).inputs["Alpha"]) == textured_alpha, "Auto albedo restoration changed coverage")
    check(connection(shader(previous).inputs["Roughness"]) == textured_rough
          and shader(previous).inputs["Roughness"].default_value == textured_rough_value,
          "Auto albedo restoration changed roughness")
    handlers.render_pre[0](types.SimpleNamespace(objects=[textured_obj]))
    check(shader(previous).inputs["Base Color"].links[0] is restored_colour, "A later frame reapplied an old manual tint")

    callback, unused = load_wrapper((0.01, 0.005, 0.002), (0.05, 0.02, 0.01))
    constant = generic("Meaningful natural constant hair", alpha=True)
    natural_constant = (0.7, 0.5, 0.25, 1.0)
    shader(constant).inputs["Base Color"].default_value = natural_constant
    constant_obj = MeshObject("M_Hair_ALPHA:skinned", constant)
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[constant_obj]))
    constant_copy = constant_obj.material_slots[0].material
    check(shader(constant_copy).inputs["Base Color"].links[0].from_node.type == "VALTORGB", "Manual constant tint did not apply")
    constant_alpha = connection(shader(constant_copy).inputs["Alpha"])
    constant_rough = shader(constant_copy).inputs["Roughness"].default_value
    callback, unused = load_wrapper((0.006, 0.004, 0.002), (0.05, 0.035, 0.02), choice="auto")
    handlers.render_pre[:] = [handler for handler in handlers.render_pre if getattr(handler, "_bpy_persistent", False)]
    handlers.render_pre[0](types.SimpleNamespace(objects=[constant_obj]))
    check(not shader(constant_copy).inputs["Base Color"].links, "Auto kept the old tint on a meaningful constant hair colour")
    check(shader(constant_copy).inputs["Base Color"].default_value == natural_constant, "Auto did not restore the original constant albedo")
    check(connection(shader(constant_copy).inputs["Alpha"]) == constant_alpha, "Constant albedo restoration changed coverage")
    check(shader(constant_copy).inputs["Roughness"].default_value == constant_rough, "Constant albedo restoration changed roughness")
    handlers.render_pre[0](types.SimpleNamespace(objects=[constant_obj]))
    check(not shader(constant_copy).inputs["Base Color"].links, "A later frame retinted the restored constant albedo")

    callback, unused = load_wrapper((0.2, 0.1, 0.03), (0.8, 0.6, 0.3))
    control_white = generic("Original white cached shader", alpha=True)
    white_obj = MeshObject("M_Hair_ALPHA:skinned", control_white, colours=[(1.0, 1.0, 1.0)])
    with contextlib.redirect_stdout(io.StringIO()):
        callback(types.SimpleNamespace(objects=[white_obj]))
    white_copy = white_obj.material_slots[0].material
    white_alpha = connection(shader(white_copy).inputs["Alpha"])
    callback, unused = load_wrapper((0.006, 0.004, 0.002), (0.05, 0.035, 0.02), choice="auto")
    handlers.render_pre[:] = [handler for handler in handlers.render_pre if getattr(handler, "_bpy_persistent", False)]
    callback(types.SimpleNamespace(objects=[white_obj]))
    white_ramp = shader(white_copy).inputs["Base Color"].links[0].from_node
    check(white_ramp.type == "VALTORGB", "Auto restored an untreated white shader instead of repairing it")
    check(white_ramp.color_ramp.elements[0].color == (0.006, 0.004, 0.002, 1.0)
          and white_ramp.color_ramp.elements[1].color == (0.05, 0.035, 0.02, 1.0),
          "Auto kept a previous manual shade on an originally white shader")
    check(connection(shader(white_copy).inputs["Alpha"]) == white_alpha, "Cached white shader repair changed coverage")

    # Invalid/missing colour settings must never create a tint or duplicate a handler.
    sys.argv = ["blender", "--", "capture", "output", "clip"]
    namespace = runpy.run_path(str(args.wrapper), run_name="__main__")
    check(handlers.render_pre == [], "Missing tint settings retained a stale callback")
    sys.argv += ["hair_dark=nan,0,0", "hair_light=1,1,1"]
    runpy.run_path(str(args.wrapper), run_name="__main__")
    check(handlers.render_pre == [], "Non-finite tint settings created a callback")
    for path, digest in before.items():
        check(hashlib.sha256(path.read_bytes()).hexdigest() == digest, "Original bytecode was modified")
    report = {"checks_passed": checks, "native_importer_hair_graph_executed": True,
              "native_filename_gate_reproduced": True, "native_corrected_shader_preserved": True,
              "hash_named_RAI_repaired": True, "persistent_cached_scene_handler": True,
              "preview_and_full_entry_point": True, "selected_colours": results,
              "missing_RAI_named_hair_fallback": fallback_results,
              "natural_primary_hair_colours_retained_in_auto": True,
              "cached_manual_tint_to_auto_restores_original_texture_and_constant": True,
              "alpha_not_invented_when_missing": True,
              "original_bytecode_unchanged": True, "windows_blender_images_or_game_executed": False}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
