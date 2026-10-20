# 05 — API surface

## Conventions

- Base: `/api/v{major}` — `v1` at launch. Version in the path; additive
  changes do not bump it. Deprecation via `Sunset` and `Deprecation` headers.
- Public, unauthenticated, cacheable, documented for third parties:
  `/api/v1/public/**`. Separate rate limits, separate OpenAPI document,
  separate CORS policy, and a hard constraint that no youth data can reach it
  (`ck_api_client_scopes` in the schema).
- Errors: RFC 9457 `application/problem+json`, with a stable `type` URI per
  error class and a `traceId`. Validation errors carry `errors` keyed by
  JSON pointer.
- Pagination: cursor only. `?cursor=&limit=` → `{ items, nextCursor }`.
  No `offset` on any collection.
- Idempotency: `Idempotency-Key` required on `POST` for applications,
  payments, badge issuance and attendance sync. Stored in the relevant
  table's `idempotency_key`/`client_record_id` column, so replay is a
  constraint, not a cache.
- Concurrency: `ETag` + `If-Match` on mutable resources, backed by Postgres
  `xmin` mapped as the EF concurrency token.
- Localisation: `Accept-Language`, with `?locale=` override for widgets.
  Every response carries `Content-Language`.
- Writes return the mutated resource; `201` carries `Location`.

SignalR hubs (not REST): `/hubs/notifications`, `/hubs/space/{spaceId}`,
`/hubs/moderation`, `/hubs/roster/{listingId}`.

---

## Public (no auth)

```
GET  /api/v1/public/listings                 search; the full filter set
GET  /api/v1/public/listings/{idOrSlug}
GET  /api/v1/public/listings/{id}/occurrences ?from=&to=
GET  /api/v1/public/listings.ics             iCal feed for a filter set
GET  /api/v1/public/collections
GET  /api/v1/public/collections/{slug}
GET  /api/v1/public/neighbourhoods
GET  /api/v1/public/neighbourhoods/{code}    community page + boundary
GET  /api/v1/public/organisations/{slug}
GET  /api/v1/public/taxonomy/{vocabulary}
GET  /api/v1/public/schools                  typeahead for "near my school"
GET  /api/v1/public/assets                   verified landscape assets
GET  /api/v1/public/badges/{verifySlug}      Open Badges verification
GET  /api/v1/public/badges/{verifySlug}.jsonld
GET  /api/v1/public/portfolios/{publicSlug}  only when visibility=public
GET  /api/v1/public/showcase
GET  /api/v1/public/tenant                   branding, locales, feature flags
GET  /.well-known/did.json                   issuer DID document (per tenant host)
GET  /api/v1/public/widgets/{widgetKey}      widget bootstrap config
POST /api/v1/public/surveys/{id}/responses   anonymous resident survey
POST /api/v1/public/reports                  report a listing (abuse/inaccuracy)
GET  /api/v1/public/openapi.json
```

`GET /public/listings` query parameters, which are the canonical filter
vocabulary used by web, mobile and widgets alike:

```
q, kind[], category[], interest[], age, grade, from, to, dayOfWeek[],
free, maxCost, nearAddress | nearSchool | nearNeighbourhood, radiusM,
modality[], language[], accommodation[], mealsProvided, transportProvided,
dropIn, orgId, sort, cursor, limit, locale
```

`nearAddress` is geocoded in-request and **never persisted or logged** — not
in `search_event`, not in access logs. nginx is configured to strip it from
the logged query string.

*Built (2026-10-19, D30):*
- **Endpoints:** `/listings`, `/listings/{slug}`, `/collections`, `/collections/{slug}`,
  `/neighbourhoods`, `/neighbourhoods/{code}`, `/organisations/{slug}`,
  `/taxonomy/{vocabulary}` and `/tenant`.
- **Filters:** `q, kind[], category[], accommodation[], age, grade, from, to, free, maxCost,
  mealsProvided, transportProvided, dropIn, nearNeighbourhood, orgId, sort, cursor, limit`.
  The rest of the list above waits on Q7 and on combining several any-of filters.
