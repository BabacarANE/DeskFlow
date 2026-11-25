using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class UnaccentExtension : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Accent-insensitive article search (ADR 010). A contrib extension marked "trusted": the database owner may create it.
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP EXTENSION IF EXISTS unaccent;");
    }
}
