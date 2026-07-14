import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import routify from '@roxi/routify/vite-plugin';
import { defineConfig } from 'vite';

export default defineConfig({
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
