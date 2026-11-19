import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` proxies to a running GameDeck on this PC; `npm run build` emits into the app's wwwroot.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/GameDeck.App/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5280',
      '/ws': { target: 'ws://localhost:5280', ws: true },
    },
  },
});
