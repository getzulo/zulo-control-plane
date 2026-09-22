import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      // TWO entry points from one build: the operator dashboard at / and the
      // customer portal at /portal.
      //
      // Separate bundles rather than a route inside the dashboard, because the
      // dashboard's code IS the fleet — every screen naming tenant slugs, node
      // names, image tags, snapshots. Shipping that to a customer's browser and
      // relying on the router never to render it is not a boundary; not shipping
      // it is.
      //
      // Naming index.html explicitly is load-bearing. The moment `input` is set
      // Vite stops applying its default, so a config listing only portal.html
      // produces an image whose dashboard is a 404 — which the Dockerfile's
      // `test -s dist/index.html` exists to catch.
      input: {
        index: resolve(import.meta.dirname, 'index.html'),
        portal: resolve(import.meta.dirname, 'portal.html'),
      },
    },
  },
})
