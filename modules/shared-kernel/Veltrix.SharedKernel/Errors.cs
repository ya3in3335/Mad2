namespace Veltrix.SharedKernel;

/// <summary>Base for expected business errors. Mapped to RFC 7807 problem responses by the API host.</summary>
public class DomainException(string code, string message, int statusCode = 400, IDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class NotFoundException(string code, string message) : DomainException(code, message, 404);
public sealed class ForbiddenException(string code, string message) : DomainException(code, message, 403);
public sealed class ConflictException(string code, string message) : DomainException(code, message, 409);
public sealed class UnauthorizedException(string code, string message) : DomainException(code, message, 401);
public sealed class PaymentRequiredException(string code, string message) : DomainException(code, message, 402);

public sealed class ValidationException(IDictionary<string, string[]> errors)
    : DomainException("validation.failed", "One or more validation errors occurred.", 400, errors)
{
    public static ValidationException For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Small fluent validator used by application services. Collects all errors before throwing.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public Validator Require(bool condition, string field, string message)
    {
        if (!condition)
        {
            if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = new();
            list.Add(message);
        }
        return this;
    }

    public Validator NotEmpty(string? value, string field, int maxLength = 200) =>
        Require(!string.IsNullOrWhiteSpace(value), field, "Required.")
            .Require(value is null || value.Length <= maxLength, field, $"Must be at most {maxLength} characters.");

    public Validator MaxLength(string? value, string field, int maxLength) =>
        Require(value is null || value.Length <= maxLength, field, $"Must be at most {maxLength} characters.");

    public Validator Positive(decimal value, string field) => Require(value > 0, field, "Must be greater than zero.");
    public Validator NonNegative(decimal value, string field) => Require(value >= 0, field, "Must not be negative.");

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
            throw new ValidationException(_errors.ToDictionary(k => k.Key, v => v.Value.ToArray()));
    }
}
