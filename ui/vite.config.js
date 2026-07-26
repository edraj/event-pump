import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import routify from '@roxi/routify/vite-plugin';
import { defineConfig } from 'vite';

export default defineConfig({
  // Where the bundle will be mounted. '/' (a vhost root) unless EP_UI_BASE says
  // otherwise — set it to serve under a subpath, e.g. EP_UI_BASE=/ep/ui/ when
  // the API owns the domain root. Vite rewrites the asset URLs in index.html
  // and exposes the value as import.meta.env.BASE_URL, which src/lib/base.js
  // turns into the router's URL rewrite.
  base: process.env.EP_UI_BASE || '/',
  plugins: [routify({}), svelte(), tailwindcss()],
  server: {
    proxy: {
      // dev: forward query calls to a locally running `eventpump api`
      '/internal': 'http://127.0.0.1:8081',
    },
  },
  test: {
    include: ['tests/**/*.test.js'],
  },
});
