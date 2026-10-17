using System.Reflection;
using BrightTime.SharedKernel.Privacy;
using BrightTime.Web.Auditing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The boot-time guard: an endpoint that can return youth data must declare [YouthRecord].
/// Endpoints are built by hand, so each case shows exactly what the guard looks at.
/// </summary>
public sealed class YouthRecordGuardTests
{
    [Fact]
    public void A_handler_returning_youth_data_without_the_attribute_is_named()
    {
        string[] offenders = [.. YouthRecordGuard.Offenders(Source(Handler(nameof(Handlers.Roster))))];

        offenders.ShouldBe(["Roster"]);
    }

    [Fact]
    public void The_same_handler_with_the_attribute_passes()
    {
        YouthRecordGuard.Offenders(Source(Handler(nameof(Handlers.Roster), new YouthRecordAttribute("roster.read")))).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(nameof(Handlers.Nested), "a property of a returned record")]
    [InlineData(nameof(Handlers.Wrapped), "inside a framework result's generic argument")]
    [InlineData(nameof(Handlers.Arrays), "an array element")]
    [InlineData(nameof(Handlers.Dictionary), "a dictionary value")]
    public void Youth_data_is_found_however_deep_it_sits(string handler, string where)
    {
        YouthRecordGuard.Offenders(Source(Handler(handler))).ShouldNotBeEmpty(where);
    }

    [Fact]
    public void A_declared_response_type_counts_as_a_return()
    {
        Endpoint endpoint = Endpoint("Results", builder =>
            builder.Metadata.Add(new ProducesResponseTypeMetadata(200, typeof(Pupil), ["application/json"])));

        YouthRecordGuard.Offenders(Source(endpoint)).ShouldBe(["Results"]);
    }

    [Fact]
    public void A_page_holding_youth_data_in_a_field_is_named()
    {
        Endpoint page = Endpoint("RosterPage", builder => builder.Metadata.Add(new ComponentTypeMetadata(typeof(RosterPage))));

        YouthRecordGuard.Offenders(Source(page)).ShouldBe(["RosterPage"]);
    }

    [Fact]
    public void Ordinary_endpoints_and_self_referencing_types_pass()
    {
        YouthRecordGuard.Offenders(Source(Handler(nameof(Handlers.Listing)), Handler(nameof(Handlers.Tree)))).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_real_site_has_no_offenders_and_starts()
    {
        await using WebApplicationFactory<Program> app = new();
        EndpointDataSource endpoints = app.Services.GetRequiredService<EndpointDataSource>();

        endpoints.Endpoints.ShouldNotBeEmpty();
        YouthRecordGuard.Offenders(endpoints).ShouldBeEmpty();
    }

    [Fact]
    public async Task Starting_with_an_offender_fails()
    {
        YouthRecordGuard guard = new(Source(Handler(nameof(Handlers.Roster))));

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() => guard.StartAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain("Roster");
        error.Message.ShouldContain("[YouthRecord]");
    }

    private static DefaultEndpointDataSource Source(params Endpoint[] endpoints) => new(endpoints);

    private static Endpoint Handler(string method, params object[] metadata) =>
        Endpoint(method, builder =>
        {
            builder.Metadata.Add(typeof(Handlers).GetMethod(method, BindingFlags.Public | BindingFlags.Static)!);
            foreach (object m in metadata)
            {
                builder.Metadata.Add(m);
            }
        });

    private static Endpoint Endpoint(string name, Action<RouteEndpointBuilder> configure)
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/" + name.ToLowerInvariant()), 0) { DisplayName = name };
        configure(builder);
        return builder.Build();
    }

    [YouthData]
    public sealed record Pupil(string Name);

    public sealed record Classroom(string Room, IReadOnlyList<Pupil> Pupils);

    public sealed record Listing(string Title, IReadOnlyList<string> Tags);

    public sealed record Node(string Name, IReadOnlyList<Node> Children);

    public static class Handlers
    {
        public static Task<List<Pupil>> Roster() => Task.FromResult(new List<Pupil>());

        public static Classroom Nested() => new("1A", []);

        public static Task<Microsoft.AspNetCore.Http.HttpResults.Ok<Classroom>> Wrapped() => throw new NotSupportedException();

        public static Pupil[][] Arrays() => [];

        public static Dictionary<string, Pupil> Dictionary() => [];

        public static Listing Listing() => new("Kites", []);

        public static Node Tree() => new("root", []);
    }

    private sealed class RosterPage : ComponentBase
    {
        private readonly List<Pupil> _pupils = [];

        public int Count => _pupils.Count;
    }
}
