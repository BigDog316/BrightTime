using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Api.Family;

public sealed record FamilyRequest(short? HouseholdSize, string? PrimaryLanguage, bool NeedsInterpreter);

public sealed record MemberRequest(
    string? Role,
    string? FirstName,
    string? LastName,
    string? PreferredName,
    DateOnly? DateOfBirth,
    short? Grade,
    string? Pronouns,
    string? Relationship);

public sealed record SupportNeedRequest(string? Kind, string? Detail, bool ShareWithProviders);

public sealed record SupportNeedsRequest(IReadOnlyList<SupportNeedRequest>? Needs);

public sealed record ContactRequest(string? Name, string? Relationship, string? Phone, string? Email, bool IsEmergency = true, bool CanPickUp = false);

/// <summary>
/// The signed-in account's family profile (M3a, D43): the household, its members with their
/// support needs, and its contacts. Every request reads or changes a youth record, so every one
/// is audited, refusals included (docs/plan/07).
/// </summary>
/// <remarks>
/// A child account cannot hold a profile: its guardian's profile lists the child instead.
/// </remarks>
public static class FamilyEndpoints
{
    public static RouteGroupBuilder MapFamilyEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder family = api.MapGroup("/family-profile").RequireAuthorization();
        family.MapGet("/", GetAsync).Read().Produces<FamilyView>();
        family.MapPut("/", SaveAsync).Changes().Produces<FamilyView>();
        family.MapPost("/members", AddMemberAsync).Changes().Produces<MemberView>(StatusCodes.Status201Created);
        family.MapPut("/members/{id:guid}", UpdateMemberAsync).Changes().Produces<MemberView>();
        family.MapDelete("/members/{id:guid}", RemoveMemberAsync).Changes();
        family.MapPut("/members/{id:guid}/support-needs", SetSupportNeedsAsync).Changes().Produces<MemberView>();
        family.MapPost("/contacts", AddContactAsync).Changes().Produces<ContactView>(StatusCodes.Status201Created);
        family.MapPut("/contacts/{id:guid}", UpdateContactAsync).Changes().Produces<ContactView>();
        family.MapDelete("/contacts/{id:guid}", RemoveContactAsync).Changes();
        return api;
    }

    private static RouteHandlerBuilder Read(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(FamilyProfiles.Read));

    private static RouteHandlerBuilder Changes(this RouteHandlerBuilder builder) => builder.WithMetadata(new YouthRecordAttribute(FamilyProfiles.Changed));

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Read) is { } refused)
        {
            return refused;
        }

        return await profiles.GetAsync(Owner(user), ct) is { } view
            ? Results.Ok(view)
            : ApiProblems.From(Error.NotFound("family.no_profile", "You have not saved your household's details yet."));
    }

    private static async Task<IResult> SaveAsync(FamilyRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result<FamilyView> saved = await profiles.SaveAsync(Owner(user), new FamilyInput(body.HouseholdSize, body.PrimaryLanguage, body.NeedsInterpreter), ct);
        return saved.IsSuccess ? Results.Ok(saved.Value) : Refused(audit, FamilyProfiles.Changed, saved.Error);
    }

    private static async Task<IResult> AddMemberAsync(MemberRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result<MemberView> added = await profiles.AddMemberAsync(Owner(user), Input(body), ct);
        return added.IsSuccess
            ? Results.Created($"/api/v1/family-profile/members/{added.Value.Id}", added.Value)
            : Refused(audit, FamilyProfiles.Changed, added.Error);
    }

    private static async Task<IResult> UpdateMemberAsync(Guid id, MemberRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result<MemberView> changed = await profiles.UpdateMemberAsync(Owner(user), id, Input(body), ct);
        return changed.IsSuccess ? Results.Ok(changed.Value) : Refused(audit, FamilyProfiles.Changed, changed.Error);
    }

    private static async Task<IResult> RemoveMemberAsync(Guid id, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result removed = await profiles.RemoveMemberAsync(Owner(user), id, ct);
        return removed.IsSuccess ? Results.NoContent() : Refused(audit, FamilyProfiles.Changed, removed.Error);
    }

    private static async Task<IResult> SetSupportNeedsAsync(Guid id, SupportNeedsRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        List<SupportNeedInput> needs = [.. (body.Needs ?? []).Select(n => new SupportNeedInput(n.Kind ?? string.Empty, n.Detail, n.ShareWithProviders))];
        Result<MemberView> set = await profiles.SetSupportNeedsAsync(Owner(user), id, needs, ct);
        return set.IsSuccess ? Results.Ok(set.Value) : Refused(audit, FamilyProfiles.Changed, set.Error);
    }

    private static async Task<IResult> AddContactAsync(ContactRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result<ContactView> added = await profiles.AddContactAsync(Owner(user), Input(body), ct);
        return added.IsSuccess
            ? Results.Created($"/api/v1/family-profile/contacts/{added.Value.Id}", added.Value)
            : Refused(audit, FamilyProfiles.Changed, added.Error);
    }

    private static async Task<IResult> UpdateContactAsync(Guid id, ContactRequest body, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result<ContactView> changed = await profiles.UpdateContactAsync(Owner(user), id, Input(body), ct);
        return changed.IsSuccess ? Results.Ok(changed.Value) : Refused(audit, FamilyProfiles.Changed, changed.Error);
    }

    private static async Task<IResult> RemoveContactAsync(Guid id, ClaimsPrincipal user, FamilyProfiles profiles, AuditTrail audit, CancellationToken ct)
    {
        if (ChildRefused(user, audit, FamilyProfiles.Changed) is { } refused)
        {
            return refused;
        }

        Result removed = await profiles.RemoveContactAsync(Owner(user), id, ct);
        return removed.IsSuccess ? Results.NoContent() : Refused(audit, FamilyProfiles.Changed, removed.Error);
    }

    private static MemberInput Input(MemberRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(body.Role ?? string.Empty, body.FirstName ?? string.Empty, body.LastName ?? string.Empty, body.PreferredName, body.DateOfBirth, body.Grade, body.Pronouns, body.Relationship);
    }

    private static ContactInput Input(ContactRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(body.Name ?? string.Empty, body.Relationship ?? string.Empty, body.Phone ?? string.Empty, body.Email, body.IsEmergency, body.CanPickUp);
    }

    /// <summary>A refusal is an access attempt too, so it leaves its own row, naming why.</summary>
    private static IResult Refused(AuditTrail audit, string action, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "family_profile", Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static IResult? ChildRefused(ClaimsPrincipal user, AuditTrail audit, string action) =>
        user.FindFirstValue(BrightTimeClaims.AccountKind) == "child"
            ? Refused(audit, action, Error.Forbidden("family.child_account", "A child's details are kept on their guardian's family profile."))
            : null;

    private static UserId Owner(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
