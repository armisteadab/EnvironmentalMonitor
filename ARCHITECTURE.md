# Environmental Monitor — Architecture & Design Document

This document explains how the **ESP32 Environmental Monitor** dashboard was built, the frameworks/libraries it relies on, how data flows through the system end-to-end, and the most important design decisions to be aware of when maintaining or extending it.

---

## 1. What This Application Does

The app is a web dashboard that displays live-ish temperature and humidity readings collected from a small number of ESP32-based DHT-22 sensors placed around a house (currently: **Outdoor**, **Upstairs**, **Basement**). It shows:

- Per-sensor "cards" with current temperature (°F/°C), humidity, and online/offline status.
- Two 24-hour line charts (temperature and humidity) with one series per sensor.
- A "Recent Readings" table of the last N raw readings.
- Simple gauge widgets summarizing the current temperature per sensor.
- A "Sensors" modal (in the sidebar) listing sensor metadata (type, color, online status, last seen).

Data is not read directly from the sensors by the web app. Instead, a separate Python script running on a machine on the same local network as the sensors polls each sensor and either writes to a local CSV file (dev/local mode) or POSTs the reading to the deployed web app's ingest API (production/Azure mode).

---

## 2. High-Level Architecture

```
 ┌───────────────┐      raw TCP socket (port 13)      ┌───────────────────┐
 │  ESP32 sensors │ <──────────────────────────────── │  sensor_logger_*.py │
 │ (DHT-22, LAN)  │  "DEVICE=X TEMP_C=.. TEMP_F=.. HUMIDITY=..%"   (runs on a
 └───────────────┘                                     local PC/Pi, same LAN)
                                                               │
                                                               │ HTTPS POST JSON
                                                               │ /api/sensor-ingest
                                                               ▼
                                                 ┌─────────────────────────────┐
                                                 │  ASP.NET Core MVC Web App   │
                                                 │  (EnvironmentalMonitor)     │
                                                 │                             │
                                                 │  SensorIngestController     │
                                                 │        │                    │
                                                 │        ▼                    │
                                                 │  SensorDataService          │
                                                 │  (appends to CSV, caches    │
                                                 │   in memory, builds view    │
                                                 │   models)                   │
                                                 │        │                    │
                                                 │        ▼                    │
                                                 │  wwwroot/data/              │
                                                 │  sensor_log.csv (data store)│
                                                 │        │                    │
                                                 │        ▼                    │
                                                 │  DashboardController        │
                                                 │        │                    │
                                                 │        ▼                    │
                                                 │  Razor Views (Dashboard/    │
                                                 │  Index.cshtml) + Chart.js   │
                                                 └─────────────────────────────┘
                                                               │
                                                               ▼
                                                        Browser (dashboard UI)
```

Key idea: **the CSV file is the database.** There is no SQL Server / EF Core / external database. This keeps the app extremely simple to deploy (a single Azure App Service, no DB provisioning) at the cost of scalability and query performance — a deliberate tradeoff for a small personal/home project.

---

## 3. Technology Stack

### Backend
- **.NET 10 / ASP.NET Core MVC** (`net10.0`, see `EnvironmentalMonitor.csproj`)
  - Classic MVC pattern (Controllers + Razor Views), not Minimal APIs or Blazor.
  - `Program.cs` uses the modern minimal-hosting-model bootstrap (`WebApplication.CreateBuilder`), but the app itself is structured as traditional MVC (`AddControllersWithViews`, `MapControllerRoute`).
  - Nullable reference types and implicit usings are enabled project-wide.
- **Dependency Injection**: `SensorDataService` is registered as a **singleton** (`builder.Services.AddSingleton<SensorDataService>()`), since it manages a thread-safe in-memory cache of the CSV data and must be shared across all requests.

### Frontend
- **Razor Views** (`.cshtml`) render server-side HTML — no SPA framework (no React/Angular/Vue).
- **Bootstrap** (`wwwroot/lib/bootstrap`) — layout primitives and the Sensors modal component.
- **jQuery** (`wwwroot/lib/jquery`, plus jquery-validation libs) — included via the standard ASP.NET Core MVC scaffolding, used mainly for Bootstrap's JS dependencies / potential future form validation.
- **Chart.js 4.4.1** (loaded from CDN in `_Layout.cshtml`) — renders the temperature and humidity time-series line charts on the dashboard.
- **Vanilla JS Canvas drawing** — the small "gauge" widgets on the dashboard (per-sensor temperature dials) are hand-drawn directly onto `<canvas>` elements using the 2D canvas API (no gauge library dependency).
- **Custom CSS** (`wwwroot/css/dashboard.css`, `site.css`) — hand-written styling for the dashboard's card/sidebar/topbar layout (not a pre-built admin template).

