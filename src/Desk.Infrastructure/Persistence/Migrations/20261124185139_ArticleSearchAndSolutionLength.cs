using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ArticleSearchAndSolutionLength : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Solution",
            table: "KnowledgeArticles",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(5000)",
            oldMaxLength: 5000);

        migrationBuilder.CreateIndex(
            name: "IX_KnowledgeArticles_TenantId_Status_PublishedAt",
            table: "KnowledgeArticles",
            columns: new[] { "TenantId", "Status", "PublishedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_KnowledgeArticles_TenantId_Status_PublishedAt",
            table: "KnowledgeArticles");

        migrationBuilder.AlterColumn<string>(
            name: "Solution",
            table: "KnowledgeArticles",
            type: "character varying(5000)",
            maxLength: 5000,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(8000)",
            oldMaxLength: 8000);
    }
}
