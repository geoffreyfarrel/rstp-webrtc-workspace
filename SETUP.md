# RTSP → WebRTC: Setup Guide

How to run this project end to end: an RTSP camera streamed to a browser
over WebRTC, via a C# WHEP backend and a TURN relay.

## Architecture

```
Camera (RTSP) → backend/ (.NET + SIPSorcery, WHEP) → TURN relay → frontend/ (Vue, browser)
```

- **`backend/`** — ASP.NET Core app. Pulls H264 from the camera over RTSP
  (via `SIPSorceryMedia.FFmpeg`) and serves it as a
  [WHEP](https://datatracker.ietf.org/doc/draft-ietf-wish-whep/) endpoint
  (`POST /Stream/whep`) using SIPSorcery for the WebRTC/ICE side.
- **`frontend/`** — Vue 3 + Vite app. Plays the stream with
  `@eyevinn/webrtc-player` (a WHEP client).
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

Edit `backend/appsettings.json`:

```json
{
  "Turn": {
    "Host": "<turn-host>:3478",
    "Username": "<turn-username>",
    "Credential": "<turn-credential>"
  },
  "StreamSettings": {
    "FFmpegLibPath": "public/bin",
    "RtspUrl": "rtsp://<user>:<password>@<camera-ip>/<path>",
    "TargetBitrate": 100000
  }
}
```

- `Turn` must point at your TURN server.
- `StreamSettings:RtspUrl` is your camera's RTSP URL, credentials included.
- `TargetBitrate` caps the H264 encoder's output (bits per second).

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

Edit `frontend/.env` — the TURN values must match what you put in
`backend/appsettings.json`:

```bash
VITE_TURN_HOST=<turn-host>:3478
VITE_TURN_USERNAME=<turn-username>
VITE_TURN_CREDENTIAL=<turn-credential>
```

Leave `VITE_BACKEND_URL` commented out to use the dev-server proxy (see
`frontend/vite.config.ts`, which forwards `/Stream` to
`http://localhost:5014`) — this is what makes the app work over ngrok or
from another device too, not just `localhost`.

Then run it:

```bash
npm run dev
```

By default it's served at `http://localhost:5173`.

## 3. Watch it work

Open `http://localhost:5173` in a browser. Video should start playing within
a few seconds.

What a working connection looks like in the backend console:

```
[PC] ICE gathering state: complete
[PC] Connection state: connected
[FFmpeg] Starting video source...
[ffmpeg] Start() completed successfully.
[FFmpeg] Sending sample, ... bytes
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
