# Optional selection-loop companion (experimental)

This companion supports the exact shipped **MovingRenders 4.21** binary with
SHA256 `b752da3c9a9dc48dc377972a7183db0194c2b3b551074f7d0f0c9766432bf75c`.
It lets roster selection loop the clean motion prefix of a newly made
**Play once + flash freeze** movie. The original match-card behavior stays frozen.

This is a separate, opt-in plugin. Use its installer with the game closed. The
installer places `WWE2K26_SelectionLoop.dlp` and
`WWE2K26_SelectionLoop.cfg` in the game's `Plugins` directory. The distributed
configuration has `enabled 0`; explicit installation writes `enabled 1`.
Restart the game after installing, removing or changing renders/settings.
`WWE2K26_SelectionLoop.log` reports activation or the reason it stayed disabled.

Make and install a new freeze render in the updated Studio. Studio writes a
`<movie>.gmr.selection-loop.json` sidecar with the completed movie's SHA256 and
explicit motion count. Both files must be in `Plugins/GorillaRenders`. Existing
movies without a matching sidecar keep their original behavior. The companion
does not guess motion length from old movies or change any GMR bytes.

The freeze producer retains a shortened motion prefix before its eleven-frame
flash/hold tail. Selection loops **that prefix**, which is at least two frames;
it cannot restore the omitted remainder of the full taunt. Extra-ID aliases
keep original behavior: the initial companion supports only the unique active
primary wrestler ID. The supported original itself has a maximum of 256 GMR
records. Missing, ambiguous, changed or unsupported sources keep native behavior.

The companion never loads or initializes the original plugin, calls its
authorization functions, changes its files or replaces its Bink caller-routing
stub. Its worker waits up to 120 seconds for the original's already-installed
Bink hook, then verifies the original file, image marker, hook/trampoline bytes,
active source record and exact selection segment. It builds an immutable policy
map before activating a MinHook detour on the selection-only range consumer.
The callback performs no file access, allocation or locks. Source-directory or
native mapping changes disable the overrides until restart. A callback already
in progress when a change is detected finishes using its immutable snapshot.

MinHook 1.3.4 is vendored unchanged with its **BSD 2-Clause** license. The original
25 consumer bytes are replayed before the pure lookup. The assembly preserves
GPRs, FLAGS and XMM0–5, including upper R12 bits, and supplies Windows shadow
space and stack alignment. It returns to the original consumer continuation.
The callback cannot throw; the mid-function jump stub does not introduce an
exception/unwind bridge. Production activation requires its full byte guards.

Build with Zig 0.13.0 (its bundled Windows x64 GNU headers/libraries are used):

```
python3 tools/build_selection_loop_plugin.py --zig /path/to/zig --output /path/to/output
g++ -std=c++17 -O2 src/SelectionLoopPlugin/selection_policy.cpp src/SelectionLoopPlugin/tests/policy_tests.cpp -o /tmp/selection-policy-tests
/tmp/selection-policy-tests
```

The output includes `package.json`, disabled configuration, the companion and a
Windows `SelectionLoopPolicyTests.exe` host. Cross-compiling is not Windows or
game execution. Portable policy tests and independent native-instruction
sentinel tests can run on Linux x64. Actual CakeHook loading, Bink playback and
the Windows detour need an in-game test before this can be described as proven
in the game.
