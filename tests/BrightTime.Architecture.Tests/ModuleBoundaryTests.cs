using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using TestResult = NetArchTest.Rules.TestResult;

namespace BrightTime.Architecture.Tests;

/// <summary>
/// The module boundary lives here, not in a README.
/// </summary>
/// <remarks>
/// A modular monolith decays into a monolith the first time someone adds a
/// convenient project reference. These tests are the dependency DAG from
/// docs/plan/01-architecture.md, executable.
/// </remarks>
public sealed class ModuleBoundaryTests
{
    /// <summary>The approved DAG: module → the modules whose Contracts it may reference.</summary>
    private static readonly Dictionary<string, string[]> AllowedDependencies = new(StringComparer.Ordinal)
    {
        ["Platform"] = [],
        ["Identity"] = ["Platform"],
        ["Directory"] = ["Platform", "Identity"],
        ["Opportunities"] = ["Platform", "Identity", "Directory"],
        ["Notifications"] = ["Platform", "Identity"],
        ["Enrollment"] = ["Platform", "Identity", "Directory", "Opportunities"],
        ["Attendance"] = ["Platform", "Identity", "Directory", "Opportunities", "Enrollment"],
        ["Community"] = ["Platform", "Identity", "Directory", "Opportunities", "Enrollment"],
    };

    private static IEnumerable<Assembly> ModuleAssemblies =>
        AllowedDependencies.Keys.Select(LoadModule);

    private static Assembly LoadModule(string name) =>
        Assembly.Load($"BrightTime.Modules.{name}");

    private static Assembly LoadContracts(string name) =>
        Assembly.Load($"BrightTime.Modules.{name}.Contracts");

    [Fact]
    public void A_module_never_references_another_modules_implementation()
    {
        // Checked at the assembly level, which is where the rule actually lives. A namespace
        // prefix check (the first version of this test) treats Directory.Contracts as part of
        // Directory and fails the moment a module uses another module's contracts correctly.
        foreach (string module in AllowedDependencies.Keys)
        {
            string[] references = [.. LoadModule(module).GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)];

            string[] offending =
            [
                .. AllowedDependencies.Keys
                    .Where(other => other != module)
                    .Select(other => $"BrightTime.Modules.{other}")
                    .Where(implementation => references.Contains(implementation, StringComparer.Ordinal)),
            ];

            offending.ShouldBeEmpty(
                $"BrightTime.Modules.{module} references another module's implementation: " +
                $"{string.Join(", ", offending)}. Talk to it through its .Contracts assembly " +
                "or publish a message through the outbox.");
        }
    }

    [Fact]
    public void A_module_only_references_contracts_its_milestone_allows()
    {
        foreach ((string module, string[] allowed) in AllowedDependencies)
        {
            string[] forbidden = AllowedDependencies.Keys
                .Where(other => other != module && !allowed.Contains(other, StringComparer.Ordinal))
                .Select(other => $"BrightTime.Modules.{other}.Contracts")
                .ToArray();

            if (forbidden.Length == 0)
            {
                continue;
            }

            TestResult result = Types.InAssembly(LoadModule(module))
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"BrightTime.Modules.{module} depends on a module above it in the DAG, which creates a cycle. " +
                $"Offending types: {Describe(result)}.");
        }
    }

    [Fact]
    public void Contracts_assemblies_depend_on_nothing_but_the_shared_kernel()
    {
        foreach (string module in AllowedDependencies.Keys)
        {
            TestResult result = Types.InAssembly(LoadContracts(module))
                .ShouldNot()
                .HaveDependencyOnAny(
                    "BrightTime.Infrastructure",
                    "BrightTime.Abstractions",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.AspNetCore",
                    "Npgsql")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"BrightTime.Modules.{module}.Contracts has picked up an implementation dependency. " +
                "A Contracts assembly is the public shape of a module and must stay loadable by anyone. " +
                $"Offending types: {Describe(result)}.");
        }
    }

    [Fact]
    public void The_shared_kernel_depends_on_no_brighttime_assembly()
    {
        TestResult result = Types.InAssembly(Assembly.Load("BrightTime.SharedKernel"))
            .ShouldNot()
            .HaveDependencyOnAny(
                "BrightTime.Abstractions",
                "BrightTime.Infrastructure",
                "BrightTime.Modules")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The shared kernel must sit below everything. Offending types: {Describe(result)}.");
    }

    [Fact]
    public void Dependency_graph_is_acyclic()
    {
        // Guards the table itself: a future edit that makes Directory depend on
        // Opportunities would otherwise be enforced faithfully and wrongly.
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        foreach (string module in AllowedDependencies.Keys)
        {
            Walk(module, visiting, done);
        }

        static void Walk(string module, HashSet<string> visiting, HashSet<string> done)
        {
            if (done.Contains(module))
            {
                return;
            }

            visiting.Add(module).ShouldBeTrue($"Cycle in the module dependency table at {module}.");

            foreach (string dependency in AllowedDependencies[module])
            {
                Walk(dependency, visiting, done);
            }

            visiting.Remove(module);
            done.Add(module);
        }
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is { Count: > 0 } names
            ? string.Join(", ", names)
            : "(none reported)";
}
