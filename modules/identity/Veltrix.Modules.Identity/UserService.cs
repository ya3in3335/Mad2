using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Identity;

/// <summary>Company user and role administration. All queries are scoped to the caller's tenant.</summary>
public sealed class UserService(DbContext db, ITenantContext ctx, AuthService auth, PasswordHasher hasher, IAuditWriter audit, IBranchDirectory branchDirectory)
{
    public async Task<PagedResult<UserDto>> ListAsync(PageQuery q, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var query = db.Set<User>().Include(u => u.Roles).ThenInclude(r => r.Role).Include(u => u.Branches)
            .Where(u => u.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail.Contains(s) || u.FullName.ToUpper().Contains(s));
        }
        query = q.SortField switch
        {
            "email" => q.Descending ? query.OrderByDescending(u => u.Email) : query.OrderBy(u => u.Email),
            "lastLoginAt" => q.Descending ? query.OrderByDescending(u => u.LastLoginAt) : query.OrderBy(u => u.LastLoginAt),
            _ => q.Descending ? query.OrderByDescending(u => u.FullName) : query.OrderBy(u => u.FullName),
        };
        var page = await query.ToPagedAsync(q, ct);
        return new PagedResult<UserDto>(page.Items.Select(ToDto).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<UserDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<UserDto> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Email, "email", 256).Require(req.Email?.Contains('@') == true, "email", "Invalid email.")
            .NotEmpty(req.FullName, "fullName").Require(req.Roles is { Count: > 0 }, "roles", "At least one role is required.")
            .ThrowIfInvalid();
        var normalized = AuthService.Normalize(req.Email);
        if (await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, ct))
            throw new ConflictException("user.email_taken", "A user with this email already exists.");
        var user = new User
        {
            TenantId = tid, Email = req.Email.Trim(), NormalizedEmail = normalized, FullName = req.FullName.Trim(),
            Phone = req.Phone, PreferredLanguage = req.PreferredLanguage,
        };
        auth.SetPassword(user, req.Password);
        await ApplyRolesAsync(user, req.Roles, tid, ct);
        await ApplyBranchesAsync(user, req.Branches, tid, ct);
        db.Set<User>().Add(user);
        audit.Write("user.created", nameof(User), user.Id.ToString(), new { user.Email, req.Roles });
        await db.SaveChangesAsync(ct);
        return await GetAsync(user.Id, ct);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var user = await LoadAsync(id, ct);
        new Validator().NotEmpty(req.FullName, "fullName").Require(req.Roles is { Count: > 0 }, "roles", "At least one role is required.").ThrowIfInvalid();
        if (user.Id == ctx.UserId && !req.IsActive)
            throw new DomainException("user.self_deactivate", "You cannot deactivate your own account.");
        await EnsureNotRemovingLastOwnerAsync(user, req.Roles, req.IsActive, ct);
        user.FullName = req.FullName.Trim();
        user.Phone = req.Phone;
        if (user.IsActive != req.IsActive)
        {
            user.IsActive = req.IsActive;
            user.SecurityStamp++;
            if (!req.IsActive) await auth.RevokeAllAsync(user.Id, "deactivated", ct);
        }
        user.Roles.Clear();
        user.Branches.Clear();
        await ApplyRolesAsync(user, req.Roles, tid, ct);
        await ApplyBranchesAsync(user, req.Branches, tid, ct);
        user.SecurityStamp++;
        audit.Write("user.updated", nameof(User), user.Id.ToString(), new { req.Roles, req.IsActive });
        await db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(id, ct));
    }

    public async Task AdminResetPasswordAsync(Guid id, string newPassword, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct);
        auth.SetPassword(user, newPassword);
        user.LockoutUntil = null;
        user.FailedLoginCount = 0;
        await auth.RevokeAllAsync(user.Id, "admin_reset", ct);
        audit.Write("user.password_reset_by_admin", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPinAsync(Guid userId, string pin, CancellationToken ct)
    {
        if (pin is null || pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
            throw ValidationException.For("pin", "PIN must be 4 to 8 digits.");
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);
        user.PinHash = hasher.Hash(pin);
        await db.SaveChangesAsync(ct);
    }

    // ---- Roles ----
    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var roles = await db.Set<Role>().Include(r => r.Permissions).Where(r => r.TenantId == tid).OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(ToDto).ToList();
    }

    public async Task<RoleDto> UpsertRoleAsync(Guid? id, UpsertRoleRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Code, "code", 64).NotEmpty(req.Name, "name", 120)
            .Require(req.Permissions.All(p => Permissions.CompanyPermissions.Contains(p)), "permissions", "Unknown permission.")
            .ThrowIfInvalid();
        Role role;
        if (id is { } rid)
        {
            role = await db.Set<Role>().Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Id == rid && r.TenantId == tid, ct)
                ?? throw new NotFoundException("role.not_found", "Role not found.");
            if (role.IsSystem) throw new DomainException("role.system", "Built-in roles cannot be modified. Create a custom role instead.");
            role.Permissions.Clear();
        }
        else
        {
            if (await db.Set<Role>().AnyAsync(r => r.TenantId == tid && r.Code == req.Code, ct))
                throw new ConflictException("role.code_taken", "Role code already exists.");
            role = new Role { TenantId = tid, Code = req.Code.Trim().ToLowerInvariant() };
            db.Set<Role>().Add(role);
        }
        role.Name = req.Name.Trim();
        foreach (var p in req.Permissions.Distinct()) role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        audit.Write(id is null ? "role.created" : "role.updated", nameof(Role), role.Id.ToString(), new { req.Permissions });
        await db.SaveChangesAsync(ct);
        return ToDto(role);
    }

    /// <summary>Create the built-in role set for a new tenant.</summary>
    public static List<Role> CreateSystemRoles(Guid tenantId)
    {
        var names = new Dictionary<string, string>
        {
            [SystemRoles.CompanyOwner] = "Company Owner", [SystemRoles.CompanyAdmin] = "Company Administrator",
            [SystemRoles.BranchManager] = "Branch Manager", [SystemRoles.Accountant] = "Accountant",
            [SystemRoles.Cashier] = "Cashier", [SystemRoles.WarehouseManager] = "Warehouse Manager",
            [SystemRoles.SalesRep] = "Sales Representative", [SystemRoles.HrManager] = "HR Manager",
            [SystemRoles.Auditor] = "Auditor", [SystemRoles.ReadOnly] = "Read-only User",
        };
        return SystemRoles.Templates.Select(t =>
        {
            var role = new Role { TenantId = tenantId, Code = t.Key, Name = names[t.Key], IsSystem = true };
            role.Permissions = t.Value.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }).ToList();
            return role;
        }).ToList();
    }

    private async Task<User> LoadAsync(Guid id, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        return await db.Set<User>().Include(u => u.Roles).ThenInclude(r => r.Role).Include(u => u.Branches)
                   .SingleOrDefaultAsync(u => u.Id == id && u.TenantId == tid, ct)
               ?? throw new NotFoundException("user.not_found", "User not found.");
    }

    private async Task ApplyRolesAsync(User user, IReadOnlyList<string> codes, Guid tid, CancellationToken ct)
    {
        var roles = await db.Set<Role>().Where(r => r.TenantId == tid && codes.Contains(r.Code)).ToListAsync(ct);
        if (roles.Count != codes.Distinct().Count()) throw ValidationException.For("roles", "Unknown role.");
        // Only owners may grant the owner role.
        if (roles.Any(r => r.Code == SystemRoles.CompanyOwner) && !ctx.HasPermission(Permissions.All))
            throw new ForbiddenException("role.owner_grant", "Only company owners can grant the owner role.");
        foreach (var r in roles) user.Roles.Add(new UserRole { UserId = user.Id, RoleId = r.Id, Role = r });
    }

    private async Task ApplyBranchesAsync(User user, IReadOnlyList<Guid>? branches, Guid tid, CancellationToken ct)
    {
        if (branches is null) return;
        var valid = await branchDirectory.GetBranchIdsAsync(tid, ct);
        if (branches.Any(b => !valid.Contains(b))) throw ValidationException.For("branches", "Unknown branch.");
        foreach (var b in branches.Distinct()) user.Branches.Add(new UserBranch { UserId = user.Id, BranchId = b });
    }

    private async Task EnsureNotRemovingLastOwnerAsync(User user, IReadOnlyList<string> newRoles, bool active, CancellationToken ct)
    {
        var isOwner = user.Roles.Any(r => r.Role?.Code == SystemRoles.CompanyOwner);
        if (!isOwner || (newRoles.Contains(SystemRoles.CompanyOwner) && active)) return;
        var owners = await db.Set<User>().CountAsync(u => u.TenantId == user.TenantId && u.IsActive &&
            u.Roles.Any(r => r.Role!.Code == SystemRoles.CompanyOwner), ct);
        if (owners <= 1) throw new DomainException("user.last_owner", "A company must keep at least one active owner.");
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Email, u.FullName, u.Phone, u.IsActive,
        u.Roles.Where(r => r.Role != null).Select(r => r.Role!.Code).ToList(), u.Branches.Select(b => b.BranchId).ToList(),
        u.LastLoginAt, u.MfaEnabled, u.LockoutUntil > DateTimeOffset.UtcNow);

    private static RoleDto ToDto(Role r) => new(r.Id, r.Code, r.Name, r.IsSystem, r.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList());
}
