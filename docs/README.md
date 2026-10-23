# BrightTime documentation

BrightTime is a countywide out-of-school-time platform. Families and young people find
after-school, summer and weekend activities. Organisations run those activities, take
registrations and record attendance. A network organiser vets the organisations and their
listings. The seed region is Kettle County, NY (fictional), in six languages: English, Spanish,
Chinese (Simplified), Bengali, Haitian Creole and Arabic.

This documentation describes the application as built through decision D66 (2026-10-22): milestones
M1 to M3 are signed off and M4 is under way.

## For people who use BrightTime

[User guides](user/README.md): what each kind of person can do, on the website and in the app.

| Guide | For |
|---|---|
| [Families](user/families.md) | Parents and guardians: finding activities, accounts, children's profiles and consent |
| [Young people](user/young-people.md) | Anyone 13 or over with their own account |
| [Organisations](user/providers.md) | Owners, admins and staff of organisations that run activities |
| [Network organisers](user/organisers.md) | The people who run the county's site: vetting, review, taxonomy, wording, audit |

## For people who build and run it

[Technical documentation](technical/README.md): how the system is put together and how to operate it.

| Document | Covers |
|---|---|
| [Architecture](technical/architecture.md) | Hosts, modules, how they talk, tenancy, the request pipeline |
| [Data and security](technical/data-and-security.md) | The schema, row-level security, encryption, keys, authentication, authorisation, audit |
| [API reference](technical/api.md) | Conventions and every route of `/api/v1` |
| [Configuration](technical/configuration.md) | Every setting each host reads |
| [Operations](technical/operations.md) | Running the stack, the schema, the worker, keys, payments, push, mail, health |
| [Development](technical/development.md) | The solution, tests, CI and the conventions the tests enforce |

## PDF editions

The same guides and the technical reference are in [pdf/](pdf/) as PDFs, each with a cover, contents,
bookmarks and page numbers. Rebuild them after changing the Markdown, with Python 3, Microsoft Edge
and `pip install markdown pymupdf playwright`:

```bash
python tools/docs-pdf/build.py . docs/pdf
```

## Records of decisions and progress

- [plan/](plan/README.md): the plan as approved, and [plan/08-decisions.md](plan/08-decisions.md),
  which records every decision (D1 to D66) and every open question.
- [m1-report.md](m1-report.md) and [m3-report.md](m3-report.md): the milestone reports.
- [HANDOFF.md](HANDOFF.md): where the work stands, and setting up a development machine.
- [seed.md](seed.md) and [import.md](import.md): the Kettle County seed, and importing listings.
- [accessibility/](accessibility/): the keyboard-only pass.
