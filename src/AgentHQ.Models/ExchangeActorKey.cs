namespace AgentHQ.Models;

/// <summary>A composite exchange actor key: the (type, id) pair that addresses rows.</summary>
public sealed record ExchangeActorKey(string Type, string Id);
