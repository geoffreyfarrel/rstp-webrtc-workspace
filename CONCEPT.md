# Concept & Flow

What this system does and how a frame gets from a camera's RTSP stream to
a `<video>` element in the browser. For how to run it, see
[SETUP.md](SETUP.md); for what changed release to release, see
[CHANGELOG.md](CHANGELOG.md).

## The problem this solves

IP cameras speak RTSP. Browsers don't — they speak WebRTC. Something has
to sit in the middle: pull H264 off the RTSP stream, and re-package it as
a WebRTC media stream a browser can subscribe to, with sub-second latency
(RTSP-over-HTTP proxies or HLS repackaging both add multi-second buffering,
which is fine for a security-camera archive but not for anything
interactive). That's the whole job of `backend/`.

## Actors

```
Camera (RTSP)  →  backend/ (.NET, SIPSorcery + FFmpeg)  →  TURN relay  →  frontend/ (Vue, browser)
```

- **Camera** — any RTSP source producing H264 (a physical IP camera,
  MediaMTX serving a looped test file, or `ffmpeg -re` publishing to one).
- **backend/** — pulls RTSP, decodes/re-encodes with FFmpeg, and speaks
  [WHEP](https://datatracker.ietf.org/doc/draft-ietf-wish-whep/) (WebRTC-HTTP
  Egress Protocol) to hand that media to a browser over standard WebRTC.
  One backend process serves N cameras, each as its own WHEP endpoint.
- **TURN relay (coturn)** — a server the backend and browser both connect
  through when a direct peer-to-peer path isn't reachable (NAT, different
  networks). Not part of this repo — it's configured as an external
  dependency (`Turn:Host` / `Turn:Secret` in `appsettings.json`).
- **frontend/** — a Vue page that lists available cameras and opens one
  WHEP connection per camera via `@eyevinn/webrtc-player`.

## Flow 1: loading the page (camera discovery)

1. Browser loads the Vue app; `onMounted` fires a `GET /Stream/cameras`.
2. The backend reads `StreamSettings:Cameras` from config and returns only
   `{ id, name }` per camera — never `RtspUrl`. Camera credentials never
   leave the server.
3. The frontend renders one `<video>` tile per camera and calls
   `connectCamera()` for each, concurrently (not sequentially — so a slow
   or hung camera doesn't delay the rest).

## Flow 2: connecting one camera (WHEP negotiation)

This is the core of `StreamController.PostWhepOffer` and happens
independently per camera:

1. **TURN credentials, minted per side.** Before touching WebRTC, the
   frontend calls `GET /Stream/turn-credentials/{cameraId}`. The backend
   uses coturn's `use-auth-secret` REST scheme (`TurnCredentialGenerator`)
   to mint a fresh username/credential pair from `Turn:Secret`, valid for
   1 hour, scoped to that camera. The backend also mints its *own* separate
   credential (`backend-{cameraId}`) for its side of the same connection.

   This exists because of a real bug: with one static TURN identity shared
   by every camera and every browser session, N cameras meant 2N
   concurrent TURN allocations all under the same username — adding a
   camera would flap an already-connected one. Per-connection identities
   fixed it. `Turn:Secret` itself is never sent to the browser, only the
   derived, time-limited credential.

2. **Browser creates the offer.** `WebRTCPlayer` (a WHEP client) builds an
   `RTCPeerConnection` using the TURN/STUN servers from step 1, creates an
   SDP offer, and `POST`s it to `/Stream/whep/{cameraId}`.

3. **Backend sets up its own peer connection and media source**, in
   parallel with reading the offer:
   - Looks up the camera's config by `cameraId` (404 if unknown).
   - Builds its own `RTCPeerConnection` with the STUN/TURN servers (its
     own backend-side credential from step 1).
   - Creates an `FFmpegFileSource` pointed at the camera's `RtspUrl`,
     restricted to H264. If the camera config sets `RtspTransport`
     (`tcp`/`udp`), that's forced here via a reflection call into FFmpeg's
     internal decoder — needed for cameras whose RTSP transport
     auto-negotiation doesn't land on something that works.

4. **SDP exchange.** The backend sets the browser's offer as its remote
   description, creates an answer, and waits (up to 120s) for ICE
   gathering to complete so the answer's SDP carries every candidate the
   backend gathered — including the TURN relay candidate, which a WHEP
   client can't retry for later (no trickle ICE on this path). A
   post-processing step re-stamps ICE candidates from the
   `onicecandidate` callbacks directly into every SDP media section,
   because SIPSorcery's own SDP renderer was observed to only carry a
   partial candidate subset (typically dropping the relay candidate,
   which matters most for anyone not on the LAN).

5. **201 Created** with the answer SDP is returned — that response *is*
   the WHEP contract. The browser applies it as its remote description
   and the ICE/DTLS handshake proceeds using the negotiated candidates
   (direct, srflx, or via the TURN relay).

6. **Once the peer connection reaches `connected`**, the backend starts
   the FFmpeg source (`ffmpegSource.Start()`, 20s timeout — camera
   unreachable/wrong credentials fails fast instead of hanging). From
   here every encoded H264 sample FFmpeg produces is forwarded straight
   into the peer connection (`pc.SendVideo`); no frame is ever dropped
   after the first IDR, because skipping any later frame breaks H264's
   inter-frame prediction chain and desyncs the decoder. If throttling is
   ever needed, it has to happen upstream, on *raw* frames before
   encoding, not on encoded samples.

7. **Bitrate cap.** Once the browser's chosen video format is known
   (`OnVideoFormatsNegotiated`), the backend also reaches into FFmpeg's
   internal encoder (again via reflection — `SIPSorceryMedia.FFmpeg`
   doesn't expose this on the public API) and caps output bitrate to
   `StreamSettings:TargetBitrate`, applied uniformly to every camera.

## Flow 3: teardown

When the peer connection state goes to `closed` or `failed` (browser tab
closed, network drop, `player.destroy()` on unmount), the backend closes
the FFmpeg source and the peer connection. Each camera's connection is
fully independent — one camera failing doesn't affect the others'
`RTCPeerConnection`/FFmpeg pipeline.

## Why a relay and not just peer-to-peer

Cameras usually sit on a LAN behind NAT with no port-forwarding, and the
whole point is remote viewing. STUN alone only helps when at least one
side has a routable server-reflexive candidate; whenever that's not true
for the actual network path, TURN is what makes the connection possible
at all — hence the relay being a first-class, always-configured part of
this architecture rather than a fallback bolted on later.

## Config → code map

| Config key | Read in | Effect |
|---|---|---|
| `StreamSettings:Cameras[].RtspUrl` | `StreamController.PostWhepOffer` | Source URL for `FFmpegFileSource` |
| `StreamSettings:Cameras[].RtspTransport` | `StreamController.PostWhepOffer` | Forces `rtsp_transport` on the FFmpeg decoder |
| `StreamSettings:TargetBitrate` | `StreamController.PostWhepOffer` | Caps the H264 encoder's output bitrate |
| `StreamSettings:FFmpegLibPath` | `StreamController.Initialise` | Where the native `libav*` DLLs live |
| `Turn:Host` / `Turn:Secret` | `StreamController` (both endpoints) | STUN/TURN server + credential minting |
| `frontend/.env`'s `VITE_BACKEND_URL` | `App.vue` | Backend origin; unset it to use the Vite dev-server proxy instead (see below) |
