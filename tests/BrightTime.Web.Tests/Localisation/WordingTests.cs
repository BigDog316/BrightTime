using System.Net;
using BrightTime.Abstractions.Messaging;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests.Localisation;

/// <summary>
/// A tenant's own wording over the shipped catalogue, and text that leaves the site (email) in
/// the reader's language. Against the seeded database.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class WordingTests(ConsoleFixture db)
{
    // A string no other test reads, in a locale no other test renders from this database.
    private const string Key = "site.not_found.body";
    private const string Locale = "ht";

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task An_organiser_rewords_a_sentence_and_the_site_shows_it_at_once_then_goes_back()
    {
        RequireDatabase();
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(KettleAccounts.OrganiserEmail);

        string edit = $"/organiser/wording?locale={Locale}&key={Key}";
        (await organiser.GetAsync(edit)).StatusCode.ShouldBe(HttpStatusCode.OK, organiser.Html);
        const string ours = "Nou pa jwenn paj sa a nan Konte Kettle.";
        HttpResponseMessage saved = await organiser.PostAsync(edit, "set-override", ("key", Key), ("locale", Locale), ("value", ours));
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect, organiser.Html);

        using Browser visitor = db.NewBrowser();
        (await visitor.GetAsync("/not-found?lang=" + Locale)).StatusCode.ShouldBe(HttpStatusCode.OK);
        visitor.Html.ShouldContain(ours);

        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'wording.override_set' and actor_id = @a and detail->>'key' = @k and detail->>'value' = @v",
            ("a", KettleAccounts.OrganiserId), ("k", Key), ("v", ours))).ShouldBe(1);

        await organiser.GetAsync(edit);
        (await organiser.PostAsync(edit, "clear-override", ("key", Key), ("locale", Locale))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await visitor.GetAsync("/not-found?lang=" + Locale);
        visitor.Html.ShouldNotContain(ours);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'wording.override_cleared' and detail->>'key' = @k", ("k", Key))).ShouldBe(1);
    }

    [Fact]
    public async Task Wording_that_drops_a_placeholder_is_refused_with_the_reason()
    {
        RequireDatabase();
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync("organiser2@kettle.example.org");

        const string key = "listing.byline";
        string edit = $"/organiser/wording?locale=es&key={key}";
        await organiser.GetAsync(edit);
        HttpResponseMessage refused = await organiser.PostAsync(edit, "set-override", ("key", key), ("locale", "es"), ("value", "De una organización"));

        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("{organisation} is missing");
        (await db.ScalarAsync<long>("select count(*) from platform.resource_override where key = @k", ("k", key))).ShouldBe(0);
    }

    [Fact]
    public async Task Only_people_who_manage_the_tenant_may_change_its_wording()
    {
        RequireDatabase();
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync("admin@kettle-county-youth-orchestra.example.org");

        HttpResponseMessage page = await provider.GetAsync("/organiser/wording");

        page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        page.Headers.Location!.ToString().ShouldContain("access-denied");
    }

    [Fact]
    public async Task A_reset_email_is_in_the_accounts_language_or_failing_that_the_forms()
    {
        RequireDatabase();
        string email = "admin@casa-de-la-comunidad-kettleton.example.org";
        Guid user = SeedIds.For("user/" + email);

        // No language of its own: the form's.
        await db.ScalarAsync<int>("with u as (update identity.app_user set locale = null where id = @id returning 1) select count(*)::int from u", ("id", user));
        EmailMessage spanish = await RequestResetAsync(email, "es");
        spanish.Subject.ShouldBe("Restablece tu contraseña de BrightTime");
        spanish.TextBody.ShouldContain("Si fuiste tú");
        spanish.HtmlBody.ShouldContain("lang=\"es\" dir=\"ltr\"");

        // Its own language wins: the email goes to the account's owner, who may not be whoever typed the address.
        await db.ScalarAsync<int>("with u as (update identity.app_user set locale = 'ar' where id = @id returning 1) select count(*)::int from u", ("id", user));
        EmailMessage arabic = await RequestResetAsync(email, "es");
        arabic.Subject.ShouldBe("إعادة تعيين كلمة مرور BrightTime");
        arabic.HtmlBody.ShouldContain("lang=\"ar\" dir=\"rtl\"");
    }

    private async Task<EmailMessage> RequestResetAsync(string email, string formLanguage)
    {
        int seen = db.Mail.To(email).Count;
        using Browser browser = db.NewBrowser();
        await browser.GetAsync("/account/forgot?lang=" + formLanguage);
        await browser.PostAsync("/account/forgot", "forgot", ("email", email));
        EmailMessage mail = (await db.Mail.WaitForAsync(email, seen)).ShouldNotBeNull();

        // Resets are throttled per account; let the next request through.
        await db.ScalarAsync<int>(
            "with t as (delete from identity.app_user_token where user_id = @id returning 1) select count(*)::int from t",
            ("id", SeedIds.For("user/" + email)));
        return mail;
    }
}
