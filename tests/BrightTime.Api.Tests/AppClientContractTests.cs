using System.Globalization;
using BrightTime.Localisation;
using BrightTime.Ui.Shared;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;
using BrightTime.Ui.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// The app's own client (BrightTime.Ui.Shared) against the real API: every call it makes, and
/// every shape it reads. The app's records are written separately from the server's, so this is
/// what keeps the two in step.
/// </summary>
/// <remarks>Admins 54 and 57 are parents; the young person is registered fresh.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class AppClientContractTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    /// <summary>The app's services, as the MAUI host makes them, with in-memory device stores.</summary>
    private ServiceProvider App() =>
        new ServiceCollection()
            .AddLogging()
            .AddBrightTimeApp(new AppEndpoints(new Uri("https://kettle.localhost/api/v1/"), new Uri("https://kettle.localhost/")), _ => api.AppClient())
            .BuildServiceProvider();

    private static T Ok<T>(ApiResult<T> result)
    {
        result.Problem.ShouldBeNull();
        return result.Value!;
    }

    private static void Ok(ApiResult result) => result.Problem.ShouldBeNull();

    [Fact]
    public async Task The_app_reads_the_public_finder_and_words_its_why()
    {
        RequireDatabase();
        await using ServiceProvider app = App();
        AppState state = app.GetRequiredService<AppState>();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();
        Text t = app.GetRequiredService<Text>();

        await state.StartAsync(Ct);
        state.IsReady.ShouldBeTrue();
        app.GetRequiredService<AppRegion>().Name.ShouldNotBeNullOrEmpty();

        Page<ListingSummary> soon = Ok(await client.SearchAsync(SearchFilters.StartingSoon, ct: Ct));
        soon.Items.ShouldNotBeEmpty();
        Page<ListingSummary> found = Ok(await client.SearchAsync(new SearchFilters { Text = "soccer" }, ct: Ct));
        found.Items.SelectMany(i => i.Why).Select(p => Words.Phrase(t, p)).ShouldAllBe(s => s.Length > 0, "every 'why' the API sends is in the app's catalogue");

        ListingDetail detail = Ok(await client.ListingAsync(soon.Items[0].Slug, Ct));
        detail.Id.ShouldBe(soon.Items[0].Id);
        Ok(await client.NeighbourhoodsAsync(Ct)).ShouldNotBeEmpty();
        Ok(await client.TermsAsync("category", Ct)).ShouldNotBeEmpty();

        if (soon.NextCursor is { } next)
        {
            Ok(await client.SearchAsync(SearchFilters.StartingSoon, next, Ct)).Items.ShouldNotContain(i => i.Id == soon.Items[0].Id);
        }
    }

    [Fact]
    public async Task A_parent_signs_in_keeps_their_session_and_uses_every_personal_call()
    {
        RequireDatabase();
        string email = Admin(54);
        string password = await api.SetPasswordAsync(email);
        await using ServiceProvider app = App();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        AppSession session = app.GetRequiredService<AppSession>();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();
        ISecretStore secrets = app.GetRequiredService<ISecretStore>();

        (await session.SignInAsync(email, "not the password", Ct)).ShouldNotBeNull();
        (await session.SignInAsync(email, password, Ct)).ShouldBeNull();
        session.Me!.IsAdult.ShouldBeTrue();
        session.Capabilities.ShouldNotBeNull();
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldNotBeNullOrEmpty("an adult stays signed in");

        // The finder's personal side.
        Ok(await client.FeedAsync(ct: Ct));
        ListingSummary listing = Ok(await client.SearchAsync(SearchFilters.StartingSoon, ct: Ct)).Items[0];
        Ok(await client.SaveAsync(listing.Id, ct: Ct));
        Ok(await client.SavedAsync(Ct)).ShouldContain(s => s.Listing.Id == listing.Id);
        Ok(await client.UnsaveAsync(listing.Id, Ct));

        Term interest = Ok(await client.TermsAsync("interest", Ct))[0];
        Ok(await client.SetInterestsAsync([(interest.Code, 3)], Ct));
        Ok(await client.InterestsAsync(Ct)).ShouldHaveSingleItem().Code.ShouldBe(interest.Code);

        NeighbourhoodSummary hood = Ok(await client.NeighbourhoodsAsync(Ct))[0];
        Ok(await client.SetSearchPreferencesAsync(hood.Code, null, Ct));
        Ok(await client.SearchPreferencesAsync(Ct)).NearNeighbourhood.ShouldBe(hood.Code);

        if (listing.NextStartAt is { } start && start > DateTimeOffset.UtcNow.AddHours(2))
        {
            Reminder reminder = Ok(await client.RemindAsync(listing.Id, "next_session", 60, Ct));
            Ok(await client.RemindersAsync(Ct)).ShouldContain(r => r.Id == reminder.Id);
            Ok(await client.ForgetReminderAsync(reminder.Id, Ct));
        }

        // Notifications.
        Ok(await client.InboxAsync(Ct));
        NotificationSettings settings = Ok(await client.NotificationSettingsAsync(Ct));
        NotificationSettings quiet = Ok(await client.SetNotificationSettingsAsync(settings with { QuietFrom = "21:00", QuietTo = "07:00" }, Ct));
        quiet.QuietFrom.ShouldBe("21:00");

        // A child, its disclosures, and the claim that waits for 13.
        Child child = Ok(await client.CreateChildAsync("Ada", DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM", CultureInfo.InvariantCulture), "parent", Ct));
        child.Link.Status.ShouldBe("pending");
        Ok(await client.ChildAsync(child.Id, Ct)).GivenName.ShouldBe("Ada");
        Ok(await client.ChildrenAsync(Ct)).ShouldContain(c => c.Id == child.Id);
        Ok(await client.DisclosuresAsync(Ct)).ShouldContain(d => d.Scope == "account" && d.Hash.Length == 64);
        ApiResult early = await client.ClaimAsync(child.Id, "kid@example.net", Ct);
        early.Problem!.Code.ShouldBe("consent.not_guardian", "a pending link is no verified guardian yet");

        // Refresh rotates; signing out forgets.
        (await session.RefreshAsync(Ct)).ShouldBeTrue();
        await session.SignOutAsync(Ct);
        session.IsSignedIn.ShouldBeFalse();
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldBeNull();
        (await client.SavedAsync(Ct)).Problem!.Status.ShouldBe(401);
    }

    [Fact]
    public async Task A_parent_adds_a_passkey_in_the_app_and_signs_in_with_it_later()
    {
        RequireDatabase();
        string email = Admin(57);
        string password = await api.SetPasswordAsync(email);
        using BrightTime.Web.Tests.SoftwarePasskey key = new("https://kettle.localhost", "kettle.localhost");
        SoftwareAuthenticator authenticator = new(key);
        await using ServiceProvider app = App();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        AppSession session = app.GetRequiredService<AppSession>();
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        (await session.SignInAsync(email, password, Ct)).ShouldBeNull();
        PasskeyCeremony ceremony = Ok(await client.PasskeyRegistrationAsync(Ct));
        PasskeyOutcome made = await authenticator.CreateAsync(ceremony.Options.GetRawText(), Ct);
        Ok(await client.AddPasskeyAsync(ceremony.State, made.CredentialJson!, "Pixel", Ct));
        Ok(await client.PasskeysAsync(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Pixel");

        // The terms, read and agreed to through the app's client.
        TermsDocument terms = Ok(await client.TermsAsync(Ct));
        Ok(await client.AcceptTermsAsync(terms.Hash, Ct)).AcceptedAt.ShouldNotBeNull();
        Ok(await client.MyTermsAsync(Ct)).Hash.ShouldBe(terms.Hash);

        await session.SignOutAsync(Ct);
        (await session.SignInWithPasskeyAsync(authenticator, Ct)).ShouldBeNull();
        session.Me!.UserName.ShouldBe(email, StringCompareShould.IgnoreCase);
    }

    /// <summary>The software authenticator behind the app's passkey interface, as Credential Manager would answer.</summary>
    private sealed class SoftwareAuthenticator(BrightTime.Web.Tests.SoftwarePasskey key) : IPasskeyAuthenticator
    {
        public bool IsSupported => true;

        public Task<PasskeyOutcome> CreateAsync(string optionsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PasskeyOutcome(key.Create(System.Text.Json.Nodes.JsonNode.Parse(optionsJson)!).ToJsonString()));

        public Task<PasskeyOutcome> GetAsync(string optionsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PasskeyOutcome(key.Get(System.Text.Json.Nodes.JsonNode.Parse(optionsJson)!).ToJsonString()));
    }

    [Fact]
    public async Task A_young_person_registers_signs_in_and_is_not_remembered_on_the_device()
    {
        RequireDatabase();
        await using ServiceProvider app = App();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();
        AppSession session = app.GetRequiredService<AppSession>();

        string child = $"child-{Guid.NewGuid():N}@example.net";
        (await client.RegisterAsync(child, DateTime.UtcNow.AddYears(-10).ToString("yyyy-MM", CultureInfo.InvariantCulture), Ct)).Problem!.Code.ShouldBe("register.under_13");

        string teen = $"teen-{Guid.NewGuid():N}@example.net";
        Ok(await client.RegisterAsync(teen, DateTime.UtcNow.AddYears(-15).ToString("yyyy-MM", CultureInfo.InvariantCulture), Ct));
        await api.Mail.WaitForAsync(teen);
        string password = await api.SetPasswordAsync(teen);

        (await session.SignInAsync(teen, password, Ct)).ShouldBeNull();
        session.Me!.IsYouth.ShouldBeTrue();
        (await app.GetRequiredService<ISecretStore>().GetAsync(AppSession.RefreshTokenKey)).ShouldBeNull("a young person's device is often shared");
        Ok(await client.FeedAsync(ct: Ct));
    }
}
