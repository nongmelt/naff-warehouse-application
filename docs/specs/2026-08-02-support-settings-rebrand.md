# Spec: Help & Support Center + Claude.ai-style settings rebrand

**Status:** Frozen 2026-08-02. Build-ready.
**Wayfinder map:** [#91](https://github.com/nongmelt/naff-warehouse-application/issues/91) · **Spec-freeze ticket:** [#97](https://github.com/nongmelt/naff-warehouse-application/issues/97)
**Implementation plan:** [`docs/plans/2026-08-02-support-settings-rebrand.md`](../plans/2026-08-02-support-settings-rebrand.md)

## 1. Scope

Four changes to the Next.js dashboard, plus the Rust backend and Postgres schema behind the first:

1. New **`/support`** page — a GitHub-issues-like ticketing surface ("Help & Support Center") with its own Postgres tables and Axum CRUD.
2. Remove the **"System"** sidebar group (it holds only Settings).
3. **Claude.ai-style sidebar footer identity** — signed-in name/email from Cloudflare Access, initials avatar, popover user menu.
4. **Claude.ai-style settings modal** — the `/settings` page is retired.

### Sources — read these, they are not restated here

Every decision below traces to a closed wayfinder ticket. Where this spec adds something, it is marked **[spec-level derivation]** and can be overridden by an implementer with a better idea; everything else is settled and must not be re-litigated.

| Input | Holds |
|---|---|
| [Grilling: support-ticket domain model (#92)](https://github.com/nongmelt/naff-warehouse-application/issues/92#issuecomment-5160333018) | Domain terms, categories, status lifecycle, comments, permissions, notifications |
| [Prototype: /support page (#96)](https://github.com/nongmelt/naff-warehouse-application/issues/96#issuecomment-5160373625) | Page layout, new-ticket modal, parcel refs, `#` mention popup, attachments widened to files |
| [Prototype: sidebar footer + user menu (#94)](https://github.com/nongmelt/naff-warehouse-application/issues/94) | V2 sidebar layout, Help group, headset icon, slim user menu |
| [Prototype: settings General page (#95)](https://github.com/nongmelt/naff-warehouse-application/issues/95) | Settings modal shape, icon segmented Appearance control |
| [Research: Cloudflare Access identity (#93)](https://github.com/nongmelt/naff-warehouse-application/issues/93#issuecomment-5160466677) → [`docs/research/2026-08-02-cloudflare-access-identity.md`](../research/2026-08-02-cloudflare-access-identity.md) | JWT contract, get-identity payload, off-tunnel fallback, verification position |
| [Task: Access logout vs Google IdP (#98)](https://github.com/nongmelt/naff-warehouse-application/issues/98#issuecomment-5160496339) | Logout is outcome (b); copy and identity-visibility consequences |
| Mockup `docs/mockups/2026-08-02-support-settings-rebrand.html` (commit `36fe699`) | Pixel-level reference for all four changes |

### Non-goals (locked out of scope on the map)

- Mirroring tickets to GitHub Issues.
- Any auth/roles system beyond displaying Access identity and attributing writes.
- Mapping Access emails to `operator_lists` rows.
- Notifications of any kind (no webhook, no LINE).
- Severity/priority fields, arbitrary labels, comment deletion, ticket mentions, `@` people mentions.

---

## 2. Domain model

Domain term is **Ticket**. UI heading is **"Help & Support Center"**; the sidebar item is **"Support"**. No collision with the Delivery Issues domain (`issue_reports`), which stays exactly as it is.

- **Status** is two-state `open` / `closed`, plus a nullable `close_reason` of `completed` / `not_planned` / `duplicate`, set on close and cleared on reopen. No `in_progress`.
- **Category** is `bug` / `feature_request` / `question` / `data_problem` / `other`, with `category_note` free text when `other`.
- **Title** is required (120-char cap). **Body** is optional free text.
- **Anyone** can comment, close, and reopen. Authors may edit their own ticket and their own comments (matched by Access email); nothing is ever deleted.
- **Attachments** are files *and* images (#96 amends #92's images-only v1) — xlsx evidence is the primary use case.
- **Related parcel** is one optional soft reference (tracking or order number), plus free-text parcel references inside body/comment text that the render side auto-linkifies.

---

## 3. Database

One new migration: `backend/migrations/20260803120000_support_tickets.sql`.

**Naming rule:** if a later timestamp already exists on the base branch when this is written, bump to the next free `YYYYMMDDHHMMSS`. Never edit an applied migration — prod has run every file in `migrations/`, and its recorded checksums were computed from a CRLF checkout, so an in-place edit is unrecoverable there.

```sql
-- Help & Support Center: dashboard users file tickets, the developer triages.
-- Structural sibling of issue_reports (20260703120000): current-state columns on
-- the parent row, append-only history in an events table, soft parcel reference
-- with no FK so tickets survive parcel purges.
CREATE TABLE IF NOT EXISTS public.support_tickets (
    id              serial4     NOT NULL,
    title           text        NOT NULL,
    body            text        NULL,
    category        text        NOT NULL,
    category_note   text        NULL,   -- free text when category = 'other'
    status          text        NOT NULL DEFAULT 'open',
    close_reason    text        NULL,   -- set on close, cleared on reopen
    tracking_number text        NULL,   -- related parcel, soft ref (no FK)
    order_number    text        NULL,
    reporter_email  text        NOT NULL,  -- from the Access JWT, never the body
    reporter_name   text        NULL,      -- display snapshot only
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    edited_at       timestamptz NULL,
    closed_at       timestamptz NULL,
    closed_by       text        NULL,
    CONSTRAINT support_tickets_pk PRIMARY KEY (id),
    CONSTRAINT support_tickets_title_chk CHECK (char_length(title) BETWEEN 1 AND 120),
    CONSTRAINT support_tickets_category_chk CHECK (category IN
        ('bug','feature_request','question','data_problem','other')),
    CONSTRAINT support_tickets_status_chk CHECK (status IN ('open','closed')),
    CONSTRAINT support_tickets_close_reason_chk CHECK (
        (status = 'open'   AND close_reason IS NULL) OR
        (status = 'closed' AND close_reason IS NOT NULL
                           AND close_reason IN ('completed','not_planned','duplicate'))
    )
);

CREATE INDEX IF NOT EXISTS idx_support_tickets_status_created
    ON public.support_tickets (status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_support_tickets_created_at
    ON public.support_tickets (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_support_tickets_reporter
    ON public.support_tickets (reporter_email);

CREATE TABLE IF NOT EXISTS public.support_ticket_comments (
    id           serial4     NOT NULL,
    ticket_id    int4        NOT NULL,
    body         text        NOT NULL,
    author_email text        NOT NULL,
    author_name  text        NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    edited_at    timestamptz NULL,
    CONSTRAINT support_ticket_comments_pk PRIMARY KEY (id),
    CONSTRAINT support_ticket_comments_fk FOREIGN KEY (ticket_id)
        REFERENCES public.support_tickets (id) ON DELETE CASCADE,
    CONSTRAINT support_ticket_comments_body_chk CHECK (char_length(body) > 0)
);
CREATE INDEX IF NOT EXISTS idx_support_ticket_comments_ticket
    ON public.support_ticket_comments (ticket_id, created_at);

-- Append-only. The thread renders comments UNION these rows, ordered by time.
CREATE TABLE IF NOT EXISTS public.support_ticket_events (
    id          serial4     NOT NULL,
    ticket_id   int4        NOT NULL,
    kind        text        NOT NULL,
    actor_email text        NULL,
    actor_name  text        NULL,
    detail      jsonb       NULL,   -- {"reason":"completed"} / {"fields":["title"]}
    at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT support_ticket_events_pk PRIMARY KEY (id),
    CONSTRAINT support_ticket_events_fk FOREIGN KEY (ticket_id)
        REFERENCES public.support_tickets (id) ON DELETE CASCADE,
    CONSTRAINT support_ticket_events_kind_chk CHECK (kind IN
        ('filed','edited','closed','reopened'))
);
CREATE INDEX IF NOT EXISTS idx_support_ticket_events_ticket
    ON public.support_ticket_events (ticket_id, at);

CREATE TABLE IF NOT EXISTS public.support_ticket_attachments (
    id           serial4     NOT NULL,
    ticket_id    int4        NOT NULL,
    comment_id   int4        NULL,   -- NULL = attached at filing time
    path         text        NOT NULL,  -- "support/{ticket_id}/{attachment_id}.{ext}"
    file_name    text        NOT NULL,  -- original name, shown on the chip
    content_type text        NOT NULL,  -- decides thumbnail vs file chip
    size_bytes   int8        NOT NULL,
    uploaded_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT support_ticket_attachments_pk PRIMARY KEY (id),
    CONSTRAINT support_ticket_attachments_ticket_fk FOREIGN KEY (ticket_id)
        REFERENCES public.support_tickets (id) ON DELETE CASCADE,
    CONSTRAINT support_ticket_attachments_comment_fk FOREIGN KEY (comment_id)
        REFERENCES public.support_ticket_comments (id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_support_ticket_attachments_ticket
    ON public.support_ticket_attachments (ticket_id);
```

**The `close_reason IS NOT NULL` in `support_tickets_close_reason_chk` is load-bearing — do not "simplify" it away.** Without it, `status = 'closed'` with `close_reason IS NULL` makes the `IN (...)` test evaluate to NULL under three-valued logic; the first disjunct is FALSE, `FALSE OR NULL` is NULL, and a CHECK constraint rejects only FALSE, never NULL. The constraint would therefore admit a closed ticket with no close reason — exactly the row the constraint exists to forbid. (Amended at T19; the shipped migration `20260803120000_support_tickets.sql` has always carried the `IS NOT NULL` form.)

**Event kind set is fixed here** (#92 item 6 deferred it to spec freeze): `filed`, `edited`, `closed`, `reopened`. Comments are *not* events — the thread is the union of `support_ticket_comments` and `support_ticket_events` sorted by timestamp, so a `commented` event would duplicate every comment. **[spec-level derivation]**

**Related-parcel storage** **[spec-level derivation]**: a suggestion picked from the typeahead fills both `tracking_number` and `order_number` from the chosen row. A string typed freehand that matched no suggestion is stored in `tracking_number` alone. No FK either way, mirroring `issue_reports`.

**No pg_notify trigger.** Support tickets are not part of the real-time packing pipeline; the page polls/refetches like the Delivery Issues dashboard does.

---

## 4. Identity and auth surface

This section is load-bearing. The full reasoning is in [`docs/research/2026-08-02-cloudflare-access-identity.md`](../research/2026-08-02-cloudflare-access-identity.md); do not weaken any of it without re-opening #93.

### 4.1 Deployment prerequisite — ships *with* the feature, not after

Enable **Protect with Access** on the tunnel's public hostname service for this app:

```yaml
access:
  required: true
  teamName: <team>
  audTag: [<Application Audience (AUD) tag>]
```

`cloudflared` then performs the RS256 signature and `aud` check before any request reaches Axum. This is the whole of the signature verification for v1 — there is deliberately none in Rust.

**Recorded, accepted v1 risk:** this hardens the tunnel path only. A deliberate forger on the warehouse LAN hitting `192.168.1.112:8080` with a self-minted JWT is *not* stopped, because Axum does not verify signatures. Accepted for a firewalled LAN with a handful of staff. **The spec must never be paraphrased as "verified" — it is "verified at the tunnel, trusted at the origin".**

Also required at deploy time: a MinIO bucket named `support`.

### 4.2 Backend — `Cf-Access-Jwt-Assertion` extractor

New module `backend/src/auth.rs` exposing an Axum extractor `AccessIdentity { sub: String, email: String }`, applied to every mutating `/support/*` route:

- Read the `Cf-Access-Jwt-Assertion` header. **Absent → 403** (`AppError::Forbidden`). This is the check that closes the LAN path and it costs nothing.
- Base64url-decode the payload segment and read `email` and `sub`. **No signature verification** — `cloudflared` already did it on this same machine (§4.1).
- Reject service tokens: `sub` empty or `email` absent → 403. Never write an empty author.
- `sub` is the stable author key, `email` is for display and ownership matching. Neither is a foreign key into a users table; none exists.
- Do **not** read `Cf-Access-Authenticated-User-Email`. It is undocumented and trivially spoofable.

**Local development** **[spec-level derivation]**: env var `ACCESS_AUTH`, default `required`. Setting `ACCESS_AUTH=dev_bypass` (docker dev compose only) makes the extractor yield a fixed `dev@localhost` identity when the header is absent. The default is the secure one, so a prod deployment that forgets the variable still rejects headerless requests.

### 4.3 Reconciling #92 item 9 with #93

#92 said the frontend sends `reporter_email` + `reporter_name` snapshots. #93 supersedes the email half:

- `reporter_email` / `author_email` / `actor_email` come **from the JWT only**. An email in the request body is ignored (not an error).
- `reporter_name` / `author_name` / `actor_name` may come from the body as a **display hint** — the JWT carries no name, and a display string is not an authorization claim.

### 4.4 Frontend — display-only identity

New hook `app/hooks/useAccessIdentity.ts`:

- `GET /cdn-cgi/access/get-identity`, same-origin.
- **Branch on `!res.ok` and guard the JSON parse separately.** Off-tunnel this endpoint returns 404 with `text/html`, so `fetch` resolves rather than rejecting and a bare try/catch never fires. Measured, not assumed.
- Render `name`, fall back to `email`. `name` is undocumented — a missing `name` must never break the sidebar.
- **No avatar exists** in the payload. Initials are the only option, not a fallback; reuse the initials pattern from `app/components/leaderboard/primitives.tsx`.
- Do not display `iat` or `ip` (they are `0` and `""` on this deployment).
- Fallback state when not behind Access (localhost / LAN): a neutral "Not signed in" label. The menu still opens; Settings still works; Log out is hidden.
- **Never POST a get-identity result to the backend as identity.** It is presentation data. The only exception is the display-name hint in §4.3.

### 4.5 Logout copy — consequences of #98

Tested live: Access drops its session, Google does not, so signing back in shows the **Google account chooser** and the previous user is one password-free click away. Because writes are attributed from the JWT, a wrong-account sign-in misattributes tickets silently.

- The **Log out** entry stays (#94). It links to `<app-domain>/cdn-cgi/access/logout`.
- **Copy must not claim a full sign-out.** Required text: menu item **"Log out"**, with the hint line **"Ends your dashboard session. You may still be signed in to Google."** Nothing that implies otherwise.
- **The sidebar footer identity is load-bearing, not decoration** — the permanently visible signed-in name is what catches a wrong-account session. It must never be collapsed to avatar-only without the name reachable (in rail mode the name lives in the popover, which is reachable; that is the floor).

---

## 5. Backend API surface

All routes registered in `backend/src/api/mod.rs`, handlers in a new `backend/src/api/support.rs`. `GET` routes are unauthenticated (reading is harmless and the tunnel gates the origin anyway); every mutating route takes the `AccessIdentity` extractor.

| Method | Path | Body / query | Returns |
|---|---|---|---|
| `GET` | `/support/tickets` | `status` (`open`\|`closed`\|`all`, default `open`), `category`, `search`, `page`, `pageSize` | `{ tickets: [...], total }` |
| `GET` | `/support/tickets/summary` | — | `{ open, closed }` for the filter-tab counts |
| `POST` | `/support/tickets` | `{ title, body?, category, categoryNote?, trackingNumber?, orderNumber?, reporterName? }` | created ticket |
| `GET` | `/support/tickets/{id}` | — | `{ ticket, comments[], events[], attachments[] }` |
| `PATCH` | `/support/tickets/{id}` | `{ title?, body?, category?, categoryNote?, trackingNumber?, orderNumber? }` | updated ticket; **403 unless caller email == `reporter_email`**; sets `edited_at`, writes an `edited` event |
| `POST` | `/support/tickets/{id}/close` | `{ reason }` | updated ticket; writes a `closed` event; anyone may call |
| `POST` | `/support/tickets/{id}/reopen` | — | updated ticket; clears `close_reason`/`closed_at`/`closed_by`; writes a `reopened` event; anyone may call |
| `POST` | `/support/tickets/{id}/comments` | `{ body, authorName? }` | created comment |
| `PATCH` | `/support/comments/{id}` | `{ body }` | updated comment; **403 unless caller email == `author_email`**; sets `edited_at` |
| `POST` | `/support/tickets/{id}/attachments` | multipart, optional `?commentId=` | created attachment row; `DefaultBodyLimit::max(16 * 1024 * 1024)` |
| `GET` | `/support/attachments/{id}/media` | — | streams the object from MinIO |

Conventions inherited from the existing codebase: `serde(rename_all = "camelCase")` on every model, `Result<_, AppError>` on every handler, 404 via `AppError::NotFound`, MinIO via `state.bucket("support")` with the object key reserved by inserting the row first and deleting it on upload failure (copy `issues::upload_photo`).

**Parcel typeahead reuses the existing `GET /packing-lists/suggest`** — it already does `ILIKE '%term%'` against `tracking_number` OR `order_number` and returns `platform`, `packing_status`, `updated_at`, which is exactly what a mention row renders. The only change needed is an optional `limit` query param (default 8, clamp 1..=25) so the mention popup can ask for 5. **No new suggest endpoint** — this supersedes #96's "needs a backend suggest endpoint" reminder, which was written before the existing endpoint was checked.

---

## 6. Frontend surface

### 6.1 New files

| Path | Responsibility |
|---|---|
| `app/support/page.tsx` | Route shell → `<SupportDashboard />` |
| `app/components/support/SupportDashboard.tsx` | List view: 60px header bar, filter tabs, category dropdown, rows |
| `app/components/support/SupportTicketDetail.tsx` | Detail: breadcrumb header, 2-col grid, thread + sidebar cards |
| `app/components/support/NewTicketModal.tsx` | Top-anchored 520px modal, footer pills, "Filing as …" line |
| `app/components/support/ParcelMentionPopup.tsx` | `#`-triggered caret popup |
| `app/components/support/ParcelTypeahead.tsx` | Related-parcel field on the new-ticket form |
| `app/components/support/supportUi.tsx` | Status glyphs, category chips, close-reason labels (sibling of `issues/issueUi.tsx`) |
| `app/components/SettingsModal.tsx` | Claude.ai-style settings modal |
| `app/components/SidebarUserMenu.tsx` | Footer identity row + popover menu |
| `app/hooks/useAccessIdentity.ts` | §4.4 |
| `app/hooks/useSupportTickets.ts` | List + summary + filters |
| `app/hooks/useSupportTicket.ts` | Detail + mutations |
| `app/hooks/useParcelSuggest.ts` | Debounced `/packing-lists/suggest` |
| `app/lib/parcelLinkify.tsx` | Auto-linkify tracking/order runs in rendered text |
| `app/lib/matchHighlight.tsx` | Contiguous-run match + `<mark>` highlight (export-invoice semantics) |

Types go in `app/types.ts` alongside the existing interfaces.

### 6.2 Fidelity references — reuse, don't reinvent

| Reference | Reused for |
|---|---|
| `app/components/issues/IssuesDashboard.tsx` | 60px `bg-card border-b` header bar, search pill, round brand action button |
| `app/components/issues/ReportDetail.tsx` | Breadcrumb header, 2-col grid, 340px sidebar action card |
| `app/components/issues/ReportIssueModal.tsx` | Modal chrome: top-anchored 520px, footer pill buttons |
| `app/components/issues/OrderTimelineModal.tsx` | Cloned for the parcel-reference modal |
| `app/components/AlertReconcileDropdown.tsx` | Mention-popup row design: platform square + mono tracking + status/time badge |
| `app/lib/platform.tsx` (`PlatformBadge` / `PlatformGlyph`) | **Real platform logos in mention rows** — the mockup's letter squares are placeholders |
| `app/hooks/useDialog.ts` | Dialog primitive; there is no component library |
| `app/components/leaderboard/primitives.tsx` | Initials avatar |

### 6.3 List view (#96 items 1–2)

Header bar: "Help & Support Center" + open/closed counts, search pill, round brand **"New ticket"** button. Below: GitHub-style filter tabs (Open / Closed / All with counts) and a category dropdown. Rows show a status glyph (**open** = dot, **completed** = check, **not_planned** = slash, **duplicate** = copy), title + category chip, `#id · opened … by reporter`, and attachment/comment counts.

### 6.4 Detail view (#96 item 3)

Breadcrumb `Support › Ticket #N`. Two-column grid: thread left, 340px sidebar right.

- **Status card** — close-reason select + "Close ticket" button (or "Reopen"). Anyone may act; the action is recorded in the thread. Close controls live in the sidebar, **not** the composer.
- **Details card** — reporter, email, category, tracking, order, opened, attachments.
- Thread = comments ∪ events merged by timestamp; events render GitHub-style ("X closed this as completed"). Edited items show "(edited)".

### 6.5 New ticket (#96 item 4)

A **modal**, not a page. Fields: title (required, 120 cap), category (+ note when `other`), body, related parcel (typeahead), attachments. Footer line: "Filing as {name} · {email}" from `useAccessIdentity`.

### 6.6 Parcel references (#96 items 6–9)

- Tracking/order numbers in body and comment text render as mono brand links; clicking opens the Order Timeline modal. **Stored as plain text — no token format in the database**; linkification happens at render.
- Typing `#` in the composer or description opens a popup **below the caret** — no inner search box. Typing after `#` filters, max **5** rows; ↑/↓ + Enter inserts the tracking number as plain text. Selected row uses brand-surface background (no left stripe, no hint row).
- **Match semantics = the export-invoice search bar:** a contiguous character run starting anywhere (`6789` matches `TH123456789`), matched against **both** tracking and order number, with the matched run highlighted in the result row.
- `@` is reserved for people and does nothing in v1. There are no ticket-number mentions.

### 6.7 Attachments (#96 item 5)

Files **and** images, at filing time and per-comment. Images render as thumbnails, other files as chips showing name + size. Backed by `POST /support/tickets/{id}/attachments`.

### 6.8 Sidebar (#94)

In `app/components/Sidebar.tsx`:

- **Delete the `system` group** from `navGroups` (currently lines 175–182) — it holds only Settings, which moves into the user menu.
- **Add a `help` group**: label "Help", group icon and item icon are the headset (headphones over a rounded face), single item `{ href: "/support", label: "Support", Icon: HeadsetIcon }`.
- **Add the footer identity row** above the existing Collapse row: initials avatar + name + email + chevron toggle. Clicking opens a slim popover menu: **Settings** (opens the settings modal), a **System / Dark / Light** quick-row, and **Log out** with the §4.5 hint line. The version string stays under Collapse, unchanged.
- Rail mode: the identity row collapses to the avatar; the name stays reachable in the popover (§4.5 floor).

**`app/components/Sidebar.rail.test.tsx` must be updated in the same task** — it hard-codes `GROUP_TITLES = ["Operation", "Analytics", "Invoices", "System"]`, asserts a `/settings` link exists in three tests, and seeds `localStorage` with the group key `"system"`. All four become `Help` / `/support` / `"help"`.

### 6.9 Settings (#95)

A pop-up modal, opened from the sidebar user menu:

- Left column: "Settings" nav with a single **General** entry.
- Right pane: **Preferences** heading → **Appearance** row with an **icon-only** monitor / sun / moon segmented control. No caption, no thumbnails, no Preview card.
- Esc, backdrop click, and ✕ all close it.
- Wiring is the existing `useTheme()` from `app/context/ThemeContext.tsx` (`theme`, `setTheme`, values `system` / `dark` / `light`, persisted to `localStorage["theme"]`). No backend call, no new theme machinery.

**Route retirement:** `app/settings/page.tsx` becomes a server-side `redirect("/")` so existing bookmarks do not 404, and `app/components/SettingsDashboard.tsx` is deleted. **[spec-level derivation** — #95 said only "retired"; drop the redirect and delete the route outright if preferred.**]**

---

## 7. Acceptance criteria

1. A user behind Access can file a ticket with a title, category, body, related parcel, and both an image and an `.xlsx` attachment; it appears in the Open tab with the right glyph, chip, and counts.
2. `reporter_email` on that row equals the JWT email, **not** anything the browser sent.
3. `curl` against the backend with no `Cf-Access-Jwt-Assertion` header gets **403** on every mutating `/support` route (with `ACCESS_AUTH` unset).
4. Anyone can close the ticket with a reason and reopen it; both render as event rows in the thread and `close_reason` is null again after reopen.
5. A non-author gets 403 editing someone else's ticket or comment; the author succeeds and the item shows "(edited)".
6. Typing `#678` in a composer shows at most 5 parcels matched on tracking **or** order, with the matched run highlighted and real platform logos; Enter inserts the plain tracking number.
7. A tracking number in saved text renders as a link that opens the Order Timeline modal.
8. The sidebar has no "System" group, has a "Help" group containing Support, and shows the signed-in name; `Sidebar.rail.test.tsx` passes.
9. The user menu opens the settings modal; the segmented control changes the theme and survives a reload; `/settings` redirects to `/`.
10. Off-tunnel (`localhost:3000`), the sidebar renders the fallback label without a console error and the rest of the dashboard is unaffected.
11. `cargo test` and `npm run lint` && `npm run build` pass.

## 8. Execution notes

- Work happens in **two submodules** (`backend/`, `frontend/`) and needs its own commits in each; this spec and plan live in the **root** repo.
- Use `.worktrees/<topic>` inside each submodule rather than switching the main checkout.
- Backend queries are SQLx compile-time checked — run against the dev DB or regenerate `.sqlx/` for `SQLX_OFFLINE=true`.
- The shared dev DB carries an orphan `20260703220000` migration row with no file; `sqlx migrate run` there needs `--ignore-missing`. Do not delete the row.
- Execution is a **local-only SDD effort** by standing preference — do not cloud-schedule it.
