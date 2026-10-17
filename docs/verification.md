# Verification — 2026-10-18

Environment: Windows 11 x64, .NET SDK 10.0.402. Official WinUI component packages
are pinned in the project. Only the UI, interactive-experience and runtime SDK
components are referenced; AI/ML/search/widget packages are not shipped.

- 11 dependency-free core scenarios pass, including decimal ranges, group IDs,
  feed pagination, retry, user cancellation, interrupted chapter resume, CBZ,
  incomplete archives, four-digit page numbers and damaged settings preservation.
- Release x64 build/publish has no compiler warnings/errors.
- Live MangaDex search, Russian chapter feed and at-home page URL lookup pass.
  No real manga image download is part of the live test; image IO/resume/CBZ use fixtures.
- Portable and Lite distributions create a native `MangaDex Lite` window and
  remain running during the startup smoke check. Lite was tested with .NET 10
  and Windows App Runtime 2.5.1 already installed.
- A fresh review identified incomplete-CBZ and timeout issues; both were fixed
  and the second review found no remaining important blockers.

The published application PRI resource index is required. The build script
refuses to package a distribution that omits it. Keep EnableMsixTooling enabled
even for the unpackaged app so SDK publish targets carry the resource index.

Limitations: no visual/manual mouse-and-keyboard pass, clean-machine runtime
installation test, or Windows 10 run was possible in this session. Folder picker,
theme/layout appearance and full real CDN downloads remain manual checks.

Published release: https://github.com/maqsatto/MangaDexLite/releases/tag/v0.1.0
Portable ZIP: 70,444,979 bytes. Lite ZIP: 10,914,028 bytes. GitHub asset SHA256
digests match local files; public download responds HTTP 200.
