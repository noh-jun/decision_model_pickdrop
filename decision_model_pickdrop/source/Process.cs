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
    private enum CMD
    {
        DROP = 0,
        OCR_START,
        OCR_COMPLETE,
        PICKUP,
    }

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

    private CMD _current_command_value = 0;

    // 마지막 수신 시간
    private DateTime _target_weight_time = DateTime.MinValue;

    // 물류 무게
    private double _target_weight = -1;

    // 카메라로 판단한 pick state
    private bool _cam_pick_state = false;

    // 라이다로 판단한 pick state
    private bool _lidar_pick_state = false;

    // 거리 센서로 측정한 Fork 의 높이
    private int _fork_height_mm = 0;

    // 거리 센서로 측정한 Fork 의 전/후진 위치
    private int _fork_forward_mm = 0;

    // 센서(라이다 or 거리센서)로 측정한 물류와 거리
    private int _Target_distance_mm = 0;

    // 태블릿에서 유사도 분석 수행 후 Complete 를 전송한 시각 & Complete 플래그
    private DateTime _scan_complete_time = DateTime.MinValue;
    private bool _scan_complete = false;

    // Pickup 판단 시작 시간 & 플래그 (중량 체크를 위한 준비)
    private DateTime _pickup_candidate_starttime = DateTime.MinValue;
    private bool _pickup_cantidate_active = false;

    private double _stable_height_mm = 0.0;
    private DateTime _height_stability_start_time_utc = DateTime.MinValue;
    private bool _height_is_stable = false;

    private DateTime _last_height_ocr_trigger_time_utc = DateTime.MinValue;
    private bool _ocr_triggered_by_height_stability = false;

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

