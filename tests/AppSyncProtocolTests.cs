using System.Text;
using System.Text.Json;
using RideManager.AppSync;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class AppSyncProtocolTests
{
    [Fact]
    public async Task HandleAsync_Hello_ReturnsCapabilities()
    {
        var handler = new AppSyncProtocolHandler(CreateOptions(), new FakeRepository());

        var response = await handler.HandleAsync(
            "{\"v\":1,\"id\":\"hello-1\",\"type\":\"hello\",\"payload\":{}}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("hello-1", document.RootElement.GetProperty("id").GetString());
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("RideManager-Test", payload.GetProperty("deviceName").GetString());
        Assert.Contains(payload.GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "gyro_sensor");
    }

    [Fact]
    public async Task HandleAsync_SyncRecent_UsesDefaultWindowAndClampsLimit()
    {
        var repository = new FakeRepository();
        var handler = new AppSyncProtocolHandler(CreateOptions(), repository);

        var response = await handler.HandleAsync(
            "{\"v\":1,\"id\":\"sync-1\",\"type\":\"sync_recent\",\"payload\":{\"limit\":999}}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, repository.LastRecentLimit);
        Assert.NotNull(repository.LastRecentSince);
        Assert.InRange(DateTimeOffset.UtcNow - repository.LastRecentSince!.Value, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
    }

    [Fact]
    public async Task HandleAsync_LoadMore_RejectsInvalidCursor()
    {
        var handler = new AppSyncProtocolHandler(CreateOptions(), new FakeRepository());

        var response = await handler.HandleAsync(
            "{\"v\":1,\"id\":\"more-1\",\"type\":\"load_more\",\"payload\":{\"cursor\":\"bad\"}}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HandleAsync_UpdateSettings_RecordsPatch()
    {
        var repository = new FakeRepository();
        var handler = new AppSyncProtocolHandler(CreateOptions(), repository);

        var response = await handler.HandleAsync(
            "{\"v\":1,\"id\":\"settings-1\",\"type\":\"update_settings\",\"payload\":{\"client_id\":\"phone-a\",\"patch\":{\"cameras\":{\"CAM_BACK\":{\"enabled\":true}}}}}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("phone-a", repository.LastClientId);
        Assert.Equal(JsonValueKind.Object, repository.LastPatch.ValueKind);
    }

    [Fact]
    public async Task HandleAsync_SyncRecent_IncludesSensorSnapshotValues()
    {
        var repository = new FakeRepository
        {
            RecentPage = new AppSyncPage(
                new[]
                {
                    new AppSyncDecisionRecord(
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        "Normal",
                        JsonDocument.Parse("{}").RootElement.Clone(),
                        Array.Empty<AppSyncCameraFindingRecord>(),
                        new[]
                        {
                            new AppSyncSensorSnapshotRecord(
                                Guid.NewGuid(),
                                "GYRO",
                                DateTimeOffset.UtcNow,
                                JsonDocument.Parse("{\"roll\":1.2,\"pitch\":-0.4,\"yaw\":3.8,\"accel_x\":0.01,\"accel_y\":0.02,\"accel_z\":9.81}").RootElement.Clone(),
                                new[]
                                {
                                    new AppSyncSensorReadingRecord(Guid.NewGuid(), "roll", 1.2, "deg"),
                                    new AppSyncSensorReadingRecord(Guid.NewGuid(), "accel_z", 9.81, "m/s2")
                                })
                        })
                },
                null,
                false)
        };
        var handler = new AppSyncProtocolHandler(CreateOptions(), repository);

        var response = await handler.HandleAsync(
            "{\"v\":1,\"id\":\"sync-values\",\"type\":\"sync_recent\",\"payload\":{}}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        var values = document.RootElement
            .GetProperty("payload")
            .GetProperty("items")[0]
            .GetProperty("sensorSnapshots")[0]
            .GetProperty("values");
        var readings = document.RootElement
            .GetProperty("payload")
            .GetProperty("items")[0]
            .GetProperty("sensorSnapshots")[0]
            .GetProperty("readings");
        Assert.Equal(1.2, values.GetProperty("roll").GetDouble());
        Assert.Equal(9.81, values.GetProperty("accel_z").GetDouble());
        Assert.Contains(readings.EnumerateArray(), reading => reading.GetProperty("metric").GetString() == "roll");
        Assert.Contains(readings.EnumerateArray(), reading => reading.GetProperty("metric").GetString() == "accel_z");
    }

    [Fact]
    public void PostgresAppSyncRepository_MapsGyroSensorReadings()
    {
        var entity = new Data.SensorSnapshotEntity
        {
            Id = Guid.NewGuid(),
            SensorName = "GYRO",
            ObservedAt = DateTimeOffset.Parse("2026-07-08T10:00:00Z"),
            ValuesJson = "{\"roll\":1.2,\"pitch\":-0.4,\"yaw\":3.8,\"accel_z\":9.81}"
        };
        entity.Readings.Add(new Data.SensorReadingEntity { Id = Guid.NewGuid(), Metric = "yaw", Value = 3.8, Unit = "deg" });
        entity.Readings.Add(new Data.SensorReadingEntity { Id = Guid.NewGuid(), Metric = "accel_z", Value = 9.81, Unit = "m/s2" });

        var record = PostgresAppSyncRepository.MapSensorSnapshot(entity);

        Assert.Equal("GYRO", record.SensorName);
        Assert.Equal(3.8, record.Values.GetProperty("yaw").GetDouble());
        Assert.Equal(2, record.Readings.Count);
        Assert.Equal("accel_z", record.Readings[0].Metric);
        Assert.Equal("yaw", record.Readings[1].Metric);
    }

    [Fact]
    public void PostgresAppSyncRepository_MapsStandaloneSensorSnapshotAsSensorOnlyRecord()
    {
        var snapshotId = Guid.NewGuid();
        var entity = new Data.SensorSnapshotEntity
        {
            Id = snapshotId,
            SensorName = "GYRO",
            ObservedAt = DateTimeOffset.Parse("2026-07-08T10:00:00Z"),
            ValuesJson = "{\"roll\":1.2,\"pitch\":-0.4,\"yaw\":3.8}"
        };
        entity.Readings.Add(new Data.SensorReadingEntity { Id = Guid.NewGuid(), Metric = "roll", Value = 1.2, Unit = "degree" });

        var record = PostgresAppSyncRepository.MapStandaloneSensorSnapshot(entity);

        Assert.Equal(snapshotId, record.Id);
        Assert.Equal("SensorOnly", record.RiskLevel);
        Assert.Equal("sensor_snapshot", record.Payload.GetProperty("recordType").GetString());
        Assert.Equal("GYRO", record.Payload.GetProperty("sensorName").GetString());
        Assert.Single(record.SensorSnapshots);
        Assert.Equal("GYRO", record.SensorSnapshots[0].SensorName);
        Assert.Equal("roll", record.SensorSnapshots[0].Readings[0].Metric);
    }

    [Fact]
    public void NotificationFramer_RoundTripsLongResponse()
    {
        var response = "{\"v\":1,\"id\":\"sync\",\"type\":\"sync_recent\",\"status\":\"ok\",\"payload\":{\"items\":[{\"sensorSnapshots\":[{\"values\":{\"heart_rate\":72,\"speed_kmh\":18.5,\"cadence_rpm\":86}}]}]}}";

        var chunks = AppSyncNotificationFramer.CreateChunks(response, 96);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 96));
        var ordered = chunks
            .Select(chunk => JsonDocument.Parse(chunk).RootElement.Clone())
            .OrderBy(chunk => chunk.GetProperty("i").GetInt32())
            .ToArray();
        var reassembled = ordered
            .Select(chunk => Convert.FromBase64String(chunk.GetProperty("d").GetString()!))
            .SelectMany(value => value)
            .ToArray();
        Assert.Equal(response, Encoding.UTF8.GetString(reassembled));
        Assert.All(ordered, chunk => Assert.Equal(ordered.Length, chunk.GetProperty("n").GetInt32()));
        Assert.All(ordered, chunk => Assert.Equal(Encoding.UTF8.GetByteCount(response), chunk.GetProperty("b").GetInt32()));
    }

    [Fact]
    public void Cursor_RoundTrips()
    {
        var expected = new AppSyncCursor(DateTimeOffset.Parse("2026-06-10T10:20:30Z"), Guid.NewGuid());

        Assert.True(AppSyncCursor.TryDecode(expected.Encode(), out var actual));

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DecidedAt, actual.DecidedAt);
    }

    private static AppSyncOptions CreateOptions()
    {
        return new AppSyncOptions(
            true,
            "RideManager-Test",
            "7f7d0001-4f52-4d32-9b2a-0f0b5a8b1000",
            "7f7d0002-4f52-4d32-9b2a-0f0b5a8b1000",
            "7f7d0003-4f52-4d32-9b2a-0f0b5a8b1000",
            2,
            24.0,
            180,
            16384);
    }

    private sealed class FakeRepository : IAppSyncRepository
    {
        public AppSyncPage? RecentPage { get; set; }

        public int LastRecentLimit { get; private set; }

        public DateTimeOffset? LastRecentSince { get; private set; }

        public string? LastClientId { get; private set; }

        public JsonElement LastPatch { get; private set; }

        public Task<AppSyncPage> GetRecentDecisionsAsync(
            DateTimeOffset since,
            int limit,
            AppSyncCursor? cursor,
            CancellationToken cancellationToken)
        {
            LastRecentSince = since;
            LastRecentLimit = limit;
            return Task.FromResult(RecentPage ?? new AppSyncPage(Array.Empty<AppSyncDecisionRecord>(), null, false));
        }

        public Task<AppSyncPage> GetMoreDecisionsAsync(
            AppSyncCursor cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AppSyncPage(Array.Empty<AppSyncDecisionRecord>(), null, false));
        }

        public Task<AppSyncSettingsUpdateResult> RecordSettingsUpdateAsync(
            JsonElement patch,
            string? clientId,
            CancellationToken cancellationToken)
        {
            LastPatch = patch.Clone();
            LastClientId = clientId;
            return Task.FromResult(new AppSyncSettingsUpdateResult(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                true,
                "accepted"));
        }
    }
}
