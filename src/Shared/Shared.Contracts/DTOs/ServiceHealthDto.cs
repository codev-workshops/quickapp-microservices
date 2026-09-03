namespace Shared.Contracts.DTOs;

public record ServiceHealthDto(
    string ServiceName,
    string Status,
    DateTime CheckedAt
);
