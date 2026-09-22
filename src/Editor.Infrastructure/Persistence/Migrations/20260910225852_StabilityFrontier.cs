using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Editor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StabilityFrontier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValueSql, not just nullable: false. Both tables have rows
            // in any deployment that has ever been used, and a non-nullable
            // column with no default fails the ALTER outright — which the
            // migrator would report as a failed deployment rather than as a
            // silent problem, but only after someone tried it on a database
            // that was not empty. Every test database here is empty.
            migrationBuilder.AddColumn<Dictionary<Guid, long>>(
                name: "stability_frontier",
                table: "documents",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<Dictionary<Guid, long>>(
                name: "acknowledged",
                table: "document_replicas",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stability_frontier",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "acknowledged",
                table: "document_replicas");
        }
    }
}
