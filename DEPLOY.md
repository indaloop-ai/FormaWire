# FormaWire on madnetai.com

Target: **https://madnetai.com/formawire/**. Ubuntu, .NET 8 Blazor Server,
nginx on the same machine, and Cloudflare Tunnel. No external NuGet packages.
These are deployment instructions; applying them on the server and enabling
Cloudflare settings are separate from building the local app.

## Configuration

Environment variables, appsettings.json, or command-line arguments can set:

| Key | Default | Purpose |
| --- | --- | --- |
| `PathBase` | empty | Empty for local development; `/formawire` in production. A trailing slash is normalized. |
| `MaxUploadMB` | `20` | File limit and SignalR incoming-message limit, in MiB (1 MB here = 1,048,576 bytes). |
| `MaxConcurrentCircuits` | `30` | Total admitted circuits per process, including temporarily disconnected circuits. |
| `SiteUrl` | `https://madnetai.com` | Base URL for the website, terms and privacy links. |

Restart the service after changing these values. Invalid values fail startup.
Only Development enables detailed circuit/SignalR errors. Production HTTP errors
use `/formawire/error`; unexpected circuit errors show a generic reload notice.

There are at most **20 disconnected circuits retained for up to 2 minutes**.
They continue to occupy capacity until disposed; reconnecting reuses the same
slot. These settings are not an inactivity timeout for connected editors.
New visitors beyond the configured cap see “Server occupato” and a retry link
(HTTP 503, Retry-After: 30). The server also checks the cap at circuit creation,
so simultaneous arrivals cannot exceed it. A browser tab normally uses one circuit.

Uploads are read by `Home.OnFile` using a size-limited `IBrowserFile.OpenReadStream`
and a `MemoryStream`, then parsed in memory. **FormaWire.Web never saves uploaded
DXF files to disk or a database.** Exports are generated in memory and downloaded
to the visitor's browser. This statement describes application file handling,
not OS swap, backups or diagnostic dumps. Data Protection keys and operational
logs are separate from drawing files. An upload is rejected before reading if
its reported size exceeds the limit; the stream also enforces the configured
byte limit. Only one upload can run per editor at a time.

Drawings and undo history consume more memory than the source file. The limits
are per process, not a guaranteed RAM budget. Measure peak memory on representative
DXF drawings and lower the limits for the server's capacity. The SignalR message
limit is distinct from the total upload limit: InputFile streams chunks. nginx's
body limit does not constrain frames after a WebSocket upgrade, so the app's
file and circuit limits are still necessary.

## Manual deployment

On Windows, from the repository root:

```powershell
dotnet publish src/FormaWire.Web -c Release -o publish/formawire
```

Copy the **whole `publish/formawire` folder contents**, including `wwwroot/`,
into **`/www/wwwroot/formawire.dev`**. Do not copy `bin/Release` or only the DLLs,
and do not add an extra `formawire` directory inside the destination. Confirm
`wwwroot/app.css`, `editor.js`, `hosting.js` and `favicon.ico` are all present.
Then run on the server:

```sh
sudo chown -R root:formawire /www/wwwroot/formawire.dev
sudo chmod -R u=rwX,g=rX,o= /www/wwwroot/formawire.dev
sudo systemctl restart formawire
curl -i -H 'Host: madnetai.com' http://127.0.0.1:5270/formawire/
```

The curl check must return **HTTP 200**. If it does not, inspect
`sudo journalctl -u formawire -n 50 --no-pager`. The server needs the ASP.NET Core
8 runtime; the existing service starts `FormaWire.Web.dll` with dotnet.

## Missing static assets and startup diagnostics

Production checks the physical WebRootPath directory and all four required files
**before listening for requests**. A missing directory/file produces a critical
log listing the missing paths and exits with code 1, rather than accepting requests
that fail with HTTP 500. Development logs a warning and continues.

```sh
sudo journalctl -u formawire.service -n 50 --no-pager
sudo systemctl status formawire.service
```

If the log says `FormaWire static asset validation failed`, verify that the entire
publish folder was copied, the unit's WorkingDirectory is correct, and the
`formawire` user can traverse the parent directories and read `wwwroot/`. The
permissions on `/www` and `/www/wwwroot` must allow directory traversal by that
user; the commands above only change permissions within the app folder.

AssetVersioner caches each file's SHA-256 URL once per process, including a cached
plain-path fallback and one warning for a missing/unreadable asset. Concurrent
requests share the same computation. URLs remain relative to `<base href>`.
Restart the app after changing assets so their version hashes are recomputed.
A missing asset cannot throw from App.razor's URL generation, even in Development
or if it becomes unavailable during the initial file read.

## systemd

Save as `/etc/systemd/system/formawire.service`:

```ini
[Unit]
Description=FormaWire Blazor Server
After=network.target

[Service]
Type=simple
User=formawire
Group=formawire
WorkingDirectory=/www/wwwroot/formawire.dev
ExecStart=/usr/bin/dotnet /www/wwwroot/formawire.dev/FormaWire.Web.dll
Environment=ASPNETCORE_URLS=http://127.0.0.1:5270
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=PathBase=/formawire
Environment=MaxUploadMB=20
Environment=MaxConcurrentCircuits=30
Environment=SiteUrl=https://madnetai.com
Environment=AllowedHosts=madnetai.com
Restart=always
RestartSec=5
TimeoutStopSec=30
StateDirectory=formawire
StateDirectoryMode=0700
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
UMask=0077

[Install]
WantedBy=multi-user.target
```

