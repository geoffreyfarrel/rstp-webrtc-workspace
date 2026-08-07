// Minimal WHEP server - no SIPSorcery, no C#. Same /Stream/whep contract as
// backend/Controllers/StreamController.cs, same TURN server, same RTSP
// camera, same bitrate cap. Only the WebRTC/ICE engine differs (werift
// instead of SIPSorcery), to isolate whether the app's ICE bug is
// SIPSorcery-specific. frontend/src/App.vue talks to this unmodified - just
// point VITE_BACKEND_URL at this server's origin.

const http = require("http");
const { spawn } = require("child_process");
const {
  RTCPeerConnection,
  RTCRtpCodecParameters,
  MediaStreamTrackFactory,
  useOPUS,
} = require("werift");

try {
  process.loadEnvFile();
} catch {
  // no .env present - fall back to whatever's already in the environment
}

const PORT = Number(process.env.PORT || 8092);
const RTSP_URL = process.env.RTSP_URL;
const TURN_HOST = process.env.TURN_HOST;
const TURN_USERNAME = process.env.TURN_USERNAME;
const TURN_CREDENTIAL = process.env.TURN_CREDENTIAL;
const TARGET_BITRATE = Number(process.env.TARGET_BITRATE || 100000);
const H264_PAYLOAD_TYPE = 102;

if (!RTSP_URL || !TURN_HOST) {
  console.error("Missing RTSP_URL/TURN_HOST - copy .env.example to .env and fill it in.");
  process.exit(1);
}

function setCors(req, res) {
  const origin = req.headers.origin || "*";
  res.setHeader("Access-Control-Allow-Origin", origin);
  res.setHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type");
}

async function handleWhep(req, res) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const offerSdp = Buffer.concat(chunks).toString("utf8");
  if (!offerSdp) {
    res.writeHead(400).end("No SDP offer provided.");
    return;
  }

  console.log(`[WHEP] Offer received (${offerSdp.length} bytes)`);

  const [track, rtpPort, disposeRtpSource] = await MediaStreamTrackFactory.rtpSource({
    kind: "video",
  });

  const ffmpeg = spawn("ffmpeg", [
    "-rtsp_transport", "tcp",
    "-i", RTSP_URL,
    "-an",
    "-c:v", "libx264",
    "-preset", "ultrafast",
    "-tune", "zerolatency",
    "-profile:v", "baseline",
    "-level", "3.1",
    "-pix_fmt", "yuv420p",
    "-b:v", String(TARGET_BITRATE),
    "-maxrate", String(TARGET_BITRATE),
    "-bufsize", String(TARGET_BITRATE * 2),
    "-g", "30",
    "-f", "rtp",
    "-payload_type", String(H264_PAYLOAD_TYPE),
    `rtp://127.0.0.1:${rtpPort}?pkt_size=1200`,
  ]);
  ffmpeg.stderr.on("data", (d) => console.log(`[ffmpeg] ${d.toString().trim()}`));
  ffmpeg.on("exit", (code) => console.log(`[ffmpeg] exited (${code})`));

  const cleanup = () => {
    ffmpeg.kill("SIGKILL");
    disposeRtpSource();
  };

  const pc = new RTCPeerConnection({
    iceServers: [
      {
        urls: `turn:${TURN_HOST}`,
        username: TURN_USERNAME,
        credential: TURN_CREDENTIAL,
      },
    ],
    iceTransportPolicy: "relay",
    codecs: {
      // We never add an audio track (matches StreamController.cs, which is
      // video-only). Both sides end up recvonly for audio, which negotiates
      // down to inactive - but werift's config merge is shallow (it
      // replaces the whole `codecs` object, it doesn't merge audio/video
      // independently), so omitting a valid audio codec here would leave
      // the auto-created audio transceiver with none to negotiate with.
      audio: [useOPUS()],
      video: [
        new RTCRtpCodecParameters({
          mimeType: "video/H264",
          clockRate: 90000,
          payloadType: H264_PAYLOAD_TYPE,
          parameters: "packetization-mode=1;profile-level-id=42e01f",
          rtcpFeedback: [
            { type: "nack" },
            { type: "nack", parameter: "pli" },
            { type: "goog-remb" },
          ],
        }),
      ],
    },
  });

  pc.connectionStateChange.subscribe((state) => {
    console.log(`[PC] Connection state: ${state}`);
    if (state === "closed" || state === "failed") cleanup();
  });

  try {
    pc.addTransceiver(track, { direction: "sendonly" });

    await pc.setRemoteDescription({ type: "offer", sdp: offerSdp });
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);

    await new Promise((resolve) => {
      if (pc.iceGatheringState === "complete") {
        resolve();
        return;
      }
      const timeout = setTimeout(() => {
        console.log("[PC] ICE gathering did not complete within 8s, returning SDP anyway.");
        resolve();
      }, 8000);
      pc.onicegatheringstatechange = () => {
        console.log(`[PC] ICE gathering state: ${pc.iceGatheringState}`);
        if (pc.iceGatheringState === "complete") {
          clearTimeout(timeout);
          resolve();
        }
      };
    });

    const finalSdp = pc.localDescription.sdp;
    console.log(`[PC] Returning answer (${finalSdp.length} bytes)`);

    setCors(req, res);
    res.writeHead(201, { "Content-Type": "application/sdp" });
    res.end(finalSdp);
  } catch (err) {
    console.error("[PC] Negotiation failed:", err);
    cleanup();
    setCors(req, res);
    res.writeHead(500).end("Failed to negotiate WebRTC connection.");
  }
}

const server = http.createServer((req, res) => {
  if (req.method === "OPTIONS") {
    setCors(req, res);
    res.writeHead(204).end();
    return;
  }

  if (req.method === "POST" && req.url === "/Stream/whep") {
    handleWhep(req, res).catch((err) => {
      console.error("[WHEP] Unhandled error:", err);
      setCors(req, res);
      res.writeHead(500).end("Internal error.");
    });
    return;
  }

  res.writeHead(404).end("Not found. POST SDP offers to /Stream/whep.");
});

server.listen(PORT, () => {
  console.log(`werift WHEP server on http://localhost:${PORT}`);
  console.log(`POST /Stream/whep - RTSP: ${RTSP_URL.replace(/\/\/.*@/, "//<redacted>@")}`);
  console.log(`TURN: ${TURN_HOST} (iceTransportPolicy=relay)`);
});
