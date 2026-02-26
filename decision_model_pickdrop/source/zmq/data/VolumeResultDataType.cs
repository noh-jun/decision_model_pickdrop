using MessagePack;

namespace Zmq.Data;

[MessagePackObject]
public class VolumeResult
{
    [Key(0)] public int Width { get; set; } = 0;
    [Key(1)] public int Height { get; set; } = 0;
    [Key(2)] public int Depth { get; set; } = 0;
    [Key(3)] public int Distance { get; set; } = 0;
}