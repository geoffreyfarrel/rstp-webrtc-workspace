<template>
  <main class="app-container">
    <h1>RTSP to WebRTC Stream</h1>

    <div class="video-wrapper">
      <video ref="videoElement" autoplay playsinline muted controls></video>
    </div>
  </main>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useConnectionStore } from './stores/connectionStore';
import { ConnectionStatusEnum } from './typings';
import { WebRTCPlayer } from '@eyevinn/webrtc-player';

const videoElement = ref<HTMLVideoElement | null>(null);
let player: WebRTCPlayer | null = null;

const connectionStore = useConnectionStore();

// C# WHEP backend (backend/Controllers/StreamController.cs). Defaults to
// same-origin so it goes through vite.config.ts's /Stream proxy - required
// for ngrok/remote access (an absolute http://localhost:5014 only resolves
// on this machine, and would also get blocked as mixed content on ngrok's
// https:// page). Only set VITE_BACKEND_URL to bypass the proxy.
const BACKEND_URL = import.meta.env.VITE_BACKEND_URL || window.location.origin;

const handlePlaying = () => {
  connectionStore.setConnection(ConnectionStatusEnum.CONNECTED);
};

onMounted(async () => {
  if (!videoElement.value) return;

  videoElement.value.addEventListener('playing', handlePlaying);

  player = new WebRTCPlayer({
    video: videoElement.value,
    type: 'whep',
    statsTypeFilter: '^candidate-*|^inbound-rtp',
    iceServers: [
      {
        urls: `turn:${import.meta.env.VITE_TURN_HOST}`,
        username: import.meta.env.VITE_TURN_USERNAME,
        credential: import.meta.env.VITE_TURN_CREDENTIAL,
      },
    ],
  });

  // @ts-expect-error - WebRTCPlayer's shipped types don't resolve its EventEmitter base (missing 'events' module types), so `.on` isn't seen despite existing at runtime.
  player.on('no-media', () => {
    connectionStore.setConnection(ConnectionStatusEnum.FAILED);
    console.error('WHEP stream timed out with no media.');
  });

  // Expose for console debugging
  (window as any).webRtcPlayer = player;

  connectionStore.setConnection(ConnectionStatusEnum.CONNECTING);
  console.log('Negotiating WebRTC connection (WHEP via C# backend)...');

  try {
    await player.load(new URL('/Stream/whep', BACKEND_URL));
    player.unmute();
  } catch (error) {
    connectionStore.setConnection(ConnectionStatusEnum.FAILED);
    console.error('Failed to connect stream:', error);
  }
});

onBeforeUnmount(() => {
  videoElement.value?.removeEventListener('playing', handlePlaying);
  if (player) {
    player.destroy();
    player = null;
    connectionStore.setConnection(ConnectionStatusEnum.DISCONNECTED);
  }
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

.video-wrapper {
  position: relative;
  overflow: hidden;
  width: 100%;

  width: 100%;
  min-height: 400px;
  max-width: 900px;
  margin-top: 1rem;

  background: black;
  border-radius: 12px;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0);
}

.video {
  display: block;
  width: 100%;
}

.overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;

  background: black;

  z-index: 2;
}

.spinner {
  display: flex;
  align-items: center;
  justify-content: center;
}

.spinner-logo {
  width: 75px;
  height: 75px;

  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}
</style>
