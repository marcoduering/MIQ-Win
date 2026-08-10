# Third-Party Notices

MIQ-Win is distributed under the MIT License (see [`LICENSE`](./LICENSE)). It
redistributes the third-party components listed below, each under its own
license. The required copyright and permission notices are reproduced in full.

| Component | Version | License | Redistributed in |
|---|---|---|---|
| libdeflate | see note below | MIT | repository + `.qlplugin` package |
| QuickLook.Common | 4.5.0.0 | MIT | repository only (build-time reference) |
| System.Memory | 4.5.5 | MIT | `.qlplugin` package |
| System.Buffers | (transitive) | MIT | `.qlplugin` package |
| System.Numerics.Vectors | (transitive) | MIT | `.qlplugin` package |
| System.Runtime.CompilerServices.Unsafe | (transitive) | MIT | `.qlplugin` package |
| System.ValueTuple | 4.5.0 | MIT | `.qlplugin` package |

`Microsoft.NETFramework.ReferenceAssemblies` is not listed: it is a build-time
`PrivateAssets="all"` package whose reference assemblies are never redistributed.

No third-party *source code* is vendored into this repository. One table of
third-party **data** is transcribed into it — see
[Attribution: FreeSurfer colour table](#attribution-freesurfer-colour-table).

---

## libdeflate

Source: https://github.com/ebiggers/libdeflate

Redistributed as the pre-built Windows x64 shared library
`QuickLook.Plugin.MIQ/native/libdeflate.dll`, shipped beside the plugin.

> Copyright 2016 Eric Biggers
> Copyright 2024 Google LLC
>
> Permission is hereby granted, free of charge, to any person
> obtaining a copy of this software and associated documentation files
> (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge,
> publish, distribute, sublicense, and/or sell copies of the Software,
> and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be
> included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
> EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
> MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS
> BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
> ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

---

## QuickLook.Common

Source: https://github.com/QL-Win/QuickLook (directory `QuickLook.Common/`),
also published as the `QuickLook.Common` package on NuGet.

Vendored at `lib/QuickLook.Common.dll` and referenced with
`<Private>false</Private>` — it is used to compile against the QuickLook host
API and is **excluded** from the packaged `.qlplugin`, because the host provides
its own copy at runtime.

Note on licensing: the QuickLook *application* is GPL-3.0
(`LICENSE-GPL.txt` at the repository root), but the `QuickLook.Common` library
carries its own MIT `LICENSE` and is published to NuGet as MIT. Only the MIT
component is used here; no GPL-licensed code is linked or redistributed.

> MIT License
>
> Copyright (c) 2021 Contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

---

## Attribution: FreeSurfer colour table

Source: FreeSurfer (`FreeSurferColorLUT.txt`), Laboratory for Computational
Neuroimaging, Athinoula A. Martinos Center for Biomedical Imaging —
https://surfer.nmr.mgh.harvard.edu

The `FreeSurfer` palette in `MIQ.Core/Rendering/SegmentationLut.cs` transcribes
the canonical label→RGB assignments for a curated subset of FreeSurfer's `aseg`
and `aparc` (Desikan-Killiany) structures, so that a FreeSurfer segmentation
previews in the colours users recognise from `freeview`. The label numbers,
structure names and RGB values originate with FreeSurfer.

This is an attribution, not a license notice: no FreeSurfer code is used, and
no FreeSurfer file is redistributed — only a hand-picked subset of label/colour
pairs re-typed into a C# dictionary. FreeSurfer itself is distributed under its
own (non-OSI) FreeSurfer Software License Agreement, which is not reproduced
here because none of its software is included. If MIQ-Win is ever redistributed
in a context with stricter provenance requirements than an MIT open-source
release, confirm that this remains acceptable for the colour table specifically.

---

## .NET Framework compatibility packages

Source: https://github.com/dotnet/runtime

The BCL backport assemblies restored from NuGet and shipped inside the
`.qlplugin` — `System.Memory.dll`, `System.Buffers.dll`,
`System.Numerics.Vectors.dll`, `System.Runtime.CompilerServices.Unsafe.dll`
and `System.ValueTuple.dll` — are all covered by the following notice.

> The MIT License (MIT)
>
> Copyright (c) .NET Foundation and Contributors
>
> All rights reserved.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.
