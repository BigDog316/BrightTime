# The Kettle County seed

`src/BrightTime.Seed` loads the fictional Kettle County tenant that M1 is built and
demonstrated on. Nothing in it is a real provider, place or person:

- websites and registration links are on `example.org`;
- phone numbers are in 555-0100..0199;
- addresses are invented.

## What it creates

| | |
|---|---|
| Geography | 1 county, 11 municipalities, 18 neighbourhoods (a 6 x 3 grid of cells), 4 school districts, 28 schools, 1 bus agency with 18 stops |
| Taxonomy | 10 categories, 30 interests, 8 accommodations, each named in all six locales |
| Providers | 60 organisations (54 approved, 6 awaiting review) and 85 sites, 5 of them with a private address |
| Listings | 420 across all four kinds, about 93% published and the rest spread across draft, review, changes requested and unpublished |
| Languages | Spanish for every listing and all six locales for 12 of the 54 activity templates; a few listings are written in Spanish, Chinese, Bengali, Haitian Creole or Arabic first |
| Accounts | 12 roles with their default permissions; an owner for every organisation (`admin@<org-slug>.example.org`), a staff member for those with more than one site (`staff@…`), and two network organisers (`organiser@kettle.example.org`, `organiser2@…`). No passwords. |

All schedules are relative to the run date, so the site always has upcoming sessions.
A given date always produces the same catalogue: the ids come from names, and the choices
come from hashes of names.

## Running it

```bash
db/apply-schema.sh brighttime 127.0.0.1 5432 postgres
```

```bash
dotnet run --project src/BrightTime.Seed -- --connection "Host=127.0.0.1;Port=5432;Database=brighttime;Username=postgres"
```

- **Connection:** use `--connection` or `BRIGHTTIME_SEED_CONNECTION`.
- **Role:** the seed spans a whole tenant, like a data migration, so it must bypass
  row-level security. Run it as a superuser, or as a member of `brighttime_migrator` with
  `Options=-c role=brighttime_migrator` added to the connection string. It refuses to run
  as anything else.
- **Re-running:** safe. Every insert is `on conflict do nothing`, so rows edited through
  the application are left alone, and every listing is projected again.
- **Projection:** runs inline, using the same service as the projection job. A finished
  seed is a searchable site, with no worker needed. `--skip-projection` leaves it to the
  worker.
- **Date:** `--today yyyy-MM-dd` pins the run date, for reproducing a catalogue.
- **Fresh database:** `--recreate` drops the database the connection names, rebuilds it
  from `db/schema` (no psql needed), then seeds. It refuses any database whose name does
  not start with `brighttime`.

## Signing in to the consoles

Until Identity sign-in exists (decision D14), the seeded accounts are reached through
`/dev/sign-in`. It is offered only in the Development environment, and only with:

- `Auth:DevelopmentSignIn=true`;
- `Auth:ConnectionString` set to a login that is a member of `brighttime_auth`.

Elsewhere the route does not exist.

To run the web app on the seed, set `Finder:Mode=Sql`,
`Finder:TenantId=0196c3e8-0000-7000-8000-00000000f001`, and a `Finder:ConnectionString`
whose login is a member of both `brighttime_app` and `brighttime_finder` (decision D13).
The consoles also need `Auth:ConnectionString`, as above. Approvals and edits are projected
into search by the jobs host (`ConnectionStrings:Jobs`, a member of `brighttime_jobs`), so run
it alongside.

## Translations

The non-English text, including taxonomy names and listing titles and summaries, was
written for the seed, not by a translator. Listing translations are stored with
`translation_source = 'machine'`, and the listing page says "Translated automatically".
A native speaker should review them before a real tenant relies on any of it.
