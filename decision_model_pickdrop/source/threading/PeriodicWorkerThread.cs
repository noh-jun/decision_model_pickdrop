using System;
using System.Diagnostics;
using System.Threading;

namespace threading;

public sealed class PeriodicWorkerThread : IDisposable
{
    public PeriodicWorkerThread(Action working, TimeSpan period, CancellationToken stopToken)
    {
        working_ = working ?? throw new ArgumentNullException(nameof(working));

        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "period must be > 0.");

        period_ = period;
        stopToken_ = stopToken;
    }

    public void Start()
    {
        lock (gate_)
        {
            if (thread_ != null)
                throw new InvalidOperationException("Worker already started.");

            cancelEvent_ = new ManualResetEventSlim(false);
            stopRegistration_ = stopToken_.Register(() => cancelEvent_!.Set());

            thread_ = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "PeriodicWorkerThread"
            };
            thread_.Start();
        }
    }

    public void StopAndJoin()
    {
        Thread? threadToJoin;
        lock (gate_)
        {
            threadToJoin = thread_;
        }

        if (threadToJoin == null)
            return;

        // stopToken은 외부에서 cancel된다는 전제.
        // 혹시 외부가 cancel을 안 해도 Join이 영원히 걸리지 않게 하려면
        // 별도 내부 CTS를 만들고 여기서 Cancel하는 형태로 바꾸면 됨.

        threadToJoin.Join();

        lock (gate_)
        {
            thread_ = null;

            stopRegistration_.Dispose();
            stopRegistration_ = default;

            cancelEvent_?.Dispose();
            cancelEvent_ = null;
        }
    }

    public void Dispose()
    {
        StopAndJoin();
    }

    private void WorkerLoop()
    {
        // Start()에서 초기화 완료 후 시작된다고 가정
        var cancelEvent = cancelEvent_!;

        var stopwatch = Stopwatch.StartNew();
        TimeSpan nextTick = stopwatch.Elapsed + period_;

        while (!stopToken_.IsCancellationRequested)
        {
            // 1) 작업 수행
            try
            {
                working_();
            }
            catch
            {
                // 예외 정책:
                // - 여기서는 worker thread가 죽지 않게 swallow
                // - 필요하면 로깅 후 continue / 혹은 break로 정책 변경 가능
            }

            // 2) 다음 tick까지 남은 시간만 event 기반 대기
            while (!stopToken_.IsCancellationRequested)
            {
                TimeSpan now = stopwatch.Elapsed;
                TimeSpan remaining = nextTick - now;

                if (remaining <= TimeSpan.Zero)
                    break;

                // timeout 대기 (CPU 거의 0%), stopToken cancel이면 즉시 Set되어 깨어남
                cancelEvent.Wait(remaining);
            }

            // 3) 다음 tick 갱신(누적 그리드 유지)
            nextTick += period_;

            // 4) (옵션) 너무 많이 밀렸으면 catch-up 폭주 방지
            if (stopwatch.Elapsed - nextTick > period_ * 4)
            {
                nextTick = stopwatch.Elapsed + period_;
            }
        }
    }

    private readonly object gate_ = new object();
    private readonly Action working_;
    private readonly TimeSpan period_;
    private readonly CancellationToken stopToken_;

    private Thread? thread_;
    private ManualResetEventSlim? cancelEvent_;
    private CancellationTokenRegistration stopRegistration_;
}