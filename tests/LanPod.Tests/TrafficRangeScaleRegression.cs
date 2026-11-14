using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanPod;
using LanPod.Controls;
using LanPod.Models;
using LanPod.Services;

internal static class TrafficRangeScaleRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(bool presented = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            try
            {
                app = TestAppFactory.Create(); app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeService.Apply(ThemeService.LightMode);
                window = new MainWindow(new NotificationService())
                {
                    Title = "图表区间动画回归验证", Topmost = presented,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = presented ? 100 : -10000, Top = presented ? 70 : -10000,
                    Width = 1180, Height = 850, ShowActivated = false, ShowInTaskbar = false
                };
                typeof(MainWindow).GetField("_isPopulatingAppSettings", Private)!.SetValue(window, true);
                var now = DateTimeOffset.Now;
                var device = new DeviceViewData
                {
                    Profile = new DeviceProfile { MacAddress = "02:00:00:00:76:21", LastRouterName = "图表区间测试设备",
                        LastKnownOnline = true, FirstSeenAt = now, LastSeenAt = now, LastIpAddress = "192.0.2.21" },
                    Identity = new DeviceIdentity("笔记本电脑", "\uE770", "图表区间测试设备"), Sessions = []
                };
                typeof(MainWindow).GetMethod("ReplaceDevices", Private)!.Invoke(window, [new[] { device }, false, false]);
                window.Show(); window.UpdateLayout(); Pump(700);
                var chart = Descendants<TrafficChart>(window).Single();
                chart.Samples = Enumerable.Range(0, 661).Select(i =>
                {
                    var age = 660 - i;
                    return new TrafficSample(now.AddSeconds(-age), age > 360 ? 0 : 9000,
                        age is >= 510 and <= 540 ? 1_000_000 : age is >= 160 and <= 190 ? 50000 : 12000);
                }).ToArray();
                Pump(100);
                Require(chart.IsVisible && chart.ActualWidth > 100, "The bound chart is not visible.");
                if (presented) Pump(2500);
                foreach (var milliseconds in new[] { 220d, 480d })
                {
                    window.TrafficRangeSegments.AnimationMilliseconds = milliseconds;
                    window.TrafficRangeSegments.SelectedIndex = 2; Pump((int)milliseconds + 60);
                    foreach (var index in new[] { 1, 0, 2 })
                    {
                        var from = chart.RangeMinutes;
                        var timer = Stopwatch.StartNew();
                        window.TrafficRangeSegments.SelectedIndex = index;
                        Pump((int)(milliseconds * .4));
                        Capture(window, $"{milliseconds}-{from}-{chart.RangeMinutes}-middle");
                        Pump(Math.Max(1, (int)(milliseconds + 45 - timer.Elapsed.TotalMilliseconds)));
                        AssertSettled(chart, $"{milliseconds}ms {from}->{chart.RangeMinutes}");
                        var scale = Field<MatrixTransform>(chart, "_plotTransform").Matrix;
                        Capture(window, $"{milliseconds}-{from}-{chart.RangeMinutes}-finished");
                        Pump(350);
                        var later = Field<MatrixTransform>(chart, "_plotTransform").Matrix;
                        Require(Math.Abs(scale.M22 - later.M22) < 1e-8 && Math.Abs(scale.OffsetY - later.OffsetY) < 1e-8,
                            "The curve kept rising after the range selector finished.");
                        Console.WriteLine($"Range scale PASS: {milliseconds}ms {from}->{chart.RangeMinutes}, no vertical drift after completion.");
                    }
                    window.TrafficRangeSegments.SelectedIndex = 0; Pump(65);
                    window.TrafficRangeSegments.SelectedIndex = 1; Pump((int)milliseconds + 45);
                    AssertSettled(chart, $"{milliseconds}ms rapid reversal");
                }
                window.TrafficRangeSegments.AnimationMilliseconds = 0;
                window.TrafficRangeSegments.SelectedIndex = 0; Pump(40);
                AssertSettled(chart, "disabled animation");
            }
            catch (Exception error) { failure = error; }
            finally { window?.PrepareForExit(); window?.Close(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(25))) { Console.Error.WriteLine("Range scale regression timed out."); Environment.Exit(1); }
        if (failure is null) return 0;
        Console.Error.WriteLine(failure); return 1;
    }

    private static void AssertSettled(TrafficChart chart, string context)
    {
        Require(Field<double>(chart, "_presentationRangeMinutes") == chart.RangeMinutes, context + ": horizontal range has not settled.");
        var scale = typeof(TrafficChart).GetField("_targetScale", Private)!.GetValue(chart)!;
        double Bound(string name) => (double)scale.GetType().GetProperty(name)!.GetValue(scale)!;
        var top = Bound("TopValue"); var bottom = Bound("BottomValue");
        var matrix = Field<MatrixTransform>(chart, "_plotTransform").Matrix;
        var expectedY = (chart.ActualHeight - 55) * 1024 / Math.Max(1, top - bottom);
        var expectedOffset = chart.ActualHeight - 32 + bottom / 1024 * expectedY;
        Require(Math.Abs(matrix.M22 / expectedY - 1) < 1e-8 && Math.Abs(matrix.OffsetY - expectedOffset) < 1e-8,
            $"{context}: selector finished while the curve's vertical scale was still changing ({matrix.M22:F4} vs {expectedY:F4}).");
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Capture(FrameworkElement element, string name)
    {
        var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var folder = Path.Combine(Environment.CurrentDirectory, "runtime-data", "range-motion-20261101", "frames");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
