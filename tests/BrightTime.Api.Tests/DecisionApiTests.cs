using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Decisions, seats and waitlists (M3a, D48): an organisation's staff accept, waitlist and decline;
/// the last seat goes to exactly one applicant however the requests race; a freed seat is offered
/// to the front of the waitlist, and the family takes it or leaves it.
/// </summary>
/// <remarks>
/// The provider is the first staff account from 13 on whose approved organisation has a builtin
/// listing that the application and waiver tests don't use; the next two staff accounts are the
/// families.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class DecisionApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private List<(string Email, Guid Org)> AllStaff() =>
        [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => (u.Email, u.Memberships.Single().OrgId))];

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private bool Builtin(Guid org, out Guid listing)
    {
        var found = api.Data.Listings.FirstOrDefault(l => l.OrgId == org && l.Status == "published" && l.RegistrationMode == "builtin");
        listing = found?.Id ?? Guid.Empty;
        return found is not null && api.Data.Organisations.Single(o => o.Id == org).Status == "approved";
    }

    private (int Provider, Guid Org, Guid Listing) Provider()
    {
        List<(string Email, Guid Org)> staff = AllStaff();
        HashSet<Guid> taken = [.. staff.Take(13).Select(s => s.Org)];
        // The organisations ApplicationApiTests and WaiverApiTests take.
        foreach (Guid other in api.Data.Listings.Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !taken.Contains(l.OrgId)
                     && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved").Select(l => l.OrgId).Distinct().Take(2).ToList())
        {
            taken.Add(other);
        }

        for (int i = 13; i < staff.Count; i++)
        {
            if (!taken.Contains(staff[i].Org) && Builtin(staff[i].Org, out Guid listing))
            {
                return (i, staff[i].Org, listing);
            }
        }

        throw new InvalidOperationException("The seed has no spare builtin listing with staff.");
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    /// <summary>A child added to the family and an application for them, submitted unless asked not to.</summary>
    private static async Task<Guid> ApplyAsync(HttpClient family, Guid listing, string first, bool submit = true)
    {
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = first, lastName = "Mensah", dateOfBirth = "2014-06-01" }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "decide-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await family.SendAsync(request, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        if (submit)
        {
            await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        }

        return application;
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient staff, Guid org, Guid application, string decision) =>
        staff.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision }, Ct);

    [Fact]
    public async Task The_last_seat_goes_to_one_applicant_and_a_freed_seat_goes_to_the_waitlist()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing) = Provider();
        List<(string Email, Guid Org)> staff = AllStaff();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient decider = await SignedInAsync(staff[index].Email);
        using HttpClient otherDecider = await SignedInAsync(staff[index].Email);
        using HttpClient familyA = await SignedInAsync(staff[index + 1].Email);
        using HttpClient familyB = await SignedInAsync(staff[index + 2].Email);

        // One seat, one waitlist place; staff decide, only admins set seats.
        await ProblemAsync(await decider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 1, waitlistCapacity = 1 }, Ct), HttpStatusCode.Forbidden, "form.forbidden");
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 1, waitlistCapacity = 1 }, Ct));

        await JsonAsync(await familyA.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct));
        await JsonAsync(await familyB.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        Guid ada = await ApplyAsync(familyA, listing, "Ada");
        Guid ben = await ApplyAsync(familyB, listing, "Ben");
        Guid cy = await ApplyAsync(familyA, listing, "Cy");
        Guid draft = await ApplyAsync(familyB, listing, "Dee", submit: false);

        // The queue: submitted applications only, oldest first, named as the families shared. The other
        // test in this class applies to the same listing, and may run first, so only this test's count.
        HashSet<Guid> mine = [ada, ben, cy, draft];
        JsonElement[] queue = [.. (await JsonAsync(await decider.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/applications", UriKind.Relative), Ct))).EnumerateArray()
            .Where(a => mine.Contains(a.GetProperty("id").GetGuid()))];
        queue.Select(a => a.GetProperty("id").GetGuid()).ShouldBe([ada, ben, cy]);
        queue[0].GetProperty("firstName").GetString().ShouldBe("Ada");
        queue[2].GetProperty("firstName").GetString().ShouldBe("Cy", "a second child's submission joins the family's share instead of replacing it");
        await ProblemAsync(await decider.GetAsync(new Uri($"api/v1/org/{org}/applications/{draft}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "decision.not_found");

        // Two staff accept different applicants for the one seat at once: exactly one wins.
        HttpResponseMessage[] raced = await Task.WhenAll(DecideAsync(decider, org, ada, "accept"), DecideAsync(otherDecider, org, ben, "accept"));
        raced.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        HttpResponseMessage lost = raced.Single(r => r.StatusCode != HttpStatusCode.OK);
        await ProblemAsync(lost, HttpStatusCode.Conflict, "seats.full");
        Guid winner = raced[0].StatusCode == HttpStatusCode.OK ? ada : ben;
        Guid loser = winner == ada ? ben : ada;
        (HttpClient winnerFamily, HttpClient loserFamily) = winner == ada ? (familyA, familyB) : (familyB, familyA);

        JsonElement waiting = await JsonAsync(await DecideAsync(decider, org, loser, "waitlist"));
        waiting.GetProperty("status").GetString().ShouldBe("waitlisted");
        waiting.GetProperty("waitlistPosition").GetInt32().ShouldBe(1);
        await ProblemAsync(await DecideAsync(decider, org, cy, "waitlist"), HttpStatusCode.Conflict, "seats.waitlist_full");
        (await JsonAsync(await decider.PostAsJsonAsync($"api/v1/org/{org}/applications/{cy}/decide", new { decision = "decline", note = "The session is full." }, Ct)))
            .GetProperty("status").GetString().ShouldBe("declined");
        await ProblemAsync(await decider.PostAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waitlist/offer", new { count = 1 }, Ct), HttpStatusCode.Conflict, "seats.full");

        JsonElement seats = await JsonAsync(await decider.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/seats", UriKind.Relative), Ct));
        (seats.GetProperty("seatsTaken").GetInt32(), seats.GetProperty("waitlistTaken").GetInt32()).ShouldBe((1, 1));
        await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 1, waitlistCapacity = 0 }, Ct), HttpStatusCode.Conflict, "seats.below_taken");

        // The winner's family withdraws; the seat is free, and goes to the front of the waitlist.
        (await winnerFamily.PostAsync(new Uri($"api/v1/applications/{winner}/withdraw", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.ScalarAsync<string>("select end_reason from enrollment.enrollment where application_id = @a", ("a", winner))).ShouldBe("family_withdrew");
        JsonElement offered = await JsonAsync(await decider.PostAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waitlist/offer", new { count = 5 }, Ct));
        offered.GetProperty("offered").EnumerateArray().Select(o => o.GetGuid()).ShouldBe([loser]);
        seats = await JsonAsync(await decider.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/seats", UriKind.Relative), Ct));
        (seats.GetProperty("seatsTaken").GetInt32(), seats.GetProperty("waitlistTaken").GetInt32()).ShouldBe((1, 0));

        (await JsonAsync(await loserFamily.PostAsync(new Uri($"api/v1/applications/{loser}/offer/accept", UriKind.Relative), null, Ct)))
            .GetProperty("status").GetString().ShouldBe("accepted");
        (await api.ScalarAsync<string>("select seat_kind || '/' || status from enrollment.enrollment where application_id = @a and status = 'active'", ("a", loser)))
            .ShouldBe("enrolled/active");
        await ProblemAsync(await loserFamily.PostAsync(new Uri($"api/v1/applications/{loser}/offer/accept", UriKind.Relative), null, Ct), HttpStatusCode.Conflict, "application.no_offer");

        // Decisions only fit where an application stands; a bulk decision reports each one.
        await ProblemAsync(await DecideAsync(decider, org, winner, "accept"), HttpStatusCode.Conflict, "decision.not_allowed");
        await ProblemAsync(await DecideAsync(decider, org, cy, "maybe"), HttpStatusCode.BadRequest, "decision.kind");
        JsonElement[] bulk = [.. (await JsonAsync(await decider.PostAsJsonAsync($"api/v1/org/{org}/applications/bulk-decide",
            new { ids = new[] { cy, Guid.NewGuid() }, decision = "review" }, Ct))).EnumerateArray()];
        bulk.Select(b => b.GetProperty("error").GetString()).ShouldBe(["decision.not_allowed", "decision.not_found"]);

        // Without a live share, the organisation sees an application but not who it's from.
        string shareId = (await familyB.GetFromJsonAsync<JsonElement>("api/v1/family-profile/shares", Ct)).EnumerateArray()
            .First(s => s.GetProperty("status").GetString() == "active").GetProperty("id").GetString()!;
        (await familyB.DeleteAsync(new Uri($"api/v1/family-profile/shares/{shareId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await JsonAsync(await decider.GetAsync(new Uri($"api/v1/org/{org}/applications/{ben}", UriKind.Relative), Ct))).GetProperty("firstName").ValueKind.ShouldBe(JsonValueKind.Null);

        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.application.decided' and actor_org_id = @o and detail->>'decision' = 'offer'",
            ("o", org))).ShouldBe(1);
    }

    [Fact]
    public async Task Other_organisations_and_families_see_and_decide_nothing()
    {
        RequireDatabase();
        (int index, Guid org, Guid listing) = Provider();
        List<(string Email, Guid Org)> staff = AllStaff();
        (string strangerEmail, Guid strangerOrg) = staff[index + 3];
        using HttpClient family = await SignedInAsync(staff[index + 2].Email);
        using HttpClient stranger = await SignedInAsync(strangerEmail);

        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        Guid application = await ApplyAsync(family, listing, "Efua");

        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/applications", UriKind.Relative), Ct), HttpStatusCode.Forbidden, "decision.forbidden");
        await ProblemAsync(await DecideAsync(stranger, org, application, "accept"), HttpStatusCode.Forbidden, "decision.forbidden");
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/org/{strangerOrg}/applications/{application}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "decision.not_found");
        await ProblemAsync(await family.GetAsync(new Uri($"api/v1/org/{org}/applications/{application}", UriKind.Relative), Ct), HttpStatusCode.Forbidden, "decision.forbidden");

        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.application.decided' and outcome = 'deny' and reason = 'decision.forbidden' and actor_org_id = @o",
            ("o", org))).ShouldBeGreaterThanOrEqualTo(1);
    }
}
