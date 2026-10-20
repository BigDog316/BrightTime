# 04 — Roles and permission matrix

## The model

Three orthogonal axes. Conflating them is how youth-data platforms leak.

1. **Tenant** — which region. Enforced in RLS *and* in the application.
2. **Role** — what kind of actor. Tenant-scoped (`identity.app_role`), so a
   tenant can rename or subset roles, but the permission *vocabulary* is
   defined in code.
3. **Scope** — which organisation, space, or youth record this particular
   actor may touch right now. Carried in `directory.org_membership`,
   `community.space_member` and `identity.youth_access_grant`.

A permission check is always `(role grants P) AND (scope covers this object)`.
Role alone never grants access to a youth record. This is the single most
important line in this document: `ProviderAdmin` is not a licence to read
youth, it is a licence to read *the youth their organisation currently
serves*, as recorded in `youth_access_grant` with a basis and an end date.

## Roles

| Code | Role | Notes |
|---|---|---|
| `AN` | Anonymous visitor | No account. Full read of the public finder. |
| `Y13` | Youth, 13+ | Self-registered or org-verified. Owns their portfolio. |
| `C12` | Child, under 13 | Parent-managed. No password. Capability-gated by `identity.youth_capability`. |
| `GU` | Guardian | Verified link to one or more children. |
| `PS` | Program staff | Member of an organisation, `org_role = 'staff'`. |
| `PA` | Provider admin | `org_role IN ('owner','admin')`. |
| `ME` | Mentor | `org_role = 'mentor'` and/or a `workforce.mentor_profile`. |
| `NO` | Network organiser | The intermediary running the tenant. |
| `MO` | Moderator | Safety queue. Often also `NO`, separated so it can be delegated. |
| `CV` | Civic / funder viewer | Read-only, suppressed aggregates only. |
| `RE` | Researcher | De-identified export only, under an approved DUA. |
| `FC` | Field collector | Trained youth or staff doing landscaping fieldwork. |
| `PX` | Platform admin | Cross-tenant. Operations, not content. |

A person holds several: a parent who also coaches is `GU` + `PS`; a college
mentor is `Y13`-grown-up + `ME` + `FC`.

## Permission matrix

`●` granted · `◐` granted within scope (own org / own children / own record) ·
`△` granted but aggregate-only and suppressed · `—` denied

### Opportunity finder

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `listings.search` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `listings.view_published` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `listings.save` | — | ● | ● | ● | ● | ● | ● | ● | ● | — | — | ● | — |
| `listings.create` | — | — | — | — | ◐ | ◐ | — | ● | — | — | — | — | — |
| `listings.submit_for_review` | — | — | — | — | ◐ | ◐ | — | ● | — | — | — | — | — |
| `listings.publish` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `listings.review` (approve/reject) | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `listings.bulk_import` | — | — | — | — | — | ◐ | — | ● | — | — | — | — | — |
| `collections.manage` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `neighbourhood_pages.manage` | — | — | — | — | — | — | — | ● | — | — | — | — | — |

### Organisations and staff

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `orgs.view_public` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `orgs.apply` | ● | — | — | ● | ● | ● | ● | ● | — | — | — | — | — |
| `orgs.edit` | — | — | — | — | — | ◐ | — | ● | — | — | — | — | — |
| `orgs.vet` / `orgs.approve` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `orgs.suspend` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `sites.manage` | — | — | — | — | ◐ | ◐ | — | ● | — | — | — | — | — |
| `staff.invite` | — | — | — | — | — | ◐ | — | ● | — | — | — | — | — |
| `clearances.record` | — | — | — | — | — | ◐ | — | ● | — | — | — | — | — |
| `clearances.view_status` | — | — | — | — | — | ◐ | ◐ own | ● | — | — | — | — | — |

