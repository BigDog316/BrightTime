using System.Reflection;
using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Persistence;

namespace BrightTime.Architecture.Tests;

public sealed partial class PersistenceConventionTests
{
    private static readonly string[] ModuleNames = ["Platform", "Identity", "Directory", "Opportunities"];

    [Fact]
    public void Every_module_context_derives_from_the_base_context()
    {
        foreach (string module in ModuleNames)
        {
            Assembly assembly = Assembly.Load($"BrightTime.Modules.{module}");

            IEnumerable<Type> contexts = assembly.GetTypes()
                .Where(t => t.Name.EndsWith("DbContext", StringComparison.Ordinal) && !t.IsAbstract);

            foreach (Type context in contexts)
            {
                typeof(BrightTimeDbContext)
                    .IsAssignableFrom(context)
                    .ShouldBeTrue(
                        $"{context.FullName} must derive from BrightTimeDbContext, which is what pins the " +
                        "module's schema and brings the tenant session interceptor with it.");
            }
        }
    }

    [Fact]
    public void Encrypted_properties_are_byte_arrays_named_with_the_Enc_suffix()
    {
        // The *_enc contract is load-bearing in three places: the EF value
        // converter, the Serilog destructuring policy that refuses to serialise
        // them, and the reviewer reading a diff. A *string* property named
        // SomethingEnc means plaintext is about to be written into a column the
        // schema promises is encrypted.
        foreach (string module in ModuleNames)
        {
            Assembly assembly = Assembly.Load($"BrightTime.Modules.{module}");

            foreach (Type type in assembly.GetTypes())
            {
                foreach (PropertyInfo property in type.GetProperties())
                {
                    if (!property.Name.EndsWith("Enc", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    property.PropertyType.ShouldBe(
                        typeof(byte[]),
                        $"{type.Name}.{property.Name} ends in 'Enc' but is not a byte[]. " +
                        "Either it is not an encrypted field and should be renamed, or it is about to " +
                        "write plaintext into an encrypted column.");
                }
            }
        }
    }

    [Fact]
    public void Only_the_clock_implementations_read_the_ambient_system_time()
    {
        // Reflection cannot see a static property access, so this is a source
        // scan. It matters: consent expiry, clearance expiry, badge validity,
        // retention windows and session schedules are all time-dependent, and
        // all of them need to be testable at a chosen instant. IClock is the
        // only door.
        string[] permitted =
        [
            Path.Combine("BrightTime.SharedKernel", "Time", "SystemClock.cs"),
            Path.Combine("BrightTime.SharedKernel", "Time", "FixedClock.cs"),
        ];

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("obj", ""), StringComparison.Ordinal) ||
                file.Contains(Path.Combine("bin", ""), StringComparison.Ordinal) ||
                permitted.Any(p => file.EndsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            // Comments explain the rule and name the thing being banned, so
            // match against code only or the rule flags its own documentation.
            string source = StripComments(File.ReadAllText(file));
            if (AmbientNowPattern().IsMatch(source))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        offenders.ShouldBeEmpty(
            "These files read the ambient system clock directly. Inject IClock instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_session_variable_names_match_the_schema()
    {
        // A typo here does not throw. It makes platform.current_tenant_id()
        // return NULL, every tenant-scoped policy compares against NULL, and
        // the application returns no rows instead of the wrong rows: quiet,
        // and still broken. Keep the two definitions welded together.
        string rolesSql = File.ReadAllText(
            Path.Combine(RepositoryRoot, "db", "schema", "00-extensions-roles.sql"));

        rolesSql.ShouldContain(
            $"current_setting('{Infrastructure.Tenancy.SessionVariables.TenantId}', true)",
            Case.Sensitive,
            "platform.current_tenant_id() must read the same session variable the interceptor sets.");

        rolesSql.ShouldContain(
            $"current_setting('{Infrastructure.Tenancy.SessionVariables.ActorId}', true)",
            Case.Sensitive,
            "platform.current_actor_id() must read the same session variable the interceptor sets.");
    }

    [Fact]
    public void Every_module_schema_has_a_ddl_file()
    {
        string schemaDirectory = Path.Combine(RepositoryRoot, "db", "schema");
        string[] files = Directory.GetFiles(schemaDirectory, "*.sql");

        foreach (string module in ModuleNames)
        {
            Assembly assembly = Assembly.Load($"BrightTime.Modules.{module}");

            IEnumerable<Type> contexts = assembly.GetTypes()
                .Where(t => typeof(BrightTimeDbContext).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (Type contextType in contexts)
            {
                string? schemaName = contextType
                    .GetField("SchemaName", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as string;

                schemaName.ShouldNotBeNull(
                    $"{contextType.Name} must expose a public const string SchemaName so the DDL can be matched to it.");

                files.Any(f => File.ReadAllText(f)
                        .Contains($"set search_path = {schemaName}", StringComparison.Ordinal))
                    .ShouldBeTrue($"No file in db/schema declares the '{schemaName}' schema.");
            }
        }
    }

    [GeneratedRegex(@"\b(DateTime|DateTimeOffset)\s*\.\s*(UtcNow|Now|Today)\b")]
    private static partial Regex AmbientNowPattern();

    [GeneratedRegex(@"/\*.*?\*/|//[^\r\n]*", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    private static string StripComments(string source) => CommentPattern().Replace(source, " ");

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("BrightTime.slnx").Length > 0 ||
                directory.GetFiles("BrightTime.sln").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
