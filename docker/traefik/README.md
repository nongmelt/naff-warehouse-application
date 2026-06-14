# Traefik reverse proxy

Single **same-origin** entrypoint (port 80) fronting the warehouse stack.
Supersedes the old nginx proxy (`compose.nginx.yml` + `nginx/`, removed).

## Why same-origin (the multi-subnet fix)

The dashboard is reached through one origin — `http://<server-ip>/` — and the
browser sends every API / WebSocket / MinIO request back to **that same
host:port**. Traefik then path-routes it internally:

| Browser request        | Routed to        | Service sees                    |
|------------------------|------------------|---------------------------------|
| `/api/*`               | backend `:8080`  | `/...` (`/api` stripped)        |
| `/s3/*`                | minio `:9000`    | `/<bucket>/<key>` (`/s3` stripped) |
| `/*` (everything else) | frontend `:3000` | dashboard pages + `_next` assets |

Because nothing in the frontend is hard-coded to a specific IP, **one image
works from every subnet** that can reach the server. A server on both
`192.168.1.x` (Wi-Fi) and `192.168.0.x` (LAN) is reached by each client via the
IP on its own subnet, and all sub-requests follow that same IP automatically.

> The old approach baked `NEXT_PUBLIC_API_URL=http://<one-ip>:8080` into the
> frontend bundle, so only clients on that one IP's subnet could reach the API.
> Same-origin relative URLs remove the baked IP entirely.

Backend is namespaced under `/api` because `/products`, `/stations` and
`/reports` exist as **both** frontend pages and backend routes — without the
prefix they would collide.

## Frontend URLs — relative, baked at build

The frontend reads these at **build time** (`docker compose build frontend`),
so they are relative paths, not hosts (see `.env.example`):

```
NEXT_PUBLIC_API_URL=/api
NEXT_PUBLIC_WS_URL=/api/packing-lists/events
NEXT_PUBLIC_MINIO_URL=/s3
```

Do **not** put a server IP here — that re-creates the single-subnet problem.
The WS path is relative too; modern browsers resolve `new WebSocket("/api/...")`
to `ws://<that-host>/api/...`.

## Access through :80 only

Reach the dashboard via Traefik on **:80**, not the frontend's `:3000` directly
— relative `/api` calls only resolve behind the proxy. The backend (`:8080`) and
MinIO (`:9000`) ports stay published for the **desktop app**, which talks to them
directly; browsers do not.

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

CORS is not exercised under same-origin routing (the browser never makes a
cross-origin request), so `CORS_ORIGIN` is effectively unused — a valid value is
still required because the backend parses it at startup.

## Run

```bash
docker compose -f compose.yml -f compose.db.yml -f compose.minio.yml \
               -f compose.traefik.yml up -d
# after changing any NEXT_PUBLIC_* value, rebuild the frontend image:
docker compose -f compose.yml -f compose.traefik.yml build frontend
```

## Testing

- **From a LAN PC** (`192.168.0.x` / `192.168.1.x`): open `http://<server-ip>/`
  → dashboard loads, live updates work, images render — on **both** subnets.
- **From a PC outside both subnets** → connection blocked by the firewall.
- **L3 routing:** a `192.168.0.x` PC only reaches a `192.168.1.x` server if a
  router/VLAN routes between the subnets. Verify from the client:
  `Test-NetConnection <server-ip> -Port 80` (`TcpTestSucceeded : True`).

**Routing self-test (no real stack needed).** Stand up Traefik with the real
config but whoami stand-ins, then check each prefix routes + strips correctly:

```bash
cat > compose.routing-test.yml <<'YAML'
name: traefikroutetest
services:
  traefik:
    image: traefik:v3.3
    volumes:
      - ./traefik/traefik.yml:/etc/traefik/traefik.yml:ro
      - ./traefik/dynamic.yml:/etc/traefik/dynamic.yml:ro
  frontend: { image: traefik/whoami, command: ["--port","3000","--name","frontend"] }
  backend:  { image: traefik/whoami, command: ["--port","8080","--name","backend"] }
  minio:    { image: traefik/whoami, command: ["--port","9000","--name","minio"] }
  tester:   { image: curlimages/curl, command: ["sleep","600"] }
YAML
docker compose -f compose.routing-test.yml up -d
docker compose -f compose.routing-test.yml exec -T tester sh -c '
  for p in / /api/packing-lists /s3/warehouse-videos/x.jpg /products; do
    echo "== $p =="; curl -s --retry 10 --retry-all-errors "http://traefik$p" | grep -E "^(Name:|GET )";
  done'
docker compose -f compose.routing-test.yml down -v && rm compose.routing-test.yml
```
Expect `/api/packing-lists` → `Name: backend` + `GET /packing-lists` (prefix
stripped) and `/products` → `Name: frontend` (collision avoided).

## Notes

- **Config reload.** File-watch is unreliable over Windows bind mounts —
  `docker restart <traefik-container>` after editing `dynamic.yml`.
- **TLS.** HTTP only (LAN). Add a `websecure` entrypoint + certs in `traefik.yml`
  for HTTPS; switch the firewall rule and `NEXT_PUBLIC_*`/WS scheme accordingly.
