# QA Test Workflow — Per-Station Key Enrollment

Covers the zero-touch enrollment feature across all three repos:

| Layer | Branch | What it does |
|-------|--------|--------------|
| Backend | `feat/station-key-enrollment` (PR #41) | `POST /stations` mints a one-time key (Argon2id hash stored); `POST /enroll` verifies the key and returns MinIO creds |
| Frontend | `feat/station-key-register` (PR #40) | Stations admin: mint key + one-time reveal (Copy / Download JSON) |
| Desktop | `feat/maui-key-enrollment` (PR #49) | Installer bakes `stationKey`/`stationName`/`apiUrl`; first launch gates on `/enroll` |

## Preconditions

- Postgres (`warehouse-postgres:5432`), MinIO (`:9000`) running.
- `warehouse_db_test.station_lists` has `key_hash` + `ip_address` columns (enrollment migration applied).
- Backend run with: `DATABASE_URL`, `MINIO_*`, **and** `ENROLL_MINIO_ENDPOINT` / `ENROLL_MINIO_ACCESS_KEY` / `ENROLL_MINIO_SECRET_KEY` (`ENROLL_MINIO_BUCKET` defaults `warehouse-videos`). Without the `ENROLL_*` set, `/enroll` returns **500**.
- Backend on `127.0.0.1:8080`, frontend on `localhost:3000` (`NEXT_PUBLIC_API_URL=http://localhost:8080`).

## A. Backend API — status-code contract

Run `scripts/qa/enroll-smoke.sh` (or by hand). Status-code contract from `api/enroll.rs`:

| # | Request | Expect |
|---|---------|--------|
| A1 | `POST /enroll {"stationName":"","stationKey":""}` | **400** (both required) |
| A2 | `POST /enroll` unknown/keyless station | **404** |
| A3 | `POST /stations {"stationName":"X"}` | **200/201** + `stationKey` (`nffwh_stn_…`) shown once |
| A4 | `POST /enroll` valid station + key | **200** + `{minioEndpoint,bucket,accessKey,secretKey}` |
| A5 | `POST /enroll` valid station, wrong key | **401** |
| A6 | A4 with `ipAddress` set | `station_lists.ip_address` updated |

One-time key: the plaintext key is returned **only** by `POST /stations`; it is never retrievable afterwards (only the Argon2id hash is stored).

## B. Frontend — one-time reveal (`/admin/stations`)

| # | Step | Expect |
|---|------|--------|
| B1 | Open Stations admin → "Add station" → submit a name | Reveal view appears with the plaintext key + `● shown once` tag |
| B2 | "Copy" | Key copied; button flips to "Copied" |
| B3 | "Download JSON" | Downloads `{stationName, stationKey, apiUrl}` |
| B4 | Press `Escape` / click backdrop **while revealing** | Modal does **not** close (key not discarded) |
| B5 | Finish/close, reopen Add station | Reveal state reset; no stale key shown |

## C. Desktop (MAUI) — first-launch gate (manual)

| # | Step | Expect |
|---|------|--------|
| C1 | Install a build with `stationKey`/`stationName`/`apiUrl` baked, no MinIO creds | First launch shows **EnrollmentPage**, not the shell |
| C2 | Click **Connect** | `POST /enroll` → on 200 saves MinIO creds, rebuilds MinIO client, boots into shell |
| C3 | Restart the app | Gate is skipped (creds present) → boots straight to shell |
| C4 | Online (https apiUrl) build with no CF creds | EnrollmentPage shows CF Access fields; both required before Connect |

Gate logic: `App.CreateWindow` shows `EnrollmentPage` iff MinIO endpoint+access+secret are all empty; any error defaults to the shell (never bricks).

## Pass criteria

- A1–A6 status codes match the table.
- B1–B5 behave as described (esp. B4 — the one-time key survives Escape/backdrop).
- C1–C3 gate transitions hold; C4 only for online builds.
- `dotnet build app/app.csproj -c Debug -f net10.0-windows10.0.19041.0 -r win-x64` → 0 errors; `dotnet test app.Tests` → all green.
