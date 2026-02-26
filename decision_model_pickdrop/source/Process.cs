using DecisionModel.Config;
using LogLib.log;
using LogLib.log.model;
using LogLib.log.strategy;
using LogLib.log.strategy.factory;
using Zmq.Data;
using Zmq.Subscribe;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Ethernet.Subscribe;
using Ethernet.Data;
using threading;

namespace decision_pickdrop_02.source;

public partial class Process : IDisposable
{
    private readonly ILogManager _log;
    private AppConfig? _config;
    private MsgPackSubscriber<RfidResult>? _rfidSubscriber;
    private MsgPackSubscriber<BatteryResult>? _batterySubscriber;
    private MsgPackSubscriber<LoadcellResult>? _loadcellSubscriber;
    private MsgPackSubscriber<DistanceResult>? _heightValueSubscriber;
    private MsgPackSubscriber<DistanceResult>? _forwardValueSubscriber;
    private MsgPackSubscriber<CamResult>? _camSubscriber;
    private MsgPackSubscriber<LidarResult>? _lidarSubscriber;
    private MsgPackSubscriber<VolumeResult>? _volumeSubscriber;
    
    private TabletJsonSubscriber? _tabletSubscriber;

    private int _current_command_value = 0;
    
    private bool _cam_pick_state = false;
    private bool _lidar_pick_state = false;

    private int _fork_height_mm = 0;
    private int _fork_forward_mm = 0;
    
    private int _Target_distance_mm = 0;

    private DateTime _scan_complete_time = DateTime.MinValue;
    private bool _scan_complete = false;
    
    private DateTime _pickup_candidate_starttime = DateTime.MinValue;
    private bool _pickup_cantidate_active = false;
    
    public Process()
    {
        _log = CreateLogManager();
        _log.Start();
    }

    public void Dispose()
    {
        void SafeDispose<TDisposable>(ref TDisposable? disposable, string name)
            where TDisposable : class, IDisposable
        {
            if (disposable is null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error($"{name} dispose failed: {ex.Message}");
            }
            finally
            {
                disposable = null;
            }
        }

        SafeDispose(ref _rfidSubscriber, "RFID Subscriber");
        SafeDispose(ref _batterySubscriber, "Battery Subscriber");
        SafeDispose(ref _loadcellSubscriber, "Loadcell Subscriber");
        SafeDispose(ref _heightValueSubscriber, "Height Value Subscriber");
        SafeDispose(ref _forwardValueSubscriber, "Forward Value Subscriber");
        SafeDispose(ref _camSubscriber, "Cam Subscriber");
        SafeDispose(ref _lidarSubscriber, "Lidar Subscriber");
        SafeDispose(ref _volumeSubscriber, "Volume Subscriber");
        
        SafeDispose(ref _tabletSubscriber, "Tablet Subscriber");

        _log.Stop();
    }

