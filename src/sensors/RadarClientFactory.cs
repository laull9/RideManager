using RideManager.Utils;
using System.Runtime.InteropServices;

namespace RideManager.Sensors;

/// <summary>
/// 根据传感器配置创建雷达客户端。
/// </summary>
public static class RadarClientFactory
{
    /// <summary>
    /// 创建 BLE 或模拟雷达客户端。
    /// </summary>
    public static IRadarClient Create(SensorEndpointOptions options, bool forceSimulated = false)
    {
        return Create(options, forceSimulated, UsePythonAsPrimaryOnLinux());
    }

    internal static IRadarClient Create(SensorEndpointOptions options, bool forceSimulated, bool usePythonAsPrimary)
    {
        if (forceSimulated
            || options.Transport.Equals("simulate", StringComparison.OrdinalIgnoreCase)
            || options.Address.Equals("simulate", StringComparison.OrdinalIgnoreCase))
        {
            return new SimulatedRadarClient(options);
        }

        if (options.Transport.Equals("python", StringComparison.OrdinalIgnoreCase)
            || options.Transport.Equals("ble-python", StringComparison.OrdinalIgnoreCase))
        {
            return new PythonRadarClient(options);
        }

        if (IsNativeBleTransport(options.Transport) && usePythonAsPrimary)
        {
            return new PythonRadarClient(options);
        }

        if (options.PythonFallbackEnabled && IsNativeBleTransport(options.Transport))
        {
            return new FallbackRadarClient(options, () => CreateNative(options));
        }

        return CreateNative(options);
    }

    private static IRadarClient CreateNative(SensorEndpointOptions options)
    {
        if (options.Transport.Equals("bluez", StringComparison.OrdinalIgnoreCase))
        {
            return new RadarBluetoothClient(options);
        }

        if (options.Transport.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)
            || options.Transport.Equals("ble", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new MacOSCoreBluetoothRadarClient(options)
                : new RadarBluetoothClient(options);
        }

        throw new NotSupportedException($"Unsupported radar transport: {options.Transport}");
    }

    private static bool IsNativeBleTransport(string transport)
    {
        return transport.Equals("bluez", StringComparison.OrdinalIgnoreCase)
            || transport.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)
            || transport.Equals("ble", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool UsePythonAsPrimaryOnLinux()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }
}
