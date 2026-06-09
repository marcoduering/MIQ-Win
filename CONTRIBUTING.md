# Contributing to MIQ-Win

## Building

**Requirements:**
- .NET SDK 8.0 or later (for the reference build and packaging)
- Windows (WPF and QuickLook require it)

```powershell
# Release build (Release is the default configuration)
./Package.ps1

# Release build with a version stamp
./Package.ps1 -Version 1.2.3
```

The `net8.0-windows` `MIQ.Core` project is a reference/testing build only; the actual plugin runs on .NET Framework 4.6.2 (`net462`), whose runtime ships with Windows.

The script builds both projects and produces `dist/QuickLook.Plugin.MIQ.qlplugin`.


**Source linking:** The parser and renderer source files in `MIQ.Core/` are also
compiled directly into the plugin via `<Compile Include>` in
`QuickLook.Plugin.MIQ.csproj`. Changes to those files affect both builds.

---

## Vendored Binaries

Two pre-built binaries are committed to the repository. When updating them,
verify the SHA-256 hashes below match, then update this file with the new hash
and version.

### `QuickLook.Plugin.MIQ/native/libdeflate.dll`

| Field | Value |
|---|---|
| **Source** | https://github.com/ebiggers/libdeflate |
| **License** | MIT |
| **SHA-256** | `60b414b5932e57f88ebf53cd3010adf8c042391d40006efd588cf463eeb5f29b` |

This is the Windows x64 shared library build of libdeflate, used for fast
gzip decompression (~15–50× faster than the .NET Framework built-in `GZipStream`
for single-shot decompression with a known output size). The plugin falls back
transparently to the managed `GZipStream` path if the DLL cannot be loaded.

To build a replacement from source:
```bash
cmake -B build -DLIBDEFLATE_BUILD_SHARED_LIB=ON -DLIBDEFLATE_BUILD_STATIC_LIB=OFF
cmake --build build --config Release
```
Copy the resulting `libdeflate.dll` into `QuickLook.Plugin.MIQ/native/`.

### `lib/QuickLook.Common.dll`

| Field | Value |
|---|---|
| **Source** | https://github.com/QL-Win/QuickLook (release v4.5.0) |
| **License** | GPL-3.0 |
| **Version** | 4.5.0.0 |
| **SHA-256** | `09b68a365d1ca47114be2240e8d90fd1f221edf6a7fe6acf5ebde291cde7ae52` |

This is the host API used to register the plugin with QuickLook (the
`IViewer` interface, `ContextObject`, theme constants, etc.). It is included
for building only — QuickLook provides its own copy at runtime, so the DLL is
excluded from the packaged `.qlplugin`. When a new QuickLook release changes
the API, replace this file with the corresponding DLL from the QuickLook
release artifacts and update the hash here.

---

## Design Notes

### File extension mapping

`.mgh.gz` and `.mgz` are both FreeSurfer compressed MGH volumes — different
extensions for the same format. Both map to `MiqFileKind.Mgz` (compressed),
while `.mgh` maps to `MiqFileKind.Mgh` (uncompressed). This is intentional:
do not "fix" the dual mapping.

### Orientation: sform vs qform

NIfTI files carry two optional coordinate transforms — sform and qform. The
parser prefers sform (`SformCode > 0`) and falls back to qform
(`QformCode > 0`) via `OrientationFrame.FromQuaternion()`. When neither is
present (both codes == 0) the orientation frame is null and the slice labels
show as unknown.

### View orientation (stored / neurological / radiological)

The `Orientation` key in `MIQ.settings.ini` (carried on
`MiqRenderingOptions.Orientation`) selects how axes are presented:

- **stored** (default) — render axes exactly as stored.
- **neurological** — canonical anatomical view, patient-LEFT on the viewer's
  left (coronal/axial).
- **radiological** — same, but patient-LEFT on the viewer's right.

