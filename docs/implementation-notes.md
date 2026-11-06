# Implementation notes

How the Tech Team Portal is built, for whoever works on it next. For running and deploying
it, see the [admin guide](admin-guide.md). Written November 2026.

## Overview

A self-hosted web app with no outside services. Everything it saves is in one data folder.

| Layer | Technology |
|---|---|
| App | C# / .NET 10 (LTS), ASP.NET Core Blazor in Interactive Server mode |
| Components | MudBlazor. Sign-in and account pages use Bootstrap, because they render without interactivity |
| Data | SQLite through EF Core. Migrations apply automatically at startup |
| Accounts | ASP.NET Core Identity with local accounts, two-factor sign-in and passkeys |
| Map viewer | Leaflet 1.9.4, bundled locally, wrapped in `wwwroot/js/sitemap.js` |
| PDF work | PDFtoImage (drawing) and PdfPig (reading text boxes) |
| Hosting | Docker. `compose.yaml` runs locally; `deploy/docker-compose.yml` adds Caddy for HTTPS |
| Checks | xUnit v3 tests, GitHub Actions CI, monthly Dependabot updates |

## Project layout

```
src/PcServ.Core/    Entities, roles and policies, Personality Core lines. No dependencies.
src/PcServ.Data/    DbContext with automatic auditing, migrations, demo data.
src/PcServ.Web/     The app.
  Components/Account/        Sign-in and account pages (server-rendered)
  Components/Pages/          App pages (interactive)
  Components/Pages/Maps/     Map list, viewer, upload and item dialogs
  Components/Pages/Manage/   Admin pages: import, alignments, users, settings, audit log
  Components/Shared/         Reusable components and edit dialogs
  Services/                  Settings, user admin, first-run and password gates
  Services/Maps/             Everything to do with maps (see below)
tests/PcServ.Tests/ Unit tests, plus tests that run the real app in memory.
tools/              Run separately from the app: the Wi-Fi map reader, and sources for the bundled wiki editor and barcode scanner.
deploy/             Production Docker Compose file and Caddy config.
docs/               These notes. Real maps and data also live here, excluded from git.
```

## Conventions to keep

- **Saving from a page:** use `AppDbFactory` or inherit `RecordPageBase`. They stamp the signed-in user,
  and the DbContext writes the audit log automatically. Bulk imports set `SuppressEntityAudit` and add one
  summary entry instead.
- **Web endpoints can't use `AppDbFactory`.** It relies on Blazor's sign-in state, which plain endpoints
  (`MapGet`) don't have, so it throws. Use `IDbContextFactory` directly. This caused a bug twice, and a unit
  test doesn't reveal it, so test endpoints with a real web request, as `MapLinkTests` does.
- **Permissions are enforced on the server.** Data a user can't see is never loaded for them. Use
  `Policies.CanView / CanEdit / CanManage / IsAdmin`, and `Roles.MeetsMinimum` for per-map and per-layer rules.
- **Text and color:** navy theme in `AppTheme.cs`, WCAG AA contrast, no orange text. Re-check new pages with axe.
- **Real data never goes in git:** no maps, school names or network details. `.gitignore` covers the known
  locations; check `git status` before committing anything new under `docs/`.

## How site maps work

The core design: one clean base image per map, with everything else stored as separate data.

| Entity | What it is |
|---|---|
| `SiteMap` | A base image for a site, with `MinimumRole` controlling who can open it |
| `MapLayer` | A set of items that can be toggled, with its own `MinimumRole` |
| `MapFeature` | One label or marker. X and Y run 0 to 1 from the top-left. `NeedsReview` marks import doubts |
| `MapLink` | Shows one map's layers on another through a saved transform, once `Confirmed` |
| `MapFeaturePlacement` | An item's hand-set position on a linked map, overriding the transform for that map only |

Files are in `<data folder>/maps/<random key>/`: `source.pdf` (the original) and `base.png` (what's shown,
up to 4800 pixels on its long side). They're outside the web root and served only by `/map-files/{id}/base`,
which checks the user's role.

### The pipeline, start to finish

1. **Bulk import** (`MapImportService`, **Manage → Import maps**). Reads a CSV listing each file, its site and
   who can open it. It creates sites as needed, and is safe to re-run: a map counts as imported if its file
   name or its map name already exists at that site.
2. **Labels** (`MapPdfReader`). Free-text boxes in a PDF become layers and are left out of the base image. Up to
   40 characters goes in Room labels; longer text goes in Title and notes. Text on shapes, usually room
   measurements, is skipped.
