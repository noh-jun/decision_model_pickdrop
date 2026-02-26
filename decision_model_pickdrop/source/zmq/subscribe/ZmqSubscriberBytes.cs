using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;
using NetMQ.Sockets;

namespace Zmq.Subscribe;

/// <summary>
/// ZMQ SUB 소켓으로 수신한 payload 바이트를 큐잉하고 콜백으로 전달한다.
///
/// 요구사항 반영:
/// - Publisher가 늦게 실행되어도 throw하지 않고 대기한다.
/// - Connect/Recv 예외 발생 시 소켓을 폐기하고 재연결을 재시도한다.
///
/// 동작 유지:
/// - (topic frame, payload frame) 2-part 수신
/// - topic 완전 일치 방어
/// - 내부 큐 크기 제한 + 초과 시 oldest drop
/// - 디스패치 루프: 큐에서 pop하여 on_message 호출
/// - 오류 발생 시 on_error 호출 후 true면 continue, false면 stop
/// </summary>
public sealed class ZmqSubscriberBytes : IDisposable
{
    public delegate void MessageCallback(byte[] payload);

    public delegate bool ErrorCallback(string errorMessage);

    private const string ClassName = "ZmqSubscriberBytes";

    // C++: rcvtimeo=100ms 대응 (Stop 반영성)
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(100);

    // Connect 실패/예외 후 재시도 지연
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(200);

    private readonly string endpoint_;
    private readonly string topic_;
    private readonly int maxQueueSize_;
    private readonly MessageCallback onMessage_;
    private readonly ErrorCallback? onError_;

    private readonly object socketLock_ = new object();
    private SubscriberSocket? socket_;

    private readonly object queueLock_ = new object();
    private readonly Queue<byte[]> queue_ = new Queue<byte[]>();

    private volatile bool stopRequested_;
    private Task? recvTask_;
    private Task? dispatchTask_;

    public ZmqSubscriberBytes(string endpoint, string topic, int maxQueueSize, MessageCallback onMessage,
        ErrorCallback? onError = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            LogErrorPrefix(endpoint, topic, "Invalid argument: endpoint is empty");
            throw new ArgumentException("endpoint is empty", nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            LogErrorPrefix(endpoint, topic, "Invalid argument: topic is empty");
            throw new ArgumentException("topic is empty", nameof(topic));
        }

        if (maxQueueSize <= 0)
        {
            LogErrorPrefix(endpoint, topic, "Invalid argument: max_queue_size must be > 0");
            throw new ArgumentOutOfRangeException(nameof(maxQueueSize), "max_queue_size must be > 0");
        }

        if (onMessage is null)
        {
            LogErrorPrefix(endpoint, topic, "Invalid argument: on_message callback is null");
            throw new ArgumentNullException(nameof(onMessage));
        }

        endpoint_ = endpoint;
        topic_ = topic;
        maxQueueSize_ = maxQueueSize;
        onMessage_ = onMessage;
        onError_ = onError;

        stopRequested_ = false;
        socket_ = null;

        // 중요: Publisher가 늦을 수 있으므로 생성자에서 Connect하지 않는다.
        StartTasksOrThrow();
    }

    public string Endpoint => endpoint_;
    public string Topic => topic_;

    public void Stop()
    {
        stopRequested_ = true;

        lock (queueLock_)
        {
            Monitor.PulseAll(queueLock_);
        }

        // recv가 timeout 폴링이므로 stop 반영된다.
        // task wait 시 현재 task에서 호출되면 교착 위험이 있어 회피한다.
        var currentTaskId = Task.CurrentId;

        if (recvTask_ is not null && recvTask_.Id != currentTaskId)
        {
            try
            {
                recvTask_.Wait();
            }
            catch
            {
                /* ignored */
            }
        }

        if (dispatchTask_ is not null && dispatchTask_.Id != currentTaskId)
        {
            try
            {
                dispatchTask_.Wait();
            }
            catch
            {
                /* ignored */
            }
        }

        SafeDisposeSocket();
    }

    public void Dispose()
    {
        Stop();
    }

    private void StartTasksOrThrow()
    {
        try
        {
            recvTask_ = Task.Run(RecvLoop);
            dispatchTask_ = Task.Run(DispatchLoop);
        }
        catch (Exception e)
        {
            LogExceptionPrefix("task start failed", e);
            stopRequested_ = true;
            lock (queueLock_)
            {
                Monitor.PulseAll(queueLock_);
            }

            try
            {
                recvTask_?.Wait();
            }
            catch
            {
                /* ignored */
            }

            try
            {
                dispatchTask_?.Wait();
            }
            catch
            {
                /* ignored */
            }

            throw;
        }
    }

    private void RecvLoop()
    {
        while (!stopRequested_)
        {
            try
            {
                if (!EnsureSocketConnectedOrWait())
                {
                    continue; // stopRequested_가 true면 false 반환될 수 있음
                }

                SubscriberSocket socketSnapshot;
                lock (socketLock_)
                {
                    if (socket_ is null)
                    {
                        continue;
                    }

                    socketSnapshot = socket_;
                }

                // (1) topic frame
                if (!socketSnapshot.TryReceiveFrameString(ReceiveTimeout, out var receivedTopic))
                {
                    continue; // timeout은 정상 대기
                }

                // (2) payload frame
                if (!socketSnapshot.TryReceiveFrameBytes(ReceiveTimeout, out var payload))
                {
                    // 비정상 멀티파트. 정책에 따라 continue/stop
                    if (!ShouldContinueOnError("recv(payload) timeout or missing frame"))
                    {
                        stopRequested_ = true;
                        WakeDispatcher();
                        return;
                    }

                    continue;
                }

                // subscribe는 prefix 매칭이므로 완전일치 방어
                if (!string.Equals(receivedTopic, topic_, StringComparison.Ordinal))
                {
                    continue;
                }

                EnqueueOrDropOldest(payload);
            }
            catch (Exception e)
            {
                // socket이 꼬였을 가능성: 폐기 후 재연결 루프로 복귀
                SafeDisposeSocket();

                if (!ShouldContinueOnError("recv failed (will reconnect): " + e.Message))
                {
                    stopRequested_ = true;
                    WakeDispatcher();
                    return;
                }

                SleepWithStop(ReconnectDelay);
            }
        }
    }

