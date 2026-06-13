# Traefik reverse proxy

Single entrypoint (port 80) fronting the warehouse stack, restricted to the LAN
subnets `192.168.0.0/24` and `192.168.1.0/24` via an `ipAllowList` middleware.

Supersedes the old nginx proxy (`compose.nginx.yml` + `nginx/`, removed).

## Stages

**Stage 1 — active.** Dashboard reachable by the server's LAN IP
(`http://<server-ip>/`), no DNS required. A catch-all router sends every request
to the frontend, gated by the subnet allowlist.

**Stage 2 — commented in `dynamic.yml`.** Per-service hostnames
(`api` / `minio` / `console.warehouse.local`). Uncomment the blocks once
`*.warehouse.local` resolves on the LAN (router/AD DNS or per-PC hosts file).

## Run

```bash
docker compose -f compose.yml -f compose.db.yml -f compose.minio.yml \
               -f compose.traefik.yml up -d
```

## Testing the allowlist

- **From another LAN PC** (`192.168.0.x` / `192.168.1.x`): `http://<server-ip>/`
  → dashboard (200). The client's real IP is preserved through Docker's DNAT.
- **From a PC outside both subnets** → 403.
- **From the server box itself** → 403. Docker NATs host-local traffic to the
  bridge gateway (`172.x`), so it isn't seen as a LAN source. Expected — always
  test from a separate PC.

## Notes

- **Config reload.** Traefik watches `dynamic.yml`, but bind-mount file-watch is
  unreliable on a Windows host — `docker restart` the traefik container after edits.
- **Dashboard functionality.** `NEXT_PUBLIC_*` are baked into the frontend image
  at build time. For the dashboard's own API/WS/MinIO calls to work from a remote
  PC, the frontend must be built with URLs that PC can reach — Stage 1: the
  server's LAN IP (`http://<server-ip>:8080` etc., see `.env.example`); Stage 2:
  the `*.warehouse.local` hosts. Rebuild with `docker compose build frontend`.
- **Backend CORS.** Set `CORS_ORIGIN` to the origin the browser uses for the
  dashboard (Stage 1: `http://<server-ip>`).
- **L3 routing.** PCs on `192.168.0.0/24` only reach a server on `192.168.1.0/24`
  if a router/VLAN bridges the subnets. Traefik filters; it can't create the path.
- **MinIO presigned URLs** (Stage 2) must be signed against the public MinIO host
  or off-subnet fetches fail with `SignatureDoesNotMatch` (backend change).
- **TLS.** HTTP only (LAN). Add a `websecure` entrypoint + certs in `traefik.yml`
  for HTTPS.
