# AGENTS.md

Architecture and engineering decisions for **DashAnalizer**.

## 1. Overview

DashAnalizer is a Windows desktop application (WPF, .NET 8) for analyzing a GPS race log in CSV format. It reads `Timestamp`, `Latitude`, and `Longitude` from the log, draws the track on a map, replays the drive over time, detects completed laps, and shows telemetry columns in a side panel.

Main capabilities:

- Track map with OpenStreetMap tiles and an offline coordinate-grid fallback.
- Time playback with a slider and a play/pause timer.
- Lap detection from a user-drawn start/finish segment.
- Per-lap highlighting and coloring.
- Track corridor from two user-drawn boundary polylines that filters the points used in lap and sector math.
- Track corridor with flexible (Catmull-Rom spline) boundaries — when boundaries are marked as `SMOOTH` in `track-left.txt`/`track-right.txt`, the corridor is constructed using smooth curves interpolated through the vertices.
- Indicator panel built from arbitrary CSV columns.
- Headless diagnostic mode: `--stats`.

The CSV source file is always opened read-only. The application never modifies it.

## 2. Stack and commands

| Item | Value |
|---|---|
| Runtime | .NET 8 (`net8.0-windows`) |
| UI framework | WPF |
| Language | C# 12, nullable enabled, implicit usings |
| Solution | `DashAnalizer.sln` |
| Project | `src/DashAnalizer/DashAnalizer.csproj` |
| External packages | none |

Build:

```powershell
dotnet build DashAnalizer.sln
```

Run:

```powershell
dotnet run --project src\DashAnalizer\DashAnalizer.csproj
```

Headless statistics:

```powershell
dotnet run --project src\DashAnalizer -- --stats report.txt DATA\LogFile.csv "56.1199,43.6057,56.1201,43.6062"
```

Argument order: `--stats [report-path] [log-path] [lat1,lon1,lat2,lon2] [left-boundary] [right-boundary]`. The fourth argument is a lap line used to compute laps without the GUI. The fifth and sixth arguments are boundary files for the track corridor; when both load, the report gains `ZoneInside: <inside> / <total>` and lap math uses only the points inside the corridor. A boundary pair that cannot form an area prints `ZoneSpec: invalid` and no filtering happens.

There are no automated tests and no test project.

## 3. Project layout

```
DashAnalizer.sln
DATA/LogFile.csv                     sample race log (10 Hz GPS)
Resources/Senart_logo_mini.png       logo asset, currently unused by XAML
src/DashAnalizer/
  App.xaml / App.xaml.cs             startup, --stats mode
  MainWindow.xaml / .xaml.cs         main window layout and orchestration
  Controls/TrackMapControl.cs        custom map control (immediate-mode rendering)
  Models/
    TrackPoint.cs                    record struct: timestamp, latitude, longitude
    Lap.cs                           record: number, start/end index, duration
    LapLine.cs                       record: start/finish segment coordinates
    Sector.cs                        record: lap number, sector number, interval, time, distance, speed; palette
    BestLap.cs                       record: chosen sector items, totals, expected count
    AccelerationSample.cs            record struct: east/north G, magnitude, bearing
    TrackArea.cs                     GeoVertex, TrackBoundary, TrackArea with derived polygon
  Services/
    TrackLogReader.cs                CSV reader and default-file lookup
    TrackLog.cs                      immutable read result
    LogColumn.cs                     one CSV column plus min/max/avg
    LapCounter.cs                    crossings, laps, average speed
    SectorCounter.cs                 sector boundaries, intervals, distance and speed
    BestLapBuilder.cs                best sector per number, manual overrides, totals
    AccelerationCalculator.cs        per-point acceleration vector from log columns
    TrackZone.cs                     corridor: right-boundary normalization, point mask, boundary file IO
    TileStore.cs                     tile cache and downloader (parameters: URL, dir, extension)
    TileCache.cs                     cache root, ZIP export/import
```

## 4. Data flow

