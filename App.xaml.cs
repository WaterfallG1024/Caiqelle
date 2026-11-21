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

            if (args != null && args.Length > 0 && args[0] == "--background")
            {
                IsBackgroundStart = true;
            }

            XamlCheckProcessRequirements();

            const string mutexName = @"Local\Caiqelle_SingleInstance_Mutex";
            const string eventName = @"Local\Caiqelle_ShowWindowEvent";
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
        private Views.ActionPillWindow? _actionPillWindow;
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

            const string eventName = @"Local\Caiqelle_ShowWindowEvent";
            
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
                    var floating = GetFloatingWindow();
                    if (!floating.IsWindowVisible)
                    {
                        floating.ShowAnimated();
                    }

                    var hookService = Services.GetRequiredService<HookService>();
                    hookService.IsFloatingUiActive = true;
                }
                else if (key == CapturedKey.Esc)
                {
                    _floatingWindow?.CloseWithAnimation();
                    CaptureManager?.CancelCapture();
                }
                else if (key == CapturedKey.ActionApply)
                {
                    _actionPillWindow?.TriggerApplyFromShortcut();
                }
                else if (key == CapturedKey.ActionDismiss)
                {
                    _actionPillWindow?.TriggerDismissFromShortcut();
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
            services.AddSingleton<OcrService>();
            services.AddSingleton<ActionService>();
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
                // .ico が存在する場合は直接読み込み、GDI+ ビットマップ変換を回避して起動を高速化
                string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Caiqelle.ico");
                if (System.IO.File.Exists(icoPath))
                {
                    return new System.Drawing.Icon(icoPath);
                }

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
                Services.GetRequiredService<ThemeService>().ApplyTheme(settings.AccentTheme, settings.AppThemeMode);

                // Instantiate a hidden window to provide a XamlRoot and DispatcherQueue for the background logic
                _hiddenTrayWindow = new Views.HiddenTrayWindow();

                var startupService = Services.GetRequiredService<StartupService>();
                if (settings.IsRunAtStartup)
                {
                    _ = Task.Run(() => startupService.SetRunAtStartup(true));
                }
                
                if (settings.IsRewindEnabled)
                {
                    var rewindService = Services.GetRequiredService<RewindService>();
                    rewindService.Start();
                }

                // バックグラウンド自動更新ループの開始（設定がONの場合のみ起動後10秒にチェック＆4時間毎に定期実行）
                var updateService = Services.GetRequiredService<UpdateService>();
                updateService.StartAutoUpdateLoop();

                // 軽量な CaptureManager 初期化（内部ウィンドウはアイドル時に段階分散して事前生成）
                CaptureManager = new CaptureManager(Services.GetRequiredService<ScreenshotService>());

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

                // ActionService（OCR 自動認識＆確認ピル表示）を起動・連携
                var actionService = Services.GetRequiredService<ActionService>();
                var actionHookService = Services.GetRequiredService<HookService>();
                var actionThemeService = Services.GetRequiredService<ThemeService>();
                var uiQueue = _hiddenTrayWindow?.DispatcherQueue;
                actionService.IsPillCurrentlyVisible = () => _actionPillWindow?.IsPillVisible == true;
                actionService.DetectionReady += (ctx) =>
                {
                    uiQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            _actionPillWindow ??= new Views.ActionPillWindow(actionService, actionHookService, actionThemeService);
                            _actionPillWindow.ShowContext(ctx);
                        }
                        catch { }
                    });
                };
                actionService.ContextInvalidated += () =>
                {
                    uiQueue?.TryEnqueue(() =>
                    {
                        try { _actionPillWindow?.HideWithAnimation(); } catch { }
                    });
                };
                actionService.StartMonitoring();

                // アイドル時にフローティングUI・通知・各オーバーレイを1ステップずつ分散生成（UIフレーム落ち防止）
                var prewarmQueue = _hiddenTrayWindow?.DispatcherQueue;
                if (prewarmQueue != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(400);
                        prewarmQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            try { _ = GetFloatingWindow(); } catch { }
                        });
                        await Task.Delay(120);
                        prewarmQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            try { Services.GetService<NotificationService>()?.Prewarm(); } catch { }
                        });
                        for (int step = 0; step < 3; step++)
                        {
                            int s = step;
                            await Task.Delay(120);
                            prewarmQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                            {
                                try { CaptureManager?.PrewarmStep(s); } catch { }
                            });
                        }
                        if (settings.IsActionAutoDetectEnabled)
                        {
                            await Task.Delay(120);
                            prewarmQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                            {
                                try
                                {
                                    _actionPillWindow ??= new Views.ActionPillWindow(actionService, actionHookService, actionThemeService);
                                }
                                catch { }
                            });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caiqelle");
                    System.IO.Directory.CreateDirectory(logDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(logDir, "crash_log.txt"), "OnLaunched: " + ex.ToString());
                }
                catch { }
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
            const int IDM_RESTART = 1002;
            const int IDM_EXIT = 1001;

            IntPtr hMenu = CreatePopupMenu();
            AppendMenuW(hMenu, 0x0000 /* MF_STRING */, IDM_RESTART, "再起動");
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

            if (cmd == IDM_RESTART)
            {
                var updateService = Services.GetService<UpdateService>();
                CleanupBeforeExit();
                updateService?.RestartApplication();
            }
            else if (cmd == IDM_EXIT)
            {
                CleanupBeforeExit();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }

        /// <summary>
        /// 終了・再起動前に常駐サービスやトレイアイコンのリソースを解放する
        /// </summary>
        private void CleanupBeforeExit()
        {
            try { _trayIcon?.Dispose(); } catch { }
            try { Services.GetService<HookService>()?.Stop(); } catch { }
            try { Services.GetService<RewindService>()?.Stop(); } catch { }
            try { Services.GetService<ActionService>()?.Dispose(); } catch { }
            try { Services.GetService<UpdateService>()?.Dispose(); } catch { }
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
                    case ViewModels.MainTab.Action:
                        mainWindow.ViewModel.NavigateToActionCommand.Execute(null);
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
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            GetCursorPos(out POINT cursorPos);
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y),
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                ?? Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

            var workArea = displayArea.WorkArea;
            double dpiScale = Views.NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            int width = Math.Max(appWindow.Size.Width, (int)(500 * dpiScale));
            int height = Math.Max(appWindow.Size.Height, (int)(580 * dpiScale));
            int margin = (int)(12 * dpiScale);

            int x = Math.Max(workArea.X, workArea.X + workArea.Width - width - margin);
            int y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height - margin);

            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            
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
