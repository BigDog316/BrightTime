namespace BrightTime.Seed.Kettle;

/// <summary>How a template's sessions recur.</summary>
internal enum SchedulePattern
{
    /// <summary>One or two weekday afternoons a week for a season.</summary>
    AfterSchool,

    /// <summary>Saturday mornings for a season.</summary>
    Saturday,

    /// <summary>Weekdays, all day, for one or two weeks over a school break.</summary>
    BreakCamp,

    /// <summary>A single evening or weekend afternoon.</summary>
    OneOff,

    /// <summary>Three days in a row.</summary>
    MultiDay,

    /// <summary>Open on weekday afternoons with no end date.</summary>
    OpenHours,

    /// <summary>Two shifts a week for a season.</summary>
    Shifts,
}

internal enum CostPattern
{
    Free,
    Fixed,
    SlidingScale,
    Varies,
    Stipend,
}

internal enum RegistrationPattern
{
    None,
    External,
    Builtin,
    WaitlistOnly,
}

/// <summary>Title and summary in one locale; a description where one was written.</summary>
internal sealed record TemplateText(string Title, string Summary, string? Description = null);

/// <summary>
/// One kind of activity. A listing is a template placed at an organisation and site; the
/// generator varies schedule, price and audience within what the template allows.
/// </summary>
internal sealed record ListingTemplate(
    string Key,
    string Category,
    string Kind,
    string[] Interests,
    short? AgeMin,
    short? AgeMax,
    SchedulePattern Schedule,
    CostPattern Cost,
    RegistrationPattern Registration,
    IReadOnlyDictionary<string, TemplateText> Text,
    string Modality = "in_person",
    string? DeadlineKind = null)
{
    public TemplateText En => Text["en"];

    /// <summary>True when every one of the six locales has text.</summary>
    public bool IsSixLocale => Text.Count == 6;
}

/// <summary>
/// The activity templates. English throughout, Spanish for every template, and all six
/// locales for twelve of them: the "representative subset" M1 asks for. Non-English text was
/// written for the seed and is stored as machine translation, pending review.
/// </summary>
internal static class KettleTemplates
{
    private static Dictionary<string, TemplateText> T(
        TemplateText en,
        TemplateText es,
        TemplateText? zh = null,
        TemplateText? bn = null,
        TemplateText? ht = null,
        TemplateText? ar = null)
    {
        Dictionary<string, TemplateText> text = new(StringComparer.Ordinal) { ["en"] = en, ["es"] = es };
        if (zh is not null)
        {
            text["zh-Hans"] = zh;
            text["bn"] = bn ?? throw new ArgumentNullException(nameof(bn));
            text["ht"] = ht ?? throw new ArgumentNullException(nameof(ht));
            text["ar"] = ar ?? throw new ArgumentNullException(nameof(ar));
        }

        return text;
    }

