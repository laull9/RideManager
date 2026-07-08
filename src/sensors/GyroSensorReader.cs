using System.Diagnostics;
using System.Globalization;
using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// Reads a Linux text-line six-axis gyro sensor over a serial-like device.
/// </summary>
public sealed class GyroSensorReader : ISensorReader, IAsyncDisposable
{
    private readonly SensorEndpointOptions _options;
    private StreamReader? _reader;
    private FileStream? _stream;
    private bool _serialConfigured;
    private bool _reportedDisabledReason;

    /// <summary>
    /// 创建陀螺仪读取器。
    /// </summary>
    public GyroSensorReader(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Reads one gyro sample. Non-Linux hosts intentionally skip this module.
    /// </summary>
    public async Task<SensorSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        if (!OperatingSystem.IsLinux())
        {
            ReportDisabledOnce("GYRO is enabled but only runs on Linux; skipping this sensor on current OS.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.Address))
        {
            ReportDisabledOnce("GYRO is enabled but sensors.gyro.address is empty.");
            return null;
        }

        try
        {
            var reader = await EnsureReaderAsync(cancellationToken).ConfigureAwait(false);
            var line = await ReadLineWithTimeoutAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null || !TryParseSample(line, DateTimeOffset.UtcNow, out var snapshot))
            {
                return null;
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportDisabledOnce($"GYRO read failed: {ex.Message}");
            await ResetReaderAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Releases the currently opened sensor stream.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await ResetReaderAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Parses one text protocol sample into a sensor snapshot.
    /// </summary>
    internal static bool TryParseSample(string line, DateTimeOffset observedAt, out SensorSnapshot snapshot)
    {
        snapshot = new SensorSnapshot("GYRO", observedAt, new Dictionary<string, double>());
        var values = line.Contains('=')
            ? ParseKeyValueSample(line)
            : ParseCsvSample(line);

        if (values.Count == 0)
        {
            return false;
        }

        snapshot = new SensorSnapshot("GYRO", observedAt, values);
        return true;
    }

    /// <summary>
    /// Opens the sensor stream and configures serial mode when needed.
    /// </summary>
    private async Task<StreamReader> EnsureReaderAsync(CancellationToken cancellationToken)
    {
        if (_reader is not null)
        {
            return _reader;
        }

        if (!File.Exists(_options.Address))
        {
            throw new IOException($"GYRO device not found: {_options.Address}");
        }

        if (IsSerialTransport(_options.Transport) && !_serialConfigured)
        {
            await ConfigureSerialAsync(cancellationToken).ConfigureAwait(false);
            _serialConfigured = true;
        }

        _stream = new FileStream(
            _options.Address,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 256,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _reader = new StreamReader(_stream);
        return _reader;
    }

    /// <summary>
    /// Uses stty to put the Linux serial device into raw line mode.
    /// </summary>
    private async Task ConfigureSerialAsync(CancellationToken cancellationToken)
    {
        var sttyPath = FindExecutable("stty");
        if (sttyPath is null)
        {
            throw new InvalidOperationException("stty was not found; cannot configure GYRO serial port.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sttyPath,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(_options.Address);
        startInfo.ArgumentList.Add(_options.BaudRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("raw");
        startInfo.ArgumentList.Add("-echo");
        startInfo.ArgumentList.Add("min");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("time");
        startInfo.ArgumentList.Add("2");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start stty for GYRO serial port.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"stty failed for GYRO serial port: {error.Trim()}");
        }
    }

    /// <summary>
    /// Reads one sample line without letting the supervisor loop block forever.
    /// </summary>
    private async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(_options.ReadTimeoutSeconds, 0.01)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses comma-separated roll/pitch/yaw/acceleration samples.
    /// </summary>
    private static IReadOnlyDictionary<string, double> ParseCsvSample(string line)
    {
        var parts = line
            .Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (parts.Length < 6)
        {
            return new Dictionary<string, double>();
        }

        var keys = new[] { "roll", "pitch", "yaw", "accel_x", "accel_y", "accel_z" };
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < keys.Length; index++)
        {
            if (!TryParseDouble(parts[index], out var value))
            {
                return new Dictionary<string, double>();
            }

            values[keys[index]] = value;
        }

        return values;
    }

    /// <summary>
    /// Parses key=value samples with common gyro and accelerometer aliases.
    /// </summary>
    private static IReadOnlyDictionary<string, double> ParseKeyValueSample(string line)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1)
            {
                continue;
            }

            var key = NormalizeKey(token[..separator]);
            if (key is null || !TryParseDouble(token[(separator + 1)..], out var value))
            {
                continue;
            }

            values[key] = value;
        }

        return values.Count >= 6 ? values : new Dictionary<string, double>();
    }

    /// <summary>
    /// Normalizes protocol field names to database metric names.
    /// </summary>
    private static string? NormalizeKey(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "roll" or "gx" or "gyro_x" => "roll",
            "pitch" or "gy" or "gyro_y" => "pitch",
            "yaw" or "gz" or "gyro_z" => "yaw",
            "ax" or "accx" or "accel_x" or "acceleration_x" => "accel_x",
            "ay" or "accy" or "accel_y" or "acceleration_y" => "accel_y",
            "az" or "accz" or "accel_z" or "acceleration_z" => "accel_z",
            _ => null
        };
    }

    /// <summary>
    /// Parses doubles using invariant culture.
    /// </summary>
    private static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Determines whether this endpoint should be configured as a serial port.
    /// </summary>
    private static bool IsSerialTransport(string transport)
    {
        return string.IsNullOrWhiteSpace(transport)
            || transport.Equals("serial", StringComparison.OrdinalIgnoreCase)
            || transport.Equals("uart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds an executable on PATH.
    /// </summary>
    private static string? FindExecutable(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports a sensor disable/read problem once to avoid flooding logs.
    /// </summary>
    private void ReportDisabledOnce(string message)
    {
        if (_reportedDisabledReason)
        {
            return;
        }

        _reportedDisabledReason = true;
        Console.WriteLine(message);
    }

    /// <summary>
    /// Closes the current reader so the next cycle can reconnect.
    /// </summary>
    private async ValueTask ResetReaderAsync()
    {
        _reader?.Dispose();
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _reader = null;
        _stream = null;
    }
}
