using System.Globalization;
using System.Security.Claims;
using BrightTime.Api.Public;
using BrightTime.Hosting.Auth;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Notifications;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Api.Notifications;

public sealed record RegisterDeviceRequest(string? Platform, string? Token, string? AppVersion, string? OsVersion, string? Locale);

public sealed record DeviceResponse(Guid Id, string Platform, DateTimeOffset RegisteredAt);

public sealed record ChannelChoiceDto(string? Kind, string? Channel, bool Enabled);

public sealed record NotificationSettingsDto(string? QuietFrom, string? QuietTo, IReadOnlyList<ChannelChoiceDto>? Choices);

public sealed record InboxItemResponse(Guid Id, string Kind, string Title, string Body, string? DeepLink, DateTimeOffset CreatedAt, bool IsRead);

/// <summary>
/// Push devices, notification preferences with quiet hours, and the inbox (docs/plan/05,
/// "Mobile-specific"). The inbox comes back in words, in the request's language, worded as the
/// push was (<see cref="NotificationText"/>), so the two always agree.
/// </summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/devices", RegisterAsync).RequireAuthorization();
        api.MapDelete("/devices/{id:guid}", UnregisterAsync).RequireAuthorization();

        RouteGroupBuilder me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("/notification-preferences", SettingsAsync);
        me.MapPut("/notification-preferences", SetSettingsAsync);
        me.MapGet("/notifications", InboxAsync);
        me.MapPost("/notifications/{id:guid}/read", ReadAsync);
        return api;
    }

    private static async Task<IResult> RegisterAsync(RegisterDeviceRequest body, ClaimsPrincipal user, PushDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Result<DeviceView> made = await devices.RegisterAsync(Actor(user), body.Platform ?? string.Empty, body.Token ?? string.Empty, body.AppVersion, body.OsVersion, body.Locale, ct);
        return made.IsSuccess
            ? Results.Created($"/api/v1/devices/{made.Value.Id}", new DeviceResponse(made.Value.Id, made.Value.Platform, made.Value.RegisteredAt))
            : ApiProblems.From(made.Error);
    }

    private static async Task<IResult> UnregisterAsync(Guid id, ClaimsPrincipal user, PushDevices devices, CancellationToken ct) =>
        await devices.UnregisterAsync(Actor(user), id, ct) ? Results.NoContent() : ApiProblems.NotFound("You have no open device with that id.");

    private static async Task<IResult> SettingsAsync(ClaimsPrincipal user, NotificationPreferences preferences, CancellationToken ct) =>
        Results.Ok(Dto(await preferences.GetAsync(Actor(user), ct)));

    private static async Task<IResult> SetSettingsAsync(NotificationSettingsDto body, ClaimsPrincipal user, NotificationPreferences preferences, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Dictionary<string, string[]> errors = [];
        TimeOnly? from = Time(body.QuietFrom, "quietFrom", errors);
        TimeOnly? to = Time(body.QuietTo, "quietTo", errors);
        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        Result<NotificationSettings> set = await preferences.SetAsync(Actor(user),
            new NotificationSettings(from, to, [.. (body.Choices ?? []).Select(c => new ChannelChoice(c.Kind ?? string.Empty, c.Channel ?? string.Empty, c.Enabled))]), ct);
        return set.IsSuccess ? Results.Ok(Dto(set.Value)) : ApiProblems.From(set.Error);
    }

    private static async Task<IResult> InboxAsync(ClaimsPrincipal user, NotificationInbox inbox, Text text, CancellationToken ct)
    {
        IReadOnlyList<InboxItem> items = await inbox.ListAsync(Actor(user), ct);
        await text.LoadAsync(ct);
        return Results.Ok(items.Select(i =>
        {
            NotificationWords said = NotificationText.Render(text, i.TitleKey, i.Arguments);
            return new InboxItemResponse(i.Id, i.Kind, said.Title, said.Body, i.DeepLink, i.CreatedAt, i.IsRead);
        }).ToList());
    }

    private static async Task<IResult> ReadAsync(Guid id, ClaimsPrincipal user, NotificationInbox inbox, CancellationToken ct) =>
        await inbox.MarkReadAsync(Actor(user), id, ct) ? Results.NoContent() : ApiProblems.NotFound("No unread notification has that id.");

    private static NotificationSettingsDto Dto(NotificationSettings s) => new(
        s.QuietFrom?.ToString("HH:mm", CultureInfo.InvariantCulture),
        s.QuietTo?.ToString("HH:mm", CultureInfo.InvariantCulture),
        [.. s.Choices.Select(c => new ChannelChoiceDto(c.Kind, c.Channel, c.Enabled))]);

    private static TimeOnly? Time(string? text, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            return time;
        }

        errors[field] = ["A local time as HH:mm."];
        return null;
    }

    private static UserId Actor(ClaimsPrincipal user) =>
        new(user.GetUserId() ?? throw new InvalidOperationException("An authorised request has a user."));
}
