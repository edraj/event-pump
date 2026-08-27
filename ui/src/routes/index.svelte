<script>
  import {
    DELIVERY_STATUSES,
    fetchEvents,
    formatTime,
    shortId,
    statusClass,
    windowFloor,
  } from '../lib/api.js';
  import { withBase } from '../lib/base.js';
  import { active, info } from '../lib/store.js';
  import { withTenant } from '../lib/tenants.js';
  import TenantBar from '../lib/TenantBar.svelte';

  const blank = {
    event_name: '',
    origin: '',
    user_id: '',
    anonymous_id: '',
    session_key: '',
    destination: '',
    status: '',
    from: '',
    to: '',
  };

  let filters = { ...blank };
  let events = [];
  let nextCursor = null;
  let loading = false;
  let error = '';
  let expanded = {};

  // The tenant's own plan drives the pickers. A free-text box here was the
  // pre-tenant shape: event names and destinations are per tenant, so anything
  // typed that this tenant does not have returns an empty page with no hint why.
  $: planEvents = $info.data?.events ?? [];
  $: destinations = $info.data?.destinations ?? [];
  // The API clamps `from` to EP_QUERY_MAX_DAYS ago whatever the form asks for.
  $: earliest = $info.data ? windowFloor($info.data.query_max_days) : '';

  // The rows on screen belong to the tenant they were fetched for. They are
  // dropped when the selection changes, not when the replacement lands: the
  // block below waits for /query/tenant before it even asks for events, so
  // leaving them up means the header names one tenant while the table shows
  // another's rows for two round trips — the exact confusion this scoping is
  // here to remove.
  let shownFor = null;
  $: if ($active !== shownFor) {
    shownFor = $active;
    events = [];
    nextCursor = null;
    expanded = {};
  }

  // Switching tenant re-scopes everything: a session key or user id from one
  // tenant means nothing in another, so carry only the shape of the query, not
  // its identifiers.
  let loadedFor = null;
  $: if ($active && $info.status === 'ready' && loadedFor !== $active) {
    const first = loadedFor === null;
    loadedFor = $active;
    if (!first) filters = { ...blank, origin: filters.origin, status: filters.status };
    load(true);
  }

  async function load(reset = true) {
    if (!$active) return;
    loading = true;
    error = '';
    const tenant = $active;
    try {
      const page = await fetchEvents(tenant, filters, {
        cursor: reset ? null : nextCursor,
        limit: 50,
      });
      // Dropped if the user switched tenant mid-request — otherwise one
      // tenant's rows would land in the other's table.
      if (tenant !== $active) return;
      events = reset ? page.events : [...events, ...page.events];
      nextCursor = page.next_cursor ?? null;
      if (reset) expanded = {};
    } catch (problem) {
      if (tenant === $active) error = String(problem.message ?? problem);
    } finally {
      // The abandoned request still runs its finally. Clearing `loading` there
      // would re-enable the form and drop the "loading…" label while the
      // replacement tenant's request is still outstanding.
      if (tenant === $active) loading = false;
    }
  }

  function reset() {
    filters = { ...blank };
    load(true);
  }

  function toggle(key) {
    expanded = { ...expanded, [key]: !expanded[key] };
  }

  function pretty(value) {
    return JSON.stringify(value, null, 2);
  }

  // Click a value in a row -> put it in its filter and search.
  function filterBy(key, value) {
    if (!value) return;
    filters[key] = value;
    load(true);
  }

  // Click a delivery chip -> filter by that destination + status together.
  function filterDelivery(destination, status) {
    filters.destination = destination;
    filters.status = status;
    load(true);
  }

  $: activeFilters = Object.entries(filters).filter(([, value]) => value).length;
  // Attribute gates are per destination (SPEC §6.1): with the gate off, this
  // tenant's attribute-derived fields never reach that destination, which is
  // the usual reason an operator finds an empty email column.
  $: gatedOff = destinations.filter((d) => !d.attributes_enabled).map((d) => d.code);
</script>

