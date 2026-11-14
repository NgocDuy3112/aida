using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace aida.Migrations
{
    public partial class AddGoogleRefreshToken : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleRefreshToken",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleRefreshTokenObtainedUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoogleRefreshToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "GoogleRefreshTokenObtainedUtc",
                table: "AspNetUsers");
        }
    }
}
