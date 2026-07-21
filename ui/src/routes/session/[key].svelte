<script>
  import { params } from '@roxi/routify';
  import { fetchIdentity, formatTime } from '../../lib/api.js';

  const handleNames = [
    'ga4_client_id',
    'ga4_session_id',
    'firebase_app_instance_id',
    'amplitude_device_id',
    'adjust_adid',
    'adjust_platform_ad_id',
    'fbp',
    'fbc',
  ];

  let identity = null;
  let error = '';

  $: sessionKey = $params.key;
  $: if (sessionKey) {
    identity = null;
    error = '';
    fetchIdentity(sessionKey)
      .then((row) => (identity = row))
      .catch((problem) => (error = String(problem)));
  }
</script>

<div class="mx-auto max-w-4xl p-6">
  <header class="mb-6">
    <a class="text-sm text-blue-600 hover:underline" href="/">← events</a>
    <h1 class="mt-2 text-xl font-semibold text-gray-900">
      session <span class="font-mono text-base">{sessionKey}</span>
    </h1>
  </header>

  {#if error}
    <p class="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {:else if !identity}
    <p class="text-sm text-gray-400">loading…</p>
  {:else}
    <div class="grid gap-6 md:grid-cols-2">
      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">identity</h2>
        <dl class="space-y-1 text-sm">
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">anonymous_id</dt>
            <dd class="font-mono text-gray-900">{identity.anonymous_id}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">user_id</dt>
            <dd class="text-gray-900">{identity.user_id ?? '—'}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">session #</dt>
            <dd class="text-gray-900">{identity.session_number ?? '—'}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">client ip</dt>
            <dd class="font-mono text-gray-900">{identity.client_ip ?? '—'}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">first seen</dt>
            <dd class="text-gray-900">{formatTime(identity.created_at)}</dd>
          </div>
          <div class="flex justify-between gap-4">
            <dt class="text-gray-500">updated</dt>
            <dd class="text-gray-900">{formatTime(identity.updated_at)}</dd>
          </div>
        </dl>
      </section>

      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">destination handles</h2>
        <dl class="space-y-1 text-sm">
          {#each handleNames as name}
            <div class="flex justify-between gap-4">
              <dt class="text-gray-500">{name}</dt>
              <dd class="truncate font-mono text-gray-900" title={identity[name] ?? ''}>
                {identity[name] ?? '—'}
              </dd>
            </div>
          {/each}
        </dl>
      </section>

      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">click ids</h2>
        <pre class="overflow-x-auto rounded bg-gray-50 p-2 text-xs text-gray-800">{JSON.stringify(identity.click_ids, null, 2)}</pre>
      </section>

      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">session context</h2>
        <pre class="overflow-x-auto rounded bg-gray-50 p-2 text-xs text-gray-800">{JSON.stringify(identity.context, null, 2)}</pre>
      </section>
    </div>
  {/if}
</div>
