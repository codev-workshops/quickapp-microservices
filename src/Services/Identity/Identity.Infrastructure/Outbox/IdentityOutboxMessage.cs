namespace Identity.Infrastructure.Outbox;

/// <summary>
/// Row-level change captured from identitydb, replayed into the monolith SQL Server
/// database by <see cref="ReverseSyncPublisher"/> (reverse CDC for zero-data-loss rollback).
/// </summary>
public class IdentityOutboxMessage
{
    public long Id { get; set; }
    public string TableName { get; set; } = string.Empty;
    public OutboxOperation Operation { get; set; }
    /// <summary>JSON object of primary-key column → value.</summary>
    public string KeyJson { get; set; } = string.Empty;
    /// <summary>JSON object of column → value (null for deletes).</summary>
    public string? PayloadJson { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public enum OutboxOperation
{
    Upsert = 1,
    Delete = 2
}
