# MangaLite Studio — vertical slice

User requirements (2026-10-18): extend the existing WinUI 3/.NET 10 app with
chapter import, editable page overlays, persistent regions/translations/review,
mock translation and image-worker contracts. Preserve the downloader, its tests
and all original images. No OCR, generation, Electron, GPL or new dependencies.
The cancelled Senkuro palette request does not apply.

## Workspace

The existing downloader remains the startup window and gains a Translation
Studio button. Studio is a separate native window: import/open/save/demo toolbar,
page list, zoomable image surface with overlays, source/translation/coordinate
editor and review status. A supplied original demo page makes the vertical slice
usable without downloading copyrighted material. The user can create, drag,
resize, select and delete regions. Text overlays preview translations only;
there is no flattened output or modification of source image bytes.

## Persistent contract

Schema v1 uses camelCase JSON. Project: schemaVersion, id, title,
sourceDirectory, sourceLanguage, targetLanguage, pages. Page: id, sourceFile,
pixelWidth, pixelHeight, regions. Region: id, bounds (x/y/width/height), sourceText,
translation, reviewStatus (draft/needsReview/approved), translationProvider.
Coordinates are finite source-image pixels, top-left origin, positive area,
fully contained in the image; zoom never changes saved coordinates.
sourceFile is a single filename. sourceDirectory is made relative to the JSON
on save and resolved on load. Only *.mangalite.json files can be written.

Missing optional fields default. Invalid optional regions/pages are skipped
with warnings; unknown data also warns. Recovered files cannot be overwritten:
save to a new file. Malformed roots and future schema versions fail without
replacing the current workspace. Existing unrelated/corrupt projects are never
overwritten. Saves are atomic and original images are opened only for reading.

## Ownership and future MCP

Core/Studio contains records, geometry, JSON store and StudioOperations. Operations
use typed IDs, data records, async Tasks and CancellationToken, with no WinUI
objects or dialogs. Future MCP tools can call the same import/load/save/add/update/
remove/translate operations. No MCP host or generic job framework in this slice.
ITranslationProvider accepts text/languages and returns text/provider identity;
MockTranslationProvider is deterministic and clearly labelled as a mock.
IImageProcessor inspects dimensions and defines future OCR/inpaint contracts.
The Windows implementation reads metadata with BitmapDecoder; OCR and inpainting
explicitly throw NotSupportedException. No image is ever rewritten.

GUI owns current project, drafts, selection, zoom and cancellation. A dirty
workspace prompts before replacement/close. Apply pending edits before changing
page/region, translating or saving. Failed operations keep the current project.
Changing text/geometry resets approval; approving requires a nonempty translation.

## Verification

Keep all original downloader tests. Add core tests for schema/defaults/recovery,
finite/bounded coordinates, pixel scaling, provider cancellation, import ordering,
atomic save/reload, rejected overwrites and byte-identical source images.
Build/publish x64 and exercise the actual Studio window with the supplied demo.
Document any UI interaction that cannot be verified in the current environment.
