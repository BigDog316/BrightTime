# M3 report: registration, attendance, messaging, payments and reports

The end-of-milestone report that [plan/09](plan/09-milestones.md) promises.
Written 2026-10-22 at `da81df0`, plus the acceptance test this report added. M3 ran from
2026-10-20 to 2026-10-22, as M3a (registration, D42 to D48) and M3b (attendance, messaging,
payments and reports, D49 to D57). The provider console's screens for all of it followed (D58
to D64).

**Signed off by Sam on 2026-10-22: M3 is closed.** All three acceptance criteria are met and
tested. Two of M3's scope items wait on answers and carry forward: the funder packet (Q10) and SMS
(Q9). Everything else in plan/09's list for M3 is built. Notes below say where a criterion was
checked by hand rather than gated.

## Acceptance

| Criterion | Status | Kept true by |
|---|---|---|
| A family applies once and uses the profile for a second program, with explicit per-org sharing | Met. One profile and one member are entered once and used for two organisations' programmes. Each gets its own share (names to one, names and birth date to the other), sees only that, and can't see the other's application. Withdrawing one share takes the name back from that organisation only. | `M3AcceptanceTests` (added for this report); `ProfileShareApiTests`, `ApplicationApiTests` |
| A staff phone records attendance in airplane mode for an hour and syncs with zero duplicates on replay | Met, with two notes below. Marks are sealed on the phone, survive a cold restart offline, and sync once. A replayed batch comes back `duplicate`, and another phone's mark for the same meeting comes back `conflict`. | `OfflineRosterTests` (marks offline, sent once; open after ten hours, locked after 72); `AttendanceApiTests` (replay gives `duplicate`, `duplicate`, `conflict`, `rejected`); by hand on the Android emulator (`8a654be`) |
| An attempted adult↔minor 1:1 conversation fails at the database in every test route | Met. Every route to a pair is refused at commit by `enforce_safe_conversation()`: removing adults, deleting participants, two leaving together, a minor passed off as an adult, a minor joining as staff. Since D53, a trigger decides who is a minor. | `ConversationSafetyTests`; `MessagingApiTests` |

Notes on the second criterion:
- **The emulator, not a phone.** The run was on the Android emulator (Pixel 7, Android 16)
  against the compose stack, in airplane mode, with a cold restart. It hasn't run on a physical
  phone, and the iOS build needs the Mac.
- **An hour, by the clock.** Nobody waited an hour offline. The tests move the clock instead:
  the roster opens after ten hours offline and locks after 72.

## What was built

**M3a: registration**
- **Family profile** (D42, D43): household, members, contacts and support needs, entered once,
  owned by the family's account, with names and dates of birth encrypted under the tenant's data
  key.
- **Profile shares** (D44): the family chooses which members and fields each organisation sees,
  for how long. An organisation reads them only while the share is live.
- **Forms** (D45): typed questions, versioned, and fixed once published. An application keeps
  the version it answered.
- **Eligibility** (D46): declarative rules (age at the start, grade, neighbourhood), shown to
  families before they apply, and checked when they do.
- **Applications** (D46): drafted, then submitted once under an idempotency key. A second
  child's submission joins the family's share instead of replacing it (D48).
- **Waivers** (D47): published text signed by typed name and hash, by a parent or guardian for
  anyone under 18.
- **Decisions** (D48): review, accept, waitlist and decline, singly or in bulk. Seats are taken
  under a row lock, and freed places are offered to the waitlist.

**M3b: attendance**
- **Records** (D49): rosters per meeting, idempotent offline batch sync, and corrections that
  supersede. Every read is audited.
- **Check-in by QR** (D50): a two-minute badge or a one-minute kiosk code, neither naming
  anyone.
- **Devices** (D51): phones and kiosks register to sync. Revoking one asks it to wipe, which it
  learns on contact.
- **The app's offline roster** (D52): sealed with AES-GCM, the key in the keychain, synced once,
  locked after 72 hours without contact, and wiped when revoked.

**M3b: messaging**
- **Conversations** (D53): staff with families, never an adult alone with a minor. Consent and
  contact restrictions are respected, and an adult's message to a minor is screened. Pushes say
  only who wrote.
- **Broadcasts** (D54): one announcement to an activity's families. Families don't reply and
  never see each other.

**M3b: payments**
- **Fees** (D55): per activity, with sliding-scale price levels by household size and an income
  band the family picks. No income is ever asked for or kept.
- **Paying** (D55): through `IPaymentGateway`, with a manual gateway that admins mark received
  or waive with a reason.
- **Stripe** (D56): payment intents and a client secret for Stripe's own card element, so no card
  touches the platform. Webhooks are believed only by signature, and the worker applies them so a
  status only moves forward.

**M3b: reports**
- **Provider reports** (D57): roster, attendance and participation, as PDF, Excel or CSV. They
  are made on request and never stored.
- Names appear only as families share them. Participation hides counts under the tenant's
  threshold, with complementary suppression, and says what it hid.

