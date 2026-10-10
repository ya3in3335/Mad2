using Microsoft.EntityFrameworkCore;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Identity;

/// <summary>Seeds built-in roles and the first company owner during onboarding.</summary>
public sealed class IdentityOnboardingContributor(DbContext db, AuthService auth) : IOnboardingContributor
{
    public int Order => 10;

    public async Task ContributeAsync(OnboardingContext c, CancellationToken ct)
    {
        var normalized = AuthService.Normalize(c.OwnerEmail);
        if (await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, ct))
            throw new ConflictException("user.email_taken", "A user with this email already exists.");
        var roles = UserService.CreateSystemRoles(c.TenantId);
        db.Set<Role>().AddRange(roles);
        var owner = new User
        {
            TenantId = c.TenantId, Email = c.OwnerEmail, NormalizedEmail = normalized, FullName = c.OwnerFullName,
            PreferredLanguage = c.OwnerLanguage,
        };
        auth.SetPassword(owner, c.OwnerPassword);
        var ownerRole = roles.Single(r => r.Code == SystemRoles.CompanyOwner);
        owner.Roles.Add(new UserRole { UserId = owner.Id, RoleId = ownerRole.Id });
        db.Set<User>().Add(owner);
        await db.SaveChangesAsync(ct);
    }
}
