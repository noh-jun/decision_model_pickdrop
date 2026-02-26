// using System;
// using System.Threading;
// using Ethernet.Data;
//
// namespace Ethernet.Subscribe;
//
// public sealed partial class TabletJsonSubscriber : IDisposable
// {
//     public delegate void MessageCallback(TabletResData message);
//
//     public delegate bool ErrorCallback(string errorMessage);
//
//     private const string ClassName = "TabletJsonSubscriber";
//
//     // TcpSubscriberBytes의 1회 recv 최대치(외부 주입 금지)
//     private const int ReceiveBufferSize = 4096;
//
//     private readonly TcpSubscriberBytes tcpSubscriber_;
//     private readonly MessageCallback onMessage_;
//     private readonly ErrorCallback? onError_;
//
//     private readonly RingByteBuffer ring_;
//     private readonly object ringLock_ = new object();
//
//     private readonly AutoResetEvent dataArrivedEvent_ = new AutoResetEvent(false);
//     private readonly Thread parserThread_;
//     private volatile bool stopRequested_ = false;
//
//     private readonly string bindIp_;
//     private readonly int port_;
//
//     public TabletJsonSubscriber(
//         string bindIp,
//         int port,
//         int byteBufferSize,
//         MessageCallback onMessage,
//         ErrorCallback? onError = null)
//     {
//         if (onMessage is null)
//             throw new ArgumentNullException(nameof(onMessage));
//
//         if (byteBufferSize <= ReceiveBufferSize)
//         {
//             throw new ArgumentOutOfRangeException(
//                 nameof(byteBufferSize),
//                 $"byteBufferSize({byteBufferSize}) must be greater than receiveBufferSize({ReceiveBufferSize}).");
//         }
//
//         bindIp_ = bindIp;
//         port_ = port;
//
//         onMessage_ = onMessage;
//         onError_ = onError;
//
//         ring_ = new RingByteBuffer(byteBufferSize);
//
//         parserThread_ = new Thread(ParserLoop)
//         {
//             IsBackground = true,
//             Name = "TabletJsonSubscriber.Parser"
//         };
//         parserThread_.Start();
//
//         tcpSubscriber_ = new TcpSubscriberBytes(
//             bindIp: bindIp,
//             port: port,
//             receiveBufferSize: ReceiveBufferSize,
//             onMessage: OnTcpChunk,
//             onError: onError is null ? null : (TcpSubscriberBytes.ErrorCallback)(msg => onError(msg)));
//     }
//
//     public void Stop()
//     {
//         stopRequested_ = true;
//         dataArrivedEvent_.Set();
//
//         tcpSubscriber_.Stop();
//
//         if (Thread.CurrentThread.ManagedThreadId != parserThread_.ManagedThreadId)
//         {
//             try
//             {
//                 parserThread_.Join();
//             }
//             catch
//             {
//                 /* ignored */
//             }
//         }
//     }
//
//     public void Dispose()
//     {
//         Stop();
//         dataArrivedEvent_.Dispose();
//     }
//
//     private void OnTcpChunk(byte[] payloadChunk)
//     {
//         if (payloadChunk is null || payloadChunk.Length == 0)
//             return;
//
//         int droppedBytes = 0;
//
//         lock (ringLock_)
//         {
//             int neededSpace = payloadChunk.Length - ring_.FreeSpace;
//             if (neededSpace > 0)
//             {
//                 ring_.Discard(neededSpace);
//                 droppedBytes = neededSpace;
//
//                 // oldest drop은 스트림 동기 깨짐 가능성이 높으므로,
//                 // 즉시 파서 상태를 reset하여 resync 모드로 전환
//                 ResetParserState_NoLock();
//             }
//
//             ring_.Write(payloadChunk, 0, payloadChunk.Length);
//         }
//
//         if (droppedBytes > 0)
//         {
//             ShouldContinueOnError(
//                 $"NOTICE: ring overflow -> oldest drop bytes={droppedBytes} (bind={bindIp_}:{port_})");
//         }
//
//         dataArrivedEvent_.Set();
//     }
//
//     private void ParserLoop()
//     {
//         while (true)
//         {
//             dataArrivedEvent_.WaitOne();
//
//             if (stopRequested_)
//                 return;
//
//             try
//             {
//                 // 1회 시도
//                 ParseAttemptResult result = TryParseOneFrameAttempt();
//
//                 // 다중 프레임 처리:
//                 // 이번 시도에서 "닫힌 객체를 소비"했다면(Dispatch or Drop),
//                 // ring에 데이터가 남아있을 때 스스로 다시 깨워서 다음 프레임을 처리한다.
//                 if (result == ParseAttemptResult.FrameDispatched ||
//                     result == ParseAttemptResult.FrameDropped)
//                 {
//                     int remaining;
//                     lock (ringLock_)
//                     {
//                         remaining = ring_.Count;
//                     }
//
//                     if (remaining > 0)
//                     {
//                         dataArrivedEvent_.Set();
//                     }
//                 }
//             }
//             catch (Exception exception)
//             {
//                 ShouldContinueOnError("NOTICE: ParserLoop exception: " + exception.Message);
//             }
//         }
//     }
//
//     private bool ShouldContinueOnError(string errorMessage)
//     {
//         if (onError_ == null)
//         {
//             Console.Error.WriteLine(
//                 $"[{ClassName}][bind={bindIp_}:{port_}][pid={Environment.ProcessId}] {errorMessage}");
//             return true;
//         }
//
//         try
//         {
//             return onError_(errorMessage);
//         }
//         catch (Exception callbackException)
//         {
//             Console.Error.WriteLine(
//                 $"[{ClassName}][bind={bindIp_}:{port_}][pid={Environment.ProcessId}] on_error callback exception: {callbackException.Message}");
//             return false;
//         }
//     }
//
//     private sealed class RingByteBuffer
//     {
//         private readonly byte[] buffer_;
//         private int head_ = 0;
//         private int tail_ = 0;
//         private int count_ = 0;
//
//         public RingByteBuffer(int capacity)
//         {
//             if (capacity <= 0)
//                 throw new ArgumentOutOfRangeException(nameof(capacity));
//
//             buffer_ = new byte[capacity];
//         }
//
//         public int Capacity => buffer_.Length;
//         public int Count => count_;
//         public int FreeSpace => buffer_.Length - count_;
//
//         public void Write(byte[] src, int offset, int length)
//         {
//             if (length <= 0)
//                 return;
//
//             if (length > FreeSpace)
//                 throw new InvalidOperationException("Not enough space in ring buffer.");
//
//             int remaining = length;
//             while (remaining > 0)
//             {
//                 int rightSpace = buffer_.Length - head_;
//                 int toCopy = Math.Min(rightSpace, remaining);
//
//                 Buffer.BlockCopy(src, offset + (length - remaining), buffer_, head_, toCopy);
//
//                 head_ = (head_ + toCopy) % buffer_.Length;
//                 count_ += toCopy;
//                 remaining -= toCopy;
//             }
//         }
//
//         public void Peek(byte[] dst, int dstOffset, int length)
//         {
//             if (length <= 0)
//                 return;
//
//             if (length > count_)
//                 throw new ArgumentOutOfRangeException(nameof(length));
//
//             int remaining = length;
//             int readPos = tail_;
//
//             while (remaining > 0)
//             {
//                 int rightSpace = buffer_.Length - readPos;
//                 int toCopy = Math.Min(rightSpace, remaining);
//
//                 Buffer.BlockCopy(buffer_, readPos, dst, dstOffset + (length - remaining), toCopy);
//
//                 readPos = (readPos + toCopy) % buffer_.Length;
//                 remaining -= toCopy;
//             }
//         }
//
//         public void Discard(int length)
//         {
//             if (length <= 0)
//                 return;
//
//             if (length > count_)
//                 length = count_;
//
//             tail_ = (tail_ + length) % buffer_.Length;
//             count_ -= length;
//
//             if (count_ == 0)
//             {
//                 head_ = 0;
//                 tail_ = 0;
//             }
//         }
//     }
// }