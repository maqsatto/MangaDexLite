# MangaDex Lite Implementation Plan

Goal: ship a usable native MangaDex downloader in the user's public repository.
Architecture: small Core library, WinUI window, dependency-free console tests.
Spec: [design.md](design.md). Execution: inline, as delegated by user.

## Global constraints
- C# / WinUI 3, Russian interface, x64, .NET 10; no changes outside this new repo.
- No Python, downloader process, webview, database, or extra UI framework.
- Async IO, one batch owner, cancellation, safe paths and atomic writes.

## Review focus
- Joint/ungrouped releases: filter by IDs, never names.
- Large feeds and stale UI replies: pagination and request cancellation.
- Retry/cancellation: never label incomplete work complete.
- Existing user files: do not replace them; verify completion manifests.
- Damaged settings: show warning and preserve the original bytes.

## Tasks
- [x] Core: first add executable tests for title links, decimal ranges, groups,
  pagination, retries, download resume/CBZ, cancellation and invalid path names.
  Run tests red, implement Models.cs, MangaDexClient.cs and ChapterDownloader.cs,
  run `dotnet run --project tests/MangaDexLite.Tests` green.
- [x] Desktop: App.xaml and MainWindow.xaml/code-behind with search, cancellable
  feed loading, language/group filters, multi-select/range, destination picker,
  download batch, visible errors, settings. Build x64 and fix diagnostics.
- [x] Delivery: add README, build/publish scripts and CI; run all core tests,
  live API smoke, release publish, app launch and `git diff --check`.
  Inspect tracked files for accidental output/secrets, commit and push. Publish
  portable ZIP as a GitHub release and verify the public download URL.
