using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The safety invariants the schema claims. Each one is a constraint or trigger, so
/// each is asserted against the database, not against application code.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ConstraintTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    [Fact]
    public async Task A_child_account_cannot_be_active_without_verifiable_parental_consent()
    {
        // enforce_child_consent() is a deferred trigger, so the check fires at commit.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid child = Guid.CreateVersion7();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "coppa-" + tenant.ToString("N"));

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
        await Sql.SeedUserAsync(connection, transaction, child, tenant, "child", status: "active");

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct));

        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task A_child_account_with_a_live_guardian_and_consent_can_become_active()
    {
        // The positive case. A trigger that is merely strict would pass the test above
        // and still block every real family.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid guardian = Guid.CreateVersion7();
        Guid child = Guid.CreateVersion7();
        Guid link = Guid.CreateVersion7();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "coppa-ok-" + tenant.ToString("N"));
        await Sql.SeedUserAsync(connection, null, guardian, tenant, "adult");

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
        await Sql.SeedUserAsync(connection, transaction, child, tenant, "child", status: "pending_consent");

        await Sql.ExecAsync(connection, transaction,
            """
            insert into identity.guardian_link
              (id, tenant_id, guardian_user_id, child_user_id, relationship, status, verification_method, verified_at)
            values (@link, @tenant, @guardian, @child, 'parent', 'verified', 'email_plus', now())
            """,
            [("link", link), ("tenant", tenant), ("guardian", guardian), ("child", child)]);

        await Sql.ExecAsync(connection, transaction,
            """
            insert into identity.parental_consent
              (id, tenant_id, child_user_id, guardian_user_id, guardian_link_id, scope, method, consent_text_hash)
            values (@id, @tenant, @child, @guardian, @link, 'account', 'email_plus', @hash)
            """,
            [("id", Guid.CreateVersion7()), ("tenant", tenant), ("child", child), ("guardian", guardian),
             ("link", link), ("hash", new byte[32])]);

        await Sql.ExecAsync(connection, transaction,
            "update identity.app_user set status = 'active' where id = @child",
            [("child", child)]);

        await transaction.CommitAsync(Ct);

        (await Sql.ScalarAsync<string>(connection, null,
            "select status from identity.app_user where id = @id", [("id", child)])).ShouldBe("active");
    }

    [Fact]
    public async Task An_under_13_account_cannot_hold_a_posting_capability_even_with_a_row_that_says_so()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        Guid child = Guid.CreateVersion7();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "u13-" + tenant.ToString("N"));
        await Sql.SeedUserAsync(connection, null, child, tenant, "child", status: "pending_consent");

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecAsync(connection, null,
            """
            insert into identity.youth_capability
              (user_id, tenant_id, is_under_13, has_account_consent, can_post_in_community)
            values (@user, @tenant, true, true, true)
            """,
            [("user", child), ("tenant", tenant)]));

        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task A_conversation_with_a_minor_and_one_adult_cannot_commit()
    {
        // The grooming channel. A one-to-one between an adult and a minor is refused
        // at commit, whatever the application did.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, Guid org, Guid adult, Guid youth, Guid conversation) = Ids();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await SeedOrgAndUsersAsync(connection, tenant, org, adult, youth);
        await Sql.ExecAsync(connection, null,
            "insert into community.conversation (id, tenant_id, org_id, conversation_kind) values (@c, @t, @o, 'org_to_family')",
            [("c", conversation), ("t", tenant), ("o", org)]);

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
        await AddParticipantAsync(connection, transaction, conversation, adult, "staff", isMinor: false);
        await AddParticipantAsync(connection, transaction, conversation, youth, "youth", isMinor: true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct));

        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task The_same_conversation_commits_once_a_second_adult_is_present()
    {
        // The positive case. The rule requires two adults, so adding an organisation
        // observer must make the conversation legitimate.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        (Guid tenant, Guid org, Guid adult, Guid youth, Guid conversation) = Ids();
        Guid observer = Guid.CreateVersion7();

        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await SeedOrgAndUsersAsync(connection, tenant, org, adult, youth);
        await Sql.SeedUserAsync(connection, null, observer, tenant, "adult");
        await Sql.ExecAsync(connection, null,
            "insert into community.conversation (id, tenant_id, org_id, conversation_kind) values (@c, @t, @o, 'org_to_family')",
            [("c", conversation), ("t", tenant), ("o", org)]);

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct))
        {
            await AddParticipantAsync(connection, transaction, conversation, adult, "staff", isMinor: false);
            await AddParticipantAsync(connection, transaction, conversation, observer, "org_admin_observer", isMinor: false);
            await AddParticipantAsync(connection, transaction, conversation, youth, "youth", isMinor: true);
            await transaction.CommitAsync(Ct);
        }

        (await Sql.ScalarAsync<bool>(connection, null,
            "select includes_minor from community.conversation where id = @c", [("c", conversation)])).ShouldBeTrue();
    }

    [Fact]
    public async Task Audit_rows_cannot_be_changed_once_written()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "audit-" + tenant.ToString("N"));
        await Sql.ExecAsync(connection, null,
            """
            insert into platform.audit_event (id, tenant_id, actor_kind, action, outcome, subject_count)
            values (@id, @tenant, 'user', 'test.action', 'allow', 0)
            """,
            [("id", Guid.CreateVersion7()), ("tenant", tenant)]);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecAsync(connection, null,
            "update platform.audit_event set action = 'rewritten' where tenant_id = @tenant",
            [("tenant", tenant)]));

        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task A_denied_access_must_record_why()
    {
        // ck_audit_reason_required: a deny without a reason cannot be written, so the
        // refusal and its explanation are one record.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        Guid tenant = Guid.CreateVersion7();
        await using NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, tenant, "deny-" + tenant.ToString("N"));

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecAsync(connection, null,
            """
            insert into platform.audit_event (id, tenant_id, actor_kind, action, outcome, subject_count)
            values (@id, @tenant, 'user', 'identity.youth_record.read', 'deny', 1)
            """,
            [("id", Guid.CreateVersion7()), ("tenant", tenant)]));

        error.SqlState.ShouldBe("23514");
    }

    private static (Guid Tenant, Guid Org, Guid Adult, Guid Youth, Guid Conversation) Ids() =>
        (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    private static async Task SeedOrgAndUsersAsync(
        NpgsqlConnection connection, Guid tenant, Guid org, Guid adult, Guid youth)
    {
        await Sql.SeedTenantAsync(connection, tenant, "conv-" + tenant.ToString("N"));
        await Sql.ExecAsync(connection, null,
            "insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind) " +
            "values (@o, @t, 'Org', 'Org', @s, 'nonprofit')",
            [("o", org), ("t", tenant), ("s", "org-" + org.ToString("N"))]);
        await Sql.SeedUserAsync(connection, null, adult, tenant, "adult");
        await Sql.SeedUserAsync(connection, null, youth, tenant, "youth");
    }

    private static Task AddParticipantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversation,
        Guid user,
        string role,
        bool isMinor) =>
        Sql.ExecAsync(connection, transaction,
            """
            insert into community.conversation_participant (conversation_id, user_id, participant_role, is_minor)
            values (@c, @u, @role, @minor)
            """,
            [("c", conversation), ("u", user), ("role", role), ("minor", isMinor)]);
}
