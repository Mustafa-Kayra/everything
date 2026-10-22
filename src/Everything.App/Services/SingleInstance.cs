using System.Runtime.InteropServices;
using Everything.App.Native;

namespace Everything.App.Services;

/// <summary>
/// Uygulamanın tek örnek çalışmasını sağlar. İkinci örnek, ilkine "pencereyi göster" ya da
/// "hızlı aramayı aç" sinyali gönderip kapanır. Nesneler, yönetici olarak çalışan bir örneğe
/// normal kullanıcı örneğinin de sinyal gönderebilmesi için herkese açık izinlerle oluşturulur.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Herkese tam erişim + düşük bütünlük etiketi: yönetici örneği ile normal örnek konuşabilsin.
    private const string Sddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

    private static readonly string Prefix = $@"Local\EverythingArama-{Environment.UserName}";
    private readonly IntPtr _mutex;
    private readonly IntPtr _showEvent;
    private readonly IntPtr _quickEvent;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(IntPtr mutex, IntPtr showEvent, IntPtr quickEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _quickEvent = quickEvent;
    }

    /// <summary>
    /// İlk örnekse nesneyi döndürür; değilse (istenirse) ilk örneğe sinyal gönderip null döner.
    /// <paramref name="waitForPrevious"/>: yeniden başlatma/güncellemede önceki örneğin kapanmasını bekle.
    /// </summary>
    public static SingleInstance? TryCreate(bool requestQuickSearch, TimeSpan waitForPrevious = default)
    {
        var deadline = DateTime.UtcNow + waitForPrevious;
        while (true)
        {
            var instance = TryCreateOnce(requestQuickSearch, signal: DateTime.UtcNow >= deadline);
            if (instance is not null || DateTime.UtcNow >= deadline)
                return instance;
            Thread.Sleep(250);
        }
    }

    private static SingleInstance? TryCreateOnce(bool requestQuickSearch, bool signal)
    {
        var mutex = CreateWithSddl((ref Win32.SecurityAttributes sa) => Win32.CreateMutex(ref sa, false, Prefix + "-Mutex"), out var error);
        if (mutex == IntPtr.Zero || error is Win32.ErrorAlreadyExists or Win32.ErrorAccessDenied)
        {
            if (mutex != IntPtr.Zero)
                Win32.CloseHandle(mutex);
            if (signal)
                Signal(requestQuickSearch ? "-Quick" : "-Show");
            return null;
        }

        var show = CreateWithSddl((ref Win32.SecurityAttributes sa) => Win32.CreateEvent(ref sa, false, false, Prefix + "-Show"), out _);
        var quick = CreateWithSddl((ref Win32.SecurityAttributes sa) => Win32.CreateEvent(ref sa, false, false, Prefix + "-Quick"), out _);
        return new SingleInstance(mutex, show, quick);
    }

    /// <summary>Diğer örneklerden gelen istekleri dinler; geri çağrılar arka plan iş parçacığında çalışır.</summary>
    public void Listen(Action onShow, Action onQuickSearch)
    {
        var thread = new Thread(() =>
        {
            using var show = new EventHandle(_showEvent);
            using var quick = new EventHandle(_quickEvent);
            var handles = new WaitHandle[] { show, quick, _cts.Token.WaitHandle };
            while (true)
            {
                var index = WaitHandle.WaitAny(handles);
                if (index == 0)
                    onShow();
                else if (index == 1)
                    onQuickSearch();
                else
                    return;
            }
        })
        { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        Win32.CloseHandle(_mutex);
    }

    private delegate IntPtr Creator(ref Win32.SecurityAttributes attributes);

    private static IntPtr CreateWithSddl(Creator create, out int error)
    {
        Win32.ConvertStringSecurityDescriptor(Sddl, 1, out var descriptor, IntPtr.Zero);
        try
        {
            var sa = new Win32.SecurityAttributes
            {
                Length = Marshal.SizeOf<Win32.SecurityAttributes>(),
                SecurityDescriptor = descriptor,
            };
            var handle = create(ref sa);
            error = Marshal.GetLastPInvokeError();
            return handle;
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
                Win32.LocalFree(descriptor);
        }
    }

    private static void Signal(string suffix)
    {
        // İlk örneğin pencereyi öne getirebilmesi için ön plan iznini devret.
        Win32.AllowSetForegroundWindow(-1);
        var handle = Win32.OpenEvent(Win32.EventModifyState, false, Prefix + suffix);
        if (handle == IntPtr.Zero)
            return;
        Win32.SetEvent(handle);
        Win32.CloseHandle(handle);
    }

    private sealed class EventHandle : WaitHandle
    {
        public EventHandle(IntPtr handle)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: true);
        }
    }
}
