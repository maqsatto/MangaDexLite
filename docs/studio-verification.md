# Studio verification — 2026-10-18

Environment: Windows 11 x64, .NET SDK 10.0.402; existing pinned WinUI packages.
No dependencies were added. Original downloader API/download/settings code is
unchanged; only a Studio launcher was added to its window. All 11 original
downloader test scenarios remain and pass. Five additional Studio scenarios
exercise serialization/defaults/recovery, strict pixel geometry, mock translation,
review changes, import ordering, save/reload, image preservation, unrelated/corrupt
file protection, future versions and path traversal.

Native Windows GUI checks against the real Studio window:

- The supplied 700 × 900 original demo renders with an editable translated region.
- Editing translation and invoking Mock update the preview and region model.
- Drag at 72% zoom changes source coordinates from (85, 140) to approximately
  (147.596588, 181.731079), preserving width/height.
- Saving through the path-only Windows App SDK picker creates valid JSON.
  Restarting with --project restores translation «Привет, мир!» and those coordinates.
- Reopened projects remain clean; NumberBox formatting does not create a false
  dirty state. The original demo PNG SHA256 remains
  A005EA3FADF4C18DB75612A771DCAED75BBFDF6D6F8B1008F592149959BE0D48.
- Import through the folder picker decodes the actual PNG and shows one page.
  Drawing creates a region; dragging its grip resizes it in source-pixel units.
- Closing a dirty workspace displays Save / Discard / Cancel.

Two fresh read-only reviews found save-picker, geometry and draft issues; these
were corrected and the final review found no remaining important blockers.

This is a vertical slice. No full OCR/inpainting/image-generation engine, flattened
image export, MCP host, batch translation queue or production translator exists.
JPEG EXIF orientation, Windows 10, optional image codecs, light theme and very
large real chapters have not been manually exercised in this session.
