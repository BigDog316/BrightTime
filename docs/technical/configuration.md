# Configuration

Each host reads standard .NET configuration: `appsettings.json`, then
`appsettings.{Environment}.json`, then environment variables, where a double underscore stands
for a colon (`Security__SigningKeys__default` is `Security:SigningKeys:default`). Secrets belong in
environment variables or a secret store, never in a committed file.

**Development is different on purpose.** With `ASPNETCORE_ENVIRONMENT` (or `DOTNET_ENVIRONMENT` for
the worker) set to `Development`:
- missing keys are made up for the life of the process;
- the OpenAPI document is served;
- cookies may travel over plain HTTP;
- `/dev/sign-in` can be switched on.

Anywhere else, every missing key below marked **required** stops the host at start-up, or stops
the operation that needs it.

Columns: **W** Web, **A** Api, **J** Jobs.

## Connections

| Setting | W | A | J | |
|---|:-:|:-:|:-:|---|
| `Finder:ConnectionString` | ● | | | **Required** when `Finder:Mode=Sql`. A login that is a member of `brighttime_app`; the web uses it for the finder (as `brighttime_finder`) and for every module |
| `Auth:ConnectionString` | ● | | | **Required.** A login in `brighttime_auth`, for sign-in |
| `ConnectionStrings:App` | | ● | | **Required.** A login in `brighttime_app` |
| `ConnectionStrings:Auth` | | ● | | **Required.** A login in `brighttime_auth` |
| `ConnectionStrings:Jobs` | | | ● | **Required.** A login in `brighttime_jobs` |

With Npgsql in containers, add `GSS Encryption Mode=Disable`: the .NET images have no Kerberos
library to try it with.

## Tenancy and addresses

| Setting | W | A | J | Default | |
|---|:-:|:-:|:-:|---|---|
| `Tenancy:Mode` | ● | ● | | `Host` | `Host` (by host name) or `Fixed` |
| `Tenancy:TenantId` | ● | ● | | | The tenant when `Mode=Fixed` |
| `Tenancy:PublicScheme` | ● | ● | | `https` | The scheme of tenants' own addresses. Outside Development anything but `https` stops start-up |
| `Tenancy:PublicPort` | ● | ● | | | A port for tenants' addresses, development only (`8000` for compose) |
| `Auth:PublicBaseUrl` | ● | ● | | | A fixed public address, overriding the tenant's domain, for a single-site install |
| `Network:TrustedProxies` | ● | ● | | | Proxy addresses whose `X-Forwarded-For` and `-Proto` are believed. List the reverse proxy here in production |
| `Network:TrustedNetworks` | ● | ● | | | CIDR networks believed the same way, for proxies without a fixed address (containers) |
| `Network:ForwardLimit` | ● | ● | | `1` | How many forwarded hops to read |

## Keys

| Setting | W | A | J | |
|---|:-:|:-:|:-:|---|
| `Security:Keys:<ref>` | ● | ● | ● | Base64 symmetric keys, 32 bytes or more, by vault reference |
| `Security:Keys:data-protection` | ● | ● | | **Required.** Encrypts the shared data-protection key ring. Must be the same in both hosts. `DataProtection:KeyRef` names a different reference |
| `Security:DataKeys:<n>:Version`, `:KeyRef` | ● | ● | ● | **Required.** The master key versions tenant data keys are derived from, each naming a `Security:Keys` reference. The highest version encrypts. Must match across all three hosts |
| `Security:SigningKeys:<ref>`, `Security:SigningKeys:default` | | ● | | **Required to award badges.** RSA private keys as PEM, 2048 bits or more. `default` serves every reference without its own; per organisation, the reference is `badge-issuer-{orgId:N}` (D66); the site's, which signs CLRs, is `clr-issuer-{tenantId:N}` (D68) |
| `Auth:Tokens:SigningKeys:<n>:Id`, `:Key` | | ● | | **Required.** Access-token keys: an id and a base64 key of 32 bytes or more. The first signs; all verify |
| `Audit:ClientHashKey` | ● | ● | | **Required.** Base64, 32 bytes or more. Client addresses and user agents are kept only as hashes under it |

Generate a symmetric key with `openssl rand -base64 32`, and an RSA key with
`openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072`.

## Sign-in and sessions

