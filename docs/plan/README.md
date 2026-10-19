# BrightTime — plan

Citywide out-of-school-time platform for a New York county (seed region:
Kettle County, NY — fictional). Six locales: English, Spanish, Chinese
(Simplified), Bengali, Haitian Creole, Arabic.

**Status (2026-10-19): M1 complete, pending your sign-off.** Every M1
acceptance criterion is met and gated in CI; see the
[M1 report](../m1-report.md). For working on the code, see
[../HANDOFF.md](../HANDOFF.md). The tables below are the plan as approved on
2026-10-09, and some of their counts have moved since.

## Read in this order

| | Document | Why |
|---|---|---|
| 1 | [08-decisions.md](08-decisions.md) | **Start here.** 12 places I disagree or recommend differently, and 12 open questions. Three of them block M1. |
| 2 | [01-architecture.md](01-architecture.md) | Solution and project structure, the 13-module map with its dependency DAG, request pipeline, CI. |
| 3 | [02-data-model.md](02-data-model.md) | Schema conventions, and a guide to the DDL in [`db/schema/`](../../db/schema/). |
| 4 | [03-indexing-and-query-plans.md](03-indexing-and-query-plans.md) | Indexing strategy, the main search query, expected plans — and why the real plans do not exist yet. |
| 5 | [04-roles-and-permissions.md](04-roles-and-permissions.md) | 13 roles × ~90 permissions, with the scope rules that matter more than the roles. |
| 6 | [05-api-surface.md](05-api-surface.md) | API per module, public and authenticated. |
| 7 | [06-screens.md](06-screens.md) | Web and mobile screens, with render mode per screen. |
| 8 | [07-threat-model.md](07-threat-model.md) | 22 threats, controls, where each is enforced, and the compliance mapping. |
| 9 | [09-milestones.md](09-milestones.md) | M1–M8 with acceptance criteria and estimates. |

The SQL:

```
db/schema/     15 files, 177 tables, 188 indexes; 99-rls-and-grants.sql holds
               the whole isolation surface in one reviewable file
db/queries/    search-listings.sql — the finder's reference query
db/explain/    run-explain.sh + 7 scenarios, ready to produce real plans
```

## What blocked M1, and how it was settled

1. **Docker** (Q11): Docker Desktop on the Windows machine. Testcontainers, the
   compose stack and real `EXPLAIN` output all run, and
   [03](03-indexing-and-query-plans.md) now holds measured plans.
2. **EF migrations or SQL-first** (Q12): SQL-first. EF is mapped to
   `db/schema/`, and CI fails on drift either way.
3. **D6, D7, D8**: confirmed with the plan and implemented.

## Deliberate deviation from the prompt

One. Functional area C is split into four modules — `Directory`,
`Enrollment`, `Attendance`, `Notifications` — rather than one. Reasoning in
[D1](08-decisions.md). Everything else in the technical requirements is
implemented as specified, with disagreements stated and left unactioned
pending your answer.
