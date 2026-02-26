using System;
using MessagePack;

namespace Zmq.Publish
{
    /// <summary>
    /// MsgPack 기반 Publisher (단일 타입 전용).
    ///
    /// - ZmqPublisherBytes로 bytes 전송
    /// - 직렬화 예외/bytes layer 예외는 throw하지 않고 on_error 정책(true/false)으로 continue/stop 제어
    /// - on_error 미등록 시 오류 발생 시 stop (subscriber MsgPack 계층과 동일한 패턴)
    /// </summary>
    public sealed class MsgPackPublisher<T> : IDisposable
    {
        public delegate bool ErrorCallback(string errorMessage);

        private const string ClassName = "MsgPackPublisher";

        private readonly ErrorCallback? onError_;
        private readonly ZmqPublisherBytes bytesPublisher_;

        public MsgPackPublisher(
            string endpoint,
            string topic,
            int maxQueueSize,
            ErrorCallback? onError = null)
        {
            onError_ = onError;

            bytesPublisher_ = new ZmqPublisherBytes(
                endpoint,
                topic,
                maxQueueSize,
                errorMessage => OnBytesError(errorMessage));
        }

        public string Endpoint => bytesPublisher_.Endpoint;
        public string Topic => bytesPublisher_.Topic;

        public void Publish(T message)
        {
            // 외부 API: null은 즉시 예외(입력 계약 위반)
            if (message == null)
            {
                LogErrorPrefix("Invalid argument: message is null");
                throw new ArgumentNullException(nameof(message));
            }

            // 내부 루프 스레드가 아니므로, 여기서는 serialize 예외를 잡아서 정책 적용
            try
            {
                byte[] payload = MessagePackSerializer.Serialize(message);
                bytesPublisher_.Publish(payload);
            }
            catch (Exception e)
            {
                LogExceptionPrefix("msgpack serialize failed", e);
                string errorMessage = "msgpack serialize failed: " + e.Message;

                if (onError_ is null)
                {
                    bytesPublisher_.Stop();
                    return;
                }

                bool shouldContinue = OnBytesError(errorMessage);
                if (!shouldContinue)
                {
                    bytesPublisher_.Stop();
                }
            }
        }

        public void Stop()
        {
            bytesPublisher_.Stop();
        }

        public void Dispose()
        {
            bytesPublisher_.Dispose();
        }

        private bool OnBytesError(string errorMessage)
        {
            // bytes layer error: 그대로 상위 error callback으로 전달
            if (onError_ is null)
            {
                // 등록 없으면 stop
                return false;
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

        private static int CurrentPid()
        {
            try { return Environment.ProcessId; }
            catch { return System.Diagnostics.Process.GetCurrentProcess().Id; }
        }

        private void LogErrorPrefix(string message)
        {
            Console.Error.WriteLine($"[{ClassName}][endpoint={Endpoint}][topic={Topic}][pid={CurrentPid()}] {message}");
        }

        private void LogExceptionPrefix(string message, Exception exception)
        {
            Console.Error.WriteLine($"[{ClassName}][endpoint={Endpoint}][topic={Topic}][pid={CurrentPid()}] {message}: {exception.Message}");
        }
    }
}