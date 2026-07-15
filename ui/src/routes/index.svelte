<script>
  import { fetchEvents, formatTime, shortId, statusClass } from '../lib/api.js';

  const filters = {
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

  let events = [];
  let nextCursor = null;
  let loading = false;
  let error = '';
  let expanded = {};

  async function load(reset = true) {
    loading = true;
    error = '';
    try {
      const cursor = reset ? null : nextCursor;
      const page = await fetchEvents(filters, { cursor, limit: 50 });
      events = reset ? page.events : [...events, ...page.events];
      nextCursor = page.next_cursor ?? null;
      if (reset) expanded = {};
    } catch (problem) {
      error = String(problem);
    } finally {
      loading = false;
    }
  }

  function toggle(key) {
    expanded = { ...expanded, [key]: !expanded[key] };
  }

  function rowKey(event) {
    return event.event_id;
  }

  function pretty(value) {
    return JSON.stringify(value, null, 2);
  }

  load();
</script>

<div class="mx-auto max-w-7xl p-6">
  <header class="mb-6 flex items-baseline gap-3">
    <h1 class="text-2xl font-semibold text-gray-900">Event Pump</h1>
    <span class="text-sm text-gray-500">events explorer — last few days</span>
  </header>

  <form
    class="mb-4 grid grid-cols-2 gap-3 rounded-lg border border-gray-200 bg-gray-50 p-4 md:grid-cols-5"
    on:submit|preventDefault={() => load(true)}
  >
    <label class="text-xs text-gray-600">
      event name
      <input class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.event_name} placeholder="product_viewed" />
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
      destination
      <input class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.destination} placeholder="ga4" />
    </label>
    <label class="text-xs text-gray-600">
      delivery status
      <select class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.status}>
        <option value="">any</option>
        <option value="pending">pending</option>
        <option value="delivered">delivered</option>
        <option value="failed">failed</option>
        <option value="dead">dead</option>
        <option value="skipped">skipped</option>
      </select>
    </label>
    <label class="text-xs text-gray-600">
      from (UTC)
      <input type="datetime-local" class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.from} />
    </label>
    <label class="text-xs text-gray-600">
      to (UTC)
      <input type="datetime-local" class="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm" bind:value={filters.to} />
    </label>
    <div class="flex items-end">
      <button
        class="w-full rounded bg-gray-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-gray-700 disabled:opacity-50"
        disabled={loading}
        type="submit"
      >
        {loading ? 'loading…' : 'search'}
      </button>
    </div>
  </form>

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
          <th class="px-3 py-2">msisdn</th>
          <th class="px-3 py-2">deliveries</th>
          <th class="px-3 py-2"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-gray-100">
        {#each events as event (rowKey(event))}
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
            <td class="px-3 py-2 text-gray-600">{event.msisdn ?? ''}</td>
            <td class="px-3 py-2">
              {#each event.deliveries as delivery}
                <span
                  class={`mr-1 inline-block rounded-full px-2 py-0.5 text-xs ${statusClass(delivery.status)}`}
                  title={delivery.last_error ?? delivery.status}
                >
                  {delivery.destination}:{delivery.status}
                </span>
              {:else}
                <span class="text-xs text-gray-400">internal only</span>
              {/each}
            </td>
            <td class="px-3 py-2 text-right">
              <button class="text-xs text-blue-600 hover:underline" on:click={() => toggle(rowKey(event))}>
                {expanded[rowKey(event)] ? 'hide' : 'details'}
              </button>
            </td>
          </tr>
          {#if expanded[rowKey(event)]}
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
                    <a class="text-blue-600 hover:underline" href={`/session/${event.session_key}`}>
                      session {shortId(event.session_key)} →
                    </a>
                  {/if}
                </div>
              </td>
            </tr>
          {/if}
        {:else}
          <tr><td colspan="10" class="px-3 py-8 text-center text-gray-400">no events in the window</td></tr>
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