- **Paging:** results come as `{ items, nextCursor, notices }`.

---

## Identity & consent

```
POST   /api/v1/auth/register                     adult / youth 13+
POST   /api/v1/auth/login
POST   /api/v1/auth/logout
POST   /api/v1/auth/token                        mobile: access + refresh
POST   /api/v1/auth/token/refresh                rotating, family-chained
POST   /api/v1/auth/passkeys/options             WebAuthn registration options
POST   /api/v1/auth/passkeys                     complete registration
POST   /api/v1/auth/passkeys/assert/options
POST   /api/v1/auth/passkeys/assert
GET    /api/v1/auth/external/{provider}          OIDC challenge
GET    /api/v1/auth/external/{provider}/callback
POST   /api/v1/auth/password/forgot | /reset
GET    /api/v1/me
PATCH  /api/v1/me                                locale, display name, interests
GET    /api/v1/me/capabilities                   youth_capability, for UI gating
DELETE /api/v1/me/sessions                       sign out everywhere
GET    /api/v1/me/access-log                     who read my record, and why
POST   /api/v1/me/export                         subject export request
POST   /api/v1/me/delete                         subject deletion request
GET    /api/v1/me/requests/{id}

POST   /api/v1/children                          guardian creates a child profile
GET    /api/v1/children
GET    /api/v1/children/{id}
PATCH  /api/v1/children/{id}
POST   /api/v1/children/{id}/consents            grant a scoped consent
DELETE /api/v1/children/{id}/consents/{scope}    revoke
GET    /api/v1/children/{id}/activity            enrollments, attendance, badges

POST   /api/v1/guardian-links                    youth or guardian initiates
POST   /api/v1/guardian-links/{id}/verify        email-plus / card / ID / form
POST   /api/v1/guardian-links/{id}/revoke
GET    /api/v1/guardian-links
```

*Built (2026-10-18, D28):*
- `POST /auth/token`, `POST /auth/token/refresh` and `POST /auth/logout` (a refresh token
  in the body; 204 whether or not it was one);
- `GET /me`;
- `DELETE /me/sessions`.

*Built (2026-10-19, D31):* `GET /me/capabilities`; `POST`, `GET` `/children` and
`GET /children/{id}`; `POST /children/{id}/consents` (scope and the disclosure hash shown);
`DELETE /children/{id}/consents/{scope}`; `POST /guardian-links/{id}/revoke`; and
`GET /consent/disclosures`, which gives each scope's text, hash and accepted methods. An organiser
verifies a link with `POST /organiser/guardian-links/{id}/attest`. Health, outside the
API's versioning: `GET /health/live` and `GET /health/ready`.

*Built (2026-10-19, D34):*
- `POST /auth/register {email}`: an adult account, for a guardian. It answers 202 for any
  well-formed address, and sends the set-password link after the response.
- `POST /guardian-links/{id}/verify {token}`: email plus. The guardian, signed in, sends the
  token from the consent email that making a child profile sent them. It answers with the
  child, now active, and a confirmatory email follows. A bad token gets 403
  `consent.token_invalid`, an expired one 412 `consent.token_expired`, and a verified link
  409 `consent.already_verified`.
- `POST /guardian-links/{id}/consent-email`: a new consent email that replaces the last
  (202), at most once a minute (429).

*Built (2026-10-20, D35):*
- `POST /auth/register {email, birthMonth}`: with a birth month of 13 to 17, a youth account.
  Under 13 gets 403 `register.under_13`, with nothing kept or sent. 18 or older, or no month,
  gets an adult account, as before.
- `POST /children/{id}/claim {email}`: from 13, the guardian hands the profile to its owner
  (202, rate limited). The link goes to that address; an address that already has an account
  tells only its owner.
- `POST /auth/claim {token, password}`: the young person takes the account (204), which then
  signs in as theirs. On the web the same link opens `/account/claim`.
