using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using Caiqelle.Services;
using Velopack;

namespace Caiqelle
{
    public static class Program
    {
        public static bool IsBackgroundStart { get; private set; }

        [global::System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
        private static extern void XamlCheckProcessRequirements();

        [global::System.STAThreadAttribute]
        static void Main(string[] args)
        {
            // Velopack の起動時フック（インストール・更新・アンインストール処理）
            VelopackApp.Build()
                .OnFirstRun((v) => {
                    // 初回インストール直後の処理
                })
                .Run();

            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("BranchShot");
                foreach (var process in processes)
                {
                    process.Kill();
                }
            }
            catch { }

            if (args != null && args.Length > 0 && args[0] == "--background")
            {
                IsBackgroundStart = true;
            }

            XamlCheckProcessRequirements();

            const string mutexName = "Caiqelle_SingleInstance_Mutex";
            const string eventName = "Caiqelle_ShowWindowEvent";
            System.Threading.Mutex? mutex = null;
            bool isFirstInstance = false;

            try
            {
                mutex = new System.Threading.Mutex(true, mutexName, out isFirstInstance);
            }
            catch
            {
                isFirstInstance = false;
            }

            if (!isFirstInstance)
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(eventName, out var existingEvent))
                {
                    existingEvent.Set();
                }
                return;
            }

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) => {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            
            mutex?.ReleaseMutex();
        }
    }

    public partial class App : Application
    {
        private Window? _window;
        public Window? MainWindow => _window;
        
        private Views.FloatingWindow? _floatingWindow;
        private Window? _hiddenTrayWindow;
        private EventWaitHandle? _showEvent;
        public IServiceProvider Services { get; }

        public App()
        {
            this.UnhandledException += (s, e) => 
            {
                try
                {
                    string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caiqelle");
                    System.IO.Directory.CreateDirectory(logDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(logDir, "crash_log.txt"), "UnhandledException: " + e.Exception.ToString());
                }
                catch { }
            };

            Services = ConfigureServices();
            InitializeComponent();

            const string eventName = "Caiqelle_ShowWindowEvent";
            
            // IPCスレッドの開始
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            Task.Run(() =>
            {
                while (true)
                {
                    _showEvent.WaitOne();

                    var dispatcher = _hiddenTrayWindow?.DispatcherQueue ?? _window?.DispatcherQueue;
                    if (dispatcher != null)
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            PositionAndShowWindow();
                        });
                    }
                }
            });

            // Start global hook
            var hookService = Services.GetRequiredService<HookService>();
            var settings = Services.GetRequiredService<SettingsManager>();
            hookService.IsHijackPrintScreen = settings.IsHijackPrintScreen;
            hookService.IsHijackWinShiftS = settings.IsHijackWinShiftS;
            hookService.OnKeyPressed += HookService_OnKeyPressed;
            hookService.Start();
        }

        public CaptureManager? CaptureManager { get; private set; }
        private Views.EditorWindow? _editorWindow;
        private Views.RewindWindow? _rewindWindow;

        private Views.FloatingWindow GetFloatingWindow() => _floatingWindow ??= new Views.FloatingWindow();



        private void HookService_OnKeyPressed(CapturedKey key)
        {
            var dispatcher = _hiddenTrayWindow?.DispatcherQueue ?? _window?.DispatcherQueue;
            if (dispatcher == null) return;
            
            dispatcher.TryEnqueue(async () =>
            {
                if (key == CapturedKey.OpenDashboard)
                {
                    PositionAndShowWindow();
                    return;
                }

                if (key == CapturedKey.ToggleRewind)
                {
                    var rewindService = Services.GetRequiredService<RewindService>();
                    var notificationService = Services.GetRequiredService<NotificationService>();

                    if (rewindService.IsRunning)
                    {
                        rewindService.Stop();
                        notificationService.ShowNotification("リワインドを一時停止しました", "\uE71A");
                    }
                    else
                    {
                        // バッファを保持したまま再開
                        rewindService.Start(clearBuffer: false);
                        notificationService.ShowNotification("リワインドを再開しました", "\uE7C8");
                    }
                    return;
                }

                if (key == CapturedKey.ShiftPrintScreen)
                {
                    var rewindService = Services.GetRequiredService<RewindService>();
                    var settings = Services.GetRequiredService<SettingsManager>();
                    var notificationService = Services.GetRequiredService<NotificationService>();

                    if (!settings.IsRewindEnabled)
                    {
                        notificationService.ShowNotification("リワインド機能が無効です (設定タブで有効化してください)", "\uE7BA");
                        return;
                    }

                    rewindService.Pause();

                    if (_editorWindow == null) _editorWindow = new Views.EditorWindow();

                    if (_rewindWindow != null)
                    {
                        try { _rewindWindow.Close(); } catch { }
                        _rewindWindow = null;
                    }

                    _rewindWindow = new Views.RewindWindow(rewindService, _editorWindow);
                    _rewindWindow.Closed += (s, e) => _rewindWindow = null;
                    _rewindWindow.Activate();
                }
                else if (key == CapturedKey.PrintScreen || key == CapturedKey.WinShiftS)
                {
                    GetFloatingWindow().ShowAnimated();
                    
                    var hookService = Services.GetRequiredService<HookService>();
                    hookService.IsFloatingUiActive = true;
                }
                else if (key == CapturedKey.Esc)
                {
                    _floatingWindow?.CloseWithAnimation();
                    CaptureManager?.CancelCapture();
                }
                else if (key == CapturedKey.R)
                {
                    _floatingWindow?.HideInstantlyAndClose();
                    ShowMainWindowAtTab(ViewModels.MainTab.Rewind);
                }
                else if (key == CapturedKey.S)
                {
                    _floatingWindow?.HideInstantlyAndClose();
                    Services.GetRequiredService<StickyTextService>().OpenStickyText();
                }
                else if (key == CapturedKey.V)
                {
                    _floatingWindow?.HideInstantlyAndClose();
                    Services.GetRequiredService<StickyTextService>().OpenStickyTextList();
                }
                else if (key == CapturedKey.C || key == CapturedKey.W || key == CapturedKey.F || key == CapturedKey.E || key == CapturedKey.P || key == CapturedKey.L)
                {
                    System.Diagnostics.Debug.WriteLine($"Action: {key}");
                    _floatingWindow?.HideInstantlyAndClose();
                    
                    if (CaptureManager != null)
                    {
                        var hook = Services.GetRequiredService<HookService>();
                        hook.IsCaptureOverlayActive = true;
                        try
                        {
                            await CaptureManager.StartCaptureAsync(key);
                        }
                        finally
                        {
                            hook.IsCaptureOverlayActive = false;
                        }
                    }
                }
            });
        }

        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<SettingsManager>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<RewindService>();
            services.AddSingleton<HookService>();
            services.AddSingleton<ScreenshotService>();
            services.AddSingleton<StartupService>();
            services.AddSingleton<NotificationService>();
            services.AddSingleton<ClipboardService>();
            services.AddSingleton<StickyTextService>();
            services.AddSingleton<UpdateService>();
            services.AddTransient<ViewModels.MainViewModel>();
            services.AddTransient<ViewModels.FolderSelectionViewModel>();
            services.AddTransient<ViewModels.FloatingViewModel>();
            services.AddTransient<ViewModels.EditorViewModel>();
            return services.BuildServiceProvider();
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                string pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png");
                if (System.IO.File.Exists(pngPath))
                {
                    using var bmp = new System.Drawing.Bitmap(pngPath);
                    return System.Drawing.Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { }

            return System.Drawing.SystemIcons.Application;
        }

        private H.NotifyIcon.TaskbarIcon? _trayIcon;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                var settings = Services.GetRequiredService<SettingsManager>();
                Services.GetRequiredService<ThemeService>().ApplyTheme(settings.AccentTheme);

                // Instantiate a hidden window to provide a XamlRoot and DispatcherQueue for the background logic
                _hiddenTrayWindow = new Views.HiddenTrayWindow();

                var startupService = Services.GetRequiredService<StartupService>();
                if (settings.IsRunAtStartup)
                {
                    startupService.SetRunAtStartup(true);
                }
                
                if (settings.IsRewindEnabled)
                {
                    var rewindService = Services.GetRequiredService<RewindService>();
                    rewindService.Start();
                }

                // バックグラウンド自動更新ループの開始（設定がONの場合のみ起動後10秒にチェック＆4時間毎に定期実行）
                var updateService = Services.GetRequiredService<UpdateService>();
                updateService.StartAutoUpdateLoop();

                // 軽量な CaptureManager 初期化（内部ウィンドウは遅延生成）
                CaptureManager = new CaptureManager(Services.GetRequiredService<ScreenshotService>());

                // 起動直後のUI応答性を損なわないよう、アイドル時にオーバーレイウィンドウを事前生成
                var prewarmDispatcher = _hiddenTrayWindow.DispatcherQueue;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    prewarmDispatcher?.TryEnqueue(() => CaptureManager?.Prewarm());
                });

                // トレイアイコンの初期化（右クリックメニューはWin32ネイティブで処理）
                _trayIcon = new H.NotifyIcon.TaskbarIcon
                {
                    // TODO: Caiqelle用アイコンを用意したら差し替える
                    Icon = LoadTrayIcon(),
                    ToolTipText = "Caiqelle",
                };
                
                var leftClickCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                leftClickCommand.ExecuteRequested += (s, e) => { TrayLeftClick(s, null!); };
                _trayIcon.LeftClickCommand = leftClickCommand;

                // 右クリック時にWin32ネイティブメニューを表示
                var rightClickCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                rightClickCommand.ExecuteRequested += (s, e) => { ShowNativeTrayContextMenu(); };
                _trayIcon.RightClickCommand = rightClickCommand;
                
                try
                {
                    _trayIcon.ForceCreate();
                }
                catch (Exception trayEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] TaskbarIcon.ForceCreate failed: {trayEx.Message}");
                }

                if (!Program.IsBackgroundStart)
                {
                    // Show MainWindow if not started in background mode
                    var dispatcher = _hiddenTrayWindow?.DispatcherQueue;
                    dispatcher?.TryEnqueue(() =>
                    {
                        PositionAndShowWindow();
                    });
                }

                // 保存有効かつ「Sticky Text起動時に復元」がOFFの場合は、アプリ起動と同時に自動復元
                if (settings.IsStickyTextSaveEnabled && !settings.IsStickyTextRestoreOnOpen && settings.SavedStickyTexts.Count > 0)
                {
                    _hiddenTrayWindow?.DispatcherQueue?.TryEnqueue(() =>
                    {
                        Services.GetRequiredService<StickyTextService>().RestoreSavedStickyTexts();
                    });
                }

                // アイドル時にフローティングUI・オーバーレイ・通知ウィンドウを事前生成して初回起動を爆速化
                _hiddenTrayWindow?.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        _ = GetFloatingWindow();
                        CaptureManager?.Prewarm();
                        Services.GetService<NotificationService>()?.Prewarm();
                    }
                    catch (Exception prewarmEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[App] Prewarm failed: {prewarmEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(@"C:\Dev\personal\Caiqelle\crash_log.txt", "OnLaunched: " + ex.ToString());
                throw;
            }
        }

        private void TrayLeftClick(object sender, RoutedEventArgs e)
        {
            var dispatcher = _hiddenTrayWindow?.DispatcherQueue ?? _window?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                PositionAndShowWindow();
            });
        }

        // Win32ネイティブのコンテキストメニューを表示
        private void ShowNativeTrayContextMenu()
        {
            const int IDM_EXIT = 1001;

            IntPtr hMenu = CreatePopupMenu();
            AppendMenuW(hMenu, 0x0000 /* MF_STRING */, IDM_EXIT, "終了");

            // カーソル位置を取得
            GetCursorPos(out POINT pt);

            // フォアグラウンドに設定しないとメニューが表示されない場合がある
            var hwnd = _hiddenTrayWindow != null 
                ? WinRT.Interop.WindowNative.GetWindowHandle(_hiddenTrayWindow) 
                : IntPtr.Zero;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);

            int cmd = TrackPopupMenu(hMenu, 0x0100 /* TPM_RETURNCMD */, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (cmd == IDM_EXIT)
            {
                try
                {
                    _trayIcon?.Dispose();
                    Services.GetService<HookService>()?.Stop();
                    Services.GetService<RewindService>()?.Stop();
                }
                catch { }
                
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }

        public void ShowMainWindowAtTab(ViewModels.MainTab tab)
        {
            PositionAndShowWindow();
            if (_window is MainWindow mainWindow)
            {
                switch (tab)
                {
                    case ViewModels.MainTab.Home:
                        mainWindow.ViewModel.NavigateToHomeCommand.Execute(null);
                        break;
                    case ViewModels.MainTab.Folder:
                        mainWindow.ViewModel.NavigateToFolderCommand.Execute(null);
                        break;
                    case ViewModels.MainTab.Rewind:
                        mainWindow.ViewModel.NavigateToRewindCommand.Execute(null);
                        break;
                    case ViewModels.MainTab.Settings:
                        mainWindow.ViewModel.NavigateToSettingsCommand.Execute(null);
                        break;
                }
            }
        }

        private void PositionAndShowWindow()
        {
            if (_window == null)
            {
                _window = new MainWindow();
                _window.Closed += (s, ev) => {
                    ev.Handled = true;
                    Views.WindowExtensions.Hide(_window);
                };
            }
            
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            
            var workArea = displayArea.WorkArea;
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
            // Quick Access Window size
            int width = 360;
            int height = 500;
            
            // Place bottom right, just above the taskbar
            int margin = 12;
            int x = workArea.X + workArea.Width - width - margin;
            int y = workArea.Y + workArea.Height - height - margin;
            
            appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            
            Caiqelle.Views.WindowExtensions.Show(_window);
            SetForegroundWindow(hwnd);
            _window.Activate();
        }

        // Win32 P/Invoke宣言
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
