<template>
  <main class="app-container">
    <h1>RTSP to WebRTC Stream</h1>

    <div class="video-wrapper">
      <!-- The native video element -->
      <video ref="videoElement" autoplay playsinline muted controls></video>
    </div>
  </main>
</template>

<script setup lang="ts">
import { WebRTCPlayer } from '@eyevinn/webrtc-player'
import { onBeforeUnmount, onMounted, ref } from 'vue'

const videoElement = ref(null)
let player: WebRTCPlayer | null = null

const WHEP_ENDPOINT = 'http://localhost:5014/stream/whep'

onMounted(async () => {
  if (videoElement.value) {
    // Initialize the player for WHEP signalling
    player = new WebRTCPlayer({
      video: videoElement.value,
      type: 'whep',
    })

    try {
      console.log('Negotiating WebRTC connection...')
      await player.load(new URL(WHEP_ENDPOINT))
      console.log('Stream connected successfully!')
    } catch (error) {
      console.error('Failed to load stream: ', error)
    }
  }
})

onBeforeUnmount(() => {
  if (player && typeof player.destroy === 'function') {
    player.destroy()
  }
})
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
  overflow: hidden;

  width: 100%;
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
</style>
