# Operations

- [The local stack](#the-local-stack)
- [The database](#the-database)
- [Deploying](#deploying)
- [Health and the worker](#health-and-the-worker)
- [Keys](#keys)
- [Payments](#payments)
- [Push notifications](#push-notifications)
- [Mail](#mail)
- [Badges](#badges)
- [Not yet in place](#not-yet-in-place)

## The local stack

Everything runs in containers with one command:

```bash
docker compose -f deploy/compose.yml up --build
```

| Service | What it is | Reached at |
|---|---|---|
| `postgres` | PostGIS 18-3.6, schema applied on first boot by `deploy/postgres/init-brighttime.sh` | `127.0.0.1:5432` |
| `seed` | Loads Kettle County, then exits ([seed.md](../seed.md)) | |
| `web`, `api`, `jobs` | The three hosts, in Development | through nginx |
| `nginx` | Routes `/api/` to the API and the rest to Web, with the security headers | `http://kettle.localhost:8000` |
| `mailpit` | Catches all mail | `http://localhost:8025` |
| `nominatim` | Self-hosted geocoding, behind the `geocoding` profile and not started by default (Q7) | `127.0.0.1:8080` |

- Browse `http://kettle.localhost:8000`. Plain `localhost` is a 404, because the tenant comes from
  the host name.
- Sign in to the consoles at `/dev/sign-in` as any seeded account.
- **The schema is applied on first boot only.** After any change under `db/schema/`, or a change
  to role permissions, rebuild the volume:

  ```bash
  docker compose -f deploy/compose.yml down -v
  ```

Running the hosts outside containers, and the test databases, are covered in
[development.md](development.md) and [HANDOFF.md](../HANDOFF.md#setting-up-a-machine).

## The database

**Building one.** `db/apply-schema.sh <database> [host] [port] [user]` drops and recreates the
database, then applies `db/schema/*.sql` in order. It refuses any name not starting with
`brighttime`. Roles are cluster-wide and created only if absent, so give each environment its own
cluster.

**Logins.** The DDL creates only `NOLOGIN` group roles. Create a login per host and grant it the
group:

```sql
create role bt_web_login  login password '…';
create role bt_auth_login login password '…';
create role bt_jobs_login login password '…';
grant brighttime_app, brighttime_finder to bt_web_login;
grant brighttime_auth to bt_auth_login;
grant brighttime_jobs to bt_jobs_login;
```

**Seeding.** `dotnet run --project src/BrightTime.Seed -- --connection "…"`, as a superuser or a
member of `brighttime_migrator`. It is safe to re-run ([seed.md](../seed.md)). A real tenant
needs its own tenant, domain, neighbourhood and taxonomy rows; there is no tenant-provisioning
screen yet.

**Upgrading a database that holds data** has no tool yet. `apply-schema.sh` rebuilds from empty,
and EF migrations aren't used (Q12). Before production data exists, schema changes need a forward
migration path: hand-written, reviewed SQL applied as `brighttime_migrator`, with
`SchemaDriftTests` proving the result.

**Settings the DDL expects:** collation `C.UTF-8` (citext comparison and index order depend on it),
and the extensions in `00-extensions-roles.sql`. Compose also sets `shared_buffers=512MB` and
`work_mem=16MB` for the finder's GIN and GiST indexes, and logs statements slower than 200 ms.

## Deploying

There is no production deployment yet. What one needs, from what is built:

- **Images:** `deploy/web.Dockerfile`, `api.Dockerfile`, `jobs.Dockerfile` and
  `seed.Dockerfile`, built from the repository root.
- **A reverse proxy** in front of Web and Api, like `deploy/nginx/dev.conf`. It must:
  - route `/api/` (and `/hubs/`) to Api and everything else to Web;
  - pass `Host`, `X-Forwarded-For` and `X-Forwarded-Proto`;
  - add the security headers in that file (a strict CSP with no third-party origins,
    `nosniff`, `no-referrer`, `DENY` framing);
  - keep query strings out of the access log;
  - terminate TLS, with HSTS.

  List the proxy in `Network:TrustedProxies`.
- **One host name per tenant**, each with a row in `platform.tenant_domain`. The primary domain is
  the tenant's address in every link.
- **One or more workers.** Jobs are claimed with `SKIP LOCKED`, so workers can run side by side.
- **Configuration** per [configuration.md](configuration.md#a-production-checklist).

## Health and the worker

Web and Api each serve:
- `/health/live`: the process is up;
- `/health/ready`: the database answers, plus the queue's lag from `platform.queue_health()`.
  It is degraded when the oldest undispatched outbox message is over a minute old, or the oldest
  due job over five.

Health probes skip the tenant lookup and the HTTPS redirect, so a load balancer can call them by IP
address.

**Looking at the queue** (as a superuser or `brighttime_migrator`):

```sql
-- what is waiting, running or dead, by type
select job_type, state, count(*), min(run_after) from platform.job group by 1, 2 order by 1, 2;

-- why the dead ones died
select id, tenant_id, job_type, attempts, last_error, created_at
from platform.job where state = 'dead' order by created_at desc limit 50;

-- outbox messages not yet dispatched
select message_type, count(*), min(occurred_at) from platform.outbox_message
where processed_at is null group by 1;
```

A job retries with backoff up to its `max_attempts` (10 by default), then is `dead`. To try a dead
job again once its cause is fixed:

```sql
update platform.job
set state = 'ready', attempts = 0, run_after = now(), last_error = null,
    locked_by = null, locked_at = null, lock_expires_at = null
where id = '…' and state = 'dead';
```

A claim that outlives `Jobs:LockDuration` is presumed abandoned and reclaimed, so a crashed worker
loses nothing.

## Keys

What each key is for is in [data-and-security.md](data-and-security.md#encryption-and-keys).

| Key | To rotate |
|---|---|
| Access-token signing (`Auth:Tokens:SigningKeys`) | Put the new key first and keep the old one second. Remove the old one after the access lifetime (10 minutes) |
| Tenant data master key (`Security:DataKeys`) | Add a new version with a new `Security:Keys` reference, in all three hosts at once. New writes use it; old data still decrypts. Keep every old version: nothing re-encrypts yet |
| Data-protection key ring (`Security:Keys:data-protection`) | The ring rotates its own keys. This key encrypts the stored ring, so changing it makes the ring unreadable: everyone is signed out and outstanding one-time links stop working. Plan it, don't just swap it |
| Audit client hash (`Audit:ClientHashKey`) | Changing it breaks the link between old and new hashes of the same client. Change it only deliberately |
| Badge signing (`Security:SigningKeys`) | Replace the PEM. The next award retires the old key (it stays in the DID document) and publishes the new one. Older credentials still verify |
| A compromised badge key | Mark its `learning.issuer_key` row `compromised`, which drops it from the DID document, then replace the PEM. Credentials it signed stop verifying, which is the point |

## Payments

Without Stripe, payments use the **manual gateway**: a family's payment waits until an admin marks
it received or waives it.

To take card payments through **Stripe** (D56):
1. Set `Payments:Stripe:SecretKey` and `Payments:Stripe:WebhookSecret` in the Api.
2. In Stripe, send webhooks to `https://<tenant host>/api/v1/webhooks/payments/stripe`.
3. Card details are entered in Stripe's own element. They never reach BrightTime.

Webhooks are believed only by their signature, within `Payments:Stripe:WebhookTolerance`. The worker
applies them, so a payment's status only moves forward. A refund made in Stripe's dashboard is
reported back. Refunds started from BrightTime aren't built.

Open (D56): what to do when a waived fee is paid anyway (it is logged, not changed), and whether
organisations must be paid directly (Stripe Connect).

## Push notifications

Set `Push:Fcm:ProjectId` and `Push:Fcm:ServiceAccountJson` in the worker to send through Firebase
Cloud Messaging (D40). Without them, pushes are written to the log. Devices that FCM reports as gone
are revoked. The app's push registration also needs the Firebase project's config files in the app
build. Apple delivery goes through FCM as well, which needs the Apple developer account.

## Mail

Web, Api and the worker send account mail (registration, reset, consent, claim, invitations)
through SMTP (`Email:*`). Locally it lands in mailpit. Notification emails aren't built yet: notifications reach
the inbox and push only.

## Badges

- Set `Security:SigningKeys:default` (or one key per organisation) in the Api before anyone awards
  a badge outside Development.
- Each organisation's DID resolves to `https://<tenant host>/api/v1/public/issuers/<org>/did.json`.
  The tenant's host name must be stable: changing it changes every issuer's DID, and older
  credentials stop resolving.
- **Check by hand before launch:** run a few credentials through 1EdTech's OB 3.0 validator, and
  the OB 2.0 hosted assertions through an OB 2.0 validator. This can't run in CI.

## Not yet in place

- An upgrade path for a database holding data (above).
- Backups, restores and retention. Retention periods are open question Q4; deletion versus audit is
  Q5.
- Object storage for media and generated files: undecided, so reports are made on request and
  listings have no images.
- Geocoding (Q7): new sites sit at their neighbourhood's centre.
- SMS (Q9).
- A tenant-provisioning tool.
