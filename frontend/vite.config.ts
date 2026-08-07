import { fileURLToPath, URL } from 'node:url';

import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import vueDevTools from 'vite-plugin-vue-devtools';

// https://vite.dev/config/
export default defineConfig({
  server: {
    host: '0.0.0.0',
    port: 5173,
    allowedHosts: ['.ngrok-free.dev'],
    proxy: {
      // Lets the browser call a same-origin relative URL for webrtc-streamer's
      // API instead of an absolute http://localhost:8000 — the latter breaks
      // for anyone loading the page through ngrok or from another device,
      // since "localhost" would resolve to their machine, not this one.
      '/api': {
        target: 'http://localhost:8000',
        changeOrigin: true,
      },
      // Same reasoning as /api above, for the C# WHEP backend: proxying
      // same-origin also avoids the browser blocking the request as mixed
      // content when the page itself is loaded over ngrok's https://.
      '/Stream': {
        target: 'http://localhost:5014',
        changeOrigin: true,
      },
    },
  },
  plugins: [vue(), vueDevTools()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
});
