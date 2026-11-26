using System.Globalization;
using BrightTime.Localisation;
using BrightTime.Modules.Learning.Portfolio;

namespace BrightTime.Reporting;

/// <summary>
/// A young person's portfolio as a printable document (D68): their own words at the top, what
/// they took part in, their skills, the badges they hold with the link where each can be checked,
/// and their approved projects, each with its pictures (D107). Worded in their language; laid out
/// by <see cref="ReportRenderer"/> like any report.
/// </summary>
public static class PortfolioDocuments
{
    public static ReportDocument Word(PortfolioFacts facts, Text text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(text);

        List<string> lines = [];
        if (facts.Headline is { } headline)
        {
            lines.Add(headline);
        }

        if (facts.Bio is { } bio)
        {
            lines.Add(bio);
        }

        lines.Add(text.Format("portfolio.export.hours", ("hours", text.Number(facts.TotalHours))));
        lines.Add(text.Format("portfolio.export.made", ("date", text.DateShort(now))));

        ReportTable activities = new(
            text["portfolio.export.participation"],
            [
                new(text["portfolio.export.col.activity"]), new(text["portfolio.export.col.organisation"]),
                new(text["portfolio.export.col.from"]), new(text["portfolio.export.col.to"]),
                new(text["portfolio.export.col.sessions"], Numeric: true), new(text["portfolio.export.col.hours"], Numeric: true),
            ],
            [.. facts.Activities.Select(a => (IReadOnlyList<string>)
            [
                a.Title, a.Organisation ?? "", text.DateShort(a.StartedOn), a.EndedOn is { } end ? text.DateShort(end) : "",
                text.Number(a.Sessions), text.Number(a.Hours),
            ])]);

        ReportTable badges = new(
            text["portfolio.export.badges"],
            [
                new(text["portfolio.export.col.badge"]), new(text["portfolio.export.col.issuer"]),
                new(text["portfolio.export.col.awarded"]), new(text["portfolio.export.col.check"]),
            ],
            [.. facts.Badges.Select(b => (IReadOnlyList<string>)[b.Name, b.Issuer, text.DateShort(b.IssuedOn), b.Verify.ToString()])]);

        ReportTable skills = new(
            text["portfolio.export.skills"],
            [new(text["portfolio.export.col.skill"]), new(text["portfolio.export.col.level"]), new(text["portfolio.export.col.endorsed_by"])],
            [.. facts.Skills.Select(k => (IReadOnlyList<string>)
            [
                k.Name, k.Level is { } level ? text["portfolio.export.level." + level.ToString(CultureInfo.InvariantCulture)] : "",
                k.EndorsedBy ?? text["portfolio.export.self"],
            ])]);

        // A section per project (D107): what it was part of, then its pictures with their captions.
        ReportGroup projects = new(
            text["portfolio.export.projects"],
            [.. facts.Projects.Select(p => new ReportEntry(
                p.Title,
                string.Join(" · ", new[] { p.Activity, p.Organisation }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } of ? [of] : [],
                [.. p.Pictures.Select(i => new ReportPicture(i.Jpeg, i.Alt, i.Caption))]))]);

        List<string> notes = [];
        if (facts.HasHidden)
        {
            notes.Add(text["portfolio.export.note.hidden"]);
        }

        if (facts.Badges.Count > 0)
        {
            notes.Add(text["portfolio.export.note.verify"]);
        }

        return new ReportDocument(
            facts.DisplayName is { } name ? text.Format("portfolio.export.title", ("name", name)) : text["portfolio.export.title_plain"],
            lines,
            [activities, skills, badges])
        {
            Groups = [projects],
            Notes = notes,
            RightToLeft = text.Culture.TextInfo.IsRightToLeft,
            Language = text.Culture.Name,
            FileName = $"portfolio-{text.FileDay(now)}",
        };
    }
}
