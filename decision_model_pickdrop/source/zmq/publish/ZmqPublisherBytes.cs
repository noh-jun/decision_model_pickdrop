using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;
using NetMQ.Sockets;

namespace Zmq.Publish
{
    /// <summary>
    /// ZMQ PUB 소켓으로 payload 바이트를 송신한다.
    ///
    /// Subscriber와 동일한 정책/형태:
    /// - 생성자에서 즉시 Bind하지 않고 백그라운드 루프에서 Bind 재시도(실패해도 throw하지 않음)
    /// - Send 예외 발생 시 소켓 폐기 후 재바인드 재시도
    /// - 내부 큐 크기 제한 + 초과 시 oldest drop
    /// - on_error 미등록 시 기본 continue (subscriber 기본 정책과 동일)
    /// - Stop()으로 루프 종료 및 자원 정리
    ///
    /// 송신 프레임:
    /// - (topic frame, payload frame) 2-part 전송
    /// </summary>
    public sealed class ZmqPublisherBytes : IDisposable
    {
        public delegate bool ErrorCallback(string errorMessage);

        private const string ClassName = "ZmqPublisherBytes";

        // Stop 반영성을 위해 짧은 대기
        private static readonly TimeSpan BindRetryDelay = TimeSpan.FromMilliseconds(200);

        private readonly string endpoint_;
        private readonly string topic_;
        private readonly int maxQueueSize_;
        private readonly ErrorCallback? onError_;

        private readonly object socketLock_ = new object();
        private PublisherSocket? socket_;

        private readonly object queueLock_ = new object();
        private readonly Queue<byte[]> queue_ = new Queue<byte[]>();

        private volatile bool stopRequested_;
        private Task? sendTask_;

        public ZmqPublisherBytes(string endpoint, string topic, int maxQueueSize, ErrorCallback? onError = null)
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

            endpoint_ = endpoint;
            topic_ = topic;
            maxQueueSize_ = maxQueueSize;
            onError_ = onError;

            stopRequested_ = false;
            socket_ = null;

            // 중요: 생성자에서 Bind하지 않는다(실패해도 throw 금지). 루프에서 재시도.
            StartTasksOrThrow();
        }

        public string Endpoint => endpoint_;
        public string Topic => topic_;

        /// <summary>
        /// payload를 큐에 넣는다(즉시 송신하지 않음).
        /// 큐가 가득 차면 oldest drop.
        /// </summary>
        public void Publish(byte[] payload)
        {
            if (payload is null)
            {
                LogErrorPrefix("Invalid argument: payload is null");
                throw new ArgumentNullException(nameof(payload));
            }

            if (stopRequested_)
            {
                // Stop 이후에는 무시(throw 하지 않음)
                return;
            }

            EnqueueOrDropOldest(payload);
        }

        public void Stop()
        {
            stopRequested_ = true;

            lock (queueLock_)
            {
                Monitor.PulseAll(queueLock_);
            }

            // sendTask wait 시 현재 task에서 호출되면 교착 위험이 있어 회피
            var currentTaskId = Task.CurrentId;
            if (sendTask_ is not null && sendTask_.Id != currentTaskId)
            {
                try { sendTask_.Wait(); } catch { /* ignored */ }
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
                sendTask_ = Task.Run(SendLoop);
            }
            catch (Exception e)
            {
                LogExceptionPrefix("task start failed", e);
                stopRequested_ = true;
                lock (queueLock_)
                {
                    Monitor.PulseAll(queueLock_);
                }
                try { sendTask_?.Wait(); } catch { /* ignored */ }
                throw;
            }
        }

        private void SendLoop()
        {
            while (!stopRequested_)
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
                    if (!EnsureSocketBoundOrWait())
                    {
                        // stopRequested_가 true면 false가 될 수 있음
                        continue;
                    }

                    PublisherSocket socketSnapshot;
                    lock (socketLock_)
                    {
                        if (socket_ is null)
                        {
                            continue;
                        }
                        socketSnapshot = socket_;
                    }

                    // (topic frame, payload frame) 2-part
                    socketSnapshot
                        .SendMoreFrame(topic_)
                        .SendFrame(payload);
                }
                catch (Exception e)
                {
                    // socket 꼬임/전송 실패 가능: 폐기 후 재바인드 루프로 복귀
                    SafeDisposeSocket();

                    if (!ShouldContinueOnError("send failed (will rebind): " + e.Message))
                    {
                        stopRequested_ = true;
                        WakeSender();
                        return;
                    }

                    SleepWithStop(BindRetryDelay);
                }
            }
        }

        private bool EnsureSocketBoundOrWait()
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

            try
            {
                var newSocket = new PublisherSocket();

                // 필요 시 튜닝 (subscriber와 대칭)
                newSocket.Options.SendHighWatermark = 1000;

                // endpoint 형식이 잘못되면 예외 -> catch에서 정책 적용 후 재시도/중단
                newSocket.Bind(endpoint_);

                lock (socketLock_)
                {
                    if (stopRequested_)
                    {
                        try { newSocket.Dispose(); } catch { /* ignored */ }
                        return false;
                    }

                    socket_ = newSocket;
                    return true;
                }
            }
            catch (Exception e)
            {
                bool shouldContinue = ShouldContinueOnError("bind failed (will retry): " + e.Message);
                if (!shouldContinue)
                {
                    stopRequested_ = true;
                    WakeSender();
                    return false;
                }

                SleepWithStop(BindRetryDelay);
                return !stopRequested_;
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

        private void WakeSender()
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
            // Subscriber 기본 정책과 동일: on_error 미등록 시 continue
            if (onError_ is null)
            {
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
            try { return Environment.ProcessId; }
            catch { return Process.GetCurrentProcess().Id; }
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
            Console.Error.WriteLine($"[{ClassName}][endpoint={endpoint_}][topic={topic_}][pid={CurrentPid()}] {message}: {exception.Message}");
        }
    }
}