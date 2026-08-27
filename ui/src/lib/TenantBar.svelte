<script>
  // The page header. Its job is to make the answer to "whose events am I
  // looking at?" impossible to miss — the query API scopes everything to one
  // tenant, and the old UI showed no sign of that at all.
  import { active, info, selectTenant, tenants } from './store.js';
  import { withBase } from './base.js';
  import { withTenant } from './tenants.js';

  export let subtitle = '';

  $: resolved = $info.data?.app_id ?? null;
  // The mount says which token nginx injects; the server says who that token
  // is. A disagreement means a location block points at the wrong secret —
  // silent cross-tenant confusion otherwise, so name it.
  $: mismatch = resolved && $active?.app_id && resolved !== $active.app_id;

  function onChange(event) {
    selectTenant(tenants[Number(event.currentTarget.value)]);
  }
</script>

<header class="mb-6 flex flex-wrap items-center gap-x-3 gap-y-2">
  <a class="text-lg font-semibold text-gray-900" href={withTenant(withBase('/'), $active)}>
    Event&nbsp;Pump
  </a>

  {#if tenants.length > 1}
    <!-- appearance-none + our own chevron: a native select picks up the OS
         widget, which is a different height and shape on every platform and
         never lines up with the text beside it. -->
    <span class="relative inline-flex items-center">
      <select
        class="appearance-none rounded-md border border-gray-300 bg-white py-1 pl-2.5 pr-8 text-sm font-medium text-gray-900 shadow-sm transition hover:border-gray-400 focus:border-gray-400 focus:outline-none focus:ring-2 focus:ring-gray-900/10"
        value={String(tenants.indexOf($active))}
        on:change={onChange}
      >
        {#each tenants as tenant, index}
          <option value={String(index)}>{tenant.app_id ?? tenant.base ?? 'default'}</option>
        {/each}
      </select>
      <svg
        class="pointer-events-none absolute right-2 h-4 w-4 text-gray-400"
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        stroke-width="1.5"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M4 6l4 4 4-4" />
      </svg>
    </span>
  {:else}
    <span class="rounded-md bg-gray-100 px-2.5 py-1 text-sm font-medium text-gray-900">
      {resolved ?? $active?.app_id ?? '…'}
    </span>
  {/if}

  {#if $info.status === 'ready'}
    <span class="text-sm text-gray-500">
      {subtitle || `last ${$info.data.query_max_days} days`}
    </span>
  {:else if $info.status === 'loading'}
    <span class="text-sm text-gray-400">loading tenant…</span>
  {/if}

  {#if mismatch}
    <span class="rounded bg-amber-100 px-2 py-0.5 text-xs text-amber-800">
      mount “{$active.app_id}” resolves to tenant “{resolved}”
    </span>
  {/if}
</header>

{#if $info.status === 'error'}
  <p class="mb-4 rounded bg-red-50 px-3 py-2 text-sm text-red-700">
    could not identify this tenant — {$info.error}
  </p>
{/if}
