namespace Veltrix.Contracts.Crm;

public sealed record CustomerDto(Guid Id, string Code, string Type, string Name, string? Phone, string? Email, string? Address, string? City, string? Wilaya,
    string? TaxId, string? TradeRegisterNo, Guid? GroupId, Guid? PriceListId, decimal CreditLimit, decimal LoyaltyPoints, bool IsActive,
    bool MarketingConsent, DateTimeOffset? ConsentAt, string? Notes, decimal Balance, DateTimeOffset UpdatedAt);
public sealed record UpsertCustomerRequest(string Type, string Name, string? Phone, string? Email, string? Address, string? City, string? Wilaya,
    string? TaxId, string? TradeRegisterNo, Guid? GroupId, Guid? PriceListId, decimal CreditLimit, bool MarketingConsent, string? Notes,
    string? Code = null, Guid? Id = null);

public sealed record CustomerGroupDto(Guid Id, string Name, decimal DiscountPercent, Guid? PriceListId);
public sealed record UpsertCustomerGroupRequest(string Name, decimal DiscountPercent, Guid? PriceListId);

public sealed record CustomerNoteDto(Guid Id, string Text, Guid? AuthorId, DateTimeOffset CreatedAt);
public sealed record AddNoteRequest(string Text);

public sealed record FollowUpTaskDto(Guid Id, Guid CustomerId, string Title, DateOnly DueDate, Guid? AssignedToUserId, string Status, string? Notes);
public sealed record UpsertTaskRequest(Guid CustomerId, string Title, DateOnly DueDate, Guid? AssignedToUserId, string Status, string? Notes);

public sealed record OpportunityDto(Guid Id, Guid CustomerId, string Title, string Stage, decimal EstimatedValue, int Probability, DateOnly? ExpectedCloseDate,
    Guid? OwnerUserId, string? Notes, DateTimeOffset UpdatedAt);
public sealed record UpsertOpportunityRequest(Guid CustomerId, string Title, string Stage, decimal EstimatedValue, int Probability, DateOnly? ExpectedCloseDate,
    Guid? OwnerUserId, string? Notes);

public sealed record LoyaltyTransactionDto(Guid Id, decimal Points, string Reason, string? Reference, DateTimeOffset CreatedAt);
public sealed record AdjustLoyaltyRequest(decimal Points, string Reason);

public sealed record StatementLineDto(DateOnly Date, string Reference, string Description, decimal Debit, decimal Credit, decimal Balance);
public sealed record CustomerStatementDto(Guid CustomerId, string Name, DateOnly From, DateOnly To, decimal OpeningBalance, IReadOnlyList<StatementLineDto> Lines, decimal ClosingBalance);

public sealed record SegmentDto(string Segment, int Customers, decimal Revenue);
public sealed record CustomerPurchaseDto(string Source, Guid DocumentId, string Number, DateTimeOffset Date, decimal Total);
