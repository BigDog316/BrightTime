using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using BrightTime.Seed;

namespace BrightTime.Web.Tests;

/// <summary>
/// The organisation's badges in the provider console (D85): written as a draft, changed, published
/// (after which it can't change), awarded to a young person enrolled with their own account, revoked
/// with a reason, and retired; the young person is told of the award and the revocation; and nobody
/// outside the organisation gets in.
/// </summary>
/// <remarks>
/// The provider is the messaging tests' (see <see cref="YouthEnrolment.SecondListing"/>), whose
/// badges other tests make too: this one finds its own by name. The stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class BadgePagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    /// <summary>A grey square PNG, with a text chunk an editor might add.</summary>
    private static byte[] Png(int side, string text)
    {
        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)side);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)side);
        header[8] = 8;
        Chunk(png, "IHDR", header);
        Chunk(png, "tEXt", Encoding.Latin1.GetBytes("Comment\0" + text));
        using MemoryStream packed = new();
        using (ZLibStream z = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < side; y++)
            {
                z.WriteByte(0);
                z.Write(Enumerable.Repeat((byte)0x80, side).ToArray());
            }
        }

        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream to, string type, byte[] data)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            to.Write(length);
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            to.Write(typed);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in typed)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            byte[] check = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(check, ~crc);
            to.Write(check);
        }
    }

    [Fact]
    public async Task Staff_write_publish_award_revoke_and_retire_a_badge()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        (SeedOrganisation provider, SeedListing listing) = YouthEnrolment.SecondListing(db);
        Guid admin = SeedIds.For("user/" + AdminOf(provider));
        Guid youth = await YouthEnrolment.AccountAsync(db, "badges");
        Guid enrolled = await YouthEnrolment.EnrolAsync(db, youth, admin, provider, listing, "Yaw", linked: true);
        string badges = $"/provider/orgs/{provider.Id}/badges";
        string name = "Stage Crew " + Guid.NewGuid().ToString("N")[..6];

        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));
        (await staff.GetAsync(badges)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain($"href=\"{badges}\" aria-current=\"page\"", Case.Sensitive, "the tab shows, current");
        staff.Html.ShouldNotContain("provider.badges.");

        // Written as a draft: criteria too short are refused and the words kept; an expiry that isn't a number too.
        (await staff.PostAsync(badges, "create", ("name", name), ("description", "Ran the lights."), ("criteria", "Short."), ("expires", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Write criteria of more than 20 characters");
        html.ShouldContain("Ran the lights.");
        (await staff.PostAsync(badges, "create", ("name", name), ("description", "Ran the lights."), ("criteria", "Ran the lights for a whole show."), ("expires", "ten"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Choose how long it lasts");
        HttpResponseMessage created = await staff.PostAsync(badges, "create", ("name", name), ("description", "Ran the lights."), ("criteria", "Ran the lights for a whole show."), ("expires", "24"));
        created.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string page = created.Headers.Location!.AbsolutePath;
        Guid badge = Guid.Parse(page[(page.LastIndexOf('/') + 1)..]);
        created.Headers.Location!.PathAndQuery.ShouldBe($"{badges}/{badge}?done=created");

        // Its own image, while a draft (D87): a file that isn't one is refused; a PNG is kept without its text.
        await staff.GetAsync(page);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("It has a plain image made from its name.");
        WebUtility.HtmlDecode(staff.Html).ShouldContain("src=\"data:image/svg+xml;base64,", Case.Sensitive, "the generated picture, previewed");
        (await staff.PostFileAsync(page, "image", ("image", "notes.png", "not a picture"u8.ToArray(), "image/png"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Choose a PNG or JPEG of up to 512 KB");
        await staff.GetAsync(page);
        (await staff.PostFileAsync(page, "image", ("image", "lights.png", Png(120, "Drawn at 4 Mill Lane"), "image/png"))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=image");
        await staff.GetAsync(page);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("It has its own image.");
        WebUtility.HtmlDecode(staff.Html).ShouldContain("src=\"data:image/png;base64,");
        (await db.ScalarAsync<bool>("select position('Mill Lane'::bytea in bytes) = 0 from learning.badge_image where badge_class_id = @b", ("b", badge))).ShouldBeTrue();
        (await staff.PostAsync(page, "image-remove")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=image_removed");
        (await db.ScalarAsync<long>("select count(*) from learning.badge_image where badge_class_id = @b", ("b", badge))).ShouldBe(0);
        await staff.GetAsync(page);
        await staff.PostFileAsync(page, "image", ("image", "lights.png", Png(120, "again"), "image/png"));

        // A draft changes; published, it can't, and it can be awarded.
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "save", ("name", name), ("description", "Ran the lights and sound."), ("criteria", "Ran the lights for a whole show."), ("expires", "24")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=saved");
        await staff.GetAsync(page);
        (await staff.PostAsync(page, "publish")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=published");
        await staff.GetAsync($"{page}?done=published");
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Published. You can award it now.");
        html.ShouldContain("Ran the lights and sound.");
        html.ShouldContain("Lasts 24 months");
        html.ShouldNotContain("formname=\"save\"", Case.Sensitive, "a published badge has no form to change it");
        html.ShouldNotContain("formname=\"image\"", Case.Sensitive, "nor its picture");
        html.ShouldContain("src=\"data:image/png;base64,");
        html.ShouldContain($"value=\"{enrolled}\"");
        html.ShouldContain("Yaw M");

        // Awarded: they're told, and it shows as held.
        (await staff.PostAsync(page, "award", ("enrollment", enrolled.ToString()), ("narrative", "Ran every cue on opening night."))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=awarded");
        Guid award = await db.ScalarAsync<Guid>("select id from learning.badge_assertion where badge_class_id = @b and recipient_user_id = @u", ("b", badge), ("u", youth));
        await staff.GetAsync(page);
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Held by 1 young person");
        html.ShouldContain("Expires ");

        // Awarding again is refused while they hold it.
        (await staff.PostAsync(page, "award", ("enrollment", enrolled.ToString()), ("narrative", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("already holds");

        // Revoked: a reason is needed, and they're told without it.
        await staff.GetAsync(page);
        string revoke = "revoke-" + award.ToString("N");
        (await staff.PostAsync(page, revoke, ("award", award.ToString()), ("reason", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await staff.GetAsync(page);
        (await staff.PostAsync(page, revoke, ("award", award.ToString()), ("reason", "Awarded to the wrong person."))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=revoked");
        await staff.GetAsync(page);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Awarded to the wrong person.");
        string told = await db.ScalarAsync<string>(
            """
            select string_agg(substring(payload::text from 'notify\.badge\.[a-z_]+'), ',' order by id)
            from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u
            """,
            ("u", "%" + youth + "%"));
        told.ShouldBe("notify.badge.awarded,notify.badge.revoked");
        (await db.ScalarAsync<long>(
            "select count(*) from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u and payload::text like '%wrong person%'",
            ("u", "%" + youth + "%"))).ShouldBe(0);

        // Retired: nobody new gets it.
        (await staff.PostAsync(page, "retire")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=retired");
        await staff.GetAsync($"{page}?done=retired");
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Retired. Nobody new can be given it.");
        html.ShouldNotContain("formname=\"award\"");
        await staff.GetAsync(badges);
        WebUtility.HtmlDecode(staff.Html).ShouldContain(name);

        // Another organisation's admin is turned away from both pages.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain(name);
    }
}
