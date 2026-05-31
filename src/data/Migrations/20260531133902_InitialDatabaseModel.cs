using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialDatabaseModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    transport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    backend = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    input_width = table.Column<int>(type: "integer", nullable: true),
                    input_height = table.Column<int>(type: "integer", nullable: true),
                    labels_json = table.Column<string>(type: "jsonb", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_model_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "run_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    host_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_run_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_system_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "safety_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risk_level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_safety_decisions", x => x.id);
                    table.ForeignKey(
                        name: "f_k_safety_decisions_run_sessions_run_session_id",
                        column: x => x.run_session_id,
                        principalTable: "run_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "actuator_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    safety_decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actuator_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    command_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_actuator_commands", x => x.id);
                    table.ForeignKey(
                        name: "f_k_actuator_commands_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "f_k_actuator_commands_safety_decisions_safety_decision_id",
                        column: x => x.safety_decision_id,
                        principalTable: "safety_decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "camera_frames",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    safety_decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    camera_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    capture_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    preprocess_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    inference_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    total_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    fps = table.Column<double>(type: "double precision", nullable: true),
                    dropped_frames = table.Column<long>(type: "bigint", nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_camera_frames", x => x.id);
                    table.ForeignKey(
                        name: "f_k_camera_frames_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "f_k_camera_frames_safety_decisions_safety_decision_id",
                        column: x => x.safety_decision_id,
                        principalTable: "safety_decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sensor_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    safety_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sensor_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    values_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_sensor_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "f_k_sensor_snapshots_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "f_k_sensor_snapshots_safety_decisions_safety_decision_id",
                        column: x => x.safety_decision_id,
                        principalTable: "safety_decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camera_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    safety_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    camera_frame_id = table.Column<Guid>(type: "uuid", nullable: true),
                    camera_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    box_x = table.Column<double>(type: "double precision", nullable: true),
                    box_y = table.Column<double>(type: "double precision", nullable: true),
                    box_width = table.Column<double>(type: "double precision", nullable: true),
                    box_height = table.Column<double>(type: "double precision", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_camera_findings", x => x.id);
                    table.ForeignKey(
                        name: "f_k_camera_findings_camera_frames_camera_frame_id",
                        column: x => x.camera_frame_id,
                        principalTable: "camera_frames",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "f_k_camera_findings_safety_decisions_safety_decision_id",
                        column: x => x.safety_decision_id,
                        principalTable: "safety_decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sensor_readings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sensor_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_sensor_readings", x => x.id);
                    table.ForeignKey(
                        name: "f_k_sensor_readings_sensor_snapshots_sensor_snapshot_id",
                        column: x => x.sensor_snapshot_id,
                        principalTable: "sensor_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_actuator_commands_actuator_name_requested_at",
                table: "actuator_commands",
                columns: new[] { "actuator_name", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_actuator_commands_device_id",
                table: "actuator_commands",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "i_x_actuator_commands_safety_decision_id",
                table: "actuator_commands",
                column: "safety_decision_id");

            migrationBuilder.CreateIndex(
                name: "i_x_actuator_commands_status",
                table: "actuator_commands",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "i_x_camera_findings_camera_frame_id",
                table: "camera_findings",
                column: "camera_frame_id");

            migrationBuilder.CreateIndex(
                name: "i_x_camera_findings_camera_id_observed_at",
                table: "camera_findings",
                columns: new[] { "camera_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_camera_findings_label",
                table: "camera_findings",
                column: "label");

            migrationBuilder.CreateIndex(
                name: "i_x_camera_findings_safety_decision_id",
                table: "camera_findings",
                column: "safety_decision_id");

            migrationBuilder.CreateIndex(
                name: "i_x_camera_frames_camera_id_captured_at",
                table: "camera_frames",
                columns: new[] { "camera_id", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_camera_frames_device_id",
                table: "camera_frames",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "i_x_camera_frames_safety_decision_id",
                table: "camera_frames",
                column: "safety_decision_id");

            migrationBuilder.CreateIndex(
                name: "i_x_devices_code",
                table: "devices",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_model_artifacts_name_backend_version",
                table: "model_artifacts",
                columns: new[] { "name", "backend", "version" });

            migrationBuilder.CreateIndex(
                name: "i_x_run_sessions_started_at",
                table: "run_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "i_x_safety_decisions_decided_at",
                table: "safety_decisions",
                column: "decided_at");

            migrationBuilder.CreateIndex(
                name: "i_x_safety_decisions_risk_level",
                table: "safety_decisions",
                column: "risk_level");

            migrationBuilder.CreateIndex(
                name: "i_x_safety_decisions_run_session_id",
                table: "safety_decisions",
                column: "run_session_id");

            migrationBuilder.CreateIndex(
                name: "i_x_sensor_readings_metric",
                table: "sensor_readings",
                column: "metric");

            migrationBuilder.CreateIndex(
                name: "i_x_sensor_readings_sensor_snapshot_id",
                table: "sensor_readings",
                column: "sensor_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "i_x_sensor_snapshots_device_id",
                table: "sensor_snapshots",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "i_x_sensor_snapshots_safety_decision_id",
                table: "sensor_snapshots",
                column: "safety_decision_id");

            migrationBuilder.CreateIndex(
                name: "i_x_sensor_snapshots_sensor_name_observed_at",
                table: "sensor_snapshots",
                columns: new[] { "sensor_name", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_system_events_level",
                table: "system_events",
                column: "level");

            migrationBuilder.CreateIndex(
                name: "i_x_system_events_source_occurred_at",
                table: "system_events",
                columns: new[] { "source", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actuator_commands");

            migrationBuilder.DropTable(
                name: "camera_findings");

            migrationBuilder.DropTable(
                name: "model_artifacts");

            migrationBuilder.DropTable(
                name: "sensor_readings");

            migrationBuilder.DropTable(
                name: "system_events");

            migrationBuilder.DropTable(
                name: "camera_frames");

            migrationBuilder.DropTable(
                name: "sensor_snapshots");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "safety_decisions");

            migrationBuilder.DropTable(
                name: "run_sessions");
        }
    }
}
