using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ethernet.Data;

public sealed class TabletResData
{
    // 0 : 정상 ; 1: 스캔 결과 ; 2: 재스캔 요청, 99 : Error
    [JsonPropertyName("res")] public int Res { get; set; }

    // 스캔 결과 리스트 (res 0일때 null)
    [JsonPropertyName("scanProductList")] public List<SdsProductData>? ScanProductList { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    // [통합 버전 추가된 컬럼]
    [JsonPropertyName("workType")] public int WorkType { get; set; }

    [JsonPropertyName("measure")] public int Measure { get; set; }

    [JsonPropertyName("zeroSet")] public int ZeroSet { get; set; }

    [JsonPropertyName("enabled")] public int Enabled { get; set; }

    [JsonPropertyName("misloading")] public int Misloading { get; set; }
}

public sealed class SdsProductData
{
    [JsonPropertyName("orderId")] public string? OrderId { get; set; }

    [JsonPropertyName("boundNo")] public string? BoundNo { get; set; }

    [JsonPropertyName("qrCode")] public string? QrCode { get; set; }

    [JsonPropertyName("productId")] public string? ProductId { get; set; }

    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }

    [JsonPropertyName("productName")] public string? ProductName { get; set; }

    [JsonPropertyName("blno")] public string? Blno { get; set; }

    [JsonPropertyName("lotNumber")] public string? LotNumber { get; set; }

    [JsonPropertyName("inboundDate")] public string? InboundDate { get; set; }

    [JsonPropertyName("house")] public string? House { get; set; }

    [JsonPropertyName("quantity")] public int? Quantity { get; set; }

    [JsonPropertyName("box")] public int? Box { get; set; }

    [JsonPropertyName("ea")] public int? Ea { get; set; }

    [JsonPropertyName("saveType")] public string? SaveType { get; set; }

    [JsonPropertyName("etc")] public string? Etc { get; set; }

    [JsonPropertyName("floor")] public int? Floor { get; set; }

    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("stat")] public int? Stat { get; set; }

    [JsonPropertyName("currentCellName")] public string? CurrentCellName { get; set; }

    [JsonPropertyName("targetCellName")] public string? TargetCellName { get; set; }

    // [추가사항]
    [JsonPropertyName("dockEpc")] public string? DockEpc { get; set; }

    [JsonPropertyName("weight")] public int? Weight { get; set; }

    [JsonPropertyName("height")] public float? Height { get; set; }

    [JsonPropertyName("width")] public float? Width { get; set; }

    [JsonPropertyName("depth")] public float? Depth { get; set; }
}
