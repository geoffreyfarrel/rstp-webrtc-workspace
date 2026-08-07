<template>
  <main class="app-container">
    <h1>RTSP to WebRTC Stream</h1>

    <div class="video-grid">
      <div v-for="camera in cameras" :key="camera.id" class="video-wrapper">
        <h2 class="camera-name">{{ camera.name }}</h2>
        <video :ref="(el) => setVideoRef(camera.id, el)" autoplay playsinline muted controls></video>
      </div>
    </div>
  </main>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { useConnectionStore } from './stores/connectionStore';
import { ConnectionStatusEnum, type Camera, type TurnCredentials } from './typings';
import { WebRTCPlayer } from '@eyevinn/webrtc-player';

const cameras = ref<Camera[]>([]);
const videoRefs = new Map<string, HTMLVideoElement>();
const players = new Map<string, WebRTCPlayer>();
const playingHandlers = new Map<string, () => void>();

const connectionStore = useConnectionStore();

// C# WHEP backend (backend/Controllers/StreamController.cs). Defaults to
// same-origin so it goes through vite.config.ts's /Stream proxy - required
// for ngrok/remote access (an absolute http://localhost:5014 only resolves
// on this machine, and would also get blocked as mixed content on ngrok's
// https:// page). Only set VITE_BACKEND_URL to bypass the proxy.
const BACKEND_URL = import.meta.env.VITE_BACKEND_URL || window.location.origin;

function setVideoRef(cameraId: string, el: Element | { $el: Element } | null) {
  if (el instanceof HTMLVideoElement) {
    videoRefs.set(cameraId, el);
  }
}

async function connectCamera(camera: Camera) {
  const videoEl = videoRefs.get(camera.id);
  if (!videoEl) return;

  connectionStore.setConnection(camera.id, ConnectionStatusEnum.CONNECTING);

  // Fetch a fresh, time-limited TURN credential for this camera rather than
  // using a static username/password - avoids every browser session and
  // every camera sharing one TURN identity (that contention was the actual
  // cause of two-camera ICE failures; see backend's use-auth-secret setup).
  let turnCreds: TurnCredentials;
  try {
    const credsResponse = await fetch(new URL(`/Stream/turn-credentials/${camera.id}`, BACKEND_URL));
    if (!credsResponse.ok) throw new Error(`Backend returned ${credsResponse.status}`);
    turnCreds = await credsResponse.json();
  } catch (error) {
    connectionStore.setConnection(camera.id, ConnectionStatusEnum.FAILED);
    console.error(`Failed to fetch TURN credentials for '${camera.id}':`, error);
    return;
  }

  const handlePlaying = () => {
    connectionStore.setConnection(camera.id, ConnectionStatusEnum.CONNECTED);
  };
  playingHandlers.set(camera.id, handlePlaying);
  videoEl.addEventListener('playing', handlePlaying);

  const player = new WebRTCPlayer({
    video: videoEl,
    type: 'whep',
    statsTypeFilter: '^candidate-*|^inbound-rtp',
    iceServers: [
      // Same coturn box also answers plain STUN binding requests - lets ICE learn
      // the server-reflexive candidate without needing a full TURN Allocate every
      // time.
      { urls: `stun:${turnCreds.host}` },
      {
        urls: `turn:${turnCreds.host}`,
        username: turnCreds.username,
        credential: turnCreds.credential,
      },
    ],
  });
  players.set(camera.id, player);

  // @ts-expect-error - WebRTCPlayer's shipped types don't resolve its EventEmitter base (missing 'events' module types), so `.on` isn't seen despite existing at runtime.
  player.on('no-media', () => {
    connectionStore.setConnection(camera.id, ConnectionStatusEnum.FAILED);
    console.error(`WHEP stream timed out with no media (${camera.id}).`);
  });

  console.log(`Negotiating WebRTC connection for '${camera.id}' (WHEP via C# backend)...`);

  try {
    await player.load(new URL(`/Stream/whep/${camera.id}`, BACKEND_URL));
    player.unmute();
  } catch (error) {
    connectionStore.setConnection(camera.id, ConnectionStatusEnum.FAILED);
    console.error(`Failed to connect stream '${camera.id}':`, error);
  }
}

// Two cameras negotiating ICE at the same time on the backend reliably
// fails - SIPSorcery's RtpIceChannel connectivity checks for both cameras
// contend with each other and every checklist entry (host, srflx, and even
// relay-to-relay pairs) ends up timing out, even though either camera
// connects fine alone. Waiting for one camera to fully settle (connected or
// failed) before starting the next one's negotiation avoids that
// contention entirely.
function waitForOutcome(cameraId: string, timeoutMs = 15000): Promise<void> {
  return new Promise((resolve) => {
    const start = Date.now();
    const check = () => {
      const status = connectionStore.getConnection(cameraId);
      if (status !== ConnectionStatusEnum.CONNECTING || Date.now() - start > timeoutMs) {
        resolve();
        return;
      }
      setTimeout(check, 250);
    };
    check();
  });
}

onMounted(async () => {
  try {
    const response = await fetch(new URL('/Stream/cameras', BACKEND_URL));
    cameras.value = await response.json();
  } catch (error) {
    console.error('Failed to load camera list:', error);
    return;
  }

  // Wait for the v-for above to render a <video> per camera before wiring
  // players up to them.
  await nextTick();
  for (const camera of cameras.value) {
    await connectCamera(camera);
    await waitForOutcome(camera.id);
  }

  // Expose for console debugging
  (window as any).webRtcPlayers = players;
});

onBeforeUnmount(() => {
  for (const [cameraId, videoEl] of videoRefs) {
    const handler = playingHandlers.get(cameraId);
    if (handler) videoEl.removeEventListener('playing', handler);
  }
  for (const [cameraId, player] of players) {
    player.destroy();
    connectionStore.setConnection(cameraId, ConnectionStatusEnum.DISCONNECTED);
  }
  players.clear();
});
</script>

<style scoped>
.app-container {
  display: flex;
  flex-direction: column;
  align-items: center;

  padding: 2rem;
  min-height: 100vh;

  font-family: sans-serif;

  background-color: #1a1a1a;
  color: white;
}

.video-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;

  width: 100%;
  max-width: 1400px;
  margin-top: 1rem;
}

.video-wrapper {
  overflow: hidden;
}

.camera-name {
  margin: 0 0 0.5rem;
  font-size: 1rem;
  font-weight: 600;
}

video {
  display: block;
  width: 100%;

  background: black;
  border-radius: 12px;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0);
}
</style>
