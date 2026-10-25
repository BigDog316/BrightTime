# API reference

The API host serves everything under `/api/v1` on each tenant's own host name, for example
`https://kettle.example.org/api/v1/`. It is what the app uses. 180 routes are listed below, grouped
by area. In Development the host also serves the full OpenAPI document at `/api/v1/openapi.json`.

- [Conventions](#conventions)
- [Errors](#errors)
- [Authentication](#authentication)
- [Public reads](#public-reads)
- [Your account](#your-account)
- [Children and consent](#children-and-consent)
- [Family profile and shares](#family-profile-and-shares)
- [Applications, waivers and payments](#applications-waivers-and-payments)
- [Attendance](#attendance)
- [Messages and devices](#messages-and-devices)
- [Learning: portfolio, badges and playlists](#learning-portfolio-badges-and-playlists)
- [Organisation routes](#organisation-routes)
- [Organiser and webhook routes](#organiser-and-webhook-routes)

## Conventions

- **Tenant.** The host name selects the tenant. A request to an unknown host is a 404.
- **Auth.** `Authorization: Bearer <access token>` on every route not marked *public*. Public routes
  are anonymous and rate-limited per client address (`Api:PublicRequestsPerMinute`, 300 by
  default); a refused request is a 429 with `Retry-After: 60`.
- **Language.** `?locale=`, then the signed-in person's own language, then `Accept-Language`, then
  the tenant's default. The response states the one used in `Content-Language`.
- **JSON.** camelCase. Instants are ISO 8601 with an offset, always UTC. Dates are `yyyy-MM-dd`.
  Ids are UUIDs.
- **Paging.** Lists that page return `{ "items": [...], "nextCursor": "..." }`. Pass `cursor` back
  for the next page. A cursor fixes the instant the search was computed at, so pages don't
  drift.
- **Idempotency.** `POST /applications` and `POST /payments/intents` require an `Idempotency-Key`
  header. A retry with the same key returns the first result rather than creating another.
  Elsewhere, retries are made safe by the operation itself: attendance batches are deduplicated
  per mark, a badge is awarded once, a decision is a state change.
- **Versioning.** Additive. New fields and routes may appear in `v1`; unknown query parameters are
  ignored.
- **Youth records.** Routes that return or change a young person's data are audited on every call
  (see [data-and-security.md](data-and-security.md#audit)).

## Errors

Errors are RFC 9457 problem documents with a stable `type` per reason:

```json
{
  "type": "urn:brighttime:problem:badges.already_awarded",
  "title": "This young person already holds this badge.",
  "status": 409,
  "traceId": "00-…"
}
```

| Status | When |
|---|---|
| 400 | Validation. `urn:brighttime:problem:validation` carries `errors` keyed by field; a module's refusal carries its own code (`badges.criteria`) |
| 401 | No valid token |
| 403 | Signed in but not allowed. The code says why (`badges.forbidden`, `portfolio.showcase`, `family.link_forbidden`) |
| 404 | Not found, or not yours to see |
| 409 | A conflict with the current state (`badges.not_published`, `seats.full`) |
| 412 | A stale version on a concurrent edit |
| 429 | Rate limited |

The `title` is English. A client shows the person the catalogue string `error.<code>` (for example
`error.badges.already_awarded`) in their language, falling back to the title. That's what the
app's `Words.Problem` does. Values a message needs travel as extensions on the problem.

## Authentication

| Route | | |
|---|---|---|
| `POST /auth/register` | public | `{ email, birthMonth }`. Under 13 is refused; 13 to 17 makes a youth account. Emails a link to choose a password |
| `POST /auth/token` | public | `{ email, password, deviceId }` → `{ accessToken, refreshToken, … }`. Rate-limited |
| `POST /auth/token/refresh` | public | `{ refreshToken }`. Rotates the refresh token; a reused token revokes its family |
| `POST /auth/logout` | | Ends this session |
| `POST /auth/passkeys/assert/options`, `POST /auth/passkeys/assert` | public | Sign in with a passkey |
| `POST /auth/passkeys/options`, `POST /auth/passkeys` | | Add a passkey |
| `GET /auth/external/providers` | public | The external providers this tenant offers |
| `POST /auth/external/token` | public | Redeem the one-time code from the web's external flow, with the PKCE verifier |
| `POST /auth/password/forgot`, `POST /auth/password/reset` | public | The reset email, and setting a new password with its token. The forgot answer is the same for every address |
| `POST /auth/claim` | public | A young person takes over the profile a guardian made, with the claim token |

## Public reads

All anonymous and rate-limited.

| Route | |
|---|---|
| `GET /public/tenant` | Branding, locales and feature flags |
| `GET /public/listings` | Search. Parameters: `q`, `kind` (repeatable), `category` (repeatable, any of), `accommodation` (repeatable, all of), `age`, `grade`, `from`, `to`, `free`, `maxCost` (whole dollars), `mealsProvided`, `transportProvided`, `dropIn`, `nearNeighbourhood` (a code), `orgId`, `sort`, `cursor`, `limit`. Malformed values are a 400; an unknown kind or a stale cursor is dropped and reported in `notices` |
| `GET /public/listings/{slug}` | One listing |
| `GET /public/listings/{slug}/eligibility` | Its eligibility rules, as families see them |
| `GET /public/listings/{slug}/form` | Its registration form, in the request's language |
| `GET /public/collections`, `GET /public/collections/{slug}` | Curated collections |
| `GET /public/neighbourhoods`, `GET /public/neighbourhoods/{code}` | Neighbourhoods, and one with its boundary and what's on |
| `GET /public/organisations/{slug}` | An organisation's public page |
| `GET /public/taxonomy/{vocabulary}` | `category`, `interest`, `accommodation` or `skill`, named in the request's language |
| `GET /public/portfolios/{slug}` | A portfolio its owner chose to show (D65). `noindex` when link-only |
| `GET /public/issuers/{orgId}/did.json` | An organisation's DID document (`application/did+json`), every non-compromised key (D66) |
| `GET /public/issuers/{orgId}/ob2` | The OB 2.0 Issuer |
| `GET /public/issuers/{orgId}/revocations` | The `1EdTechRevocationList` of its revoked credentials (`no-store`) |
| `GET /public/badges/classes/{id}` | A published badge as an OB 3.0 Achievement |
| `GET /public/badges/classes/{id}/ob2` | As an OB 2.0 BadgeClass |
| `GET /public/badges/classes/{id}/image.svg` | Its generated image |
| `GET /public/badges/{slug}` | A credential's standing (`valid`, `expired`, `revoked`), with the credential and its JWT. `no-store`, `noindex` |
| `GET /public/badges/{slug}/ob2` | The OB 2.0 hosted assertion, or `410 Gone` with `revoked: true` and the reason |
| `GET /public/playlists`, `GET /public/playlists/{slug}` | Published playlists: steps, estimated hours, the badge at the end, and any badge needed to start (D67) |
| `GET /terms` | The current terms of use, with their hash |

## Your account

| Route | |
|---|---|
| `GET /me` | The signed-in person |
| `GET /me/capabilities` | What the account may do now (enrol, be messaged, post, upload, showcase), so the app gates on the server's answer |
| `GET /me/terms`, `POST /me/terms` | Whether the person has agreed to the current terms, and agreeing (by hash) |
| `DELETE /me/sessions` | Sign out everywhere |
| `GET /me/passkeys`, `DELETE /me/passkeys/{id}` | The account's passkeys |
| `GET /me/feed` | The personal feed, paged, each item with why it was shown |
| `GET`, `PUT /me/interests` | Interests, with weights |
| `GET`, `PUT /me/search-preferences` | The anchor neighbourhood and distance |
| `GET /me/saved-items`, `PUT`, `DELETE /me/saved-items/{listingId}` | Saved listings, with an optional note |
| `GET`, `POST /me/reminders`, `DELETE /me/reminders/{id}` | Reminders before a session or a deadline, booked as jobs |
| `GET /me/notifications`, `POST /me/notifications/{id}/read` | The inbox |
| `GET`, `PUT /me/notification-preferences` | Channels per kind, and quiet hours |

## Children and consent

| Route | |
|---|---|
| `GET`, `POST /children`, `GET /children/{id}` | A guardian's child profiles: a given name and a birth month only |
| `GET /consent/disclosures` | The exact text of each consent scope, with its hash |
| `POST /children/{id}/consents`, `DELETE /children/{id}/consents/{scope}` | Give consent to a scope (by the disclosure's hash), or withdraw it |
| `POST /children/{id}/claim` | Hand a child of 13 or over their own sign-in, by email |
| `POST /guardian-links/{id}/verify` | Confirm the guardianship with the consent email's token |
| `POST /guardian-links/{id}/consent-email` | Send the consent email again |
| `POST /guardian-links/{id}/revoke` | Stop managing the profile; ends every consent |

## Family profile and shares

The owner alone reads or changes a family profile.

| Route | |
|---|---|
| `GET`, `PUT /family-profile` | The household |
| `POST /family-profile/members`, `PUT`, `DELETE /family-profile/members/{id}` | Members; names and dates of birth are encrypted |
| `PUT /family-profile/members/{id}/support-needs` | A member's support needs |
| `PUT /family-profile/members/{id}/account` | Link a youth or child member to the owner's own account or a verified-guardian child's (`{ userId }`, `null` unlinks; D65) |
| `POST /family-profile/contacts`, `PUT`, `DELETE /family-profile/contacts/{id}` | Emergency and other contacts |
| `GET`, `POST /family-profile/shares`, `DELETE /family-profile/shares/{id}` | What the family shares with an organisation: members, fields, optionally one listing, until a date. Deleting withdraws it at once |

## Applications, waivers and payments

| Route | |
|---|---|
| `GET /applications`, `POST /applications` | The family's applications; start a draft (`Idempotency-Key` required) |
| `GET`, `PATCH /applications/{id}` | One application; answer its form |
| `POST /applications/{id}/submit` | Submit once: checks completeness, hard eligibility rules and required waivers, and shares the applicant as chosen |
| `POST /applications/{id}/withdraw` | Withdraw |
| `POST /applications/{id}/offer/accept`, `/offer/decline` | Answer a place offered from the waitlist |
| `GET /applications/{id}/waivers`, `POST /applications/{id}/waivers/{waiverId}/sign` | The waivers it needs, and signing one (by the text's hash) |
| `GET /applications/{id}/fees` | The fees and price levels that apply |
| `POST /payments/intents`, `GET /payments/intents/{id}` | Pay a fee at a price level (`Idempotency-Key` required). With Stripe, returns the client secret for Stripe's element; otherwise the payment waits to be marked received |

## Attendance

| Route | |
|---|---|
| `GET /me/attendance` | A family's own attendance |
| `POST /me/attendance/badges` | A two-minute, single-use check-in code for an enrolment, for staff to scan |
| `POST /attendance/checkin` | Check in by scanning a kiosk's one-minute code |
| `POST /attendance/devices/contact` | A device's check-in with the server, without signing in: says whether it's revoked and must wipe |

## Messages and devices

| Route | |
|---|---|
| `GET /conversations`, `GET /conversations/{id}` | The person's conversations |
| `POST /conversations/{id}/messages` | Reply. A message from an adult to a minor may be held for a moderator |
| `POST /devices`, `DELETE /devices/{id}` | Register a phone for push, and forget it |

## Learning: portfolio, badges and playlists

| Route | |
|---|---|
| `GET`, `PUT /me/portfolio` | The young person's portfolio and its settings (display name, headline, bio, visibility, link name) |
| `POST /me/portfolio/participation/{id}/hide`, `/show` | Hide a record from the total and any shown page, or show it again |
| `GET /me/badges` | Badges held, revoked ones marked, each with its credential JWT |
| `POST /playlists/{id}/enroll` | Start a published playlist: needs the account's sign-up capability and any badge the playlist asks for. Starting one already started returns it |
| `GET /me/playlists` | Playlists started, each step with its state (`not_started`, `submitted`, `completed`, `needs_rework`), whether it's open yet, and any reviewer's note |
| `PUT /me/playlists/{id}/steps/{stepId}` | Mark a step done (`{ reflection }` for a reflection step): completed, or submitted when it needs review. Completing the last required step completes the playlist and awards its badge |

## Organisation routes

All under `/org/{orgId}`. Each needs a live membership in that organisation with the permission and
minimum role shown. *Admin* means owner or admin.

| Route | Who | |
|---|---|---|
| `GET /family-shares`, `GET /family-shares/{id}` | Staff | Families' live shares with the organisation |
| `GET`, `POST /forms`; `GET`, `PUT /forms/{id}`; `POST /forms/{id}/publish`, `/revise`, `/retire` | Admin | Registration forms |
| `PUT`, `DELETE /listings/{listingId}/form` | Admin | Attach or detach a published form |
| `GET`, `POST /waivers`; `GET`, `PUT /waivers/{id}`; `POST /waivers/{id}/publish`, `/revise`, `/retire` | Admin | Waivers |
| `PUT`, `DELETE /listings/{listingId}/waivers/{waiverId}` | Admin | Ask for a waiver on a listing, required or optional |
| `GET`, `PUT /listings/{listingId}/eligibility`; `POST …/eligibility/preview` | Admin | Rules (`age_at_start`, `grade_range`, `neighbourhood_in`, `badge_required`, `playlist_completed`), and a dry run against a made-up applicant, who may hold `badgeClassIds` and `playlistIds` |
| `GET /listings/{listingId}/applications`; `GET /applications/{id}` | Staff | The queue, and one application as the family's share allows |
| `POST /applications/{id}/decide`, `POST /applications/bulk-decide` | Staff | Decide singly or in bulk; the bulk answer lists any left and why |
| `GET /listings/{listingId}/seats`; `POST /listings/{listingId}/waitlist/offer` | Staff | Places taken and waiting; offer freed places to the front of the waitlist |
| `PUT /listings/{listingId}/seats` | Admin | Set places and waitlist places |
| `GET`, `PUT /listings/{listingId}/fees`; `GET /listings/{listingId}/payments` | Admin | Fees and price levels; payments |
| `POST /payments/{id}/received`, `POST /payments/{id}/waive` | Admin | Mark received; waive with a reason |
| `GET /attendance/meetings`; `GET /listings/{listingId}/roster` | Staff | Meetings, and a meeting's roster |
| `GET`, `POST /attendance`; `PATCH /attendance/{id}` | Staff | Records; record a mark; correct one with a reason |
| `POST /attendance/batch` | Staff, registered device | An offline batch. Each mark comes back `accepted`, `duplicate`, `conflict` or `rejected` |
| `POST /attendance/scan` | Staff | Check in by scanning a family's code |
| `POST /checkin-tokens` | Staff | A kiosk's one-minute code |
| `GET /kiosk-devices` | Admin | Phones and kiosks registered to sync |
| `POST /kiosk-devices`; `POST /kiosk-devices/{id}/revoke` | Staff | Register a device; revoke one, which asks it to wipe on its next contact |
| `POST /conversations` | Staff | Start a conversation with a family (an admin joins when a minor is in it) |
| `POST /broadcasts` | Staff | An announcement to an activity's families |
| `GET /reports/catalogue`; `POST /reports/runs` | Staff | Reports, made on request as PDF, Excel or CSV and never stored |
| `GET`, `POST /badge-classes`; `PUT /badge-classes/{id}` (a draft only); `POST …/publish`, `/retire` | Admin to define; staff to list | Badges (D66) |
| `GET`, `POST /badge-classes/{id}/awards` | Staff | Awards, named as the family's share allows; award one (`{ enrollmentId, narrative }`) |
| `POST /badge-assertions/{id}/revoke` | Admin | Revoke with a reason |
| `GET`, `POST /playlists`; `GET`, `PUT /playlists/{id}` (a draft); `PUT /playlists/{id}/steps` (a draft); `POST …/publish`, `/archive` | Staff (`playlists.author`) | Playlists (D67) |
| `GET /playlists/{id}/submissions`; `POST /playlists/{id}/steps/{stepId}/review` | Staff (`playlists.review_steps`) | Steps waiting for review, named by portfolio display name only; approve, or send back with a note (`{ enrollmentId, approve, note }`) |

Organisations' listings, sites, staff and profile are managed in the web console, not the API.

## Organiser and webhook routes

| Route | |
|---|---|
| `POST /organiser/guardian-links/{id}/attest` | A network organiser confirms a signed consent form (`consent.attest_signed_form`) |
| `POST /webhooks/payments/{provider}` | The payment provider's webhook (`stripe`). Believed only by its signature; applied by the worker |