3. **Reading Wi-Fi maps** (`tools/extract-network-layers.cs`). The Wi-Fi PDFs are flat images, so:
   - Symbols are found by color, and the legend is found as a tight vertical stack of one of each kind.
   - Red names are isolated from the drawing, then read with Tesseract OCR.
   - Names are read twice (whole page, then each enlarged alone). A disagreement flags the item.
   - Names pair with the nearest symbol of the matching type, so outdoor names go to outdoor symbols first.
   - Known misreads are corrected and doubts are flagged. It writes one JSON file per map.

   OCR needs a native library, which is why this is a separate tool and not part of the app or Docker image.
4. **Network layers** (`NetworkLayerImportService`, step 3 on the import page). Turns those files into layers and
   redraws the base image without its colored markings, so a layer that's off really disappears.
5. **Alignment** (`MapAligner`, `MapLinkService`, **Manage → Align layers**). Finds how each Wi-Fi map lines up with
   each other map at that site, by correlating their black linework across many scales. Same-sized drawings that
   match exactly confirm automatically. Everything else waits for a person, on a page showing the plan in grey and
   the Wi-Fi lines in red, with click-to-align as the fallback.
6. **Viewing** (`SiteMapService.GetAsync`). Returns the map's own layers plus layers from confirmed links, moved
   through the transform. Items landing off the map are left out. Linked items keep their IDs, so editing one on a
   plan corrects it everywhere.

### Why it works this way

- **Links, not copies:** a correction only needs making once.
- **Nothing shows until confirmed:** a wrongly placed access point is worse than a missing one.
- **Moves on a plan stay on that plan:** the item is usually right on its Wi-Fi map, and the drawings differ. A move on the Wi-Fi map itself moves the item, and plans follow unless they have their own placement.
- **Wi-Fi layers stay Tech and above**, and a map's own rule still applies through a link, so a Viewer can't reach
  restricted data by opening a plan instead.

### Custom layers

`MapLayerService` handles layers people make themselves (`MapLayerKind.Custom`). Each has `MapLegendEntry` rows, and
its items point to one through `MapFeature.LegendEntryId`, so editing an entry redraws every item using it. Imported
items keep using fixed `Symbol` values. Shape outlines are defined twice, in `MapShapes` and in `CUSTOM_SHAPES` in
`sitemap.js`, and must stay in step. The Photos and notes layer is also `Custom`, but is managed by the app and is
excluded by name. A layer linked from another map through an alignment is only changed on its own map, because item
positions are stored in that map's coordinates.

## How orders work

`OrderService` checks for Tech or above on every call, not just on the pages, because orders hold staff names. `Order`
keeps its current stage and date, and each change adds an `OrderStatusChange`, so history is never overwritten. Stage
wording is all in `OrderStages`. The sent stage names `Order.Contact`, and `OrderStatusChange.Contact` keeps who it went to at
the time. Names are stored as text, not links to `SiteStaffMember`, so removing someone from a staff list leaves orders as they were. Vendors are never deleted, only marked as no longer approved, so past orders keep them.

A pin is a normal `MapFeature` on an app-managed `MapLayerKind.Orders` layer, linked from `Order.MapFeatureId`. That
means Move items, alignment and PDF export all work without special handling. `SiteMapService` builds the pin's label
from the order each time a map loads, so it can't go stale, and leaves out pins of finished orders while keeping them
stored, so reopening an order brings its pin back. The layer's role can't be changed, because pins name staff members.

## How repairs work

`RepairService` checks for Tech or above on every call. A `Device` is created the first time an asset tag is checked in, and
remembers its serial number, model and last site, so later scans fill in. Asset tags and serial numbers match in any case
(`NOCASE`). A device can have one open `Repair` at a time. Status wording, and what the inventory system should say for each status, is all
in `RepairStatuses`. `Repair.InventoryUpdatedTo` records the status that system was last set to, and it counts as out of date
whenever that differs from `Status`, so no one has to remember to reset anything.

A device is out of circulation when its latest repair is `BeyondRepair` (`RepairService.OutOfCirculation`), so checking it in
again puts it back. `Device.DeprovisionedOn` is cleared when a device comes back, so it's listed again if it's retired later.

Camera scanning is experimental until it's been tried on real tags, and is available to Techs and above. The scanner is built from `tools/barcode-scanner/` into `wwwroot/lib/barcode/`, which is committed, with the decoder served locally rather than from the library's default public address. A live code must appear in two frames running, to avoid misreads, and each code is then ignored for four seconds. It always uses the bundled decoder rather than the browser's own, so it behaves the same everywhere.

## How the wiki works

