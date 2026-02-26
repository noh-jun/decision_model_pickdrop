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
        StartSubscriber("Distance Height", _config.DistanceHeight, ForkHeightPayloadHandler,
            ref _heightValueSubscriber);
        StartSubscriber("Distance Forward", _config.DistanceForward, ForkForwardPayloadHandler,
            ref _forwardValueSubscriber);
        StartSubscriber("Cam Result", _config.CamResult, CamPayloadHandler, ref _camSubscriber);
        StartSubscriber("Lidar Result", _config.LidarResult, LidarPayloadHandler, ref _lidarSubscriber);
        StartSubscriber("Volume Result", _config.VolumeResult, VolumePayloadHandler, ref _volumeSubscriber);

        try
        {
            // Ethernet (TCP/IP) 통신 시 1회 수신 최대 크기를 1024 bytes 으로 설정
            var receiveBufferSize = 1024;
            var transport =
                new TcpRawChunkSubscriber(_config!.TabletComm.Ip, _config.TabletComm.Port, receiveBufferSize);
            _tabletSubscriber = new TabletJsonSubscriber(
                transport,
                _config.TabletComm.ByteBufferSize,
                TabletJsonPayloadHandler,
                TabletErrorHandler);

            _log.Info(
                $"Tablet Subscriber started. Ip='{_config.TabletComm.Ip}', Port={_config.TabletComm.Port}, ByteBufferSize={_config.TabletComm.ByteBufferSize}");
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
        var configPath =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../../config/settings.yaml"));

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
        var ForkFoward = _fork_forward_mm; // 현재 규칙에는 직접 사용 안 함(유지)
        var TargetDistance = _Target_distance_mm;

        var ScanCompleteTime = _scan_complete_time;
        var ScanComplete = _scan_complete;

        var PickupCandidateStartTime = _pickup_candidate_starttime;
        var PickupCandidateActive = _pickup_cantidate_active;

        var nowUtc = DateTime.UtcNow;

        // =========================
        // 1) pickup / drop 신호 계산
        // =========================
        bool pickupSignal;
        bool dropSignal;

        if (ForkHeight >= ForkHeightThreshold)
        {
            // 포크 높음(>=): 카메라 단독
            pickupSignal = CameraPickState;
            dropSignal = !CameraPickState;
        }
        else
        {
            // 포크 낮음(<): pickup=라이다&&카메라, drop=라이다 단독(false)
            pickupSignal = LiDARPickState && CameraPickState;
            dropSignal = !LiDARPickState;
        }

        // ==========================================
        // 2) drop 우선 처리: drop이면 즉시 0 + 후보 취소
        // ==========================================
        if (dropSignal)
        {
            if (command != 0)
            {
                command = 0;
            }

            if (PickupCandidateActive)
            {
                PickupCandidateActive = false;
            }
        }

        // ==========================================
        // 3) pickup 확정(=3) 처리: "즉시 3 금지" -> 후보 상태로 대기 후 확정
        //    - command==3이면 여기서는 아무것도 하지 않음(이미 확정)
        // ==========================================
        if (command != 3 && !dropSignal)
        {
            if (pickupSignal)
            {
                // 후보 진입
                if (!PickupCandidateActive)
                {
                    PickupCandidateActive = true;
                    PickupCandidateStartTime = nowUtc;
                }

                // 중량 센서 입력이 신선해야 안정화 판단이 의미 있음(권장)
                bool weightFresh = IsWeightInputFresh(nowUtc);

                // 안정 중량 조건 만족 -> 3 확정
                if (weightFresh && IsWeightStableAndAboveMinKg(PickupCandidateStartTime, nowUtc))
                {
                    command = 3;
                    PickupCandidateActive = false;
                }
                else
                {
                    // 타임아웃 예외 확정(원본은 10초)
                    var pickupConfirmTimeout = TimeSpan.FromSeconds(10);
                    if (nowUtc - PickupCandidateStartTime >= pickupConfirmTimeout)
                    {
                        // 원본은 "중량=-1"로 확정했음.
                        // 여기서는 command=3만 확정하고, weight 유효성은 별도 플래그로 기록하는 걸 권장.
                        command = 3;
                        PickupCandidateActive = false;

                        // 예: _pickup_confirmed_without_weight = true; 같은 플래그를 두고 기록 가능
                    }
                }
            }
            else
            {
                // pickup이 꺼졌으면 후보 취소(확정 전)
                if (PickupCandidateActive)
                {
                    PickupCandidateActive = false;
                }
            }
        }

        // ==========================================
        // 4) OCR_START(=command 1) 트리거 / 리셋
        //    - 포크 낮음(<)에서만 거리 기반 동작
        // ==========================================
        bool isForkLowForDistanceLogic = ForkHeight < ForkHeightThreshold;

        if (isForkLowForDistanceLogic)
        {
            // 0에서만 OCR_START 시도
            if (command == 0 &&
                TargetDistance > 0 &&
                TargetDistance <= TargetDistanceThreshold)
            {
                SendOcrStart(); // "OCR_START" 전송
                command = 1;
            }

            // 1 상태에서 목표가 멀어지면 0으로 내려서 재트리거 가능하게
            if (command == 1 &&
                TargetDistance > TargetDistanceThreshold)
            {
                command = 0;
            }
        }

        // ==========================================
        // 5) scan_complete 기반 command=2
        //    - command==3은 절대 2로 내리지 않음
        // ==========================================
        if (command != 3 &&
            ScanComplete &&
            (nowUtc - ScanCompleteTime) <= TimeSpan.FromSeconds(60))
        {
            command = 2;
        }

        // ==========================================
        // 6) 결과 반영(멤버 업데이트 + publish)
        // ==========================================
        _pickup_cantidate_active = PickupCandidateActive;
        _pickup_candidate_starttime = PickupCandidateStartTime;

        if (_current_command_value != command)
        {
            _current_command_value = command;
            PublishCommand(command);
        }
    }

// =========================
// 아래 3개는 네 환경에 맞게 구현/연결하면 됨
// =========================

// 1) OCR_START 전송(예: TCP/UDP/ROS publish 등)
    private void SendOcrStart()
    {
        // TODO: 구현체 연결
    }

// 2) /forklift/command publish
    private void PublishCommand(int command)
    {
        // TODO: 구현체 연결
    }

// 3) 중량 입력 최신성(센서 끊김/정지 방지)
    private bool IsWeightInputFresh(DateTime nowUtc)
    {
        // TODO: 예) (nowUtc - _last_weight_update_time) <= 1.0s
        return true;
    }

// 4) 안정 중량 판정(후보 시작 이후 샘플만으로 안정/최소중량 만족)
    private bool IsWeightStableAndAboveMinKg(DateTime candidateStartUtc, DateTime nowUtc)
    {
        // TODO:
        // - candidateStartUtc 이후의 weight 샘플을 window(예: 1~2초 또는 3~6샘플)에 모은다
        // - max-min <= toleranceKg AND avg >= minPickupWeightKg 이면 true
        return false;
    }
}