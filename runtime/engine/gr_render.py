"""Forward performance options to the original compiled Blender renderer.

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

import gr_render  # noqa: E402,F401 -- the original compiled renderer
