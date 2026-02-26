using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ethernet.Subscribe;

public sealed class TcpSubscriberBytes : IDisposable
{
    public delegate void MessageCallback(byte[] payloadChunk);

    public delegate bool ErrorCallback(string errorMessage);

    private const string ClassName = "TcpSubscriberBytes";

    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(200);

    private readonly IPAddress bindAddress_;
    private readonly int port_;
    private readonly int receiveBufferSize_;
    private readonly MessageCallback onMessage_;
    private readonly ErrorCallback? onError_;

    private readonly object listenerLock_ = new object();
    private TcpListener? listener_;

    private readonly object clientLock_ = new object();
    private Socket? clientSocket_;

    private volatile bool stopRequested_;
    private Task? acceptAndRecvTask_;

    public TcpSubscriberBytes(
        string bindIp,
        int port,
        int receiveBufferSize,
        MessageCallback onMessage,
        ErrorCallback? onError = null)
    {
        if (!IPAddress.TryParse(bindIp, out var ip))
            throw new ArgumentException("Invalid bindIp", nameof(bindIp));

        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        if (receiveBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiveBufferSize));

        bindAddress_ = ip;
        port_ = port;
        receiveBufferSize_ = receiveBufferSize;
        onMessage_ = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
        onError_ = onError;

        StartTaskOrThrow();
    }

    public void Stop()
    {
        stopRequested_ = true;

        SafeCloseClient();
        SafeStopListener();

        var currentTaskId = Task.CurrentId;
        if (acceptAndRecvTask_ is not null && acceptAndRecvTask_.Id != currentTaskId)
        {
            try
            {
                acceptAndRecvTask_.Wait();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void StartTaskOrThrow()
    {
        try
        {
            acceptAndRecvTask_ = Task.Run(AcceptAndRecvLoop);
        }
        catch (Exception e)
        {
            ShouldContinueOnError("Task start failed: " + e.Message);
            throw;
        }
    }

    private void AcceptAndRecvLoop()
    {
        var recvBuffer = new byte[receiveBufferSize_];

        while (!stopRequested_)
        {
            try
            {
                EnsureListenerStarted();

                Socket client = AcceptClient();
                ConfigureClientSocket(client);
                SetClientSocket(client);

                while (!stopRequested_)
                {
                    int receivedBytes;

                    try
                    {
                        receivedBytes = client.Receive(recvBuffer, 0, recvBuffer.Length, SocketFlags.None);
                    }
                    catch (SocketException se)
                    {
                        if (se.SocketErrorCode == SocketError.TimedOut)
                            continue;

                        throw;
                    }

                    if (receivedBytes == 0)
                    {
                        SafeCloseClient();
                        break;
                    }

                    var chunk = new byte[receivedBytes];
                    Buffer.BlockCopy(recvBuffer, 0, chunk, 0, receivedBytes);

                    onMessage_(chunk);
                }
            }
            catch (OperationCanceledException)
            {
                stopRequested_ = true;
                return;
            }
            catch (Exception e)
            {
                SafeCloseClient();

                if (!ShouldContinueOnError("TCP error: " + e.Message))
                {
                    stopRequested_ = true;
                    return;
                }

                SleepWithStop(ReconnectDelay);
            }
        }
    }

    private void EnsureListenerStarted()
    {
        lock (listenerLock_)
        {
            if (listener_ != null)
                return;

            listener_ = new TcpListener(bindAddress_, port_);
            listener_.Start(1);
        }
    }

    private Socket AcceptClient()
    {
        TcpListener? listenerSnapshot;
        lock (listenerLock_)
            listenerSnapshot = listener_;

        if (listenerSnapshot == null)
            throw new InvalidOperationException("Listener not started");

        while (!stopRequested_)
        {
            if (listenerSnapshot.Pending())
                return listenerSnapshot.AcceptSocket();

            Thread.Sleep(50);
        }

        throw new OperationCanceledException();
    }

    private void ConfigureClientSocket(Socket client)
    {
        client.ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds;
        client.NoDelay = true;
    }

    private void SetClientSocket(Socket client)
    {
        lock (clientLock_)
        {
            clientSocket_?.Close();
            clientSocket_ = client;
        }
    }

    private void SafeCloseClient()
    {
        lock (clientLock_)
        {
            if (clientSocket_ == null)
                return;

            try
            {
                try
                {
                    clientSocket_.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }

                clientSocket_.Close();
            }
            catch (Exception e)
            {
                ShouldContinueOnError("Client close failed: " + e.Message);
            }
            finally
            {
                clientSocket_ = null;
            }
        }
    }

    private void SafeStopListener()
    {
        lock (listenerLock_)
        {
            if (listener_ == null)
                return;

            try
            {
                listener_.Stop();
            }
            catch (Exception e)
            {
                ShouldContinueOnError("Listener stop failed: " + e.Message);
            }
            finally
            {
                listener_ = null;
            }
        }
    }
    
    private bool ShouldContinueOnError(string errorMessage)
    {
        if (onError_ == null)
        {
            Console.Error.WriteLine(
                $"[{ClassName}][bind={bindAddress_}:{port_}][pid={Environment.ProcessId}] {errorMessage}");
            return true;
        }

        try
        {
            return onError_(errorMessage);
        }
        catch (Exception callbackException)
        {
            Console.Error.WriteLine(
                $"[{ClassName}][bind={bindAddress_}:{port_}][pid={Environment.ProcessId}] on_error callback exception: {callbackException.Message}");
            return false;
        }
    }

    private void SleepWithStop(TimeSpan delay)
    {
        const int sliceMs = 50;
        int remainingMs = (int)delay.TotalMilliseconds;

        while (!stopRequested_ && remainingMs > 0)
        {
            int sleepMs = Math.Min(sliceMs, remainingMs);
            Thread.Sleep(sleepMs);
            remainingMs -= sleepMs;
        }
    }
}
