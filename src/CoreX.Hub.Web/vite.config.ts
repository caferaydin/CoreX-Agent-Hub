/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * The dev server is bound to loopback deliberately.
 *
 * The Hub API has no authentication because nothing off the machine can reach it, and a dev
 * server that served this UI to the network would undo half of that reasoning - the browser
 * would be someone else's and the API calls would come from their session, not the operator's.
 */
export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5178,
    strictPort: true,

    // Same-origin in development too, so the production CORS posture is never the thing that
    // has to be relaxed to make local work comfortable.
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5177',
        changeOrigin: false,
      },
    },
  },
  preview: {
    host: '127.0.0.1',
    port: 5178,
    strictPort: true,
  },
  test: {
    environment: 'jsdom',

    // The browser acceptance is Playwright's. These two suites answer different questions and
    // the runners are not interchangeable - vitest would load a Playwright spec and fail on an
    // import it has no business resolving.
    exclude: ['node_modules/**', 'dist/**', 'e2e/**'],
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