**The provider console** (D58 to D64): the same modules the API runs, on the same login, RLS and
actor guard.
- **Reports:** download.
- **Applications:** the queue, single and bulk decisions, places and waitlist offers.
- **Attendance:** a week of meetings, rosters marked as a whole, corrections with a reason and
  their history.
- **Fees and payments:** fees and price levels; waiving or marking received.
- **Forms:** the form builder, with keys taken from the English.
- **Eligibility:** rules with a dry run against a made-up applicant.
- **Waivers:** written, published and locked, then required or optional on listings.
- **Messages:** conversations and announcements.

## How to run it

```bash
docker compose -f deploy/compose.yml down -v
```

```bash
docker compose -f deploy/compose.yml up --build
```

The volume must be rebuilt because the schema and role grants changed in M3. Then browse
`http://kettle.localhost:8000`, sign in at `/dev/sign-in` as an organisation's admin, and open
**Organisations**. Payments use the manual gateway unless `Payments:Stripe:SecretKey` and
`:WebhookSecret` are set. Running outside containers is in [HANDOFF.md](HANDOFF.md).

## Test results

1224 tests, all passing against PostgreSQL 18.6 + PostGIS 3.6.4, up from 1031 at M1's close.
Test methods per project (theories run more than once):

| Project | Methods | | Project | Methods |
|---|---:|---|---|---:|
| Web | 193 | | Infrastructure | 39 |
| Opportunities module | 182 | | SharedKernel | 35 |
| Integration | 106 | | Ui.Shared | 23 |
| API | 96 | | Localisation | 20 |
| Directory module | 40 | | Seed | 18 |
| Identity module | 40 | | E2E (Playwright, axe) | 11 |
| Architecture | 10 | | Platform module | 5 |

Bugs found by tests and runs, not by reading:
- **Undeletable applications** (D46): `waiver_signature`'s set-null keys on an append-only table
  made every application and enrolment undeletable. They now restrict.
- **A second child cut off the first** (D48): a second submission replaced the family's share
  instead of joining it.
- **A minor passed off as an adult** (D53): `is_minor` was the caller's to set. A trigger now
  decides it from the account.
- **Four faults on the emulator** (D52):
  - an offline start signed staff out;
  - the chosen mark wasn't announced;
  - a stray "Sent 0 marks.";
  - counts without plurals.
- **A fee lost its default** (found by the console test, D61): change tracking hid a bulk update,
  so editing a fee in place dropped its default flag.
- **A local offset crashed reports** (D58): Npgsql refuses a non-UTC offset. Periods are now
  normalised in the module.
- **A flaky consent test:** a random antiforgery token on the page sometimes contained "ada". The
  test's child now has a name no token can contain.

## Schema changes during M3

The DDL stayed the source of truth. M3 changed 47 lines in two files:
- **`profile_share`** (D44):
  - `member_ids`, so a share covers chosen members, not the whole family;
  - checks on its known fields, a non-empty member list and an expiry after the grant;
  - an index of each organisation's live shares.
- **`waiver_signature`** (D46): its `enrollment_id` and `application_id` restrict instead of
  setting null. The table is append-only, and a set-null made its parents undeletable.
- **`community.set_participant_minor()`** (D53): a before-insert-or-update trigger on
  `conversation_participant` that sets `is_minor` from the account's kind, and refuses an unknown
  account.

## Known gaps

M3 scope left for answers:
- **The funder packet** waits on Q10: which three reports, ideally actual funder templates.
- **SMS** waits on Q9. Message notifications reach the in-app inbox and push, but not email:
  email delivery of notifications isn't built yet.

Found or left during M3:
- **The offline roster on a physical phone:** run on the emulator only. The iOS build needs the
  Mac.
- **The app's kiosk and badge screens** (D50's codes work through the API and the app's QR
  scanning isn't built yet), and the console's QR scanner and kiosk mode.
- **Refunds started on the platform** (D56): the gateway can, but no route asks it to. A refund
  made in Stripe's dashboard is reported back.
- **Report fonts and licence** (D57): see below.
- **Console gaps:**
  - paging past 500 applications;
  - editing a price level or a rule in place;
  - translating help text and choices in the form builder;
  - an organisation-wide inbox for admins of conversations they aren't in.
- **Object storage** is still undecided, so reports are made on request rather than queued.
- **Translations:** the new strings are machine drafts (Q13), marked in each catalogue's
  `@@drafts`.

## Decisions needed

None blocked closing M3. These shape what carries forward, and launch:

| | Question | Needed by |
|---|---|---|
| Q10 | The three reporting consumers, ideally funder templates | The funder packet |
| Q9 | SMS provider and number | SMS notifications |
| D56 | What happens when a waived fee is paid anyway (now logged, not changed); whether organisations must be paid directly (Stripe Connect) | Live payments |
| D57 | QuestPDF's licence (Community is free only under US$1M revenue); fonts for Bengali, Chinese and Arabic names | Reports in production |
| D46 | How organisations, listings and community reports are deleted against append-only tables | Before anything deletes them |
| Q4, Q5 | Retention periods; deletion versus audit | Before production data |
| Q13 | Who reviews the translations | Launch |
| — | Where media and generated files are stored | Queued reports, listing images |
