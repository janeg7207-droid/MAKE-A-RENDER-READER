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
    automatic = os.environ.get("GORILLA_HAIR_CHOICE", "").strip().lower() == "auto"
    hidden = set(options.get("hide", "").split(","))

    def only_link(socket):
        links = socket.links
        return links[0] if len(links) == 1 else None

    def same_socket(left, right):
        return left == right

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
                    and original.from_node.image is not None:
                tree.links.new(original.from_socket, shader.inputs["Base Color"])
                return True
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
                and base.from_node.image is not None:
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
                if source is not None and not (source.from_node.type == "TEX_IMAGE" and source.from_node.image is None):
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
                    or image_link.from_node.image is None \
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

import gr_render  # noqa: E402,F401 -- the original compiled renderer
