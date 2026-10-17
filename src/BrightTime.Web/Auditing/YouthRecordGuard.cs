using System.Collections;
using System.Reflection;
using BrightTime.SharedKernel.Privacy;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http.Metadata;

namespace BrightTime.Web.Auditing;

/// <summary>
/// Refuses to start the host if any endpoint can return youth data without declaring
/// <see cref="YouthRecordAttribute"/>. Forgetting the audit becomes a boot failure, not a
/// silent gap (docs/plan/04-roles-and-permissions.md).
/// </summary>
/// <remarks>
/// <para>
/// "Can return" is decided from types. For a minimal API endpoint, its handler's return type
/// and any declared response types; for a page, the component's own fields and properties,
/// which is where a page holds what it renders. Each is walked through generics, arrays,
/// collections and properties, looking for a type marked <see cref="YouthDataAttribute"/>.
/// </para>
/// <para>
/// It is a type check, so it errs toward strictness: an endpoint that merely could return
/// youth data must declare the attribute and audit. It cannot see data smuggled through
/// <c>object</c> or a string; review covers that.
/// </para>
/// </remarks>
public sealed class YouthRecordGuard(EndpointDataSource endpoints) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        List<string> offenders = [.. Offenders(endpoints)];
        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                "These endpoints can return youth data but do not declare [YouthRecord], so their reads would not be audited: "
                + string.Join("; ", offenders));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static IEnumerable<string> Offenders(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (Endpoint endpoint in source.Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<YouthRecordAttribute>() is not null)
            {
                continue;
            }

            IEnumerable<Type> roots = RootsOf(endpoint);
            if (roots.Any(t => CarriesYouthData(t, [])))
            {
                yield return endpoint.DisplayName ?? endpoint.ToString() ?? "(unnamed endpoint)";
            }
        }
    }

    private static IEnumerable<Type> RootsOf(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is { } handler)
        {
            yield return handler.ReturnType;
        }

        foreach (IProducesResponseTypeMetadata produces in endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>())
        {
            if (produces.Type is { } type)
            {
                yield return type;
            }
        }

        if (endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is { } component)
        {
            foreach (Type member in MemberTypes(component.Type))
            {
                yield return member;
            }
        }
    }

    private static bool CarriesYouthData(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type) || type.IsPrimitive || type == typeof(string) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.IsGenericType)
        {
            return false;
        }

        if (type.GetCustomAttribute<YouthDataAttribute>(inherit: true) is not null)
        {
            return true;
        }

        if (type.IsArray && CarriesYouthData(type.GetElementType()!, seen))
        {
            return true;
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(a => CarriesYouthData(a, seen)))
        {
            return true;
        }

        // Framework types (Task, results, collections) carry data only through their generic arguments.
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true
            || typeof(IEnumerable).IsAssignableFrom(type) && !type.IsGenericType)
        {
            return false;
        }

        return MemberTypes(type).Any(t => CarriesYouthData(t, seen));
    }

    private static IEnumerable<Type> MemberTypes(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.PropertyType)
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.Name.Contains("k__BackingField", StringComparison.Ordinal))
                .Select(f => f.FieldType));
}