`WikiService` checks roles on every call: everyone signed in reads what their role allows, Techs and above write, and Managers and above arrange sections and delete pages. `WikiPage` holds what readers see, each publish adds a `WikiPageVersion`, and each person's unpublished work is one `WikiDraft`. A draft records the version it started from, so publishing over someone else's newer version asks first.

Everything is cleaned by `WikiHtml.Clean` on the server when saved, because the browser can't be trusted. To allow a new kind of content, add it to that allow list, and to the editor. Heading addresses for "On this page" are added when a page is read, and aren't stored.

The editor is Tiptap, built from `tools/wiki-editor/` into `wwwroot/lib/tiptap/wiki-editor.js`, which is committed, so neither the server nor Docker needs Node. To change it, run `npm ci` and `npm run build` in that folder. Versions are fixed in its `package-lock.json`. Dependabot doesn't watch that folder yet.

Drafts aren't in the audit log, which would otherwise record every pause in typing. Publishing, restoring and deleting each add one line.

## The test version

`TestVersion` keeps a list of Admins who have it on, and `AppDbFactory` gives each of them the separate `pcserv-test.db`. Anything that must stay real uses `CreateRealAsync` or the Identity stores directly, which is why accounts and Settings don't change. Web endpoints have no Blazor sign-in context, so they call `TestVersion.CreateForAsync(user)`. Any new endpoint that reads data must do the same, or it would show real data to someone in the test version. Map and wiki files share the real folders, but keys are random, and a reset only deletes files listed in the test database. `TestVersionManager` holds the fictional content.

## Account recovery

`AccountRecovery` runs from the command line after the database is ready, then exits without starting the web server, so it's safe alongside a running copy. It's deliberately not available over the web.

## District settings

Anything unique to one organization belongs in `DistrictSettings`, not in the code or in git, so the generic template can ship
with these blank. Each setting must be optional, and the app must work without it.

| Setting | Locally, in `src/PcServ.Web/district.json` | In production, in `deploy/.env` |
|---|---|---|
| What to call the asset system | `PcServ:District:InventoryName` | `PCSERV_INVENTORY_NAME` |
| Its address | `PcServ:District:InventoryUrl` | `PCSERV_INVENTORY_URL` |
| What to call the device console | `PcServ:District:DeviceConsoleName` | `PCSERV_DEVICE_CONSOLE_NAME` |
| Its address | `PcServ:District:DeviceConsoleUrl` | `PCSERV_DEVICE_CONSOLE_URL` |
| Digits in an asset tag | `PcServ:District:AssetTagDigits` | `PCSERV_ASSET_TAG_DIGITS` |

`docs/district.example.json` shows the local file. Settings are read at startup, so restart the app after changing them.

Names are written as they read mid-sentence, such as "the asset system". `DistrictSettings.Sentence` and `Label` adapt them for the start of a sentence and for headings.

Still in the code, and candidates to move here: `OrderStages` and `RepairStatuses` wording, and the app's name.

## Data and files

| What | Where | In git? |
|---|---|---|
| Source code and these notes | This repository | Yes |
| Database, imported maps, confirmed alignments, sign-in keys | `src/PcServ.Web/App_Data/` locally, or the `pcserv_pcserv-data` Docker volume | **No: back up separately** |

Deleting `App_Data` loses checked alignments and corrected names, because they exist only in the database. Importing
again would restore the maps, but not that work.

## Changing the app

- **Database changes:** edit entities in `PcServ.Core`, then run
  `dotnet tool run dotnet-ef migrations add <Name> --project src/PcServ.Data --startup-project src/PcServ.Web`.
- **Personality Core lines:** `src/PcServ.Core/Personality/Quips.cs`. It's off by default, with a small switch at the bottom of Settings. Every moment has a plain line, used when it's off.
- **Releasing:** a version tag such as `v0.1.0` runs `.github/workflows/release.yml`, which tests and publishes the image to GitHub's container registry.
- **Keeping current:** Dependabot opens a monthly pull request for package, image and CI updates, and won't propose a new major .NET version on its own. Every push runs the tests, then builds and starts the real Docker image.

## Testing

```powershell
dotnet test                # Add -c Release if the app is running, because it locks the Debug build.
dotnet run --project src/PcServ.Web
```

- **Automated tests** cover permissions, importing, alignment math, audit logging and sign-in flows. Test data is
  fictional, including the Maple Grove sample map.
- **Browser and accessibility checks** have been run by hand, using Playwright with Edge and axe. They aren't part of CI yet.
- **Always test imports on a throwaway database**, by pointing `PcServ__DataDirectory` somewhere temporary.

## Known limitations

See [known-limitations.md](known-limitations.md).
