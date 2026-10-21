"""Forward options and repair untreated native hair materials before rendering.

The packaged engine's parallel MAKE path starts Blender directly, bypassing
the helper that normally appends GORILLA_EXTRA.  gs_paths.blender_script prefers
this entry point when present.  Rendering still uses engine/blender/gr_render.pyc.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender"))
extra = os.environ.get("GORILLA_EXTRA", "").split()
if extra and sys.argv[-len(extra):] != extra:
    sys.argv.extend(extra)


def _register_hair_colour_repair():
    """Repair the importer's ColorMap/RAI shader, including hash-named textures.

    The original renderer identifies this shader by an image-name substring.
    The importer already identifies its actual RAI map from the material file;
    its exact node connections provide a stronger identity than that filename.
    """
    import math
    import bpy

    handlers = getattr(getattr(bpy, "app", None), "handlers", None)
    if handlers is None:
        return  # Allows non-rendering tools to inspect the original module.
    for previous in list(handlers.render_pre):
        if getattr(previous, "_gorilla_hair_colour_repair", False):
            handlers.render_pre.remove(previous)
    try:
        options = dict(value.split("=", 1) for value in sys.argv[sys.argv.index("--") + 4:] if "=" in value)
        dark = tuple(float(value) for value in options["hair_dark"].split(","))
        light = tuple(float(value) for value in options["hair_light"].split(","))
        if len(dark) != 3 or len(light) != 3 or not all(math.isfinite(value) for value in dark + light):
            return
    except (KeyError, ValueError):
        return

    # Native model preparation can open a cached .blend. This callback must
    # survive that load, then run after the original material passes finish.
    repaired = set()
    warned = set()
    fallback_done = set()
    decoded_images = {}
    automatic = os.environ.get("GORILLA_HAIR_CHOICE", "").strip().lower() == "auto"
    hidden = set(options.get("hide", "").split(","))

    def only_link(socket):
        links = socket.links
        return links[0] if len(links) == 1 else None

    def same_socket(left, right):
        return left == right

    def usable_image(image):
        if image is None:
            return False
        # Cached external and packed images load lazily: has_data can be false
        # until size resolves their pixels. A failed load has zero dimensions.
        # Resolve each datablock once, outside subsequent frame callbacks.
        try:
            identity = image.as_pointer()
        except AttributeError:
            identity = id(image)
        except (RuntimeError, ReferenceError):
            return False
        if identity not in decoded_images:
            try:
                size = image.size
                decoded_images[identity] = len(size) >= 2 and size[0] > 0 and size[1] > 0
            except (AttributeError, TypeError, ValueError, RuntimeError, ReferenceError):
                decoded_images[identity] = False
        return decoded_images[identity]

    def active_shader(material):
        if not material.use_nodes or material.node_tree is None:
            return None
        for output in material.node_tree.nodes:
            if output.type != "OUTPUT_MATERIAL" or not getattr(output, "is_active_output", True):
                continue
            link = only_link(output.inputs["Surface"])
            if link is not None and link.from_node.type == "BSDF_PRINCIPLED":
                return link.from_node
        return None

    def hair_mesh_stem(obj):
        # These are the pipeline's material-based object names, e.g.
        # M_Hair_ALPHA:skinned. Exclude common hair accessories explicitly.
        stem = obj.name.split(":", 1)[0]
        if "." in stem and stem.rsplit(".", 1)[1].isdigit():
            stem = stem.rsplit(".", 1)[0]
        lower = stem.lower()
        if not stem.endswith("_ALPHA") or not ("hair" in lower or "combination" in lower) \
                or stem in hidden or getattr(obj, "hide_render", False):
            return None
        if any(word in lower for word in ("hairband", "hair_band", "hairtie", "hair_tie", "scrunch", "bandana")):
            return None
        return stem

    def natural_primary_colours(obj, stem):
        # M_Hair carries actual hair tints in the supplied models. S_Hair
        # scalp layers can instead contain red/white control values.
        if not stem.lower().startswith("m_hair"):
            return False
        attributes = getattr(obj.data, "color_attributes", None)
        layer = attributes.get("ColorMap") if attributes is not None else None
        if layer is None:
            attributes = getattr(obj.data, "vertex_colors", None)
            layer = attributes.get("ColorMap") if attributes is not None else None
        if layer is None or len(layer.data) == 0:
            return False
        step = max(1, (len(layer.data) + 2047) // 2048)
        meaningful = total = 0
        for index in range(0, len(layer.data), step):
            item = layer.data[index]
            values = getattr(item, "color_srgb", None)
            if values is None:
                values = item.color
            rgb = tuple(float(value) for value in values[:3])
            if len(rgb) != 3 or not all(math.isfinite(value) for value in rgb):
                continue
            total += 1
            white = min(rgb) >= 0.85 and max(rgb) - min(rgb) <= 0.1
            primary_control = max(rgb) >= 0.95 and sum(value < 0.05 for value in rgb) >= 2
            if not white and not primary_control:
                meaningful += 1
        return total > 0 and meaningful * 2 >= total

    def owned_ramp(shader):
        base = only_link(shader.inputs["Base Color"])
        if base is None:
            return None
        node = base.from_node
        if node.type == "VALTORGB" and getattr(node, "label", "") == "Gorilla hair colour fallback":
            return node
        if node.type == "VECT_MATH" and node.operation == "SCALE":
            source = only_link(node.inputs[0])
            if source is not None and source.from_node.type == "VALTORGB" \
                    and getattr(source.from_node, "label", "") == "Gorilla hair colour":
                return source.from_node
        return None

    def restore_automatic_albedo(shader, ramp, tree):
        if not automatic or getattr(ramp, "label", "") != "Gorilla hair colour fallback":
            return False
        shade = only_link(ramp.inputs[0])
        if shade is not None and shade.from_node.type == "RGBTOBW":
            original = only_link(shade.from_node.inputs[0])
            if original is not None and original.from_node.type == "TEX_IMAGE" \
                    and usable_image(original.from_node.image):
                tree.links.new(original.from_socket, shader.inputs["Base Color"])
                return True
            if original is not None and original.from_node.type == "TEX_IMAGE":
                # An older cached fallback may still sample a missing image.
                # Keep its tint, without treating failed pixels as luminance.
                tree.links.remove(shade)
                ramp.inputs[0].default_value = 0.5
        elif shade is None:
            # Linking the private ramp never changes the BSDF socket's default,
            # so it still holds the model's original constant albedo.
            value = shader.inputs["Base Color"].default_value
            if len(value) >= 3 and not (min(value[:3]) >= 0.5 and max(value[:3]) - min(value[:3]) <= 0.05):
                for link in list(shader.inputs["Base Color"].links):
                    tree.links.remove(link)
                return True
        return False

    def generic_hair_fallback(obj, index, slot, material, stem):
        identity = obj.as_pointer(), index
        if identity in fallback_done:
            return
        shader = active_shader(material)
        if shader is None:
            return
        base = only_link(shader.inputs["Base Color"])
        # Native RAI repair and the renderer's own finished colour ramps must
        # not receive a second tint. Unknown procedural shaders stay intact.
        natural = automatic and natural_primary_colours(obj, stem)
        if base is not None and base.from_node.type not in ("VERTEX_COLOR", "TEX_IMAGE") \
                and not (natural and owned_ramp(shader) is not None):
            return
        if base is not None and base.from_node.type == "VERTEX_COLOR" \
                and base.from_node.layer_name != "ColorMap":
            return
        if automatic and base is not None and base.from_node.type == "TEX_IMAGE" \
                and usable_image(base.from_node.image):
            fallback_done.add(identity)
            return  # An existing colour texture already supplies its natural tint.
        if natural and base is not None and base.from_node.type == "VERTEX_COLOR":
            fallback_done.add(identity)
            return
        if automatic and base is None:
            value = shader.inputs["Base Color"].default_value
            if len(value) >= 3 and not (min(value[:3]) >= 0.5 and max(value[:3]) - min(value[:3]) <= 0.05):
                fallback_done.add(identity)
                return  # Preserve an existing meaningful constant hair colour.

        # Always isolate this fallback to the verified hair object. The same
        # material, or even the same mesh datablock, may also be used by a body.
        copy = material.copy()
        try:
            target = active_shader(copy)
            tree = copy.node_tree
            source = only_link(target.inputs["Base Color"])
            if natural:
                vertex = tree.nodes.new("ShaderNodeVertexColor")
                vertex.layer_name = "ColorMap"
                vertex.label = "Gorilla natural hair colour"
                tree.links.new(vertex.outputs["Color"], target.inputs["Base Color"])
            else:
                ramp = tree.nodes.new("ShaderNodeValToRGB")
                ramp.label = "Gorilla hair colour fallback"
                ramp.color_ramp.elements[0].color = dark + (1.0,)
                ramp.color_ramp.elements[1].color = light + (1.0,)
                if source is not None and not (source.from_node.type == "TEX_IMAGE" and not usable_image(source.from_node.image)):
                    shade = tree.nodes.new("ShaderNodeRGBToBW")
                    tree.links.new(source.from_socket, shade.inputs[0])
                    tree.links.new(shade.outputs[0], ramp.inputs[0])
                else:
                    ramp.inputs[0].default_value = 0.5
                tree.links.new(ramp.outputs[0], target.inputs["Base Color"])
            previous_link = slot.link
            try:
                slot.link = "OBJECT"
                slot.material = copy
            except (RuntimeError, ReferenceError):
                slot.link = previous_link
                raise
            repaired.add(copy.as_pointer())
            fallback_done.add(identity)
            print("  hair colour repaired on identified mesh:", stem,
                  "(existing coverage retained)", flush=True)
        except (AttributeError, KeyError, TypeError, ValueError, RuntimeError, ReferenceError):
            materials = getattr(getattr(bpy, "data", None), "materials", None)
            if materials is not None:
                try:
                    materials.remove(copy)
                except (RuntimeError, ReferenceError):
                    pass
            raise

    def native_hair_shader(material):
        if not material.use_nodes or material.node_tree is None:
            return None
        tree = material.node_tree
        for shader in tree.nodes:
            if shader.type != "BSDF_PRINCIPLED":
                continue
            base = only_link(shader.inputs["Base Color"])
            alpha = only_link(shader.inputs["Alpha"])
            rough = only_link(shader.inputs["Roughness"])
            if base is None or alpha is None or rough is None:
                continue
            vertex = base.from_node
            separate = alpha.from_node
            if vertex.type != "VERTEX_COLOR" or vertex.layer_name != "ColorMap" \
                    or separate.type != "SEPARATE_COLOR" or getattr(separate, "mode", "RGB") != "RGB":
                continue
            if not same_socket(base.from_socket, vertex.outputs["Color"]) \
                    or not same_socket(alpha.from_socket, separate.outputs[1]) \
                    or rough.from_node != separate or not same_socket(rough.from_socket, separate.outputs[0]):
                continue
            image_link = only_link(separate.inputs[0])
            if image_link is None or image_link.from_node.type != "TEX_IMAGE" \
                    or not usable_image(image_link.from_node.image) \
                    or not same_socket(image_link.from_socket, image_link.from_node.outputs["Color"]):
                continue
            active_output = any(node.type == "OUTPUT_MATERIAL" and getattr(node, "is_active_output", True)
                                and any(link.from_node == shader for link in node.inputs["Surface"].links)
                                for node in tree.nodes)
            if active_output:
                return tree, shader, separate
        return None

    def repair(scene, unused_depsgraph=None):
        for obj in scene.objects:
            if obj.type != "MESH":
                continue
            slots = getattr(obj, "material_slots", None)
            materials = [slot.material for slot in slots] if slots is not None else obj.data.materials
            stem = hair_mesh_stem(obj)
            for index, material in enumerate(materials):
                if material is None:
                    continue
                identity = material.as_pointer()
                if identity in repaired:
                    continue
                created = []
                restore = None
                try:
                    active = active_shader(material)
                    own = owned_ramp(active) if active is not None else None
                    if own is not None:
                        if restore_automatic_albedo(active, own, material.node_tree):
                            repaired.add(identity)
                        elif stem is not None and slots is not None and automatic and natural_primary_colours(obj, stem):
                            generic_hair_fallback(obj, index, slots[index], material, stem)
                        else:
                            own.color_ramp.elements[0].color = dark + (1.0,)
                            own.color_ramp.elements[1].color = light + (1.0,)
                            repaired.add(identity)
                        continue
                    found = native_hair_shader(material)
                    if found is None:
                        if stem is not None and slots is not None:
                            generic_hair_fallback(obj, index, slots[index], material, stem)
                        continue
                    tree, shader, separate = found
                    base_input = shader.inputs["Base Color"]
                    restore = only_link(base_input).from_socket, base_input
                    ramp = tree.nodes.new("ShaderNodeValToRGB")
                    created.append(ramp)
                    ramp.label = "Gorilla hair colour"
                    ramp.color_ramp.elements[0].color = dark + (1.0,)
                    ramp.color_ramp.elements[1].color = light + (1.0,)
                    variation = tree.nodes.new("ShaderNodeMath")
                    created.append(variation)
                    variation.operation = "MULTIPLY_ADD"
                    variation.inputs[1].default_value = 0.7
                    variation.inputs[2].default_value = 0.65
                    colour = tree.nodes.new("ShaderNodeVectorMath")
                    created.append(colour)
                    colour.operation = "SCALE"
                    tree.links.new(separate.outputs[0], ramp.inputs[0])
                    tree.links.new(separate.outputs[2], variation.inputs[0])
                    tree.links.new(ramp.outputs[0], colour.inputs[0])
                    tree.links.new(variation.outputs[0], colour.inputs["Scale"])
                    # Commit last: the original coverage, roughness, normal and
                    # other material connections retain their exact sources.
                    tree.links.new(colour.outputs[0], shader.inputs["Base Color"])
                    repaired.add(identity)
                    print("  hair colour repaired from native RAI shader:", material.name, flush=True)
                except (AttributeError, KeyError, TypeError, ValueError, RuntimeError, ReferenceError) as error:
                    # An unfamiliar shader must retain its original material.
                    for node in reversed(created):
                        try:
                            material.node_tree.nodes.remove(node)
                        except (RuntimeError, ReferenceError):
                            pass
                    if restore is not None:
                        try:
                            current = only_link(restore[1])
                            if current is None or not same_socket(current.from_socket, restore[0]):
                                material.node_tree.links.new(*restore)
                        except (RuntimeError, ReferenceError):
                            pass
                    if identity not in warned:
                        warned.add(identity)
                        print("  hair colour repair skipped:", material.name, str(error), flush=True)

    repair._gorilla_hair_colour_repair = True
    repair = handlers.persistent(repair)
    handlers.render_pre.append(repair)


_register_hair_colour_repair()


# BEGIN GENERATED GEOMETRY BUNDLE
_GEOMETRY_BUNDLE = {
    'gorilla_geometry': ('f032e583d1f13f6e6258edd05b6bef48cb135a465ec0665806cada7a42e44988',
        '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'
    ),
    'gorilla_geometry_cache': ('1946b7b91dbd34893055e6c080fe761af903de7fae3ee88b05053503db01c564',
        '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'
        'mNWvWvUBQawgOPU/sv4T9g=='
    ),
    'gorilla_body_masks': ('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'
    ),
}
# END GENERATED GEOMETRY BUNDLE


def _load_geometry_bundle():
    """Keep the existing eleven-file updater format self-contained."""
    import base64
    import hashlib
    import types
    import zlib

    for name, (digest, encoded) in _GEOMETRY_BUNDLE.items():
        source = zlib.decompress(base64.b64decode(encoded))
        if hashlib.sha256(source).hexdigest() != digest:
            raise RuntimeError("Invalid bundled geometry helper: " + name)
        module = types.ModuleType(name)
        module.__file__ = __file__
        sys.modules[name] = module
        exec(compile(source, __file__ + ":" + name, "exec"), module.__dict__)


def _renderer_options():
    try:
        return dict(value.split("=", 1) for value in sys.argv[sys.argv.index("--") + 4:] if "=" in value)
    except ValueError:
        return {}


def _original_renderer():
    import gr_render  # noqa: E402,F401 -- the original compiled renderer


def _run_renderer():
    if os.environ.get("GORILLA_GEOMETRY_REVISION") != "1":
        return _original_renderer()
    _load_geometry_bundle()
    import gorilla_geometry
    import gorilla_geometry_cache
    import gorilla_body_masks

    options = _renderer_options()
    # Registration does not import a character or change its meshes. The
    # original renderer remains responsible for loading the selected sources.
    try:
        registration = gorilla_geometry.register(options)
    except Exception as error:
        print("  geometry visibility: registration unavailable; keeping the original renderer:", str(error), flush=True)
        return _original_renderer()
    if not registration.installed:
        return _original_renderer()
    try:
        gorilla_body_masks.register(options, registration)
    except Exception as error:
        print("  body hide masks: registration unavailable; keeping geometry visibility:", str(error), flush=True)
    return gorilla_geometry_cache.run_with_geometry_cache(options, _original_renderer)


_run_renderer()
