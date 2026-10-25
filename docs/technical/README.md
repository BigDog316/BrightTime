# Technical documentation

How BrightTime is built and run, as of D66 (2026-10-22). This set describes the system as it is.
For why it is that way, follow the decision numbers (D1 to D66) into
[plan/08-decisions.md](../plan/08-decisions.md). For the plan as approved, see
[plan/](../plan/README.md).

| Document | Read it to… |
|---|---|
| [Architecture](architecture.md) | understand the three hosts, the modules, how modules talk, tenancy and the request pipeline |
| [Data and security](data-and-security.md) | understand the schema, row-level security, encryption and keys, sign-in, permissions, audit and the youth-safety rules the database enforces |
| [API reference](api.md) | call `/api/v1`: conventions, errors, paging, idempotency, and every route |
| [Configuration](configuration.md) | configure a host: every setting, its default, and which hosts read it |
| [Operations](operations.md) | run the stack, apply the schema, seed, keep the worker healthy, rotate keys, set up payments, push and mail |
| [Development](development.md) | build, test, and keep CI green, including the conventions the tests enforce |

## The system in one paragraph

BrightTime is a modular monolith on .NET 10 and PostgreSQL 18 with PostGIS. Three hosts share one
database: **Web** (Blazor static server rendering: the public finder and the consoles), **Api**
(`/api/v1`, used by the MAUI app and integrations) and **Jobs** (the worker: outbox delivery and
background jobs). Each module owns one schema and one EF Core context, and talks to other modules
only through their `.Contracts` assemblies or through outbox messages. Tenants are told apart by
host name; row-level security isolates them in the database, and a second, restrictive policy
keeps every youth-record table closed to a request with no signed-in actor. The DDL in
`db/schema/` is the source of truth, and EF is mapped to it.

## Status by area

| Area | Built | Milestone |
|---|---|---|
| Tenancy, finder, consoles, sign-in, audit, localisation, import | Yes | M1 |
| Public read API, consent, youth accounts, passkeys, notifications, the app | Yes (app: Android built and run on the emulator; iOS written, not compiled) | M2 |
| Family profile, shares, forms, eligibility, applications, waivers, decisions | Server and provider console; family screens not yet | M3a |
| Attendance (with offline sync), messaging, fees and payments (manual and Stripe), reports | Server, provider console and the app's offline roster; family screens not yet | M3b |
| Participation and portfolio (D65), Open Badges (D66), playlists and gated listings (D67), portfolio exports as PDF, OB 3.0 and CLR 2.0 (D68) | Server only | M4 |
| Community spaces, workforce, landscape, analytics, integrations | Schema only | M5 to M8 |