```
CSV file
  -> TrackLogReader.Read(path)
       -> parses header, locates timestamp/latitude/longitude
       -> builds List<TrackPoint>
       -> builds LogColumn[] for every header column
  -> TrackLog (points, columns, skipped rows, bounds)
  -> AccelerationCalculator.Compute(points, columns)
       -> per-point AccelerationSample? (east/north G)
  -> MainWindow
       -> Map.Track, Map.CurrentIndex, Map.Accelerations
       -> Map.LapLine, Map.SectorLines, Map.Sectors
       -> TrackZone.BuildMask(points, area) -> Map.ZoneMask, Map.LeftBoundary, Map.RightBoundary
       -> TimeSlider range
       -> indicator rows
  -> TrackMapControl.OnRender
       -> graticule, tiles, zone fill, track polyline, markers, acceleration arrow, lap line,
          sector lines, zone boundaries
```

`TrackLogReader` performs a single pass over the file. Required columns are found by case-insensitive header prefix, so headers such as `Timestamp (s)` and `Latitude (deg)` are accepted. Rows with unparsable required values are skipped and counted in `SkippedRows`; all other cells are still collected for the indicator panel.

All columns are stored twice per column: a `double[]` (with `NaN` for non-numeric cells) and a `string?[]` for text fallback. A column is treated as numeric when at least one value parsed and no non-empty value failed to parse (`LogColumn.cs:22`).

## 5. Coordinate pipeline

`TrackMapControl` uses Web Mercator with fractional zoom.

1. Geographic coordinates are converted to unit world coordinates `[0..1]`:
   - `UnitX(lon) = (lon + 180) / 360`
   - `UnitY(lat) = 0.5 - ln((1 + sin lat) / (1 - sin lat)) / (4π)`
2. The view is defined by `_centerX`, `_centerY` (unit world) and `_zoom`.
3. World size is `256 * 2^zoom` pixels.
4. Screen position is `(unit - center) * worldSize + viewport / 2`.
5. Inverse conversion is used for mouse picking and lap-line placement.

Tile zoom is the rounded fractional zoom, clamped to `0..19`. The graticule is rendered below tiles and remains visible when no tile is available. `FitToTrack()` computes zoom from the track bounding box with a 10% margin.

Default view center is the sample track region near `43.6021, 56.1196` (`TrackMapControl.cs:213`).

## 6. Lap detection

`LapCounter` implements lap timing:

- `FindCrossings(points, line, minGap = 10)` tests every consecutive track segment against the lap segment using strict orientation tests. Collinear and touching cases do not count.
- A crossing is suppressed if it is fewer than `minGap` points after the previous crossing, which filters GPS jitter.
- `ComputeLapDurations` measures from the previous crossing (or point 0 for the first lap) to each crossing.
- `BuildLaps` produces `Lap` records with inclusive start/end indices.
- `AverageSpeedKph` prefers the mean of the `Speed (kph)` column. If the column is missing or empty, it falls back to haversine distance divided by lap duration.

UI implications:

- The first lap starts at track point 0, not at the first crossing. A warm-up section before the line is therefore included in lap 1.
- Crossing indices are recomputed whenever the lap line changes; colors are reset at the same time.
- A lap can be excluded from statistics in its submenu. Excluded laps stay on the map and keep their highlight and color; they are only filtered out of average time, average speed, best, and worst. Exclusions are in-memory and reset when the lap layout changes.

## 6.1 Acceleration vector

`AccelerationCalculator.Compute` builds one `AccelerationSample?` per track point from `Longitudinal Acceleration (G)` and `Lateral Acceleration (G)`.

- Forward axis: positive longitudinal acceleration points along the vehicle heading.
- Left axis: positive lateral acceleration points to the left (ISO 8855). This was verified against `DATA/LogFile.csv`: strong lateral samples match left turns.
- Heading comes from `Bearing (deg)`. When the bearing cell is empty, the azimuth between the nearest distinct neighboring points is used.
- East/north components: `east = forward * sin(h) - left * cos(h)`, `north = forward * cos(h) + left * sin(h)`.
- Magnitude is `sqrt(east² + north²)`; direction is `atan2(east, north)` as a compass bearing (0° north, clockwise).
- A missing component is treated as zero. A row with both components missing, or with no usable heading, yields `null`.
- If neither acceleration column exists, `Compute` returns an empty array and the UI disables the toggle.

