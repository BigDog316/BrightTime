using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>
/// What an organisation's logo may be (D109): a PNG or JPEG of up to 2 MB, at least 200 pixels
/// each way, and no more than four times as wide as it's tall or as tall as it's wide.
/// </summary>
public static class OrganisationLogoRules
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MinSide = 200;
    public const int MaxSide = 6000;
    public const double MaxAspect = 4;

    public static readonly ImageRules Image = new(MaxBytes, MinSide, MinSide, MaxSide, MaxAspect);

    public static Error NotAnImage { get; } = Error.Validation(
        "organisation_logo.image",
        "Choose a PNG or JPEG of up to 2 MB, at least 200 pixels each way, and no more than four times as wide as it's tall.");

    public static Error NotFound { get; } = Error.NotFound("organisation_logo.not_found", "There's no such organisation.");

    public static Error Concurrent { get; } =
        Error.Conflict("organisation_logo.concurrent", "Someone else changed this organisation's logo at the same time. Look again, then try once more.");
}

/// <summary>
/// An organisation's logo (D109): a public media object (D103) with its smaller copies (D106),
/// shown beside the organisation's name on its page and its listings once it's approved.
/// </summary>
/// <remarks>
/// Like the rest of the profile, a new logo is live at once and the organiser sees it in the audit
/// log; an organiser can take one down. Nothing describes it: it always sits beside the name, so
/// it's shown as decoration. Platform keeps the bytes in its own transaction, so a logo whose
/// organisation write fails is removed again, and the one it replaces is removed once let go.
/// </remarks>
public sealed class OrganisationLogos(DirectoryDbContext db, IMediaLibrary media, IClock clock, IAuditWriter audit)
{
    /// <summary>Who took a logo down: the organisation's own owners and admins, or an organiser.</summary>
    public enum Remover
    {
        Provider,
        Organiser,
    }

    /// <summary>Gives the organisation a logo, in place of any it had.</summary>
    public async Task<Result> SetAsync(Guid organisation, ReadOnlyMemory<byte> file, UserId actor, CancellationToken cancellationToken = default)
    {
        // Checked before the bytes are kept, so a refused upload stores nothing.
        bool exists = await db.InTransactionAsync(
            ct => db.Organisations.AsNoTracking().AnyAsync(o => o.Id == organisation, ct), cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return Result.Failure(OrganisationLogoRules.NotFound);
        }

        if (await media.AddImageAsync(file, OrganisationLogoRules.Image, MediaVisibility.Public, actor, cancellationToken).ConfigureAwait(false) is not { } image)
        {
            return Result.Failure(OrganisationLogoRules.NotAnImage);
        }

        Result<Guid?> replaced;
        try
        {
            replaced = await PointAsync(organisation, image.MediaId, "organisation.logo_set", actor, Remover.Provider, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await media.RemoveAsync(image.MediaId, actor, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!replaced.IsSuccess)
        {
            await media.RemoveAsync(image.MediaId, actor, cancellationToken).ConfigureAwait(false);
            return Result.Failure(replaced.Error!);
        }

        if (replaced.Value is { } previous)
        {
            await media.RemoveAsync(previous, actor, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>Takes the organisation's logo down. Having none already is no failure.</summary>
    public async Task<Result> RemoveAsync(Guid organisation, UserId actor, Remover by, CancellationToken cancellationToken = default)
    {
        Result<Guid?> removed = await PointAsync(organisation, null, "organisation.logo_removed", actor, by, cancellationToken).ConfigureAwait(false);
        if (!removed.IsSuccess)
        {
            return Result.Failure(removed.Error!);
        }

        if (removed.Value is { } previous)
        {
            await media.RemoveAsync(previous, actor, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>Points the organisation at <paramref name="logo"/>, or at none, and gives the logo it let go.</summary>
    private async Task<Result<Guid?>> PointAsync(Guid organisation, Guid? logo, string action, UserId actor, Remover by, CancellationToken cancellationToken)
    {
        try
        {
            return await db.InTransactionAsync(async ct =>
            {
                // Organisation's row version is its concurrency token: two changes at once can't both
                // let go of the same logo and leave the other's bytes with nothing pointing at them.
                Organisation? o = await db.Organisations.FirstOrDefaultAsync(x => x.Id == organisation, ct).ConfigureAwait(false);
                if (o is null)
                {
                    return Result<Guid?>.Failure(OrganisationLogoRules.NotFound);
                }

                Guid? previous = o.LogoMediaId;
                if (previous is null && logo is null)
                {
                    return Result<Guid?>.Success(null);
                }

                o.LogoMediaId = logo;
                o.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await audit.RecordInTransactionAsync(db, new AuditAccess
                {
                    Action = action,
                    ActorId = actor,
                    ActorOrgId = by == Remover.Provider ? new OrganisationId(organisation) : null,
                    SubjectKind = "organisation",
                    SubjectIds = [organisation],
                    Detail = new Dictionary<string, string>
                    {
                        ["media"] = (logo ?? previous)!.Value.ToString(),
                        ["by"] = by == Remover.Provider ? "provider" : "organiser",
                    },
                }, ct).ConfigureAwait(false);
                return Result<Guid?>.Success(previous);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<Guid?>.Failure(OrganisationLogoRules.Concurrent);
        }
    }
}
