# Reports: Order Fulfillment Overview — Design Spec

**Date:** 2026-04-26
**Branch:** dev-1.3

---

## Overview

Add a **Reports** section to the frontend dashboard with an initial **Order Fulfillment Overview** report. The report groups order volume and quality metrics by platform and shipping option, exposes per-group drill-down for QC Hold and Video Incomplete orders, and supports PDF export with download history persisted in PostgreSQL.

---

## 1. Navigation

- Add **Reports** as a sidebar nav item in `frontend/app/components/Sidebar.tsx`
- Position: directly after **Timeline** (`/tracking`) in `navItems`
- Route: `/reports`

---

## 2. UI Structure

```
/reports
  └─ Report type dropdown  [Order Fulfillment Overview ▼]
  └─ Filters bar           Period | Platform | Shipping
  └─ Summary bar           Total | Packed % | QC Pass % | QC Hold % | Video %
  └─ Breakdown table       Platform → Shipping rows with metrics
  └─ Drill-down panels     Auto-rendered for groups with QC Hold > 0 OR Video Incomplete > 0
  └─ Download PDF button
  └─ Download History list
```

### Filters

| Filter | Values |
|--------|--------|
| Period | Today / Yesterday / 3 days / 7 days / Custom (matches existing TimeFilter pattern) |
| Platform | All + distinct values from `packing_lists.platform` |
| Shipping | All + distinct values from `order_lists_raw` + `order_lists_raw_lazada` (dynamic query) |

- `from` defaults to start of today
- `to` defaults to `NOW()` (current time) when not specified — never left unbounded

### Summary Bar Metrics

- Total Orders
- Packed: count (% of total)
- QC Passed: count (% of total)
- QC Hold: count (% of total)
- Video Complete: `completed_count / total_packed` as percentage

### Breakdown Table

Grouped by **Platform** (primary) → **Shipping** (secondary).

Columns: Platform | Shipping | Orders | Packed | QC Passed | QC Hold | Video

All counts include percentage relative to total orders for that platform/shipping group.

`hasIssues = qcHold > 0 OR videoIncomplete > 0` — drives drill-down rendering.

### Drill-down Panels

Rendered below the breakdown table. One panel per platform/shipping group where `hasIssues = true`. Each panel has two sub-tables:

**QC Hold sub-table columns:**

| Column | Source |
|--------|--------|
| Tracking Number | `packing_lists.tracking_number` |
| QC Operator | `packing_lists.checked_by` |
| Packing Status | `packing_lists.packing_status` |
| Packer | `packing_lists.packed_by` (null if not yet packed) |
| Packing Station | `station_lists.station_name` via `packing_station_id` (null if not yet packed) |
| Video Status | latest `packing_videos.status` for tracking number (null if not yet packed) |
| Updated | `packing_lists.updated_at` |

Packer, Packing Station, and Video Status only populated when `packing_status = 'Packed'`.

**Video Incomplete sub-table columns:**

| Column | Source |
|--------|--------|
| Tracking Number | `packing_lists.tracking_number` |
| Operator | `packing_lists.packed_by` |
| Station | `station_lists.station_name` via `packing_station_id` |
| Video Status | latest `packing_videos.status` |
| Updated | `packing_lists.updated_at` |

Drill-down data fetched lazily on expand. All sections force-expanded before PDF print.

### Updated Timestamp

Displayed in page header. Value = `MAX(packing_lists.updated_at)` across all rows matching current filters.

---

## 3. Architecture

### Option Selected: B — Two endpoints, lazy drill-down

- Summary endpoint is fast (no per-row detail).
- Drill-down fetched per group on demand (or all at once pre-print).

---

## 4. Backend

### New file: `backend/src/api/reports.rs`

#### `GET /reports/fulfillment`

Query params: `from`, `to`, `platform`, `shipping`

Returns:

```json
{
  "updatedAt": "2026-04-26T14:32:05Z",
  "summary": {
    "totalOrders": 248,
    "packed": 201,
    "packedPct": 81.0,
    "qcPassed": 189,
    "qcPassedPct": 76.2,
    "qcHold": 12,
    "qcHoldPct": 4.8,
    "videoComplete": 195,
    "videoTotal": 201,
    "videoCompletePct": 97.0
  },
  "rows": [
    {
      "platform": "Shopee",
      "shipping": "Standard",
      "totalOrders": 80,
      "packed": 65,
      "packedPct": 81.3,
      "qcPassed": 60,
      "qcPassedPct": 75.0,
      "qcHold": 5,
      "qcHoldPct": 6.3,
      "videoComplete": 64,
      "videoTotal": 65,
      "videoCompletePct": 98.5,
      "hasIssues": true
    }
  ]
}
```

**QC Hold definition:** `packing_status = 'QC Hold'`

**QC Passed definition:** `updated_product_lists` not null + all item quantities = 0 (consistent with existing `qc_dashboard.rs`)

**Video Complete definition:** `packing_videos.status = 'Completed'` — count per tracking number using latest video row

**shipping_options source:** `COALESCE(order_lists_raw.shipping_options, order_lists_raw_lazada.shipping_options)` (same JOIN pattern as `qc_dashboard.rs`)

#### `GET /reports/fulfillment/drilldown`

Query params: `platform`, `shipping`, `from`, `to`

Returns:

