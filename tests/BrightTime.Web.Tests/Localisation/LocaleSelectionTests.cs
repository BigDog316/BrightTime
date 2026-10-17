using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Localisation;
using Microsoft.AspNetCore.Http;

namespace BrightTime.Web.Tests.Localisation;

public sealed class LocaleSelectionTests
{
    private static readonly Locale[] All = [.. Locale.Supported];

    private static (Locale Locale, bool Chosen) Resolve(
        string? query = null, string? cookie = null, string? account = null, string? acceptLanguage = null,
        IReadOnlyList<Locale>? enabled = null, Locale? tenantDefault = null)
    {
        DefaultHttpContext http = new();
        if (query is not null)
        {
            http.Request.QueryString = new QueryString("?lang=" + query);
        }

        if (cookie is not null)
        {
            http.Request.Headers.Cookie = $"{LocaleSelection.CookieName}={cookie}";
        }

        if (acceptLanguage is not null)
        {
            http.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        return LocaleSelection.Resolve(http, enabled ?? All, tenantDefault ?? Locale.English, account);
    }

    [Fact]
    public void A_language_link_wins_and_is_marked_as_a_choice() =>
        Resolve(query: "ar", cookie: "es", account: "bn", acceptLanguage: "zh-CN").ShouldBe((Locale.Arabic, true));

    [Fact]
    public void A_remembered_choice_comes_next() =>
        Resolve(cookie: "es", account: "bn", acceptLanguage: "zh-CN").ShouldBe((Locale.Spanish, false));

    [Fact]
    public void Then_the_accounts_own_language() =>
        Resolve(account: "bn", acceptLanguage: "zh-CN").ShouldBe((Locale.Bengali, false));

    [Fact]
    public void Then_the_browser_by_quality_skipping_what_is_not_offered() =>
        Resolve(acceptLanguage: "fr-FR, fr;q=0.9, es-US;q=0.8, en;q=0.5").ShouldBe((Locale.Spanish, false));

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("zh")]
    public void Any_chinese_reader_gets_the_chinese_on_offer(string tag) =>
        Resolve(acceptLanguage: tag).Locale.ShouldBe(Locale.ChineseSimplified);

    [Fact]
    public void A_language_the_tenant_does_not_publish_in_is_passed_over() =>
        Resolve(query: "ar", acceptLanguage: "ar", enabled: [Locale.English, Locale.Spanish], tenantDefault: Locale.Spanish)
            .ShouldBe((Locale.Spanish, false));

    [Fact]
    public void Nothing_to_go_on_is_the_tenants_default() =>
        Resolve(tenantDefault: Locale.HaitianCreole).ShouldBe((Locale.HaitianCreole, false));
}
