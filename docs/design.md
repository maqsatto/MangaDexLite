# MangaDex Lite

Approved direction: user delegated design and implementation on 2026-10-18.

Standalone Windows x64 app, C# .NET 10 and WinUI 3. Russian UI. No Python,
gallery-dl process, webview, database or third-party MVVM framework. Only the
Microsoft Windows App SDK and its build dependencies. Keep BallonsTranslator untouched.

One window: search/title URL on top, search results on the left, manga details,
language/group filters and multi-select chapter list on the right. Footer owns
destination, folder/CBZ and original/data-saver settings, download progress and cancellation.
Users can select all visible chapters or a numeric range (including decimal chapter numbers).
All groups are kept distinct by UUID; joint releases match either group. Ungrouped
releases remain selectable. External-only chapters show their unavailable state.

Core library owns API pagination, response parsing, request pacing/retries and
asynchronous downloads. GUI owns selection and a single sequential batch. Search
and chapter loading are cancellable; stale responses must never replace new results.
HTTP 429 and transient server failures use bounded retries and Retry-After.
No bypass for unavailable content or site restrictions.

Output: manga/title UUID, chapter/UUID, zero-padded page files. Atomic page writes,
completion manifests with expected page names, no overwrite of completed output.
Interrupted chapters resume by skipping completed pages. CBZ is atomically published
after all pages are present. Originals and compressed downloads have separate names.
Untrusted API strings cannot escape output directories or form invalid Windows names.

Settings live in LocalAppData/MangaDexLite. Malformed settings warn and do not get
silently overwritten. Errors stay visible without crashing or freezing the UI.
Closing cancels active work. No automatic downloads or account login.

Validation: dependency-free core test executable with HTTP fixtures for pagination,
filters, request retry, cancellation, filename handling, resume and CBZ. Build and
publish WinUI x64, launch smoke check, read-only live API check. Ship a portable ZIP
including .NET and Windows App SDK for easy first use; also document smaller
framework-dependent publication. No numeric size or memory claim without measurement.
