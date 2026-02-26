using System;
using MessagePack;

namespace Zmq.Subscribe
{
    /// <summary>
    /// MsgPack 기반 Subscriber (단일 타입 전용).
    ///
    /// - ZmqSubscriberBytes에서 받은 payload를 MessagePack으로 역직렬화하여 T로 변환
    /// - 사용자 on_message(T) 호출
    /// - 역직렬화/콜백 예외는 throw하지 않고 on_error 정책(true/false)으로 continue/stop 제어
    /// - on_error 미등록 시 오류 발생 시 stop
    /// </summary>
    public sealed class MsgPackSubscriber<T> : IDisposable
    {
        public delegate void MessageCallback(T message);
        public delegate bool ErrorCallback(string errorMessage);

        private const string ClassName = "MsgPackSubscriber";

        private readonly MessageCallback onMessage_;
        private readonly ErrorCallback? onError_;
        private readonly ZmqSubscriberBytes bytesSubscriber_;

        public MsgPackSubscriber(
            string endpoint,
            string topic,
            int maxQueueSize,
            MessageCallback onMessage,
            ErrorCallback? onError = null)
        {
            if (onMessage is null)
            {
                LogErrorPrefix(endpoint, topic, "Invalid argument: on_message callback is null");
                throw new ArgumentNullException(nameof(onMessage));
            }

            onMessage_ = onMessage;
            onError_ = onError;

            bytesSubscriber_ = new ZmqSubscriberBytes(
                endpoint,
                topic,
                maxQueueSize,
                payload => OnBytesMessage(payload),
                errorMessage => OnBytesError(errorMessage));
        }

        public string Endpoint => bytesSubscriber_.Endpoint;
        public string Topic => bytesSubscriber_.Topic;

        public void Stop()
        {
            bytesSubscriber_.Stop();
        }

        public void Dispose()
        {
            bytesSubscriber_.Dispose();
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

        private void OnBytesMessage(byte[] payload)
        {
            // 스레드 내부이므로 throw 금지. 모든 예외는 stderr + error callback으로 제어.
            try
            {
                // C++ msgpack::unpack + object.as<T>() 대응
                // MessagePack-CSharp는 타입에 맞는 formatter(속성/리졸버)이 필요할 수 있음.
                T message = MessagePackSerializer.Deserialize<T>(payload);

                onMessage_(message);
            }
            catch (Exception e)
            {
                LogExceptionPrefix("msgpack deserialize or on_message failed", e);
                string errorMessage = "msgpack deserialize/on_message failed: " + e.Message;

                if (onError_ is null)
                {
                    bytesSubscriber_.Stop();
                    return;
                }

                bool shouldContinue = OnBytesError(errorMessage);
                if (!shouldContinue)
                {
                    bytesSubscriber_.Stop();
                }
            }
        }

        private static int CurrentPid()
        {
            try { return Environment.ProcessId; }
            catch { return System.Diagnostics.Process.GetCurrentProcess().Id; }
        }

        private static void LogErrorPrefix(string endpoint, string topic, string message)
        {
            Console.Error.WriteLine($"[{ClassName}][endpoint={endpoint}][topic={topic}][pid={CurrentPid()}] {message}");
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
