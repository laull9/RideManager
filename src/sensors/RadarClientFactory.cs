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
        if (forceSimulated
            || options.Transport.Equals("simulate", StringComparison.OrdinalIgnoreCase)
            || options.Address.Equals("simulate", StringComparison.OrdinalIgnoreCase))
        {
            return new SimulatedRadarClient(options);
        }

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
}