    private bool EnsureSocketConnectedOrWait()
    {
        if (stopRequested_)
        {
            return false;
        }

        lock (socketLock_)
        {
            if (socket_ is not null)
            {
                return true;
            }
        }

        // 아직 연결이 안 된 상태: 생성/연결 시도
        try
        {
            var newSocket = new SubscriberSocket();

            // topic 구독
            newSocket.Subscribe(topic_);

            // connect (publisher가 늦어도 connect 자체는 보통 성공한다)
            // endpoint 형식이 잘못되면 예외 -> 아래 catch에서 처리하고 재시도/중단 결정
            newSocket.Connect(endpoint_);

            lock (socketLock_)
            {
                // Stop 중이면 즉시 정리
                if (stopRequested_)
                {
                    try
                    {
                        newSocket.Dispose();
                    }
                    catch
                    {
                        /* ignored */
                    }

                    return false;
                }

                socket_ = newSocket;
                return true;
            }
        }
        catch (Exception e)
        {
            // connect 실패: throw 금지. 에러 콜백으로 통지 후 정책에 따라 대기/중단.
            bool shouldContinue = ShouldContinueOnError("connect failed (will retry): " + e.Message);
            if (!shouldContinue)
            {
                stopRequested_ = true;
                WakeDispatcher();
                return false;
            }

            SleepWithStop(ReconnectDelay);
            return !stopRequested_;
        }
    }

    private void DispatchLoop()
    {
        while (true)
        {
            byte[]? payload = null;

            lock (queueLock_)
            {
                while (!stopRequested_ && queue_.Count == 0)
                {
                    Monitor.Wait(queueLock_);
                }

                if (queue_.Count == 0)
                {
                    if (stopRequested_)
                    {
                        return;
                    }

                    continue;
                }

                payload = queue_.Dequeue();
            }

            try
            {
                onMessage_(payload);
            }
            catch (Exception e)
            {
                LogExceptionPrefix("on_message callback exception", e);

                if (!ShouldContinueOnError("on_message exception: " + e.Message))
                {
                    stopRequested_ = true;
                    WakeDispatcher();
                    return;
                }
            }
        }
    }

    private void EnqueueOrDropOldest(byte[] payload)
    {
        lock (queueLock_)
        {
            if (queue_.Count >= maxQueueSize_)
            {
                _ = queue_.Dequeue(); // oldest drop
            }

            queue_.Enqueue(payload);
            Monitor.Pulse(queueLock_);
        }
    }

    private void WakeDispatcher()
    {
        lock (queueLock_)
        {
            Monitor.PulseAll(queueLock_);
        }
    }

    private void SafeDisposeSocket()
    {
        lock (socketLock_)
        {
            if (socket_ is null)
            {
                return;
            }

            try
            {
                socket_.Dispose();
            }
            catch (Exception e)
            {
                LogExceptionPrefix("socket dispose failed (ignored)", e);
            }
            finally
            {
                socket_ = null;
            }
        }
    }

    private bool ShouldContinueOnError(string errorMessage)
    {
        // 요구사항: publisher 늦음 등으로 subscriber가 죽지 않도록 기본은 continue
        if (onError_ is null)
        {
            // stderr 로는 남겨준다(디버깅 용이)
            LogErrorPrefix(errorMessage);
            return true;
        }

        try
        {
            return onError_(errorMessage);
        }
        catch (Exception e)
        {
            LogExceptionPrefix("on_error callback exception", e);
            return false;
        }
    }

    private void SleepWithStop(TimeSpan delay)
    {
        // Stop 반영성을 위해 짧게 쪼개서 sleep
        const int sliceMs = 50;
        int remainingMs = (int)delay.TotalMilliseconds;

        while (!stopRequested_ && remainingMs > 0)
        {
            int sleepMs = remainingMs < sliceMs ? remainingMs : sliceMs;
            Thread.Sleep(sleepMs);
            remainingMs -= sleepMs;
        }
    }

    private static int CurrentPid()
    {
        try
        {
            return Environment.ProcessId;
        }
        catch
        {
            return Process.GetCurrentProcess().Id;
        }
    }

    private void LogErrorPrefix(string message)
    {
        Console.Error.WriteLine($"[{ClassName}][endpoint={endpoint_}][topic={topic_}][pid={CurrentPid()}] {message}");
    }

    private static void LogErrorPrefix(string endpoint, string topic, string message)
    {
        Console.Error.WriteLine($"[{ClassName}][endpoint={endpoint}][topic={topic}][pid={CurrentPid()}] {message}");
    }

    private void LogExceptionPrefix(string message, Exception exception)
    {
        Console.Error.WriteLine(
            $"[{ClassName}][endpoint={endpoint_}][topic={topic_}][pid={CurrentPid()}] {message}: {exception.Message}");
    }
}
