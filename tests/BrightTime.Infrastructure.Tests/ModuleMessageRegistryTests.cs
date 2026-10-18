using System.Text.Json;
using BrightTime.Infrastructure.Outbox;
using BrightTime.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Infrastructure.Tests;

public sealed class ModuleMessageRegistryTests
{
    public enum Colour
    {
        Red,
        Green,
    }

    public sealed record Painted(Guid Id, Colour Colour) : IModuleMessage
    {
        public static string MessageType => "studio.wall.painted";
    }

    public sealed class First : IModuleMessageHandler<Painted>
    {
        public Painted? Got { get; private set; }

        public Task HandleAsync(Painted message, CancellationToken cancellationToken)
        {
            Got = message;
            return Task.CompletedTask;
        }
    }

    public sealed class Second : IModuleMessageHandler<Painted>
    {
        public Task HandleAsync(Painted message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Several_handlers_subscribe_to_one_message_in_registration_order()
    {
        ServiceCollection services = new();
        services.AddMessageHandler<Painted, First>().AddMessageHandler<Painted, Second>();

        ModuleMessageRegistry registry = services.BuildServiceProvider().GetRequiredService<ModuleMessageRegistry>();

        registry.HandlersFor(Painted.MessageType).ShouldBe([ModuleMessageRegistry.KeyOf(typeof(First)), ModuleMessageRegistry.KeyOf(typeof(Second))]);
        registry.HandlersFor("studio.nobody.listens").ShouldBeEmpty();
    }

    [Fact]
    public void A_handler_is_registered_once()
    {
        ServiceCollection services = new();
        services.AddMessageHandler<Painted, First>();

        Should.Throw<InvalidOperationException>(() => services.AddMessageHandler<Painted, First>());
    }

    [Fact]
    public async Task A_stored_message_is_read_back_and_handed_to_its_handler()
    {
        ServiceCollection services = new();
        services.AddMessageHandler<Painted, First>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Painted sent = new(Guid.NewGuid(), Colour.Green);

        ModuleMessageRegistry.Invoker invoke = provider.GetRequiredService<ModuleMessageRegistry>().Resolve(ModuleMessageRegistry.KeyOf(typeof(First)))!;
        await invoke(scope.ServiceProvider, JsonSerializer.Serialize(sent, OutboxPublish.Json), TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<First>().Got.ShouldBe(sent);
    }

    [Fact]
    public void Enums_are_stored_by_name_so_reordering_one_cannot_change_a_stored_message() =>
        JsonSerializer.Serialize(new Painted(Guid.Empty, Colour.Green), OutboxPublish.Json).ShouldContain("\"colour\":\"Green\"");

    [Theory]
    [InlineData("directory.site.changed", "directory")]
    [InlineData("platform.taxonomy_term.changed", "platform")]
    [InlineData("loose", "loose")]
    public void The_source_module_is_the_message_types_first_part(string type, string module) =>
        OutboxPublish.SourceModule(type).ShouldBe(module);
}
