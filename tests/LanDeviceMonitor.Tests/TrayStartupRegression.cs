using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LanDeviceMonitor.Services;

internal static class TrayStartupRegression
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Configure(int operation, int failures, int windowFailure, int blocked);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeAction();

    internal static int Run()
    {
        var fixturePath = Path.GetFullPath(".tools/native-tray-tests/TrayStartupFixture.dll");
        var fixture = NativeLibrary.Load(fixturePath);
        NativeLibrary.SetDllImportResolver(typeof(NativeTrayIcon).Assembly,
            (name, _, _) => name == "LanDeviceMonitor.TrayRuntime.dll" ? fixture : 0);
        var configure = Marshal.GetDelegateForFunctionPointer<Configure>(NativeLibrary.GetExport(fixture, "TestConfigure"));
        var release = Marshal.GetDelegateForFunctionPointer<NativeAction>(NativeLibrary.GetExport(fixture, "TestReleaseShell"));
        var stop = Marshal.GetDelegateForFunctionPointer<NativeAction>(NativeLibrary.GetExport(fixture, "LanTrayStop"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "runtime-data", "tray-startup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "tray-runtime.log");
            var app = TestAppFactory.Create();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var owner = new Window { ShowInTaskbar = false };
            app.MainWindow = owner;
            var dispatcherErrors = new List<Exception>();
            app.DispatcherUnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
            using var icon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            try
            {
                configure(0, 100, 0, 0);
                using (var tray = new NativeTrayIcon(icon, "Synthetic startup", () => { }, _ => { }, logPath))
                {
                    Require(!IsWindowVisible(new WindowInteropHelper(owner).Handle), "Background owner became visible.");
                    Require(!tray.IsAdded && tray.Handle != 0, "Pending registration was reported as added.");
                    var dispatcherWorked = false;
                    app.Dispatcher.BeginInvoke(new Action(() => dispatcherWorked = true));
                    Pump(50);
                    Require(dispatcherWorked, "Pending shell registration blocked application work.");
                }
                Pump(30);
                Require(dispatcherErrors.Count == 0, "Disposal of pending registration queued a fatal error.");
                Console.WriteLine("PASS pending tray allows application work and hidden startup, then exits cleanly");

                configure(0, 0, 0, 1);
                var clock = Stopwatch.StartNew();
                using (var tray = new NativeTrayIcon(icon, "Synthetic slow shell", () => { }, _ => { }, logPath))
                {
                    Require(clock.Elapsed < TimeSpan.FromSeconds(1), "Native constructor waited for Shell_NotifyIcon.");
                    Require(!tray.IsAdded, "Blocked shell was reported as registered.");
                    Pump(50);
                    release();
                    WaitForIcon(tray);
                }
                Console.WriteLine("PASS managed readiness precedes blocking shell registration and recovers");

                foreach (var operation in new[] { 0, 4, 10 })
                {
                    configure(operation, 2, 0, 0);
                    using var tray = new NativeTrayIcon(icon, "Synthetic recovery", () => { }, _ => { }, logPath);
                    WaitForIcon(tray);
                }
                Console.WriteLine("PASS managed wrapper recovers add, V4 and tooltip failures");

                configure(0, 0, 1, 0);
                try
                {
                    using var tray = new NativeTrayIcon(icon, "Synthetic failure", () => { }, _ => { }, logPath);
                    throw new InvalidOperationException("Window failure was hidden.");
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 5) { }
                Pump(30);
                Require(dispatcherErrors.Count == 0, "Constructor failure raised a second dispatcher exception.");
                Console.WriteLine("PASS window failure retains error 5 without a duplicate dispatcher exception");

                configure(0, 0, 0, 0);
                using (var tray = new NativeTrayIcon(icon, "Synthetic termination", () => { }, _ => { }, logPath))
                {
                    WaitForIcon(tray);
                    stop();
                    var deadline = Stopwatch.StartNew();
                    while (dispatcherErrors.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(2)) Pump(10);
                    Require(dispatcherErrors.Count == 1, "Unexpected native termination did not report exactly one error.");
                    dispatcherErrors.Clear();
                }
                Console.WriteLine("PASS unexpected native termination remains visible to the application");

                for (var i = 0; i < 50; i++)
                {
                    configure(0, 100, 0, 0);
                    using (var tray = new NativeTrayIcon(icon, "Synthetic immediate exit", () => { }, _ => { }, logPath)) { }
                    Pump(5);
                }
                Require(dispatcherErrors.Count == 0, "Immediate start/dispose raced a ready event or dispatcher callback.");
                var log = File.ReadAllText(logPath);
                foreach (var stage in new[] { "runtime.ready", "NIM_ADD.failed", "NIM_SETVERSION.failed", "NIM_MODIFY.tip.failed", "icon.ready", "CreateWindow.failed", "runtime.stopped" })
                    Require(log.Contains("stage=" + stage, StringComparison.Ordinal), "Missing diagnostic stage " + stage);
                Require(!log.Contains("2166572391", StringComparison.Ordinal), "Shell raw last-error was recorded as an API error.");
                Console.WriteLine("PASS 50 immediate managed start/dispose cycles and per-stage diagnostic records");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                release();
                owner.Close();
                app.Shutdown();
                Directory.Delete(directory, recursive: true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
        {
            release();
            Console.Error.WriteLine("Tray startup regression timed out.");
            return 1;
        }
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine("Tray startup regression PASS.");
        return 0;
    }

    private static void WaitForIcon(NativeTrayIcon tray)
    {
        var clock = Stopwatch.StartNew();
        while (!tray.IsAdded && clock.Elapsed < TimeSpan.FromSeconds(5)) Pump(10);
        Require(tray.IsAdded, "Tray did not recover from temporary shell failure.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
}
