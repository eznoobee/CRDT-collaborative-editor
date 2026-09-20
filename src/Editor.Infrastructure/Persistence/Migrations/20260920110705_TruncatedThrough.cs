using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Editor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TruncatedThrough : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "truncated_through",
                table: "documents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "truncated_through",
                table: "documents");
        }
    }
}