`TrackMapControl` receives the samples through the `Accelerations` dependency property and draws with `ShowAcceleration`. The click handler raises `TrackPointClicked`, which moves the slider and turns the vector on. Arrow length is `28 + 60 * min(|a|, 1.2)` pixels, clamped to `28..120`; the chip shows magnitude and bearing.

## 6.2 Sectors

`SectorCounter` slices the whole log with several cross-track boundary lines.

- `CollectBoundaries(points, sectorLines, lapLine)` runs `LapCounter.FindCrossings` for every line, adds the lap-line crossings, and returns sorted unique point indices.
- `BuildSectors(points, boundaries, lapCrossings)` builds intervals between adjacent boundaries. The first interval starts at point 0; the last ends at the final point.
- A crossing of the lap line is also a sector boundary. The sector number resets to 1 there, so numbering is `Lap 2 · Sector 1`, `Lap 2 · Sector 2`, and so on.
- Without a lap line (or with no lap crossings) all sectors belong to lap 1 and numbering is continuous.
- Duration is `Timestamp[end] - Timestamp[start]`. Distance is the haversine sum over the interval. Speed is `meters / seconds * 3.6`; it does not use the `Speed (kph)` column.
- Zero-duration intervals are skipped. Duplicate crossings are removed by the sorted set.

`Sector` carries the lap number, per-lap sector number, inclusive start/end indices, duration, distance, and speed. `SectorPalette` maps the per-lap sector number to one of eight colors, so colors repeat on each lap.

`TrackMapControl` exposes `SectorLines`, `IsPlacingSectorLine`, `Sectors`, and `HighlightedSector`, plus the `SectorLineCompleted` event. Placement uses the same two-click mechanism as the lap line, with its own purple dashed preview and `Г1`, `Г2` labels. `HighlightedSector` and the best-lap view are mutually exclusive; `MainWindow` clears one when the other is enabled.

## 6.3 Best lap

`BestLapBuilder.Build(sectors, overrides)` composes a synthetic lap from sectors of different laps.

- Sectors are grouped by `Number`. The fastest `DurationSeconds` wins; ties break on the lower lap number.
- `overrides` maps a sector number to a lap number. A replacement is applied only if that lap has a sector with the same number; otherwise the automatic choice stays.
- Numbers missing from every lap are skipped. `ExpectedCount` is the highest sector number, so the UI can show `12 из 14`.
- Totals are sums of the chosen sectors. Average speed is `distance / seconds * 3.6`, using geometry rather than the `Speed (kph)` column.
- Each item carries `IsManual` so the panel can label hand-picked sectors.
- `MainWindow` keeps `_bestLapOverrides`, rebuilds after every `RecomputeSectors()`, and clears overrides whenever sector geometry changes.

`TrackMapControl` exposes `BestLapSectors`. When non-empty, `DrawTrack` draws only those segments with sector palette colors, boundary dots, and `С{n}` chips. Priority is best lap, then single sector, then the lap visibility filter, then the full track.

## 6.4 Lap visibility

`TrackMapControl.HiddenLaps` holds lap numbers hidden from the map. Empty means the full track.

- `MainWindow` stores `_hiddenLaps`; each lap submenu item «Показывать на карте» is an independent checkable toggle.
- Hiding every lap is not a stable state: the set clears and the full track returns.
- Filtered rendering draws only visible laps. Hidden laps lose their track, custom color, boundary dot, and sector segments.
- Visibility is display-only: statistics, lap exclusions, and the best lap ignore it.
- The set resets with `RecomputeLaps()` and on lap-line changes, like lap exclusions.

## 6.5 Track corridor

A corridor ("полигон трассы") restricts lap and sector math to one ribbon along the track. Two separate polylines define it: left and right boundary, each entered click by click. `TrackArea` stores both boundaries; `Polygon` is the left polyline forward followed by the right polyline reversed, so the ends close automatically.

