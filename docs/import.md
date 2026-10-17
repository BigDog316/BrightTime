# Importing listings

An organisation's owners and admins can import listings from a CSV file or from Open Referral
HSDS 3.0 JSON. The page is at **Provider console → organisation → Import**
(`/provider/orgs/{id}/import`).

1. **Check.** Upload a file. Every row is checked as the listing editor would check it, and
   the report shows what would happen to each row: a new draft, an update, left alone, or
   not imported with its errors. Nothing is written.
2. **Apply.** Rows with errors are skipped. The file is checked again first, so a site closed
   or a listing sent for review since the check is caught. The check expires after 30
   minutes; upload the file again after that.

Limits: 1 MB and 500 listings per file, in UTF-8.

## What an import does

- **New rows become drafts.** Submit them for review as usual. An import never publishes.
- **Each row needs an id**, the provider's own name for the listing. A second import with the
  same id updates that listing instead of adding a copy. Ids are separate for each
  organisation and each format.
- **Updating a published listing** puts the change live at once and in the organiser's
  "changed since review" lane (D15), as an edit in the editor does.
- **A listing waiting for review**, or past editing, is left alone and reported.
- The operation is recorded in the audit log as `listing.import_applied`, with counts.

## CSV

Download the template from the import page (`/provider/import-template.csv`): it has every
column and one example row. The first row names the columns, in any order and any case.
Required: `id`, `title`, `summary`, `starts_on`, `start_time`, `end_time`. An unknown or
repeated column refuses the whole file, because a misspelt column would otherwise be
silently ignored.

| Column | Value |
|---|---|
| `id` | Your id for the listing, up to 200 characters |
| `kind` | `program` (default), `event`, `job`, `resource` |
| `title`, `summary`, `description` | As in the editor |
| `language` | Primary language: `en`, `es`, `zh-Hans`, `bn`, `ht`, `ar` |
| `age_min`, `age_max`, `grade_min`, `grade_max` | Whole numbers |
| `categories`, `interests`, `accommodations` | Taxonomy codes, separated by `;` |
| `languages` | Languages spoken, separated by `;` |
| `modality` | `in_person` (default), `virtual`, `hybrid` |
| `virtual_url` | For virtual and hybrid listings |
| `sites` | Site names exactly as on the Sites page, separated by `;`. Only open sites match |
| `cost_model`, `cost_min`, `cost_max`, `sliding_scale_note` | As in the editor; amounts in dollars |
| `registration`, `registration_url`, `capacity` | `external` (default), `builtin`, `none` |
| `meals`, `transport`, `drop_in` | `yes` or `no` |
| `transport_note`, `accessibility_note`, `contact_email`, `contact_phone` | Text |
| `repeats` | `weekly` (default) or `once` |
| `days` | `MO;WE` and so on, for weekly |
| `every_weeks` | `1` to `4` |
| `starts_on`, `ends_on` | `yyyy-mm-dd` |
| `start_time`, `end_time` | `HH:mm`, in the tenant's time zone |
| `skip_dates` | Days with no session, `yyyy-mm-dd`, separated by `;` |
| `deadline_kind`, `deadline_on` | As in the editor |
| `show_from`, `take_down_after` | Scheduled publication, `yyyy-mm-dd` |

Quote any value that contains a comma, a quote or a line break, as spreadsheets do when they
save CSV. A site name with a comma in it needs quotes.

The upload form can name a **default category** for rows that have none.

## HSDS 3.0

The file is an array of services, an object with a `services` array, or a page from an HSDS
API (`contents`). Each service becomes a program:

| HSDS | Listing |
|---|---|
| `id` | the row's id |
| `name` | title |
| `description` | description; its first sentence, shortened to 300 characters, is the summary |
| `status` other than `active` | not imported |
| `minimum_age`, `maximum_age` | ages, rounded inward |
| `url` | registration link (and the virtual address when there is no location) |
| `email`, `phones[0].number` | contact |
| `languages[].code` | languages spoken |
| `service_at_locations[].location.name` | sites, by name; none means virtual |
| `attributes[].taxonomy_term.code` | categories and interests; unknown codes are ignored |
| `cost_options[].amount` | free, fixed, or a sliding scale from the lowest to the highest |
| `schedules[]` | the first weekly schedule with times (`byday`, `interval`, `dtstart`/`valid_from`, `until`/`valid_to`, `opens_at`, `closes_at`), else the first one-off |

Monthly and other patterns are refused for that service rather than guessed. Organisation
ids in the file are ignored: everything imports into the organisation that uploads it.

Not yet recorded: an `integration.import_run` row per import, for scheduled feeds. Interactive
imports are in the audit log instead; the run table arrives with integrations (M8).
