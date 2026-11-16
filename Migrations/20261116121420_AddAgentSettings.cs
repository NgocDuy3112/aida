using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace aida.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentSettingsJson",
                table: "AspNetUsers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentSettingsJson",
                table: "AspNetUsers");
        }
    }
}