<div class="mx-auto max-w-7xl p-6">
  <TenantBar />

  <form
    class="mb-4 grid grid-cols-2 gap-3 rounded-lg border border-gray-200 bg-gray-50 p-4 md:grid-cols-5"
    on:submit|preventDefault={() => load(true)}
  >
    <label class="text-xs text-gray-600">
      event name
      <select class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.event_name}>
        <option value="">any</option>
        {#each planEvents as name}
          <option value={name}>{name}</option>
        {/each}
      </select>
    </label>
    <label class="text-xs text-gray-600">
      origin
      <select class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.origin}>
        <option value="">any</option>
        <option value="client">client</option>
        <option value="server">server</option>
      </select>
    </label>
    <label class="text-xs text-gray-600">
      destination
      <select class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.destination}>
        <option value="">any</option>
        {#each destinations as destination}
          <option value={destination.code}>{destination.code}</option>
        {/each}
      </select>
    </label>
    <label class="text-xs text-gray-600">
      delivery status
      <select class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.status}>
        <option value="">any</option>
        {#each DELIVERY_STATUSES as status}
          <option value={status}>{status}</option>
        {/each}
      </select>
    </label>
    <label class="text-xs text-gray-600">
      user id
      <input class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.user_id} />
    </label>
    <label class="text-xs text-gray-600">
      anonymous id
      <input class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.anonymous_id} />
    </label>
    <label class="text-xs text-gray-600">
      session key
      <input class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.session_key} />
    </label>
    <label class="text-xs text-gray-600">
      from (local)
      <input
        type="datetime-local"
        min={earliest}
        class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm"
        bind:value={filters.from}
      />
    </label>
    <label class="text-xs text-gray-600">
      to (local)
      <input type="datetime-local" class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.to} />
    </label>
    <div class="flex items-end gap-2">
      <button
        class="flex-1 rounded bg-gray-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-gray-700 disabled:opacity-50"
        disabled={loading || $info.status !== 'ready'}
        type="submit"
      >
        {loading ? 'loading…' : 'search'}
      </button>
      {#if activeFilters}
        <button
          class="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-600 hover:bg-white"
          type="button"
          on:click={reset}
        >
          clear
        </button>
      {/if}
    </div>
  </form>

  {#if gatedOff.length}
    <p class="mb-4 text-xs text-gray-500">
      attributes gated off for {gatedOff.join(', ')} — email and phone are stored
      for this tenant but not forwarded to those destinations.
    </p>
  {/if}

  {#if error}
    <p class="mb-4 rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}

  <div class="overflow-x-auto rounded-lg border border-gray-200">
    <table class="w-full text-left text-sm">
      <thead class="bg-gray-100 text-xs uppercase text-gray-500">
        <tr>
          <th class="px-3 py-2">received</th>
          <th class="px-3 py-2">event</th>
          <th class="px-3 py-2">origin</th>
          <th class="px-3 py-2">user</th>
          <th class="px-3 py-2">anonymous</th>
          <th class="px-3 py-2">session</th>
          <th class="px-3 py-2">email</th>
          <th class="px-3 py-2">phone</th>
          <th class="px-3 py-2">deliveries</th>
          <th class="px-3 py-2"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-gray-100">
        {#each events as event (event.event_id)}
          <tr class="hover:bg-gray-50">
            <td class="whitespace-nowrap px-3 py-2 text-gray-600">{formatTime(event.received_at)}</td>
            <td class="px-3 py-2">
              <button class="font-mono text-blue-600 hover:underline" on:click={() => filterBy('event_name', event.event_name)}>{event.event_name}</button>
            </td>
            <td class="px-3 py-2">
              <button class="text-blue-600 hover:underline" on:click={() => filterBy('origin', event.origin)}>{event.origin}</button>
            </td>
            <td class="px-3 py-2 text-gray-600">
              {#if event.user_id}
                <button class="text-blue-600 hover:underline" on:click={() => filterBy('user_id', event.user_id)}>{event.user_id}</button>
              {/if}
            </td>
            <td class="px-3 py-2 font-mono text-gray-500" title={event.anonymous_id}>
              {#if event.anonymous_id}
                <button class="text-blue-600 hover:underline" on:click={() => filterBy('anonymous_id', event.anonymous_id)}>{shortId(event.anonymous_id)}</button>
              {/if}
            </td>
            <td class="px-3 py-2 font-mono text-gray-500" title={event.session_key}>
              {#if event.session_key}
                <button class="text-blue-600 hover:underline" on:click={() => filterBy('session_key', event.session_key)}>{shortId(event.session_key)}</button>
              {/if}
            </td>
            <td class="px-3 py-2 text-gray-600">{event.email ?? ''}</td>
            <td class="px-3 py-2 text-gray-600">{event.phone ?? ''}</td>
            <td class="px-3 py-2">
              {#each event.deliveries as delivery}
                <button
                  type="button"
                  class={`mr-1 inline-block cursor-pointer rounded-full px-2 py-0.5 text-xs ${statusClass(delivery.status)}`}
                  title={delivery.last_error ?? delivery.status}
                  on:click={() => filterDelivery(delivery.destination, delivery.status)}
                >
                  {delivery.destination}:{delivery.status}
                </button>
              {:else}
                <span class="text-xs text-gray-400">internal only</span>
              {/each}
            </td>
            <td class="px-3 py-2 text-right">
              <button class="text-xs text-blue-600 hover:underline" on:click={() => toggle(event.event_id)}>
                {expanded[event.event_id] ? 'hide' : 'details'}
              </button>
            </td>
          </tr>
          {#if expanded[event.event_id]}
            <tr class="bg-gray-50">
              <td colspan="10" class="px-4 py-3">
                <div class="grid gap-4 md:grid-cols-2">
                  <div>
                    <h3 class="mb-1 text-xs font-semibold uppercase text-gray-500">properties</h3>
                    <pre class="overflow-x-auto rounded bg-white p-2 text-xs text-gray-800">{pretty(event.properties)}</pre>
                  </div>
                  <div>
                    <h3 class="mb-1 text-xs font-semibold uppercase text-gray-500">context</h3>
                    <pre class="overflow-x-auto rounded bg-white p-2 text-xs text-gray-800">{pretty(event.context)}</pre>
                  </div>
                </div>
                <div class="mt-2 flex gap-4 text-xs text-gray-500">
                  <span>event_id: <span class="font-mono">{event.event_id}</span></span>
                  <span>occurred: {formatTime(event.occurred_at)}</span>
                  {#if event.session_key}
                    <!-- withBase, not a bare '/session/…': Routify intercepts
                         the click, but the href still has to be correct for
                         middle-click, copy-link, and reload. withTenant keeps a
                         copied link pointing at the tenant it came from — the
                         session key is meaningless under any other. -->
                    <a
                      class="text-blue-600 hover:underline"
                      href={withTenant(withBase(`/session/${event.session_key}`), $active)}
                    >
                      session {shortId(event.session_key)} →
                    </a>
                  {/if}
                </div>
              </td>
            </tr>
          {/if}
        {:else}
          <tr>
            <td colspan="10" class="px-3 py-8 text-center text-gray-400">
              {#if $info.status === 'ready'}
                no events for {$info.data.app_id} in this window
              {:else}
                …
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  {#if nextCursor}
    <div class="mt-4 text-center">
      <button
        class="rounded border border-gray-300 px-4 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        disabled={loading}
        on:click={() => load(false)}
      >
        load more
      </button>
    </div>
  {/if}
</div>
