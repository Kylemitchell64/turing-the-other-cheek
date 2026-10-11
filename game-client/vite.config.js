import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    // Installable PWA (ADR 0012). Hashed JS/CSS/icons are precached; the HTML shell is
    // NOT, so a deploy shows up on the next launch instead of hiding behind a stale cache.
    VitePWA({
      registerType: 'autoUpdate',
      injectRegister: 'script-defer',
      manifest: {
        id: '/',
        name: 'Turing the Other Cheek',
        short_name: 'Turing',
        description: "A party game where one player is secretly an AI that learns how your group types.",
        start_url: '/',
        scope: '/',
        display: 'standalone',
        orientation: 'portrait',
        background_color: '#0a0f0a',
        theme_color: '#050805',
        icons: [
          { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          { src: 'maskable-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,svg,png,woff2}'],
        // og.png is only for link previews; AdminPage is operator-only and lazy-loaded
        globIgnores: ['og.png', '**/AdminPage-*.js'],
        navigateFallback: null,
        cleanupOutdatedCaches: true,
        runtimeCaching: [
          {
            // Navigations: network first so a fresh deploy always wins; the cached copy
            // only answers when the phone is offline or the network stalls.
            // Never the API or the SignalR hub: they share the client's origin in the Docker
            // image, and OAuth login is a top-level navigation to /api/auth/*.
            // Self-contained on purpose: Workbox stringifies this into sw.js, so it can't
            // call helpers from this file.
            urlPattern: ({ request, url, sameOrigin }) =>
              request.mode === 'navigate' && sameOrigin &&
              !url.pathname.startsWith('/api') && !url.pathname.startsWith('/hubs'),
            handler: 'NetworkFirst',
            options: { cacheName: 'pages', networkTimeoutSeconds: 4 },
          },
        ],
      },
    }),
  ],
})