```json
{
  "platform": "Shopee",
  "shipping": "Standard",
  "qcHold": [
    {
      "trackingNumber": "TH-123456789",
      "qcOperator": "Alice",
      "packingStatus": "Packed",
      "packedBy": "Bob",
      "packingStation": "Station-01",
      "videoStatus": "Completed",
      "updatedAt": "2026-04-26T14:20:11Z"
    }
  ],
  "videoIncomplete": [
    {
      "trackingNumber": "TH-444555666",
      "operator": "Carol",
      "station": "Station-03",
      "videoStatus": "Failed",
      "updatedAt": "2026-04-26T14:15:08Z"
    }
  ]
}
```

#### `POST /reports/downloads`

Body:

```json
{
  "reportType": "fulfillment_overview",
  "filters": { "from": "2026-04-26T00:00:00Z", "to": "2026-04-26T14:32:05Z", "platform": "All", "shipping": "All" }
}
```

Returns `201 Created`.

#### `GET /reports/downloads`

Returns array of download history entries, ordered by `downloaded_at DESC`.

```json
[
  {
    "id": 1,
    "reportType": "fulfillment_overview",
    "filters": { "from": "...", "to": "...", "platform": "All", "shipping": "All" },
    "downloadedAt": "2026-04-26T14:32:10Z"
  }
]
```

### New DB migration

```sql
CREATE TABLE report_downloads (
  id            SERIAL PRIMARY KEY,
  report_type   TEXT        NOT NULL,
  filters       JSONB       NOT NULL,
  downloaded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### Route registration in `backend/src/api/mod.rs`

```
GET  /reports/fulfillment           → reports::fulfillment_summary
GET  /reports/fulfillment/drilldown → reports::fulfillment_drilldown
POST /reports/downloads             → reports::log_download
GET  /reports/downloads             → reports::list_downloads
```

---

## 5. Frontend

### New files

| File | Purpose |
|------|---------|
| `frontend/app/reports/page.tsx` | Route page, composes components |
| `frontend/app/components/FulfillmentReport.tsx` | Filters + summary + breakdown table |
| `frontend/app/components/FulfillmentDrilldown.tsx` | Per-group QC Hold + Video Incomplete panels |
| `frontend/app/components/DownloadHistory.tsx` | Download history list |
| `frontend/app/hooks/useFulfillmentReport.ts` | Fetches `/reports/fulfillment` |
| `frontend/app/hooks/useDrilldown.ts` | Fetches `/reports/fulfillment/drilldown` per group |
| `frontend/app/hooks/useDownloadHistory.ts` | Fetches + posts `/reports/downloads` |

### Modified files

| File | Change |
|------|--------|
| `frontend/app/components/Sidebar.tsx` | Add Reports nav item after Timeline |
| `frontend/app/types.ts` | Add report-related TypeScript types |

### PDF Export

- `Download PDF` button calls `window.print()`
- Before print: force-expand all drill-down sections that have data
- After print dialog closes (using `window.matchMedia('print')` `afterprint` event): POST to `/reports/downloads`
- Print stylesheet hides: sidebar, filters bar, download button, download history
- Print stylesheet shows: all drill-down panels expanded

### Data Flow

```
reports/page.tsx
  └─ FulfillmentReport
       ├─ useFulfillmentReport(filters)
       ├─ SummaryBar
       ├─ BreakdownTable
       │    └─ row[hasIssues=true] → FulfillmentDrilldown
       │                                └─ useDrilldown(platform, shipping, filters)
       ├─ [Download PDF button] → print + log
       └─ DownloadHistory
            └─ useDownloadHistory()
```

---

## 6. Error Handling

| Scenario | Behavior |
|----------|---------|
| No data for filter combo | Zeros in summary, "No orders found" in table, no drill-down |
| Missing shipping_options | Grouped under `"Unknown"` shipping |
| Drilldown fetch fails | Inline error in that panel; others unaffected |
| `to` not provided | Backend defaults to `NOW()` |
| `from` not provided | Frontend always sends `from` (start of selected period); backend treats null `from` as unbounded |

---

## 7. New TypeScript Types (additions to `types.ts`)

```typescript
export interface FulfillmentSummary {
  totalOrders: number;
  packed: number; packedPct: number;
  qcPassed: number; qcPassedPct: number;
  qcHold: number; qcHoldPct: number;
  videoComplete: number; videoTotal: number; videoCompletePct: number;
}

export interface FulfillmentRow {
  platform: string; shipping: string;
  totalOrders: number;
  packed: number; packedPct: number;
  qcPassed: number; qcPassedPct: number;
  qcHold: number; qcHoldPct: number;
  videoComplete: number; videoTotal: number; videoCompletePct: number;
  hasIssues: boolean;
}

export interface FulfillmentReport {
  updatedAt: string;
  summary: FulfillmentSummary;
  rows: FulfillmentRow[];
}

export interface QcHoldDrillItem {
  trackingNumber: string;
  qcOperator: string;
  packingStatus: string;
  packedBy: string | null;
  packingStation: string | null;
  videoStatus: string | null;
  updatedAt: string;
}

export interface VideoIncompleteDrillItem {
  trackingNumber: string;
  operator: string | null;
  station: string | null;
  videoStatus: string | null;
  updatedAt: string;
}

export interface DrilldownResponse {
  platform: string; shipping: string;
  qcHold: QcHoldDrillItem[];
  videoIncomplete: VideoIncompleteDrillItem[];
}

export interface ReportDownload {
  id: number;
  reportType: string;
  filters: { from: string; to: string; platform: string; shipping: string };
  downloadedAt: string;
}
```

---

## 8. Out of Scope

- Authentication / "downloaded by" tracking (deferred)
- Additional report types beyond Order Fulfillment Overview (dropdown placeholder only)
- Excel / CSV export (PDF only)
