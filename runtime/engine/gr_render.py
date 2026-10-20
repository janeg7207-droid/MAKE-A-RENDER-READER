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


# BEGIN GENERATED GEOMETRY BUNDLE
_GEOMETRY_BUNDLE = {
    'gorilla_geometry': ('f032e583d1f13f6e6258edd05b6bef48cb135a465ec0665806cada7a42e44988',
        'eNq1G2tz3Lbx+/0KlPkQMqFpSXbcRs3NVLFlR9MoyshOpjOqhsMjcXeMeOSV4Em+evzfuw8ABF8npWk1iUUSi8Vise+FPM+7lk2S'
        'l0J+TNJGXL5+IzZSrcW2ru5lmZSpFEmZiXWeSXEv63yZy0zkJcDm91KsZFKLbS5TqaLZ7Kos9qJZS1EmNJpvtlXdyPpLJWrZ7OoS'
        'pqpqVwPOqs5kLaRqkkWRq7VUAhYom7zZR0JcJjApT4pZmQAtIa7byI/P0mpXNkLBkilOQLJwMUZVLcWurGUBU2GRVJYwsPhNpo0S'
        'WSXKqolm72S1kU29D4W6gw0/yHy1bgC9WidbKe7knnFu9OrwVsNWJKwuNlXGO1/LWkYzz/NmM94dANXJ3rwsEiVfvTRvy12ZNlVV'
        'KPNhnah1kS/MK/9yPlRbWSdNVdt3O1Pt7WOz30o1W9bVRqRVUcAW86pUGplAlmXNblvI2ez6/NeL9xdXP4m5OJ5dnv3j4vKXy/jX'
        '8+sPF6/P38PHk/jo6Aj/n8VvL85/fIPfPs0E/Hi1vM8V4PVOhbeq6rwoknil+RfbwZCBt0mzHgXkwzZgwOeTb14dAIw1hIbPy0x+'
        'HAVHCTVQuOFJoJhGNeSySFZqFJRHNBhKWw4CPQppB0PDp02ynWASjMS7FycWtCrGCaWBcPZ5Fl+e/XT27vxN/MPFm3M4iyEo6lsM'
        'ugi64s0u6MSvZQoaANDtyfueO+SFfEKCuStwORRoWRgN8YJZzAoNVK9y1YAQwvECzp+qEuRolsmliNOkrMo8TQofsQWntCvWaxDU'
        'CD9GZVVvUtAB33yoZVLgg/2QLJR5X/ITYYMfs05eNnIla/8+KXZSL5MvBUhcCeYCDBKPhGIBqqXHiZQkV1L8imPndV3Vvvc9AMik'
        'FASv1blq2L6BcOE5RrB3dx9aASOSPU2CIWybpHd8rDwApoNMUktjIUs9FIg/zXn0EIEfwHxpg3h9fnn2M88AgwXUEqVJkwKpYMOM'
        '3Bl6kRaJp07mJ6J/4dj1KJDCAFHeyI3K/y2RnJeHScmV2IL5XMIRthSgG0DT/+Lk2SJvwMSqfIVmnBa01MAcZjKwVXO7XYsH5v2D'
        'teNALK50JL6ba1h4OPr4Vv+0iMYJP+tyUJOhRLVrFFK+g2VfnBhKW95FyXYry8ylBigBOxst9o1kpwJM84q8aVBx+9MRSj2ALHQE'
        'iB1AtHj1UpYp6JivoZuK4P0giDJJA16i0jz3rHSBpmSxcYI+aGYIzFRb0E+WOS1lYAVOXdYBYIQegWi9PH//gwcOkb6uZONru37T'
        '2vNbkkzjGXrM5T2Q0ptvX4jvQaqRGWkFjh60SIB9yMuVSHeqAR8EEgJK0+yB2qYSP++bNVgO+C9JQVwVePMzB1dNqJ4ta7BVAila'
        '5MDfPfBUgbxJmaHUGX+PcpvpY02KokrJKkUDwfoErvvUbhnpCUgi4XNI5KFUak6wQvgBsg7GiWtswz+7XO3K6h9gn6YDKfC1QwuN'
        'JwodXxN0UfGyMPF2oDj0taM9mkbGfiu+A1WCVbs6daPXvD2oXCMbGGjnTUs0IevHFU9CiYYSDyxLmiQy+Ii1gzWeTGLfQdxwXHIb'
        'osAGhiWdoT+AW4cqDnZr/NtB2tGrl+KfnXXwB+CTcu+n64TJAvnwjo5PXrz85tWf//JtskjBJngkPQRizKqD+g/QTkGRQ7kRcoxD'
        'bg05voe2DIU0aZq8pqcK4u3ae9rSjulB9bJ44difNL9j/Poz4BMoxoiVo6WCDvSQDQTWPTg2seQlxVfC9/Hfobh/LU4C8fy5eNFj'
        'wdQ29DdCRB/lx1RuG+GfNWBDF7uGnVgo/i73+ukD2HL92Hq5UFwBFcuietCv13IJiQjshd7dIMihgh0L+ZU2m0O10/Ae5X6awDzj'
        'zIl9qfFDIfIHkZEoFnKVpPvnIJHlCmAxjsKIBJIhZ+kxR/ahbsMos1bMwadDj0YwRjA6dDxEZ1/sRnU81tnUGxqh3JCTWr0pjgTY'
        'oYTiIQdPtWsE7Qb9WcKBIZqkBTibO7sx42fGCJu5NhipRArHj2PgvevkAbC2EYMODADtTV+iQVkN3+bMzJ4xBVzj5hPk+OXj+nY4'
        'nHxSSHkYdYT5KodASKqL9gkh13TY5azKqQ+DBR1t+5/rlMlX0Ysov9pSBj4WoEGElN6hpVKRLO/zGsIXNFjeu6vrix9/PIvff7h6'
        '/XewrLYSEPFTDPnZDhO5leJFvCAi2Pj66upDu3V0DiDhwDdYg8ngBXAgzvK6e4It+EBSB9h0vvZblZdOskYbCsAVvDawGMcMlkZn'
        'cfzqOL5OymwfX9VNVcYnR8ffekFLDwr+9EKWFlzsewC9xIwV8S7MS7RJM2+QRxh8uVrmwEGE7lnrwwtP7FAD41x0n6OUPplU9qhP'
        '23yHtzwxRmyaw2/kMtkVTXxGI8/xEz/ykgMtcdJ4Yk3ofmH0wUFXNaVM7/UDlx4O6FBoNCktMOqPr8GsyvrnuvqoFYc0DILevIlj'
        'X8liiV4oBxudFJBxJ1vABBruKBmARHELwu8GELk8mD1zFgLGwr5ruxYeb9DJsfAL51d0sKM2T2Px+8QQNjsBV9Q0+F8l9UqF4quv'
        '7h7wqSekXKCZ9zYXEQWDqZ2Z3e2bNJfwBWOU00j/oPR0e0qmdnrt1IemT4tFFh7KmO1ZKBbbff/INBhsMssxENCvEGh8+hz0QA0i'
        'PE3z3AWBBdCbbvfdz2xIDSMNH837Q43sqc0rhAJZQa8dF0ZjFD0WBQnU26RQveG0qNTUWAynVNKgAiXubQ21kGy8Flyt4lSxm/c8'
        'jcu2wJFhxK95DzGMSlY9AdYfTQjuEnU6Ijs8EiUZiI1G13XFkP+DMRLC1CWdVP5UeBAm62mhWBY7tdYhS0svZhqaXqeWaOKBDlfE'
        'fD7gy6gC6uSkG7A48y3G8dmU7ExOfnRxnSPNJohqtw5CtNk6ew9Z/0KYocCOuzrCUet8UHntZFntGOEhd0KRIE9/rHqGFUhrKdnm'
        'LGRRlSslmgpC4ixfUlRkgmi3jsbxPxDIkRevzx+D2aHsCzcaCr8ASxLy5MBJxIgNuAN81ehApI7Hs2hjd3nezRFEyh7Wwzx2M4TI'
        'O7u+PPvwy/W59xR29JpXwIKMtqCPFKuxabUBohtbvKXEQcl/7TB8dFlkWlCGR5rK49PbQQT/Kc841Vly/U7nyjj9s2WH/hD8N/uw'
        'LTid6DAuOPQac6UEi3Zd4odHx0e8gLOjMBysOvyiZAEND2Da3+fyIQj+C6lbJ1T66yRstIhLUZavpEIfoBtpEVdDDF1BtJYfGcZN'
        'D6rW/mvLQ+hdr6zSOt82VY0ndXPbqd5RWQ12jDwLBSejpZDlboOtAun/O99qKQ3NefUZQFV9tIXJSrn1PJzGuRokahiJdQd0xW60'
        'mkH1OO4WjNTgOvU/XvZA2e+RujoJCy8FaBEbZg+7Uu22JFmZe0KaRKOVlP0P9dHWqafrgL3eyWFZsqLNGfywfaK3woVlxj1CdV/a'
        '6RTcYtlT6XE51xFsRNZf2JE+E6x9cvuvptgc2kYrow+djioLfTggr/9je6paqk33tLtV2yml30/A6rRMtazbxqjbNdOr7DYLiR0E'
        'XIobaKHtjuKvz538lkpNHa3sZNhGTUnSWlaijqJmao10h9TIMdp2ge1itfCmZXA6ygfqL8xttROL8xNwUqEbnduOBJA2BXmfVzvF'
        'Bc4bBL+lgERjkBBfduPTjku0U+ZO3bH/o/lq5I31FKeFZpnQ0uHkkDonPKdfNgMYHMMEItx0jdcpIFD2NQUjXB0crxuUMc7TSZF0'
        't2/WHQVGLk6j0c1xRjWA+p2p8TWIeL6Rj5WZusUupYY+tB9dul1+3xgFbQpIk2xwaRyTE4hyXKAj0elc9Avxi+Ja6q60t2DoYguc'
        '/5cQCu1qCg9XRbWAzAfj2G2RpFhQrfA6EFhfBxdkDvfgNAWGCXhl591lJN6vEwwD4JGSW7qDk0IsohGnmHSBYwajTD1GMKNtC9BJ'
        '8h0DoWmJTWbZSf0iyPj1eBwMptx47y49aro5ZQm/O+oUIdpYyURZc76fE73VLEKZGBBAles4tKuGog+BphggHjW9bo7czs64KKTi'
        'EcyYqALt7uYN8TB699DO7SfNvWFHGjGoBWiDZ1CfwKO/Mqn3MskLIIDuhOHFM5CtHLvLZVN3znZgB3SgbJmPYcLxU13yIJzXaNgx'
        'g6Bik8P4bQone25aJ+S4H8gykBh8ZGvM2zRdxV6CqK/ncPzJ6R8nfmYjlLTovKe/4V71w2mDDDfehfV55WnjLSAwkcigLqYvxKqO'
        'uU+PJ1RTz7/Us3EaNk46maEVH/ETWvceNrJUiVA7ug+w3BXdxISvDWSQHUESix2oJej7AmIG016aqE/4nj4sMNFgSNDjM77UzFd3'
        'OXi3jAsSEL35tNlhRZQ531pG8InFPm4rGtpE0p1CxzB6nvcD3i9hK6cvRIZahJ5xm8gUerX5/SvIb3KPlvSurB5K4Ctk2tL2mbhh'
        'kFC6+MnTt7xOxRFFUwoiEZnpV701fPs8KJ1wJWq0TEHYZ26J0eq4W2Vur51RJX+qefHu/Ow6fv3DFWQcWI12i81Yc0LstpdM8R4A'
        'SdRycH4TTeQugfIjSkXe6MKZMuXVDrF4M1UvHymAh08hPItnwEPnUoeTVdNJRvpMek0mEqb5aBOTCnsDBeUZup2CYkgfbDMdsx1d'
        'G3IGzV0Mw57jk7+E4vjkW/jnxVEwUujQxQ49nc0MFXkOFqesQN1YibkVX+Nl0EE8CNY3L3vBor3lO+/TPZ8jzd/aY57jLRo8YTaH'
        'uInZoyEd9fh5hcl4jxo6kB/iIWujdDj261yhRD+OpcdDM1zUj4FrXmrNnGClLPStLLqZ4JJjRGhytziLeg1aLqz8Y0hkBo2Qn4LQ'
        'HwfoinrQp79ju91i9cR+rfGZ2LEbMHf5P/t/Rc0TvqvvIhK+oO5cNAM70tDltWpXcBywkGTx8yd4CrZO1lHgfXPshkg2SyNV7GlT'
        'PNE86IifwaJbGI7vJ4FwxmzXOM0QzG1tjNT3h3N6gZ6dovsh6JBMRaf/2/Re8J7QdmuqPPBm5jqfWOziLV0m8rvuotN/AWk207v0'
        'm69RLTfgYH13VvBYv8bch9Fnpr3InHuSNm6i9/YyyQWjgQgGmKlwZtk4fxzx3JEtyHwSiGTR0+gQB/+agrG212Q45hdjF67NZZLR'
        'eI/OnANX8M2LQvoWbHAJ2qqU77W4NjtF0m7mmwCXG2eTNxBguL1VPHpNfDQoHYOMHI1hlKhR6F/G2ov+VBtxNoUf5RgRzh6R3aeI'
        'q9mxReO4eNztOlGEy4x3xXvY+W4Jsx3J6UsfOv5a8M1bzX6ndznQ4FmvcGTrr7YP7cUjf6HQsq6/7bZmM3a4ZnR4oE8idFhqbekc'
        'Zs/UGsLE294gHMnkezGTPiSrKn1+uFNNUu+ZaxvECLcRh0dnXIpT925zykIu8W68qap5wfTpMzLmS6jfBgxrLzzQnL/ZPyQiq67s'
        'NgLrizQp2YFbBS4pkUkSR28SGGTRYZnpqBujbfvpBocj8l2P0wFgw2zUx6cIPQSO7sAtxpncqhUgXrumeTSsZCKGCRylbr83Ee4c'
        'ez+OoAvrBzPMdjsjbHQwOU7f8TCtCjsGph3vqCsT2t5daMH8lgi68W71oQUJOGRvAe0fdLhlW2zV+o7ztVg7MY8h2cwctTrt1r3x'
        'gHhkGePubUl6NgWoC9odlrhccqMCy/eOkv4Hx0OE9g=='
    ),
    'gorilla_geometry_cache': ('9d1db3e981025f09c891a56da0f7d9133bcd2a146d6722f6606643f0ccc738f0',
        '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'
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
    return gorilla_geometry_cache.run_with_geometry_cache(options, _original_renderer)


_run_renderer()
