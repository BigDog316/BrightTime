# M5 report: the social learning space, with moderation

The end-of-milestone report that [plan/09](plan/09-milestones.md) promises.
Written 2026-11-28. M5 ran from 2026-11-27 to 2026-11-28 in fourteen slices, D114 to D127:
- moderation first: the queue (D114), reports (D115), the history (D116), screening what young people
  write (D117, D118), and muting and suspending (D119);
- then the space: spaces and posts (D120), reporting posts (D121), challenges (D122), rubrics (D125);
- then media safety (D123), and the showcase (D124, D126, D127).

The user guides and technical documentation were updated alongside each slice.

**Not yet signed off.** The three acceptance criteria are met and tested, within the limits each
row states. What's open is decisions (abuse classification, the legal path for child sexual abuse
material), and work that waits for M6 (mentors). The sections below give the detail.

## Acceptance

| Criterion | Status | Kept true by |
|---|---|---|
| Nothing youth-authored reaches another user before screening | Met. Everything a young person writes is checked for personal information on our own servers (D117) before anyone else reads it. **Held** for a moderator, and they're told why: messages (D117), and posts and replies in a space (D120). **Refused**, and they're told what to take out: challenge work when it's sent (D122); a project's title, description and picture descriptions when it's sent for approval; a playlist reflection or step note; their portfolio's name, headline, bio and link name (D118). The showcase shows only work that passed and staff approved, and a caption staff write is screened too (D124). A screen that errs holds or refuses: it fails closed. **Not screened, by decision:** a report's own words (moderators only), goals (private), quiz answers (choices), and application and household details, which are personal information given to an organisation on purpose. **Pictures** aren't classified (Q8): they're virus-scanned and re-encoded (D123), and a young person's reaches only the staff reviewing it before it's approved (D104) | `LocalPiiScreenTests`, `MessageScreenTests`, `MessagingApiTests`, `HeldMessagesPageTests`, `SpacesTests`, `SpacesApiTests`, `ChallengesTests`, `LearnerRecordApiTests`, `PlaylistApiTests`, `PortfolioApiTests`, `ShowcaseTests` |
| The moderation audit trail reconstructs every decision | Met. Every decision is its own row: screening's (`screening_result`; Learning's forms, `screening.checked` in the audit log), each report, each moderator's decision in the append-only `moderation_action` with who and their note, each mute or suspension (`person_restriction`), and how an escalation ended. A message's or post's **History** (D116, D121) rebuilds its story in order. A county take-down of a showcase item is a `moderation_action` with its reason too (D127, from this report). Not recorded, because nothing was published: staff writing that screening refused (a brief, a rubric, a caption) | `ModerationHistoryTests`, `PostReportsTests`, `ReportQueueTests`, `RestrictionTests`, `ShowcaseTests` |
| A mandated-reporter escalation produces a documented, testable workflow | Met in the platform. Grooming and self-harm reports are critical: every moderator is emailed, and one waiting more than an hour is marked (D115). **Escalate** needs a note saying what was done and who was told, and can remove the message too. The report then waits under **Escalated, waiting for the outcome** until a moderator records how it ended, such as the agency's reference (D116). Each step is in `moderation_action` and the history, and the workflow is in the [organisers' guide](user/organisers.md#moderation-reports). The procedure itself (whom to call and how) is the county's, outside BrightTime, and the guide says to follow it first | `ReportQueueTests`, `ModerationHistoryTests`, `CriticalReportMailTests` |

## plan/09's M5 scope

| Scope | Status |
|---|---|
| Spaces, membership with the ≥2-adults invariant, posts, comments | Built (D120, D121). The database refuses a space with young people and fewer than two adult staff (`enforce_safe_space()`); comments are replies |
| Challenges, submissions, rubrics | Built (D122, D125) |
| Mentor feedback | **Waits for M6**: there are no mentors yet. Staff give feedback (D122) |
| Showcase with consent | Built (D124, D126): staff propose, the young person says yes, a guardian's consent where one decides |
| Screening: PII detection | Built, local, always on (D117, D118) |
| Screening: abuse classification | **Not built**: Q8's hosted classifier is open |
| Hold, redact, block | Hold and refuse are built. **Redaction isn't**: a moderator releases or removes |
| Screening results recorded | Built (D116, D117; D118 in the audit log) |
| Report from anywhere | **Partly**: messages (D115) and posts (D121). Not yet projects, challenge work, showcase items or portfolios |
| Severity queue with a critical lane, triage, actions | Built (D114, D115, D120) |
| Append-only history | Built (D116, D121, D127) |
| User suspension | Built for adults (D119), from a message's history |
| Escalation path | Built (D115, D116) |
| Media: scanning, EXIF stripping, re-encoding, SVG rejection | Built (D103, D123). **CSAM detection isn't** (T19: a provider and a legal path, not a control we can claim) |

## What was built

**Moderation** (D114 to D116, D119, D121, D127), at `/organiser/moderation` for network organisers
and moderators (`moderation.triage`), never of their own organisation:
- **Reports first, critical at the top:** take one on, then remove, dismiss or escalate it, with a
  note.
- **Escalations wait for their outcome.**
- **Then what screening held**, messages and posts, released or removed with a reason.
- **History:** each message's and post's whole story, and the county's recent decisions.
- **Muting and suspending an adult**, for 24 hours, 7 days or 30 days, and lifting either. A
  suspension signs them out within minutes, and the worker ends it.

**Screening** (D117, D118): `LocalPiiScreen` finds phone numbers, email addresses, street
addresses, social handles and links, with each span's place and kind. Nothing leaves the platform,
and no model reads the text. D53's contact-exchange screen still checks what adults write where
young people read it.

**Spaces** (D120, D121), one per activity:
- Staff choose the space's staff. Its enrolled young people of 13 and over join once two adults
  are in it.
- Posts and replies are screened. A removed post with replies stays as a line.
- Any member reports a post.
- In the console (the space's page) and the app (**Groups**).

**Challenges and rubrics** (D122, D125):
- Staff set a challenge, with a brief, a due date and a rubric if they like.
- Young people save their work, and send it once it passes screening.
- Staff comment, accept it, or send it back, and score every criterion or none. The young person
  sees the weighted total.
- Work is seen only by its author and the space's staff.

**Media safety** (D123):
- Every upload is virus-scanned by clamd, as it came, before it's kept. If the scan finds something
  or can't be done, the upload is refused, and the refusal is audited.
- A picture that passes is made again from its pixels, upright and at most 4,096 pixels a side, so
  nothing else of the file is kept.
- A host outside Development without a scanner doesn't start. The worker scans pictures kept
  before.

**The showcase** (D124, D126, D127):
- An organisation's staff propose accepted challenge work or an approved project, with a screened
  caption.
- The young person is told (D126), and says yes or no in the app. It's shown under their
  portfolio's name, without their family name, while they may publish to the showcase.
- The public page is `/showcase`. Its author, the organisation or the county takes an item down.

## How to run it

```bash
docker compose -f deploy/compose.yml down -v
```

```bash
docker compose -f deploy/compose.yml up --build
```

Rebuild the volume: M5 changed the schema (below). Compose now runs ClamAV. Its first start
downloads the virus signatures, which takes a minute or two, and until it answers, uploads are
refused as unchecked.
- Sign in at `/dev/sign-in`:
  - as `moderator@kettle.example.org` for **Moderation**, its history and the showcase's
    take-downs;
  - as an organisation's admin for its **Spaces**, **Rubrics** and **Showcase**.
- Young people use the app: **Groups**, **Portfolio** › **Showcase**.
- Outside Development, set `Media:Clamd:Host` in all three hosts
  ([configuration](technical/configuration.md#media)).

**Not run by hand for M5:** the compose stack since D119, and a real clamd. The scanner is tested
against a stand-in that speaks clamd's protocol (`ClamdScannerTests`); the first `up` checks the
rest.

## Test results

1,455 tests, all passing against PostgreSQL 18.6 + PostGIS 3.6.4, in 1,018 test methods:

| Project | Methods | | Project | Methods |
|---|---:|---|---|---:|
| Web | 234 | | Directory module | 44 |
| Opportunities module | 188 | | Identity module | 42 |
| API | 120 | | SharedKernel | 39 |
| Ui.Shared | 117 | | Localisation | 20 |
| Integration | 117 | | Seed | 18 |
| Infrastructure | 45 | | Platform module | 13 |
| E2E (Playwright, axe) | 11 | | Architecture | 10 |

Each M5 slice's flow runs end to end as the real logins, with row-level security and the actor
guard in force:
- `HeldMessagesPageTests`, `ReportQueueTests`, `ModerationHistoryTests` and `RestrictionTests`;
- `SpacesTests`, `PostReportsTests`, `ChallengesTests` and `RubricsTests`;
- `MediaSafetyTests`, `ShowcaseTests` and `ShowcaseApiTests`.

Found by tests and runs, not by reading:
- **A post its author took down hid its replies** (found in D121). It stays as a line now, as a
  removed one does.
- **The showcase's consent check was stricter than consent itself** (found in D124). The schema
  asked for a guardian's consent for any minor's published work. That would have refused a
  teenager without a guardian link, who consents for themselves everywhere else. The check now
  follows the capability calculator (`guardian_managed`).
- **A county take-down of a showcase item wasn't in the moderation record** (found writing this
  report). It is now, with a required reason (D127).

## Schema changes during M5

The DDL stayed the source of truth:
- **`identity.app_user.suspended_until`**, and **`community.person_restriction`** (D119).
- **`ux_space_listing_active`**, and the triggers `set_space_member_minor()` and
  `enforce_safe_space()` (D120).
- **`community.challenge`** `created_at`, `closed_at` and `ix_challenge_space`;
  **`submission.updated_at`**; **`feedback.outcome`** (D122).
- **`platform.media_object.scanned_at`**, with `ix_media_object_unscanned` (D123).
- **`community.showcase_item`**, reshaped (D124):
  - the work as approved;
  - `guardian_managed` replaces `author_is_minor`;
  - who took it down and why;
  - one item per piece of work;
  - the table joins the actor guard.

  With it, the definer function **`community.public_showcase()`**.
- **`community.rubric`** `created_by`, `updated_at` and `archived_at`, with name, weight, text and
  score checks (D125).
- **`ck_notification_kind`** gains `showcase_request` (D126).

D114 to D118 and D121 used tables the approved schema already had. D127 writes `moderation_action`
with a new `target_kind`, which the table already allowed.

## Known gaps

M5 scope not built, from the table above:
- abuse classification;
- redaction;
- reporting anything but messages and posts;
- mentor feedback (M6);
- CSAM detection.

Left for later, by decision:
- **Young people's pictures** in posts, messages and challenge work (`post_media`,
  `message_media`). D123's media safety is what they waited on.
- **Notices** for challenge feedback, a new challenge, and a showcase take-down. Only the showcase
  proposal tells anyone (D126).
- **Muting or suspending from a post's history**: it's offered only from a message's history.
  Young people's accounts aren't muted or suspended.
- **A rubric chosen after a challenge is set.**
- **Scores in the portfolio or its exports.**
- **Pictures on the showcase**: a project's pictures are private (D104).
- **plan/05's `GET /moderation/history`**: the console has the history; the API doesn't.

## Decisions needed

| | Question | Needed by |
|---|---|---|
| Q8 | The hosted abuse classifier: who provides it, under what data-processing agreement, behind the tenant flag | Abuse classification; closing that scope item |
| T19 | CSAM: a hash-matching provider, the legal reporting path, and the person at the county named for it. Counsel should confirm a provider's reporting duty | Young people's pictures in posts and messages; launch |
| — | The county's mandated-reporter procedure, in its own words, to show beside **Escalate** (today the guide says to follow it) | Launch |
| Q13 | Who reviews the translations (M5's new strings are machine drafts in `@@drafts`) | Launch |
| — | Carried from M4: a host the internet can reach, for the OB 3.0 validator check | Signing off M4 |
