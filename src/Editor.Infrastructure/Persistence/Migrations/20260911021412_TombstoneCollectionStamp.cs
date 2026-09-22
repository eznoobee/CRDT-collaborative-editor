using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Editor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TombstoneCollectionStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_collected_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_last_collected_at",
                table: "documents",
                column: "last_collected_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_last_collected_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "last_collected_at",
                table: "documents");
        }
    }
}
