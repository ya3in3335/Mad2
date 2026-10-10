using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Veltrix.Modules.Identity;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string Issuer { get; set; } = "veltrix-api";
    public string Audience { get; set; } = "veltrix-clients";
    /// <summary>HMAC signing key (min 32 bytes). Must be supplied by configuration/secret store in production.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int PasswordIterations { get; set; } = PasswordHasher.DefaultIterations;
}

public static class VeltrixClaims
{
    public const string Tenant = "tid";
    public const string Permission = "perm";
    public const string Branch = "br";
    public const string AllBranches = "br_all";
    public const string PlatformAdmin = "pa";
    public const string Device = "did";
    public const string Role = "role";
}

public sealed class JwtTokenService(IOptions<AuthOptions> options)
{
    private readonly AuthOptions _o = options.Value;

    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(_o.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(
        User user, IEnumerable<string> roles, IEnumerable<string> permissions, IReadOnlyList<Guid>? branches, Guid? deviceId, DateTimeOffset now)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (user.TenantId is { } tid) claims.Add(new(VeltrixClaims.Tenant, tid.ToString()));
        if (user.IsPlatformAdmin) claims.Add(new(VeltrixClaims.PlatformAdmin, "1"));
        if (deviceId is { } did) claims.Add(new(VeltrixClaims.Device, did.ToString()));
        claims.AddRange(roles.Select(r => new Claim(VeltrixClaims.Role, r)));
        claims.AddRange(permissions.Distinct().Select(p => new Claim(VeltrixClaims.Permission, p)));
        if (branches is null) claims.Add(new(VeltrixClaims.AllBranches, "1"));
        else claims.AddRange(branches.Select(b => new Claim(VeltrixClaims.Branch, b.ToString())));

        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = _o.Issuer,
        ValidAudience = _o.Audience,
        IssuerSigningKey = Key,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = VeltrixClaims.Role,
    };
}
