using System.Text.Json;
using BrightTime.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Infrastructure.Outbox;

/// <summary>
/// Who handles which module message. Several handlers per message type are expected; each is
/// delivered separately, as its own job, so one failing handler neither blocks nor repeats another.
/// </summary>
public sealed class ModuleMessageRegistry
{
    /// <summary>Runs one handler for a stored message, in a scope the caller has set the tenant on.</summary>
    public delegate Task Invoker(IServiceProvider scope, string payload, CancellationToken cancellationToken);

    private readonly Dictionary<string, List<string>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Invoker> _byHandler = new(StringComparer.Ordinal);

    /// <summary>The handler keys subscribed to a message type, in registration order. Empty when nobody is.</summary>
    public IReadOnlyList<string> HandlersFor(string messageType) =>
        _byType.TryGetValue(messageType, out List<string>? keys) ? keys : [];

    public Invoker? Resolve(string handlerKey) => _byHandler.GetValueOrDefault(handlerKey);

    /// <summary>
    /// A handler's stable name: its type's full name. A job already queued for a handler that is
    /// renamed fails with "no handler" and is retried until it is dead, so rename with care.
    /// </summary>
    public static string KeyOf(Type handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler.FullName ?? handler.Name;
    }

    internal void Register<TMessage, THandler>()
        where TMessage : IModuleMessage
        where THandler : class, IModuleMessageHandler<TMessage>
    {
        string key = KeyOf(typeof(THandler));
        if (_byHandler.ContainsKey(key))
        {
            throw new InvalidOperationException($"{key} is already registered as a message handler; a handler handles one message type.");
        }

        _byHandler[key] = async (scope, payload, ct) =>
        {
            TMessage message = JsonSerializer.Deserialize<TMessage>(payload, OutboxPublish.Json)
                ?? throw new InvalidOperationException($"An empty {TMessage.MessageType} message cannot be handled.");
            await scope.GetRequiredService<THandler>().HandleAsync(message, ct).ConfigureAwait(false);
        };

        if (!_byType.TryGetValue(TMessage.MessageType, out List<string>? keys))
        {
            _byType[TMessage.MessageType] = keys = [];
        }

        keys.Add(key);
    }
}

public static class ModuleMessageRegistration
{
    /// <summary>Subscribes <typeparamref name="THandler"/> to <typeparamref name="TMessage"/>. Handlers must be idempotent: delivery is at least once.</summary>
    public static IServiceCollection AddMessageHandler<TMessage, THandler>(this IServiceCollection services)
        where TMessage : IModuleMessage
        where THandler : class, IModuleMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<THandler>();
        Registry(services).Register<TMessage, THandler>();
        return services;
    }

    internal static ModuleMessageRegistry Registry(IServiceCollection services)
    {
        ServiceDescriptor? existing = services.FirstOrDefault(d => d.ServiceType == typeof(ModuleMessageRegistry));
        if (existing?.ImplementationInstance is ModuleMessageRegistry registry)
        {
            return registry;
        }

        registry = new ModuleMessageRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
