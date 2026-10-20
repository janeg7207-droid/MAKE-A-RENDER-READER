"""Migrate engine-owned model caches once when the gear importer changes.

The original engine keys model.blend by MCD metadata, so importer changes need
an additional revision. Source models are never modified. Legacy cache files
remain recoverable beside the rebuilt file.
"""

import builtins
import gzip
import contextlib
import hashlib
import importlib
import json
import os
from pathlib import Path, PureWindowsPath
import shutil
import sys
import uuid
import zlib


REVISION = "1"
SIDECAR = "model.geometry.json"
LOCKFILE = ".gorilla-geometry.lock"
JOURNAL = "model.geometry.pending.json"
_MAX_JSON = 16 * 1024 * 1024
_NATIVE_BLEND_PROOFS = {}


class GeometryCacheValidationError(RuntimeError):
    """The native operation finished, but auxiliary cache validation failed."""



def _file_stamp(path):
    path = Path(path)
    stat = path.stat()
    if not path.is_file():
        raise ValueError("not a file")
    return {"path": os.path.normcase(str(path.resolve())),
            "size": stat.st_size, "mtime_ns": stat.st_mtime_ns}


def _implementation():
    paths = {Path(__file__).resolve()}
    geometry = sys.modules.get("gorilla_geometry")
    if geometry is not None and getattr(geometry, "__file__", None):
        paths.add(Path(geometry.__file__).resolve())
    result = []
    for path in sorted(paths, key=str):
        result.append({"path": os.path.normcase(str(path)),
                       "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    return result


def _read_json(path, limit=_MAX_JSON):
    path = Path(path)
    if path.is_symlink() or path.stat().st_size > limit:
        raise ValueError("unsupported cache metadata")
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def _mesh_manifest(path):
    try:
        rows = _read_json(path)
        return isinstance(rows, list) and 0 < len(rows) <= 100000 and all(
            isinstance(row, dict) and isinstance(row.get("name"), str)
            and isinstance(row.get("full"), str)
            and isinstance(row.get("verts"), int) and not isinstance(row["verts"], bool)
            and row["verts"] >= 0 and isinstance(row.get("materials"), list)
            and all(isinstance(value, str) for value in row["materials"])
            for row in rows)
    except (OSError, ValueError, TypeError):
        return False


def _blend_file(path):
    try:
        path = Path(path)
        if path.is_symlink() or not path.is_file():
            return False
        with path.open("rb") as stream:
            header = stream.read(12)
        if header.startswith(b"BLENDER"):
            return True
        if header.startswith(b"\x1f\x8b"):
            # Older compressed .blend files use gzip. Read only the actual
            # Blender header, without inflating the complete model in Python.
            with gzip.open(path, "rb") as stream:
                return stream.read(12).startswith(b"BLENDER")
        if not header.startswith(b"\x28\xb5\x2f\xfd"):
            return False
        # Modern Blender uses Zstandard. Its native library reader supports
        # the exact format on Windows and Linux without another Python/DLL
        # dependency. A matching save/load event already proved a fresh file.
        stamp = _file_stamp(path)
        if _NATIVE_BLEND_PROOFS.get(stamp["path"]) == stamp:
            return True
        bpy = sys.modules.get("bpy")
        libraries = getattr(getattr(bpy, "data", None), "libraries", None)
        if libraries is None or not callable(getattr(libraries, "load", None)):
            return False
        with libraries.load(str(path), link=False):
            pass  # Request no datablocks; the active scene stays unchanged.
        _NATIVE_BLEND_PROOFS[stamp["path"]] = stamp
        return True
    except (OSError, EOFError, RuntimeError, ValueError, zlib.error):
        return False


@contextlib.contextmanager
def _native_file_events(context):
    bpy = sys.modules.get("bpy")
    handlers = getattr(getattr(bpy, "app", None), "handlers", None)
    target = _cache_target(context["blend"])
    installed = []
    def remember(*unused):
        try:
            filename = getattr(getattr(bpy, "data", None), "filepath", "")
            if filename and _cache_target(Path(filename)) == target:
                stamp = _file_stamp(context["blend"])
                _NATIVE_BLEND_PROOFS[stamp["path"]] = stamp
        except (OSError, ValueError):
            pass
    if handlers is not None:
        persistent = getattr(handlers, "persistent", None)
        callback = persistent(remember) if callable(persistent) else remember
        for name in ("save_post", "load_post"):
            collection = getattr(handlers, name, None)
            if collection is not None:
                collection.append(callback)
                installed.append((collection, callback))
    try:
        yield
    finally:
        for collection, callback in installed:
            if callback in collection:
                collection.remove(callback)


def _base_model(character):
    local = character / "BaseModel" / "baseModel.mcd"
    if local.is_file():
        return local
    # This is the original renderer's exact fallback, including its explicit
    # GORILLA_STOCK override. Do not search or choose a body by name similarity.
    stock = os.environ.get("GORILLA_STOCK")
    if not stock:
        stock = str(importlib.import_module("gs_paths").STOCK_ROOT)
    return Path(stock) / "Characters" / character.name / "BaseModel" / "baseModel.mcd"


def _context(options):
    try:
        if os.environ.get("GORILLA_GEOMETRY_REVISION") != REVISION or not isinstance(options, dict):
            return None
        character = Path(options["char_dir"])
        blend = Path(options["blend"])
        attire_text = str(options["attire_mcd"]).replace("\\", "/")
        attire = Path(attire_text)
        if not character.is_absolute() or not blend.is_absolute() or not character.is_dir():
            return None
        if (blend.name != "model.blend" or blend.is_symlink()
                or attire.is_absolute() or PureWindowsPath(attire_text).is_absolute()
                or ".." in attire.parts or not attire.parts):
            return None
        if not blend.parent.is_dir():
            return None
        # Alias/junction layouts are legitimate native sources. Preserve the
        # selected relative path and fingerprint its exact resolved target.
        source = character / attire
        base = _base_model(character)
        if not source.is_file() or not base.is_file():
            return None
        sidecar = blend.parent / SIDECAR
        if sidecar.is_symlink():
            return None
        return {"blend": blend, "sidecar": sidecar,
                "identity": {"schema": 1, "revision": REVISION,
                    "character": os.path.normcase(str(character.resolve())),
                    "attire": attire_text, "sources": [_file_stamp(source), _file_stamp(base)],
                    "implementation": _implementation()}}
    except (KeyError, OSError, ValueError, TypeError, ImportError, AttributeError):
        return None


def _current(context):
    try:
        marker = _read_json(context["sidecar"], 1024 * 1024)
        return (marker.get("identity") == context["identity"]
                and marker.get("blend") == _file_stamp(context["blend"])
                and _blend_file(context["blend"]))
    except (OSError, ValueError, TypeError, AttributeError):
        return False


@contextlib.contextmanager
def _cache_lock(folder):
    path = folder / LOCKFILE
    if path.is_symlink():
        raise RuntimeError("The wrestler model cache lock is not a regular file.")
    with path.open("a+b") as stream:
        stream.seek(0, 2)
        if stream.tell() == 0:
            stream.write(b"\0")
            stream.flush()
        stream.seek(0)
        locked = False
        try:
            if os.name == "nt":
                import msvcrt
                try:
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as error:
                    raise RuntimeError("This wrestler's cached model is being rebuilt in another Studio process. Try again when it finishes.") from error
            else:
                import fcntl
                try:
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                except OSError as error:
                    raise RuntimeError("This wrestler's cached model is being rebuilt in another Studio process. Try again when it finishes.") from error
            locked = True
            yield
        finally:
            if locked:
                stream.seek(0)
                if os.name == "nt":
                    msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def _copy_exclusive(source, destination):
    created = False
    try:
        with Path(source).open("rb") as reader, Path(destination).open("xb") as writer:
            created = True
            shutil.copyfileobj(reader, writer)
        shutil.copystat(source, destination)
    except BaseException:
        if created:
            Path(destination).unlink(missing_ok=True)
        raise


def _copy_or_link(source, destination):
    try:
        os.link(source, destination)
    except FileExistsError:
        raise
    except OSError:
        _copy_exclusive(source, destination)


def _backup(blend):
    path = blend.with_name("model.pre-geometry-v" + REVISION + ".blend")
    if path.exists() or path.is_symlink():
        path = blend.with_name("model.pre-geometry-v" + REVISION + "-" + uuid.uuid4().hex + ".blend")
    _copy_or_link(blend, path)
    return path


def _restore(blend, backup):
    temporary = blend.with_name(".geometry-restore-" + uuid.uuid4().hex + ".blend")
    try:
        _copy_or_link(backup, temporary)
        os.replace(temporary, blend)
    finally:
        if temporary.exists():
            temporary.unlink()


def _rollback(blend, backup):
    if backup is not None:
        _restore(blend, backup)
    elif blend.exists() and not blend.is_symlink():
        # No previous cache existed. A failed fresh save must not become the
        # native engine's next cached input. Other working files are retained.
        blend.unlink()


def _owner_identity(identity):
    return {"revision": identity["revision"], "character": identity["character"],
            "attire": identity["attire"],
            "sources": [item["path"] for item in identity["sources"]]}


def _cache_target(blend):
    return os.path.normcase(str(blend.resolve()))


def _sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        while True:
            block = stream.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def _atomic_json(path, payload, prefix):
    temporary = path.with_name(prefix + uuid.uuid4().hex + ".json")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(payload, stream, sort_keys=True, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def _begin_journal(context, backup):
    path = context["blend"].parent / JOURNAL
    if path.exists() or path.is_symlink():
        raise RuntimeError("An earlier wrestler model cache rebuild still needs recovery.")
    payload = {"schema": 1, "operation": "gorilla-gear-cache-rebuild",
               "target": _cache_target(context["blend"]),
               "owner": _owner_identity(context["identity"]),
               "backup": backup.name if backup is not None else None,
               "backup_stamp": _file_stamp(backup) if backup is not None else None,
               "backup_sha256": _sha256(backup) if backup is not None else None}
    manifest = context["blend"].parent / "meshes.json"
    if manifest.exists():
        if not _mesh_manifest(manifest):
            raise GeometryCacheValidationError("The existing wrestler mesh cache is not valid native metadata.")
        saved = manifest.with_name("meshes.pre-geometry-v" + REVISION + "-" + uuid.uuid4().hex + ".json")
        _copy_exclusive(manifest, saved)
        payload["manifest"] = {"state": "present", "backup": saved.name,
                               "stamp": _file_stamp(saved), "sha256": _sha256(saved)}
    else:
        payload["manifest"] = {"state": "absent"}
    _atomic_json(path, payload, ".geometry-pending-")
    context["pending"] = payload


def _clear_journal(context):
    (context["blend"].parent / JOURNAL).unlink(missing_ok=True)


def _has_journal(context):
    path = context["blend"].parent / JOURNAL
    return path.exists() or path.is_symlink()


def _restore_manifest(context, pending):
    target = context["blend"].parent / "meshes.json"
    record = pending.get("manifest")
    if record is None:
        # Revision 1 before paired metadata backups. Do not reuse a manifest
        # that might have come from the interrupted new model.
        target.unlink(missing_ok=True)
        return
    if not isinstance(record, dict):
        raise ValueError("the pending mesh metadata backup is invalid")
    if record.get("state") == "absent":
        if target.is_symlink():
            raise ValueError("the pending mesh metadata target is not a regular file")
        target.unlink(missing_ok=True)
        return
    name = record.get("backup")
    prefix = "meshes.pre-geometry-v" + REVISION + "-"
    token = name[len(prefix):-5] if isinstance(name, str) and name.startswith(prefix) and name.endswith(".json") else ""
    if (record.get("state") != "present" or len(token) != 32
            or any(value not in "0123456789abcdef" for value in token)
            or Path(name).name != name or PureWindowsPath(name).name != name):
        raise ValueError("the pending mesh metadata backup path is invalid")
    backup = target.parent / name
    if (not _mesh_manifest(backup) or _file_stamp(backup) != record.get("stamp")
            or _sha256(backup) != record.get("sha256")):
        raise ValueError("the preserved mesh metadata backup changed or is unavailable")
    temporary = target.with_name(".geometry-meshes-" + uuid.uuid4().hex + ".json")
    try:
        _copy_exclusive(backup, temporary)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def _recover_pending(context):
    path = context["blend"].parent / JOURNAL
    if not _has_journal(context):
        return
    try:
        pending = _read_json(path, 1024 * 1024)
        if (not isinstance(pending, dict) or pending.get("schema") != 1
                or pending.get("operation") != "gorilla-gear-cache-rebuild"
                or pending.get("target") != _cache_target(context["blend"])
                or pending.get("owner") != _owner_identity(context["identity"])):
            raise ValueError("the pending rebuild belongs to different cache sources")
        name = pending.get("backup")
        backup = None
        if name is not None:
            prefix = "model.pre-geometry-v" + REVISION
            suffix = name[len(prefix):] if isinstance(name, str) and name.startswith(prefix) else ""
            valid = suffix == ".blend" or (suffix.startswith("-") and suffix.endswith(".blend")
                and len(suffix) == 39 and all(value in "0123456789abcdef" for value in suffix[1:-6]))
            if (not isinstance(name, str) or not valid or Path(name).name != name
                    or PureWindowsPath(name).name != name):
                raise ValueError("the pending rebuild has an invalid backup path")
            backup = context["blend"].parent / name
        elif pending.get("backup_stamp") is not None or pending.get("backup_sha256") is not None:
            raise ValueError("the pending rebuild has inconsistent backup metadata")
        # The marker is committed before the journal is cleared. A killed
        # process in that short gap already has a proven successful new cache.
        if _current(context):
            _clear_journal(context)
            return
        if backup is not None:
            if (not _blend_file(backup) or _file_stamp(backup) != pending.get("backup_stamp")
                    or _sha256(backup) != pending.get("backup_sha256")):
                raise ValueError("the preserved cache backup changed or is unavailable")
        _restore_manifest(context, pending)
        _rollback(context["blend"], backup)
        _clean_manifest_temporaries(context["blend"].parent)
        _clear_journal(context)
        print("Recovered the wrestler model cache after an interrupted rebuild.", flush=True)
    except (OSError, ValueError, TypeError, KeyError) as error:
        raise RuntimeError("The interrupted wrestler model cache rebuild could not be recovered safely: " + str(error)) from error


def _clean_manifest_temporaries(folder):
    # Only our fixed-prefix, UUID-named metadata files in this owned cache are
    # eligible. The scan remains bounded after repeated interrupted attempts.
    for index, path in enumerate(folder.glob(".geometry-meshes-*.json")):
        if index >= 128:
            break
        token = path.name[len(".geometry-meshes-"):-5]
        if len(token) == 32 and all(value in "0123456789abcdef" for value in token) and not path.is_symlink():
            path.unlink(missing_ok=True)


@contextlib.contextmanager
def _atomic_mesh_manifest(context):
    # The native meshes mode writes with json.dump(..., open(path, "w")) and
    # then SystemExit(0). Keep that exact schema, but publish only a complete
    # JSON file. Otherwise a killed first import could leave malformed JSON
    # that the engine reads before it gets back to this recovery helper.
    target = context["blend"].parent / "meshes.json"
    target_name = os.path.normcase(str(target.resolve()))
    original_open = builtins.open
    writers = []
    successful = False
    def opened(file, mode="r", *args, **kwargs):
        if mode not in ("w", "wt") or not isinstance(file, (str, bytes, os.PathLike)):
            return original_open(file, mode, *args, **kwargs)
        path = Path(os.fsdecode(file))
        if os.path.normcase(str(path.resolve())) != target_name:
            return original_open(file, mode, *args, **kwargs)
        if path.is_symlink():
            raise RuntimeError("The wrestler mesh cache is not a regular file.")
        temporary = target.with_name(".geometry-meshes-" + uuid.uuid4().hex + ".json")
        stream = original_open(temporary, "x" if mode == "w" else "xt", *args, **kwargs)
        writers.append((stream, temporary))
        return stream
    builtins.open = opened
    try:
        yield
        successful = True
    except SystemExit as error:
        successful = error.code is None or error.code == 0
        raise
    finally:
        builtins.open = original_open
        try:
            for stream, temporary in writers:
                if not stream.closed:
                    stream.flush()
                    os.fsync(stream.fileno())
                    stream.close()
                if successful:
                    if not _mesh_manifest(temporary):
                        raise GeometryCacheValidationError("The rebuilt wrestler mesh cache was not saved successfully.")
                    os.replace(temporary, target)
        finally:
            for stream, temporary in writers:
                if not stream.closed:
                    stream.close()
                temporary.unlink(missing_ok=True)


def _run_original(context, callback):
    original_print = builtins.print
    markers = context.setdefault("success_markers", [])
    def printed(*values, **options):
        if values and values[0] == "MESHES_DONE" and options.get("file", sys.stdout) is sys.stdout:
            markers.append((values, dict(options)))
            return
        return original_print(*values, **options)
    builtins.print = printed
    try:
        with _native_file_events(context), _atomic_mesh_manifest(context):
            return callback()
    finally:
        builtins.print = original_print


def _publish_success(context):
    for values, options in context.pop("success_markers", []):
        print(*values, **options)


def _run_current(context, callback):
    try:
        result = _run_original(context, callback)
    except SystemExit as error:
        if error.code is None or error.code == 0:
            _publish_success(context)
        raise
    _publish_success(context)
    return result


def _rollback_transaction(context, backup):
    pending = context.get("pending")
    if pending is not None:
        _restore_manifest(context, pending)
    _rollback(context["blend"], backup)
    _clear_journal(context)


def _validation_failed(context, backup):
    # Native save/load success and a complete manifest are valuable artifacts.
    # An extra revision-marker failure must never delete a working model and
    # leave meshes.json pointing at a nonexistent model.blend. Keep this pair
    # uncommitted and with its recovery journal; suppress MESHES_DONE so the
    # original engine reports the import failure instead of starting rest on
    # an incomplete transaction.
    if _blend_file(context["blend"]):
        manifest = context["blend"].parent / "meshes.json"
        if manifest.exists() and not manifest.is_symlink() and not _mesh_manifest(manifest):
            # A missing manifest makes the native engine regenerate metadata.
            # An existing malformed manifest would fail before recovery runs.
            manifest.unlink()
        return
    _rollback_transaction(context, backup)


def _commit(context, options):
    blend = context["blend"]
    current = _context(options)
    if current is None or current["identity"] != context["identity"]:
        raise RuntimeError("The wrestler model sources changed while its cache was being rebuilt. Try the preview again.")
    if not _blend_file(blend) or not _mesh_manifest(blend.parent / "meshes.json"):
        raise RuntimeError("The rebuilt wrestler model cache was not saved successfully.")
    payload = {"identity": context["identity"], "blend": _file_stamp(blend)}
    _atomic_json(context["sidecar"], payload, ".geometry-marker-")
    _clear_journal(context)


def run_with_geometry_cache(options, run_original_callable):
    """Run the native renderer, refreshing only a proven engine-owned cache.

    The callback's return value and SystemExit(0) are preserved. Failed imports
    or saves restore the previous cache; source MCDs, textures and outputs stay
    untouched. A current cache is reused without another import.
    """
    if not callable(run_original_callable):
        raise TypeError("run_original_callable must be callable")
    context = _context(options)
    if context is None:
        return run_original_callable()
    if _current(context) and not _has_journal(context):
        return _run_current(context, run_original_callable)
    blend = context["blend"]
    if not _has_journal(context) and blend.exists() and (not _blend_file(blend) or not _mesh_manifest(blend.parent / "meshes.json")):
        return run_original_callable()
    with _cache_lock(blend.parent):
        # Another process could have finished before this process acquired the
        # lock. Recheck both source identity and the committed cache revision.
        latest = _context(options)
        if latest is None or latest["identity"] != context["identity"]:
            raise RuntimeError("The wrestler model sources changed before its cache rebuild. Try again.")
        _recover_pending(context)
        if _current(context):
            return _run_current(context, run_original_callable)
        backup = None
        if blend.exists():
            if not _blend_file(blend) or not _mesh_manifest(blend.parent / "meshes.json"):
                return run_original_callable()
            backup = _backup(blend)
        _begin_journal(context, backup)
        if backup is not None:
            try:
                blend.unlink()
            except BaseException:
                # The old cache is still present; remove the pending marker
                # instead of continuing into a guessed fresh import.
                _clear_journal(context)
                raise
            print("Rebuilding the cached wrestler model for gear visibility…", flush=True)
        try:
            result = _run_original(context, run_original_callable)
        except SystemExit as error:
            if error.code is None or error.code == 0:
                try:
                    _commit(context, options)
                except BaseException:
                    _validation_failed(context, backup)
                    raise
                _publish_success(context)
                raise
            _rollback_transaction(context, backup)
            raise
        except GeometryCacheValidationError:
            _validation_failed(context, backup)
            raise
        except BaseException:
            _rollback_transaction(context, backup)
            raise
        try:
            _commit(context, options)
        except BaseException:
            _validation_failed(context, backup)
            raise
        _publish_success(context)
        return result
