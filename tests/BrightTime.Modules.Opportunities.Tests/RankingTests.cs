using System.Reflection;
using BrightTime.Modules.Opportunities.Search;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class RankingTests
{
    private static RankingFeatures Features(
        double text = 0, double? distance = null, double? days = null, int interest = 0, double popularity = 0) =>
        new(text, distance, days, interest, popularity);

    [Fact]
    public void The_score_stays_between_zero_and_one()
    {
        RelevanceScorer.Score(Features(text: 9, distance: 0, days: 0, interest: 99, popularity: 9999))
            .ShouldBeInRange(0.0, 1.0);

        RelevanceScorer.Score(Features(text: 0, distance: 1_000_000, days: 10_000, interest: 0, popularity: 0))
            .ShouldBeInRange(0.0, 1.0);
    }

    [Fact]
    public void A_closer_listing_outranks_a_distant_one_all_else_equal() =>
        RelevanceScorer.Score(Features(distance: 500))
            .ShouldBeGreaterThan(RelevanceScorer.Score(Features(distance: 8000)));

    [Fact]
    public void A_better_text_match_outranks_a_worse_one_all_else_equal() =>
        RelevanceScorer.Score(Features(text: 0.5))
            .ShouldBeGreaterThan(RelevanceScorer.Score(Features(text: 0.05)));

    [Fact]
    public void A_listing_that_starts_soon_outranks_one_that_starts_much_later() =>
        RelevanceScorer.Score(Features(days: 2))
            .ShouldBeGreaterThan(RelevanceScorer.Score(Features(days: 200)));

    [Fact]
    public void Each_declared_interest_adds_to_the_score_up_to_a_cap()
    {
        double none = RelevanceScorer.Score(Features(interest: 0));
        double one = RelevanceScorer.Score(Features(interest: 1));
        double three = RelevanceScorer.Score(Features(interest: 3));
        double ten = RelevanceScorer.Score(Features(interest: 10));

        one.ShouldBeGreaterThan(none);
        three.ShouldBeGreaterThan(one);
        ten.ShouldBe(three, "overlap saturates at three interests, so it cannot dominate the ranking");
    }

    [Fact]
    public void A_missing_distance_scores_neutrally_rather_than_as_zero_or_as_close()
    {
        // No anchor chosen means no distance signal. A listing must not be
        // penalised for the user not having picked a place.
        double unknown = RelevanceScorer.Score(Features(distance: null));
        double veryFar = RelevanceScorer.Score(Features(distance: 1_000_000));
        double veryNear = RelevanceScorer.Score(Features(distance: 0));

        unknown.ShouldBeGreaterThan(veryFar);
        unknown.ShouldBeLessThan(veryNear);
    }

    [Fact]
    public void The_weights_sum_to_one()
    {
        // If they drift, a perfect listing scores above one and the bounds test
        // above would catch it only by luck.
        (RelevanceScorer.TextWeight + RelevanceScorer.DistanceWeight + RelevanceScorer.StartSoonWeight
         + RelevanceScorer.InterestWeight + RelevanceScorer.PopularityWeight)
            .ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void The_explanation_names_only_the_factors_that_applied()
    {
        IReadOnlyList<string> reasons = RelevanceScorer.Explain(
            Features(text: 0.4, distance: 1200, days: 9, interest: 2));

        reasons.ShouldContain("matches your search");
        reasons.ShouldContain("1.2 km away");
        reasons.ShouldContain("starts in 9 days");
        reasons.ShouldContain("matches 2 of your interests");
    }

    [Fact]
    public void The_explanation_says_nothing_about_factors_that_did_not_apply()
    {
        IReadOnlyList<string> reasons = RelevanceScorer.Explain(Features());

        reasons.ShouldBeEmpty();
    }

    [Fact]
    public void The_ranking_inputs_contain_no_sensitive_attribute()
    {
        // The guard docs/plan/03 promises. The feature record is the entire ranking
        // input, so if one of these names ever appears, a family's circumstances
        // have started to influence what they are shown. Fail the build, not the audit.
        string[] forbidden =
        [
            "race", "ethnic", "income", "poverty", "freelunch", "disab", "accommodation",
            "immigra", "gender", "sex", "religio", "school", "grade", "performance", "district",
            "neighbourhood", "household", "zip", "address", "age",
        ];

        string[] names = typeof(RankingFeatures)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant())
            .ToArray();

        foreach (string name in names)
        {
            foreach (string term in forbidden)
            {
                name.ShouldNotContain(term,
                    customMessage: $"RankingFeatures.{name} matches the forbidden term '{term}'.");
            }
        }
    }
}
