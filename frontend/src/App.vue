<template>
  <main class="app-container">
    <h1>RTSP to WebRTC Stream</h1>

    <div class="video-wrapper">
      <!-- The native video element -->
      <video ref="videoElement" autoplay playsinline muted controls></video>
      <div
        v-if="connectionStore.connectionStatus !== ConnectionStatusEnum.CONNECTED"
        class="overlay"
      >
        <div
          v-if="connectionStore.connectionStatus === ConnectionStatusEnum.CONNECTING"
          class="spinner"
        >
          <LoaderCircle class="spinner-logo" />
        </div>
      </div>
      <h2 v-else-if="connectionStore.connectionStatus !== ConnectionStatusEnum.CONNECTED"
        >Connection Failed</h2
      >
    </div>
  </main>
</template>

<script setup lang="ts">
import { WebRTCPlayer } from '@eyevinn/webrtc-player';
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useConnectionStore } from './stores/connectionStore';
import { ConnectionStatusEnum } from './typings';
import { LoaderCircle } from 'lucide-vue-next';

const videoElement = ref<HTMLVideoElement | null>(null);
let player: WebRTCPlayer | null = null;

const connectionStore = useConnectionStore();

const WHEP_ENDPOINT = 'http://localhost:5014/stream/whep';

const handlePlaying = () => {
  connectionStore.setConnection(ConnectionStatusEnum.CONNECTED);
};

onMounted(async () => {
  if (videoElement.value) {
    // Initialize the player for WHEP signalling
    player = new WebRTCPlayer({
      video: videoElement.value,
      type: 'whep',
    });

    videoElement.value.addEventListener('playing', handlePlaying);

    try {
      connectionStore.setConnection(ConnectionStatusEnum.CONNECTING);
      console.log('Negotiating WebRTC connection...');

      await player.load(new URL(WHEP_ENDPOINT));

      // connectionStore.setConnection(ConnectionStatusEnum.CONNECTED);
      console.log('Stream connected successfully!');
    } catch (error) {
      connectionStore.setConnection(ConnectionStatusEnum.FAILED);
      console.error('Failed to load stream: ', error);
    }
  }
});

onBeforeUnmount(() => {
  videoElement.value?.removeEventListener('playing', handlePlaying);
  if (player && typeof player.destroy === 'function') {
    player.destroy();
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
