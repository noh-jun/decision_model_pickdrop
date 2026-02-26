using System;
using MessagePack;

namespace Zmq.Data;

// C++: struct LoadcellStatus
// MSGPACK_DEFINE(
//   gross_weight, right_weight, left_weight,
//   right_battery_percent, right_charge_status, right_online_status,
//   left_battery_percent, left_charge_status, left_online_status,
//   gross_net_mark, overload_mark, out_of_tolerance_mark)
[MessagePackObject]
public sealed class LoadcellStatus
{
    [Key(0)] public double GrossWeight { get; set; } = 0.0;

    [Key(1)] public double RightWeight { get; set; } = 0.0;

    [Key(2)] public double LeftWeight { get; set; } = 0.0;

    [Key(3)] public byte RightBatteryPercent { get; set; } = 0;

    // 0: uncharged, 1: charging
    [Key(4)] public byte RightChargeStatus { get; set; } = 0;

    // 0: online, 1: offline, 2: hw failure
    [Key(5)] public byte RightOnlineStatus { get; set; } = 0;

    [Key(6)] public byte LeftBatteryPercent { get; set; } = 0;

    // 0: uncharged, 1: charging
    [Key(7)] public byte LeftChargeStatus { get; set; } = 0;

    // 0: online, 1: offline, 2: hw failure
    [Key(8)] public byte LeftOnlineStatus { get; set; } = 0;

    // 0: gross, 1: net
    [Key(9)] public byte GrossNetMark { get; set; } = 0;

    // 0: not overloaded, 1: overload
    [Key(10)] public byte OverloadMark { get; set; } = 0;

    // 0: ok, 1: left, 2: right
    [Key(11)] public byte OutOfToleranceMark { get; set; } = 0;
}

[MessagePackObject]
public sealed class LoadcellResult
{
    [Key(0)] public string DriverInstanceId { get; set; } = string.Empty;

    [Key(1)] public ulong SeqNo { get; set; }

    [Key(2)] public ulong PubTimestamp { get; set; }

    [Key(3)] public DriverState DriverState { get; set; } = DriverState.Ok;

    [Key(4)] public int RetCode { get; set; }

    [Key(5)] public string DriverErrMsg { get; set; } = string.Empty;

    [Key(6)] public LoadcellStatus Status { get; set; } = new LoadcellStatus();
}