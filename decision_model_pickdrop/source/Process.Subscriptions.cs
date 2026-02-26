using Zmq.Data;
using Zmq.Subscribe;
using System.Text;
using Ethernet.Data;

namespace decision_pickdrop_02.source;

public partial class Process
{
    private void RfidPayloadHandler(RfidResult payload)
    {
        const int maxTagsToPrint = 50;

        var textBuilder = new StringBuilder(512);

// Header
        textBuilder.Append("RFID Payload: ");
        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId);
        textBuilder.Append(", seq_no=").Append(payload.SeqNo);
        textBuilder.Append(", pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp));
        textBuilder.Append(", driver_state=").Append(payload.DriverState);
        textBuilder.Append(", ret_code=").Append(payload.RetCode);

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append(", driver_err_msg=").Append(payload.DriverErrMsg);
        }

// Tags
        var tags = payload.Tags;
        int tagCount = tags?.Count ?? 0;

        textBuilder.Append(", tags_count=").Append(tagCount);

        if (tagCount == 0)
        {
            _log.Info(textBuilder.ToString());
            return;
        }

        textBuilder.Append(", tags=[");

        int printedCount = 0;
        foreach (var tag in tags!)
        {
            if (tag is null)
            {
                continue;
            }

            if (printedCount > 0)
            {
                textBuilder.Append(", ");
            }

            textBuilder.Append("{");
            textBuilder.Append("epc=").Append(tag.Epc);
            textBuilder.Append(", rssi=").Append(tag.Rssi);
            textBuilder.Append(", readCount=").Append(tag.ReadCount);
            textBuilder.Append(", antenna=").Append(tag.Antenna);

            // timeStamp가 ulong(ns epoch)라고 가정하고 사람이 읽는 시간으로 출력
            textBuilder.Append(", timeStamp=").Append(FormatTimestamp(tag.TimeStamp));

            textBuilder.Append("}");

            printedCount++;
            if (printedCount >= maxTagsToPrint)
            {
                break;
            }
        }

        if (tagCount > printedCount)
        {
            textBuilder.Append(", ... +").Append(tagCount - printedCount).Append(" more");
        }

        textBuilder.Append("]");

