using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboEvents.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "events_hosted",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        room_id = table.Column<int>(type: "int", nullable: false),
                        room_name = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        host_id = table.Column<int>(type: "int", nullable: false),
                        host_name = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        title = table
                            .Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        started_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        ended_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        end_reason = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        ended_by = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        called = table.Column<int>(type: "int", nullable: false),
                        peak_attendees = table.Column<int>(type: "int", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_events_hosted", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "events_prize_sets",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        name = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        currencies = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        badge_code = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        furni_name = table
                            .Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        furni_count = table.Column<int>(type: "int", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_events_prize_sets", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "events_winners",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        event_id = table.Column<long>(type: "bigint", nullable: true),
                        room_id = table.Column<int>(type: "int", nullable: false),
                        player_id = table.Column<int>(type: "int", nullable: false),
                        player_name = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        awarded_by_id = table.Column<int>(type: "int", nullable: false),
                        awarded_by = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        prize_name = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        currencies = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        badge_code = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        furni_name = table
                            .Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        furni_count = table.Column<int>(type: "int", nullable: false),
                        operation_id = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        status = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        currencies_paid = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        badge_given = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        badge_done = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        furni_started = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        furni_given = table.Column<int>(type: "int", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        paid_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        error = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_events_winners", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_events_hosted_host_id_started_at",
                table: "events_hosted",
                columns: new[] { "host_id", "started_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_hosted_room_id_ended_at",
                table: "events_hosted",
                columns: new[] { "room_id", "ended_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_hosted_started_at",
                table: "events_hosted",
                column: "started_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_prize_sets_name",
                table: "events_prize_sets",
                column: "name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_winners_created_at",
                table: "events_winners",
                column: "created_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_winners_event_id",
                table: "events_winners",
                column: "event_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_winners_player_id_created_at",
                table: "events_winners",
                columns: new[] { "player_id", "created_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_events_winners_status",
                table: "events_winners",
                column: "status"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "events_hosted");

            migrationBuilder.DropTable(name: "events_prize_sets");

            migrationBuilder.DropTable(name: "events_winners");
        }
    }
}
