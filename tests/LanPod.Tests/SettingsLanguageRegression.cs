using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanPod;
using LanPod.Models;
using LanPod.Services;
using LanPod.Services.WebAccess;

internal static class SettingsLanguageRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(bool preview = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            var address = $"settings-{Guid.NewGuid():N}.invalid";
            LanWebServer? server = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                app = TestAppFactory.Create();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var directory = Path.Combine(Environment.CurrentDirectory, "runtime-data", "settings-language-regression");
                Directory.CreateDirectory(directory);
                var statePath = Path.Combine(directory, "state.json");
                File.WriteAllText(statePath, "{\"settings\":{\"routerAddress\":\"" + address + "\",\"themeMode\":\"Light\"}}");
                var repository = new DeviceRepository(statePath);
                Wait(repository.LoadAsync());
                Require(repository.Settings.InterfaceLanguage == "zh-Hans", "old configuration defaults to Simplified Chinese");
                var credentials = new RouterCredentialService();
                Require(!credentials.HasPassword(address), "synthetic credential target is isolated");
                credentials.SavePassword(address, "synthetic-short");

                window = new MainWindow(new NotificationService())
                {
                    Width = 1180, Height = 900, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false
                };
                typeof(MainWindow).GetField("_repository", PrivateInstance)!.SetValue(window, repository);
                Wait(window.PrepareInitialContentAsync());
                window.Show();
                Invoke(window, "SettingsButton_Click", window, new RoutedEventArgs());
                Pump(250);
                var options = window.InterfaceLanguageCombo.Items.Cast<ComboBoxItem>().ToArray();
                Require(options.Select(item => item.Content?.ToString()).SequenceEqual(
                    new[] { "简体中文", "繁體中文", "日本語", "English" }), "language options use the required native names and order");
                Require(window.InterfaceLanguageCombo.SelectedIndex == 0, "default language is selected");
                var row = (Grid)window.InterfaceLanguageCombo.Parent;
                var rows = (StackPanel)row.Parent;
                var index = rows.Children.IndexOf(row);
                Require(rows.Children[index - 2] == window.ThemeModeCombo.Parent &&
                    rows.Children[index + 2] == window.StartWithWindowsCheck.Parent,
                    "language row is between color theme and startup");
                Require(Descendants<TextBlock>(rows).Any(text => text.Text == "颜色主题"), "color theme label is renamed");

                foreach (var password in new[] { "abc", "eight888", "synthetic-long-password", "密码🙂ab" })
                {
                    credentials.SavePassword(address, password);
                    Invoke(window, "PopulateSettings", repository.Settings);
                    Require(credentials.GetPasswordLength(address) == password.Length &&
                        window.SavedRouterPasswordMask.Text.Length == password.Length &&
                        window.SavedRouterPasswordMask.Visibility == Visibility.Visible,
                        "saved password mask follows stored UTF-16 length");
                    typeof(MainWindow).GetField("_isPopulatingAppSettings", PrivateInstance)!.SetValue(window, true);
                    window.RouterPasswordBox.Password = "replacement";
                    Require(window.SavedRouterPasswordMask.Visibility == Visibility.Collapsed, "typing a replacement hides the saved mask");
                    window.RouterPasswordBox.Clear();
                    Require(window.SavedRouterPasswordMask.Visibility == Visibility.Visible, "clearing replacement restores the stored mask");
                    typeof(MainWindow).GetField("_isPopulatingAppSettings", PrivateInstance)!.SetValue(window, false);
                }

                server = new LanWebServer();
                Wait(server.StartAsync(0, true));
                server.SetPreferences(repository.Settings, true);
                typeof(MainWindow).GetField("_lanWebServer", PrivateInstance)!.SetValue(window, server);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                var url = server.Urls.Single();
                var languages = new[] { "zh-Hans", "zh-Hant", "ja", "en", "zh-Hans" };
                var titles = new[] { "设置", "設定", "設定", "Settings", "设置" };
                for (var i = 0; i < languages.Length; i++)
                {
                    window.InterfaceLanguageCombo.SelectedItem = options.First(item => item.Tag?.ToString() == languages[i]);
                    if (i > 0) PumpUntil(() => ReadLanguage(statePath) == languages[i], 3000);
                    Pump(100);
                    Require(LocalizationService.Language == languages[i] &&
                        Descendants<TextBlock>(window.SettingsPanel).Any(text => text.FontSize == 28 && text.Text == titles[i]),
                        "desktop switches language immediately");
                    var loaded = new DeviceRepository(statePath);
                    Wait(loaded.LoadAsync());
                    Require(loaded.Settings.InterfaceLanguage == languages[i], "language survives configuration reload");
                    using var snapshot = JsonDocument.Parse(Wait(http.GetStringAsync(url + "/api/snapshot?view=overview")));
                    if (i > 0) Require(snapshot.RootElement.GetProperty("preferences").GetProperty("interfaceLanguage").GetString() == languages[i],
                        "open web pages receive language changes while monitoring is disconnected");
                    using var localized = JsonDocument.Parse(Wait(http.GetStringAsync(url + "/api/localization/" + languages[i])));
                    Require(localized.RootElement.GetProperty("strings").GetProperty("设置").GetString() == titles[i],
                        "web and desktop share the same translations");
                    Capture(window, Path.Combine(directory, languages[i] + "-settings.png"));
                    if (languages[i] is "en" or "ja")
                    {
                        window.Width = 380;
                        Pump(200);
                        Invoke(window, "SettingsButton_Click", window, new RoutedEventArgs());
                        Pump(200);
                        Require(window.SettingsPanel.IsVisible && window.InterfaceLanguageCombo.IsVisible,
                            "language selector stays available in a narrow settings page");
                        Capture(window, Path.Combine(directory, languages[i] + "-compact-settings.png"));
                        window.Width = 1180;
                        Pump(100);
                    }
                }
                var selectedIndex = window.InterfaceLanguageCombo.SelectedIndex;
                Invoke(window, "SettingsButton_Click", window, new RoutedEventArgs());
                Require(window.InterfaceLanguageCombo.SelectedIndex == selectedIndex, "reopening settings preserves the selection");
                window.DoNotDisturbStartBox.Text = "invalid";
                window.InterfaceLanguageCombo.SelectedIndex = 3;
                PumpUntil(() => ReadLanguage(statePath) == "en", 3000);
                Require(repository.Settings.InterfaceLanguage == "en", "language saving is independent of an unfinished quiet-hours edit");
                VerifyFontCoverage(app);
                Console.WriteLine("PASS settings language, persistence, shared web translations, row order, password masks and embedded-font coverage");
                if (preview)
                {
                    window.DoNotDisturbStartBox.Text = "22:00";
                    Wait(repository.ApplyPollAsync(
                        [new DeviceSnapshot("02:00:00:00:45:01", "Test Phone", "test-phone", true, "192.0.2.45", 1, 60, 1024, 2048, 4096, 8192)],
                        DateTimeOffset.Now));
                    server.Publish(new MonitorUpdate(Wait(repository.GetDevicesAsync()), [], DateTimeOffset.Now));
                    Console.WriteLine("PREVIEW_URL=" + url + "/");
                    var deadline = Environment.TickCount64 + 300000;
                    var command = Task.Run(Console.ReadLine);
                    while (Environment.TickCount64 < deadline)
                    {
                        Pump(20);
                        if (!command.IsCompleted) continue;
                        var language = command.GetAwaiter().GetResult();
                        if (language is null or "exit") break;
                        var option = options.FirstOrDefault(item => item.Tag?.ToString() == language);
                        if (option is not null) window.InterfaceLanguageCombo.SelectedItem = option;
                        Pump(200);
                        Console.WriteLine("PREVIEW_LANGUAGE=" + LocalizationService.Language);
                        command = Task.Run(Console.ReadLine);
                    }
                }
            }
            catch (Exception exception) { failure = exception.GetBaseException(); }
            finally
            {
                window?.PrepareForExit();
                window?.Close();
                if (server is not null) Wait(server.DisposeAsync().AsTask());
                app?.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(preview ? 360 : 70))) { Console.Error.WriteLine("Settings language regression timed out."); return 1; }
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static void VerifyFontCoverage(App app)
    {
        var family = (FontFamily)app.FindResource("AppTextFont");
        foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
        {
            Require(new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var font), "embedded text font resolves");
            var missing = new HashSet<int>();
            foreach (var language in new[] { "zh-Hans", "zh-Hant", "ja", "en" })
                foreach (var text in LocalizationService.GetStrings(language).Values)
                    foreach (var rune in text.EnumerateRunes())
                        if (!char.IsWhiteSpace((char)rune.Value) && !font.CharacterToGlyphMap.ContainsKey(rune.Value)) missing.Add(rune.Value);
            Require(missing.Count == 0, "embedded font covers all translated text " + string.Join(",", missing.Select(code => $"U+{code:X4}")));
        }
    }

    private static string? ReadLanguage(string path)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(path));
        return state.RootElement.GetProperty("settings").TryGetProperty("interfaceLanguage", out var language) ? language.GetString() : null;
    }

    private static void Capture(MainWindow window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Invoke(MainWindow window, string method, params object[] arguments) =>
        typeof(MainWindow).GetMethod(method, PrivateInstance)!.Invoke(window, arguments);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted, 10000); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Settings regression condition timed out.");
            Pump(15);
        }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

}
