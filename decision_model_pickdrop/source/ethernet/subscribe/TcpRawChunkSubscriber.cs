using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ethernet.Subscribe;

public sealed class TcpRawChunkSubscriber : IRawChunkSubscriber
{
    private const string ClassName = "TcpRawChunkSubscriber";

    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(200);

    private readonly IPAddress bindAddress_;
    private readonly int port_;
    private readonly int receiveBufferSize_;

    private Action<byte[]>? onChunk_;
    private Func<string, bool>? onError_;

    private readonly object listenerLock_ = new();
    private TcpListener? listener_;

    private readonly object clientLock_ = new();
    private Socket? clientSocket_;

    private volatile bool stopRequested_;
    private Task? acceptAndRecvTask_;
    private bool started_;

    public TcpRawChunkSubscriber(string bindIp, int port, int receiveBufferSize)
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
    }

    public void Start(Action<byte[]> onChunk, Func<string, bool>? onError = null)
    {
        if (onChunk is null)
            throw new ArgumentNullException(nameof(onChunk));

        if (started_)
            return;

        onChunk_ = onChunk;
        onError_ = onError;

        stopRequested_ = false;
        acceptAndRecvTask_ = Task.Run(AcceptAndRecvLoop);
        started_ = true;
    }

    public void Stop()
    {
        if (!started_)
            return;

        stopRequested_ = true;

        SafeCloseClient();
        SafeStopListener();

        try
        {
            acceptAndRecvTask_?.Wait();
        }
        catch
        {
        }

        acceptAndRecvTask_ = null;
        started_ = false;
        onChunk_ = null;
        onError_ = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void AcceptAndRecvLoop()
    {
        var recvBuffer = new byte[receiveBufferSize_];

        while (!stopRequested_)
        {
            try
            {
                EnsureListenerStarted();
                EnsureClientConnected();

                while (!stopRequested_)
                {
                    TryReplaceClientIfNewConnectionPending();

                    Socket? client = GetClientSocketSnapshot();
                    if (client == null)
                        break;

                    int receivedBytes;
                    try
                    {
                        receivedBytes = client.Receive(
                            recvBuffer, 0, recvBuffer.Length, SocketFlags.None);
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode == SocketError.TimedOut)
                            continue;

                        SafeCloseClient();
                        if (!ShouldContinueOnError(
                                "NOTICE: TCP receive failed: " + ex.Message))
                        {
                            stopRequested_ = true;
                            return;
                        }

                        break;
                    }

                    if (receivedBytes == 0)
                    {
                        SafeCloseClient();
                        if (!ShouldContinueOnError(
                                "NOTICE: TCP client disconnected."))
                        {
                            stopRequested_ = true;
                            return;
                        }

                        break;
                    }

                    var chunk = new byte[receivedBytes];
                    Buffer.BlockCopy(recvBuffer, 0, chunk, 0, receivedBytes);

                    try
                    {
                        onChunk_?.Invoke(chunk);
                    }
                    catch (Exception callbackEx)
                    {
                        if (!ShouldContinueOnError(
                                "NOTICE: onChunk callback exception: " +
                                callbackEx.Message))
                        {
                            stopRequested_ = true;
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SafeCloseClient();

                if (!ShouldContinueOnError(
                        "NOTICE: TCP loop exception: " + ex.Message))
                {
                    stopRequested_ = true;
                    return;
                }

                SleepWithStop(ReconnectDelay);
            }
        }
    }

    private void EnsureClientConnected()
    {
        if (GetClientSocketSnapshot() != null)
            return;

        Socket newClient = AcceptClient();
        ConfigureClientSocket(newClient);
        ReplaceClientSocket(newClient);
    }

    private void TryReplaceClientIfNewConnectionPending()
    {
        TcpListener? listenerSnapshot;
        lock (listenerLock_)
            listenerSnapshot = listener_;

        if (listenerSnapshot == null)
            return;

        if (!listenerSnapshot.Pending())
            return;

        Socket newClient = listenerSnapshot.AcceptSocket();
        ConfigureClientSocket(newClient);
        ReplaceClientSocket(newClient);

        ShouldContinueOnError(
            "NOTICE: new client connected -> previous client dropped.");
    }

    private Socket? GetClientSocketSnapshot()
    {
        lock (clientLock_)
            return clientSocket_;
    }

    private void ReplaceClientSocket(Socket newClient)
    {
        lock (clientLock_)
        {
            if (clientSocket_ != null)
            {
                CloseAndDisposeSocket(clientSocket_);
            }

            clientSocket_ = newClient;
        }
    }

    private void SafeCloseClient()
    {
        lock (clientLock_)
        {
            if (clientSocket_ == null)
                return;

            CloseAndDisposeSocket(clientSocket_);
            clientSocket_ = null;
        }
    }

    private void CloseAndDisposeSocket(Socket socket)
    {
        try
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            socket.Dispose();
        }
        catch (Exception ex)
        {
            ShouldContinueOnError(
                "Socket close failed: " + ex.Message);
        }
    }

    private void ConfigureClientSocket(Socket client)
    {
        client.ReceiveTimeout =
            (int)ReceiveTimeout.TotalMilliseconds;
        client.NoDelay = true;
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
            catch (Exception ex)
            {
                ShouldContinueOnError(
                    "Listener stop failed: " + ex.Message);
            }
            finally
            {
                listener_ = null;
            }
        }
    }

    private bool ShouldContinueOnError(string errorMessage)
    {
        var errorCallback = onError_;
        if (errorCallback == null)
        {
            Console.Error.WriteLine(
                $"[{ClassName}][bind={bindAddress_}:{port_}][pid={Environment.ProcessId}] {errorMessage}");
            return true;
        }

        try
        {
            return errorCallback(errorMessage);
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