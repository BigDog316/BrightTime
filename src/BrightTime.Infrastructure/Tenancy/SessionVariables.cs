namespace BrightTime.Infrastructure.Tenancy;

/// <summary>
/// The PostgreSQL session variables the row-level security policies read.
/// </summary>
/// <remarks>
/// These names appear in exactly two places: here, and in
/// <c>db/schema/00-extensions-roles.sql</c> where
/// <c>platform.current_tenant_id()</c> and <c>platform.current_actor_id()</c>
/// read them. An integration test asserts the pair stays in step, because a
/// typo here does not fail — it silently makes every RLS predicate evaluate
/// against NULL, and a tenant-scoped policy that compares against NULL returns
/// no rows rather than the wrong rows. Quiet, but still wrong.
/// </remarks>
public static class SessionVariables
{
    public const string TenantId = "brighttime.tenant_id";

    public const string ActorId = "brighttime.actor_id";

    /// <summary>The threshold of the finder's typo-tolerance operator, <c>&lt;&lt;%</c>, set per transaction; see docs/plan/03-indexing-and-query-plans.md.</summary>
    public const string FuzzyThreshold = "pg_trgm.strict_word_similarity_threshold";
}