- A child's view gains `accountKind` (`child`, or `youth` once claimed).

Still to come: passwords and passkeys through the API, and export and delete requests.

Consent scopes are the enum in `identity.parental_consent.scope`:
`account`, `enrollment`, `attendance_photo`, `community_participation`,
`showcase_publication`, `media_capture`, `messaging`, `district_data_share`,
`research_use`, `location_use`.

---

## Opportunities (authenticated surface)

```
GET    /api/v1/listings                       includes drafts within scope
POST   /api/v1/listings
GET    /api/v1/listings/{id}
PATCH  /api/v1/listings/{id}
POST   /api/v1/listings/{id}/submit
POST   /api/v1/listings/{id}/clone            ?season=
DELETE /api/v1/listings/{id}                  draft only; otherwise archive
POST   /api/v1/listings/{id}/archive
PUT    /api/v1/listings/{id}/translations/{locale}
PUT    /api/v1/listings/{id}/sites
PUT    /api/v1/listings/{id}/terms
POST   /api/v1/listings/{id}/sessions
PATCH  /api/v1/listings/{id}/sessions/{sid}
POST   /api/v1/listings/{id}/media
GET    /api/v1/listings/{id}/preview-eligibility   dry-run for a given member

POST   /api/v1/organiser/listings/{id}/approve
POST   /api/v1/organiser/listings/{id}/reject       body: reason
POST   /api/v1/organiser/listings/{id}/request-changes
GET    /api/v1/organiser/review-queue

GET    /api/v1/me/saved-items
PUT    /api/v1/me/saved-items/{listingId}
DELETE /api/v1/me/saved-items/{listingId}
GET    /api/v1/me/feed                        personalised, with `why` per item
POST   /api/v1/me/reminders
DELETE /api/v1/me/reminders/{id}
PUT    /api/v1/me/search-preferences
```

*Built (2026-10-19, D33):*
- **Saved items, feed, reminders and search preferences:** everything listed above.
- **Also:** `GET` and `PUT /me/interests`, `GET /me/search-preferences` and `GET /me/reminders`.
- **Feed:** answers as `{ items, nextCursor, notices }`, with each item's "why".

---

## Directory

```
POST   /api/v1/organisations                      application
GET    /api/v1/organisations/{id}
PATCH  /api/v1/organisations/{id}
POST   /api/v1/organisations/{id}/submit
GET    /api/v1/organisations/{id}/documents
POST   /api/v1/organisations/{id}/documents
GET    /api/v1/organisations/{id}/sites
POST   /api/v1/organisations/{id}/sites
PATCH  /api/v1/sites/{id}
GET    /api/v1/organisations/{id}/members
POST   /api/v1/organisations/{id}/members/invite
PATCH  /api/v1/organisations/{id}/members/{mid}
DELETE /api/v1/organisations/{id}/members/{mid}
GET    /api/v1/organisations/{id}/clearances
PUT    /api/v1/organisations/{id}/clearances/{userId}/{kind}   status + expiry only

POST   /api/v1/organiser/organisations/{id}/approve | /reject | /suspend
GET    /api/v1/organiser/vetting-queue
```

---

## Enrollment

