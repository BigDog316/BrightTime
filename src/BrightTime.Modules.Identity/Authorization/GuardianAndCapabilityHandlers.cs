using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Authorization;

namespace BrightTime.Modules.Identity.Authorization;

/// <summary>The actor is a verified guardian of the subject.</summary>
public sealed class GuardianOfHandler(IIdentityQueries identity)
    : AuthorizationHandler<GuardianOfRequirement, AuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GuardianOfRequirement requirement,
        AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(target);

        if (context.User.GetUserId() is not { } actorId || target.SubjectUserId is not { } subject)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this, AuthorizationFailureReasons.NotAuthenticated, "Sign in to continue."));
            return;
        }

        bool verified = await identity
            .IsVerifiedGuardianOfAsync(new UserId(actorId), subject)
            .ConfigureAwait(false);

        if (verified)
        {
            context.Succeed(requirement);
            return;
        }

        // Distinguish "not their guardian" from "guardian link not yet
        // verified": the second is a step the parent can complete, and telling
        // them so is the difference between a usable product and a dead end.
        IReadOnlyList<GuardianRelationship> children = await identity
            .GetChildrenAsync(new UserId(actorId))
            .ConfigureAwait(false);

        bool anyLinkToSubject = children.Any(c => c.ChildUserId == subject);

        context.Fail(AuthorizationFailureReasons.For(
            this,
            anyLinkToSubject
                ? AuthorizationFailureReasons.GuardianLinkUnverified
                : AuthorizationFailureReasons.NotGuardian,
            anyLinkToSubject
                ? "Your link to this child has not been verified yet. Finish verification to continue."
                : "This account is not a verified guardian of that child."));
    }
}

/// <summary>The actor is the subject, or a verified guardian of it.</summary>
public sealed class SelfOrGuardianHandler(IIdentityQueries identity)
    : AuthorizationHandler<SelfOrGuardianRequirement, AuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SelfOrGuardianRequirement requirement,
        AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(target);

        if (context.User.GetUserId() is not { } actorId || target.SubjectUserId is not { } subject)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this, AuthorizationFailureReasons.NotAuthenticated, "Sign in to continue."));
            return;
        }

        if (actorId == subject.Value)
        {
            context.Succeed(requirement);
            return;
        }

        if (await identity.IsVerifiedGuardianOfAsync(new UserId(actorId), subject).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        context.Fail(AuthorizationFailureReasons.For(
            this,
            AuthorizationFailureReasons.NotGuardian,
            "This record belongs to someone else."));
    }
}

/// <summary>
/// The COPPA gate at the endpoint: the actor's own account currently holds the
/// capability.
/// </summary>
/// <remarks>
/// Reads the stored capability set rather than recomputing consent, so the
/// answer is the same one the UI was given by <c>/api/v1/me/capabilities</c>.
/// A lookup failure yields <see cref="YouthCapabilities.Denied"/>, so an error
/// degrades to "can do nothing".
/// </remarks>
public sealed class CapabilityHandler(IIdentityQueries identity)
    : AuthorizationHandler<CapabilityRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CapabilityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.GetUserId() is not { } actorId)
        {
            context.Fail(AuthorizationFailureReasons.For(
                this, AuthorizationFailureReasons.NotAuthenticated, "Sign in to continue."));
            return;
        }

        YouthCapabilities capabilities = await identity
            .GetCapabilitiesAsync(new UserId(actorId))
            .ConfigureAwait(false);

        if (requirement.Capability.IsGrantedBy(capabilities))
        {
            context.Succeed(requirement);
            return;
        }

        string reason = capabilities switch
        {
            { IsUnder13: true, HasAccountConsent: false } =>
                AuthorizationFailureReasons.ParentalConsentMissing,
            { IsUnder13: true } => AuthorizationFailureReasons.CapabilityWithheld,
            _ => AuthorizationFailureReasons.ParentalConsentMissing,
        };

        string message = capabilities switch
        {
            { IsUnder13: true, HasAccountConsent: false } =>
                "A parent or guardian needs to confirm this account before it can be used.",
            { IsUnder13: true } =>
                "This is not available on an account for someone under 13.",
            _ =>
                "A parent or guardian needs to allow this first.",
        };

        context.Fail(AuthorizationFailureReasons.For(this, reason, message));
    }
}

/// <summary>No contact restriction exists between actor and subject, in either direction.</summary>
public sealed class NoRestrictedContactHandler(IIdentityQueries identity)
    : AuthorizationHandler<NoRestrictedContactRequirement, AuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NoRestrictedContactRequirement requirement,
        AuthorizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(target);

        if (context.User.GetUserId() is not { } actorId || target.SubjectUserId is not { } subject)
        {
            // Nothing to compare. Other requirements in the policy decide.
            context.Succeed(requirement);
            return;
        }

        bool restricted = await identity
            .IsContactRestrictedAsync(new UserId(actorId), subject)
            .ConfigureAwait(false);

        if (restricted)
        {
            // Deliberately vague to the actor: naming the restriction would
            // confirm the relationship to the person it protects against.
            context.Fail(AuthorizationFailureReasons.For(
                this,
                AuthorizationFailureReasons.ContactRestricted,
                "This action is not available."));
            return;
        }

        context.Succeed(requirement);
    }
}
