using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoko.Plugin.Anilist.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CreateTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Anime",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false),
                    MalID = table.Column<int>(type: "INTEGER", nullable: true),
                    Season = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SeasonYear = table.Column<int>(type: "INTEGER", nullable: true),
                    IsLicensed = table.Column<bool>(type: "INTEGER", nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    MeanScore = table.Column<double>(type: "REAL", nullable: false),
                    CoverImagePath = table.Column<string>(type: "TEXT", nullable: false),
                    BannerImagePath = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Recommendations = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anime", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeID = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleID = table.Column<int>(type: "INTEGER", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    AiredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Portraits",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ImagePath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Portraits", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_AnimeID",
                table: "Episodes",
                column: "AnimeID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Anime");

            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropTable(
                name: "Portraits");
        }
    }
}