```
GET    /api/v1/family-profile
PUT    /api/v1/family-profile
GET    /api/v1/family-profile/members
POST   /api/v1/family-profile/members
PATCH  /api/v1/family-profile/members/{id}
PUT    /api/v1/family-profile/members/{id}/support-needs
GET    /api/v1/family-profile/shares
DELETE /api/v1/family-profile/shares/{id}         revoke a provider's access

GET    /api/v1/listings/{id}/form
POST   /api/v1/applications                       Idempotency-Key required
GET    /api/v1/applications/{id}
PATCH  /api/v1/applications/{id}                  draft answers
POST   /api/v1/applications/{id}/submit
POST   /api/v1/applications/{id}/withdraw
GET    /api/v1/applications                       scoped: mine / my children / my org

POST   /api/v1/org/{orgId}/applications/{id}/decide      accept | decline | waitlist
POST   /api/v1/org/{orgId}/applications/bulk-decide
GET    /api/v1/org/{orgId}/listings/{lid}/waitlist
POST   /api/v1/org/{orgId}/listings/{lid}/waitlist/offer

GET    /api/v1/enrollments
GET    /api/v1/enrollments/{id}
POST   /api/v1/enrollments/{id}/withdraw

GET    /api/v1/waivers/{id}
POST   /api/v1/waivers/{id}/sign
POST   /api/v1/org/{orgId}/waivers
POST   /api/v1/org/{orgId}/forms
PATCH  /api/v1/org/{orgId}/forms/{id}

POST   /api/v1/payments/intents                   provider-agnostic
GET    /api/v1/payments/intents/{id}
POST   /api/v1/payments/intents/{id}/waive
POST   /api/v1/webhooks/payments/{provider}       signature-verified, idempotent
```

---

## Attendance

```
GET    /api/v1/org/{orgId}/listings/{lid}/roster       ?occurrence=
POST   /api/v1/org/{orgId}/attendance                  single record
POST   /api/v1/org/{orgId}/attendance/batch            offline sync; per-item results
GET    /api/v1/org/{orgId}/attendance                  ?from=&to=&listingId=
PATCH  /api/v1/org/{orgId}/attendance/{id}             correction (supersedes)
POST   /api/v1/org/{orgId}/checkin-tokens              rotating QR
POST   /api/v1/attendance/checkin                      kiosk: token in body
GET    /api/v1/org/{orgId}/kiosk-devices
POST   /api/v1/org/{orgId}/kiosk-devices
POST   /api/v1/org/{orgId}/kiosk-devices/{id}/revoke   triggers remote purge
GET    /api/v1/me/attendance                           youth / guardian view
```

`POST /attendance/batch` returns `207`-style per-item outcomes:
`{ clientRecordId, status: applied|duplicate|conflict|rejected, serverId, reason }`.
Duplicates are a success, not an error — that is the point of
`ux_attendance_client_record`.

---

## Learning

```
GET    /api/v1/me/portfolio
PATCH  /api/v1/me/portfolio                      visibility, slug, headline
GET    /api/v1/me/participation
POST   /api/v1/me/participation                  self-reported, pending verify
GET    /api/v1/me/badges
GET    /api/v1/me/skills
POST   /api/v1/me/goals
POST   /api/v1/me/portfolio/export               pdf | json_ob3 | clr2

GET    /api/v1/projects/{id}
POST   /api/v1/projects
PATCH  /api/v1/projects/{id}
POST   /api/v1/projects/{id}/submit-for-approval
POST   /api/v1/org/{orgId}/projects/{id}/approve

GET    /api/v1/badge-classes
POST   /api/v1/org/{orgId}/badge-classes
PATCH  /api/v1/org/{orgId}/badge-classes/{id}
POST   /api/v1/org/{orgId}/badge-classes/{id}/issue     Idempotency-Key required
POST   /api/v1/org/{orgId}/badge-assertions/{id}/revoke
GET    /api/v1/org/{orgId}/issuer-keys
POST   /api/v1/org/{orgId}/issuer-keys/rotate

GET    /api/v1/playlists
GET    /api/v1/playlists/{slug}
POST   /api/v1/org/{orgId}/playlists
PUT    /api/v1/org/{orgId}/playlists/{id}/steps
POST   /api/v1/playlists/{id}/enroll
GET    /api/v1/me/playlists
PUT    /api/v1/me/playlists/{id}/steps/{stepId}        progress, evidence
POST   /api/v1/org/{orgId}/playlists/{id}/steps/{stepId}/review
```

---

## Community

