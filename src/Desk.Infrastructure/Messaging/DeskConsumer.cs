using System.Reflection;
using System.Text.Json;
using Desk.Core.Events;

namespace Desk.Infrastructure.Messaging;

/// <summary>Where a consumer runs: the Worker, or the API (the SignalR hub lives there).</summary>
public enum ConsumerHost
{
    Worker,
    Api,
}

/// <summary>
/// Declares a consumer: its queue (<c>desk.{queueSuffix}</c>), the routing keys bound to it and its host.
/// This attribute is the whole declaration: topology, delay queues and hosting follow from it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ConsumerAttribute(string queueSuffix, ConsumerHost host, params string[] routingKeys) : Attribute
{
    public string QueueSuffix { get; } = queueSuffix;

    public ConsumerHost Host { get; } = host;

    public string[] RoutingKeys { get; } = routingKeys;
}

public sealed record ConsumerDefinition(Type Type, string Name, string QueueSuffix, IReadOnlyList<string> RoutingKeys, ConsumerHost Host)
{
    public string Queue => MessagingNames.QueueFor(QueueSuffix);

    public static ConsumerDefinition For(Type type)
    {
        var attribute = (ConsumerAttribute?)Attribute.GetCustomAttribute(type, typeof(ConsumerAttribute))
            ?? throw new InvalidOperationException($"{type.Name} must carry [Consumer(...)].");
        if (!typeof(DeskConsumer).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{type.Name} must derive from {nameof(DeskConsumer)}.");
        }

        return new ConsumerDefinition(type, type.Name, attribute.QueueSuffix, attribute.RoutingKeys, attribute.Host);
    }

    public static IEnumerable<ConsumerDefinition> Scan(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DeskConsumer).IsAssignableFrom(t) && Attribute.IsDefined(t, typeof(ConsumerAttribute)))
            .Select(For);
}

/// <summary>
/// Base class of every consumer. It is resolved from a DI scope created per message, after the pipeline has set the
/// tenant of the message, opened the transaction and recorded <c>ProcessedMessage</c>: the handler only holds the business effect.
/// Throw <see cref="UnprocessableMessageException"/> for a message that can never succeed (no retry, straight to the DLQ);
/// any other exception triggers the retry schedule.
/// </summary>
public abstract class DeskConsumer
{
    public abstract Task HandleAsync(MessageEnvelope message, CancellationToken ct);
}

/// <summary>The message is well-formed but cannot be processed, whatever the number of attempts (e.g. unknown ticket).</summary>
public sealed class UnprocessableMessageException(string message) : Exception(message);

/// <summary>Raised for a consumer named by <c>Messaging:FailConsumer</c>.</summary>
public sealed class SimulatedFailureException(string consumer) : Exception($"Failure forced by Messaging:FailConsumer for {consumer}.");

public static class FailureClassifier
{
    /// <summary>True when retrying cannot help: the message goes straight to the DLQ.</summary>
    public static bool IsPermanent(Exception exception) =>
        exception is UnprocessableMessageException or JsonException;
}
