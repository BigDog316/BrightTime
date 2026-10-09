# 06 — Main screens, web and mobile

Reading level target: Flesch-Kincaid grade 6–8 on every youth-facing string,
checked in CI against the `en` resource file. WCAG 2.2 AA throughout, verified
by axe-core on every public route in the E2E suite plus a manual keyboard and
screen-reader pass per milestone.

Legend: **SSR** = static server-rendered, works with JavaScript disabled ·
**Island** = interactive component on an otherwise static page ·
**Interactive** = fully interactive page.

## Public web (anonymous)

| Screen | Mode | Notes |
|---|---|---|
| Home / personalised feed | SSR + islands | Anonymous: featured collections, what's on this week, neighbourhood picker. Signed-in: feed with a `why` line per card. |
| Search results — list | **SSR** | A GET form. Filters are query parameters, pagination is links. This page must work on a 2G connection with JS off. |
| Search results — map | Island | MapLibre + OSM, clustered pins, list stays in sync. `?lowbw=1` serves the list only. |
| Listing detail | SSR | What it is, who runs it, when, where, cost, ages, languages, accommodations, meals, transport, how to register. Transit and walking time from the chosen anchor. Add to calendar (.ics). Save. Share. Report. |
| Collection page | SSR | Organiser-curated, with a curator note per item. |
| Neighbourhood page | SSR | Organiser-authored blocks + live listing counts + local contacts + transit. |
| Organisation page | SSR | Mission, sites, current listings, languages served, accessibility. |
| Playlist page | SSR | Steps, which are required, estimated hours, the badge at the end. |
| Badge verification | SSR | Resolves a `verifySlug`: criteria, issuer, issue date, revocation status, the signed credential. No recipient identity beyond what the credential itself carries. |
| Public portfolio | SSR | Only when visibility is `public`/`link_only`. Display name, not legal name. |
| Showcase | SSR | Approved youth work. |
| Resident survey | SSR | Fully anonymous, no account. |
| Sign in / register | SSR | Passkey first, password second, external IdP third. |
| Accessibility statement, privacy notice, parents' bill of rights | SSR | The last one is a legal requirement under NY Ed Law 2-d and is linked from every consent screen. |

## Youth (13+)

| Screen | Mode | Notes |
|---|---|---|
| My feed | Interactive | Recommendations with visible reasons and a "less like this" control that edits declared interests rather than training a hidden model. |
| Saved items | Interactive | Groups by deadline. |
| My schedule | Interactive | Enrollments, upcoming sessions, deadlines; .ics subscribe. |
| My portfolio | Interactive | Programs, hours, badges, skills, projects. Visibility control per item. Export. |
| Badge detail | SSR | Criteria, evidence, public link, what it unlocks. |
| My playlists | Interactive | Progress, next step, evidence upload. |
| Interests & goals | Interactive | The only ranking inputs the youth controls, stated as such. |
| My groups | Interactive | Cohort spaces, challenges, mentor feedback. |
| Notifications | Interactive | |
| Who has seen my record | Interactive | The transparency screen: every read, by whom, with what basis and when. Unusual, and the single screen I would most defend in a design review. |
| Privacy & data | Interactive | Export, delete, revoke a provider's access, see consents. |

## Child (under 13) — guardian-managed

No independent sign-in. A guardian switches into a child context. The child's
view is read-only and shows only: my programs, my badges, my schedule. No
posting, no messaging, no uploads, no SMS — enforced by
`identity.youth_capability`, not by hiding buttons.

## Guardian

| Screen | Mode | Notes |
|---|---|---|
| Family dashboard | Interactive | One card per child: enrollments, upcoming, deadlines, waitlists, unsigned waivers, unpaid fees. |
| Add a child | Interactive | Triggers the verifiable parental consent flow. |
| Consent centre | Interactive | Per child, per scope, with the exact disclosure text that was agreed, the date, and a revoke button. Revocation is immediate and cascades to capability. |
| Family profile | Interactive | Entered once. Shows which organisations can see which fields, and lets you revoke. |
| Applications | Interactive | One flow across providers. Status per application. |
| Child's activity | Interactive | Attendance, hours, badges, mentor feedback. |
| Messages | Interactive | Org-to-family threads. Always shows which staff can read the thread. |
| Verify a guardian link | Interactive | Email-plus, card verification, ID match, or signed form. |

## Provider console

