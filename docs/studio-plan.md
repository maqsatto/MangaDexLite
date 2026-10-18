# MangaLite Studio Implementation Plan

> Execute inline using superpowers:executing-plans. The user has delegated
> implementation choices and supplied the complete vertical-slice requirements.

**Goal:** One imported page with editable translated overlays and save/reload.
**Architecture:** Existing downloader plus a separate Studio window backed by
UI-independent Core/Studio operations and standard Windows image decoding.
**Tech stack:** Existing C# .NET 10 / WinUI 3 packages; no new dependencies.
**Spec:** [studio-design.md](studio-design.md).

## Global constraints
- Preserve downloader code paths and its 11 original tests.
- Never write original images; write only explicit *.mangalite.json projects.
- No OCR/inpainting/generation engine, MCP server, Electron or GPL dependency.
- Source-pixel coordinates; async IO; typed, cancellable core operations.

## Review focus
- Zoomed drag/resize must retain source-pixel positions.
- Invalid/unknown JSON must warn and preserve the source file.
- Existing non-project files and unrelated projects must never be replaced.
- Async failures/cancellation must retain workspace and drafts.
- Imported pages need natural numeric order and stable IDs after reload.

## Tasks
- [x] Add failing Studio tests in tests/MangaDexLite.Tests/StudioTests.cs;
  preserve the existing Program.cs scenarios and invoke the new suite separately.
- [x] Implement Core/Studio models, geometry, provider/processor contracts,
  StudioProjectStore and StudioOperations. Run the full core executable green.
- [x] Add WindowsImageProcessor and StudioWindow. Integrate one launcher button
  into MainWindow, without moving downloader logic. Add an original demo PNG and
  schema/sample JSON. Build x64 and fix real construction errors.
- [x] Exercise import/region edits/mock/save/reload on the demo, run all tests,
  publish a separate Studio prototype ZIP, perform fresh review and diff checks.
  Update README with limitations and command/API ownership; commit/push branch.

## Ledger

- Baseline: clean main at b19d594; feature branch feature/mangalite-studio.
- Ruling: keep downloader as startup window; separate workspace avoids changing
  its layout/state machine. Application/project assembly names stay compatible.
