using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowStandaloneSensorSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_sensor_snapshots_safety_decisions_safety_decision_id",
                table: "sensor_snapshots");

            migrationBuilder.AlterColumn<Guid>(
                name: "safety_decision_id",
                table: "sensor_snapshots",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "f_k_sensor_snapshots_safety_decisions_safety_decision_id",
                table: "sensor_snapshots",
                column: "safety_decision_id",
                principalTable: "safety_decisions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_sensor_snapshots_safety_decisions_safety_decision_id",
                table: "sensor_snapshots");

            migrationBuilder.AlterColumn<Guid>(
                name: "safety_decision_id",
                table: "sensor_snapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "f_k_sensor_snapshots_safety_decisions_safety_decision_id",
                table: "sensor_snapshots",
                column: "safety_decision_id",
                principalTable: "safety_decisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