| Screen | Mode | Notes |
|---|---|---|
| Organisation profile | Interactive | Vetting status, outstanding checklist items, document expiries. |
| Sites | Interactive | Map pin, address, hours, accessibility features, transit note. |
| Staff & roles | Interactive | Invite, role, site assignment, clearance status and expiry (status only — the console cannot display or store a report). |
| Listings | Interactive | Drafts, pending review, published, archived. Seasonal clone. Scheduled publication. |
| Listing editor | Interactive | Tabbed: basics, audience, schedule (with RRULE builder and an occurrence preview), locations, cost, registration, accessibility, media with required alt text, translations. Live "how this looks to a family" preview. |
| Bulk import | Interactive | CSV or HSDS upload → dry run → row-level error report → apply. |
| Registration forms | Interactive | Form builder, eligibility rules, waivers, fees and sliding scale. |
| Applications | Interactive | Queue with eligibility flags, bulk decide, waitlist management and offers. |
| Rosters | Interactive | Per listing, per occurrence. |
| Attendance | Interactive | Check-in list, QR scanner, kiosk mode, correction history. |
| Messaging | Interactive | Broadcast to enrolled/waitlisted families. Thread view. |
| Spaces | Interactive | Cohort spaces, challenges, feedback, showcase approvals. |
| Surveys | Interactive | Youth feedback, outcome follow-ups. |
| Quality self-assessment | Interactive | Domain-by-domain instrument, scored, with a trend line and an improvement plan. |
| Reports | Interactive | Participation, attendance, demographics (suppressed), funder packets. PDF and Excel. |
| Badges | Interactive | Define a badge class, issue, revoke, see verification traffic. |
| Gigs | Interactive | Post, review applicants with match explanations, approve hours. |

## Organiser console

| Screen | Mode | Notes |
|---|---|---|
| Review queue | Interactive | Organisations and listings awaiting approval. Side-by-side diff on resubmission. |
| Vetting workflow | Interactive | Checklist, documents, decision with a reason. |
| Taxonomy | Interactive | Categories, interests, skills, accommodations, per locale. |
| Collections & featured | Interactive | Curation, scheduling. |
| Neighbourhood pages | Interactive | Block editor per locale. |
| Moderation queue | Interactive | Severity-ordered. `grooming_concern` and `self_harm` sit in a separate critical lane with a 1-hour acknowledgement SLA and an escalation path. Full action history per target. |
| Ecosystem dashboard | Interactive | Youth served, participation by neighbourhood/age/category, utilisation, unmet demand, retention. Suppression visible. |
| Landscaping | Interactive | Asset map, field campaigns, review queue, supply-vs-need layers, desert scores with driver breakdown. |
| Field campaigns | Interactive | Create, assign areas, monitor, review observations. |
| Surveys | Interactive | Resident and youth instruments, response analysis. |
| Workforce | Interactive | Mentor directory, training requirements, curriculum vetting. |
| Tenant settings | Interactive | Branding, locales, geography, feature flags, suppression thresholds, retention policies. |
| Data requests | Interactive | Subject export/delete queue with due dates. |
| Audit explorer | Interactive | Filter by actor, subject, action, date. |
| District connections | Interactive | Off by default. Cannot be enabled without DPA reference, signed date, purpose and a field allowlist. |
| API clients & widgets | Interactive | Keys, scopes, origins, widget builder with live embed preview. |

## Civic / funder

A deliberately small surface: ecosystem overview, neighbourhood comparison,
equity view (supply per 1,000 youth by neighbourhood against reported
barriers), investment view (seats funded, utilisation, cost per seat), and
report downloads. Read-only, suppressed, no drill-through to a person.

## Mobile (MAUI Blazor Hybrid)

| Screen | Native capability |
|---|---|
| Onboarding: age, interests, anchor (neighbourhood or school) | — |
| Feed | — |
| Search + filter sheet | — |
| Map | Location (foreground only, on demand) |
| Listing detail | Calendar, share |
| Saved | Offline read of saved items |
| My schedule | Local notifications |
| Portfolio & badges | — |
| Playlists | Camera for evidence upload |
| Group feed | — |
| Notifications | Push |
| **Staff: roster check-in** | Offline SQLCipher store, sync queue |
| **Staff: QR scanner** | Camera |
| **Kiosk mode** | Locked single-purpose task, rotating token |
| **Field collection** | GPS, camera, offline queue, area pack download |
| Mentor: gigs & hours | — |

Mobile specifics worth stating now:

- Location is requested only when the user taps "near me", never at launch,
  and never in background. There is no always-on location permission in
  either store listing.
- The offline roster holds the minimum: enrollment id, display name, and the
  occurrence. No DOB, no support needs, no contact details. It purges on
  `wipe_requested_at`, and refuses to open after 72 hours without server
  contact.
- Youth accounts get no "remember me" by default and a 30-minute idle
  timeout, because the device is frequently shared.
- Store submission needs: Apple privacy manifest and data-use disclosures,
  Google Data Safety form, both declaring no tracking, no third-party ads,
  no data sale. Kids-category compliance review for both stores is an M8
  checklist item, not an afterthought.
