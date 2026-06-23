# warehouse-raw retention (orphan video capture)

Orphan videos (recorded against a tracking number with no `packing_lists` row)
are uploaded by the desktop app to the **`warehouse-raw`** bucket via the shared
video workflow and registered with `POST /orphan-videos`. They sit there with
`match_status = 'pending'` until an operator either:

- **assigns** them — the backend copies the object into `warehouse-videos`,
  inserts a `packing_videos` row, then **deletes the raw object** (best-effort,
  the final step of the move); or
- **discards** them — the backend **deletes the raw object** immediately and
  flips `match_status = 'discarded'` (the orphan row stays as audit).

There is **NO automatic expiry.** A still-`pending` orphan persists in
`warehouse-raw` indefinitely until it is matched or discarded. Earlier designs
applied a 15-day MinIO ILM rule — that rule has been **removed**. Retention is
governed entirely by the match-move / discard / manual-cleanup actions above.

If a raw object goes missing out-of-band (e.g. manual bucket cleanup of a
never-matched orphan), the backend handles it defensively: a HEAD/GET 404 on
preview or assign flips the row to `match_status = 'expired'` and returns
`410 Gone`. This is a defensive case, not a routine outcome.

## One-time setup (run once per environment)

```bash
# 1. Register the MinIO alias (host/port + root creds from docker/.env).
#    Inside the compose network use http://minio:9000; from the host use the
#    published console/api port.
mc alias set warehouse http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"

# 2. Create the raw bucket if it does not yet exist (idempotent).
mc mb --ignore-existing warehouse/warehouse-raw
```

Do **NOT** add any ILM expiry rule to `warehouse-raw`. If a legacy 15-day rule is
present, remove it:

```bash
mc ilm rule ls warehouse/warehouse-raw   # should list NO rules
mc ilm rule rm --all --force warehouse/warehouse-raw   # only if a legacy rule exists
```

## warehouse-videos has NO expiry (assert this)

The final videos bucket must NEVER auto-delete. Confirm it has no ILM rules:

```bash
mc ilm rule ls warehouse/warehouse-videos
```

Expected output: `No lifecycle configuration set on warehouse/warehouse-videos`.
If any rule is listed here, remove it:
`mc ilm rule rm --all --force warehouse/warehouse-videos`.

## Manual cleanup of never-matched orphans (future concern)

A periodic manual sweep of long-`pending` orphans (e.g. operator review + discard,
or direct bucket cleanup) is a future operational concern, not an automated rule.
