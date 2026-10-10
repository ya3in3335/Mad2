namespace Veltrix.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>RFC 7807 problem details shape returned for all errors.</summary>
public sealed record ProblemResponse(string Type, string Title, int Status, string? Detail, string? Code, string? CorrelationId,
    IDictionary<string, string[]>? Errors);

public sealed record IdResponse(Guid Id);
public sealed record NumberedIdResponse(Guid Id, string Number);