### Enrollment and youth records

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `family_profile.manage` | — | ◐ own | — | ◐ own | — | — | — | — | — | — | — | — | — |
| `family_profile.share` | — | ◐ own | — | ◐ own | — | — | — | — | — | — | — | — | — |
| `applications.submit` | — | ◐ self | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `applications.view` | — | ◐ own | — | ◐ children | ◐ org | ◐ org | — | — | — | — | — | — | — |
| `applications.decide` | — | — | — | — | ◐ org | ◐ org | — | — | — | — | — | — | — |
| `enrollments.view` | — | ◐ own | — | ◐ children | ◐ org | ◐ org | — | — | — | — | — | — | — |
| `youth_record.read` | — | ◐ own | — | ◐ children | ◐ **grant** | ◐ **grant** | — | — | ◐ report | — | — | — | — |
| `support_needs.read` | — | ◐ own | — | ◐ children | ◐ **grant+share** | ◐ **grant+share** | — | — | — | — | — | — | — |
| `waivers.sign` | — | ◐ 18+ | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `payments.initiate` | — | ◐ 18+ | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `payments.waive` | — | — | — | — | — | ◐ org | — | ● | — | — | — | — | — |

`◐ grant` = requires a live row in `identity.youth_access_grant` for
(this org, this youth). `◐ grant+share` additionally requires
`enrollment.profile_share` covering the field and
`family_member_support_need.share_with_providers = true`.

`PX` has **no** youth-record read. Platform administration is tenancy,
deployments and incident response; it is not a licence over children's data.
Break-glass access exists as a separate, time-boxed, dual-authorised grant
that writes an `audit_event` with `reason` mandatory
(`ck_audit_reason_required`) and notifies the tenant's organiser and DPO.

### Attendance

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `attendance.record` | — | — | — | — | ◐ org | ◐ org | ◐ assigned | — | — | — | — | — | — |
| `attendance.view_roster` | — | — | — | — | ◐ org | ◐ org | ◐ assigned | — | — | — | — | — | — |
| `attendance.view_own` | — | ● | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `attendance.export` | — | — | — | — | — | ◐ org | — | △ | — | — | — | — | — |
| `attendance.offline_sync` | — | — | — | — | ◐ org | ◐ org | ◐ assigned | — | — | — | — | — | — |

### Learning, badges, playlists

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `portfolio.view_own` | — | ● | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `portfolio.view_public` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `portfolio.export` | — | ◐ own | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `portfolio.set_visibility` | — | ◐ own | — | ◐ children | — | — | — | — | — | — | — | — | — |
| `badges.define` | — | — | — | — | — | ◐ org | — | ● | — | — | — | — | — |
| `badges.issue` | — | — | — | — | ◐ org | ◐ org | ◐ assigned | ● | — | — | — | — | — |
| `badges.revoke` | — | — | — | — | — | ◐ org | — | ● | — | — | — | — | — |
| `badges.verify` (public) | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `playlists.author` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `playlists.review_steps` | — | — | — | — | ◐ org | ◐ org | ◐ assigned | — | — | — | — | — | — |

### Community and moderation

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `spaces.post` | — | ◐ member | — | — | ◐ member | ◐ member | ◐ member | — | — | — | — | — | — |
| `spaces.view` | — | ◐ member | — | ◐ children's | ◐ org | ◐ org | ◐ member | — | ● | — | — | — | — |
| `spaces.manage_membership` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `submissions.give_feedback` | — | — | — | — | ◐ org | ◐ org | ◐ member | — | — | — | — | — | — |
| `messages.send_to_family` | — | — | — | — | ◐ org | ◐ org | — | — | — | — | — | — | — |
| `messages.send_to_youth_1to1` | — | — | — | — | **never** | **never** | **never** | **never** | **never** | — | — | — | — |
| `content.report` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | — | ● | — |
| `moderation.triage` | — | — | — | — | — | — | — | ● | ● | — | — | — | — |
| `moderation.remove` | — | — | — | — | — | ◐ org | — | ● | ● | — | — | — | — |
| `moderation.suspend_user` | — | — | — | — | — | — | — | ● | ● | — | — | — | — |
| `showcase.approve` | — | — | — | — | ◐ org | ◐ org | — | ● | ● | — | — | — | — |

`messages.send_to_youth_1to1` has no grantable row anywhere. It appears in the
matrix as a permanent `never` because the capability must be visibly absent
rather than merely unassigned — and the data model cannot express it
(`ck_conversation_minor_safety`, `community.enforce_safe_conversation()`).

