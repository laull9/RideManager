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
        Assert.Equal("RideManager-Test", document.RootElement.GetProperty("payload").GetProperty("deviceName").GetString());
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
            return Task.FromResult(new AppSyncPage(Array.Empty<AppSyncDecisionRecord>(), null, false));
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
