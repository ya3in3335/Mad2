namespace Veltrix.Contracts.Subscriptions;

public sealed record PlanDto(Guid Id, string Code, string Name, string? Description, decimal MonthlyPrice, decimal AnnualPrice, string Currency,
    IReadOnlyList<string> Features, int MaxDevices, int MaxUsers, int MaxBranches, int TrialDays, int OfflineGraceDays, bool IsActive, bool IsPublic);
public sealed record UpsertPlanRequest(string Code, string Name, string? Description, decimal MonthlyPrice, decimal AnnualPrice, string Currency,
    IReadOnlyList<string> Features, int MaxDevices, int MaxUsers, int MaxBranches, int TrialDays, int OfflineGraceDays, bool IsActive, bool IsPublic);

public sealed record SubscriptionDto(Guid Id, Guid TenantId, string? TenantName, string PlanCode, string BillingCycle, string Status, DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd, int Seats, bool CancelAtPeriodEnd, Guid? LicenseId, decimal PeriodPrice, string Currency, DateTimeOffset CreatedAt);
public sealed record CreateSubscriptionRequest(Guid TenantId, string PlanCode, string BillingCycle, int? Seats, bool StartWithTrial, decimal? CustomPrice);
public sealed record ChangePlanRequest(string PlanCode, string? BillingCycle, bool ProrateInvoice);
public sealed record ChangeSeatsRequest(int Seats);
public sealed record CancelSubscriptionRequest(bool Immediately, string Reason);

public sealed record BillingInvoiceDto(Guid Id, string Number, Guid TenantId, string? TenantName, Guid SubscriptionId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    decimal Amount, decimal Tax, decimal Total, string Currency, string Status, DateTimeOffset IssuedAt, DateOnly DueDate, DateTimeOffset? PaidAt,
    string? PaymentMethod, string? PaymentReference, string? Description);
public sealed record ConfirmPaymentRequest(string Method, string? Reference, DateTimeOffset? PaidAt);
public sealed record IssueInvoiceRequest(decimal? TaxPercent);
public sealed record EntitlementChangeDto(Guid Id, Guid SubscriptionId, string Change, string? Details, DateTimeOffset At, Guid? By);
public sealed record MySubscriptionDto(SubscriptionDto? Subscription, PlanDto? Plan, IReadOnlyList<BillingInvoiceDto> Invoices, string PaymentInstructions);
