namespace BrightTime.Infrastructure.Outbox;

/// <summary>Marker for anything publishable through the outbox. Lives in a module's Contracts assembly.</summary>
public interface IModuleMessage
{
    /// <summary>Stable wire name. Explicit rather than derived from the CLR type, so renaming a class does not strand queued messages.</summary>
    static abstract string MessageType { get; }
}

/// <summary>Enqueues a module message for in-process fan-out after commit.</summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Stages a message. It is written when the caller's unit of work commits;
    /// if the transaction rolls back, the message was never published.
    /// </summary>
    void Publish<TMessage>(TMessage message)
        where TMessage : IModuleMessage;
}

/// <summary>Handles one module message. Registered per message type; several handlers per type are expected.</summary>
public interface IModuleMessageHandler<in TMessage>
    where TMessage : IModuleMessage
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}