    public void OnInit()
    {
        if (!LoadConfig())
        {
            _log.Error("Config load failed. Exiting...");
            return;
        }

        void StartSubscriber<TPayload>(
            string subscriberName,
            ZMQ section,
            MsgPackSubscriber<TPayload>.MessageCallback callback,
            ref MsgPackSubscriber<TPayload>? subscriber)
        {
            try
            {
                var endpoint = section.Endpoint;
                var topic = section.Topic;
                var maxQueueSize = section.MaxQueueSize;

                subscriber = new MsgPackSubscriber<TPayload>(endpoint, topic, maxQueueSize, callback);

                _log.Info(
                    $"{subscriberName} subscriber started. Endpoint='{endpoint}', Topic='{topic}', MaxQueueSize={maxQueueSize}");
            }
            catch (Exception ex)
            {
                _log.Error($"{subscriberName} Subscriber init failed: {ex.Message}");
            }
        }

        StartSubscriber("RFID", _config!.Rfid, RfidPayloadHandler, ref _rfidSubscriber);
        StartSubscriber("Battery", _config.Battery, BatteryPayloadHandler, ref _batterySubscriber);
        StartSubscriber("Loadcell", _config.Loadcell, LoadcellPayloadHandler, ref _loadcellSubscriber);
        StartSubscriber("Distance Height", _config.DistanceHeight, ForkHeightPayloadHandler, ref _heightValueSubscriber);
        StartSubscriber("Distance Forward", _config.DistanceForward, ForkForwardPayloadHandler, ref _forwardValueSubscriber);
        StartSubscriber("Cam Result", _config.CamResult, CamPayloadHandler, ref _camSubscriber);
        StartSubscriber("Lidar Result", _config.LidarResult, LidarPayloadHandler, ref _lidarSubscriber);
        StartSubscriber("Volume Result", _config.VolumeResult, VolumePayloadHandler, ref _volumeSubscriber);

        try
        {
            // Ethernet (TCP/IP) 통신 시 1회 수신 최대 크기를 1024 bytes 으로 설정
            var receiveBufferSize = 1024;
            var transport = new TcpRawChunkSubscriber(_config!.TabletComm.Ip, _config.TabletComm.Port, receiveBufferSize);
            _tabletSubscriber = new TabletJsonSubscriber(
                transport,
                _config.TabletComm.ByteBufferSize,
                TabletJsonPayloadHandler,
                TabletErrorHandler);
            
            _log.Info($"Tablet Subscriber started. Ip='{_config.TabletComm.Ip}', Port={_config.TabletComm.Port}, ByteBufferSize={_config.TabletComm.ByteBufferSize}");
        }
        catch (Exception e)
        {
            _log.Error($"Tablet Subscriber init failed: {e.Message}");
        }
        
        // worker thread 생성
        using var cts = new CancellationTokenSource();
        using var worker = new PeriodicWorkerThread(
            () => Working(),
            TimeSpan.FromMilliseconds(100),
            cts.Token
        );
        
        worker.Start();
        _log.Info("Worker 스레드를 시작합니다.");
        
        _log.Info("Ctrl + D 를 누르면 종료합니다.");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.D &&
                key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                break;
            }
        }
        
        _log.Info("Ctrl + D 입력 감지. 종료합니다.");

        // worker thread 종료
        cts.Cancel();
        worker.StopAndJoin();
        _log.Info("Worker 스레드를 종료합니다.");
        
        void StopSubscriber<TPayload>(
            string subscriberName,
            ref MsgPackSubscriber<TPayload>? subscriber)
        {
            try
            {
                subscriber?.Stop();
                _log.Info($"{subscriberName} subscriber stopped.");
            }
            catch (Exception ex)
            {
                _log.Error($"{subscriberName} subscriber stop failed: {ex.Message}");
            }
            finally
            {
                subscriber = null;
            }
        }

        StopSubscriber("RFID", ref _rfidSubscriber);
        StopSubscriber("Battery", ref _batterySubscriber);
        StopSubscriber("Loadcell", ref _loadcellSubscriber);
        StopSubscriber("Distance Height", ref _heightValueSubscriber);
        StopSubscriber("Distance Forward", ref _forwardValueSubscriber);
        StopSubscriber("Cam Result", ref _camSubscriber);
        StopSubscriber("Lidar Result", ref _lidarSubscriber);
        StopSubscriber("Volume Result", ref _volumeSubscriber);

        try
        {
            _tabletSubscriber?.Stop();
            _log.Info("Tablet subscriber stopped.");
        }
        catch (Exception e)
        {
            _log.Error("Tablet subscriber stop failed: " + e.Message);
        }
        finally
        {
            _tabletSubscriber = null;
        }
        
    }

    private ILogManager CreateLogManager()
    {
        var log = new LogManager(new LogFactory(), 128);

#if DEBUG
        log.AddOutput(LogLevel.Debug, StrategyKind.Console, 1);
#endif
        log.AddOutput(LogLevel.Info, StrategyKind.Console, 1);
        log.AddOutput(LogLevel.Warn, StrategyKind.Console, 1);
        log.AddOutput(LogLevel.Error, StrategyKind.Console, 1);

        return log;
    }

    private bool LoadConfig()
    {
        var configPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../../config/settings.yaml"));

        try
        {
            _config = YamlConfigLoader.Load(configPath);

            var sb = new StringBuilder(1024);

            sb.AppendLine();
            sb.AppendLine("==================================================");
            sb.AppendLine("                 CONFIGURATION LOAD               ");
            sb.AppendLine("==================================================");
            sb.AppendLine($"Path : {configPath}");
            sb.AppendLine();

            // App
            sb.AppendLine("[APP]");
            sb.AppendLine($"  Name    : {_config.App.Name}");
            sb.AppendLine($"  Version : {_config.App.Version}");
            sb.AppendLine($"  Fork Height Thresholdmm : {_config.App.ForkHeightThresholdmm}");
            sb.AppendLine();

#if SHOW
            // ZMQ Sections
            void AppendZmqSection(string title, ZMQ section)
            {
                sb!.AppendLine($"[{title}]");
                sb.AppendLine($"  Endpoint     : {section.Endpoint}");
                sb.AppendLine($"  Topic        : {section.Topic}");
                sb.AppendLine($"  MaxQueueSize : {section.MaxQueueSize}");
                sb.AppendLine();
            }
            
            AppendZmqSection("RFID", _config.Rfid);
            AppendZmqSection("BATTERY", _config.Battery);
            AppendZmqSection("LOADCELL", _config.Loadcell);
            AppendZmqSection("DISTANCE_HEIGHT", _config.DistanceHeight);
            AppendZmqSection("DISTANCE_FORWARD", _config.DistanceForward);
            AppendZmqSection("CAM_RESULT", _config.CamResult);
            AppendZmqSection("LIDAR_RESULT", _config.LidarResult);
            AppendZmqSection("OCR_RESULT", _config.OcrResult);
            AppendZmqSection("SLAM_RESULT", _config.SlamResult);
            AppendZmqSection("VOLUME_RESULT", _config.VolumeResult);
#endif

            sb.AppendLine("==================================================");

            _log.Info(sb.ToString());
            return true;
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CONFIG LOAD FAILED");
            sb.AppendLine($"Reason : {ex.Message}");
            sb.AppendLine($"Path   : {configPath}");

            _log.Error(sb.ToString());
            return false;
        }
    }

    private void Working()
    {
        var ForkHeightThreshold = _config!.App.ForkHeightThresholdmm;
        var TargetDistanceThreshold = _config.App.TargetDistanceThresholdmm;
        var command = _current_command_value;
        var CameraPickState = _cam_pick_state;
        var LiDARPickState = _lidar_pick_state;
        var ForkHeight = _fork_height_mm;
        var ForkFoward = _fork_forward_mm;
        var TargetDistance = _Target_distance_mm;
        var ScanCompleteTime = _scan_complete_time;
        var ScanComplete = _scan_complete;
        var PickupCandidateStartTime = _pickup_candidate_starttime;
        var PickupCandidateActive = _pickup_cantidate_active;
        
        // _fork_height_mm >= _config.App.ForkHeightThresholdmm 일 경우
        // => _cam_pick_state 으로 pick-up 판단 (_current_command_value = 3 으로 변경)
        // => _cam_pick_state 으로 drop 판단 (_current_command_value = 0 으로 변경)
        
        // _fork_height_mm < _config.App.ForkHeightThresholdmm 일 경우
        // => _lidar_pick_state & _cam_pick_state 으로 pick-up 판단 (_current_command_value = 3 으로 변경)
        // => _lidar_pick_state 으로 drop 판단 (_current_command_value = 0 으로 변경)
        
        // _Target_distance_mm <= _config.App.TargetDistanceThresholdmm
        // && _fork_height_mm < ForkHeightThresholdmm
        // && _current_command_value = 0 일 경우
        // => OCR START 전송 (_current_command_value = 1 으로 변경)
        
        // _fork_height_mm < ForkHeightThresholdmm
        // && _Target_distance_mm > _config.App.TargetDistanceThresholdmm
        // && _current_command_value = 1 일 경우
        // => (_current_command_value = 0 으로 변경)
        
        // _current_command_value != 3 && 
        // (_scan_complete = true && _scan_complete_time 이 현재 시간 기준으로 60 초 이내일 때) 일 경우
        // => (_current_command_value = 2 으로 변경)
    }
}
