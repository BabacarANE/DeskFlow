using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddKnowledgeAndSlaBreach : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KnowledgeArticles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Symptom = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                Cause = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                Solution = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                Tags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SourceTicketId = table.Column<Guid>(type: "uuid", nullable: true),
                AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KnowledgeArticles", x => x.Id);
                table.ForeignKey(
                    name: "FK_KnowledgeArticles_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_KnowledgeArticles_Tickets_SourceTicketId",
                    column: x => x.SourceTicketId,
                    principalTable: "Tickets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_KnowledgeArticles_Users_AuthorId",
                    column: x => x.AuthorId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "SlaBreaches",
            columns: table => new
            {
                TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlaBreaches", x => new { x.TicketId, x.Kind });
                table.ForeignKey(
                    name: "FK_SlaBreaches_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_SlaBreaches_Tickets_TicketId",
                    column: x => x.TicketId,
                    principalTable: "Tickets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeArticles_AuthorId",
            table: "KnowledgeArticles",
            column: "AuthorId");

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeArticles_SourceTicketId",
            table: "KnowledgeArticles",
            column: "SourceTicketId");

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeArticles_TenantId_SourceTicketId",
            table: "KnowledgeArticles",
            columns: new[] { "TenantId", "SourceTicketId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeArticles_TenantId_Status",
            table: "KnowledgeArticles",
            columns: new[] { "TenantId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_SlaBreaches_TenantId",
            table: "SlaBreaches",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "KnowledgeArticles");

        migrationBuilder.DropTable(
            name: "SlaBreaches");
    }
}