- `TrackZone.Create(left, right)` returns `null` unless both boundaries have at least two vertices.
- `TrackZone.NormalizeRight` compares endpoint distances (`l0→r0` + `l1→r1` against `l0→r1` + `l1→r0`) and reverses the right boundary when it was drawn the other way. The user does not have to match directions.
- `TrackZone.Contains` is a ray-casting test over `Polygon`. A point exactly on an edge counts as inside. Boundaries may cross; the even-odd rule applies.
- `TrackZone.BuildMask` returns one `bool` per track point. Segment-based math uses a segment only when both endpoints are inside, so a segment that leaves the corridor is dropped.
- The mask is an optional parameter of `LapCounter.FindCrossings`, `LapCounter.AverageSpeedKph`, `SectorCounter.CollectBoundaries`, and `SectorCounter.BuildSectors`. `--stats` follows the same path.
- Laps and sectors are never removed, only their distances and speeds are filtered. A sector with no inside segments keeps its time and reports zero distance and speed, so sector numbering does not shift.
- Timestamps are untouched: lap and sector duration stays real time.
- The filter is off while only one boundary exists. Once both exist it turns on automatically; the «Учитывать только точки внутри» menu toggle is session-only and not persisted.
- Changing a boundary calls `RebuildTrackArea`, then `RecomputeLaps`, which resets lap exclusions, hidden laps, the highlighted sector, and best-lap overrides.

`MainWindow` owns `_leftBoundary`, `_rightBoundary`, `_trackArea`, `_zoneMask`, and `_zoneFilterEnabled`. `TrackMapControl` exposes `LeftBoundary`, `RightBoundary`, `ZoneMask`, `IsPlacingLeftBoundary`, and `IsPlacingRightBoundary`, plus `LeftBoundaryCompleted` and `RightBoundaryCompleted`.

Boundary placement input:

- Left click adds a vertex; the draft is drawn with a dashed pen plus a line to the cursor.
- Double click, `Enter`, or right click completes the draft when it has at least two vertices.
- `Esc` leaves placement mode and drops the draft.
- Only one placement mode can be active; starting one clears the other, the lap line, and the sector line.

Boundary editing (works in every mode, placement included):

- A click within 9 px of a vertex starts a drag. The vertex follows the cursor; both boundary DPs are updated per move so the line and the fill follow live, but the mask and lap math are recomputed only once, on release.
- A click without movement selects the vertex; the selection is drawn as an accent ring. `Delete` removes it unless the boundary would drop below two vertices.
- A double click within 6 px of an existing segment inserts a vertex at the projected point, provided the projection keeps a 2% margin from both ends. Double clicks near a vertex do not insert.
- Hit priority: vertex, then segment, then the current placement or pan behavior.
- While placing, the first click of an insert double-click still appends a draft vertex; the control removes that vertex when the insert happens. A double click that misses every segment keeps completing the draft.
- Selection and hover reset whenever `LeftBoundary` or `RightBoundary` changes, so a normalized (reversed) right boundary cannot leave a stale index.
- Editing raises `LeftBoundaryEdited` / `RightBoundaryEdited`. `MainWindow` writes the new boundary, calls `RebuildTrackArea`, and saves the files; placement flags stay untouched.
- A drag that is interrupted by lost mouse capture commits what was already moved.

## 7. Rendering model

`TrackMapControl` derives from `FrameworkElement` and draws everything in `OnRender` with `DrawingContext` (immediate mode, no visual tree per element).

Render order (`TrackMapControl.cs:378`):

1. Background rectangle.
2. Coordinate graticule with labels.
3. Active tile layer: OSM or satellite, if enabled and loaded.
4. Corridor fill between both boundaries, under the track.
5. Track polyline, completed portion, colored sector segments, colored lap segments, lap boundary dots. In best-lap or lap-filter mode only the selected segments are drawn. With an active zone mask the full track is grey, inside runs are drawn in their normal color, and selected-mode drawing keeps only inside runs.
6. Start, finish, and current-position markers.
7. Acceleration vector and its `G · bearing` chip at the current point.
8. Lap line, sector boundary lines, and placement preview.
9. Left and right boundary polylines with vertices and `Левая`/`Правая` chips, plus the boundary draft.
10. Scale bar and active-layer attribution (`© Участники OpenStreetMap` or `© Esri, Maxar, Earthstar Geographics`).
11. Placement hint.