```
GET    /api/v1/spaces                            mine
GET    /api/v1/spaces/{id}
POST   /api/v1/org/{orgId}/spaces
PUT    /api/v1/org/{orgId}/spaces/{id}/members
GET    /api/v1/spaces/{id}/posts
POST   /api/v1/spaces/{id}/posts                 → 202, state=screening
DELETE /api/v1/posts/{id}                        author delete
GET    /api/v1/spaces/{id}/challenges
POST   /api/v1/org/{orgId}/spaces/{id}/challenges
POST   /api/v1/challenges/{id}/submissions
POST   /api/v1/submissions/{id}/feedback         rubric scores + comment
GET    /api/v1/conversations
POST   /api/v1/conversations                     422 if it would be adult↔minor 1:1
GET    /api/v1/conversations/{id}/messages
POST   /api/v1/conversations/{id}/messages
POST   /api/v1/org/{orgId}/broadcasts            to enrolled families
POST   /api/v1/reports                           report anything
GET    /api/v1/moderation/queue                  ?severity=&state=
POST   /api/v1/moderation/reports/{id}/triage
POST   /api/v1/moderation/actions
GET    /api/v1/moderation/history                ?targetKind=&targetId=
POST   /api/v1/org/{orgId}/showcase/{itemId}/approve
```

`POST /conversations` returning `422` with
`type: /problems/unsafe-conversation-shape` is a designed, documented
outcome — the client is expected to offer "add your program coordinator" as
the remedy.

---

## Workforce

```
GET    /api/v1/me/mentor-profile
PUT    /api/v1/me/mentor-profile
PUT    /api/v1/me/mentor-profile/availability
GET    /api/v1/me/training
POST   /api/v1/courses/{id}/enroll
PUT    /api/v1/courses/{id}/units/{unitId}/complete
GET    /api/v1/courses
POST   /api/v1/organiser/courses
GET    /api/v1/curriculum
GET    /api/v1/curriculum/{slug}
POST   /api/v1/org/{orgId}/curriculum
POST   /api/v1/organiser/curriculum/{id}/vet
POST   /api/v1/org/{orgId}/curriculum/{id}/adopt
GET    /api/v1/gigs                              ?skill=&nearMe=&virtual=
POST   /api/v1/org/{orgId}/gigs
PATCH  /api/v1/org/{orgId}/gigs/{id}
POST   /api/v1/gigs/{id}/apply
GET    /api/v1/org/{orgId}/gigs/{id}/applications    with match explanation
POST   /api/v1/org/{orgId}/gig-applications/{id}/decide
GET    /api/v1/me/assignments
POST   /api/v1/me/assignments/{id}/hours
POST   /api/v1/org/{orgId}/hours/{id}/approve
GET    /api/v1/org/{orgId}/mentors/search            ?skill=&dayOfWeek=&radiusM=
```

---

## Landscape

```
GET    /api/v1/assets                            ?bbox=&kind=&status=
POST   /api/v1/assets
PATCH  /api/v1/assets/{id}
POST   /api/v1/organiser/assets/{id}/verify | /reject | /merge
GET    /api/v1/field/campaigns
GET    /api/v1/field/assignments                 mine
GET    /api/v1/field/assignments/{id}/bundle     offline pack: area, existing assets, taxonomy
POST   /api/v1/field/observations/batch          offline sync, client_record_id
GET    /api/v1/organiser/field/review-queue
POST   /api/v1/organiser/field/observations/{id}/review
GET    /api/v1/surveys/{id}
POST   /api/v1/surveys/{id}/responses
POST   /api/v1/organiser/surveys
GET    /api/v1/landscape/supply                  ?asOf=&ageBand=&category=
GET    /api/v1/landscape/deserts                 ?asOf=&ageBand=
GET    /api/v1/landscape/travel-time             ?origin=&mode=
GET    /api/v1/landscape/barriers                suppressed
POST   /api/v1/organiser/landscape/recompute
```

---

## Analytics & reporting

