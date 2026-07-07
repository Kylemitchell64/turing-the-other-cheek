// Where the API lives. Three cases:
//   - VITE_API_URL set (Vercel prod, or a custom dev target)  -> use it
//   - not set + prod build (the Docker image serves this from wwwroot) -> same origin ("")
//   - not set + dev (vite on 5173, api on 5222)               -> the local api
// Same-origin means the Docker image alone is fully playable without any env var.
export const API_BASE =
  import.meta.env.VITE_API_URL ?? (import.meta.env.DEV ? "http://localhost:5222" : "");
