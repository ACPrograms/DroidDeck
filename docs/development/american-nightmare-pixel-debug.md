# American Nightmare on Pixel: rendering workaround and remaining limits

Local engineering results from 2026-10-11/12. Steam game 202750 was tested on a
Pixel 11 Pro Fold using the Pixel Lab runtime. These observations do not establish
compatibility across the Pixel family or a fix for the system resets.

## Rendering result

The bright dotted triangle edges over textured rocks, boards and the player are
not an application wireframe setting: traced input and driver pipelines all used
`VK_POLYGON_MODE_FILL`, with no dynamic polygon mode. The original affected path
used 2x MSAA, RGBA16F color and D24S8 depth. The game's minimum UI AA setting and
`d3d11.disableMsaa` did not disable its D3D9 multisampling.

A real single-sample D3D9 comparison removed the visible bright triangle edges.
Trial 16 verified 114 input and 114 driver pipelines, all single-sample. Trial 17
retained correct meshes/textures through movement and turning, with 119 pipelines
in each phase, all single-sample. This isolates a useful workaround to the MSAA
path; it does not identify the precise faulty operation or prove a vendor bug.
The override also changes effective surface descriptors, so the application may
select different AA-aware shader/rendering paths.

`tools/dxvk/patches/0004-d3d9-force-single-sample.patch` adds an explicit per-game
`d3d9.forceSingleSample` option, default **False**. After validating the original
sample request, D3D9 surface normalization selects `D3DMULTISAMPLE_NONE` and quality
0. GetDesc, image creation, resolves and sample-dependent state use this effective
description. This intentionally disables MSAA, per-sample masks and
alpha-to-coverage. It is a compatibility override, not transparent MSAA emulation
or a universal PowerVR default. Existing low32, BC-format, present synchronization
and tiler workarounds must remain intact.

## Independent HUD correction

The diagnostic frametimes HUD bound an R8_UINT storage texel buffer to a shader
whose SPIR-V image format was Unknown. This required formatless buffer writes
which the device did not report for that format, producing VUID 07029.
`0003-hud-explicit-r8-texel-format.patch` declares `r8ui` to match the actual host
view. No capabilities are fabricated. The build now compiles and validates the
actual shader, and checks binding 3 for DimBuffer/storage/R8ui. The original shader
fails that contract; the patched shader passes. Real-game trial 15 used the new
DLLs, removed 07029, and retained the original triangle edges. This HUD bug is
separate from the rendering workaround, and is not a proven reset cause.

VUID 06479 for color-format comparison descriptors remains under investigation.
The D3D9 shader uses a conditional comparison path; static validation output alone
does not prove that the offending branch executes. No warning suppression or
false format-feature claims are proposed. Trial 14's LOG+FAIL with a duplicate
limit blocked only the first message of each kind, not every invalid API call;
subsequent trials used logging only. Do not describe these runs as fully blocked
validation or GPU-assisted validation.

## Responsiveness, performance and stability

Paired screenshots prove movement and camera changes, rather than relying on an
input receipt. Trial 17 completed eight samples without a black viewport. Its
cold sample was 3.2 FPS; later recognized samples were 19.1-29.9 FPS, with roughly
2.54 CPU-core equivalents. Camera/workload, capture overhead and thermals were
not controlled; this is not a reliable A/B benchmark.

The planned eight-minute trial 17 stopped at a focus/session guard after the last
confirmed sample at 315.6 seconds. Steam owned focus; the target game was still
available for a single client quit. Its temporary settings were successfully
restored, and the phone boot counter remained 12. The focus change's cause is
unknown. The run is **incomplete**, not an eight-minute pass or a demonstrated
game crash. Controls, focus ownership and sustained stability still need broader
coverage beyond these short tests.

### Final production configuration and user confirmation

