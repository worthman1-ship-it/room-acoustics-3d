import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The C# engine (src/RoomAcoustics.Api) listens on 5080; the browser talks to it through this proxy, so there is no CORS to think about in development.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://localhost:5080', changeOrigin: true } },
  },
});
