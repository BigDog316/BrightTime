namespace BrightTime.SharedKernel.Messaging;

/// <summary>
/// A domain event one module publishes for others to handle.
/// </summary>
/// <remarks>
/// Lives in the shared kernel rather than in Infrastructure on purpose. These
/// types belong in a module's <c>.Contracts</c> assembly — that is the whole
/// point of them — and a Contracts assembly must stay free of EF Core, ASP.NET
/// Core and the outbox implementation so anyone can load it. The architecture
/// test enforces that, so the marker has to sit below both.
/// </remarks>
public interface IModuleMessage
{
    /// <summary>
    /// The stable wire name. Declared explicitly rather than derived from the
    /// CLR type so that renaming or moving a class does not strand messages
    /// already queued in <c>platform.outbox_message</c>.
    /// </summary>
    static abstract string MessageType { get; }
}

/// <summary>
/// Handles one module message. Registered per message type; several handlers
/// per type are expected and normal.
/// </summary>
public interface IModuleMessageHandler<in TMessage>
    where TMessage : IModuleMessage
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}
