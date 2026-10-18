# BrightTime — plan

Citywide out-of-school-time platform for a New York county (seed region:
Kettle County, NY — fictional). Six locales: English, Spanish, Chinese
(Simplified), Bengali, Haitian Creole, Arabic.

**Status: plan approved; M1 under way.** For current state, see
[../HANDOFF.md](../HANDOFF.md). The sections below are the original
plan-approval text, and the blockers they list have since been settled
in [08-decisions.md](08-decisions.md).

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

## The three things blocking M1

1. **Docker is not installed on this machine** (nor `psql`). No Testcontainers,
   no local PostGIS, and no real `EXPLAIN` output — which is why
   [03](03-indexing-and-query-plans.md) states predicted plans and labels them
   as predictions rather than inventing numbers. Docker Desktop, OrbStack or
   Colima; your preference. (Q11)
2. **EF migrations or SQL-first?** The DDL uses partial indexes with
   `coalesce`, `UNIQUE NULLS NOT DISTINCT`, generated columns, constraint
   triggers, RLS policies and partitioning — several of which EF cannot
   express. I lean SQL-first with EF mapped to it and CI failing on drift
   either way. (Q12)
3. **Confirm D6, D7, D8** — Postgres FTS cannot stem four of your six
   languages; the public finder should be static SSR that works with
   JavaScript off; no youth home address is ever persisted.

## Deliberate deviation from the prompt

One. Functional area C is split into four modules — `Directory`,
`Enrollment`, `Attendance`, `Notifications` — rather than one. Reasoning in
[D1](08-decisions.md). Everything else in the technical requirements is
implemented as specified, with disagreements stated and left unactioned
pending your answer.
