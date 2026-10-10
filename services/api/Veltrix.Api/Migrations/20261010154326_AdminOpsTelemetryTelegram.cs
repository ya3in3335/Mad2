using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veltrix.Api.Migrations
{
    /// <inheritdoc />
    public partial class AdminOpsTelemetryTelegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "diagnostics");

            migrationBuilder.EnsureSchema(
                name: "telegram");

            migrationBuilder.AddColumn<int>(
                name: "FailedSyncOperations",
                schema: "licensing",
                table: "devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHeartbeatAt",
                schema: "licensing",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncAt",
                schema: "licensing",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                schema: "licensing",
                table: "devices",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingSyncOperations",
                schema: "licensing",
                table: "devices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "diagnostic_requests",
                schema: "diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    OsVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ReportJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    App = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ErrorType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SampleStackTrace = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AlertedThreshold = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "telegram",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChatId = table.Column<long>(type: "bigint", nullable: true),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "error_events",
                schema: "diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OsVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Context = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_error_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_error_events_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "diagnostics",
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_LastHeartbeatAt",
                schema: "licensing",
                table: "devices",
                column: "LastHeartbeatAt");

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_requests_DeviceId_Status",
                schema: "diagnostics",
                table: "diagnostic_requests",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_error_events_DeviceId",
                schema: "diagnostics",
                table: "error_events",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_error_events_IncidentId_OccurredAt",
                schema: "diagnostics",
                table: "error_events",
                columns: new[] { "IncidentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Fingerprint_TenantId",
                schema: "diagnostics",
                table: "incidents",
                columns: new[] { "Fingerprint", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Status_LastSeenAt",
                schema: "diagnostics",
                table: "incidents",
                columns: new[] { "Status", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_TenantId",
                schema: "diagnostics",
                table: "incidents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_CreatedAt",
                schema: "telegram",
                table: "outbox",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_DedupKey_CreatedAt",
                schema: "telegram",
                table: "outbox",
                columns: new[] { "DedupKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_Status_NextAttemptAt",
                schema: "telegram",
                table: "outbox",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnostic_requests",
                schema: "diagnostics");

            migrationBuilder.DropTable(
                name: "error_events",
                schema: "diagnostics");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "telegram");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_devices_LastHeartbeatAt",
                schema: "licensing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "FailedSyncOperations",
                schema: "licensing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                schema: "licensing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "LastSyncAt",
                schema: "licensing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                schema: "licensing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "PendingSyncOperations",
                schema: "licensing",
                table: "devices");
        }
    }
}
