namespace BrightTime.Seed.Kettle;

/// <summary>
/// What a network organiser would curate: three collections and pages for three neighbourhoods.
/// Picked from the seeded listings by rule, so they stay valid when the listings change.
/// </summary>
internal static class KettleCuration
{
    public const string FreeCollection = "free-and-fun";
    public const string JobsCollection = "first-jobs";
    public const string ArtsCollection = "arts-after-school";

    /// <summary>The neighbourhoods with an organiser's page; the rest show the generic page.</summary>
    public static readonly string[] PagedNeighbourhoods = ["hood-downtown", "hood-westside", "hood-riverside"];

    public static IReadOnlyList<SeedCollection> Collections(IReadOnlyList<SeedListing> listings, IReadOnlyList<SeedTerm> terms)
    {
        SeedListing[] published = [.. listings.Where(l => l.Status == "published").OrderBy(l => l.Slug, StringComparer.Ordinal)];
        Guid? arts = terms.FirstOrDefault(t => t.Vocabulary == "category" && t.Code == "arts")?.Id;

        return
        [
            new(SeedIds.For("collection/" + FreeCollection), FreeCollection,
                "Free and fun",
                "No cost, no catch. Good places to start.",
                IsFeatured: true, SortOrder: 0,
                [.. published.Where(l => l.CostModel == "free").Take(8).Select((l, i) => (l.Id, i == 0 ? "A favourite with first-timers." : (string?)null))],
                [("es", "Gratis y divertido", "Sin costo y sin trampas. Buenos lugares para empezar."), ("zh-Hans", "免费又好玩", "不花钱，没有附加条件。适合第一次参加。")]),
            new(SeedIds.For("collection/" + JobsCollection), JobsCollection,
                "First jobs and internships",
                "Paid work and real experience for teens.",
                IsFeatured: true, SortOrder: 1,
                [.. published.Where(l => l.Kind == "job").Take(8).Select(l => (l.Id, (string?)"Look at the deadline before you apply."))],
                [("es", "Primeros empleos y pasantías", "Trabajo pagado y experiencia real para adolescentes.")]),
            new(SeedIds.For("collection/" + ArtsCollection), ArtsCollection,
                "Arts after school",
                "Music, dance, drawing and theater, most days of the week.",
                IsFeatured: false, SortOrder: 2,
                [.. published.Where(l => arts is { } a && l.TermIds.Contains(a)).Take(10).Select(l => (l.Id, (string?)null))],
                []),
        ];
    }

    public static IReadOnlyList<SeedNeighbourhoodPage> NeighbourhoodPages(IReadOnlyList<SeedArea> areas)
    {
        List<SeedNeighbourhoodPage> pages = [];
        foreach (string code in PagedNeighbourhoods)
        {
            SeedArea? area = areas.FirstOrDefault(a => a.Kind == "neighbourhood" && a.Code == code);
            if (area is null)
            {
                continue;
            }

            Guid page = SeedIds.For("nhpage/" + code);
            Guid Block(string locale, int n) => SeedIds.For($"nhblock/{code}/{locale}/{n}");
            string library = code == "hood-downtown" ? "the Main Branch library on Market Square" : $"the {area.Name} community room";

            pages.Add(new SeedNeighbourhoodPage(page, area.Id,
            [
                new(Block("en", 0), "en", "rich_text", 0, $"About {area.Name}",
                    $"{area.Name} has after-school programs most weekdays and something on most weekends.\n\n" +
                    $"**New here?** Start at {library}: staff can point you to anything on this page.\n\n" +
                    "- Most programs are free or cheap.\n- Many have staff who speak Spanish.\n- See [all collections](/collections).",
                    "{}"),
                new(Block("en", 1), "en", "listing_list", 1, "Programs starting soon", null, """{"kind":"program","limit":4}"""),
                new(Block("en", 2), "en", "collection", 2, null, null, $$"""{"collection":"{{FreeCollection}}"}"""),
                new(Block("en", 3), "en", "contact_list", 3, "People to ask", null,
                    $$"""{"contacts":[{"name":"{{area.Name}} youth outreach","role":"Kettle County Youth Bureau","phone":"(914) 555-01{{50 + pages.Count}}","email":"outreach@kettle.example.org"}]}"""),
                new(Block("en", 4), "en", "faq", 4, null, null,
                    """{"items":[{"q":"Do I need to sign up?","a":"Some programs need you to sign up. Each listing says how."},{"q":"Is it safe to walk there?","a":"Most sites are near a bus stop. Each listing has a note on getting there."}]}"""),
                new(Block("en", 5), "en", "transit", 5, "Getting around", "Buses 7 and 12 run every 20 minutes on weekdays. Youth ride for half price with a school ID.", "{}"),

                new(Block("es", 0), "es", "rich_text", 0, $"Sobre {area.Name}",
                    $"En {area.Name} hay programas después de clases casi todos los días y algo casi todos los fines de semana.\n\n" +
                    "**¿Eres nuevo?** Pregunta en la biblioteca: el personal te puede orientar.\n\n" +
                    "- Casi todos los programas son gratis o baratos.\n- Muchos tienen personal que habla español.\n- Mira [todas las colecciones](/collections).",
                    "{}"),
                new(Block("es", 1), "es", "listing_list", 1, "Programas que empiezan pronto", null, """{"kind":"program","limit":4}"""),
                new(Block("es", 2), "es", "faq", 2, "Preguntas", null,
                    """{"items":[{"q":"¿Tengo que inscribirme?","a":"Algunos programas piden inscripción. Cada actividad dice cómo."}]}"""),
            ]));
        }

        return pages;
    }
}
