# Changelog

All notable changes to this project are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are tagged
git refs (`git tag -l`).

## [1.1.0] - 2026-08-07

### Added

- Multi-camera support: `StreamSettings:Cameras` is now an array, each
  camera served at its own `/Stream/whep/{Id}` endpoint and given its own
  minted TURN credential (`TurnCredentialGenerator`) instead of one shared
  identity across all cameras/sessions.
- Per-camera `RtspTransport` override (`"tcp"` / `"udp"`) so a camera whose
  RTSP transport auto-negotiation doesn't settle on a working transport can
  have it forced explicitly.
- RTSP connection timeout, so a dead/unreachable camera fails fast instead
  of hanging the stream startup.
- Cameras load concurrently on frontend mount instead of sequentially.

### Changed

- `backend/appsettings.json` is no longer tracked in git (it holds live
  camera/TURN credentials). `backend/appsettings.example.json` is the
  committed template — copy it to `appsettings.json` and fill in real
  values locally. See [SETUP.md](SETUP.md).
- FFmpeg lib path resolution uses a relative path instead of a
  machine-specific hardcoded one.

### Removed

- Dead `webrtc-streamer` Docker service code and the unused
  connection-status/spinner UI left over from that approach, superseded by
  the SIPSorcery WHEP backend.

### Fixed

- SDP corruption bug that broke WHEP/SIPSorcery ICE negotiation.
- Relay ICE candidates being dropped from the WHEP answer SDP.

### Security

- Scrubbed historical RTSP/TURN credentials from git history (they had been
  committed directly in `backend/appsettings.json` across several earlier
  commits). If you have an existing clone from before this rewrite, re-clone
  rather than pull — the history was rewritten and force-pushed.

## [1.0.0] - 2026-07-28

- First working RTSP-to-WebRTC pipeline (C# / SIPSorcery WHEP backend +
  Vue frontend).
