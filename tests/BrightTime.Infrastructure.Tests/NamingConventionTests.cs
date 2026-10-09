using BrightTime.Infrastructure.Persistence;

namespace BrightTime.Infrastructure.Tests;

public sealed class NamingConventionTests
{
    [Theory]
    [InlineData("Tenant", "tenant")]
    [InlineData("TenantDomain", "tenant_domain")]
    [InlineData("AuditEvent", "audit_event")]
    [InlineData("OutboxMessage", "outbox_message")]
    [InlineData("SubjectIds", "subject_ids")]
    [InlineData("IsRtl", "is_rtl")]
    [InlineData("TsConfig", "ts_config")]
    [InlineData("MinCellSize", "min_cell_size")]
    [InlineData("Sha256", "sha256")]
    [InlineData("DateOfBirthEnc", "date_of_birth_enc")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("", "")]
    public void Maps_clr_names_to_the_schema_spelling(string input, string expected) =>
        NamingConventions.ToSnakeCase(input).ShouldBe(expected);

    [Theory]
    [InlineData("EINBlindIndex", "ein_blind_index")]
    [InlineData("DPAReference", "dpa_reference")]
    [InlineData("GTFSStopId", "gtfs_stop_id")]
    public void Handles_acronyms_the_way_the_ddl_spells_them(string input, string expected) =>
        NamingConventions.ToSnakeCase(input).ShouldBe(expected);
}
