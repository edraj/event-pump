# Event Pump as a systemd **user** service (EL9)

One unit, one process (`eventpump standalone` = API + delivery worker), no root,
everything under `~/eventpump`. For the production two-service split running as a
system-wide service account, use `../systemd/` + the RPM instead.

Layout this sets up on the server:

```
~/eventpump/
├── eventpump                   # Native AOT binary, no .NET runtime needed
├── migrations/*.sql            # auto-found: sits next to the binary
├── sql/producer_contract.sql   # ditto
├── eventpump.env               # config (systemd EnvironmentFile)
└── tracking-plan.json
```

Because `migrations/` and `sql/` sit next to the binary, `eventpump migrate`
finds them with no `EP_MIGRATIONS_DIR` / `EP_PRODUCER_CONTRACT` set.

---

## 1. Build the binary (on a build box with `dotnet-sdk-10.0`, `clang`, `zlib-devel`)

```bash
dotnet publish server/src/EventPump -c Release -r linux-x64 -o publish
```

ILCompiler links against glibc 2.34 (= RHEL 9), so a binary built on Fedora runs
on EL9+. Nothing needs to be installed on the target server.

## 2. Ship it to the server

From the repo root on the build box:

```bash
SRV=you@el9-host
ssh $SRV 'mkdir -p ~/eventpump/migrations ~/eventpump/sql'
scp publish/eventpump                  $SRV:~/eventpump/eventpump
scp server/migrations/*.sql            $SRV:~/eventpump/migrations/
scp server/sql/producer_contract.sql   $SRV:~/eventpump/sql/
scp deploy/.env.example                $SRV:~/eventpump/eventpump.env
scp deploy/tracking-plan.example.json  $SRV:~/eventpump/tracking-plan.json
ssh $SRV 'chmod 700 ~/eventpump/eventpump && chmod 600 ~/eventpump/eventpump.env'
```

## 3. Postgres

The outbox lives in the platform's business database. Either point
`EP_DB_CONNSTRING` at your existing DB, or on this host:

```bash
sudo dnf install -y postgresql-server
sudo postgresql-setup --initdb
sudo systemctl enable --now postgresql
sudo -u postgres psql -c "CREATE USER eventpump PASSWORD 'CHANGE_ME';"
sudo -u postgres psql -c "CREATE DATABASE platform OWNER eventpump;"
```

`initdb` defaults to `ident`/`peer` auth — for a password login over
`127.0.0.1` set `host all all 127.0.0.1/32 scram-sha-256` in
`/var/lib/pgsql/data/pg_hba.conf`, then `sudo systemctl reload postgresql`.

## 4. Config

```bash
vi ~/eventpump/eventpump.env
```

**This file is read by systemd, not by a shell.** Unlike the repo's `local.env`,
it must have no `export` prefixes, no `$PWD` or other interpolation, and no
inline `#` comments after a value. `deploy/.env.example` is already in the right
format — copy it, don't adapt `local.env`.

Minimum to change:

```ini
EP_DB_CONNSTRING=Host=127.0.0.1;Username=eventpump;Password=CHANGE_ME;Database=platform
EP_TRACKING_PLAN=/home/YOURUSER/eventpump/tracking-plan.json
EP_CLIENT_TOKENS=webapp:CHANGE_ME_WEB
EP_INTERNAL_TOKEN=CHANGE_ME_INTERNAL
EP_COOKIE_DOMAIN=.example.com
EP_CORS_ORIGINS=https://www.example.com
```

`EP_TRACKING_PLAN` needs an **absolute** path (systemd does not expand `~`), and
is required — the process refuses to start without it. The default listeners
(`8080`/`8081`/`9090`) are all above 1024, so an unprivileged user can bind them.

## 5. Install and start the unit

```bash
mkdir -p ~/.config/systemd/user
cp deploy/systemd-user/eventpump.service ~/.config/systemd/user/

# Without this the user manager is torn down at logout and the service dies.
sudo loginctl enable-linger $USER

systemctl --user daemon-reload
systemctl --user enable --now eventpump
```

Check it:

```bash
systemctl --user status eventpump
journalctl --user -u eventpump -f
curl -fsS http://127.0.0.1:9090/healthz
```

## 6. Redeploy

```bash
scp publish/eventpump $SRV:~/eventpump/eventpump
ssh $SRV 'systemctl --user restart eventpump'
```

`ExecStartPre` re-runs `migrate` on every start; it is re-runnable and a no-op
when the schema is current.

---

## EL9 specifics worth knowing

**`systemctl --user` over SSH.** It works in a normal interactive SSH session.
It does *not* work under `sudo -u otheruser` without a session bus — use
`sudo -u otheruser XDG_RUNTIME_DIR=/run/user/$(id -u otheruser) systemctl --user ...`,
or just SSH in as that user.

**Linger is the whole trick.** Without `enable-linger`, the user manager starts
at first login and stops at last logout, taking the service with it — so the
service silently isn't running after a reboot until someone logs in.

**SELinux.** EL9 ships enforcing, but ordinary users run unconfined, so a binary
executed from `$HOME` by the user manager is fine as-is. Two things do need a
boolean: if nginx reverse-proxies to the API,
`sudo setsebool -P httpd_can_network_connect 1`; if Postgres is on this host and
you left it on the default port, nothing extra.

**Firewall.** The listeners bind `127.0.0.1` only, so no `firewall-cmd` change is
needed. Put nginx in front for anything public — per SPEC §9.5 the API must be
served from a subdomain of the site's registrable domain (e.g.
`collect.example.com`) with a matching `EP_COOKIE_DOMAIN`, and nginx must pass
`X-Real-IP`. See `../nginx-ui.conf.example`.

**Logs** go to the journal as JSON lines (`AddJsonConsole`). `journalctl --user -u
eventpump` for this user only; add `--since -1h` / `-o cat` to read the raw JSON.

---

## If you'd rather run the API and worker as two units

`standalone` trades process isolation for one fewer unit — fine for a single VM,
but ingestion failures lose data permanently while delivery failures don't, which
is why production keeps them apart. To split, copy the unit twice and swap the
`ExecStart` verb:

```bash
sed 's/standalone/api/; /ExecStartPre/d'    eventpump.service > ~/.config/systemd/user/eventpump-api.service
sed 's/standalone/worker/; /ExecStartPre/d' eventpump.service > ~/.config/systemd/user/eventpump-worker.service
```

Then run `eventpump migrate` by hand at deploy time (dropped from both units so
two processes don't race on the schema), and
`systemctl --user enable --now eventpump-api eventpump-worker`.
