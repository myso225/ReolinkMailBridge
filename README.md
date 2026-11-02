# ReolinkMailBridge

A .NET 10 service that receives Reolink camera **alert emails** on its own SMTP server and republishes them to
**MQTT** with Home Assistant discovery. It is built for battery cameras (Argus Eco Pro) that have no RTSP or HTTP API:
the camera opens the connection itself when it detects something, so it uses no extra battery.

```
camera ──SMTP──► ReolinkMailBridge ──MQTT──► Mosquitto ──► Home Assistant
camera ◄─UDP (Baichuan, battery)─┘
```

## Home Assistant entities (per camera)

| Entity | Source |
|---|---|
| **Motion** (binary) | ON for every alert, turns OFF after `MotionOffDelaySeconds` |
| **Person** / **Vehicle** / **Animal** (binary) | ON when the email names that smart detection |
| **Last picture** (image) | JPEG attached to the last alert email |
| **Last motion** (timestamp) | Camera's time of the last alert; all `state` fields as attributes |
| **Last detection** | `person` / `vehicle` / `animal` / `motion` |
| **Motions (8 h)** | Alerts within the last `ActivityWindowHours` |
| **Battery** (%) | Read from the camera over UDP, see [Battery](#battery) |
| **Battery temperature** (°C, diagnostic) | Same reading |
| **Charging** (binary) | ON while the camera reports `charging` |

Every entity is unavailable while the bridge is offline (MQTT last will on `reolink/bridge/status`).

## MQTT topics

The layout matches EufyNet, so Node-RED flows can treat both alike.

| Topic | Payload | Retained |
|---|---|---|
| `reolink/bridge/status` | `online` / `offline` | yes |
| `reolink/<cam>/state` | `{"camera","model","last_motion","last_type","motions","window_hours","battery","battery_updated","charging","charge_status","adapter","battery_temperature","battery_voltage","picture_taken"}` | yes |
| `reolink/<cam>/image` | last picture, JPEG bytes | yes |
| `reolink/<cam>/event` | `{"camera","type","raw","time","has_picture"}` per alert, for Node-RED | no |
| `reolink/<cam>/motion`, `/person`, `/vehicle`, `/animal` | `ON` | no |

State and the last picture live in `DataDirectory` (`activity.json`, `<cam>.jpg`), so they survive a restart, and are
published again after every MQTT reconnect. The motion counter is recounted every minute, so it goes down without new
alerts. Discovery is sent again whenever Home Assistant publishes `online` on `homeassistant/status`.

## Configuration

Settings live in `appsettings.json` under `Bridge`. Machine-specific values (camera IPs and UIDs) go in
`appsettings.Local.json` next to it, which git ignores:

```json
{ "Bridge": { "Cameras": [ { "Ip": "192.168.1.x", "Uid": "9527000XXXXXXXXX" }, { "Ip": "...", "Uid": "..." } ] } }
```

Any setting can be overridden with an environment variable, for example `Bridge__Mqtt__Host=192.168.1.79`,
`Bridge__Mqtt__Password=...` or `Bridge__Cameras__0__Ip=...` (index as in the `Cameras` list).

Cameras are matched by `SenderAddress` first, then by the camera name found in the email. A camera that isn't
configured is still announced, under a name taken from the email. Each received email is saved as `.eml` in
`SaveMailDirectory` (only the newest `MaxSavedMails` are kept), so you can check what a camera actually sends.

## Battery

Emails carry no battery level, so the bridge logs in to the camera the way the Reolink app does (Baichuan protocol
over UDP, ports 2015/2018, AES after login) and asks for message 253 (BatteryInfo):

- `BatteryReadDelaySeconds` (5) after each alert email, while the camera is awake anyway; one read per camera per 2 min.
- When the last reading is older than `BatteryFallbackHours` (6), including at start. This wakes the camera, which
  costs a little battery; raise the value to save it.

Each camera needs `Ip`, `Uid` (Reolink app → camera → Settings → Device Info) and the camera login. Keep the
password out of `appsettings.json` and pass it as an environment variable, e.g.
`Bridge__Cameras__0__Password=...` for the first camera. Without `Ip`, `Uid` and `Password` the battery stays unknown.

Check one camera by hand: `dotnet run -- battery-probe <ip> <uid>` (asks for the password).

## Camera setup (Reolink app → camera → Settings → More Features → Email Alerts)

Do this for each camera:

| Field | Value |
|---|---|
| Sender's Email | `garage@reolink.local` (Garden: `garden@reolink.local`), the same as `SenderAddress` |
| Password | anything (the bridge accepts any login unless `Smtp:Username` is set) |
| SMTP Server | IP of the host running the bridge, e.g. `192.168.1.79` |
| Server Port | `25` when running locally on Windows, `2525` for the Docker container |
| SSL or TLS | **off** |
| Recipient | anything, e.g. `ha@reolink.local` |

The app sends a test email when you save, which should show up in the bridge log straight away. Under Email Alerts,
keep Email Content on "Text with Picture" so the Last picture entity gets an image.

## Running

Locally:

```bash
dotnet run
```

### Docker

GitHub Actions builds the image for `linux/amd64` and `linux/arm64` (Raspberry Pi 4/5) on every push to `main` and
publishes it as `ghcr.io/myso225/reolink-mail-bridge` with tags `latest`, `sha-<commit>` and, for `v*` git tags,
the version.

On the Pi, add a service to the existing `compose.yaml`. The cameras in the list are the ones in the image's
`appsettings.json` (Garage = 0, Garden = 1), so only their addresses and passwords need setting:

```yaml
  reolink-mail-bridge:
    image: ghcr.io/myso225/reolink-mail-bridge:latest
    container_name: reolink-mail-bridge
    restart: unless-stopped
    user: "1000:1000"
    # Host network: the camera answers the battery query from a new UDP port, which Docker's NAT would drop.
    # SMTP then listens on the host's port 2525 directly (set Server Port 2525 on the cameras).
    network_mode: host
    environment:
      - Bridge__Mqtt__Host=192.168.1.79
      - Bridge__Mqtt__Username=${REOLINK_MQTT_USER}
      - Bridge__Mqtt__Password=${REOLINK_MQTT_PASSWORD}
      - Bridge__Cameras__0__Ip=192.168.1.146
      - Bridge__Cameras__0__Uid=${GARAGE_UID}
      - Bridge__Cameras__0__Password=${GARAGE_PASSWORD}
      - Bridge__Cameras__1__Ip=192.168.1.91
      - Bridge__Cameras__1__Uid=${GARDEN_UID}
      - Bridge__Cameras__1__Password=${GARDEN_PASSWORD}
      - TZ=Europe/Bratislava
    volumes:
      - ./reolink-mail-bridge:/data
```

The `${...}` values come from a `.env` file next to `compose.yaml` (keep it readable by you only, `chmod 600 .env`):

```
REOLINK_MQTT_USER=...
REOLINK_MQTT_PASSWORD=...
GARAGE_UID=...
GARAGE_PASSWORD=...
GARDEN_UID=...
GARDEN_PASSWORD=...
```

Create the data folder owned by the container user first (`mkdir reolink-mail-bridge && sudo chown 1000:1000
reolink-mail-bridge`), then `docker compose pull reolink-mail-bridge && docker compose up -d reolink-mail-bridge`.
If the GitHub package is private, run `docker login ghcr.io` once with a token that has `read:packages`, or make the
package public under GitHub → Packages → reolink-mail-bridge → Package settings.

## Limits

- The battery read needs `network_mode: host` in Docker: the camera answers from a different UDP port than the one
  it was asked on, and Docker's bridge NAT drops that (checked with Docker Desktop: "No answer from camera").
- The parser is checked against real Argus Eco Pro emails (fw v3.0.0.2951). Subject:
  `Person Detected from Garage at 2026/11/2 12:50:08`. Body: `Alarm Camera Name:` / `Alarm Event:` /
  `Alarm Device Name:`, plus a 896×512 JPEG. The "Test e-mail" the app sends on save is ignored.
- `--Bridge:Mqtt:Enabled=false` runs without MQTT: alerts and each camera's state are printed instead.
- Plain SMTP only (no STARTTLS). Keep the port on the LAN.
