using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veltrix.Api.Infrastructure;

#nullable disable

namespace Veltrix.Api.Migrations
{
    /// <summary>
    /// Data-only migration for the single-plan model: legacy tier plans (starter/professional/enterprise/...) are kept for history
    /// but made inactive and private; the "full" plan (all features) is seeded at startup. No schema change.
    /// </summary>
    [DbContext(typeof(VeltrixDbContext))]
    [Migration("20261010180000_SingleFullPlan")]
    public partial class SingleFullPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE subscriptions.plans SET "IsActive" = false, "IsPublic" = false WHERE "Code" <> 'full';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible by design: previous active/public flags are not recorded.
        }
    }
}
