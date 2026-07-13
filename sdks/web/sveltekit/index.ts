import type { EventPump } from '../src/client';

type AfterNavigate = (callback: () => void) => void;

/**
 * SvelteKit helper: wire page() to afterNavigate without this package
 * depending on SvelteKit. In +layout.svelte:
 *
 *   import { afterNavigate } from '$app/navigation';
 *   import { ep } from 'event-pump-web';
 *   import { trackPages } from 'event-pump-web/sveltekit';
 *   ep.init({ endpoint, appToken });
 *   trackPages(ep, afterNavigate);
 */
export function trackPages(ep: EventPump, afterNavigate: AfterNavigate): void {
  afterNavigate(() => ep.page());
}