### Workforce

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `mentor_profile.manage_own` | — | ◐ 18+ | — | ● | ● | ● | ● | ● | — | — | — | ● | — |
| `mentors.search` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `gigs.post` | — | — | — | — | — | ◐ org | — | ● | — | — | — | — | — |
| `gigs.apply` | — | — | — | — | — | — | ● | — | — | — | — | — | — |
| `gigs.decide` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `hours.log` | — | — | — | — | — | — | ◐ own | — | — | — | — | — | — |
| `hours.approve` | — | — | — | — | ◐ org | ◐ org | — | — | — | — | — | — | — |
| `courses.author` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `curriculum.vet` | — | — | — | — | — | — | — | ● | — | — | — | — | — |

### Landscaping

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `assets.view_verified` | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `field.collect` | — | — | — | — | — | — | — | — | — | — | — | ● | — |
| `field.review` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `surveys.author` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `surveys.respond` | ● | ● | — | ● | ● | ● | ● | ● | ● | — | — | ● | — |
| `surveys.view_responses` | — | — | — | — | — | ◐ org △ | — | ● △ | — | △ | △ | — | — |

### Dashboards, reporting, platform

| Permission | AN | Y13 | C12 | GU | PS | PA | ME | NO | MO | CV | RE | FC | PX |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `dashboard.provider` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `dashboard.ecosystem` | — | — | — | — | — | — | — | ● | — | △ | — | — | — |
| `reports.run_provider` | — | — | — | — | ◐ org | ◐ org | — | ● | — | — | — | — | — |
| `reports.run_ecosystem` | — | — | — | — | — | — | — | ● | — | △ | — | — | — |
| `research_export.request` | — | — | — | — | — | — | — | ● | — | — | ● | — | — |
| `research_export.approve` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `tenant.settings` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `tenant.feature_flags` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `taxonomy.manage` | — | — | — | — | — | — | — | ● | — | — | — | — | — |
| `district_connection.configure` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `api_clients.manage` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `audit.read` | — | ◐ own | — | ◐ children | — | ◐ org | — | ● | ● | — | — | — | ● |
| `subject_request.fulfil` | — | — | — | — | — | — | — | ● | — | — | — | — | ● |
| `consent.attest_signed_form` | — | — | — | — | — | — | — | ● | — | — | — | — | — |

`audit.read ◐ own` is the transparency right: a 13+ youth can see who read
their record and why. A guardian can see the same for a linked child. This is
a feature, not a side effect of having an audit log.

## Implementation

Permissions are `const string` on static classes per module
(`Permissions.Listings.Publish = "listings.publish"`). Roles map to permission
sets in `identity.app_role_claim`, seeded per tenant and editable by `NO`
within a fixed ceiling — a tenant cannot grant itself a permission the code
does not define, and cannot grant `youth_record.read` without scope.

Authorisation requirements, all in `BrightTime.Modules.Identity`:

| Requirement | Asserts |
|---|---|
| `TenantRequirement` | request tenant == token tenant == `brighttime.tenant_id` |
| `PermissionRequirement(p)` | actor's role set grants `p` |
| `OrgScopeRequirement(p)` | above, plus live `org_membership` in the target org with sufficient `org_role` |
| `YouthRecordRequirement(fields)` | live `youth_access_grant` covering (org, youth) and the requested fields; writes the audit event |
| `GuardianOfRequirement` | verified `guardian_link`, not revoked, not restricted |
| `SelfOrGuardianRequirement` | actor is the subject, or a verified guardian of it |
| `CapabilityRequirement(c)` | `youth_capability.<c>` is true — the COPPA gate |
| `SpaceMembershipRequirement(role)` | live `space_member` with the role |
| `ClearanceCurrentRequirement(kinds)` | `staff_clearance` cleared and unexpired for every kind |
| `NoRestrictedContactRequirement` | no `restricted_contact` between actor and subject, either direction |
| `SuppressedAggregateOnlyRequirement` | response must pass `analytics.suppress`; blocks any row-level path |

Two cross-cutting rules enforced by a filter rather than per endpoint:

1. Any endpoint touching a youth record must declare
   `[YouthRecord(Fields = …)]`. A startup test enumerates every endpoint,
   checks whether its response graph contains a type marked `[YouthData]`, and
   **fails startup** if the attribute is missing. Forgetting the audit entry
   becomes a boot failure rather than a silent gap.
2. Any endpoint returning an aggregate must declare `[Suppressed(Family = …)]`.
   Same test, same failure mode.
