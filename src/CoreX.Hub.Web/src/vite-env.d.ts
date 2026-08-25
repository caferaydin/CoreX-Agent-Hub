/// <reference types="vite/client" />

/**
 * The one build-time setting this UI reads.
 *
 * Absent in normal use: production is same-origin and the dev server proxies `/api`, so the
 * base URL is empty in both. It exists for the case where someone deliberately runs the two on
 * different ports. It is a build variable, never a runtime discovery - nothing here looks at
 * network interfaces or guesses a host.
 */
interface ImportMetaEnv {
  readonly VITE_HUB_API?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