`MiqVolume.PlanFor(plane)` is the single resolver — it returns a
`SlicePlan(SliceAxis, HAxis, VAxis, HReversed, VReversed, Labels)` and every
per-plane path goes through it (`PrepareSlice`, `AxesFor`, `SliceCount`, plus
the interactive control's crosshair and click-navigation). `SliceConfig.Coordinate`
honors `HReversed`; the control inverts both flags to map a click back to its
storage voxel, so the two must stay in sync.

Two rules when touching this code:

1. **Reoriented-mode edge labels are hardcoded** per (plane, mode) in
   `ReorientedPlan` — do *not* derive them from `OrientationFrame.DisplayLabels`.
   Those describe the *stored* axes and would lie in a reoriented view (a RAS
   volume's stored sagittal reads `P|A`, the reverse of the canonical `A|P`).
2. **Sagittal is identical in both reoriented modes** (Anterior on the viewer's
   left, no in-plane R/L); coronal and axial differ only by the horizontal R/L
   flip. Files lacking an `OrientationFrame` always fall back to stored.

### RGB rendering

`rgb24` / `rgba32` voxels are rendered in colour rather than collapsed to
grayscale. A finished slice is a `SliceImage` — a union of `GrayscaleImage` or
`RgbImage` (port of MIQCore's `SliceImage` enum). `RgbImage` holds interleaved
3-byte RGB (composited via WPF `PixelFormats.Rgb24`). Two rules mirror macOS
MIQ:

1. **Alpha is dropped** — `ReadRgb` copies exactly 3 bytes per voxel, guarded by
   the literal `3` (not bytes-per-voxel), so `rgba32`'s 4th byte is never read.
   The preview is opaque.
2. **RGB bypasses intensity windowing** — the bytes are already display-ready.
   RGB slices are excluded from the pooled percentile window (`CenterSlices` /
   `SharedWindow` only pool `Gray` values); `Finalize` builds the `RgbImage`
   without applying any `IntensityWindow`.

### Why no `System.Drawing`

The QuickLook host ships `System.Drawing.Primitives` in its own process, which
conflicts with any `System.Drawing` the plugin loads. All rendering is done with
pure WPF `DrawingContext` / `BitmapSource`.

### Progressive (volume-0-first) loading

A multi-volume NIfTI is previewed volume-0-first so the first pixels appear without
reading the whole file — a large win on slow or network storage. Phase 1
(`MiqParser.ParsePartial`) returns just the header + **volume 0** as a partial
`MiqImage` (`IsPartial = true`). The initial view is byte-identical to a full load —
the intensity window pools volume-0 center slices in both modes — so only the
scrubber is deferred.

This applies to **every** multi-volume `.nii.gz` (decompress volume 0 only, via the
streaming `GunzipPartial`), and to uncompressed `.nii` above
`MiqParser.PartialLoadThreshold` (150 MB) via `ParseNiftiFirstVolume`. Below the
threshold, or for 3-D files (where volume 0 *is* the whole payload, so there's
nothing to defer), the full parse runs up front and the scrubber is live
immediately. See *Known limitations* for the permanent (`ExpansionBlocked`) variant
used when the full data can't be held.

**Phase 2 is lazy.** The full load does *not* run automatically — that made flicking
through previews stutter, since every glance kicked off a background decompress
(and native libdeflate can't be cancelled mid-call, so orphaned loads piled up).
Instead the volume row renders in a `Loadable` state (`WpfPreviewRenderer.ScrubMode`)
with an interactive track, and the **first scrub gesture** (Alt+wheel or a click on
the track) invokes an `onExpandRequested` callback wired from `Plugin.cs`. That runs
the full `Parse` on a background `Task` and swaps the result in via `ExpandVolume`,
enabling the scrubber. Just flicking through files to view volume 0 therefore
triggers zero background work. The expansion still runs at `ThreadPriority.BelowNormal`
and passes the viewer's `_cts` token to `MiqParser.Parse` (whose managed-gzip loop
and uncompressed chunked read check it between chunks), so navigating away mid-load
abandons it promptly rather than blocking the next preview.

### Known limitations

- **Files larger than ~2 GB in memory:** voxel data is held in a single
  `byte[]`, capped at `Array.MaxLength` (≈2 GB, `MiqParser.MaxArrayBytes`). A
  **4-D** series above that cap is previewed **volume 0 only**: `MiqParser` loads
  just the header + first volume and sets `MiqImage.ExpansionBlocked`, which
  suppresses background expansion and replaces the volume scrubber with a *"first
  volume only (too large for 4-D)"* notice. The design **assumes a single volume
  always fits in 2 GB.** Uncompressed NIfTI takes this path via
  `ParseNiftiFirstVolume`; compressed `.nii.gz` via the existing single-volume
  fast path (made permanent when the decompressed ISIZE exceeds the cap). A lone
  volume that genuinely exceeds the cap (an assumed-impossible 3-D > 2 GB), or
  any file whose uncompressed size tops the **4 GB** ceiling, falls back to a
  clear error.
- **gzip ISIZE is mod 2^32:** compressed files whose uncompressed size exceeds
  4 GB report ISIZE = 0 and are handled by a streaming fallback, but the
  resulting allocation may still exhaust memory.
- **NRRD ASCII / hex / bzip2 encoding:** not supported by design. Re-save with
  `encoding: raw` or `encoding: gzip`.
- **Detached NRRD headers (`.nhdr`):** out of scope; use self-contained `.nrrd`.
- **RGBA NIfTI alpha:** `rgb24` and `rgba32` render in full colour; for
  `rgba32` the alpha channel is dropped and the preview is opaque.
- **NIfTI-2 with >4 D dimensions:** only the first four axes are previewed.

### Unsafe code

`AllowUnsafeBlocks` is enabled in both projects. The only unsafe code is two
bit-reinterpretation helpers in `MiqCompat.cs` — `Int32BitsToSingle` and
`Int64BitsToDouble`. These are necessary because
`BitConverter.Int32BitsToSingle` / `Int64BitsToDouble` are not available in
the .NET Framework 4.6.2 BCL. The implementations are deterministic and
carry no pointer-arithmetic risk.