The clean production configuration was persisted only for game 202750 through
the application's per-game environment UI. It selects the tested DXVK component
and sets `d3d9.forceSingleSample = True`, retaining the existing low32, BC,
presentation-sync and `dxvk.tilerMode = False` workarounds. Shared configuration
and other games were unchanged. Shader audit layers, validation and the
frametimes HUD are not enabled in this production configuration.

A final automated production trial loaded the existing saved game, verified
turning/movement with five paired non-black screenshots, and observed 115 seconds
without the bright triangle edges or another phone reset. It then quit normally;
the saved per-game environment remained unchanged. This is bounded observation,
not a long-duration stability or controlled performance benchmark.

On 2026-10-11 (America/Toronto), the device owner separately confirmed that this
stage worked and authorized publication. No duration, frame rate or full-game
completion was supplied with that confirmation. It is additional user validation
of the stage, not a claim that all Pixel devices or the GNOC resets are fixed.

## Native guards, resets and diagnostic controls

The Pixel base is e589e3ae21959c9c74c490d917cebf256acc8ac8. PR561's 9e6fe314 and
3c363910 guards were integrated in an isolated worktree; 26 selected production-C
regressions and a full APK/runtime verification passed. Pixel AHB support remains
present. The existing Lab app was updated without uninstalling or resetting its
Steam account. This APK preceded a second system reset, so the native guards are
not a verified reset fix.

Two resets changed the boot counter 10 -> 11 -> 12. After personal unlock, narrow
kernel/firmware projections confirmed GNOC/access-control details for the first,
and `reset,warm,gnoc_error` with a fresh GNOC numeric record for the second. The
specific faulty component/operation and any relation to rendering are unknown.
`tools/pixel_reset_diagnostics.py` exports only fixed fault categories/numeric
fields, lock state, boot counter and entry times; never arbitrary log text or
account data. Seven host tests cover its gating and narrow output.

Private opt-in diagnostics captured maintenance5 inline modules, which the older
shim path missed. Materialization passed nine actual-C ASan/UBSan cases; benign
pipeline-chain planning passed twelve. Real-game NoContraction, scaled vertex
conversion and force-clip rewrites were actually applied and did not remove the
edges. None is a production default. Sixty-two valid bounded, owned offscreen GPU
cases passed exact pixel readback, fences and validation, covering interpolation,
seams, perspective meshes, interface widening, scaled fetch, HDR/depth prepasses,
real clipping and nonconstant depth. Invalid initial fixtures are excluded. These
passing controls do not exonerate every driver/rendering path.

## Reproducibility and recovery

The runtime artifact supplies both architectures, exact DLL hashes, public patched
DXVK sources, all four patches, wrapper and build recipe. It contains no game
assets, bytecode, saves, account files, screenshots or raw device logs. Source
patches apply cleanly to pinned DXVK revision 25ca63f17f34bdc05a39873ee63907d3cbbfa030.
All five runtime DLL targets were built for both architectures.

Stop further load tests after a full reset. Wait for personal unlock and inspect a
fresh narrow projection before another launch. Temporary profile restoration checks
baseline/current hashes and unchanged shared/other-game settings; it never rolls
back saved game progress. Persistent selection must affect only 202750 and leave
private shader audit layers, validation settings and frametimes HUD disabled.
Routine Heartbeat must not launch games or automatically resume this engineering.

## Submission scope

These runtime tools are submitted against the experimental PowerVR `dev` branch,
following its merged presentation-semaphore PR
<https://github.com/wallentx/DroidDeck/pull/3>. They do not change the managed pack
manifest/checksums or enable the opt-in overrides globally. Shipping the built
runtime still needs maintainer review and a new managed-pack release.

The compositor ownership safeguards are independently proposed in
<https://github.com/Droid-Deck/DroidDeck/pull/561>. Those native changes were present
in the integrated Pixel Lab APK used for the game experiments, but are not part
of this runtime PR's diff. The original BC emulation is a prerequisite supplied by
the PowerVR branch. This submission supplies source and reproducibility tools,
not an APK, proprietary driver, game data or private diagnostic archive.
