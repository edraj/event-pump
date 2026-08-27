<script>
  // One session's identity registry row, always read through the active
  // tenant's mount: session_key is a UUID, but the query is app_id-scoped, so
  // the same key under another tenant is a 404 rather than a different answer.
  import { params } from '@roxi/routify';
  import { fetchIdentity, formatTime, handlesFor } from '../../lib/api.js';
  import { withBase } from '../../lib/base.js';
  import { active, info } from '../../lib/store.js';
  import { withTenant } from '../../lib/tenants.js';
  import TenantBar from '../../lib/TenantBar.svelte';

  let identity = null;
  let error = '';

  $: sessionKey = $params.key;
  $: tenant = $active;
  $: if (sessionKey && tenant) load(sessionKey, tenant);

  function load(key, forTenant) {
    identity = null;
    error = '';
    fetchIdentity(forTenant, key)
      .then((row) => {
        if (forTenant === $active) identity = row;
      })
      .catch((problem) => {
        if (forTenant !== $active) return;
        error = String(problem.message ?? problem);
      });
  }

  // Only the handles for destinations this tenant runs. The old page showed all
  // eight regardless, so an unused destination's blank rows looked the same as
  // a handle that simply had not been captured yet.
  $: handleGroups = handlesFor($info.data?.destinations);
  // The allowlist from the tenant's plan (SPEC §6.1) rather than a hard-coded
  // email/phone pair — every tenant declares its own attributes.
  $: attributeDefs = $info.data?.attributes ?? [];
</script>

<div class="mx-auto max-w-4xl p-6">
  <TenantBar subtitle="session detail" />

  <!-- withBase so the link survives a subpath mount, withTenant so it comes
       back to the tenant this session belongs to. -->
  <a class="text-sm text-blue-600 hover:underline" href={withTenant(withBase('/'), tenant)}>← events</a>
  <h1 class="mb-6 mt-2 text-xl font-semibold text-gray-900">
    session <span class="font-mono text-base">{sessionKey}</span>
  </h1>

  {#if error}
    <p class="rounded bg-red-50 px-3 py-2 text-sm text-red-700">
      {error}
      {#if $info.data}
        <span class="block text-xs text-red-600">
          looked up under tenant “{$info.data.app_id}” — a session belongs to exactly one tenant.
        </span>
      {/if}
    </p>
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
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">user attributes</h2>
        {#if attributeDefs.length === 0}
          <p class="text-sm text-gray-400">this tenant's plan declares no attributes</p>
        {:else}
          <dl class="space-y-1 text-sm">
            {#each attributeDefs as attribute}
              <div class="flex justify-between gap-4">
                <dt class="text-gray-500">
                  {attribute.name}
                  <span class="text-xs text-gray-400">({attribute.type})</span>
                </dt>
                <dd class="truncate text-gray-900" title={identity.attributes?.[attribute.name] ?? ''}>
                  {identity.attributes?.[attribute.name] ?? '—'}
                </dd>
              </div>
            {/each}
          </dl>
        {/if}
      </section>

      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">destination handles</h2>
        {#if handleGroups.length === 0}
          <p class="text-sm text-gray-400">
            no handle-carrying destination is enabled for this tenant
          </p>
        {:else}
          {#each handleGroups as group}
            <h3 class="mt-3 mb-1 text-xs font-medium text-gray-400 first:mt-0">{group.destination}</h3>
            <dl class="space-y-1 text-sm">
              {#each group.fields as name}
                <div class="flex justify-between gap-4">
                  <dt class="text-gray-500">{name}</dt>
                  <dd class="truncate font-mono text-gray-900" title={identity[name] ?? ''}>
                    {identity[name] ?? '—'}
                  </dd>
                </div>
              {/each}
            </dl>
          {/each}
        {/if}
      </section>

      <section class="rounded-lg border border-gray-200 p-4">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">click ids</h2>
        <pre class="overflow-x-auto rounded bg-gray-50 p-2 text-xs text-gray-800">{JSON.stringify(identity.click_ids, null, 2)}</pre>
      </section>

      <section class="rounded-lg border border-gray-200 p-4 md:col-span-2">
        <h2 class="mb-3 text-xs font-semibold uppercase text-gray-500">session context</h2>
        <pre class="overflow-x-auto rounded bg-gray-50 p-2 text-xs text-gray-800">{JSON.stringify(identity.context, null, 2)}</pre>
      </section>
    </div>
  {/if}
</div>