`StateDirectory` permits the runtime's Data Protection key storage beneath the
service account's home; it is not an upload directory. Do not expose port 5270
publicly. An application restart discards all in-memory drawings, so users
should download their work before maintenance.

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now formawire
sudo systemctl status formawire
sudo journalctl -u formawire -n 100 --no-pager
curl -I -H 'Host: madnetai.com' http://127.0.0.1:5270/formawire/
```

## nginx and Cloudflare Tunnel

Add these maps once in nginx's `http` context (for example a file in
`/etc/nginx/conf.d/` included from that context):

```nginx
map $http_upgrade $formawire_connection_upgrade {
    default upgrade;
    ''      close;
}

# cloudflared reaches nginx over HTTP, but the visitor used HTTPS.
map $http_x_forwarded_proto $formawire_forwarded_proto {
    default $scheme;
    https   https;
    http    http;
}
```

Add these location blocks to the **existing madnetai.com server block**. Keep
the website's other routes intact. This example assumes the Tunnel's nginx
listener is bound to `127.0.0.1:8080` and is accessible only locally. Configure
the Tunnel's madnetai.com origin service as `http://127.0.0.1:8080` (or keep its
existing local nginx port). Do not point the Tunnel straight at Kestrel if nginx
is intended to apply these settings. Preserve the original Host header.

```nginx
location = /formawire {
    absolute_redirect off;
    return 308 /formawire/$is_args$args;
}

location ^~ /formawire/ {
    # Trust CF-Connecting-IP only because this listener is loopback-only and
    # cloudflared is the local upstream. Never use this trust on a public listener.
    set_real_ip_from 127.0.0.1;
    set_real_ip_from ::1;
    real_ip_header CF-Connecting-IP;

    # No trailing slash: preserve /formawire for app.UsePathBase.
    proxy_pass http://127.0.0.1:5270;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $formawire_connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $formawire_forwarded_proto;
    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
    proxy_buffering off;
    proxy_cache off;
    client_max_body_size 20m;
}
```

Match `client_max_body_size` to `MaxUploadMB` whenever it changes. nginx replaces
forwarded headers rather than appending a client-provided chain. ASP.NET Core
accepts X-Forwarded-For and X-Forwarded-Proto only from its trusted local proxy,
with a forwarding limit of one. If the proxy is moved into another host/container,
explicitly configure that trusted proxy; do not clear the known-proxy restrictions.

[nginx documents the required WebSocket upgrade headers](https://nginx.org/en/docs/http/websocket.html).
Validate and reload:

```sh
sudo nginx -t
sudo systemctl reload nginx
```

In Cloudflare for madnetai.com, set **Network → WebSockets → On**. Create a Cache
Rule to **bypass cache for `/formawire` and `/formawire/*`**; do not cache Blazor
HTML, hub traffic, busy responses or errors. Check that existing “Cache Everything”
rules do not override it. [Cloudflare WebSocket documentation](https://developers.cloudflare.com/network/websockets/).

For public access, add a Cloudflare rate-limiting rule on new editor requests and
SignalR negotiation, not every request or message on an established editor:

```text
(http.host eq "madnetai.com" and
 (http.request.uri.path in {"/formawire" "/formawire/" "/formawire/_blazor/negotiate"}))
```

Use source IP as the counting characteristic. A starting point is 30 requests
per 10 seconds, blocking for 10 seconds; adapt to the account's available controls
and shared office IP traffic. Monitor before tightening it. WAF/rate limiting sees
the initial WebSocket handshake, not ongoing WebSocket frames; it complements the
server-side limits. This guide does not enable any Cloudflare setting automatically.

## Verify before opening access

Local production-style check, from the repository root (command-line settings
also work on Windows; no launch profile is used):

```sh
dotnet run --project src/FormaWire.Web --no-launch-profile -- --urls http://localhost:5270 --PathBase=/formawire
python tests/verify_web_assets.py http://localhost:5270/formawire/
```

Open `http://localhost:5270/formawire/`. Confirm in browser DevTools:

- app.css, editor.js, hosting.js, favicon.ico and `_framework/blazor.web.js` load
  under `/formawire/` without 404s. The base element is `/formawire/`.
- `_blazor/negotiate` uses the prefix and the WebSocket at `/formawire/_blazor`
  upgrades with HTTP 101. Drawing and uploading a small DXF work.
- An oversized DXF reports the 20 MB limit without replacing the existing drawing.
- Terms/privacy links and both export warnings are visible.

Test capacity with `--MaxConcurrentCircuits=1`: open one editor tab, then a second.
The second must show the busy page. Close the first tab; after the retention period
(up to 2 minutes), retrying must work. Existing sessions should remain usable while
new visitors are refused. Reset the test override before deployment.

Repeat the network checks at https://madnetai.com/formawire/ through the Tunnel,
including a refresh and a brief disconnect/reconnect. Verify that the terms and
privacy pages are published on the website; this app only links to them.