    public static IReadOnlyList<ListingTemplate> All { get; } =
    [
        // ---- STEM ------------------------------------------------------------------------
        new("robotics-club", "stem", "program", ["robotics", "engineering"], 10, 14,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Robotics club", "Design, build and program robots with a team.",
                    "Teams of four build and program a robot over the season and show it at the county showcase. No experience needed; kits and laptops are provided."),
              new("Club de robótica", "Diseña, construye y programa robots en equipo.",
                    "Equipos de cuatro construyen y programan un robot durante la temporada y lo presentan en la muestra del condado. No se necesita experiencia; se prestan kits y computadoras."),
              new("机器人俱乐部", "和队友一起设计、制作和编程机器人。"),
              new("রোবোটিক্স ক্লাব", "দলের সাথে রোবট ডিজাইন, তৈরি ও প্রোগ্রাম করো।"),
              new("Klib robotik", "Konsevwa, bati epi pwograme robo ak yon ekip."),
              new("نادي الروبوتات", "صمّم الروبوتات وابنِها وبرمجها مع فريق."))),

        new("coding-lab", "stem", "program", ["coding"], 13, 18,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.None,
            T(new("Teen coding lab", "Write your first app, website or game with friendly mentors.",
                    "Bring a project or start one. Mentors help with Python, web pages and game making. Laptops are available to borrow during the lab."),
              new("Laboratorio de programación para adolescentes", "Crea tu primera aplicación, página web o videojuego con mentores amables.",
                    "Trae un proyecto o empieza uno nuevo. Los mentores ayudan con Python, páginas web y videojuegos. Puedes usar una computadora prestada durante el laboratorio."),
              new("青少年编程实验室", "在友善导师的帮助下，写出你的第一个应用、网站或游戏。"),
              new("কিশোরদের কোডিং ল্যাব", "বন্ধুসুলভ মেন্টরদের সাথে তোমার প্রথম অ্যাপ, ওয়েবসাইট বা গেম তৈরি করো।"),
              new("Laboratwa kodaj pou adolesan", "Ekri premye aplikasyon, sit entènèt oswa jwèt ou ak mantò ki janti."),
              new("مختبر البرمجة للمراهقين", "اكتب أول تطبيق أو موقع أو لعبة لك بمساعدة مرشدين ودودين."))),

        new("science-explorers", "stem", "program", ["science"], 6, 10,
            SchedulePattern.AfterSchool, CostPattern.SlidingScale, RegistrationPattern.Builtin,
            T(new("Science explorers", "Hands-on experiments for curious kids.",
                    "Every week is a new experiment: circuits, rockets, slime and microscopes. Kids take home what they make."),
              new("Exploradores de la ciencia", "Experimentos prácticos para niños curiosos."))),

        new("maker-hours", "stem", "resource", ["engineering", "coding"], 11, 19,
            SchedulePattern.OpenHours, CostPattern.Free, RegistrationPattern.None,
            T(new("Open maker hours", "Drop in to use 3D printers, a laser cutter and craft tools.",
                    "Staff are on hand to help with your first print. Under-13s need an adult with them to use the laser cutter."),
              new("Horario abierto del taller", "Ven a usar impresoras 3D, una cortadora láser y herramientas de manualidades."))),

        new("young-engineers", "stem", "program", ["engineering", "science"], 11, 15,
            SchedulePattern.Saturday, CostPattern.Free, RegistrationPattern.External,
            T(new("Young engineers circle", "A small-group engineering program for girls and nonbinary youth.",
                    "Design bridges, wind turbines and water filters, and meet engineers who work in the county."),
              new("Círculo de jóvenes ingenieras", "Un programa de ingeniería en grupos pequeños para chicas y jóvenes no binarios."))),

        new("astronomy-night", "stem", "event", ["science"], null, null,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("Family astronomy night", "Look through telescopes and learn the night sky.",
                    "Telescopes are provided. If it is cloudy, the evening moves indoors for a planetarium show."),
              new("Noche de astronomía en familia", "Mira por telescopios y aprende sobre el cielo nocturno."))),

        new("youth-hackathon", "stem", "event", ["coding"], 14, 19,
            SchedulePattern.MultiDay, CostPattern.Free, RegistrationPattern.Builtin,
            T(new("Youth hackathon", "Build an app in a weekend with a team.",
                    "Form a team, pick a problem in your community and build something that helps. Meals are provided all weekend."),
              new("Hackatón juvenil", "Crea una aplicación en un fin de semana con tu equipo.")),
            DeadlineKind: "registration"),

        // ---- Arts ------------------------------------------------------------------------
        new("youth-orchestra", "arts", "program", ["music"], 9, 18,
            SchedulePattern.AfterSchool, CostPattern.SlidingScale, RegistrationPattern.Builtin,
            T(new("Youth orchestra", "Rehearse and perform with other young musicians.",
                    "Weekly rehearsals and two concerts a year. Instruments can be borrowed. Placement auditions are short and friendly."),
              new("Orquesta juvenil", "Ensaya y toca con otros jóvenes músicos.",
                    "Ensayos semanales y dos conciertos al año. Se prestan instrumentos. Las audiciones de ubicación son breves y amables."),
              new("青少年管弦乐团", "与其他年轻音乐家一起排练和演出。"),
              new("যুব অর্কেস্ট্রা", "অন্য তরুণ সংগীতশিল্পীদের সাথে মহড়া ও পরিবেশনা করো।"),
              new("Òkès jèn yo", "Repete epi jwe ak lòt jèn mizisyen."),
              new("أوركسترا الشباب", "تدرّب واعزف مع موسيقيين شباب آخرين.")),
            DeadlineKind: "audition"),

        new("hip-hop-dance", "arts", "program", ["dance"], 8, 16,
            SchedulePattern.AfterSchool, CostPattern.Fixed, RegistrationPattern.External,
            T(new("Hip-hop dance", "Learn choreography and perform at the spring showcase.",
                    "Beginners welcome. Wear comfortable shoes and bring water."),
              new("Baile hip-hop", "Aprende coreografías y presenta en la muestra de primavera."))),

        new("drama-workshop", "arts", "program", ["theater"], 12, 18,
            SchedulePattern.AfterSchool, CostPattern.SlidingScale, RegistrationPattern.Builtin,
            T(new("Teen drama workshop", "Improvisation, scene work and a play at the end of the season.",
                    "Everyone who wants a part gets one. Backstage and lighting roles are open too."),
              new("Taller de teatro para adolescentes", "Improvisación, escenas y una obra al final de la temporada."))),

        new("art-studio", "arts", "program", ["visual-art"], 7, 13,
            SchedulePattern.AfterSchool, CostPattern.Fixed, RegistrationPattern.External,
            T(new("After-school art studio", "Painting, drawing and printmaking in a real studio.",
                    "Small groups work with a teaching artist. All materials are included, and smocks are provided."),
              new("Taller de arte después de clases", "Pintura, dibujo y grabado en un verdadero estudio.",
                    "Grupos pequeños trabajan con un artista docente. Todos los materiales están incluidos y se prestan batas."),
              new("课后艺术工作室", "在真正的工作室里学习绘画、素描和版画。"),
              new("স্কুল-পরবর্তী আর্ট স্টুডিও", "আসল স্টুডিওতে ছবি আঁকা, স্কেচ ও প্রিন্টমেকিং।"),
              new("Estidyo atizay apre lekòl", "Penti, desen ak enpresyon nan yon vrè estidyo."),
              new("مرسم الفنون بعد المدرسة", "الرسم والتلوين والطباعة الفنية في مرسم حقيقي."))),

        new("photo-walks", "arts", "program", ["photography"], 12, 18,
            SchedulePattern.Saturday, CostPattern.Free, RegistrationPattern.External,
            T(new("Photography walks", "Explore the neighborhood with a camera and learn to frame a shot.",
                    "Use your phone or borrow a camera. The season ends with a small exhibition."),
              new("Caminatas fotográficas", "Explora el vecindario con una cámara y aprende a encuadrar."))),

        new("film-lab", "arts", "program", ["film"], 13, 19,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Youth film lab", "Write, shoot and edit a short film.",
                    "Work in a crew with real cameras and editing software. Finished films screen at the library."),
              new("Laboratorio de cine juvenil", "Escribe, filma y edita un cortometraje."))),

        new("student-concert", "arts", "event", ["music"], null, null,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("Student concert", "An evening of music performed by local young musicians.",
                    "Free and open to everyone. Doors open half an hour before the show."),
              new("Concierto estudiantil", "Una velada de música interpretada por jóvenes músicos locales."))),

        new("teen-art-show", "arts", "event", ["visual-art"], null, null,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("Teen art exhibition opening", "See work by young artists from across the county.",
                    "Meet the artists, enjoy refreshments and vote for the people's choice award."),
              new("Inauguración de exposición de arte juvenil", "Mira obras de jóvenes artistas de todo el condado."))),

        new("open-studio", "arts", "resource", ["visual-art", "music"], 12, 19,
            SchedulePattern.OpenHours, CostPattern.Free, RegistrationPattern.None,
            T(new("Open studio and music room", "Drop in to draw, paint or practice an instrument.",
                    "Supplies and practice rooms are free. Sign in at the front desk."),
              new("Estudio abierto y sala de música", "Ven a dibujar, pintar o practicar un instrumento."))),

        // ---- Sports ----------------------------------------------------------------------
        new("youth-basketball", "sports", "program", ["basketball"], 8, 14,
            SchedulePattern.AfterSchool, CostPattern.Fixed, RegistrationPattern.Builtin,
            T(new("Youth basketball league", "Weekly practices and Saturday games for all skill levels.",
                    "Every player gets court time. Jerseys are included in the fee, and fee waivers are available."),
              new("Liga juvenil de baloncesto", "Prácticas semanales y partidos los sábados para todos los niveles.")),
            DeadlineKind: "registration"),

        new("open-gym", "sports", "resource", ["basketball"], 12, 19,
            SchedulePattern.OpenHours, CostPattern.Free, RegistrationPattern.None,
            T(new("Evening open gym", "Pickup games and free play. Just show up.",
                    "Bring a water bottle. Staff supervise the gym and lend out balls."),
              new("Gimnasio abierto por la tarde", "Partidos informales y juego libre. Solo ven.",
                    "Trae una botella de agua. El personal supervisa el gimnasio y presta balones."),
              new("傍晚开放体育馆", "自由打球，随时来玩。"),
              new("সন্ধ্যার খোলা জিম", "খেলার জন্য চলে এসো, নিবন্ধনের দরকার নেই।"),
              new("Jimnaz louvri aswè", "Jwèt ak jwe lib. Jis vini."),
              new("صالة رياضية مفتوحة مساءً", "مباريات ودية ولعب حر. فقط تعال."))),

        new("soccer-clinic", "sports", "program", ["soccer"], 6, 12,
            SchedulePattern.Saturday, CostPattern.Fixed, RegistrationPattern.External,
            T(new("Soccer skills clinic", "Dribbling, passing and small-sided games.",
                    "Shin guards are required; loaners are available."),
              new("Clínica de fútbol", "Regate, pases y partidos en grupos pequeños."))),

        new("learn-to-swim", "sports", "program", ["swimming"], 5, 12,
            SchedulePattern.Saturday, CostPattern.Fixed, RegistrationPattern.Builtin,
            T(new("Learn to swim", "Small-group swim lessons with certified instructors.",
                    "Groups are placed by skill on the first day. A parent or carer must stay on site for under-8s."),
              new("Aprende a nadar", "Clases de natación en grupos pequeños con instructores certificados.")),
            DeadlineKind: "registration"),

        new("running-club", "sports", "program", ["track"], 13, 18,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.None,
            T(new("Teen running club", "Run with a friendly group and train for a 5K.",
                    "All paces welcome. The season finishes with the county fun run."),
              new("Club de corredores adolescentes", "Corre con un grupo amigable y entrena para una carrera de 5K."))),

        new("martial-arts", "sports", "program", ["martial-arts"], 7, 15,
            SchedulePattern.AfterSchool, CostPattern.Fixed, RegistrationPattern.External,
            T(new("Youth martial arts", "Build focus, strength and confidence.",
                    "Uniforms are provided for the first month. No sparring until students are ready."),
              new("Artes marciales juveniles", "Desarrolla concentración, fuerza y confianza."))),

        new("teen-swim", "sports", "resource", ["swimming"], 12, 18,
            SchedulePattern.OpenHours, CostPattern.Free, RegistrationPattern.None,
            T(new("Teen open swim", "Free swim time with lifeguards on duty.",
                    "Swimmers must pass a short deep-water check on their first visit."),
              new("Natación libre para adolescentes", "Natación libre con salvavidas presentes."))),

        new("fun-run", "sports", "event", ["track"], null, null,
            SchedulePattern.OneOff, CostPattern.Fixed, RegistrationPattern.External,
            T(new("Family fun run", "A 5K and a kids' dash through the park.",
                    "Every finisher gets a medal. Strollers are welcome on the 5K course."),
              new("Carrera familiar", "Una carrera de 5K y una carrera infantil por el parque.")),
            DeadlineKind: "early_bird"),

        // ---- Outdoors --------------------------------------------------------------------
        new("nature-explorers", "outdoors", "program", ["hiking", "environment"], 6, 11,
            SchedulePattern.Saturday, CostPattern.SlidingScale, RegistrationPattern.External,
            T(new("Nature explorers", "Trail walks, bug hunts and stream studies.",
                    "Dress for the weather. Waterproof boots are available to borrow."),
              new("Exploradores de la naturaleza", "Caminatas, búsqueda de insectos y estudio de arroyos."))),

        new("garden-club", "outdoors", "program", ["gardening"], 8, 15,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Youth garden club", "Grow, harvest and cook vegetables from the garden.",
                    "Work in the greenhouse through the winter and plant the outdoor beds in spring."),
              new("Club de jardinería juvenil", "Cultiva, cosecha y cocina verduras del huerto.",
                    "Trabaja en el invernadero durante el invierno y siembra los canteros en primavera."),
              new("青少年园艺俱乐部", "在菜园里种植、收获并烹饪蔬菜。"),
              new("যুব বাগান ক্লাব", "বাগানের সবজি চাষ, সংগ্রহ ও রান্না করো।"),
              new("Klib jaden pou jèn", "Plante, rekòlte epi kwit legim nan jaden an."),
              new("نادي البستنة للشباب", "ازرع خضروات الحديقة واحصدها واطبخها."))),

        new("park-cleanup", "outdoors", "event", ["environment", "volunteering"], null, null,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("River trail clean-up day", "Help clean up the river trail. Gloves and snacks provided.",
                    "Volunteers under 16 need a signed permission form, available at check-in."),
              new("Día de limpieza del sendero del río", "Ayuda a limpiar el sendero del río. Se dan guantes y meriendas."))),

        new("intro-paddling", "outdoors", "program", ["hiking"], 12, 17,
            SchedulePattern.Saturday, CostPattern.Fixed, RegistrationPattern.Builtin,
            T(new("Intro to paddling", "Learn to kayak and canoe on calm water.",
                    "Swimmers only. Life jackets and boats are provided; sessions move indoors to the pool in cold weather."),
              new("Introducción al remo", "Aprende kayak y canoa en aguas tranquilas."))),

        // ---- Academic --------------------------------------------------------------------
        new("homework-help", "academic", "resource", ["math", "reading"], 8, 18,
            SchedulePattern.OpenHours, CostPattern.Free, RegistrationPattern.None,
            T(new("Homework help center", "Free drop-in tutoring for every subject.",
                    "Trained volunteers help with homework in every subject. Bring your assignments; snacks are provided."),
              new("Centro de ayuda con las tareas", "Tutoría gratuita sin cita para todas las materias.",
                    "Voluntarios capacitados ayudan con las tareas de todas las materias. Trae tus tareas; hay meriendas."),
              new("作业辅导中心", "各科免费辅导，无需预约。"),
              new("হোমওয়ার্ক সহায়তা কেন্দ্র", "সব বিষয়ে বিনামূল্যে টিউটরিং, আগে থেকে নিবন্ধন লাগবে না।"),
              new("Sant èd pou devwa", "Sipò gratis pou tout matyè, san randevou."),
              new("مركز المساعدة في الواجبات", "دروس مجانية في جميع المواد دون موعد مسبق."))),

        new("reading-buddies", "academic", "program", ["reading"], 6, 9,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.Builtin,
            T(new("Reading buddies", "Younger readers pair up with teen mentors.",
                    "Each child reads with the same mentor every week and picks a free book to keep each month."),
              new("Compañeros de lectura", "Lectores pequeños leen con mentores adolescentes."))),

        new("math-circle", "academic", "program", ["math"], 10, 15,
            SchedulePattern.Saturday, CostPattern.Free, RegistrationPattern.External,
            T(new("Math circle", "Puzzles and problem-solving for kids who love math.",
                    "No tests and no grades: just interesting problems and people to solve them with."),
              new("Círculo de matemáticas", "Acertijos y resolución de problemas para quienes aman las matemáticas."))),

        new("young-writers", "academic", "program", ["writing"], 11, 17,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Young writers workshop", "Write stories and poems and publish a class anthology.",
                    "Workshops are led by local authors. Every writer gets a printed copy of the anthology."),
              new("Taller de jóvenes escritores", "Escribe cuentos y poemas y publica una antología del grupo."))),

        new("online-tutoring", "academic", "program", ["math", "reading", "writing"], 8, 17,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Online tutoring", "Live one-on-one tutoring by video.",
                    "Book a session with the same tutor each week. A library card is needed to sign in."),
              new("Tutoría en línea", "Tutoría individual en vivo por video.")),
            Modality: "virtual"),

        new("english-conversation", "academic", "program", ["reading", "writing"], 13, 19,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.None,
            T(new("English conversation for teens", "Practice everyday English in a relaxed group.",
                    "For teens learning English. Conversation topics are chosen by the group."),
              new("Conversación en inglés para adolescentes", "Practica el inglés cotidiano en un grupo relajado.",
                    "Para adolescentes que aprenden inglés. El grupo elige los temas de conversación."),
              new("青少年英语会话", "在轻松的小组中练习日常英语。"),
              new("কিশোরদের জন্য ইংরেজি কথোপকথন", "স্বস্তির পরিবেশে দলের সাথে দৈনন্দিন ইংরেজি অনুশীলন করো।"),
              new("Konvèsasyon an angle pou adolesan", "Pratike angle chak jou nan yon gwoup dekontrakte."),
              new("محادثة باللغة الإنجليزية للمراهقين", "تدرّب على الإنجليزية اليومية في مجموعة مريحة."))),

        // ---- Health ----------------------------------------------------------------------
        new("teen-wellness", "health", "program", ["mental-health"], 13, 18,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Teen wellness group", "Talk, unwind and learn ways to handle stress.",
                    "Led by a licensed counselor. What is said in the group stays in the group."),
              new("Grupo de bienestar para adolescentes", "Habla, relájate y aprende a manejar el estrés."))),

        new("kids-yoga", "health", "program", ["mental-health"], 5, 11,
            SchedulePattern.Saturday, CostPattern.Fixed, RegistrationPattern.External,
            T(new("Kids yoga", "Stretch, breathe and play.",
                    "Mats are provided. Wear clothes that are easy to move in."),
              new("Yoga para niños", "Estira, respira y juega."))),

        new("teen-health-fair", "health", "event", ["mental-health"], 12, 19,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("Teen health fair", "Free sports physicals, check-ups and health information.",
                    "Bring your insurance card if you have one. No one is turned away."),
              new("Feria de salud para adolescentes", "Exámenes deportivos gratuitos, chequeos e información de salud."))),

        // ---- Civic -----------------------------------------------------------------------
        new("youth-council", "civic", "program", ["leadership"], 14, 19,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Youth council", "Advise local government on issues that matter to young people.",
                    "Members serve for a year, meet monthly with officials and run one community project."),
              new("Consejo juvenil", "Asesora al gobierno local sobre temas que importan a los jóvenes.")),
            DeadlineKind: "application"),

        new("debate-team", "civic", "program", ["debate"], 12, 18,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Debate team", "Learn to argue a case and compete in county tournaments.",
                    "Practice research, public speaking and teamwork. Travel to tournaments is covered."),
              new("Equipo de debate", "Aprende a argumentar y compite en torneos del condado."))),

        new("volunteer-corps", "civic", "program", ["volunteering", "leadership"], 13, 18,
            SchedulePattern.Saturday, CostPattern.Free, RegistrationPattern.External,
            T(new("Teen volunteer corps", "Earn service hours helping at food drives and community events.",
                    "Pick the projects that suit you. Service hours are logged and signed off for school."),
              new("Cuerpo de voluntarios adolescentes", "Gana horas de servicio ayudando en colectas de alimentos y eventos.",
                    "Elige los proyectos que te convengan. Las horas de servicio se registran y se certifican para la escuela."),
              new("青少年志愿团", "在食物募捐和社区活动中帮忙，累积服务时数。"),
              new("কিশোর স্বেচ্ছাসেবক দল", "খাদ্য সংগ্রহ ও কমিউনিটি অনুষ্ঠানে সাহায্য করে সেবার ঘণ্টা অর্জন করো।"),
              new("Kò volontè adolesan", "Fè èdtan sèvis lè w ede nan kolèt manje ak evènman kominote."),
              new("فريق المتطوعين المراهقين", "اكسب ساعات خدمة بالمساعدة في حملات جمع الطعام وفعاليات المجتمع."))),

        new("service-day", "civic", "event", ["volunteering"], 12, 19,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.Builtin,
            T(new("Community service day", "A day of projects across the county, with lunch for every volunteer.",
                    "Choose a project when you sign up: painting, sorting donations or planting trees."),
              new("Día de servicio comunitario", "Un día de proyectos en todo el condado, con almuerzo para cada voluntario."))),

        // ---- Careers ---------------------------------------------------------------------
        new("camp-counselor", "careers", "job", ["leadership"], 16, 21,
            SchedulePattern.BreakCamp, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Paid holiday camp counselor", "Help run a day camp for younger kids over the school break.",
                    "Lead games, crafts and outings for campers aged 6 to 10. Training is paid."),
              new("Consejero de campamento (pagado)", "Ayuda a dirigir un campamento de día para niños durante las vacaciones escolares.",
                    "Dirige juegos, manualidades y salidas para niños de 6 a 10 años. La capacitación es pagada."),
              new("假期营地辅导员（带薪）", "在学校假期帮忙运营儿童日营。"),
              new("ছুটির ক্যাম্প কাউন্সেলর (বেতনসহ)", "স্কুল ছুটিতে ছোটদের ডে ক্যাম্প চালাতে সাহায্য করো।"),
              new("Konseye kan vakans (peye)", "Ede jere yon kan pou timoun pandan vakans lekòl."),
              new("مرشد مخيم العطلة (مدفوع الأجر)", "ساعد في إدارة مخيم نهاري للأطفال خلال العطلة المدرسية.")),
            DeadlineKind: "application"),

        new("library-page", "careers", "job", ["reading"], 15, 18,
            SchedulePattern.Shifts, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Library page (part-time, paid)", "Shelve books and help visitors, eight hours a week.",
                    "A first job with flexible after-school hours. Working papers are required for under-18s."),
              new("Asistente de biblioteca (medio tiempo, pagado)", "Ordena libros y ayuda a los visitantes, ocho horas por semana.")),
            DeadlineKind: "application"),

        new("junior-lifeguard", "careers", "job", ["swimming"], 16, 21,
            SchedulePattern.Shifts, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Junior lifeguard (paid)", "Paid lifeguard training, then shifts at the pool.",
                    "Certification is free for hires. You must pass a swim test at the first session."),
              new("Salvavidas junior (pagado)", "Capacitación pagada como salvavidas y luego turnos en la piscina.")),
            DeadlineKind: "application"),

        new("business-internship", "careers", "job", ["entrepreneurship"], 16, 21,
            SchedulePattern.Shifts, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Paid business internship", "Work alongside staff at a local business and earn a stipend.",
                    "Placements in retail, offices and nonprofits. A mentor checks in every week."),
              new("Pasantía pagada en negocios", "Trabaja junto al personal de un negocio local y recibe un estipendio.")),
            DeadlineKind: "application"),

        new("trades-preapprenticeship", "careers", "job", ["trades"], 17, 21,
            SchedulePattern.Shifts, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Pre-apprenticeship in the trades", "Paid hands-on training in carpentry, electrical and plumbing.",
                    "Graduates get help applying to union apprenticeships. Tools and boots are provided."),
              new("Preaprendizaje en oficios", "Capacitación práctica pagada en carpintería, electricidad y plomería.")),
            DeadlineKind: "application"),

        new("job-readiness", "careers", "program", ["entrepreneurship"], 14, 21,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Job readiness workshop", "Write a résumé, practice interviews and get ready for a first job.",
                    "Leave with a résumé, a mock interview and a list of employers who are hiring teens."),
              new("Taller de preparación laboral", "Escribe tu currículum, practica entrevistas y prepárate para tu primer empleo."))),

        new("conservation-crew", "careers", "job", ["environment"], 15, 19,
            SchedulePattern.Saturday, CostPattern.Stipend, RegistrationPattern.External,
            T(new("Youth conservation crew (paid)", "Restore trails and streams and get paid for it.",
                    "Crews of six work outdoors with a crew leader. Work boots and gloves are provided."),
              new("Brigada juvenil de conservación (pagada)", "Restaura senderos y arroyos y recibe un pago.")),
            DeadlineKind: "application"),

        // ---- Life skills -----------------------------------------------------------------
        new("teen-cooking", "life-skills", "program", ["cooking"], 12, 17,
            SchedulePattern.AfterSchool, CostPattern.SlidingScale, RegistrationPattern.Builtin,
            T(new("Teen cooking class", "Cook a full meal each week and take home the recipes.",
                    "Learn knife skills, kitchen safety and budget cooking. Tell us about allergies when you register."),
              new("Clase de cocina para adolescentes", "Cocina una comida completa cada semana y llévate las recetas.",
                    "Aprende a usar el cuchillo, seguridad en la cocina y a cocinar con poco presupuesto. Avísanos de alergias al inscribirte."),
              new("青少年烹饪课", "每周做一顿完整的饭，并把食谱带回家。"),
              new("কিশোরদের রান্নার ক্লাস", "প্রতি সপ্তাহে পুরো খাবার রান্না করো আর রেসিপি বাড়ি নিয়ে যাও।"),
              new("Klas kwizin pou adolesan", "Kwit yon repa konplè chak semèn epi pote resèt yo lakay."),
              new("دروس الطبخ للمراهقين", "اطبخ وجبة كاملة كل أسبوع وخذ الوصفات معك إلى البيت."))),

        new("money-skills", "life-skills", "program", ["financial-literacy"], 14, 19,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("Money skills for teens", "Budgeting, saving and opening your first bank account.",
                    "Short weekly sessions online. Finish the course and earn a small savings bonus."),
              new("Finanzas personales para adolescentes", "Presupuesto, ahorro y tu primera cuenta bancaria.")),
            Modality: "virtual"),

        // ---- College prep ----------------------------------------------------------------
        new("college-essay", "college-prep", "program", ["college-applications", "writing"], 16, 19,
            SchedulePattern.AfterSchool, CostPattern.Free, RegistrationPattern.External,
            T(new("College essay workshop", "Draft and polish your application essay with an advisor.",
                    "Bring a draft or start from scratch. Advisors read every essay and give written feedback."),
              new("Taller de ensayos universitarios", "Escribe y mejora tu ensayo de solicitud con un asesor.",
                    "Trae un borrador o empieza desde cero. Los asesores leen cada ensayo y dan comentarios por escrito."),
              new("大学申请文书工作坊", "在顾问的帮助下撰写并润色你的申请文书。"),
              new("কলেজ প্রবন্ধ কর্মশালা", "উপদেষ্টার সাথে তোমার আবেদনের প্রবন্ধ লেখো ও ঘষেমেজে নাও।"),
              new("Atelye redaksyon pou inivèsite", "Ekri epi amelyore redaksyon aplikasyon ou ak yon konseye."),
              new("ورشة مقالات القبول الجامعي", "اكتب مقال طلب الالتحاق وحسّنه مع مستشار."))),

        new("college-fair", "college-prep", "event", ["college-applications"], 14, 19,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("County college fair", "Meet admissions staff from more than 40 colleges.",
                    "Families welcome. Interpreters for Spanish, Chinese, Bengali, Haitian Creole and Arabic are on hand."),
              new("Feria universitaria del condado", "Conoce al personal de admisiones de más de 40 universidades."))),

        new("sat-prep", "college-prep", "program", ["math", "reading"], 15, 18,
            SchedulePattern.Saturday, CostPattern.SlidingScale, RegistrationPattern.Builtin,
            T(new("SAT prep", "Practice tests and strategy sessions before test day.",
                    "Four full practice tests with personal score reports. Fee waivers are available."),
              new("Preparación para el SAT", "Exámenes de práctica y estrategias antes del día del examen.")),
            DeadlineKind: "registration"),

        new("financial-aid-night", "college-prep", "event", ["college-applications", "financial-literacy"], 16, 19,
            SchedulePattern.OneOff, CostPattern.Free, RegistrationPattern.None,
            T(new("Financial aid night", "Get help with the FAFSA and scholarship applications.",
                    "Bring a parent or guardian and last year's tax information if you can."),
              new("Noche de ayuda financiera", "Recibe ayuda con la FAFSA y las solicitudes de becas.")),
            DeadlineKind: "scholarship"),
    ];
}
