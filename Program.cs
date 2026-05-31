using RideManager.Actuators;
using RideManager.Camera;
using RideManager.Core;
using RideManager.Data;
using RideManager.Models;
using RideManager.Sensors;
using RideManager.Utils;

var options = ConfigLoader.Load("config.toml");
var runtimeSelector = new ModelRuntimeSelector(options.Models);
var cameraPipelines = CameraPipelineFactory.CreateThreeCameraPipelines(options.Cameras, runtimeSelector);

var sensorReaders = new ISensorReader[]
{
    new RadarBluetoothReader(options.Sensors.Radar),
    new GyroSensorReader(options.Sensors.Gyro)
};

var supervisor = new RideSupervisor(
    cameraPipelines,
    sensorReaders,
    new NoopBrakeController(options.Actuators.Brake),
    new NoopSpeakerNotifier(options.Actuators.Speaker),
    new SafetyDecisionEngine(),
    new PostgresDetectionEventWriter(options.Database));

await supervisor.RunOnceAsync(CancellationToken.None);
