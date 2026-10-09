using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Infrastructure.Outbox;

/// <summary>Stages a module message for in-process fan-out after commit.</summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Stages a message. It is written when the caller's unit of work commits;
    /// if the transaction rolls back, the message was never published. That
    /// coupling is the point — there is no window in which a state change
    /// happened but its event did not.
    /// </summary>
    void Publish<TMessage>(TMessage message)
        where TMessage : IModuleMessage;
}
