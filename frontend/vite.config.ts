import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// https://vitejs.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiUrl = (env.VITE_API_URL ?? 'http://localhost:5039').replace(/\/+$/, '');

  return {
    plugins: [react()],
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target: apiUrl,
          changeOrigin: true,
          // Keep the proxy alive for long-running / SSE / agent requests.
          timeout: 120000,
          proxyTimeout: 120000,
        },
        '/hubs': {
          target: apiUrl,
          changeOrigin: true,
          ws: true,
          // SignalR / SSE connections must not be torn down prematurely.
          timeout: 120000,
          proxyTimeout: 120000,
        },
      },
    },
  };
});
