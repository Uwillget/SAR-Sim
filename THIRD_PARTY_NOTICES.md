# Third-Party Notices

This project and its Windows application use third-party software. The notices below
are provided for attribution and redistribution compliance; they are not legal advice.

## Self-contained Python analysis worker

The Windows application includes a PyInstaller-built process for rendering analysis
figures. Its exact, machine-readable component inventory and the corresponding license
texts ship at:

`Structured-Air-Route-Simulator_Data/StreamingAssets/SARAnalytics/Worker/THIRD_PARTY_LICENSES/`

The inventory is generated from the frozen build environment and covers:

- CPython and its redistributed native runtime libraries;
- NumPy, Pandas, Matplotlib, and their pinned Python runtime dependencies;
- the PyInstaller bootloader and pinned build-tool dependencies;
- OpenSSL, libffi, bzip2, Expat, XZ/liblzma, zlib, the Universal CRT, and the
  Microsoft Visual C++ runtime copied into the worker;
- bundled OpenBLAS/LAPACK/GCC runtime notices, Pillow image-library notices, and
  the DejaVu/STIX font licenses supplied by the relevant Python distributions.

Portions of this software are copyright © The FreeType Project
(https://freetype.org). All rights reserved.

## Unity runtime and packages

The application is built with Unity 2022.3 LTS. Unity runtime redistribution is governed
by the applicable Unity Editor/Runtime terms. Unity packages resolved by
`Packages/packages-lock.json`, including UGUI, TextMesh Pro, Timeline, Visual Scripting,
and Unity's Newtonsoft.Json package, carry the Unity Companion License or the license
included with the resolved package. Their package-local license files remain the
authoritative terms.

The Unity MCP package is an editor/development dependency and is not intended to be
included in the player build. Its source and license must be reviewed separately when
redistributing the editable Unity project.

## No endorsement

Third-party names are used only to identify redistributed components. No endorsement by
their authors or rights holders is implied.
