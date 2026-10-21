using System.Security.Claims;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Api.Messaging;

public sealed record StartConversationRequest(Guid? EnrollmentId, Guid? ApplicationId, string? Subject, string? Body);

public sealed record MessageRequest(string? Body);

/// <summary>
/// Messaging (M3b, D53): an organisation's staff open conversations with families; everyone in a
/// conversation reads and writes it. There is no route to a one-to-one channel between an adult and
/// a minor, and the database refuses one whichever route is tried (docs/plan/07, T1).
/// </summary>
public static class MessagingEndpoints
{
    public static RouteGroupBuilder MapMessagingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder mine = api.MapGroup("/conversations").RequireAuthorization();
        mine.MapGet("/", ListAsync).WithMetadata(new YouthRecordAttribute(Conversations.Read)).Produces<IReadOnlyList<ConversationSummary>>();
        mine.MapGet("/{id:guid}", GetAsync).WithMetadata(new YouthRecordAttribute(Conversations.Read)).Produces<ConversationView>();
        mine.MapPost("/{id:guid}/messages", SendAsync).WithMetadata(new YouthRecordAttribute(Conversations.Sent)).Produces<MessageView>(StatusCodes.Status201Created);

        api.MapPost("/org/{orgId:guid}/conversations", StartAsync).RequireAuthorization()
            .WithMetadata(new YouthRecordAttribute(Conversations.Sent)).Produces<ConversationView>(StatusCodes.Status201Created);
        return api;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, Conversations conversations, CancellationToken ct) =>
        Results.Ok(await conversations.ListAsync(Actor(user), ct));

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, Conversations conversations, AuditTrail audit, CancellationToken ct)
    {
        Result<ConversationView> found = await conversations.GetAsync(Actor(user), id, ct);
        return found.IsSuccess ? Results.Ok(found.Value) : Refused(audit, Conversations.Read, null, found.Error);
    }

    private static async Task<IResult> SendAsync(Guid id, MessageRequest body, ClaimsPrincipal user, Conversations conversations, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<MessageView> sent = await conversations.SendAsync(Actor(user), id, body.Body ?? string.Empty, ct);
        return sent.IsSuccess
            ? Results.Created($"/api/v1/conversations/{id}", sent.Value)
            : Refused(audit, Conversations.Sent, null, sent.Error);
    }

    /// <summary>
    /// Staff open a conversation with a family. With a young person in it, an admin of the
    /// organisation joins as an observer; with no second adult to add, it is refused
    /// (<c>messaging.unsafe_shape</c>), and the remedy is to ask the programme coordinator to join.
    /// </summary>
    private static async Task<IResult> StartAsync(Guid orgId, StartConversationRequest body, ClaimsPrincipal user, IAuthorizationService authorization, Conversations conversations, AuditTrail audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        OrganisationId org = new(orgId);
        AuthorizationResult may = await authorization.AuthorizeAsync(user, new AuthorizationTarget { OrganisationId = org },
            new OrganisationScopeRequirement(Permissions.Messages.SendToFamily, OrganisationRoles.Staff));
        if (!may.Succeeded)
        {
            return Refused(audit, Conversations.Sent, org, Error.Forbidden("messaging.forbidden", "Only this organisation's staff open conversations with its families."));
        }

        Result<ConversationView> started = await conversations.StartAsync(org, Actor(user), body.EnrollmentId, body.ApplicationId, body.Subject, body.Body ?? string.Empty, ct);
        return started.IsSuccess
            ? Results.Created($"/api/v1/conversations/{started.Value.Id}", started.Value)
            : Refused(audit, Conversations.Sent, org, started.Error);
    }

    private static IResult Refused(AuditTrail audit, string action, OrganisationId? org, Error error)
    {
        audit.Record(new AuditAccess { Action = action, Outcome = AuditOutcome.Deny, SubjectKind = "conversation", ActorOrgId = org, Reason = error.Code });
        return ApiProblems.From(error);
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