```
GET    /api/v1/dashboard/youth
GET    /api/v1/dashboard/guardian
GET    /api/v1/dashboard/provider/{orgId}
GET    /api/v1/dashboard/organiser
GET    /api/v1/dashboard/civic
GET    /api/v1/metrics/participation             ?by=neighbourhood|age|category|modality
GET    /api/v1/metrics/utilisation
GET    /api/v1/metrics/demand-gaps
GET    /api/v1/metrics/retention
POST   /api/v1/reports/runs                      report key + scope + format
GET    /api/v1/reports/runs/{id}                 → download when available
GET    /api/v1/reports/catalogue
POST   /api/v1/research-exports                  purpose ≥ 40 chars, k ≥ 5
POST   /api/v1/organiser/research-exports/{id}/approve
GET    /api/v1/research-exports/{id}
```

Every aggregate response carries a suppression envelope:

```json
{ "data": [...],
  "suppression": { "minCellSize": 10, "suppressedCells": 4,
                   "complementary": true, "policy": "participation" } }
```

Showing that something was suppressed, and how much, is part of the
contract — a civic dashboard that silently hides cells teaches users to
mistrust the numbers.

---

## Platform & integrations

```
GET    /api/v1/organiser/tenant
PATCH  /api/v1/organiser/tenant                  branding, locales, min cell size
GET    /api/v1/organiser/feature-flags
PUT    /api/v1/organiser/feature-flags/{key}
GET    /api/v1/organiser/taxonomy/{vocabulary}
POST   /api/v1/organiser/taxonomy/{vocabulary}
PUT    /api/v1/organiser/resource-overrides/{locale}/{key}
GET    /api/v1/organiser/retention-policies
PUT    /api/v1/organiser/retention-policies/{dataClass}
GET    /api/v1/organiser/subject-requests
POST   /api/v1/organiser/subject-requests/{id}/fulfil
GET    /api/v1/organiser/audit                   ?actor=&subject=&action=&from=&to=
GET    /api/v1/organiser/api-clients
POST   /api/v1/organiser/api-clients
POST   /api/v1/organiser/api-clients/{id}/rotate
GET    /api/v1/organiser/widgets
POST   /api/v1/organiser/widgets

POST   /api/v1/imports                           CSV / HSDS; dry-run by default
GET    /api/v1/imports/{id}                      row-level errors
POST   /api/v1/imports/{id}/apply

GET    /api/v1/organiser/district-connections
POST   /api/v1/organiser/district-connections    created disabled; DPA required to enable
POST   /api/v1/organiser/district-connections/{id}/enable
POST   /api/v1/organiser/district-connections/{id}/sync
GET    /api/v1/organiser/district-connections/{id}/runs
GET    /api/v1/auth/sso/{provider}/start          clever | classlink; flag-gated, off by default
```

## Mobile-specific

```
POST   /api/v1/devices                           push token registration
DELETE /api/v1/devices/{id}
GET    /api/v1/sync/manifest                     what the client should pull, with etags
GET    /api/v1/sync/bundle/{domain}              roster | field-assignment packs
GET    /api/v1/tiles/{z}/{x}/{y}.pbf             MapLibre vector tile proxy, cached
GET    /api/v1/geocode?q=                        proxied; never logged, never stored
```

The tile and geocode proxies exist so the client never talks to a third party
directly — no third-party request means no third-party log of what a young
person searched for or where they are.

*Built (2026-10-19, D33):*
- **Devices:** `POST /devices` and `DELETE /devices/{id}`.
- **Settings and inbox:** `GET` and `PUT /me/notification-preferences` (choices by kind and
  channel, and one quiet window); `GET /me/notifications`; `POST /me/notifications/{id}/read`.
- **Waiting:** sync and the tile and geocode proxies wait on offline attendance (M3) and Q7.

*Changed (2026-10-20, D35):* the inbox comes back in words (`title`, `body`), in the
request's language, worded as the push was. The web serves `/.well-known/apple-app-site-association`
and `/.well-known/assetlinks.json` for the app's `/app/*` links, from `AppLinks:*`, and answers 404
until an app is configured.
