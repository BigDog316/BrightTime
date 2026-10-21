using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// docs/plan/07, T1: every route to a one-to-one channel between an adult and a minor fails at the
/// database. ConstraintTests covers the direct insert and the positive case; these are the other
/// routes: leaving a pair by removal, by deletion, by the guardian leaving, and by passing a minor
/// off as an adult or as staff.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ConversationSafetyTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cs => database.ConnectionString;

    private sealed record Thread(Guid Tenant, Guid Conversation, Guid Staff, Guid Observer, Guid Guardian, Guid Youth);

    /// <summary>A legitimate conversation: staff, an observer and a guardian (all adults), and a youth.</summary>
    private async Task<(NpgsqlConnection Connection, Thread Thread)> ThreadAsync()
    {
        Thread t = new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        Guid org = Guid.CreateVersion7();
        NpgsqlConnection connection = await Sql.OpenAsync(Cs, Ct);
        await Sql.SeedTenantAsync(connection, t.Tenant, "safe-" + t.Tenant.ToString("N"));
        await Sql.ExecAsync(connection, null,
            "insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind) values (@o, @t, 'Org', 'Org', @s, 'nonprofit')",
            [("o", org), ("t", t.Tenant), ("s", "org-" + org.ToString("N"))]);
        foreach (Guid adult in new[] { t.Staff, t.Observer, t.Guardian })
        {
            await Sql.SeedUserAsync(connection, null, adult, t.Tenant, "adult");
        }

        await Sql.SeedUserAsync(connection, null, t.Youth, t.Tenant, "youth");
        await Sql.ExecAsync(connection, null,
            "insert into community.conversation (id, tenant_id, org_id, conversation_kind) values (@c, @t, @o, 'org_to_family')",
            [("c", t.Conversation), ("t", t.Tenant), ("o", org)]);
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct))
        {
            await AddAsync(connection, transaction, t.Conversation, t.Staff, "staff", isMinor: false);
            await AddAsync(connection, transaction, t.Conversation, t.Observer, "org_admin_observer", isMinor: false);
            await AddAsync(connection, transaction, t.Conversation, t.Guardian, "guardian", isMinor: false);
            await AddAsync(connection, transaction, t.Conversation, t.Youth, "youth", isMinor: true);
            await transaction.CommitAsync(Ct);
        }

        return (connection, t);
    }

    [Fact]
    public async Task Removing_adults_until_one_is_left_with_the_minor_fails_at_commit()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (NpgsqlConnection connection, Thread t) = await ThreadAsync();
        await using (connection)
        {
            // One adult may leave: three adults become two.
            await Sql.ExecAsync(connection, null, "update community.conversation_participant set removed_at = now() where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Guardian)]);

            // The next removal would leave a pair.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
            await Sql.ExecAsync(connection, transaction, "update community.conversation_participant set removed_at = now() where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Observer)]);
            (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct))).SqlState.ShouldBe("23514");
        }
    }

    [Fact]
    public async Task Deleting_participants_to_leave_a_pair_fails_at_commit()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (NpgsqlConnection connection, Thread t) = await ThreadAsync();
        await using (connection)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
            await Sql.ExecAsync(connection, transaction, "delete from community.conversation_participant where conversation_id = @c and user_id in (@o, @g)",
                [("c", t.Conversation), ("o", t.Observer), ("g", t.Guardian)]);
            (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct))).SqlState.ShouldBe("23514");
        }
    }

    [Fact]
    public async Task The_guardian_and_the_observer_leaving_together_fails_at_commit()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (NpgsqlConnection connection, Thread t) = await ThreadAsync();
        await using (connection)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
            await Sql.ExecAsync(connection, transaction, "update community.conversation_participant set removed_at = now() where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Guardian)]);
            await Sql.ExecAsync(connection, transaction, "update community.conversation_participant set removed_at = now() where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Observer)]);
            (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct))).SqlState.ShouldBe("23514");
        }
    }

    [Fact]
    public async Task Passing_a_minor_off_as_an_adult_changes_nothing()
    {
        // is_minor is the database's answer (D53): a youth account is a minor whatever the write says.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (NpgsqlConnection connection, Thread t) = await ThreadAsync();
        await using (connection)
        {
            await Sql.ExecAsync(connection, null, "update community.conversation_participant set is_minor = false where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Youth)]);
            (await Sql.ScalarAsync<bool>(connection, null, "select is_minor from community.conversation_participant where conversation_id = @c and user_id = @u",
                [("c", t.Conversation), ("u", t.Youth)])).ShouldBeTrue();

            // So a new pair, with the minor declared an adult, still fails.
            Guid pair = Guid.CreateVersion7();
            Guid org = await Sql.ScalarAsync<Guid>(connection, null, "select org_id from community.conversation where id = @c", [("c", t.Conversation)]);
            await Sql.ExecAsync(connection, null, "insert into community.conversation (id, tenant_id, org_id, conversation_kind) values (@c, @t, @o, 'org_to_family')",
                [("c", pair), ("t", t.Tenant), ("o", org)]);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Ct);
            await AddAsync(connection, transaction, pair, t.Staff, "staff", isMinor: false);
            await AddAsync(connection, transaction, pair, t.Youth, "youth", isMinor: false);
            (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Ct))).SqlState.ShouldBe("23514");
        }
    }

    [Fact]
    public async Task A_minor_cannot_join_as_staff_to_make_up_the_adults()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        (NpgsqlConnection connection, Thread t) = await ThreadAsync();
        await using (connection)
        {
            Guid secondYouth = Guid.CreateVersion7();
            await Sql.SeedUserAsync(connection, null, secondYouth, t.Tenant, "youth");
            (await Should.ThrowAsync<PostgresException>(() => AddAsync(connection, null, t.Conversation, secondYouth, "staff", isMinor: false)))
                .SqlState.ShouldBe("23514");
        }
    }

    private static Task AddAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid conversation, Guid user, string role, bool isMinor) =>
        Sql.ExecAsync(connection, transaction,
            "insert into community.conversation_participant (conversation_id, user_id, participant_role, is_minor) values (@c, @u, @role, @minor)",
            [("c", conversation), ("u", user), ("role", role), ("minor", isMinor)]);
}
