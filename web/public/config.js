// Runtime API base override. In local dev this is empty and the app falls back
// to import.meta.env.VITE_API_BASE (web/.env). In the container image this file
// is regenerated at startup from the VITE_API_BASE env var (see web/Dockerfile).
window.__API_BASE__ = ''
