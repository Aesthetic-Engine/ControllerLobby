# Third-Party Software Notices and Licenses

ControllerLobby Beta 1 distributes the following third-party runtimes and libraries as part of its self-contained Windows (`win-x64`) installer and runtime package. Full license texts and upstream third-party notices are included in the `notices/` directory of the distribution:

Original ControllerLobby code is covered by the root `LICENSE` (MIT).
`Mame0279Defaults.json` derives from pinned MAME 0.279 BSD-3-Clause files by
Aaron Giles and Brad Hughes. Their source URLs, hashes, attribution and licence
are in `docs/MAME_DEFAULTS_LICENSE.txt` in source and
`notices/MAME_DEFAULTS_LICENSE.txt` in the portable distribution. The licence of
these individual source files is distinct from MAME's overall licence; no MAME
emulator binary is bundled.

- `notices/PYTHON_LICENSE.txt` — Official CPython 3.13.1 License and bundled dependency notices (PSF-2.0, OpenSSL, libffi, zlib, bzip2, SQLite).
- `notices/DOTNET_LICENSE.txt` — Microsoft Software License Terms for Microsoft .NET Library (Distributable Code).
- `notices/DOTNET_THIRD_PARTY_NOTICES.txt` — Complete third-party notices for the .NET 10 Runtime and Windows Desktop (WPF) libraries.

---

## 1. Python 3.13.1 Windows Embeddable Package (`win-amd64`)

- **Component:** Official CPython 3.13.1 Windows x64 Embeddable Zip (`python-3.13.1-embed-amd64.zip`)
- **Archive SHA-256:** `7B7923FF0183A8B8FCA90F6047184B419B108CB437F75FC1C002F9D2F8BCEC16`
- **Location in Package:** `python/` (and persisted to `<LaunchBox>\ControllerLobby.Setup\python\` during installation for local verify, repair, and uninstall)
- **License:** Python Software Foundation License Version 2 (PSF-2.0) and bundled third-party licenses in `python/LICENSE.txt` and `notices/PYTHON_LICENSE.txt`.

Copyright (c) 2001-2024 Python Software Foundation; All Rights Reserved.

---

## 2. Self-Contained Microsoft .NET 10 & WPF Runtime (`win-x64`)

- **Component:** Self-contained `win-x64` publication of `ControllerLobby.BigBox` (`Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64`, published from .NET SDK `10.0.403` with `SatelliteResourceLanguages=en`).
- **Location in Package:** `payload/`
- **Licenses & Notices:**
  - Microsoft .NET Runtime and WPF Managed/Native Libraries (`coreclr.dll`, `clrjit.dll`, `clrgc.dll`, `hostfxr.dll`, `hostpolicy.dll`, `System.*.dll`, `PresentationFramework*.dll`, `PresentationCore.dll`, `WindowsBase.dll`, `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`, `DirectWriteForwarder.dll`, `msquic.dll`, `D3DCompiler_47_cor3.dll`, `vcruntime140_cor3.dll`): see `notices/DOTNET_LICENSE.txt` and `notices/DOTNET_THIRD_PARTY_NOTICES.txt`, and the MIT License below.

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
