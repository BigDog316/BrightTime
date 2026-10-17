namespace BrightTime.Seed.Kettle;

/// <summary>
/// Categories, interests and accommodations, named in all six locales.
/// </summary>
/// <remarks>
/// The non-English names were written for the seed, not by a translator. They are
/// plausible and short, and they need review by a native speaker before any real tenant
/// relies on them. That is also why <c>listing_translation</c> rows from the seed are
/// marked <c>machine</c>.
/// </remarks>
internal static class KettleTaxonomy
{
    private static readonly string[] LocaleOrder = ["en", "es", "zh-Hans", "bn", "ht", "ar"];

    /// <summary>code, then names in <see cref="LocaleOrder"/>.</summary>
    private static readonly (string Code, string[] Names)[] Categories =
    [
        ("academic", ["Academic support", "Apoyo académico", "学业辅导", "পড়াশোনায় সহায়তা", "Sipò lekòl", "الدعم الأكاديمي"]),
        ("arts", ["Arts", "Artes", "艺术", "শিল্পকলা", "Atizay", "الفنون"]),
        ("stem", ["STEM", "Ciencia y tecnología (STEM)", "科学技术（STEM）", "বিজ্ঞান ও প্রযুক্তি (STEM)", "Syans ak teknoloji (STEM)", "العلوم والتكنولوجيا (STEM)"]),
        ("sports", ["Sports and fitness", "Deportes y actividad física", "体育与健身", "খেলাধুলা ও ফিটনেস", "Espò ak egzèsis", "الرياضة واللياقة"]),
        ("outdoors", ["Outdoors and nature", "Aire libre y naturaleza", "户外与自然", "প্রকৃতি ও বাইরের কার্যকলাপ", "Deyò ak lanati", "الطبيعة والأنشطة الخارجية"]),
        ("health", ["Health and wellbeing", "Salud y bienestar", "健康与身心", "স্বাস্থ্য ও সুস্থতা", "Sante ak byennèt", "الصحة والعافية"]),
        ("civic", ["Leadership and community", "Liderazgo y comunidad", "领导力与社区", "নেতৃত্ব ও সমাজসেবা", "Lidèchip ak kominote", "القيادة وخدمة المجتمع"]),
        ("careers", ["Jobs and careers", "Empleo y carreras", "工作与职业", "চাকরি ও ক্যারিয়ার", "Travay ak karyè", "الوظائف والمهن"]),
        ("life-skills", ["Life skills", "Habilidades para la vida", "生活技能", "জীবন দক্ষতা", "Konpetans lavi", "المهارات الحياتية"]),
        ("college-prep", ["College prep", "Preparación universitaria", "升学准备", "কলেজ প্রস্তুতি", "Preparasyon pou inivèsite", "الاستعداد للجامعة"]),
    ];

    /// <summary>code, parent category, then names.</summary>
    private static readonly (string Code, string Parent, string[] Names)[] Interests =
    [
        ("coding", "stem", ["Coding", "Programación", "编程", "কোডিং", "Kodaj", "البرمجة"]),
        ("robotics", "stem", ["Robotics", "Robótica", "机器人", "রোবোটিক্স", "Robotik", "الروبوتات"]),
        ("engineering", "stem", ["Engineering", "Ingeniería", "工程", "প্রকৌশল", "Jeni", "الهندسة"]),
        ("science", "stem", ["Science", "Ciencias", "科学", "বিজ্ঞান", "Syans", "العلوم"]),
        ("math", "academic", ["Math", "Matemáticas", "数学", "গণিত", "Matematik", "الرياضيات"]),
        ("reading", "academic", ["Reading", "Lectura", "阅读", "পড়া", "Lekti", "القراءة"]),
        ("writing", "academic", ["Writing", "Escritura", "写作", "লেখা", "Ekriti", "الكتابة"]),
        ("music", "arts", ["Music", "Música", "音乐", "সংগীত", "Mizik", "الموسيقى"]),
        ("dance", "arts", ["Dance", "Danza", "舞蹈", "নৃত্য", "Dans", "الرقص"]),
        ("theater", "arts", ["Theater", "Teatro", "戏剧", "নাটক", "Teyat", "المسرح"]),
        ("visual-art", "arts", ["Visual art", "Artes visuales", "视觉艺术", "চিত্রকলা", "Atizay vizyèl", "الفنون البصرية"]),
        ("photography", "arts", ["Photography", "Fotografía", "摄影", "ফটোগ্রাফি", "Fotografi", "التصوير الفوتوغرافي"]),
        ("film", "arts", ["Film and video", "Cine y video", "电影与视频", "চলচ্চিত্র ও ভিডিও", "Fim ak videyo", "الأفلام والفيديو"]),
        ("basketball", "sports", ["Basketball", "Baloncesto", "篮球", "বাস্কেটবল", "Baskètbòl", "كرة السلة"]),
        ("soccer", "sports", ["Soccer", "Fútbol", "足球", "ফুটবল", "Foutbòl", "كرة القدم"]),
        ("swimming", "sports", ["Swimming", "Natación", "游泳", "সাঁতার", "Naje", "السباحة"]),
        ("track", "sports", ["Track and field", "Atletismo", "田径", "অ্যাথলেটিক্স", "Atletis", "ألعاب القوى"]),
        ("martial-arts", "sports", ["Martial arts", "Artes marciales", "武术", "মার্শাল আর্ট", "Ar masyal", "الفنون القتالية"]),
        ("hiking", "outdoors", ["Hiking", "Senderismo", "徒步", "হাইকিং", "Randone", "المشي لمسافات طويلة"]),
        ("gardening", "outdoors", ["Gardening", "Jardinería", "园艺", "বাগান করা", "Jadinaj", "البستنة"]),
        ("environment", "outdoors", ["Environment", "Medio ambiente", "环境保护", "পরিবেশ", "Anviwònman", "البيئة"]),
        ("cooking", "life-skills", ["Cooking", "Cocina", "烹饪", "রান্না", "Kwizin", "الطبخ"]),
        ("financial-literacy", "life-skills", ["Money skills", "Educación financiera", "理财知识", "আর্থিক সাক্ষরতা", "Konesans lajan", "الثقافة المالية"]),
        ("mental-health", "health", ["Mental health", "Salud mental", "心理健康", "মানসিক স্বাস্থ্য", "Sante mantal", "الصحة النفسية"]),
        ("leadership", "civic", ["Leadership", "Liderazgo", "领导力", "নেতৃত্ব", "Lidèchip", "القيادة"]),
        ("debate", "civic", ["Debate", "Debate", "辩论", "বিতর্ক", "Deba", "المناظرة"]),
        ("volunteering", "civic", ["Volunteering", "Voluntariado", "志愿服务", "স্বেচ্ছাসেবা", "Volontarya", "التطوع"]),
        ("entrepreneurship", "careers", ["Entrepreneurship", "Emprendimiento", "创业", "উদ্যোক্তা", "Antreprenarya", "ريادة الأعمال"]),
        ("trades", "careers", ["Skilled trades", "Oficios", "技术行业", "কারিগরি পেশা", "Metye", "الحرف المهنية"]),
        ("college-applications", "college-prep", ["College applications", "Solicitudes universitarias", "大学申请", "কলেজে আবেদন", "Aplikasyon inivèsite", "طلبات الالتحاق بالجامعة"]),
    ];