### Data ingestion / sensor polling
- **Python 3** script(s) in `wwwroot/data/`:
  - `sensor_logger.py` — original/local version: polls sensors over raw TCP sockets and appends readings straight to a local CSV file only.
  - `sensor_logger_azure.py` — production version: does everything `sensor_logger.py` does (local CSV backup for resilience) **and** POSTs each reading as JSON to the deployed Azure App Service's `/api/sensor-ingest` endpoint using the `requests` library.
  - Sensors are polled via a plain **TCP socket on port 13**, expecting a text response like:
    `DEVICE=BASEMENT TEMP_C=20.0 TEMP_F=68.0 HUMIDITY=63.4%`
    This is parsed via regex (`re`) into device/temp/humidity fields.
  - Polling interval is every 30 minutes by default (`POLL_INTERVAL = 30 * 60`).

### Hosting / Deployment
- **Azure App Service** — the publish profile in `Properties/PublishProfiles/` (`EnvironmentalMonitor20260715220237 - Web Deploy.pubxml`) targets a Web Deploy-based Azure App Service deployment.
- No containerization (no Dockerfile) — it's a directly-published ASP.NET Core app.

---

## 4. Project Structure

```
EnvironmentalMonitor/
├── Program.cs                        # App bootstrap: DI registration, middleware pipeline, routing
├── Controllers/
│   ├── DashboardController.cs        # Serves the main dashboard view + /Dashboard/Sensors JSON endpoint
│   ├── SensorIngestController.cs     # POST /api/sensor-ingest — receives readings from the Python logger
│   └── HomeController.cs             # Default MVC scaffolding (Index/Privacy/Error) — mostly unused/legacy
├── Models/
│   ├── SensorReading.cs              # Core domain models: SensorReading, SensorCard, RecentReadingRow,
│   │                                 #   SensorInfo, DashboardViewModel
│   ├── SensorIngestRequest.cs        # DTO for the ingest API's JSON payload, with validation attributes
│   └── ErrorViewModel.cs             # Standard MVC error view model
├── Services/
│   └── SensorDataService.cs          # Core business logic: CSV read/write, caching, dashboard view-model
│                                      #   construction, sensor status computation
├── Views/
│   ├── Dashboard/Index.cshtml        # Main dashboard page (cards, charts, table, gauges) + inline JS
│   ├── Shared/_Layout.cshtml         # App shell: sidebar nav, topbar, Sensors modal, shared scripts
│   └── Home/...                     # Default scaffolded views (mostly unused)
├── wwwroot/
│   ├── css/dashboard.css             # Custom dashboard styling
│   ├── data/
│   │   ├── sensor_log.csv            # THE DATA STORE — append-only CSV of all readings
│   │   ├── sensor_logger.py          # Local-only polling script
│   │   └── sensor_logger_azure.py    # Production polling script (also POSTs to Azure)
│   └── lib/                          # Bootstrap, jQuery, jquery-validation (client-side libs)
└── Properties/PublishProfiles/       # Azure Web Deploy publish profile
```

---

## 5. Data Flow in Detail

### 5.1 Ingestion (write path)
1. `sensor_logger_azure.py` runs continuously on a machine with LAN access to the sensors.
2. Every 30 minutes it opens a raw TCP socket to each sensor IP on port 13 and reads a line of text.
3. The text is parsed via regex into `device`, `temp_c`, `temp_f`, `humidity`.
4. The reading is appended to a **local CSV backup** first (resilience if the network/app is down).
5. The reading is then POSTed as JSON to `POST /api/sensor-ingest` on the deployed app.
6. `SensorIngestController.Post(...)`:
   - Validates the payload (`deviceId` required, model validation via data annotations).
   - Defaults `RecordedUtc` to server time if omitted, and computes `TemperatureF` from `TemperatureC` if not supplied.
   - Builds a `SensorReading` and calls `SensorDataService.AppendReading(...)`.
7. `SensorDataService.AppendReading(...)`:
   - Thread-safe (`lock`) append to `wwwroot/data/sensor_log.csv`, creating the file/header if needed.
   - Invalidates the in-memory cache so the next read reflects the new row.

### 5.2 Display (read path)
1. Browser requests `/` (routed to `DashboardController.Index()` via the default MVC route).
2. `SensorDataService.BuildDashboard(hoursBack: 24, recentRows: 10)`:
   - Loads all readings from CSV (`GetAll()`), using a **timestamp-based cache** (`_cache` + `_cacheLoaded`) that only re-reads the file from disk if its last-write-time has changed — avoiding re-parsing the CSV on every request.
   - Determines "now" (`AsOf`) as the **latest timestamp found in the data**, not `DateTime.UtcNow` — this makes the "Online/Offline" status and time windows behave sensibly even with a static/demo dataset.
   - Builds one `SensorCard` per known device (latest reading, online status = last seen < 60 minutes ago relative to `AsOf`).
   - Builds 24-hour time series **bucketed into 30-minute intervals**, averaging readings within each bucket per device (`TempSeries` / `HumiditySeries` dictionaries keyed by display name, with `null` gaps for missing buckets so Chart.js can skip them via `spanGaps`).
   - Builds the "Recent Readings" table (last 10 rows across all devices, newest first).
