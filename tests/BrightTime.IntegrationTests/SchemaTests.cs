namespace BrightTime.IntegrationTests;

/// <summary>
/// Asserts that the DDL in <c>db/schema/</c> actually applies, and that the
/// safety invariants it claims are real.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SchemaTests(DatabaseFixture database)
{
    [Fact]
    public async Task The_whole_schema_applies_cleanly()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "Database unavailable.");

        // The fixture applies every file in order, including the assertion at
        // the end of 99-rls-and-grants.sql that raises if any module table is
        // missing row-level security. Reaching here means that passed.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Every_module_table_has_row_level_security()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "Database unavailable.");

        await database.ExecuteAsync(
            """
            do $$
            declare v_missing text;
            begin
              select string_agg(format('%s.%s', n.nspname, c.relname), ', ')
                into v_missing
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
              where c.relkind in ('r','p')
                and n.nspname in ('platform','identity','directory','opportunity','enrollment',
                                  'attendance','learning','community','workforce','landscape',
                                  'analytics','notify','integration')
                and not c.relrowsecurity
                and not c.relispartition;
              if v_missing is not null then
                raise exception 'tables without RLS: %', v_missing;
              end if;
            end $$;
            """);
    }

    [Fact]
    public async Task The_app_role_cannot_bypass_row_level_security()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "Database unavailable.");

        // If brighttime_app ever gains BYPASSRLS, every policy in the system
        // becomes decoration.
        await database.ExecuteAsync(
            """
            do $$
            begin
              if exists (select 1 from pg_roles
                         where rolname = 'brighttime_app' and rolbypassrls) then
                raise exception 'brighttime_app must not have BYPASSRLS';
              end if;
              if exists (select 1 from pg_roles
                         where rolname = 'brighttime_app' and rolsuper) then
                raise exception 'brighttime_app must not be a superuser';
              end if;
            end $$;
            """);
    }
}
