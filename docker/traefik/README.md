# Traefik reverse proxy

Single entrypoint (port 80) fronting the warehouse stack. Supersedes the old
nginx proxy (`compose.nginx.yml` + `nginx/`, removed).

## Access control — host firewall (NOT Traefik)

Access is restricted to the LAN subnets `192.168.0.0/24` and `192.168.1.0/24`
by a **Windows Firewall rule on the server**, not by Traefik.

**Why not Traefik:** on Docker Desktop the published-port proxy SNATs the client
IP, so Traefik only ever sees an internal gateway (e.g. `10.99.0.1`) — a Traefik
`ipAllowList` would reject every client. The Windows Firewall runs in the host
network stack and sees the real client IP.

Run once on each server (elevated PowerShell):
```powershell
New-NetFirewallRule -DisplayName "Warehouse HTTP 80" -Direction Inbound -Protocol TCP `
  -LocalPort 80 -RemoteAddress 192.168.0.0/24,192.168.1.0/24 -Action Allow -Profile Any
```
This applies on all profiles (the server's Wi-Fi/LAN may be categorised Public).

> On native **Linux** Docker the client IP is preserved, so you could instead add
> a Traefik `ipAllowList` middleware in `dynamic.yml`. This stack targets Docker
> Desktop, so the firewall is the gate.

## Stages

**Stage 1 — active.** Dashboard reachable by the server's LAN IP
(`http://<server-ip>/`), no DNS. Catch-all router → frontend.

**Stage 2 — commented in `dynamic.yml`.** Per-service hostnames
(`api` / `minio` / `console.warehouse.local`). Uncomment once `*.warehouse.local`
resolves on the LAN (router/AD DNS or per-PC hosts file).

## Run

```bash
docker compose -f compose.yml -f compose.db.yml -f compose.minio.yml \
               -f compose.traefik.yml up -d
```

## Testing

- **From another LAN PC** (`192.168.0.x` / `192.168.1.x`): `http://<server-ip>/`
  → dashboard (200).
- **From a PC outside both subnets** → connection blocked by the firewall.
- **L3 routing:** a `192.168.0.x` PC only reaches a `192.168.1.x` server if a
  router/VLAN routes between the subnets. Verify from the client:
  `Test-NetConnection <server-ip> -Port 80` (`TcpTestSucceeded : True`).

## Notes

- **Config reload.** File-watch is unreliable over Windows bind mounts —
  `docker restart` the traefik container after editing `dynamic.yml`.
- **Dashboard functionality.** `NEXT_PUBLIC_*` are baked into the frontend image
  at build time. For the dashboard's own API/WS/MinIO calls to work from a remote
  PC, rebuild frontend with URLs that PC can reach — Stage 1: the server LAN IP
  (`http://<server-ip>:8080` etc., see `.env.example`); Stage 2: the
  `*.warehouse.local` hosts. `docker compose build frontend`.
- **Backend CORS.** Set `CORS_ORIGIN` to the origin the browser uses for the
  dashboard (Stage 1: `http://<server-ip>`).
- **TLS.** HTTP only (LAN). Add a `websecure` entrypoint + certs in `traefik.yml`
  for HTTPS.
