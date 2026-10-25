using System.Collections.Concurrent;
using System.Text.Json;
using AgentHQ.Core.Events;

namespace AgentHQ.Api.Services;

/// <summary>Fan-out for live SSE subscribers. Replay comes from the events table.</summary>
public sealed class SseHub
{
    private readonly ConcurrentDictionary<Guid, Func<AgentHqEvent, Task>> _subscribers = new();

    public IDisposable Subscribe(Func<AgentHqEvent, Task> onEvent)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = onEvent;
        return new Unsubscriber(() => _subscribers.TryRemove(id, out _));
    }

    public Task PublishAsync(AgentHqEvent evt)
    {
        var tasks = _subscribers.Values.Select(f => f(evt));
        return Task.WhenAll(tasks);
    }

    public static string Format(AgentHqEvent evt) =>
        $"id: {evt.Seq}\nevent: {evt.Type}\ndata: {JsonSerializer.Serialize(evt.Payload, AgentHqEvent.JsonOpts)}\n\n";

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
