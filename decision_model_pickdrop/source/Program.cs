using System;
using System.Threading;
using decision_pickdrop_02.source;
class Program
{
    private static Mutex? singleInstanceMutex;
    static void Main(string[] args)
    {
        const string mutexName = "Global\\decision_pickdrop_02_single_instance";

        bool createdNew;

        singleInstanceMutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out createdNew);

        if (!createdNew)
        {
            Console.WriteLine("이미 실행 중입니다.");
            return;
        }

        try
        {
            var main = new Process();
            main.OnInit();
            main.Dispose();
        }
        finally
        {
            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
        }
    }
}