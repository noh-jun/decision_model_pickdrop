using MessagePack;

namespace Zmq.Data;

// C++ struct BatteryStatus
// MSGPACK_DEFINE(address, voltage_v, current_a, soc_percent, battery_state_bits,
//                time_to_full_min, time_to_empty_min, temperature_c,
//                soh_percent, remaining_capacity_ah, remaining_energy_wh)
// => array format index 0..10
[MessagePackObject]
public sealed class BatteryStatus
{
    [Key(0)] public byte Address { get; set; } = 0;

    [Key(1)] public double VoltageV { get; set; } = 0.0;

    [Key(2)] public double CurrentA { get; set; } = 0.0;

    [Key(3)] public double SocPercent { get; set; } = 0.0;

    [Key(4)] public ushort BatteryStateBits { get; set; } = 0;

    [Key(5)] public double TimeToFullMin { get; set; } = 0.0;

    [Key(6)] public double TimeToEmptyMin { get; set; } = 0.0;

    [Key(7)] public double TemperatureC { get; set; } = 0.0;

    [Key(8)] public double SohPercent { get; set; } = 0.0;

    [Key(9)] public double RemainingCapacityAh { get; set; } = 0.0;

    [Key(10)] public double RemainingEnergyWh { get; set; } = 0.0;
}

[MessagePackObject]
public sealed class BatteryResult
{
    [Key(0)] public string DriverInstanceId { get; set; } = string.Empty;

    [Key(1)] public ulong SeqNo { get; set; }

    [Key(2)] public ulong PubTimestamp { get; set; }

    [Key(3)] public DriverState DriverState { get; set; } = DriverState.Ok;

    [Key(4)] public int RetCode { get; set; }

    [Key(5)] public string DriverErrMsg { get; set; } = string.Empty;

    [Key(6)] public BatteryStatus Status { get; set; } = new();
}