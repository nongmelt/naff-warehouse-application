# Traefik reverse proxy

Single entrypoint (port 80) that fronts the warehouse stack on `*.warehouse.local`
and only admits the LAN subnets `192.168.0.0/24` and `192.168.1.0/24`.

**Replaces `compose.nginx.yml`.** Never run both — they both bind host port 80.

## Hosts

| Host                       | Target          |
|----------------------------|-----------------|
| `app.warehouse.local`      | `frontend:3000` |
| `api.warehouse.local`      | `backend:8080`  |
| `minio.warehouse.local`    | `minio:9000` (S3 API) |
| `console.warehouse.local`  | `minio:9001` (MinIO console) |
| `traefik.warehouse.local`  | Traefik dashboard |

## Run

```bash
docker compose \
  -f compose.yml -f compose.db.yml -f compose.minio.yml \
  -f compose.traefik.yml -f compose.dnsmasq.yml up -d
```

## Required out-of-proxy steps

The proxy alone is not enough — three things outside this file must also be set:

1. **L3 routing.** The server lives on `192.168.1.0/24`. PCs on `192.168.0.0/24`
   can only reach it if a router/VLAN routes between the subnets. Traefik filters
   traffic that arrives; it cannot create the path.

2. **DNS.** Clients must resolve `*.warehouse.local` to the server IP. Either run
   `compose.dnsmasq.yml` and point clients' DNS at the server, or add hosts-file
   entries on each PC. Set `SERVER_IP` in `dnsmasq/dnsmasq.conf`.

3. **Frontend rebuild + backend config.** `NEXT_PUBLIC_*` URLs are baked into the
   frontend image at BUILD time, so the image must be rebuilt with the
   `warehouse.local` hosts (see `.env.example`). Also:
   - Backend `CORS_ORIGIN` must include `http://app.warehouse.local`.
   - **MinIO presigned URLs** are signed by the backend using its MinIO endpoint.
     Off-subnet browsers can only fetch objects if those URLs use
     `http://minio.warehouse.local` — the backend must presign against the public
     host, not the internal `minio:9000`, or fetches fail with `SignatureDoesNotMatch`.

## TLS

HTTP only (LAN). To add HTTPS, define a `websecure` entrypoint + a certificate
resolver (or static certs) in `traefik.yml` and switch the routers to it.
Note: browser `getUserMedia`/secure-context features require HTTPS off `localhost`.