        _log.Info(textBuilder.ToString());
    }

    private void BatteryPayloadHandler(BatteryResult payload)
    {
        var textBuilder = new StringBuilder(384);

        textBuilder.AppendLine("Battery Payload:");

        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId).AppendLine();
        textBuilder.Append("seq_no=").Append(payload.SeqNo).AppendLine();
        textBuilder.Append("pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp)).AppendLine();
        textBuilder.Append("driver_state=").Append(payload.DriverState).AppendLine();
        textBuilder.Append("ret_code=").Append(payload.RetCode).AppendLine();

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append("driver_err_msg=").Append(payload.DriverErrMsg).AppendLine();
        }

        var st = payload.Status;
        if (st != null)
        {
            textBuilder.AppendLine("[Battery Status]:");

            textBuilder.Append("  address=").Append(st.Address).AppendLine();
            textBuilder.Append("  voltage_v=").Append(st.VoltageV).AppendLine();
            textBuilder.Append("  current_a=").Append(st.CurrentA).AppendLine();
            textBuilder.Append("  soc_percent=").Append(st.SocPercent).AppendLine();
            textBuilder.Append("  battery_state_bits=").Append(st.BatteryStateBits).AppendLine();
            textBuilder.Append("  time_to_full_min=").Append(st.TimeToFullMin).AppendLine();
            textBuilder.Append("  time_to_empty_min=").Append(st.TimeToEmptyMin).AppendLine();
            textBuilder.Append("  temperature_c=").Append(st.TemperatureC).AppendLine();
            textBuilder.Append("  soh_percent=").Append(st.SohPercent).AppendLine();
            textBuilder.Append("  remaining_capacity_ah=").Append(st.RemainingCapacityAh).AppendLine();
            textBuilder.Append("  remaining_energy_wh=").Append(st.RemainingEnergyWh).AppendLine();
        }

        _log.Info(textBuilder.ToString());
    }

    private void LoadcellPayloadHandler(LoadcellResult payload)
    {
        var textBuilder = new StringBuilder(512);

        textBuilder.AppendLine("Loadcell Payload:");

        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId).AppendLine();
        textBuilder.Append("seq_no=").Append(payload.SeqNo).AppendLine();
        textBuilder.Append("pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp)).AppendLine();
        textBuilder.Append("driver_state=").Append(payload.DriverState).AppendLine();
        textBuilder.Append("ret_code=").Append(payload.RetCode).AppendLine();

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append("driver_err_msg=").Append(payload.DriverErrMsg).AppendLine();
        }

        var st = payload.Status;
        if (st != null)
        {
            textBuilder.AppendLine("[Loadcell Status]:");

            textBuilder.Append("  Gross Weight=").Append(st.GrossWeight).AppendLine();
            textBuilder.Append("  Right Weight=").Append(st.RightWeight).AppendLine();
            textBuilder.Append("  Left Weight=").Append(st.LeftWeight).AppendLine();

            textBuilder.Append("  Right Battery Percent=").Append(st.RightBatteryPercent).AppendLine();
            textBuilder.Append("  Right Charge Status=").Append(st.RightChargeStatus).AppendLine();
            textBuilder.Append("  Right Online Status=").Append(st.RightOnlineStatus).AppendLine();

            textBuilder.Append("  Left Battery Percent=").Append(st.LeftBatteryPercent).AppendLine();
            textBuilder.Append("  Left Charge Status=").Append(st.LeftChargeStatus).AppendLine();
            textBuilder.Append("  Left Online Status=").Append(st.LeftOnlineStatus).AppendLine();

            textBuilder.Append("  Gross Net Mark=").Append(st.GrossNetMark).AppendLine();
            textBuilder.Append("  Overload Mark=").Append(st.OverloadMark).AppendLine();
            textBuilder.Append("  Out Of Tolerance Mark=").Append(st.OutOfToleranceMark).AppendLine();
        }

        _log.Info(textBuilder.ToString());
    }

    private void LogDistancePayload(string topic, DistanceResult payload)
    {
        var textBuilder = new StringBuilder(512);

        textBuilder.AppendLine("Distance Payload:");

        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId).AppendLine();
        textBuilder.Append("seq_no=").Append(payload.SeqNo).AppendLine();
        textBuilder.Append("pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp)).AppendLine();
        textBuilder.Append("driver_state=").Append(payload.DriverState).AppendLine();
        textBuilder.Append("ret_code=").Append(payload.RetCode).AppendLine();

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append("driver_err_msg=").Append(payload.DriverErrMsg).AppendLine();
        }

        textBuilder.AppendLine($"[Distance {topic} Value]:");

        textBuilder.Append("   RawMm=").Append(payload.RawMm).AppendLine();
        textBuilder.Append("   CalibratedMm=").Append(payload.CalibratedMm).AppendLine();

        _log.Info(textBuilder.ToString());
    }

    private void ForkHeightPayloadHandler(DistanceResult payload)
    {
        LogDistancePayload("Fork Height", payload);

        _fork_height_mm = payload.CalibratedMm;
    }

    private void ForkForwardPayloadHandler(DistanceResult payload)
    {
        LogDistancePayload("Fork Forward", payload);

        _fork_forward_mm = payload.CalibratedMm;
    }

    private void CamPayloadHandler(CamResult payload)
    {
        var textBuilder = new StringBuilder(512);

        textBuilder.AppendLine("Cam Payload:");

        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId).AppendLine();
        textBuilder.Append("seq_no=").Append(payload.SeqNo).AppendLine();
        textBuilder.Append("pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp)).AppendLine();
        textBuilder.Append("driver_state=").Append(payload.DriverState).AppendLine();
        textBuilder.Append("ret_code=").Append(payload.RetCode).AppendLine();

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append("driver_err_msg=").Append(payload.DriverErrMsg).AppendLine();
        }

        textBuilder.Append("pick_state=").Append(payload.PickState).AppendLine();

        _log.Info(textBuilder.ToString());

        // Input Value
        _cam_pick_state = payload.PickState;
    }

    private void LidarPayloadHandler(LidarResult payload)
    {
        var textBuilder = new StringBuilder(512);

        textBuilder.AppendLine("Lidar Payload:");

        textBuilder.Append("driver_instance_id=").Append(payload.DriverInstanceId).AppendLine();
        textBuilder.Append("seq_no=").Append(payload.SeqNo).AppendLine();
        textBuilder.Append("pub_timestamp=").Append(FormatTimestamp(payload.PubTimestamp)).AppendLine();
        textBuilder.Append("driver_state=").Append(payload.DriverState).AppendLine();
        textBuilder.Append("ret_code=").Append(payload.RetCode).AppendLine();

        if (!string.IsNullOrEmpty(payload.DriverErrMsg))
        {
            textBuilder.Append("driver_err_msg=").Append(payload.DriverErrMsg).AppendLine();
        }

        textBuilder.Append("pick_state=").Append(payload.PickState).AppendLine();

        _log.Info(textBuilder.ToString());

        // Input Value
        _lidar_pick_state = payload.PickState;
    }

    private void VolumePayloadHandler(VolumeResult payload)
    {
        var textBuilder = new StringBuilder(512);

        textBuilder.AppendLine("Volume Measure Payload:");

        textBuilder.Append("Width=").Append(payload.Width).AppendLine();
        textBuilder.Append("Height=").Append(payload.Height).AppendLine();
        textBuilder.Append("Depth=").Append(payload.Depth).AppendLine();

        _log.Info(textBuilder.ToString());

        // Input Value
        _Target_distance_mm = payload.Distance;
    }

    private void TabletJsonPayloadHandler(TabletResData payload)
    {
        var textBuilder = new StringBuilder(1024);

        textBuilder.AppendLine("Tablet Payload:");

        // ===== TabletResData =====
        textBuilder.Append("res=").Append(payload.Res).AppendLine();
        textBuilder.Append("message=").Append(payload.Message ?? string.Empty).AppendLine();

        textBuilder.Append("workType=").Append(payload.WorkType).AppendLine();
        textBuilder.Append("measure=").Append(payload.Measure).AppendLine();
        textBuilder.Append("zeroSet=").Append(payload.ZeroSet).AppendLine();
        textBuilder.Append("enabled=").Append(payload.Enabled).AppendLine();
        textBuilder.Append("misloading=").Append(payload.Misloading).AppendLine();

        // ===== scanProductList =====
        var list = payload.ScanProductList;
        if (list is null || list.Count == 0)
        {
            textBuilder.Append("scanProductList_count=0").AppendLine();
            _log.Info(textBuilder.ToString());
            return;
        }

        textBuilder.Append("scanProductList_count=").Append(list.Count).AppendLine();

        for (int index = 0; index < list.Count; ++index)
        {
            var item = list[index];
            if (item is null)
            {
                textBuilder.Append("scanProductList[").Append(index).AppendLine("]=null");
                continue;
            }

            textBuilder.Append("scanProductList[").Append(index).AppendLine("]:");

            textBuilder.Append("  orderId=").Append(item.OrderId ?? string.Empty).AppendLine();
            textBuilder.Append("  boundNo=").Append(item.BoundNo ?? string.Empty).AppendLine();
            textBuilder.Append("  qrCode=").Append(item.QrCode ?? string.Empty).AppendLine();
            textBuilder.Append("  productId=").Append(item.ProductId ?? string.Empty).AppendLine();
            textBuilder.Append("  productCode=").Append(item.ProductCode ?? string.Empty).AppendLine();
            textBuilder.Append("  productName=").Append(item.ProductName ?? string.Empty).AppendLine();
            textBuilder.Append("  blno=").Append(item.Blno ?? string.Empty).AppendLine();
            textBuilder.Append("  lotNumber=").Append(item.LotNumber ?? string.Empty).AppendLine();
            textBuilder.Append("  inboundDate=").Append(item.InboundDate ?? string.Empty).AppendLine();
            textBuilder.Append("  house=").Append(item.House ?? string.Empty).AppendLine();

            textBuilder.Append("  quantity=").Append(item.Quantity?.ToString() ?? string.Empty).AppendLine();
            textBuilder.Append("  box=").Append(item.Box?.ToString() ?? string.Empty).AppendLine();
            textBuilder.Append("  ea=").Append(item.Ea?.ToString() ?? string.Empty).AppendLine();

            textBuilder.Append("  saveType=").Append(item.SaveType ?? string.Empty).AppendLine();
            textBuilder.Append("  etc=").Append(item.Etc ?? string.Empty).AppendLine();
            textBuilder.Append("  floor=").Append(item.Floor?.ToString() ?? string.Empty).AppendLine();

            textBuilder.Append("  status=").Append(item.Status ?? string.Empty).AppendLine();
            textBuilder.Append("  stat=").Append(item.Stat?.ToString() ?? string.Empty).AppendLine();
            textBuilder.Append("  currentCellName=").Append(item.CurrentCellName ?? string.Empty).AppendLine();
            textBuilder.Append("  targetCellName=").Append(item.TargetCellName ?? string.Empty).AppendLine();

            textBuilder.Append("  dockEpc=").Append(item.DockEpc ?? string.Empty).AppendLine();
            textBuilder.Append("  weight=").Append(item.Weight?.ToString() ?? string.Empty).AppendLine();

            textBuilder.Append("  height=").Append(item.Height?.ToString() ?? string.Empty).AppendLine();
            textBuilder.Append("  width=").Append(item.Width?.ToString() ?? string.Empty).AppendLine();
            textBuilder.Append("  depth=").Append(item.Depth?.ToString() ?? string.Empty).AppendLine();
        }

        _log.Info(textBuilder.ToString());
        
        // Input Value
        
        // 작업지시서에서 OCR 유사도분석을 성공했을 경우, 현재 시간을 기록하고 _scan_complete 를 true 으로 변경 
        if (1 == payload.Res && true != _scan_complete)
        {
            _scan_complete_time = DateTime.UtcNow;
            _scan_complete = true;
        }
        // payload.Res 가 1 이 아닐 경우, OCR 유사도 분석을 성공하지 못했으므로, _scan_complete 을 false 으로 변경 
        else if (1 != payload.Res)
        {
            _scan_complete = false;
        }
    }

    private bool TabletErrorHandler(string errorMessage)
    {
        // 여기서 errorMessage 내용에 따라:
        // - frame incomplete
        // - drop(other format)
        // - socket/recv error
        // 등을 식별 가능하게 로그/카운트하면 됨.

        _log.Warn($"Tablet subscriber error: {errorMessage}");

        // true = 계속 수신 유지
        // false = subscriber stop
        return true;
    }
    
    private static string FormatTimestamp(ulong unixNanoseconds)
    {
        const ulong NanosecondsPerSecond = 1_000_000_000UL;

        ulong secondsPart = unixNanoseconds / NanosecondsPerSecond;
        ulong remainingNanoseconds = unixNanoseconds % NanosecondsPerSecond;

        // long 범위 체크 (이론상 2262년까지 안전)
        if (secondsPart > long.MaxValue)
            return "timestamp_overflow";

        var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds((long)secondsPart)
            .AddTicks((long)(remainingNanoseconds / 100)); // 100ns = 1 tick

        return dateTimeOffset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}