Pens and brushes are created once in the static constructor and frozen. Per-lap and per-sector colored pens are cached in `_coloredPens`. Track geometry is rebuilt as `StreamGeometry` per render pass and frozen.

State is exposed through `DependencyProperty` fields (`Track`, `CurrentIndex`, `ShowTiles`, `ShowSatellite`, `Laps`, `HiddenLaps`, `LapColors`, `LapLine`, `IsPlacingLapLine`, `SectorLines`, `IsPlacingSectorLine`, `Sectors`, `HighlightedSector`, `BestLapSectors`, `LeftBoundary`, `RightBoundary`, `ZoneMask`, `IsPlacingLeftBoundary`, `IsPlacingRightBoundary`), all with `AffectsRender`. This is the only binding mechanism between `MainWindow` and the control; there is no MVVM framework.

Interaction:

- Mouse wheel zooms around the cursor.
- Left drag pans.
- A left click without dragging selects the nearest track point within 14 px, moves the time slider there, and enables the acceleration vector.
- In lap-line or sector-line placement mode, two left clicks define the segment; `Esc` cancels.
- In boundary placement mode, each left click adds a vertex; double click, `Enter`, or right click completes the polyline; `Esc` cancels.
- A boundary vertex can be dragged at any time; a double click on a boundary segment inserts a vertex; `Delete` removes the selected vertex.
- `ClipToBounds = true` prevents tiles from painting over neighboring panels.

## 8. Tile pipeline

Two independent `TileStore` instances live in `TrackMapControl`:

| Layer | URL | Cache directory | Extension |
|---|---|---|---|
| OSM | `https://tile.openstreetmap.org/{z}/{x}/{y}.png` | `Tiles\{z}\{x}\{y}.png` | `.png` |
| Satellite | `https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}` | `Tiles\satellite\{z}\{x}\{y}.jpg` | `.jpg` |

Note the Esri axis order: `{z}/{y}/{x}`, not `{z}/{x}/{y}`. Esri World Imagery needs no API key; attribution is `© Esri, Maxar, Earthstar Geographics`.

`TileStore` resolves tiles in this order:

1. In-memory `ConcurrentDictionary`.
2. Disk cache under the configured directory.
3. HTTPS download from the configured URL factory with an 8-second timeout.

Details:

- Duplicate requests for the same tile are suppressed by `_requested`.
- Downloads run off the UI thread; `TileLoaded` triggers `InvalidateVisual` on the dispatcher.
- Decoded bitmaps are frozen so they can be used from any thread.
- Any failure yields `null`; the map keeps showing the graticule.
- `DownloadEnabled` follows `ShowTiles` and the cache-operation pause. Tiles already in the disk cache are still used when download is disabled.
- `ShowSatellite` selects the active store in `DrawTiles` and the attribution text in `DrawAttribution`.
- `ClearMemory()` bumps a generation counter and clears both caches. A download started before the clear is discarded, so imported files are not shadowed by stale results.
- `ReloadTiles()` clears memory of both stores and invalidates the control; the next render re-reads tiles from disk.
- `SetTileDownloadsPaused(bool)` suspends network downloads during ZIP export/import while still allowing disk reads.

`TileCache` owns the cache root (`%LOCALAPPDATA%\DashAnalizer\Tiles`) and the ZIP transfer:

- `Export(path)` walks the root recursively, stores paths relative to it with `/` separators, and returns the file count. Files that cannot be opened for read are skipped. A missing root returns 0; the caller deletes the resulting empty ZIP.
- `Import(path)` streams entries, accepts only `.png` and `.jpg`, rejects rooted paths, `:` and `.`/`..` segments, and verifies the resolved destination stays inside the root (Zip Slip). Matching files are overwritten, other cached tiles are preserved.
- Both operations are synchronous and called from `MainWindow` through `Task.Run`.

