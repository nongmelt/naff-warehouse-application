# Reports: Order Fulfillment Overview — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Note:** Per user instruction — **no git commits**. Each task ends with a build/type-check verification step.

**Goal:** Add a Reports section to the frontend dashboard with an Order Fulfillment Overview report grouped by platform and shipping, with per-group drill-down for QC Hold and Video Incomplete orders, PDF export, and download history persisted in PostgreSQL.

**Architecture:** Two lazy endpoints (`GET /reports/fulfillment` for grouped summary, `GET /reports/fulfillment/drilldown` for per-group detail) plus download log/list endpoints. Frontend uses focused hooks per concern with the same `useState`/`useEffect`/`fetch` pattern as existing hooks.

**Tech Stack:** Rust/Axum/SQLx (backend), Next.js 16/React 19/Tailwind v4/TypeScript strict (frontend), PostgreSQL, `window.print()` for PDF.

**Spec:** `docs/superpowers/specs/2026-04-26-reports-fulfillment-overview-design.md`

---

## File Map

**Backend — create:**
- `backend/src/api/reports.rs` — all 4 report handlers + types

**Backend — modify:**
- `backend/src/api/mod.rs` — add `mod reports` + 4 routes
- `backend/migration/schema.sql` — append `report_downloads` table

**Frontend — create:**
- `frontend/app/reports/page.tsx`
- `frontend/app/components/FulfillmentReport.tsx`
- `frontend/app/components/FulfillmentDrilldown.tsx`
- `frontend/app/components/DownloadHistory.tsx`
- `frontend/app/hooks/useFulfillmentReport.ts`
- `frontend/app/hooks/useDrilldown.ts`
- `frontend/app/hooks/useDownloadHistory.ts`

**Frontend — modify:**
- `frontend/app/types.ts` — add report types
- `frontend/app/components/Sidebar.tsx` — add Reports nav item

---

## Task 1: DB — add `report_downloads` table

**Files:**
- Modify: `backend/migration/schema.sql`

- [ ] **Step 1: Append table definition to schema.sql**

Open `backend/migration/schema.sql` and append at the end:

```sql
CREATE TABLE IF NOT EXISTS report_downloads (
    id            SERIAL PRIMARY KEY,
    report_type   TEXT        NOT NULL,
    filters       JSONB       NOT NULL,
    downloaded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

- [ ] **Step 2: Run the migration against your local DB**

```bash
psql "$DATABASE_URL" -c "
CREATE TABLE IF NOT EXISTS report_downloads (
    id            SERIAL PRIMARY KEY,
    report_type   TEXT        NOT NULL,
    filters       JSONB       NOT NULL,
    downloaded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);"
```

Expected output: `CREATE TABLE`

---

## Task 2: Backend — structs and helpers in `reports.rs`

**Files:**
- Create: `backend/src/api/reports.rs`

- [ ] **Step 1: Create the file with all types**

Create `backend/src/api/reports.rs`:

```rust
use axum::{
    extract::{Query, State},
    http::StatusCode,
    Json,
};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use sqlx::{FromRow, PgPool};

use crate::error::AppError;

// ── Query params ─────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
pub struct FulfillmentQuery {
    pub from: Option<DateTime<Utc>>,
    pub to: Option<DateTime<Utc>>,
    pub platform: Option<String>,
    pub shipping: Option<String>,
}

#[derive(Debug, Deserialize)]
pub struct DrilldownQuery {
    pub platform: String,
    pub shipping: String,
    pub from: Option<DateTime<Utc>>,
    pub to: Option<DateTime<Utc>>,
}

// ── DB row types ─────────────────────────────────────────────────────────────

#[derive(Debug, FromRow)]
struct FulfillmentRowDb {
    pub platform: String,
    pub shipping: String,
    pub total_orders: i64,
    pub packed: i64,
    pub qc_passed: i64,
    pub qc_hold: i64,
    pub video_complete: i64,
    pub video_total: i64,
    pub last_updated_at: Option<DateTime<Utc>>,
}

#[derive(Debug, Serialize, FromRow)]
#[serde(rename_all = "camelCase")]
pub struct QcHoldDrillRow {
    pub tracking_number: String,
    pub qc_operator: Option<String>,
    pub packing_status: Option<String>,
    pub packed_by: Option<String>,
    pub packing_station: Option<String>,
    pub video_status: Option<String>,
    pub updated_at: Option<DateTime<Utc>>,
}

