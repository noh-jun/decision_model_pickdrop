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

    // 태블릿에서 유사도 분석 수행 후 Complete 를 전송한 시각
    private DateTime _scan_complete_time = DateTime.MinValue;

    // 태블릿에서 유사도 분석 수행 후 결과
    private bool _scan_complete = false;

    // Pickup 판단 시작 시간 (중량 체크를 위한 준비)
    private DateTime _pickup_candidate_starttime = DateTime.MinValue;

    // Pickup 판단 시작 알림 (중량 체크를 위한 준비)
    private bool _pickup_cantidate_active = false;

    private double _stable_height_mm = 0.0;
    private bool _height_is_stable = false;
    private DateTime _height_stability_start_time_utc = DateTime.MinValue;

    private bool _ocr_triggered_by_height_stability = false;
    private DateTime _last_height_ocr_trigger_time_utc = DateTime.MinValue;
    
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
        var ForkHeightThreshold = _config!.App.ForkHeightThresholdmm;
        var TargetDistanceThreshold = _config.App.TargetDistanceThresholdmm;

        CMD command = _current_command_value;

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
        // 0) pickup / drop 신호 계산
        // =========================
        bool pickupSignal = false;
        bool dropSignal = false;

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
            command = CMD.DROP;

            if (PickupCandidateActive)
                PickupCandidateActive = false;
        }
        // ==========================================
        // 3) pickup 확정 처리: "즉시 3 금지" -> 후보 상태로 대기 후 확정
        //      Drop-Signal (x) & Pickup-Signal (o) & CMD.PICKUP != Current-CMD
        // ==========================================
        else if (CMD.PICKUP != command && pickupSignal)
        {
            // 후보 진입
            if (!PickupCandidateActive)
            {
                PickupCandidateActive = true;
                PickupCandidateStartTime = nowUtc;
            }

            // 중량 센서 입력이 신선해야 안정화 판단이 의미 있음(권장)
            bool weightFresh = IsWeightInputFresh(nowUtc);
            bool weightStable = IsWeightStableAndAboveMinKg(PickupCandidateStartTime, nowUtc);

            // 안정 중량 조건 만족 -> 3 확정
            if (weightFresh && weightStable)
            {
                command = CMD.PICKUP;
                PickupCandidateActive = false;
            }
            // 타임아웃 예외 확정(원본은 10초)
            else if (nowUtc - PickupCandidateStartTime >= TimeSpan.FromSeconds(10))
            {
                // 원본은 "중량=-1"로 확정했음.
                // 여기서는 command=3만 확정하고, weight 유효성은 별도 플래그로 기록하는 걸 권장.
                command = CMD.PICKUP;
                PickupCandidateActive = false;

                // 예: _pickup_confirmed_without_weight = true; 같은 플래그를 두고 기록 가능
            }
        }
        // ==========================================
        // 3-1) Pickup/Drop 모두 아닐 경우 Pickup 후보 상태 해제
        //      Drop-Signal (x) & Pickup-Signal (x) & CMD.PICKUP != Current-CMD
        // ==========================================
        else if (CMD.PICKUP != command && !pickupSignal)
        {
            // pickup이 꺼졌으면 후보 취소(확정 전)
            if (PickupCandidateActive)
            {
                PickupCandidateActive = false;
            }
        }

        // ==========================================
        // 4) OCR_START 트리거 / 리셋
        //    - 거리 기반: 포크 낮음(<)에서만
        //    - 높이 안정화 기반(3초 재트리거): 포크 높음(>=)에서만
        //
        // 원본 규칙(중요):
        // - command==2 또는 3이면 OCR_START 자체를 차단
        // - OCR_START는 command==1 상태에서도 재발행될 수 있음
        // - command를 1로 올리는 건 command==0일 때만
        // ==========================================
        bool blockOcrStart = (CMD.OCR_COMPLETE == command) || (CMD.PICKUP == command);

        if (ForkHeight < ForkHeightThreshold)
        {
            // ---- 거리(X) 기반 ----
            // 포크가 낮아지면 높이 안정화 상태 리셋(원본 reset_height_stability)
            _height_is_stable = false;
            _ocr_triggered_by_height_stability = false;
            _stable_height_mm = 0.0;
            _height_stability_start_time_utc = DateTime.MinValue;
            _last_height_ocr_trigger_time_utc = DateTime.MinValue;

            if (!blockOcrStart)
            {
                // 0에서만 OCR_START 시도
                if (CMD.DROP == command &&
                    TargetDistance > 0 &&
                    TargetDistance <= TargetDistanceThreshold)
                {
                    SendOcrStart(); // "OCR_START" 전송
                    command = CMD.OCR_START;
                }

                // 1 상태에서 목표가 멀어지면 0으로 내려서 재트리거 가능하게
                if (CMD.OCR_START == command &&
                    TargetDistance > TargetDistanceThreshold)
                {
                    command = CMD.DROP;
                }
            }
        }
        else // ForkHeight >= ForkHeightThreshold
        {
            // ---- 높이 안정화 기반 (3초 재트리거) ----
            if (!blockOcrStart)
            {
                // 설정값이 없다면 우선 상수로 두고, 추후 config로 빼면 됨
                double toleranceMm = 20.0; // height_stability_tolerance_mm_
                double stableDurationSec = 0.5; // height_stability_duration_sec_
                double retriggerIntervalSec = 3.0; // height_ocr_retrigger_interval_sec_

                double heightDiff = Math.Abs(ForkHeight - _stable_height_mm);

                if (heightDiff <= toleranceMm)
                {
                    if (!_height_is_stable)
                    {
                        // 안정화 시작
                        _height_is_stable = true;
                        _stable_height_mm = ForkHeight;
                        _height_stability_start_time_utc = nowUtc;
                    }
                    else
                    {
                        // 안정화 지속 시간 체크
                        double stableSeconds = (nowUtc - _height_stability_start_time_utc).TotalSeconds;

                        if (stableSeconds >= stableDurationSec)
                        {
                            if (!_ocr_triggered_by_height_stability)
                            {
                                // 첫 트리거
                                SendOcrStart();
                                _ocr_triggered_by_height_stability = true;
                                _last_height_ocr_trigger_time_utc = nowUtc;

                                // command==0일 때만 1로 올림 (원본과 동일)
                                if (CMD.DROP == command)
                                {
                                    command = CMD.OCR_START;
                                }
                            }
                            else
                            {
                                // 재트리거: 마지막 트리거 이후 N초 경과 시
                                double sinceLastSec = (nowUtc - _last_height_ocr_trigger_time_utc).TotalSeconds;

                                if (sinceLastSec >= retriggerIntervalSec)
                                {
                                    SendOcrStart();
                                    _last_height_ocr_trigger_time_utc = nowUtc;

                                    // command==0이면 1로 올림, command==1이면 유지
                                    if (CMD.DROP == command)
                                    {
                                        command = CMD.OCR_START;
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 높이 변화 감지 → 안정화 깨짐
                    _height_is_stable = false;
                    _stable_height_mm = ForkHeight;
                    _height_stability_start_time_utc = nowUtc;

                    // 원본 의미: 높이 기반 OCR을 이미 쏜 상태인데(ocr_triggered_by_height_stability),
                    // command가 1 또는 2였다면(여기서는 1만 해당. 2는 blockOcrStart로 진입 자체가 막힘)
                    // 작업 변경 감지로 보고 0으로 리셋
                    if (_ocr_triggered_by_height_stability &&
                        (CMD.OCR_START == command || CMD.OCR_COMPLETE == command))
                    {
                        command = CMD.DROP;
                    }

                    _ocr_triggered_by_height_stability = false;
                }
            }
            else
            {
                // command==2/3 상태에서는 높이 기반 트리거 중단(원본과 동일 의도)
                _ocr_triggered_by_height_stability = false;
                _height_is_stable = false;
            }
        }

        // ==========================================
        // 5) scan_complete 기반 command=2
        //    - command==3은 절대 2로 내리지 않음
        // ==========================================
        if (CMD.PICKUP != command &&
            ScanComplete &&
            (nowUtc - ScanCompleteTime) <= TimeSpan.FromSeconds(60))
        {
            command = CMD.OCR_COMPLETE;
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