using System.Net;
using System.Net.Http.Json;
using BrightTime.Abstractions.Messaging;
using BrightTime.Hosting.Auditing;

namespace BrightTime.Api.Tests;

/// <summary>
/// A guardian's adult account from the app, made as D23 makes an applicant's: the answer is the
/// same whether or not the address had an account, and the way in is an emailed link.
/// </summary>
[Collection(ApiDatabase.Name)]
public sealed class RegistrationTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private async Task<(HttpStatusCode Status, string Body)> RegisterAsync(string email)
    {
        using HttpClient client = api.Client();
        HttpResponseMessage response = await client.PostAsJsonAsync("api/v1/auth/register", new { email }, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Registering_answers_alike_for_a_new_address_and_one_with_an_account()
    {
        RequireDatabase();
        string fresh = $"guardian-{Guid.NewGuid():N}@example.org";
        string existing = Admin(37);

        (HttpStatusCode Status, string Body) made = await RegisterAsync(fresh);
        (HttpStatusCode Status, string Body) found = await RegisterAsync(existing);

        made.Status.ShouldBe(HttpStatusCode.Accepted);
        found.ShouldBe(made);

        // The new address has an adult account with no password, and a link to set one.
        EmailMessage welcome = await api.Mail.WaitForAsync(fresh);
        welcome.TextBody.ShouldContain("/account/reset?token=");
        welcome.TextBody.ShouldContain("probably you");
        (await api.ScalarAsync<string>("select account_kind from identity.app_user where normalized_email = upper(@e)", ("e", fresh))).ShouldBe("adult");
        (await api.ScalarAsync<bool>("select password_hash is null and not email_confirmed from identity.app_user where normalized_email = upper(@e)", ("e", fresh))).ShouldBeTrue();

        // The existing account is told so, in the email only its owner reads, with a reset link.
        EmailMessage notice = await api.Mail.WaitForAsync(existing);
        notice.Subject.ShouldBe(welcome.Subject);
        notice.TextBody.ShouldContain("There is already one for that address");
        notice.TextBody.ShouldContain("/account/reset?token=");

        Guid id = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", fresh));
        for (int i = 0; i < 100 && await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and subject_ids = array[@u]", ("a", AuthAudit.RegistrationRequested), ("u", id)) == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        (await api.ScalarAsync<string>("select detail->>'created' from platform.audit_event where action = @a and subject_ids = array[@u]", ("a", AuthAudit.RegistrationRequested), ("u", id)))
            .ShouldBe("true");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("Someone <someone@example.org>")]
    public async Task Registering_needs_an_email_address(string email)
    {
        RequireDatabase();

        (HttpStatusCode status, string body) = await RegisterAsync(email);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("urn:brighttime:problem:validation");
    }
}