| Setting | W | A | Default | |
|---|:-:|:-:|---|---|
| `Auth:Tokens:AccessLifetime` | | ● | `00:10:00` | Access token lifetime |
| `Auth:Tokens:RefreshIdle` | | ● | `30.00:00:00` | An adult's session ends after this long unused |
| `Auth:Tokens:YouthRefreshIdle` | | ● | `00:30:00` | A young person's (T14) |
| `Auth:Tokens:RefreshAbsolute` | | ● | `90.00:00:00` | No session outlives this |
| `Auth:SignInAttemptsPerMinute` | ● | ● | `10` | Sign-in, forgot and reset submissions per client address |
| `Auth:CheckBreachedPasswords` | ● | ● | `false` | The k-anonymity check against Have I Been Pwned |
| `Auth:StepUpMinutes` | ● | | `10` | How recent a sign-in a step-up action needs |
| `Auth:DevelopmentSignIn` | ● | | `false` | `/dev/sign-in`, honoured only in Development (D14) |
| `Auth:External:<name>:DisplayName`, `:Authority`, `:ClientId`, `:ClientSecret` | ● | | | An external OIDC provider, such as Google. The authority is its https issuer |
| `Auth:AppCallback` | ● | | `org.brighttime.app://external` | Where the web hands the app its one-time code (D41) |
| `Passkeys:AppOrigins` | ● | ● | | The app's origins (`android:apk-key-hash:…`), accepted by the shared relying party |
| `Api:PublicRequestsPerMinute` | | ● | `300` | The public routes' limit per client address |

## The app's links

| Setting | W | |
|---|:-:|---|
| `AppLinks:AppleAppIds` | ● | `TEAMID.bundle.id` entries, served in `/.well-known/apple-app-site-association` |
| `AppLinks:AndroidPackage` | ● | The Android package name |
| `AppLinks:AndroidCertificateFingerprints` | ● | SHA-256 signing fingerprints, served in `/.well-known/assetlinks.json` |

## Mail, push, payments and reports

| Setting | W | A | J | Default | |
|---|:-:|:-:|:-:|---|---|
| `Email:Host`, `:Port`, `:EnableSsl`, `:UserName`, `:Password` | ● | ● | ● | port `25` | SMTP |
| `Email:From` | ● | ● | ● | `BrightTime <no-reply@brighttime.invalid>` | The sender |
| `Email:PickupDirectory` | ● | ● | ● | | Write messages to a folder instead of sending (tests and development) |
| `Push:Fcm:ProjectId`, `:ServiceAccountJson` | | | ● | | Push through Firebase Cloud Messaging (D40). Without them, pushes are logged, not sent |
| `Payments:Stripe:SecretKey`, `:WebhookSecret` | | ● | | | Both set: payments go to Stripe Checkout (D56, D74). Either missing: the manual gateway |
| `Payments:Stripe:ApiBase`, `:ApiVersion`, `:WebhookTolerance` | | ● | | `https://api.stripe.com/`, `2024-06-20`, `00:05:00` | |
| `Reports:QuestPdfLicence` | ● | ● | | Community | QuestPDF's licence type. Community is free only under US$1M revenue (D57) |

## The finder

| Setting | W | Default | |
|---|:-:|---|---|
| `Finder:Mode` | ● | `Sql` | `Sql`, or an in-memory sample for tests |
| `Finder:TimeZone` | ● | `America/New_York` | For "starts on" dates |

## The worker

| Setting | J | Default | |
|---|:-:|---|---|
| `Jobs:Queue` | ● | `default` | The queue it claims from |
| `Jobs:BatchSize` | ● | `10` | Jobs claimed at once |
| `Jobs:PollInterval` | ● | `00:00:02` | How often it looks |
| `Jobs:LockDuration` | ● | `00:05:00` | How long a claim is held before another worker may take it |
| `Jobs:WorkerId` | ● | machine and process | |
| `Outbox:BatchSize` | ● | `100` | Messages dispatched at once |
| `Outbox:PollInterval` | ● | `00:00:01` | |

## Health

| Setting | W | A | Default | |
|---|:-:|:-:|---|---|
| `Health:OutboxLagDegraded` | ● | ● | `00:01:00` | `/health/ready` reports degraded past this outbox lag |
| `Health:JobLagDegraded` | ● | ● | `00:05:00` | and past this job lag |

## A production checklist

Before a host serves real people:

1. `ASPNETCORE_ENVIRONMENT=Production`, and `Tenancy:PublicScheme=https`.
2. Connection strings for the `bt_*` logins, never the superuser.
3. `Security:Keys:data-protection` and `Security:DataKeys` the same in every host, and the
   referenced keys present.
4. `Auth:Tokens:SigningKeys` in the Api, `Audit:ClientHashKey` in Web and Api.
5. `Security:SigningKeys:default` (or one per organisation) in the Api, if badges are used.
6. `Network:TrustedProxies` naming the reverse proxy.
7. `Email:*` for a real relay, with a real `Email:From`.
8. `Auth:CheckBreachedPasswords=true`.
9. `Auth:DevelopmentSignIn` unset.
10. Optional: `Payments:Stripe:*`, `Push:Fcm:*`, `Auth:External:*`, `AppLinks:*`,
    `Passkeys:AppOrigins`, `Reports:QuestPdfLicence`.
