#!/usr/bin/env python3
"""Build the optional x64 Windows companion using Zig's bundled MinGW toolchain.

No original binary is read or changed. MinHook is pinned/vendored with its license.
Use --zig /absolute/path/to/zig; this script does not download tools or dependencies.
"""
import argparse
import hashlib
import json
from pathlib import Path
import os
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--zig', required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
source = root / 'src' / 'SelectionLoopPlugin'
vendor = source / 'vendor' / 'minhook'
provenance = json.loads((vendor / 'SOURCE.json').read_text())
for name, expected in provenance['sha256'].items():
    actual = hashlib.sha256((vendor / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit('Vendored MinHook integrity failure: ' + name)
environment = dict(os.environ)
environment.setdefault('ZIG_GLOBAL_CACHE_DIR', str(output / 'zig-global-cache'))
environment.setdefault('ZIG_LOCAL_CACHE_DIR', str(output / 'zig-local-cache'))
def run(arguments):
    subprocess.run([str(args.zig), *map(str, arguments)], cwd=root, env=environment, check=True)
common = ['-target', 'x86_64-windows-gnu', '-O2', '-fno-builtin-memchr',
          '-fno-builtin-wcschr', '-fno-builtin-wcsrchr']
objects = []
for name in ['hook.c', 'buffer.c', 'trampoline.c', 'hde/hde64.c']:
    obj = output / (name.replace('/', '_') + '.obj')
    run(['cc', *common, '-c', vendor / 'src' / name, '-o', obj])
    objects.append(obj)
dll = output / 'WWE2K26_SelectionLoop.dlp'
run(['c++', *common, '-std=c++17', '-fno-exceptions', '-fno-rtti', '-shared',
     source / 'plugin.cpp', source / 'selection_policy.cpp', source / 'selection_entry.S',
     *objects, '-lbcrypt', '-luser32', '-o', dll])
# This host is built for Windows; cross-compiling it is not a Windows runtime test.
run(['c++', *common, '-std=c++17', source / 'selection_policy.cpp',
     source / 'tests' / 'policy_tests.cpp', '-o', output / 'SelectionLoopPolicyTests.exe'])
shutil.copyfile(source / 'WWE2K26_SelectionLoop.cfg', output / 'WWE2K26_SelectionLoop.cfg')
shutil.copyfile(vendor / 'LICENSE.txt', output / 'MinHook-LICENSE.txt')
manifest = {
    'schema': 1,
    'filename': dll.name,
    'sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
    'size': dll.stat().st_size,
}
build_info = {
    'originalSha256': 'b752da3c9a9dc48dc377972a7183db0194c2b3b551074f7d0f0c9766432bf75c',
    'experimental': True,
    'architecture': 'x64',
    'selection': 'validated clean motion prefix loop',
    'matchCard': 'original native freeze unchanged',
    'defaultEnabled': False,
}
(output / 'package.json').write_text(json.dumps(manifest, indent=2) + '\n')
(output / 'BUILD_INFO.json').write_text(json.dumps(build_info, indent=2) + '\n')
print(json.dumps({'result': 'BUILT', 'dll': str(dll), 'bytes': dll.stat().st_size, 'sha256': manifest['sha256']}))
