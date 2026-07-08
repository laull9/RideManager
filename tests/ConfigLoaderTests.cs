using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void Load_ParsesRadarPythonFallbackOptions()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [sensors.radar]
            enabled = true
            transport = "bluetooth"
            address = ""
            device_name = "EVADAR-C6"
            service_uuid = "0000ad01-0000-1000-8000-00805f9b34fb"
            notify_uuid = "0000ad02-0000-1000-8000-00805f9b34fb"
            health_uuid = "0000ad04-0000-1000-8000-00805f9b34fb"
            match_by_service = true
            subscribe_health = true
            scan_timeout_seconds = 12.0
            services_timeout_seconds = 10.0
            reconnect_delay_seconds = 2.0
            python_fallback_enabled = true
            python_executable = "python3.12"
            python_script = "scripts/custom_radar.py"
            python_fallback_timeout_seconds = 5.5
            python_restart_delay_seconds = 1.25
            """);

        try
        {
            var config = ConfigLoader.Load(configPath);

            Assert.True(config.Sensors.Radar.PythonFallbackEnabled);
            Assert.Equal("python3.12", config.Sensors.Radar.PythonExecutable);
            Assert.Equal("scripts/custom_radar.py", config.Sensors.Radar.PythonScript);
            Assert.Equal(5.5, config.Sensors.Radar.PythonFallbackTimeoutSeconds, 6);
            Assert.Equal(1.25, config.Sensors.Radar.PythonRestartDelaySeconds, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void Load_DefaultsRadarPythonFallbackWhenLegacyTomlOmitsIt()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [sensors.radar]
            enabled = true
            transport = "bluetooth"
            device_name = "EVADAR-C6"
            service_uuid = "0000ad01-0000-1000-8000-00805f9b34fb"
            notify_uuid = "0000ad02-0000-1000-8000-00805f9b34fb"
            match_by_service = true
            """);

        try
        {
            var config = ConfigLoader.Load(configPath);

            Assert.True(config.Sensors.Radar.PythonFallbackEnabled);
            Assert.Equal("python3", config.Sensors.Radar.PythonExecutable);
            Assert.Equal("scripts/ble_radar_stream.py", config.Sensors.Radar.PythonScript);
            Assert.Equal(8.0, config.Sensors.Radar.PythonFallbackTimeoutSeconds, 6);
            Assert.Equal(2.0, config.Sensors.Radar.PythonRestartDelaySeconds, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void Load_ParsesGyroAndSpeakerOptions()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [sensors.gyro]
            enabled = true
            transport = "serial"
            address = "/dev/ttyUSB0"
            baud_rate = 230400
            read_timeout_seconds = 0.35

            [actuators.speaker]
            enabled = true
            asset_directory = "assests"
            warning_file = "slow_down.wav"
            danger_file = "brake_now.wav"
            player_command = "/usr/bin/aplay"
            min_interval_seconds = 1.5
            """);

        try
        {
            var config = ConfigLoader.Load(configPath);

            Assert.True(config.Sensors.Gyro.Enabled);
            Assert.Equal("/dev/ttyUSB0", config.Sensors.Gyro.Address);
            Assert.Equal(230400, config.Sensors.Gyro.BaudRate);
            Assert.Equal(0.35, config.Sensors.Gyro.ReadTimeoutSeconds, 6);
            Assert.True(config.Actuators.Speaker.Enabled);
            Assert.Equal("assests", config.Actuators.Speaker.AssetDirectory);
            Assert.Equal("slow_down.wav", config.Actuators.Speaker.WarningFile);
            Assert.Equal("brake_now.wav", config.Actuators.Speaker.DangerFile);
            Assert.Equal("/usr/bin/aplay", config.Actuators.Speaker.PlayerCommand);
            Assert.Equal(1.5, config.Actuators.Speaker.MinIntervalSeconds, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
