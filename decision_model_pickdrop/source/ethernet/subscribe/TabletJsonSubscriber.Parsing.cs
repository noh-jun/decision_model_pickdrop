using System;
using System.Buffers;
using System.Text.Json;
using Ethernet.Data;

namespace Ethernet.Subscribe;

public sealed partial class TabletJsonSubscriber
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    // 파싱 1회 시도의 결과(다중 프레임 self-signal에 사용)
    internal enum ParseAttemptResult
    {
        NoData,
        DataButNoFullFrame,
        FrameDropped,
        FrameDispatched
    }

    // parser state (stream 지속 상태)
    private int scanIndex_ = 0;
    private int frameStartIndex_ = -1;
    private int braceDepth_ = 0;
    private bool inString_ = false;
    private bool escape_ = false;

    private void ResetParserState_NoLock()
    {
        scanIndex_ = 0;
        frameStartIndex_ = -1;
        braceDepth_ = 0;
        inString_ = false;
        escape_ = false;
    }

    private void ResetParserState()
    {
        ResetParserState_NoLock();
    }

    private ParseAttemptResult TryParseOneFrameAttempt()
    {
        byte[] snapshot = Array.Empty<byte>();
        int snapshotLength = 0;

        lock (ringLock_)
        {
            snapshotLength = ring_.Count;
            if (snapshotLength == 0)
            {
                ShouldContinueOnError("NOTICE: parser woke up but ring has no data.");
                return ParseAttemptResult.NoData;
            }

            snapshot = ArrayPool<byte>.Shared.Rent(snapshotLength);
            ring_.Peek(snapshot, 0, snapshotLength);
        }

        try
        {
            // 1) 시작 '{' 이전 garbage 제거 (braceDepth==0일 때만)
            if (braceDepth_ == 0 && frameStartIndex_ < 0 && scanIndex_ == 0)
            {
                int firstBrace = IndexOfByte(snapshot, snapshotLength, (byte)'{');
                if (firstBrace < 0)
                {
                    DiscardUpTo(snapshotLength);
                    ResetParserState();
                    ShouldContinueOnError("NOTICE: no '{' found in buffered data -> dropped all buffered bytes.");
                    return ParseAttemptResult.FrameDropped;
                }

                if (firstBrace > 0)
                {
                    DiscardUpTo(firstBrace);
                    ResetParserState();
                    ShouldContinueOnError($"NOTICE: dropped garbage header bytes={firstBrace} (before first '{{').");
                    return ParseAttemptResult.FrameDropped;
                }
            }

            // 2) 닫힌 객체(0→1→0) 찾기
            if (!TryFindNextFrame(snapshot, snapshotLength, out int frameEndIndexInclusive))
            {
                // 2-1) 미완성 프레임이 지속되는 경우(resync 휴리스틱)
                // braceDepth_ > 0 인 상태에서 "새 프레임 시작처럼 보이는 '{'"를 발견하면,
                // 이전 미완성 조각은 복구 불가로 보고 drop + reset 한다.
                if (braceDepth_ > 0)
                {
                    if (TryResyncOnNewFrameStart(snapshot, snapshotLength, out int newFrameStart))
                    {
                        DiscardUpTo(newFrameStart);
                        ResetParserState();
                        ShouldContinueOnError(
                            $"NOTICE: incomplete frame abandoned -> resync at new '{{' offset={newFrameStart}.");
                        return ParseAttemptResult.FrameDropped;
                    }
                }

                // 데이터는 있으나 프레임 미완성(요청사항: notice)
                ShouldContinueOnError(
                    $"NOTICE: data received but frame is incomplete (buffered={snapshotLength}, depth={braceDepth_}, inString={inString_}).");
                return ParseAttemptResult.DataButNoFullFrame;
            }

            int frameLength = frameEndIndexInclusive - frameStartIndex_ + 1;
            if (frameStartIndex_ < 0 || frameLength <= 0)
            {
                DiscardUpTo(frameEndIndexInclusive + 1);
                ResetParserState();
                ShouldContinueOnError("NOTICE: invalid frame indices -> dropped candidate frame.");
                return ParseAttemptResult.FrameDropped;
            }

            ReadOnlySpan<byte> frameBytes = new ReadOnlySpan<byte>(snapshot, frameStartIndex_, frameLength);

            // 3) 닫힌 객체 감지됨: Parse 실패 / res 없음이면 “쓸모없음” → drop
            if (!TryParseAndDispatch(frameBytes))
            {
                DiscardUpTo(frameEndIndexInclusive + 1);
                ResetParserState();
                return ParseAttemptResult.FrameDropped;
            }

            DiscardUpTo(frameEndIndexInclusive + 1);
            ResetParserState();
            return ParseAttemptResult.FrameDispatched;
        }
        finally
        {
            if (snapshot.Length != 0)
                ArrayPool<byte>.Shared.Return(snapshot);
        }
    }

    private bool TryFindNextFrame(byte[] snapshot, int length, out int frameEndIndexInclusive)
    {
        frameEndIndexInclusive = -1;

        for (int i = scanIndex_; i < length; ++i)
        {
            byte b = snapshot[i];

            if (inString_)
            {
                if (escape_)
                {
                    escape_ = false;
                    continue;
                }

                if (b == (byte)'\\')
                {
                    escape_ = true;
                    continue;
                }

                if (b == (byte)'"')
                {
                    inString_ = false;
                    continue;
                }

                continue;
            }

            if (b == (byte)'"')
            {
                inString_ = true;
                continue;
            }

            if (b == (byte)'{')
            {
                if (braceDepth_ == 0)
                    frameStartIndex_ = i;

                braceDepth_++;
                continue;
            }

            if (b == (byte)'}')
            {
                if (braceDepth_ > 0)
                {
                    braceDepth_--;
                    if (braceDepth_ == 0 && frameStartIndex_ >= 0)
                    {
                        frameEndIndexInclusive = i;
                        scanIndex_ = i + 1;
                        return true;
                    }
                }

                continue;
            }
        }

        scanIndex_ = length;
        return false;
    }

    // 미완성 프레임에서 복구 불가로 판단하고 새 프레임 시작점을 찾는 휴리스틱
    // - braceDepth_ > 0 인 상태에서 '{'를 찾고,
    // - 그 '{' 이후 짧은 구간(ResLookaheadBytes) 내에 '"res"' 토큰이 보이면 새 프레임 시작으로 간주
    private bool TryResyncOnNewFrameStart(byte[] snapshot, int length, out int newFrameStartIndex)
    {
        const int ResLookaheadBytes = 128;

        newFrameStartIndex = -1;

        // frameStartIndex_는 현재 미완성 프레임의 시작을 가리킴
        int start = frameStartIndex_ >= 0 ? frameStartIndex_ + 1 : 0;

        for (int i = Math.Max(start, 0); i < length; ++i)
        {
            if (snapshot[i] != (byte)'{')
                continue;

            int lookaheadEnd = Math.Min(length, i + ResLookaheadBytes);
            if (ContainsResToken(snapshot, i, lookaheadEnd))
            {
                newFrameStartIndex = i;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsResToken(byte[] data, int startInclusive, int endExclusive)
    {
        // 매우 단순한 토큰 탐지: "res"
        // (문자열 내부인지까지 완벽히 판별하진 않지만, 프로토콜 상 res는 상위 초반에 나오는 전제 하에서 실용적)
        for (int i = startInclusive; i + 4 < endExclusive; ++i)
        {
            if (data[i] == (byte)'"' &&
                data[i + 1] == (byte)'r' &&
                data[i + 2] == (byte)'e' &&
                data[i + 3] == (byte)'s' &&
                data[i + 4] == (byte)'"')
            {
                return true;
            }
        }

        return false;
    }

    private bool TryParseAndDispatch(ReadOnlySpan<byte> frameBytes)
    {
        try
        {
            byte[] jsonBytes = frameBytes.ToArray();
            using JsonDocument document = JsonDocument.Parse(jsonBytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                ShouldContinueOnError("NOTICE: frame dropped (root is not object).");
                return false;
            }

            if (!document.RootElement.TryGetProperty("res", out JsonElement resElement))
            {
                ShouldContinueOnError("NOTICE: frame dropped (missing 'res') -> other format or header dropped.");
                return false;
            }

            if (resElement.ValueKind != JsonValueKind.Number)
            {
                ShouldContinueOnError("NOTICE: frame dropped ('res' is not number).");
                return false;
            }

            TabletResData? message = JsonSerializer.Deserialize<TabletResData>(
                document.RootElement,
                JsonOptions);

            if (message is null)
            {
                ShouldContinueOnError("NOTICE: frame dropped (deserialize returned null).");
                return false;
            }

            onMessage_(message);
            return true;
        }
        catch (JsonException jsonException)
        {
            ShouldContinueOnError("NOTICE: frame dropped (json parse failed): " + jsonException.Message);
            return false;
        }
        catch (Exception exception)
        {
            ShouldContinueOnError("NOTICE: frame dropped (dispatch failed): " + exception.Message);
            return false;
        }
    }

    private void DiscardUpTo(int countToDiscard)
    {
        if (countToDiscard <= 0)
            return;

        lock (ringLock_)
        {
            int discard = Math.Min(countToDiscard, ring_.Count);
            ring_.Discard(discard);
        }
    }

    private static int IndexOfByte(byte[] data, int length, byte value)
    {
        for (int i = 0; i < length; ++i)
        {
            if (data[i] == value)
                return i;
        }

        return -1;
    }
}