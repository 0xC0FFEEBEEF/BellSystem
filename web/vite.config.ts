import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Production: `npm run build` -> web/dist, served by the SchoolBell server (same origin as the
// API and WebSockets). Development: `npm run dev` proxies /api and /ws to the local server.
export default defineConfig({
  plugins: [react()],
  base: "/",
  build: { outDir: "dist", emptyOutDir: true, sourcemap: false, target: "es2022" },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5001",
      "/ws": { target: "ws://127.0.0.1:5001", ws: true },
    },
  },
});
