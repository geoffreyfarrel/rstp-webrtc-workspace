# RTSP → WebRTC: Setup Guide

How to run this project end to end: an RTSP camera streamed to a browser
over WebRTC, via a C# WHEP backend and a TURN relay.

## Architecture

```
Camera (RTSP) → backend/ (.NET + SIPSorcery, WHEP) → TURN relay → frontend/ (Vue, browser)
```

- **`backend/`** — ASP.NET Core app. Pulls H264 from one or more cameras over
  RTSP (via `SIPSorceryMedia.FFmpeg`) and serves each as a
  [WHEP](https://datatracker.ietf.org/doc/draft-ietf-wish-whep/) endpoint
  (`POST /Stream/whep/{cameraId}`, cameras listed at `GET /Stream/cameras`)
  using SIPSorcery for the WebRTC/ICE side.
- **`frontend/`** — Vue 3 + Vite app. Fetches the camera list and plays one
  stream per camera with `@eyevinn/webrtc-player` (a WHEP client).
- **TURN server (coturn)** — relays media between the backend and the
  browser when a direct path isn't available. This project connects to an
  already-running coturn instance; it isn't part of this repo's setup.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js (`^22.18.0` or `>=24.12.0` — see `frontend/package.json`)
- A camera with an RTSP stream (H264)
- A reachable TURN server (host, username, credential)

FFmpeg itself doesn't need to be installed separately — the native libav*
DLLs `SIPSorceryMedia.FFmpeg` needs are already vendored in
`backend/public/bin`.

## 1. Configure and run the backend

`backend/appsettings.json` holds live camera/TURN credentials, so it's
gitignored and never committed — only `backend/appsettings.example.json`
(a placeholder template) is tracked. Copy it and fill in real values:

```bash
cp backend/appsettings.example.json backend/appsettings.json
```

```json
{
  "Turn": {
    "Host": "<turn-host>:3478",
    "Secret": "<turn-static-auth-secret>"
  },
  "StreamSettings": {
    "FFmpegLibPath": "public/bin",
    "TargetBitrate": 100000,
    "Cameras": [
      { "Id": "camera1", "Name": "Camera 1", "RtspUrl": "rtsp://<user>:<password>@<camera-ip>/<path>" },
      { "Id": "camera2", "Name": "Camera 2", "RtspUrl": "rtsp://<user>:<password>@<camera-ip>/<path>", "RtspTransport": "tcp" }
    ]
  }
}
```

- `RtspTransport` (optional, per camera) — forces libavformat's RTSP
  transport negotiation to `"tcp"` or `"udp"`. Leave it unset and FFmpeg's
  default auto-negotiation applies; only set it for a camera/network where
  that auto-negotiation doesn't land on a working transport.

- `Turn:Host` must point at your TURN server. `Turn:Secret` is coturn's
  `static-auth-secret` (requires coturn configured with `lt-cred-mech` +
  `use-auth-secret`, not a static `user=` entry) - the backend mints a
  fresh, time-limited username/credential per camera per connection from
  this secret (`TurnCredentialGenerator`, coturn's TURN REST API scheme)
  instead of every camera and every browser session sharing one static
  TURN identity. That per-identity sharing was the actual cause of a
  two-camera ICE failure bug: N cameras meant 2N concurrent TURN
  allocations (backend + browser side, per camera) all under the same
  username, and adding a camera would flap an existing one's connection.
  The browser gets its own credential via `GET
  /Stream/turn-credentials/{cameraId}` - the secret itself never reaches
  client code.
- `StreamSettings:Cameras` is an array — add one entry per camera. `Id` is
  used in the URL (`/Stream/whep/{Id}`) so keep it URL-safe; `Name` is just
  for display. `RtspUrl` is that camera's RTSP URL, credentials included -
  it's never sent to the browser, only `Id`/`Name` are (via `GET
  /Stream/cameras`).
- `TargetBitrate` caps the H264 encoder's output (bits per second), applied
  to every camera.

Then run it:

```bash
cd backend
dotnet run
```

By default it listens on `http://localhost:5014` (see
`backend/Properties/launchSettings.json`). Confirm it's up:

```bash
curl http://localhost:5014/Stream/test
# → "Backend is reachable!"
```

## 2. Configure and run the frontend

```bash
cd frontend
npm install
cp .env.example .env
```

No editing needed for a local setup — the browser gets its TURN
host/username/credential dynamically per camera from the backend
(`GET /Stream/turn-credentials/{cameraId}`), so `frontend/.env` has
nothing TURN-related to configure. Leave `VITE_BACKEND_URL` commented out
to use the dev-server proxy (see `frontend/vite.config.ts`, which forwards
`/Stream` to `http://localhost:5014`) — this is what makes the app work
over ngrok or from another device too, not just `localhost`. Only set it
if you're bypassing the proxy (e.g. backend running on a different host).

Then run it:

```bash
npm run dev
```

By default it's served at `http://localhost:5173`.

## 3. Watch it work

Open `http://localhost:5173` in a browser. One video tile per camera in
`StreamSettings:Cameras` should appear and start playing within a few
seconds.

What a working connection looks like in the backend console (each line is
prefixed with the camera's `Id` so multiple cameras' logs stay
distinguishable):

```
[camera1] [PC] ICE gathering state: complete
[camera1] [PC] Connection state: connected
[camera1] [FFmpeg] Starting video source...
[camera1] [ffmpeg] Start() completed successfully.
[camera1] [FFmpeg] Sending sample, ... bytes
```

And in the browser console: no errors from `@eyevinn/webrtc-player`, and the
`<video>` element starts playing.

## Notes

- CORS on the backend (`backend/Program.cs`) only allows
  `http://localhost:5173` — if you serve the frontend from a different
  origin, update that policy too.
- The backend logs the full offer/answer SDP at `Information` level
  (`backend/Controllers/StreamController.cs`) — useful if a connection ever
  stops working again and you need to inspect the negotiated SDP.

## Reusing this repo in another project

This repo has no site-specific config committed to it — everything
environment-specific (camera URLs/credentials, TURN host/secret,
`VITE_TURN_HOST`) lives in gitignored files (`backend/appsettings.json`,
`frontend/.env`) copied from the tracked `*.example.*` templates. That
means you can pull the whole repo into another project without carrying
anyone's credentials along with it. Two ways to do that:

- **Git submodule** (keeps it updatable): `git submodule add
  https://github.com/geoffreyfarrel/rstp-webrtc-workspace.git
  <path-in-your-project>`, then pin it to a release tag with `git -C
  <path-in-your-project> checkout v1.1.0` (see `git tag -l` /
  [CHANGELOG.md](CHANGELOG.md) for what changed at each version).
- **Vendor a snapshot** (no ongoing link back to this repo): download the
  source at a given tag (`git archive` or the GitHub "download zip" for
  that tag) and drop `backend/` and `frontend/` into your project directly.

Either way, after pulling it in you still do the normal setup above: copy
the `*.example.*` files, fill in your new project's credentials, and
they'll stay local (gitignored) from there on.
