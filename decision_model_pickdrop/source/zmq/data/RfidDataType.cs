using System;
using System.Collections.Generic;
using MessagePack;

namespace Zmq.Data;

// C++ struct tagInfo { epc, rssi, readCount, antenna, timeStamp }
// MSGPACK_DEFINE(epc, rssi, readCount, antenna, timeStamp)
// => array format: [0]=epc, [1]=rssi, [2]=readCount, [3]=antenna, [4]=timeStamp
[MessagePackObject]
public sealed class TagInfo
{
    [Key(0)] public string Epc { get; set; } = string.Empty;

    [Key(1)] public int Rssi { get; set; } = 0;

    [Key(2)] public uint ReadCount { get; set; } = 0;

    [Key(3)] public int Antenna { get; set; } = 0;

    [Key(4)] public ulong TimeStamp { get; set; } = 0;
}

// C++ struct DataResult {
//   driver_instance_id, seq_no, pub_timestamp, driver_state, ret_code, driver_err_msg, tags
// }
// MSGPACK_DEFINE(driver_instance_id, seq_no, pub_timestamp, driver_state, ret_code, driver_err_msg, tags)
// => array format indexes 0..6
[MessagePackObject]
public sealed class RfidResult
{
    [Key(0)] public string DriverInstanceId { get; set; } = string.Empty;

    [Key(1)] public ulong SeqNo { get; set; }

    [Key(2)] public ulong PubTimestamp { get; set; }

    // C++에서는 uint8_t driver_state로 넣고 있음.
    // C#에서 byte로 받아도 되고, DriverState enum으로 받아도 됨.
    // enum으로 받으면 가독성이 좋아짐.
    [Key(3)] public DriverState DriverState { get; set; }

    [Key(4)] public int RetCode { get; set; }

    [Key(5)] public string DriverErrMsg { get; set; } = string.Empty;

    [Key(6)] public List<TagInfo> Tags { get; set; } = new List<TagInfo>();
}