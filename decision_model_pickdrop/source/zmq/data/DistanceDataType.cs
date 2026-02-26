using MessagePack;

namespace Zmq.Data;

[MessagePackObject]
public class DistanceResult
{
    [Key(0)] public string DriverInstanceId { get; set; } = string.Empty;

    [Key(1)] public ulong SeqNo { get; set; }

    [Key(2)] public ulong PubTimestamp { get; set; }

    [Key(3)] public byte DriverState { get; set; }

    [Key(4)] public int RetCode { get; set; }

    [Key(5)] public string DriverErrMsg { get; set; } = string.Empty;

    [Key(6)] public int RawMm { get; set; }

    [Key(7)] public int CalibratedMm { get; set; }
}