#[derive(Debug, Serialize, FromRow)]
#[serde(rename_all = "camelCase")]
pub struct VideoIncompleteDrillRow {
    pub tracking_number: String,
    pub operator: Option<String>,
    pub station: Option<String>,
    pub video_status: Option<String>,
    pub updated_at: Option<DateTime<Utc>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogDownloadReq {
    pub report_type: String,
    pub filters: serde_json::Value,
}

#[derive(Debug, Serialize, FromRow)]
#[serde(rename_all = "camelCase")]
pub struct ReportDownloadRow {
    pub id: i32,
    pub report_type: String,
    pub filters: serde_json::Value,
    pub downloaded_at: DateTime<Utc>,
}

// ── Response types ────────────────────────────────────────────────────────────

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FulfillmentSummaryResp {
    pub total_orders: i64,
    pub packed: i64,
    pub packed_pct: f64,
    pub qc_passed: i64,
    pub qc_passed_pct: f64,
    pub qc_hold: i64,
    pub qc_hold_pct: f64,
    pub video_complete: i64,
    pub video_total: i64,
    pub video_complete_pct: f64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FulfillmentReportRow {
    pub platform: String,
    pub shipping: String,
    pub total_orders: i64,
    pub packed: i64,
    pub packed_pct: f64,
    pub qc_passed: i64,
    pub qc_passed_pct: f64,
    pub qc_hold: i64,
    pub qc_hold_pct: f64,
    pub video_complete: i64,
    pub video_total: i64,
    pub video_complete_pct: f64,
    pub has_issues: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FulfillmentReportResp {
    pub updated_at: Option<DateTime<Utc>>,
    pub summary: FulfillmentSummaryResp,
    pub rows: Vec<FulfillmentReportRow>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DrilldownResp {
    pub platform: String,
    pub shipping: String,
    pub qc_hold: Vec<QcHoldDrillRow>,
    pub video_incomplete: Vec<VideoIncompleteDrillRow>,
}

// ── Helper ────────────────────────────────────────────────────────────────────

fn pct(n: i64, total: i64) -> f64 {
    if total == 0 {
        0.0
    } else {
        (n as f64 / total as f64 * 1000.0).round() / 10.0
    }
}
```

---

## Task 3: Backend — `fulfillment_summary` handler

**Files:**
- Modify: `backend/src/api/reports.rs` (append handler)

- [ ] **Step 1: Append the handler to `reports.rs`**

```rust
pub async fn fulfillment_summary(
    State(pool): State<PgPool>,
    Query(q): Query<FulfillmentQuery>,
) -> Result<Json<FulfillmentReportResp>, AppError> {
    let rows = sqlx::query_as::<_, FulfillmentRowDb>(
        r#"WITH latest_video AS (
               SELECT DISTINCT ON (tracking_number)
                   tracking_number,
                   status
               FROM packing_videos
               ORDER BY tracking_number, created_at DESC
           ),
           base AS (
               SELECT
                   COALESCE(TRIM(pl.platform), 'Unknown') AS platform,
                   COALESCE(
                       (SELECT olr.shipping_options
                        FROM order_lists_raw olr
                        WHERE olr.tracking_number = pl.tracking_number
                        LIMIT 1),
                       (SELECT oll.shipping_options
                        FROM order_lists_raw_lazada oll
                        WHERE oll.tracking_number = pl.tracking_number
                        LIMIT 1),
                       'Unknown'
                   ) AS shipping,
                   pl.packing_status,
                   pl.updated_product_lists,
                   pl.updated_at,
                   lv.status AS video_status
               FROM packing_lists pl
               LEFT JOIN latest_video lv ON lv.tracking_number = pl.tracking_number
               WHERE ($1::timestamptz IS NULL OR pl.created_at >= $1)
                 AND pl.created_at <= COALESCE($2, NOW())
           )
           SELECT
               platform,
               shipping,
               COUNT(*)::bigint                                                            AS total_orders,
               COUNT(*) FILTER (WHERE packing_status IN ('Packed','QC Hold','QC Passed'))::bigint AS packed,
               COUNT(*) FILTER (
                   WHERE packing_status = 'QC Passed'
                      OR (
                             updated_product_lists IS NOT NULL
                         AND jsonb_typeof(updated_product_lists->'items') = 'array'
                         AND (SELECT COALESCE(SUM((e->>'quantity')::int), 1)
                              FROM jsonb_array_elements(updated_product_lists->'items') AS e) = 0
                      )
               )::bigint                                                                   AS qc_passed,
               COUNT(*) FILTER (WHERE packing_status = 'QC Hold')::bigint                 AS qc_hold,
               COUNT(*) FILTER (
                   WHERE video_status = 'Completed'
                     AND packing_status IN ('Packed','QC Hold','QC Passed')
               )::bigint                                                                   AS video_complete,
               COUNT(*) FILTER (WHERE packing_status IN ('Packed','QC Hold','QC Passed'))::bigint AS video_total,
               MAX(updated_at)                                                             AS last_updated_at
           FROM base
           WHERE ($3::text IS NULL OR platform = $3)
             AND ($4::text IS NULL OR shipping = $4)
           GROUP BY platform, shipping
           ORDER BY platform, shipping"#,
    )
    .bind(q.from)
    .bind(q.to)
    .bind(q.platform.as_deref())
    .bind(q.shipping.as_deref())
    .fetch_all(&pool)
    .await?;

    let total_orders:   i64 = rows.iter().map(|r| r.total_orders).sum();
    let packed:         i64 = rows.iter().map(|r| r.packed).sum();
    let qc_passed:      i64 = rows.iter().map(|r| r.qc_passed).sum();
    let qc_hold:        i64 = rows.iter().map(|r| r.qc_hold).sum();
    let video_complete: i64 = rows.iter().map(|r| r.video_complete).sum();
    let video_total:    i64 = rows.iter().map(|r| r.video_total).sum();
    let updated_at          = rows.iter().filter_map(|r| r.last_updated_at).max();

    let summary = FulfillmentSummaryResp {
        total_orders,
        packed,
        packed_pct:        pct(packed,         total_orders),
        qc_passed,
        qc_passed_pct:     pct(qc_passed,      total_orders),
        qc_hold,
        qc_hold_pct:       pct(qc_hold,        total_orders),
        video_complete,
        video_total,
        video_complete_pct: pct(video_complete, video_total),
    };

    let report_rows = rows
        .into_iter()
        .map(|r| FulfillmentReportRow {
            packed_pct:          pct(r.packed,         r.total_orders),
            qc_passed_pct:       pct(r.qc_passed,      r.total_orders),
            qc_hold_pct:         pct(r.qc_hold,        r.total_orders),
            video_complete_pct:  pct(r.video_complete,  r.video_total),
            has_issues:          r.qc_hold > 0 || r.video_complete < r.video_total,
            platform:            r.platform,
            shipping:            r.shipping,
            total_orders:        r.total_orders,
            packed:              r.packed,
            qc_passed:           r.qc_passed,
            qc_hold:             r.qc_hold,
            video_complete:      r.video_complete,
            video_total:         r.video_total,
        })
        .collect();

    Ok(Json(FulfillmentReportResp {
        updated_at,
        summary,
        rows: report_rows,
    }))
}
```

---

## Task 4: Backend — `fulfillment_drilldown` handler

**Files:**
- Modify: `backend/src/api/reports.rs` (append handler)

- [ ] **Step 1: Append the handler to `reports.rs`**

```rust
pub async fn fulfillment_drilldown(
    State(pool): State<PgPool>,
    Query(q): Query<DrilldownQuery>,
) -> Result<Json<DrilldownResp>, AppError> {
    let qc_hold_rows = sqlx::query_as::<_, QcHoldDrillRow>(
        r#"WITH latest_video AS (
               SELECT DISTINCT ON (tracking_number)
                   tracking_number,
                   status
               FROM packing_videos
               ORDER BY tracking_number, created_at DESC
           )
           SELECT
               pl.tracking_number,
               pl.checked_by                AS qc_operator,
               pl.packing_status,
               pl.packed_by,
               sl.station_name              AS packing_station,
               lv.status                    AS video_status,
               pl.updated_at
           FROM packing_lists pl
           LEFT JOIN station_lists sl ON sl.id = pl.packing_station_id
           LEFT JOIN latest_video  lv ON lv.tracking_number = pl.tracking_number
           WHERE pl.packing_status = 'QC Hold'
             AND COALESCE(TRIM(pl.platform), 'Unknown') = $1
             AND COALESCE(
                     (SELECT olr.shipping_options FROM order_lists_raw olr
                      WHERE olr.tracking_number = pl.tracking_number LIMIT 1),
                     (SELECT oll.shipping_options FROM order_lists_raw_lazada oll
                      WHERE oll.tracking_number = pl.tracking_number LIMIT 1),
                     'Unknown'
                 ) = $2
             AND ($3::timestamptz IS NULL OR pl.created_at >= $3)
             AND pl.created_at <= COALESCE($4, NOW())
           ORDER BY pl.updated_at DESC"#,
    )
    .bind(&q.platform)
    .bind(&q.shipping)
    .bind(q.from)
    .bind(q.to)
    .fetch_all(&pool)
    .await
    .unwrap_or_default();

    let video_incomplete_rows = sqlx::query_as::<_, VideoIncompleteDrillRow>(
        r#"WITH latest_video AS (
               SELECT DISTINCT ON (tracking_number)
                   tracking_number,
                   status
               FROM packing_videos
               ORDER BY tracking_number, created_at DESC
           )
           SELECT
               pl.tracking_number,
               pl.packed_by                 AS operator,
               sl.station_name              AS station,
               lv.status                    AS video_status,
               pl.updated_at
           FROM packing_lists pl
           LEFT JOIN station_lists sl ON sl.id = pl.packing_station_id
           LEFT JOIN latest_video  lv ON lv.tracking_number = pl.tracking_number
           WHERE pl.packing_status IN ('Packed','QC Hold','QC Passed')
             AND COALESCE(lv.status, '') != 'Completed'
             AND COALESCE(TRIM(pl.platform), 'Unknown') = $1
             AND COALESCE(
                     (SELECT olr.shipping_options FROM order_lists_raw olr
                      WHERE olr.tracking_number = pl.tracking_number LIMIT 1),
                     (SELECT oll.shipping_options FROM order_lists_raw_lazada oll
                      WHERE oll.tracking_number = pl.tracking_number LIMIT 1),
                     'Unknown'
                 ) = $2
             AND ($3::timestamptz IS NULL OR pl.created_at >= $3)
             AND pl.created_at <= COALESCE($4, NOW())
           ORDER BY pl.updated_at DESC"#,
    )
    .bind(&q.platform)
    .bind(&q.shipping)
    .bind(q.from)
    .bind(q.to)
    .fetch_all(&pool)
    .await
    .unwrap_or_default();

    Ok(Json(DrilldownResp {
        platform:         q.platform,
        shipping:         q.shipping,
        qc_hold:          qc_hold_rows,
        video_incomplete: video_incomplete_rows,
    }))
}
```

---

## Task 5: Backend — download log and list handlers

**Files:**
- Modify: `backend/src/api/reports.rs` (append two handlers)

- [ ] **Step 1: Append both handlers to `reports.rs`**

```rust
pub async fn log_download(
    State(pool): State<PgPool>,
    Json(body): Json<LogDownloadReq>,
) -> Result<StatusCode, AppError> {
    sqlx::query(
        "INSERT INTO report_downloads (report_type, filters) VALUES ($1, $2)",
    )
    .bind(&body.report_type)
    .bind(&body.filters)
    .execute(&pool)
    .await?;

    Ok(StatusCode::CREATED)
}

pub async fn list_downloads(
    State(pool): State<PgPool>,
) -> Result<Json<Vec<ReportDownloadRow>>, AppError> {
    let rows = sqlx::query_as::<_, ReportDownloadRow>(
        "SELECT id, report_type, filters, downloaded_at
         FROM report_downloads
         ORDER BY downloaded_at DESC
         LIMIT 100",
    )
    .fetch_all(&pool)
    .await?;

    Ok(Json(rows))
}
```

---

## Task 6: Backend — register routes, verify build

**Files:**
- Modify: `backend/src/api/mod.rs`

- [ ] **Step 1: Add `mod reports;` to the module declarations**

In `backend/src/api/mod.rs`, add after the last `mod` line (before the `use` block):

```rust
mod reports;
```

- [ ] **Step 2: Add 4 routes to the router**

In `backend/src/api/mod.rs`, inside the `Router::new()` chain, add before `.layer(cors)`:

```rust
        .route("/reports/fulfillment",           get(reports::fulfillment_summary))
        .route("/reports/fulfillment/drilldown", get(reports::fulfillment_drilldown))
        .route("/reports/downloads",             get(reports::list_downloads).post(reports::log_download))
```

- [ ] **Step 3: Verify backend compiles**

```bash
cd backend && SQLX_OFFLINE=true cargo check
```

Expected: `Finished` with no errors. Fix any type mismatches before continuing.

---

## Task 7: Frontend — add TypeScript types

**Files:**
- Modify: `frontend/app/types.ts`

- [ ] **Step 1: Append report types to `types.ts`**

At the end of `frontend/app/types.ts` add:

```typescript
// ── Reports ──────────────────────────────────────────────────────────────────

export interface FulfillmentSummary {
  totalOrders: number;
  packed: number;
  packedPct: number;
  qcPassed: number;
  qcPassedPct: number;
  qcHold: number;
  qcHoldPct: number;
  videoComplete: number;
  videoTotal: number;
  videoCompletePct: number;
}

export interface FulfillmentRow {
  platform: string;
  shipping: string;
  totalOrders: number;
  packed: number;
  packedPct: number;
  qcPassed: number;
  qcPassedPct: number;
  qcHold: number;
  qcHoldPct: number;
  videoComplete: number;
  videoTotal: number;
  videoCompletePct: number;
  hasIssues: boolean;
}

export interface FulfillmentReport {
  updatedAt: string | null;
  summary: FulfillmentSummary;
  rows: FulfillmentRow[];
}

export interface QcHoldDrillItem {
  trackingNumber: string;
  qcOperator: string | null;
  packingStatus: string | null;
  packedBy: string | null;
  packingStation: string | null;
  videoStatus: string | null;
  updatedAt: string | null;
}

export interface VideoIncompleteDrillItem {
  trackingNumber: string;
  operator: string | null;
  station: string | null;
  videoStatus: string | null;
  updatedAt: string | null;
}

export interface DrilldownResponse {
  platform: string;
  shipping: string;
  qcHold: QcHoldDrillItem[];
  videoIncomplete: VideoIncompleteDrillItem[];
}

export interface ReportDownload {
  id: number;
  reportType: string;
  filters: { from: string; to: string; platform: string; shipping: string };
  downloadedAt: string;
}

export type ReportType = "fulfillment_overview";
```

---

## Task 8: Frontend — update Sidebar

**Files:**
- Modify: `frontend/app/components/Sidebar.tsx`

- [ ] **Step 1: Add ReportsIcon SVG component**

In `Sidebar.tsx`, add after the `MonitorIcon` component (before `const navItems`):

```tsx
const ReportsIcon = () => (
  <svg xmlns="http://www.w3.org/2000/svg" className="h-4 w-4 shrink-0" viewBox="0 0 20 20" fill="currentColor">
    <path d="M2 11a1 1 0 011-1h2a1 1 0 011 1v5a1 1 0 01-1 1H3a1 1 0 01-1-1v-5zm6-4a1 1 0 011-1h2a1 1 0 011 1v9a1 1 0 01-1 1H9a1 1 0 01-1-1V7zm6-3a1 1 0 011-1h2a1 1 0 011 1v12a1 1 0 01-1 1h-2a1 1 0 01-1-1V4z" />
  </svg>
);
```

- [ ] **Step 2: Insert Reports nav item after Timeline**

In `navItems`, add `{ href: "/reports", label: "Reports", Icon: ReportsIcon }` after the Timeline entry:

```tsx
const navItems = [
  { href: "/",          label: "Home",            Icon: HomeIcon      },
  { href: "/stations",  label: "Live Stations",   Icon: MonitorIcon   },
  { href: "/tracking",  label: "Timeline",        Icon: TrackingIcon  },
  { href: "/reports",   label: "Reports",         Icon: ReportsIcon   },
  { href: "/qc",        label: "QC Station",      Icon: QcIcon        },
  { href: "/packing",   label: "Packing Station", Icon: PackingIcon   },
  { href: "/logs",      label: "Logs",            Icon: LogsIcon      },
];
```

---

## Task 9: Frontend — hooks

**Files:**
- Create: `frontend/app/hooks/useFulfillmentReport.ts`
- Create: `frontend/app/hooks/useDrilldown.ts`
- Create: `frontend/app/hooks/useDownloadHistory.ts`

- [ ] **Step 1: Create `useFulfillmentReport.ts`**

```typescript
"use client";

import { useCallback, useEffect, useState } from "react";
import { FulfillmentReport, TimeFilter } from "../types";

const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";

async function safeJson<T>(res: Response, fallback: T): Promise<T> {
  const text = await res.text();
  if (!text.trim()) return fallback;
  try { return JSON.parse(text) as T; } catch { return fallback; }
}

function todayStr() {
  return new Date().toISOString().slice(0, 10);
}

function timeRange(
  filter: TimeFilter,
  customFrom: string,
  customTo: string,
): { from?: string; to?: string } {
  const now = new Date();
  if (filter === "today") {
    const start = new Date(now);
    start.setHours(0, 0, 0, 0);
    return { from: start.toISOString() };
  }
  if (filter === "yesterday") {
    const start = new Date(now);
    start.setDate(now.getDate() - 1);
    start.setHours(0, 0, 0, 0);
    const end = new Date(now);
    end.setDate(now.getDate() - 1);
    end.setHours(23, 59, 59, 999);
    return { from: start.toISOString(), to: end.toISOString() };
  }
  if (filter === "3days") {
    const start = new Date(now);
    start.setDate(now.getDate() - 3);
    start.setHours(0, 0, 0, 0);
    return { from: start.toISOString() };
  }
  if (filter === "7days") {
    const start = new Date(now);
    start.setDate(now.getDate() - 7);
    start.setHours(0, 0, 0, 0);
    return { from: start.toISOString() };
  }
  if (filter === "custom") {
    const from = customFrom ? new Date(customFrom + "T00:00:00").toISOString() : undefined;
    const to   = customTo   ? new Date(customTo   + "T23:59:59.999").toISOString() : undefined;
    return { from, to };
  }
  return {};
}

const EMPTY_REPORT: FulfillmentReport = {
  updatedAt: null,
  summary: {
    totalOrders: 0, packed: 0, packedPct: 0,
    qcPassed: 0, qcPassedPct: 0, qcHold: 0, qcHoldPct: 0,
    videoComplete: 0, videoTotal: 0, videoCompletePct: 0,
  },
  rows: [],
};

export function useFulfillmentReport() {
  const [data,       setData]       = useState<FulfillmentReport>(EMPTY_REPORT);
  const [loading,    setLoading]    = useState(true);
  const [time,       setTime]       = useState<TimeFilter>("today");
  const [customFrom, setCustomFrom] = useState(todayStr());
  const [customTo,   setCustomTo]   = useState(todayStr());
  const [platform,   setPlatform]   = useState<string>("all");
  const [shipping,   setShipping]   = useState<string>("all");

  const fetchData = useCallback(async (
    t: TimeFilter, cf: string, ct: string, plt: string, shp: string,
  ) => {
    setLoading(true);
    const range = timeRange(t, cf, ct);
    const params = new URLSearchParams();
    if (range.from) params.set("from", range.from);
    if (range.to)   params.set("to",   range.to);
    if (plt !== "all") params.set("platform", plt);
    if (shp !== "all") params.set("shipping", shp);
    try {
      const res  = await fetch(`${API}/reports/fulfillment?${params}`);
      const json = await safeJson<FulfillmentReport>(res, EMPTY_REPORT);
      setData(json);
    } catch (err) {
      console.error("useFulfillmentReport:", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData(time, customFrom, customTo, platform, shipping);
  }, [time, customFrom, customTo, platform, shipping, fetchData]);

  return {
    data, loading,
    time, setTime,
    customFrom, setCustomFrom,
    customTo,   setCustomTo,
    platform,   setPlatform,
    shipping,   setShipping,
    refresh: () => fetchData(time, customFrom, customTo, platform, shipping),
    timeRange: () => timeRange(time, customFrom, customTo),
  };
}
```

- [ ] **Step 2: Create `useDrilldown.ts`**

```typescript
"use client";

import { useCallback, useEffect, useState } from "react";
import { DrilldownResponse } from "../types";

const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";

async function safeJson<T>(res: Response, fallback: T): Promise<T> {
  const text = await res.text();
  if (!text.trim()) return fallback;
  try { return JSON.parse(text) as T; } catch { return fallback; }
}

const EMPTY: DrilldownResponse = {
  platform: "", shipping: "", qcHold: [], videoIncomplete: [],
};

export function useDrilldown(
  platform: string,
  shipping: string,
  from: string | undefined,
  to: string | undefined,
  enabled: boolean,
) {
  const [data,    setData]    = useState<DrilldownResponse>(EMPTY);
  const [loading, setLoading] = useState(false);
  const [error,   setError]   = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    if (!enabled) return;
    setLoading(true);
    setError(null);
    const params = new URLSearchParams({ platform, shipping });
    if (from) params.set("from", from);
    if (to)   params.set("to",   to);
    try {
      const res  = await fetch(`${API}/reports/fulfillment/drilldown?${params}`);
      const json = await safeJson<DrilldownResponse>(res, EMPTY);
      setData(json);
    } catch (err) {
      console.error("useDrilldown:", err);
      setError("Failed to load drill-down data");
    } finally {
      setLoading(false);
    }
  }, [platform, shipping, from, to, enabled]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { data, loading, error, refetch: fetchData };
}
```

- [ ] **Step 3: Create `useDownloadHistory.ts`**

```typescript
"use client";

import { useCallback, useEffect, useState } from "react";
import { ReportDownload } from "../types";

const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";

async function safeJson<T>(res: Response, fallback: T): Promise<T> {
  const text = await res.text();
  if (!text.trim()) return fallback;
  try { return JSON.parse(text) as T; } catch { return fallback; }
}

export function useDownloadHistory() {
  const [history,  setHistory]  = useState<ReportDownload[]>([]);
  const [loading,  setLoading]  = useState(true);

  const fetchHistory = useCallback(async () => {
    setLoading(true);
    try {
      const res  = await fetch(`${API}/reports/downloads`);
      const json = await safeJson<ReportDownload[]>(res, []);
      setHistory(json);
    } catch (err) {
      console.error("useDownloadHistory:", err);
    } finally {
      setLoading(false);
    }
  }, []);

  const logDownload = useCallback(async (filters: {
    from: string; to: string; platform: string; shipping: string;
  }) => {
    try {
      await fetch(`${API}/reports/downloads`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reportType: "fulfillment_overview", filters }),
      });
      await fetchHistory();
    } catch (err) {
      console.error("logDownload:", err);
    }
  }, [fetchHistory]);

  useEffect(() => { fetchHistory(); }, [fetchHistory]);

  return { history, loading, logDownload, refresh: fetchHistory };
}
```

---

## Task 10: Frontend — `FulfillmentReport.tsx`

**Files:**
- Create: `frontend/app/components/FulfillmentReport.tsx`

- [ ] **Step 1: Create the component**

```tsx
"use client";

