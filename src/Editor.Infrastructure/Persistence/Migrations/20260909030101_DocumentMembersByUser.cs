using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Editor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentMembersByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_document_members_user_id",
                table: "document_members",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_members_user_id",
                table: "document_members");
        }
    }
}
