using System;

namespace DecisionModel.Config
{
    public class AppConfig
    {
        public AppSection App { get; set; } = new AppSection();
        public ZMQ Rfid { get; set; } = new ZMQ();
        public ZMQ Battery { get; set; } = new ZMQ();
        public ZMQ Loadcell { get; set; } = new ZMQ();
        public ZMQ DistanceHeight { get; set; } = new ZMQ();
        public ZMQ DistanceForward { get; set; } = new ZMQ();
        public ZMQ CamResult { get; set; } = new ZMQ();
        public ZMQ LidarResult { get; set; } = new ZMQ();
        public ZMQ OcrResult { get; set; } = new ZMQ();
        public ZMQ SlamResult { get; set; } = new ZMQ();
        public ZMQ VolumeResult { get; set; } = new ZMQ();
        public TCP TabletComm { get; set; } = new TCP();
    }
    
    public class AppSection
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public double ForkHeightThresholdmm { get; set; } = 0.0;
        public double TargetDistanceThresholdmm { get; set; } = 0.0;
    }

    public class ZMQ
    {
        public string Endpoint { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public int MaxQueueSize { get; set; } = 3;
    }

    public class TCP
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; } = 0;
        public int ByteBufferSize { get; set; } = 3;
    }
}
