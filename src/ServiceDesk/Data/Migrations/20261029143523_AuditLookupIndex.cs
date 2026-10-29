using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Entity_EntityId",
                table: "AuditEvents",
                columns: new[] { "Entity", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Entity_EntityId",
                table: "AuditEvents");
        }
    }
}
