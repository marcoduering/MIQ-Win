# Contributing to MIQ-Win

## Building

Requires the .NET 8 SDK and Windows (WPF and QuickLook need it).

```powershell
./Package.ps1                  # Release build → dist/QuickLook.Plugin.MIQ.qlplugin
./Package.ps1 -Version 1.2.3   # ...with a version stamp
```

`MIQ.Core` (`net8.0-windows`) is a reference/testing build only; the shipping plugin
targets .NET Framework 4.6.2 (`net462`), whose runtime comes with Windows.
`Package.ps1` builds both.

**Source linking:** `MIQ.Core/`'s parser and renderer sources are *also* compiled
into the plugin via `<Compile Include>` in `QuickLook.Plugin.MIQ.csproj` — changes
there affect both builds.

---

## Vendored Binaries

Two pre-built binaries are committed. When replacing either, verify the SHA-256
below, then update the hash and version here.

Both are MIT, as are the NuGet BCL backports that ship in the package. Their
notices live in [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md), which
`Package.ps1` puts into the `.qlplugin` alongside `LICENSE`. **Adding or updating
a redistributed binary means updating that file too** — MIT permits redistribution
only if the notice travels with the copy.

### `QuickLook.Plugin.MIQ/native/libdeflate.dll`

| Field | Value |
|---|---|
| **Source** | https://github.com/ebiggers/libdeflate |
| **License** | MIT (Eric Biggers; Google LLC) |
| **Version** | 1.19+, locally built, otherwise unidentified — see below |
| **SHA-256** | `60b414b5932e57f88ebf53cd3010adf8c042391d40006efd588cf463eeb5f29b` |

Windows x64 shared library, used for fast gzip decompression (~15–50× faster than
.NET Framework's `GZipStream` for single-shot decompression with a known output
size). Falls back transparently to managed `GZipStream` if it can't be loaded.

The exact version is **unrecoverable** — investigated 2026-08-10, don't repeat it:
no version resource or embedded version string; the hash matches no official
1.19–1.24 release binary (so it was built locally); and the 21 exports are identical
across 1.19–1.24, giving only a floor of 1.19 (`libdeflate_alloc_compressor_ex` /
`libdeflate_alloc_decompressor_ex`, added there). **Record the version next time this
is rebuilt** — without it, checking the binary against an advisory is guesswork.

Rebuild from source, then copy `libdeflate.dll` into `QuickLook.Plugin.MIQ/native/`:
```bash
cmake -B build -DLIBDEFLATE_BUILD_SHARED_LIB=ON -DLIBDEFLATE_BUILD_STATIC_LIB=OFF
cmake --build build --config Release
```

### `lib/QuickLook.Common.dll`

| Field | Value |
|---|---|
| **Source** | https://github.com/QL-Win/QuickLook (release v4.5.0) |
| **License** | **MIT** — *not* GPL-3.0 |
| **Version** | 4.5.0.0 |
| **SHA-256** | `09b68a365d1ca47114be2240e8d90fd1f221edf6a7fe6acf5ebde291cde7ae52` |

The host API (`IViewer`, `ContextObject`, theme constants). Build-time only —
QuickLook provides its own copy at runtime, so it is excluded from the packaged
`.qlplugin`. Replace it from the QuickLook release artifacts when the API changes.

**License:** this table said GPL-3.0 until 2026-08-10. That was wrong, and worth
knowing in case it was copied elsewhere, since it makes an MIT plugin look like a
copyleft violation. The QuickLook *application* is GPL-3.0, but the
`QuickLook.Common/` directory carries its own MIT `LICENSE` and is published to
NuGet as MIT (4.5.0, `net462`, matching this DLL). No GPL code is linked or
redistributed. Re-verify if a future release changes that `LICENSE`.

**Kept vendored deliberately** (settled 2026-08-10): a `PackageReference` would
compile equivalently, but vendoring is why builds need no installed QuickLook and
no restore, and the hash pins the exact bits. The DLL is never redistributed, so a
swap would solve nothing. Accepted cost — dependency scanners don't see committed
binaries, so advisories won't surface automatically.

---

## Design Notes

### File extension mapping

`.mgh.gz` and `.mgz` are the same format (compressed FreeSurfer MGH) and both map
to `MiqFileKind.Mgz`; `.mgh` maps to `MiqFileKind.Mgh`. Intentional — don't "fix"
the dual mapping.

### Orientation: sform vs qform

The parser prefers NIfTI's sform (`SformCode > 0`) and falls back to qform
(`QformCode > 0`) via `OrientationFrame.FromQuaternion()`. With both codes `0` the
orientation frame is null and slice labels show as unknown.

### View orientation (stored / neurological / radiological)

The `Orientation` key in `MIQ.settings.ini` (carried on
`MiqRenderingOptions.Orientation`) selects how axes are presented:

- **stored** (default) — render axes exactly as stored.
- **neurological** — canonical anatomical view, patient-LEFT on the viewer's left
  (coronal/axial).
- **radiological** — same, but patient-LEFT on the viewer's right.