    private static readonly (string Code, string[] Names)[] Accommodations =
    [
        ("wheelchair-accessible", ["Wheelchair accessible", "Accesible en silla de ruedas", "轮椅无障碍", "হুইলচেয়ার প্রবেশযোগ্য", "Aksesib pou chèz woulant", "مناسب للكراسي المتحركة"]),
        ("sensory-friendly", ["Sensory-friendly", "Adaptado a necesidades sensoriales", "感官友好", "সংবেদন-বান্ধব", "Adapte pou bezwen sansoryèl", "مناسب لذوي الحساسية الحسية"]),
        ("asl-interpretation", ["ASL interpretation", "Interpretación en lengua de señas (ASL)", "美国手语翻译", "ASL দোভাষী", "Entèpretasyon ASL", "ترجمة بلغة الإشارة الأمريكية"]),
        ("quiet-space", ["Quiet space available", "Espacio tranquilo disponible", "提供安静空间", "শান্ত জায়গা আছে", "Gen yon espas trankil", "تتوفر مساحة هادئة"]),
        ("all-gender-restroom", ["All-gender restroom", "Baño para todos los géneros", "全性别洗手间", "সব লিঙ্গের জন্য শৌচাগার", "Twalèt pou tout moun", "دورة مياه لجميع الأجناس"]),
        ("adaptive-equipment", ["Adaptive equipment", "Equipo adaptado", "适应性设备", "অভিযোজিত সরঞ্জাম", "Ekipman adapte", "معدات مُكيّفة"]),
        ("dietary-options", ["Dietary options", "Opciones de alimentación", "饮食选择", "খাদ্যের বিকল্প", "Opsyon manje", "خيارات غذائية"]),
        ("ell-support", ["English learner support", "Apoyo para estudiantes de inglés", "英语学习者支持", "ইংরেজি শিক্ষার্থীদের সহায়তা", "Sipò pou moun k ap aprann angle", "دعم متعلمي اللغة الإنجليزية"]),
    ];

    public static IReadOnlyList<string> CategoryCodes { get; } = [.. Categories.Select(c => c.Code)];

    public static IReadOnlyList<string> AccommodationCodes { get; } = [.. Accommodations.Select(a => a.Code)];

    public static IReadOnlyList<string> InterestCodesIn(string category) =>
        [.. Interests.Where(i => i.Parent == category).Select(i => i.Code)];

    public static Guid TermId(string vocabulary, string code) => SeedIds.For($"term/{vocabulary}/{code}");

    public static IReadOnlyList<SeedTerm> Terms()
    {
        List<SeedTerm> terms = [];

        short order = 0;
        foreach ((string code, string[] names) in Categories)
        {
            terms.Add(new SeedTerm(TermId("category", code), "category", code, null, false, order++, Named(names)));
        }

        order = 0;
        foreach ((string code, string parent, string[] names) in Interests)
        {
            terms.Add(new SeedTerm(
                TermId("interest", code), "interest", code, TermId("category", parent), true, order++, Named(names)));
        }

        order = 0;
        foreach ((string code, string[] names) in Accommodations)
        {
            terms.Add(new SeedTerm(TermId("accommodation", code), "accommodation", code, null, false, order++, Named(names)));
        }

        return terms;
    }

    private static Dictionary<string, string> Named(string[] names)
    {
        if (names.Length != LocaleOrder.Length)
        {
            throw new InvalidOperationException($"Expected {LocaleOrder.Length} names, got {names.Length}.");
        }

        return LocaleOrder.Zip(names).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
    }
}
