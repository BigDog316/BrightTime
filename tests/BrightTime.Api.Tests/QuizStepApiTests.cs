using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// A quiz step (D92): an organisation writes one (a bad one refused), a young person reads its
/// questions without the answers, tries it short of the pass mark and is told how they did, then
/// passes it, finishing the playlist. Their answers are never kept.
/// </summary>
/// <remarks>
/// Uses the sixteenth organisation with no staff (the other API tests take the third to fifteenth).
/// The young person is a fresh adult account.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class QuizStepApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private Guid Organisation()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        return api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => l.OrgId).Distinct().Skip(15).First();
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

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

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static readonly object[] Questions =
    [
        new { prompt = "Which instrument has the lowest notes?", options = new[] { "Violin", "Double bass", "Flute" }, correct = 1 },
        new { prompt = "Who keeps the orchestra together?", options = new[] { "The conductor", "The audience" }, correct = 0 },
        new { prompt = "How many strings has a cello?", options = new[] { "Three", "Four", "Six" }, correct = 1 },
    ];

    [Fact]
    public async Task A_quiz_is_marked_kept_only_as_a_score_and_passing_it_finishes_the_playlist()
    {
        RequireDatabase();
        Guid org = Organisation();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient learner = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anyone = api.Client();

        // Written: a question with one answer, or none marked right, is refused; three good ones at 60 per cent are kept.
        Guid playlist = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists",
            new { title = "Orchestra Basics " + Guid.NewGuid().ToString("N")[..6], isOrdered = false }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        string steps = $"api/v1/org/{org}/playlists/{playlist}/steps";
        await ProblemAsync(await admin.PutAsJsonAsync(steps, new { steps = new[] { new { kind = "quiz", title = "Quiz", questions = new[] { new { prompt = "Yes?", options = new[] { "Yes" }, correct = 0 } }, passPercent = 100 } } }, Ct),
            HttpStatusCode.BadRequest, "playlists.step_quiz");
        await ProblemAsync(await admin.PutAsJsonAsync(steps, new { steps = new[] { new { kind = "quiz", title = "Quiz", questions = new[] { new { prompt = "Which?", options = new[] { "A", "B" }, correct = (int?)null } }, passPercent = 100 } } }, Ct),
            HttpStatusCode.BadRequest, "playlists.step_quiz");
        JsonElement written = await JsonAsync(await admin.PutAsJsonAsync(steps, new { steps = new[] { new { kind = "quiz", title = "Know your orchestra", questions = Questions, passPercent = 60 } } }, Ct));
        JsonElement quiz = written.GetProperty("steps")[0];
        Guid step = quiz.GetProperty("id").GetGuid();
        (quiz.GetProperty("questions").GetArrayLength(), quiz.GetProperty("questions")[0].GetProperty("correct").GetInt32(), quiz.GetProperty("passPercent").GetInt32())
            .ShouldBe((3, 1, 60), "the organisation sees which answers are right");
        JsonElement published = await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/publish"), null, Ct));
        string slug = published.GetProperty("slug").GetString()!;

        // Read by anyone, or by the young person: the questions, never the answers.
        JsonElement open = await JsonAsync(await anyone.GetAsync(Relative($"api/v1/public/playlists/{slug}"), Ct));
        open.GetProperty("steps")[0].GetProperty("questions")[0].GetProperty("correct").ValueKind.ShouldBe(JsonValueKind.Null);
        JsonElement mine = await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{playlist}/enroll"), null, Ct));
        mine.GetProperty("steps")[0].GetProperty("step").GetProperty("questions")[2].GetProperty("correct").ValueKind.ShouldBe(JsonValueKind.Null);

        // Every question must be answered, with an answer it has.
        string done = $"api/v1/me/playlists/{playlist}/steps/{step}";
        await ProblemAsync(await learner.PutAsJsonAsync(done, new { }, Ct), HttpStatusCode.BadRequest, "playlists.quiz_answers");
        await ProblemAsync(await learner.PutAsJsonAsync(done, new { answers = new[] { 1, 0 } }, Ct), HttpStatusCode.BadRequest, "playlists.quiz_answers");
        await ProblemAsync(await learner.PutAsJsonAsync(done, new { answers = new[] { 1, 0, 3 } }, Ct), HttpStatusCode.BadRequest, "playlists.quiz_answers");

        // One right of three, short of 60 per cent (two): a try, open for another, with how they did.
        JsonElement tried = (await JsonAsync(await learner.PutAsJsonAsync(done, new { answers = new[] { 0, 0, 0 } }, Ct))).GetProperty("steps")[0];
        (tried.GetProperty("state").GetString(), tried.GetProperty("quizCorrect").GetInt32(), tried.GetProperty("quizAttempts").GetInt32()).ShouldBe(("in_progress", 1, 1));

        // Two right: passed, and the playlist with it.
        JsonElement passed = await JsonAsync(await learner.PutAsJsonAsync(done, new { answers = new[] { 1, 0, 0 } }, Ct));
        (passed.GetProperty("steps")[0].GetProperty("state").GetString(), passed.GetProperty("steps")[0].GetProperty("quizCorrect").GetInt32(), passed.GetProperty("steps")[0].GetProperty("quizAttempts").GetInt32())
            .ShouldBe(("completed", 2, 2));
        passed.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
        await ProblemAsync(await learner.PutAsJsonAsync(done, new { answers = new[] { 1, 0, 1 } }, Ct), HttpStatusCode.Conflict, "playlists.step_done");

        // Kept as a score: nothing of what they chose.
        (await api.ScalarAsync<string>("select row_to_json(p)::text from learning.playlist_step_progress p where step_id = @s", ("s", step)))
            .ShouldNotContain("[1,0,0]");
        (await api.ScalarAsync<long>(
            "select count(*) from information_schema.columns where table_schema = 'learning' and table_name = 'playlist_step_progress' and column_name like '%answer%'")).ShouldBe(0);
    }
}