## 9. State and persistence

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\DashAnalizer\lapline.txt` | lap line: `lat1 lon1 lat2 lon2`, invariant culture |
| `%LOCALAPPDATA%\DashAnalizer\sector-lines.txt` | sector boundary lines, one `lat1 lon1 lat2 lon2` per line |
| `%LOCALAPPDATA%\DashAnalizer\track-left.txt` | left corridor boundary, one `lat lon` per line |
| `%LOCALAPPDATA%\DashAnalizer\track-right.txt` | right corridor boundary, one `lat lon` per line |
| `%LOCALAPPDATA%\DashAnalizer\indicators.txt` | selected indicator column names, one per line |
| `%LOCALAPPDATA%\DashAnalizer\Tiles\{z}\{x}\{y}.png` | OSM tile cache |
| `%LOCALAPPDATA%\DashAnalizer\Tiles\satellite\{z}\{x}\{y}.jpg` | satellite tile cache |

Write failures for lap line, sector lines, corridor boundaries, and indicator selection are intentionally ignored; none is required for the app to work. Corrupt settings files are ignored on load.

Lap exclusions from statistics are not persisted. They live in the in-memory `_excludedLaps` set and reset whenever `RecomputeLaps()` runs, because lap numbers only make sense for one lap-line layout. Sector selections are also session-only: sector geometry is persisted, computed sectors and the highlighted sector are not. The satellite layer choice is session-only too. Lap visibility and best-lap overrides are likewise in-memory: `_hiddenLaps` resets with lap recomputation, `_bestLapOverrides` resets whenever sector geometry changes. Corridor boundaries are persisted in `track-left.txt` and `track-right.txt`; the filter checkbox is not, so a launch with both boundaries present starts with the filter on.

ZIP archives contain tiles only. `lapline.txt`, `sector-lines.txt`, `track-left.txt`, `track-right.txt`, and `indicators.txt` are never exported.

The default log is located by `TrackLogReader.FindDefaultLogPath()`:

1. `DATA\LogFile.csv` in the current working directory.
2. `DATA\LogFile.csv` up to 10 parent directories above the executable.
3. Hard-coded fallback `d:\GIT.AI\DashAnalizer\DATA\LogFile.csv`.

## 10. Key decisions

- **Code-behind over MVVM.** The app is small and single-window. Event handlers mutate control state directly. No view models, commands, or DI container.
- **Custom map control instead of a map library.** Only a track polyline and a few markers are needed. A custom control avoids a heavy dependency and gives full control over the offline fallback.
- **Read-only CSV contract.** Parsing never writes to the source. All derived state lives in memory or in `%LOCALAPPDATA%`.
- **`double.NaN` as the missing-value sentinel.** Numeric columns keep a parallel text array, so empty or non-numeric cells remain displayable and never break lap-speed math.
- **Culture split.** CSV parsing and persistence use `CultureInfo.InvariantCulture`; UI formatting uses `CultureInfo.CurrentCulture`, so decimal separators match the user locale.
- **Dependency properties for redraw.** Assigning a new list instance is preferred over mutating a list in place, because WPF compares references before invalidating rendering.
- **Graceful degradation.** Missing network, missing tiles, corrupt settings, and unwritable cache all degrade to a usable app rather than an error dialog.
- **Session-only lap exclusion.** Excluding a lap from statistics is a transient analysis choice, so it is not written to disk. Persisting it would risk applying stale lap numbers to a different log or lap line.
- **Sector boundaries as cross-track lines, not point clicks.** A line is georeferenced and reusable across logs in the same area, matching the lap-line model. All crossings are sorted by time, so line order does not affect sector order.
- **Per-lap sector numbering.** Sector numbers restart at every lap-line crossing. This makes sectors comparable across laps (`Lap 2 · Sector 1` vs `Lap 3 · Sector 1`) while the map still covers the whole log. Without a lap line the numbering is continuous.
- **Best lap by sector number, not by lap time.** Comparing sectors per number isolates the fast and slow parts of the track. Manual overrides exist because a single fastest sector can be an outlier; the user can lock a stable choice.
- **Best lap totals from geometry.** Time, distance, and speed are sums over the chosen sectors. This keeps the result consistent with sector math and independent of the `Speed (kph)` column.
- **Lap visibility separate from statistics.** Hiding a lap is a map filter, not an analytical exclusion. Statistics keeps its own `_excludedLaps` set so the two intents never overwrite each other.
- **Sector speed from geometry.** Sector speed is distance divided by duration, not the mean of the `Speed (kph)` column, because a sector can be very short and column sampling is irregular.
- **Acceleration vector from telemetry, not differentiation.** The vector uses the logged longitudinal/lateral G columns instead of differentiating speed or position, which avoids noise amplification. The cost is a dependency on those columns and on a trusted sign convention.
- **Corridor as two independent boundaries.** Left and right polylines are drawn separately because a race track is not a clickable rectangle. The area is derived (`left + reversed right`), so the ends close without a third line.
- **Right boundary direction normalized, not required from the user.** Endpoint proximity decides whether to reverse the right polyline. Drawing both boundaries in the same direction stays optional.
- **Point-in-area by ray casting with an edge test.** Points on the boundary count as inside; crossing boundaries follow the even-odd rule. No polygon library is added.
- **Filter by mask, not by rebuilding the log.** One `bool[]` is passed to existing lap and sector functions. Points outside the corridor stay on the map in grey, so the user still sees the full path and the log itself is untouched.
- **Sectors are filtered, not removed.** A sector with no inside segments keeps its time slot and reports zero distance and speed. Removing it would renumber later sectors and break cross-lap comparison.
- **Real timestamps under the filter.** Only geometry is filtered. Lap and sector time stays the log time, so a pit stop or an off-track excursion still consumes its real seconds.
- **Live drag, deferred recompute.** Vertex drags replace the boundary DP on every mouse move so the fill and the line track the cursor, but the point mask, laps, and sectors are recomputed once on release. Recomputing per frame would ray-cast the whole log on every pixel.
- **Editing without a separate edit mode.** Vertices can be moved, inserted, and deleted in any mode; the hit radius decides between vertex, segment, and placement behavior. A separate edit mode would add state the app does not need.
- **Selection is transient.** The selected vertex index is cleared whenever a boundary DP changes, because right-boundary normalization can reverse the vertex order. Stable identity would need per-vertex ids.
- **Parameterized `TileStore`, two instances.** One class serves OSM and Esri because only the URL template, cache directory, and extension differ. Esri uses `{z}/{y}/{x}` and JPEG, so the URL factory and extension are constructor arguments instead of constants.
- **Separate satellite cache directory.** OSM tiles keep the existing root layout, satellite tiles live under `Tiles\satellite`. Old caches stay valid and a ZIP can carry both layers without name collisions.
- **Satellite toggle separate from map visibility.** `Подложка` hides the whole tile layer; `Спутник` only switches OSM ↔ satellite. Turning satellite on also turns the layer on so the user sees the effect immediately.
- **ZIP for cache transfer, merge on import.** A single archive moves a whole tile cache between machines. Import merges and overwrites matching tiles instead of deleting the current cache, so a partial archive cannot destroy previously downloaded data.
- **Zip Slip validation.** Imported entry names are checked for rooted paths, drive letters, `.`/`..`, and resolved destinations outside the cache root. Only `.png` and `.jpg` are written.
- **Cache operations off the UI thread, downloads paused.** `TileCache` stays synchronous and WPF-free; `MainWindow` wraps it in `Task.Run` and pauses network requests so the archive reflects a stable cache. Memory caches are cleared after import to force re-reads from disk.
- **Russian UI, English code identifiers.** User-visible strings and code comments are Russian; types and members are English. This file is English by requirement.

## 11. Known limitations

- Synchronous CSV read on the UI thread; a very large log freezes the window during load (`MainWindow.xaml.cs:89`).
- Every column is materialized twice in memory for the whole file. Large logs multiply memory use.
- Hard-coded absolute fallback path in `TrackLogReader.cs:152`.
- Tile memory cache is unbounded and `TileStore` is never disposed (the `HttpClient` lives for the process lifetime). Two stores now exist, so there are two unbounded caches.
- Tile requests cannot be cancelled; requests outside the viewport still complete.
- Cache export and import read or write the whole archive synchronously on a worker thread; there is no progress bar or cancellation.
- Esri World Imagery is a public endpoint with no API key; it may throttle or block heavy use. There is no fallback source beyond OSM.
- Import merges but never prunes tiles that are not in the archive.
- Changing the satellite toggle does not persist; every launch starts on OSM.
- The acceleration vector requires the longitudinal/lateral G columns; there is no differentiation fallback.
- Lateral sign is assumed positive-left. A logger with the opposite convention would mirror the lateral component.
- The acceleration arrow is screen-space and uses logged G magnitude; it is a direction indicator, not a calibrated scale bar.
- Lap line coordinates are georeferenced but not tied to a specific log file. Reusing the same track region across logs is assumed.
- Sector boundary lines are georeferenced and persisted independently of the log, with the same assumption.
- Sector numbering depends on the lap line. Changing or clearing the lap line changes sector grouping and resets the highlighted sector.
- Sector statistics group by per-lap sector number; a partial first or last sector still participates in the averages.
- Best-lap overrides and lap visibility are not saved. They reset when the lap line, sector lines, or log change.
- The best lap is assembled from existing sectors only. It cannot be timed over the full lap boundary, so lengths of partial first and last sectors can differ between laps.
- The best-lap panel lists every sector, so a layout with many sectors produces a long right panel.
- First lap includes the section from point 0 to the first crossing.
- Corridor boundaries are georeferenced but not tied to a specific log file, like the lap line. Reusing the same track region across logs is assumed.
- The corridor is a single closed area. A figure-eight crossing works through the even-odd rule, but a track that leaves and re-enters the same corridor can drop legitimate segments.
- Edge tolerance is `1e-9` degrees, roughly 0.1 mm. A point that should be on the boundary but differs by GPS noise is classified by the general ray-cast result.
- Filtering only changes geometry, so a corridor that clips a tight corner still removes real distance from that corner.
- The filter checkbox and boundary placement mode are session-only. Only the two boundary files persist.
- Changing a boundary invalidates lap exclusions, lap visibility, the highlighted sector, and best-lap overrides, because it recomputes laps and sectors.
- Every vertex drag or insertion recomputes the full point mask and lap math on release; a very large log will pause briefly.
- There is no undo. A wrong move or a wrong `Delete` is corrected only by moving the vertex back or redrawing the boundary.
- Vertices are addressed by index, so selection cannot survive a boundary reversal or replacement. It is cleared on every boundary change instead.
- Insertion is available only on straight segments of an existing boundary; draft segments and the closing edge between the boundary ends are not editable.
- No unit or integration tests.
- `bin/` and `obj/` build artifacts are present in the working tree; there is no `.gitignore`.
- The sample log has 10,920 lines at roughly 10 Hz; the playback timer assumes that cadence but does not read it from the data.

## 12. Rules for agents

- Never write to or rewrite files under `DATA/`. `LogFile.csv` is input data.
- Keep user-facing strings in Russian; keep comments in Russian; keep identifiers in English.
- Parse external numbers with `InvariantCulture`; format UI numbers with `CurrentCulture`.
- Do not delete or shorten existing comments unless the task explicitly asks for it.
- Preserve the read-only contract: no temp copies, no in-place edits of the source CSV.
- When adding a new rendered layer, add it to `OnRender` in the existing order and use frozen pens/brushes.
- When changing lap math, keep `LapCounter` free of WPF references so it stays testable and usable from `--stats`.
- Keep corridor math in `TrackZone` and `TrackArea` WPF-free for the same reason. `TrackMapControl` only draws the geometry it receives.
- When changing architecture, data flow, persistence paths, or decisions, update this file in the same change.
- Do not add NuGet dependencies without a clear need; the project currently has none.
- Verify with `dotnet build DashAnalizer.sln` after changes. Launching the GUI is optional for pure logic changes; `--stats` provides a headless smoke test.
