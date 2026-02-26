using System;

namespace Ethernet.Subscribe;

/// <summary>
/// Raw byte chunk 공급자(Transport 추상화).
///
/// CONTRACT
/// - byte[] 단위로만 전달한다. (메시지/프레임 개념 없음)
/// - 큐/드랍/프레이밍/Deserialize 정책을 절대 포함하지 않는다.
/// - onChunk는 수신 스레드에서 직접 호출될 수 있다.
/// - onError의 반환(bool)을 존중하여 계속/중단을 결정한다.
/// </summary>
public interface IRawChunkSubscriber : IDisposable
{
    /// <summary>
    /// 수신을 시작한다.
    /// </summary>
    /// <param name="onChunk">수신된 raw byte chunk 콜백</param>
    /// <param name="onError">오류/notice 콜백. true=continue, false=stop</param>
    void Start(Action<byte[]> onChunk, Func<string, bool>? onError = null);

    /// <summary>
    /// 수신을 중단한다.
    /// </summary>
    void Stop();
}