3. The Razor view serializes `ChartLabels`, `TempSeries`, `HumiditySeries` to JSON inline (`System.Text.Json.JsonSerializer.Serialize`) and hands them to Chart.js on the client to render two line charts.
4. Gauges are drawn manually with the Canvas 2D API based on each card's current temperature.
5. The Sensors modal in the sidebar calls `GET /Dashboard/Sensors` (AJAX via `fetch`), which returns `SensorDataService.GetSensorInfos()` as JSON — a summary of each known sensor's name, type, color, and online/last-seen status.
6. The dashboard **auto-reloads every 5 minutes** via `setTimeout(() => window.location.reload(), 5 * 60 * 1000)` in the view's inline script, giving a "near real-time" feel without needing WebSockets/SignalR.

---

## 6. Key Domain Concepts

- **Device keys vs. display names**: Raw sensor identifiers (`OUTSIDE`, `UPSTAIRS`, `BASEMENT`) are mapped to human-friendly display names and colors via `SensorDataService.DeviceMeta`, a static dictionary that is the **single source of truth** for which sensors exist, their display order, and their brand color. Adding a new physical sensor means adding an entry here.
- **"Online" status is relative, not absolute**: A sensor is considered online if its most recent reading is less than 60 minutes older than `AsOf` (the latest timestamp in the whole dataset) — not compared to the real wall-clock time. This is intentional so the dashboard still looks "correct" against a static/frozen dataset (e.g., a demo or a paused logger) rather than showing everything as offline.
- **CSV as the data store**: Chosen for simplicity — no database server to provision/manage, human-readable/editable, easy to back up, trivially portable. Tradeoffs: no concurrent-writer safety beyond the in-process `lock` (fine for a single-instance low-traffic app, would need rethinking for scale-out), full file re-read on cache invalidation, no indexing.
- **In-memory caching with file-timestamp invalidation**: `SensorDataService` avoids re-parsing the CSV on every dashboard load by checking `File.GetLastWriteTimeUtc` against the last time it loaded the cache — a simple, dependency-free caching strategy appropriate for a low-frequency-write workload (one write per sensor per 30 minutes).
- **Local-first, cloud-second ingestion**: The Python logger always writes locally first, then attempts to send to Azure — so a network hiccup or app downtime never causes data loss at the source; only the "live" cloud dashboard would show a gap until the logger catches up (there's currently no backfill/replay mechanism for missed POSTs, which would be a good enhancement).

---

## 7. Notable Implementation Details Worth Knowing

- **`Program.cs`** is intentionally minimal: no EF Core, no auth, no Swagger, no CORS config — this is a small internal/personal tool, not a public multi-tenant API.
- **`SensorIngestController`** is a pure API controller (`[ApiController]`, `ControllerBase`), separate from the MVC `DashboardController`, keeping the "write" and "read" concerns cleanly split.
- **View models are pre-shaped for the view**: `SensorDataService.BuildDashboard()` does all the aggregation/bucketing server-side and hands the Razor view simple, display-ready structures (`DashboardViewModel`) — the view itself contains no business logic beyond formatting and Chart.js wiring.
- **No authentication/authorization** is implemented; the ingest endpoint and dashboard are both open. This is acceptable for a private home-network/demo project but would need to be addressed (e.g., an API key header check in `SensorIngestController`) before any public-facing or multi-user deployment.
- **`HomeController`/`Views/Home`** are leftover from the default ASP.NET Core MVC template and are not part of the actual product experience (the default route points to `Dashboard/Index`).
- **Two Python logger scripts exist side-by-side** (`sensor_logger.py` and `sensor_logger_azure.py`) — the former is the original local-only version, the latter is the actively used production version that also pushes to Azure. Keep this in mind if updating sensor-polling logic; changes likely need to be made in `sensor_logger_azure.py` and mirrored (or the local-only script deprecated) as needed.

---

## 8. Summary

This application is a deliberately lightweight, dependency-minimal home telemetry dashboard:

- **Backend**: ASP.NET Core MVC (.NET 10), a single singleton service (`SensorDataService`) doing CSV I/O + in-memory caching + view-model shaping, and a small JSON ingest API.
- **Frontend**: Server-rendered Razor views styled with Bootstrap + custom CSS, with Chart.js for time-series charts and hand-rolled Canvas gauges — no SPA framework.
- **Data store**: A flat, append-only CSV file (`wwwroot/data/sensor_log.csv`) acting as the entire "database."
- **Ingestion**: An external Python script polls ESP32/DHT-22 sensors over raw TCP sockets on the local network and forwards readings to the app's `/api/sensor-ingest` endpoint (with a local CSV backup for resilience).
- **Deployment**: Published directly to an Azure App Service via a Web Deploy publish profile — no containers, no database server, no external dependencies beyond the CDN-hosted Chart.js script.

The overall design favors **simplicity and low operational overhead** over scalability or robustness — appropriate for its scope as a small number of home sensors reporting on a 30-minute cadence.