import { FulfillmentReport as FulfillmentReportType, FulfillmentRow, TimeFilter } from "../types";
import { FulfillmentDrilldown } from "./FulfillmentDrilldown";

interface Props {
  data: FulfillmentReportType;
  loading: boolean;
  time: TimeFilter;
  setTime: (v: TimeFilter) => void;
  customFrom: string;
  setCustomFrom: (v: string) => void;
  customTo: string;
  setCustomTo: (v: string) => void;
  platform: string;
  setPlatform: (v: string) => void;
  shipping: string;
  setShipping: (v: string) => void;
  rangeFrom: string | undefined;
  rangeTo: string | undefined;
  forPrint: boolean;
}

function fmt(n: number, pct: number) {
  return `${n} (${pct}%)`;
}

function fmtVideo(complete: number, total: number, pct: number) {
  return total === 0 ? "—" : `${complete}/${total} (${pct}%)`;
}

function fmtTs(ts: string | null) {
  if (!ts) return "—";
  return new Date(ts).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

const TIME_OPTIONS: { value: TimeFilter; label: string }[] = [
  { value: "today",     label: "Today"      },
  { value: "yesterday", label: "Yesterday"  },
  { value: "3days",     label: "Last 3 days" },
  { value: "7days",     label: "Last 7 days" },
  { value: "custom",    label: "Custom"     },
];

const PLATFORM_OPTIONS = ["all", "Shopee", "Lazada", "Tiktok"];
const SHIPPING_OPTIONS = ["all", "Standard", "Express", "Economy"];

export function FulfillmentReport({
  data, loading, time, setTime, customFrom, setCustomFrom,
  customTo, setCustomTo, platform, setPlatform,
  shipping, setShipping, rangeFrom, rangeTo, forPrint,
}: Props) {
  const { summary, rows } = data;
  const issueRows = rows.filter((r) => r.hasIssues);

  return (
    <div className="space-y-4">
      {/* Updated timestamp */}
      {data.updatedAt && (
        <p className="text-right text-xs text-gray-400 dark:text-gray-500 print:hidden">
          Updated: {fmtTs(data.updatedAt)}
        </p>
      )}

      {/* Filters */}
      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-gray-200 bg-white p-3 dark:border-gray-700 dark:bg-gray-800 print:hidden">
        <select
          value={time}
          onChange={(e) => setTime(e.target.value as TimeFilter)}
          className="rounded border border-gray-300 bg-white px-2 py-1 text-sm dark:border-gray-600 dark:bg-gray-700 dark:text-gray-100"
        >
          {TIME_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </select>
        {time === "custom" && (
          <>
            <input type="date" value={customFrom} onChange={(e) => setCustomFrom(e.target.value)}
              className="rounded border border-gray-300 bg-white px-2 py-1 text-sm dark:border-gray-600 dark:bg-gray-700 dark:text-gray-100" />
            <span className="text-sm text-gray-500">to</span>
            <input type="date" value={customTo} onChange={(e) => setCustomTo(e.target.value)}
              className="rounded border border-gray-300 bg-white px-2 py-1 text-sm dark:border-gray-600 dark:bg-gray-700 dark:text-gray-100" />
          </>
        )}
        <select
          value={platform}
          onChange={(e) => setPlatform(e.target.value)}
          className="rounded border border-gray-300 bg-white px-2 py-1 text-sm dark:border-gray-600 dark:bg-gray-700 dark:text-gray-100"
        >
          {PLATFORM_OPTIONS.map((o) => (
            <option key={o} value={o}>{o === "all" ? "All Platforms" : o}</option>
          ))}
        </select>
        <select
          value={shipping}
          onChange={(e) => setShipping(e.target.value)}
          className="rounded border border-gray-300 bg-white px-2 py-1 text-sm dark:border-gray-600 dark:bg-gray-700 dark:text-gray-100"
        >
          {SHIPPING_OPTIONS.map((o) => (
            <option key={o} value={o}>{o === "all" ? "All Shipping" : o}</option>
          ))}
        </select>
      </div>

      {/* Summary bar */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
        {[
          { label: "Total Orders",    value: String(summary.totalOrders) },
          { label: "Packed",          value: `${summary.packed} (${summary.packedPct}%)` },
          { label: "QC Passed",       value: `${summary.qcPassed} (${summary.qcPassedPct}%)` },
          { label: "QC Hold",         value: `${summary.qcHold} (${summary.qcHoldPct}%)`, warn: summary.qcHold > 0 },
          { label: "Video Complete",  value: fmtVideo(summary.videoComplete, summary.videoTotal, summary.videoCompletePct) },
        ].map(({ label, value, warn }) => (
          <div
            key={label}
            className={`rounded-lg border p-3 ${
              warn
                ? "border-orange-200 bg-orange-50 dark:border-orange-800 dark:bg-orange-950/30"
                : "border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-800"
            }`}
          >
            <p className="text-xs text-gray-500 dark:text-gray-400">{label}</p>
            <p className={`mt-1 text-lg font-semibold ${warn ? "text-[#EE4D2D]" : "text-gray-900 dark:text-gray-100"}`}>
              {loading ? "—" : value}
            </p>
          </div>
        ))}
      </div>

      {/* Breakdown table */}
      <div className="overflow-x-auto rounded-lg border border-gray-200 dark:border-gray-700">
        <table className="min-w-full text-sm">
          <thead className="bg-gray-50 dark:bg-gray-800">
            <tr>
              {["Platform", "Shipping", "Orders", "Packed", "QC Passed", "QC Hold", "Video"].map((h) => (
                <th key={h} className="px-3 py-2 text-left text-xs font-semibold text-gray-500 dark:text-gray-400">
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100 bg-white dark:divide-gray-700 dark:bg-gray-900">
            {loading ? (
              <tr><td colSpan={7} className="px-3 py-8 text-center text-sm text-gray-400">Loading…</td></tr>
            ) : rows.length === 0 ? (
              <tr><td colSpan={7} className="px-3 py-8 text-center text-sm text-gray-400">No orders found</td></tr>
            ) : (
              rows.map((row) => <BreakdownRow key={`${row.platform}-${row.shipping}`} row={row} />)
            )}
          </tbody>
        </table>
      </div>

      {/* Drill-down panels */}
      {!loading && issueRows.length > 0 && (
        <div className="space-y-4">
          <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-300">Drill-down Details</h3>
          {issueRows.map((row) => (
            <FulfillmentDrilldown
              key={`${row.platform}-${row.shipping}`}
              platform={row.platform}
              shipping={row.shipping}
              from={rangeFrom}
              to={rangeTo}
              forPrint={forPrint}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function BreakdownRow({ row }: { row: FulfillmentRow }) {
  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-800/50">
      <td className="px-3 py-2 font-medium text-gray-900 dark:text-gray-100">{row.platform}</td>
      <td className="px-3 py-2 text-gray-600 dark:text-gray-300">{row.shipping}</td>
      <td className="px-3 py-2 text-gray-900 dark:text-gray-100">{row.totalOrders}</td>
      <td className="px-3 py-2">{fmt(row.packed, row.packedPct)}</td>
      <td className="px-3 py-2">{fmt(row.qcPassed, row.qcPassedPct)}</td>
      <td className={`px-3 py-2 font-medium ${row.qcHold > 0 ? "text-[#EE4D2D]" : "text-gray-600 dark:text-gray-300"}`}>
        {row.qcHold > 0 ? `${row.qcHold} (${row.qcHoldPct}%) ⚠` : `${row.qcHold}`}
      </td>
      <td className={`px-3 py-2 ${row.videoComplete < row.videoTotal ? "text-[#EE4D2D] font-medium" : "text-gray-600 dark:text-gray-300"}`}>
        {fmtVideo(row.videoComplete, row.videoTotal, row.videoCompletePct)}
      </td>
    </tr>
  );
}
```

---

## Task 11: Frontend — `FulfillmentDrilldown.tsx`

**Files:**
- Create: `frontend/app/components/FulfillmentDrilldown.tsx`

- [ ] **Step 1: Create the component**

```tsx
"use client";

import { useState, useEffect } from "react";
import { useDrilldown } from "../hooks/useDrilldown";
import { QcHoldDrillItem, VideoIncompleteDrillItem } from "../types";

interface Props {
  platform: string;
  shipping: string;
  from: string | undefined;
  to: string | undefined;
  forPrint: boolean;
}

function fmtTs(ts: string | null) {
  if (!ts) return "—";
  return new Date(ts).toLocaleString("en-GB", {
    hour: "2-digit", minute: "2-digit", second: "2-digit",
  });
}

export function FulfillmentDrilldown({ platform, shipping, from, to, forPrint }: Props) {
  const [expanded, setExpanded] = useState(false);
  const isOpen = expanded || forPrint;
  const { data, loading, error } = useDrilldown(platform, shipping, from, to, isOpen);

  useEffect(() => {
    if (forPrint) setExpanded(true);
  }, [forPrint]);

  const hasQcHold       = data.qcHold.length > 0;
  const hasVideoIssues  = data.videoIncomplete.length > 0;

  return (
    <div className="rounded-lg border border-gray-200 dark:border-gray-700">
      {/* Header */}
      <button
        onClick={() => setExpanded((v) => !v)}
        className="flex w-full items-center justify-between px-4 py-3 text-left hover:bg-gray-50 dark:hover:bg-gray-800/50 print:hidden"
      >
        <span className="text-sm font-semibold text-gray-700 dark:text-gray-300">
          {platform} / {shipping}
        </span>
        <span className="text-xs text-gray-400">{isOpen ? "▲" : "▼"}</span>
      </button>
      {/* Print header (always visible in print) */}
      <div className="hidden px-4 py-3 print:block">
        <span className="text-sm font-semibold">{platform} / {shipping}</span>
      </div>

      {isOpen && (
        <div className="border-t border-gray-100 px-4 pb-4 pt-3 dark:border-gray-700 space-y-4">
          {loading && <p className="text-sm text-gray-400">Loading…</p>}
          {error   && <p className="text-sm text-red-500">{error}</p>}

          {!loading && !error && (
            <>
              {/* QC Hold table */}
              {hasQcHold ? (
                <div>
                  <h4 className="mb-2 text-xs font-semibold text-gray-500 dark:text-gray-400">
                    QC Hold ({data.qcHold.length})
                  </h4>
                  <div className="overflow-x-auto">
                    <table className="min-w-full text-xs">
                      <thead className="bg-gray-50 dark:bg-gray-800">
                        <tr>
                          {["Tracking", "QC Operator", "Pack Status", "Packer", "Pack Station", "Video Status", "Updated"].map((h) => (
                            <th key={h} className="px-2 py-1.5 text-left font-semibold text-gray-500 dark:text-gray-400">{h}</th>
                          ))}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
                        {data.qcHold.map((item) => (
                          <QcHoldDrillRowComponent key={item.trackingNumber} item={item} />
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ) : (
                <p className="text-xs text-gray-400">No QC Hold orders</p>
              )}

              {/* Video Incomplete table */}
              {hasVideoIssues ? (
                <div>
                  <h4 className="mb-2 text-xs font-semibold text-gray-500 dark:text-gray-400">
                    Video Incomplete ({data.videoIncomplete.length})
                  </h4>
                  <div className="overflow-x-auto">
                    <table className="min-w-full text-xs">
                      <thead className="bg-gray-50 dark:bg-gray-800">
                        <tr>
                          {["Tracking", "Operator", "Station", "Video Status", "Updated"].map((h) => (
                            <th key={h} className="px-2 py-1.5 text-left font-semibold text-gray-500 dark:text-gray-400">{h}</th>
                          ))}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
                        {data.videoIncomplete.map((item) => (
                          <VideoIncompleteDrillRowComponent key={item.trackingNumber} item={item} />
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ) : (
                <p className="text-xs text-gray-400">No video issues</p>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function QcHoldDrillRowComponent({ item }: { item: QcHoldDrillItem }) {
  const isPacked = item.packingStatus === "Packed" || item.packingStatus === "QC Hold" || item.packingStatus === "QC Passed";
  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-800/50">
      <td className="px-2 py-1.5 font-mono text-gray-900 dark:text-gray-100">{item.trackingNumber}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{item.qcOperator ?? "—"}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{item.packingStatus ?? "—"}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{isPacked ? (item.packedBy ?? "—") : "—"}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{isPacked ? (item.packingStation ?? "—") : "—"}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{isPacked ? (item.videoStatus ?? "—") : "—"}</td>
      <td className="px-2 py-1.5 text-gray-400">{fmtTs(item.updatedAt)}</td>
    </tr>
  );
}

function VideoIncompleteDrillRowComponent({ item }: { item: VideoIncompleteDrillItem }) {
  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-800/50">
      <td className="px-2 py-1.5 font-mono text-gray-900 dark:text-gray-100">{item.trackingNumber}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{item.operator ?? "—"}</td>
      <td className="px-2 py-1.5 text-gray-700 dark:text-gray-300">{item.station ?? "—"}</td>
      <td className="px-2 py-1.5 text-[#EE4D2D] font-medium">{item.videoStatus ?? "Missing"}</td>
      <td className="px-2 py-1.5 text-gray-400">{fmtTs(item.updatedAt)}</td>
    </tr>
  );
}
```

---

## Task 12: Frontend — `DownloadHistory.tsx` + `reports/page.tsx`

**Files:**
- Create: `frontend/app/components/DownloadHistory.tsx`
- Create: `frontend/app/reports/page.tsx`

- [ ] **Step 1: Create `DownloadHistory.tsx`**

```tsx
"use client";

import { ReportDownload } from "../types";

interface Props {
  history: ReportDownload[];
  loading: boolean;
}

function fmtTs(ts: string) {
  return new Date(ts).toLocaleString("en-GB", {
    year: "numeric", month: "short", day: "numeric",
    hour: "2-digit", minute: "2-digit",
  });
}

function fmtFilters(f: ReportDownload["filters"]) {
  const parts: string[] = [];
  if (f.platform && f.platform !== "all") parts.push(f.platform);
  if (f.shipping  && f.shipping  !== "all") parts.push(f.shipping);
  if (f.from) {
    const from = new Date(f.from).toLocaleDateString("en-GB");
    const to   = f.to ? new Date(f.to).toLocaleDateString("en-GB") : "now";
    parts.push(`${from} – ${to}`);
  }
  return parts.join(" · ") || "All data";
}

export function DownloadHistory({ history, loading }: Props) {
  return (
    <div className="rounded-lg border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900 print:hidden">
      <div className="border-b border-gray-100 px-4 py-3 dark:border-gray-700">
        <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-300">Download History</h3>
      </div>
      {loading ? (
        <p className="px-4 py-4 text-sm text-gray-400">Loading…</p>
      ) : history.length === 0 ? (
        <p className="px-4 py-4 text-sm text-gray-400">No downloads yet</p>
      ) : (
        <ul className="divide-y divide-gray-100 dark:divide-gray-700">
          {history.map((item) => (
            <li key={item.id} className="flex items-center justify-between px-4 py-2">
              <div>
                <p className="text-xs font-medium text-gray-700 dark:text-gray-300">
                  Order Fulfillment Overview
                </p>
                <p className="text-xs text-gray-400">{fmtFilters(item.filters)}</p>
              </div>
              <p className="text-xs text-gray-400 shrink-0 ml-4">{fmtTs(item.downloadedAt)}</p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

- [ ] **Step 2: Create `frontend/app/reports/page.tsx`**

First create the directory:
```bash
mkdir -p frontend/app/reports
```

Then create `frontend/app/reports/page.tsx`:

```tsx
"use client";

import { useState, useCallback } from "react";
import { useFulfillmentReport } from "../hooks/useFulfillmentReport";
import { useDownloadHistory } from "../hooks/useDownloadHistory";
import { FulfillmentReport } from "../components/FulfillmentReport";
import { DownloadHistory } from "../components/DownloadHistory";

const REPORT_TYPES = [
  { value: "fulfillment_overview", label: "Order Fulfillment Overview" },
];

export default function ReportsPage() {
  const [reportType, setReportType] = useState("fulfillment_overview");
  const [forPrint,   setForPrint]   = useState(false);

  const report   = useFulfillmentReport();
  const downloads = useDownloadHistory();

  const range     = report.timeRange();
  const rangeFrom = range.from;
  const rangeTo   = range.to;

  const handleDownloadPdf = useCallback(async () => {
    // Force all drilldowns open before print
    setForPrint(true);
    // Give React one tick to expand all panels, then print
    await new Promise<void>((resolve) => {
      const handler = () => {
        window.removeEventListener("afterprint", handler);
        resolve();
      };
      window.addEventListener("afterprint", handler);
      setTimeout(() => window.print(), 80);
    });
    setForPrint(false);
    // Log the download
    await downloads.logDownload({
      from:     rangeFrom ?? "",
      to:       rangeTo   ?? new Date().toISOString(),
      platform: report.platform,
      shipping: report.shipping,
    });
  }, [downloads, rangeFrom, rangeTo, report.platform, report.shipping]);

  return (
    <div className="min-h-screen bg-gray-50 p-6 dark:bg-gray-950">
      {/* Print header (hidden on screen) */}
      <div className="hidden print:block mb-6">
        <h1 className="text-xl font-bold">Order Fulfillment Overview</h1>
        <p className="text-sm text-gray-500">
          {rangeFrom ? new Date(rangeFrom).toLocaleDateString("en-GB") : ""}
          {rangeTo   ? ` – ${new Date(rangeTo).toLocaleDateString("en-GB")}` : ""}
        </p>
      </div>

      {/* Page header */}
      <div className="mb-6 flex items-center justify-between print:hidden">
        <div>
          <h1 className="text-xl font-bold text-gray-900 dark:text-gray-100">Reports</h1>
          <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">Order analytics and quality overview</p>
        </div>
        <button
          onClick={handleDownloadPdf}
          className="flex items-center gap-2 rounded-lg bg-[#EE4D2D] px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-[#d94429] transition-colors"
        >
          <svg xmlns="http://www.w3.org/2000/svg" className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M3 17a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm3.293-7.707a1 1 0 011.414 0L9 10.586V3a1 1 0 112 0v7.586l1.293-1.293a1 1 0 111.414 1.414l-3 3a1 1 0 01-1.414 0l-3-3a1 1 0 010-1.414z" clipRule="evenodd" />
          </svg>
          Download PDF
        </button>
      </div>

      {/* Report type selector */}
      <div className="mb-4 print:hidden">
        <select
          value={reportType}
          onChange={(e) => setReportType(e.target.value)}
          className="rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
        >
          {REPORT_TYPES.map((r) => (
            <option key={r.value} value={r.value}>{r.label}</option>
          ))}
        </select>
      </div>

      {/* Report content */}
      <div className="space-y-6">
        {reportType === "fulfillment_overview" && (
          <FulfillmentReport
            data={report.data}
            loading={report.loading}
            time={report.time}
            setTime={report.setTime}
            customFrom={report.customFrom}
            setCustomFrom={report.setCustomFrom}
            customTo={report.customTo}
            setCustomTo={report.setCustomTo}
            platform={report.platform}
            setPlatform={report.setPlatform}
            shipping={report.shipping}
            setShipping={report.setShipping}
            rangeFrom={rangeFrom}
            rangeTo={rangeTo}
            forPrint={forPrint}
          />
        )}

        {/* Download history */}
        <DownloadHistory history={downloads.history} loading={downloads.loading} />
      </div>

      {/* Print styles */}
      <style>{`
        @media print {
          body { background: white; }
          @page { margin: 1.5cm; }
        }
      `}</style>
    </div>
  );
}
```

---

## Task 13: Frontend — verify full build

- [ ] **Step 1: Run type-check + build**

```bash
cd frontend && npm run build
```

Expected: Build completes with no TypeScript errors. If errors appear:
- Type mismatch between hook return and component props → check that `FulfillmentReport` props match what `useFulfillmentReport` returns
- Missing import → check all `import` paths in new files
- `FulfillmentReport` naming conflict (component vs type) → the component file exports `FulfillmentReport` function, the type is `FulfillmentReport` from `types.ts`. Resolve by importing type as `FulfillmentReport as FulfillmentReportType` (already done in component)

- [ ] **Step 2: Run lint**

```bash
cd frontend && npm run lint
```

Expected: No errors. Fix any lint warnings before proceeding.

---

## Self-Review Checklist

- [x] DB migration covers `report_downloads` table
- [x] All 4 backend handlers: `fulfillment_summary`, `fulfillment_drilldown`, `log_download`, `list_downloads`
- [x] Routes registered in `mod.rs`
- [x] All TypeScript types added to `types.ts`
- [x] Sidebar updated with Reports nav item after Timeline
- [x] All 3 hooks: `useFulfillmentReport`, `useDrilldown`, `useDownloadHistory`
- [x] `FulfillmentReport` component: filters + summary + breakdown table + drill-down panels
- [x] `FulfillmentDrilldown`: QC Hold table (with packing status/operator/video when packed) + Video Incomplete table
- [x] `DownloadHistory`: renders history list with filters summary
- [x] `reports/page.tsx`: report type dropdown, PDF button, print styles, log on print
- [x] `forPrint` propagation forces all drilldowns open before `window.print()`
- [x] No commits — each task ends with verify step
- [x] No placeholders