// =========================
// Decesion 순서
//      : Parameter Capture
//      : Drop 판단 -> Pick-up 확정 판단 -> Pick-up 취소 판단
//      : OCR_START 판단 (거리 기반 + 높이 안정화 기반(3초 재트리거))
//      : OCR_COMPLETE 판단
//      : Parameter Update
// =========================
    private void Working()
    {
        // ========== Parameter Capture ==========
        var forkHeightThreshold = _config!.App.ForkHeightThresholdmm;
        var targetDistanceThreshold = _config.App.TargetDistanceThresholdmm;

        var nowUtc = DateTime.UtcNow;

        var command = _current_command_value;

        var cameraPickState = _cam_pick_state;
        var lidarPickState = _lidar_pick_state;

        double forkHeight = _fork_height_mm;
        double forkForward = _fork_forward_mm; // 현재 규칙에는 직접 사용 안 함(유지)
        double targetDistance = _Target_distance_mm;

        var scanCompleteTime = _scan_complete_time;
        var scanComplete = _scan_complete;

        var pickupCandidateStartTime = _pickup_candidate_starttime;
        var pickupCandidateActive = _pickup_cantidate_active;

        // ========== 0) pickup/drop 신호 계산 ==========
        var signals = CalcSignals(forkHeight, forkHeightThreshold, cameraPickState, lidarPickState);
        var pickupSignal = signals.pickup;
        var dropSignal = signals.drop;

        // ========== 1) Drop / Pickup 결정 ==========
        HandleDropOrPickup();

        // ========== 2) OCR_START 결정 (거리 기반 / 높이 안정화 기반) ==========
        HandleOcrStart();

        // ========== 3) OCR_COMPLETE 결정 ==========
        HandleOcrComplete();

        // ========== 4) Parameter Update / Publish ==========
        _pickup_cantidate_active = pickupCandidateActive;
        _pickup_candidate_starttime = pickupCandidateStartTime;

        if (_current_command_value != command)
        {
            _current_command_value = command;
            PublishCommand(command);
        }

        // ------------------------------------------------------------------
        // Local functions
        // ------------------------------------------------------------------

        static (bool pickup, bool drop) CalcSignals(double forkHeight, double forkHeightThreshold, bool cameraPickState, bool lidarPickState)
        {
            if (forkHeight >= forkHeightThreshold)
            {
                // 포크 높음(>=): 카메라 단독
                return (pickup: cameraPickState, drop: !cameraPickState);
            }

            // 포크 낮음(<): pickup=라이다&&카메라, drop=라이다 단독(false)
            return (pickup: lidarPickState && cameraPickState, drop: !lidarPickState);
        }

        void HandleDropOrPickup()
        {
            // 2) drop 우선 처리: drop이면 즉시 0 + 후보 취소
            if (dropSignal)
            {
                command = CMD.DROP;
                pickupCandidateActive = false;
                return;
            }

            // 3) pickup 확정 처리: "즉시 3 금지" -> 후보 상태로 대기 후 확정
            if (command != CMD.PICKUP && pickupSignal)
            {
                EnterPickupCandidateIfNeeded();

                bool weightFresh = IsWeightInputFresh(nowUtc);
                bool weightStable = IsWeightStableAndAboveMinKg(pickupCandidateStartTime, nowUtc);

                if (weightFresh && weightStable)
                {
                    ConfirmPickup();
                    return;
                }

                // 타임아웃 예외 확정(원본은 10초)
                if (nowUtc - pickupCandidateStartTime >= TimeSpan.FromSeconds(10))
                {
                    ConfirmPickup();
                    // 예: _pickup_confirmed_without_weight = true;
                    return;
                }

                return; // 후보 유지
            }

            // 3-1) Pickup/Drop 모두 아닐 경우 Pickup 후보 상태 해제
            if (command != CMD.PICKUP && !pickupSignal)
            {
                pickupCandidateActive = false;
            }

            void EnterPickupCandidateIfNeeded()
            {
                if (pickupCandidateActive) return;
                pickupCandidateActive = true;
                pickupCandidateStartTime = nowUtc;
            }

            void ConfirmPickup()
            {
                command = CMD.PICKUP;
                pickupCandidateActive = false;
            }
        }

        void HandleOcrStart()
        {
            // 원본 규칙:
            // - command==2 또는 3이면 OCR_START 차단
            // - OCR_START는 command==1 상태에서도 재발행될 수 있음
            // - command를 1로 올리는 건 command==0일 때만
            bool blockOcrStart = (command == CMD.OCR_COMPLETE) || (command == CMD.PICKUP);

            if (forkHeight < forkHeightThreshold)
            {
                ResetHeightStabilityState(); // 포크가 낮아지면 높이 안정화 상태 리셋
                HandleDistanceBasedOcr(blockOcrStart);
                return;
            }

            HandleHeightStabilityOcr(blockOcrStart);

            void ResetHeightStabilityState()
            {
                _height_is_stable = false;
                _ocr_triggered_by_height_stability = false;
                _stable_height_mm = 0.0;
                _height_stability_start_time_utc = DateTime.MinValue;
                _last_height_ocr_trigger_time_utc = DateTime.MinValue;
            }

            void HandleDistanceBasedOcr(bool blocked)
            {
                if (blocked)
                    return;

                // 0에서만 OCR_START 시도
                if (command == CMD.DROP &&
                    targetDistance > 0 &&
                    targetDistance <= targetDistanceThreshold)
                {
                    SendOcrStart();
                    command = CMD.OCR_START;
                    // 일부러 return 안 함: 아래 "멀어지면 0으로" 체크도 수행하도록
                }

                // 1 상태에서 목표가 멀어지면 0으로 내려서 재트리거 가능하게
                if (command == CMD.OCR_START &&
                    targetDistance > targetDistanceThreshold)
                {
                    command = CMD.DROP;
                }
            }

            void HandleHeightStabilityOcr(bool blocked)
            {
                // ---- 높이 안정화 기반 (3초 재트리거) ----
                if (blocked)
                {
                    _ocr_triggered_by_height_stability = false;
                    _height_is_stable = false;
                    return;
                }

                const double toleranceMm = 20.0;
                const double stableDurationSec = 0.5;
                const double retriggerIntervalSec = 3.0;

                double heightDiff = Math.Abs(forkHeight - _stable_height_mm);

                if (heightDiff > toleranceMm)
                {
                    BreakStabilityBecauseHeightChanged();
                    return;
                }

                if (!_height_is_stable)
                {
                    StartStability();
                    return;
                }

                if (!HasBeenStableLongEnough())
                    return;

                if (!_ocr_triggered_by_height_stability)
                {
                    TriggerOcrStartIfNeeded();
                    _ocr_triggered_by_height_stability = true;
                    return;
                }

                if (IsRetriggerDue())
                {
                    TriggerOcrStartIfNeeded();
                }

                // --- helpers inside height stability ---
                void BreakStabilityBecauseHeightChanged()
                {
                    _height_is_stable = false;
                    _stable_height_mm = forkHeight;
                    _height_stability_start_time_utc = nowUtc;

                    if (_ocr_triggered_by_height_stability &&
                        (command == CMD.OCR_START || command == CMD.OCR_COMPLETE))
                    {
                        command = CMD.DROP;
                    }

                    _ocr_triggered_by_height_stability = false;
                }

                void StartStability()
                {
                    _height_is_stable = true;
                    _stable_height_mm = forkHeight;
                    _height_stability_start_time_utc = nowUtc;
                }

                bool HasBeenStableLongEnough()
                {
                    double stableSeconds = (nowUtc - _height_stability_start_time_utc).TotalSeconds;
                    return stableSeconds >= stableDurationSec;
                }

                bool IsRetriggerDue()
                {
                    double sinceLastSec = (nowUtc - _last_height_ocr_trigger_time_utc).TotalSeconds;
                    return sinceLastSec >= retriggerIntervalSec;
                }

                void TriggerOcrStartIfNeeded()
                {
                    SendOcrStart();
                    _last_height_ocr_trigger_time_utc = nowUtc;

                    // command==0이면 1로 올림, command==1이면 유지
                    if (CMD.DROP == command)
                        command = CMD.OCR_START;
                }
            }
        }

        void HandleOcrComplete()
        {
            // 5) scan_complete 기반 command=2
            //    - command==3은 절대 2로 내리지 않음
            if (command == CMD.PICKUP)
                return;

            if (!scanComplete)
                return;

            if ((nowUtc - scanCompleteTime) > TimeSpan.FromSeconds(60))
                return;

            command = CMD.OCR_COMPLETE;
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
    private void PublishCommand(CMD command)
    {
        _current_command_value = command;

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