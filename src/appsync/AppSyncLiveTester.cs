using Microsoft.EntityFrameworkCore;
using RideManager.Data;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 表示 App 同步蓝牙上位机 live test 配置。
/// </summary>
public sealed record AppSyncLiveTestOptions(TimeSpan? Duration, bool RequireDatabase);

/// <summary>
/// 只启动数据库访问和 App 蓝牙上位机，供手机端联调。
/// </summary>
public sealed class AppSyncLiveTester
{
    private readonly AppSyncOptions _appSyncOptions;
    private readonly DatabaseOptions _databaseOptions;

    /// <summary>
    /// 创建 App 同步 live test。
    /// </summary>
    public AppSyncLiveTester(AppSyncOptions appSyncOptions, DatabaseOptions databaseOptions)
    {
        _appSyncOptions = appSyncOptions;
        _databaseOptions = databaseOptions;
    }

    /// <summary>
    /// 启动数据库检查和 App 蓝牙同步服务。
    /// </summary>
    public async Task RunAsync(AppSyncLiveTestOptions options, CancellationToken cancellationToken)
    {
        await CheckDatabaseAsync(options.RequireDatabase, cancellationToken).ConfigureAwait(false);

        await using var server = new AppSyncServer(_appSyncOptions, _databaseOptions);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        using var runCancellation = options.Duration is { } value
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Duration is { } duration)
        {
            runCancellation.CancelAfter(duration);
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            Console.WriteLine("App sync live test is running. Press Ctrl+C to stop.");
            await Task.Delay(Timeout.InfiniteTimeSpan, runCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// 检查数据库连接，避免手机端连上后才发现数据源不可用。
    /// </summary>
    private async Task CheckDatabaseAsync(bool requireDatabase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_databaseOptions.ConnectionString))
        {
            var message = "App sync live test database connection string is empty.";
            if (requireDatabase)
            {
                throw new InvalidOperationException(message);
            }

            Console.WriteLine($"{message} Sync requests will return empty data.");
            return;
        }

        try
        {
            await using var dbContext = RideManagerDbContext.Create(_databaseOptions);
            if (await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.WriteLine("App sync live test database connection ok.");
                return;
            }

            if (requireDatabase)
            {
                throw new InvalidOperationException("App sync live test database cannot connect.");
            }

            Console.WriteLine("App sync live test database cannot connect. Bluetooth service will still start.");
        }
        catch (Exception ex) when (!requireDatabase && ex is InvalidOperationException or Npgsql.NpgsqlException)
        {
            Console.WriteLine($"App sync live test database check failed: {ex.Message}. Bluetooth service will still start.");
        }
    }
}