`MiqVolume.PlanFor(plane)` is the single resolver, returning
`SlicePlan(SliceAxis, HAxis, VAxis, HReversed, VReversed, Labels)`; every per-plane
path goes through it (`PrepareSlice`, `AxesFor`, `SliceCount`, and the interactive
control's crosshair and click-navigation). `SliceConfig.Coordinate` honors
`HReversed`; the control inverts both flags to map a click back to its storage
voxel, so the two must stay in sync.

Two rules when touching this code:

1. **Reoriented-mode edge labels are hardcoded** per (plane, mode) in
   `ReorientedPlan` — do *not* derive them from `OrientationFrame.DisplayLabels`,
   which describe the *stored* axes and would lie in a reoriented view (a RAS
   volume's stored sagittal reads `P|A`, the reverse of the canonical `A|P`).
2. **Sagittal is identical in both reoriented modes** (Anterior on the left, no
   in-plane R/L); coronal and axial differ only by the horizontal R/L flip. Files
   with no `OrientationFrame` always fall back to stored.

### RGB rendering

`rgb24` / `rgba32` voxels render in colour rather than collapsing to grayscale. A
finished slice is a `SliceImage` — a union of `GrayscaleImage` or `RgbImage` (port
of MIQCore's `SliceImage` enum); `RgbImage` holds interleaved 3-byte RGB, composited
via WPF `PixelFormats.Rgb24`. Two rules mirror macOS MIQ:

1. **Alpha is dropped** — `ReadRgb` copies exactly 3 bytes per voxel, guarded by the
   literal `3` (not bytes-per-voxel), so `rgba32`'s 4th byte is never read. The
   preview is opaque.
2. **RGB bypasses intensity windowing** — the bytes are already display-ready. RGB
   slices are excluded from the pooled percentile window (`CenterSlices` /
   `SharedWindow` pool only `Gray` values), and `Finalize` builds the `RgbImage`
   without applying any `IntensityWindow`.

### Why no `System.Drawing`

The QuickLook host ships `System.Drawing.Primitives` in its own process, which
conflicts with any `System.Drawing` the plugin loads. All rendering is pure WPF
`DrawingContext` / `BitmapSource`.

### Progressive (volume-0-first) loading

Multi-volume NIfTI is previewed volume-0-first, so the first pixels appear without
reading the whole file — a large win on slow or network storage. Phase 1
(`MiqParser.ParsePartial`) returns header + **volume 0** as a partial `MiqImage`
(`IsPartial = true`). The initial view is byte-identical to a full load (the
intensity window pools volume-0 center slices either way), so only the scrubber is
deferred.

This covers **every** multi-volume `.nii.gz` (volume 0 only, via the streaming
`GunzipPartial`) and uncompressed `.nii` above `MiqParser.PartialLoadThreshold`
(150 MB) via `ParseNiftiFirstVolume`. Below the threshold, or for 3-D files (where
volume 0 *is* the whole payload), the full parse runs up front and the scrubber is
live immediately. See *Known limitations* for the permanent (`ExpansionBlocked`)
variant.

**Phase 2 is lazy.** The full load does *not* run automatically — that made flicking
through previews stutter, since every glance kicked off a background decompress, and
native libdeflate can't be cancelled mid-call so orphaned loads piled up. Instead the
volume row renders `Loadable` (`WpfPreviewRenderer.ScrubMode`) with an interactive
track, and the **first scrub gesture** (Alt+wheel or a track click) invokes an
`onExpandRequested` callback wired from `Plugin.cs`, which runs the full `Parse` on a
background `Task` and swaps the result in via `ExpandVolume`. Glancing at volume 0
therefore triggers zero background work. Expansion runs at
`ThreadPriority.BelowNormal` and passes the viewer's `_cts` token to
`MiqParser.Parse` (its managed-gzip loop and uncompressed chunked read check between
chunks), so navigating away abandons it promptly.

### Known limitations

- **Files larger than ~2 GB in memory:** voxel data is one `byte[]`, capped at
  `Array.MaxLength` (≈2 GB, `MiqParser.MaxArrayBytes`). A **4-D** series above the
  cap previews **volume 0 only**: `MiqParser` loads header + first volume and sets
  `MiqImage.ExpansionBlocked`, which suppresses expansion and replaces the scrubber
  with a *"first volume only (too large for 4-D)"* notice. Uncompressed NIfTI takes
  this path via `ParseNiftiFirstVolume`; `.nii.gz` via the single-volume fast path
  (made permanent when decompressed ISIZE exceeds the cap). The design **assumes a
  single volume always fits in 2 GB** — a lone volume over the cap (an
  assumed-impossible 3-D > 2 GB), or any file over the **4 GB** ceiling, falls back
  to a clear error.
- **gzip ISIZE is mod 2^32:** files whose uncompressed size exceeds 4 GB report
  ISIZE = 0 and use a streaming fallback, but the allocation may still exhaust memory.
- **NRRD ASCII / hex / bzip2 encoding:** unsupported by design. Re-save with
  `encoding: raw` or `encoding: gzip`.
- **Detached NRRD headers (`.nhdr`):** out of scope; use self-contained `.nrrd`.
- **RGBA NIfTI alpha:** dropped — `rgba32` renders in colour but opaque.
- **NIfTI-2 with >4 dimensions:** only the first four axes are previewed.

### Unsafe code

`AllowUnsafeBlocks` is on in both projects for exactly two bit-reinterpretation
helpers in `MiqCompat.cs`, `Int32BitsToSingle` and `Int64BitsToDouble`, standing in
for `BitConverter.Int32BitsToSingle` / `Int64BitsToDouble` which the .NET Framework
4.6.2 BCL lacks. Both are deterministic and carry no pointer-arithmetic risk.
