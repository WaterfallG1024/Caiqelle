using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Caiqelle.Services
{
    public record DetectedActionField(
        string FieldId,
        string Label,
        string MaskedValue,
        string PlainValue,
        Point TargetScreenPoint,
        bool HasPlaceholder = false);

    public record ActionDetectionContext(
        IntPtr WindowHwnd,
        string WindowTitle,
        IReadOnlyList<DetectedActionField> Fields);

    /// <summary>
    /// 画面のアクティブコンテキストを軽量監視し、Google フォーム等の入力欄を OCR 検出して
    /// 確認用ピル UI 経由で安全に自動入力を実行するサービス
    /// </summary>
    public class ActionService : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly OcrService _ocrService;
        private readonly ScreenshotService _screenshotService;
        private readonly NotificationService _notificationService;

        private Timer? _monitorTimer;
        private Windows.Graphics.Imaging.SoftwareBitmap? _reuseOcrBitmap;
        private int _isScanning = 0;
        private int _isExecuting = 0;
        private string _lastActiveContextKey = string.Empty;
        private string _lastNotifiedContextKey = string.Empty;
        private string _lastNotifiedFieldSignature = string.Empty;
        private int _consecutiveZeroDetections = 0;
        private ulong _lastScannedScreenHash = 0;
        private DateTime _contextStableSinceUtc = DateTime.UtcNow;
        private DateTime _suppressScanUntilUtc = DateTime.MinValue;

        private readonly HashSet<string> _dismissedContextKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _filledFieldIdsByContext = new(StringComparer.Ordinal);
        private readonly object _stateLock = new();

        private static readonly string[] BrowserProcessNames =
        {
            "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "arc"
        };

        private static readonly string[] FormPlaceholderHints =
        {
            "回答を入力", "回答を入カ", "回答を人カ", "回答を人力", "回答を",
            "your answer", "テキストを入力", "テキストを入カ"
        };

        private static readonly string[] FormContextHints =
        {
            "google フォーム", "google forms", "回答を入力", "回答を入カ", "your answer",
            "フォームをクリア", "clear form", "送信", "submit", "必須の質問", "アカウントを切り替える"
        };

        private static readonly char[] KeywordSeparators =
        {
            ',', '，'
        };

        public event Action<ActionDetectionContext>? DetectionReady;
        public event Action? ContextInvalidated;
        public Func<bool>? IsPillCurrentlyVisible { get; set; }

        public ActionService(
            SettingsManager settingsManager,
            OcrService ocrService,
            ScreenshotService screenshotService,
            NotificationService notificationService)
        {
            _settingsManager = settingsManager;
            _ocrService = ocrService;
            _screenshotService = screenshotService;
            _notificationService = notificationService;
        }

        public void StartMonitoring()
        {
            _monitorTimer ??= new Timer(MonitorTick, null, 250, 160);
        }

        public void StopMonitoring()
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        public void ClearCompletedHistory()
        {
            lock (_stateLock)
            {
                _dismissedContextKeys.Clear();
                _filledFieldIdsByContext.Clear();
                _lastScannedScreenHash = 0;
                _lastNotifiedContextKey = string.Empty;
                _lastNotifiedFieldSignature = string.Empty;
            }
        }

        /// <summary>
        /// 指定したコンテキスト（HWND＋タブタイトル）をスキップ済みとして記録する
        /// </summary>
        public void MarkContextCompleted(IntPtr hwnd, string windowTitle)
        {
            string key = BuildContextKey(hwnd, windowTitle);
            lock (_stateLock)
            {
                _dismissedContextKeys.Add(key);
            }
        }

        /// <summary>
        /// ピルが自動非表示（タイムアウト）になった際、一定時間後に再検知できるよう通知状態と画面ハッシュをリセットする
        /// </summary>
        public void NotifyPillTimedOut()
        {
            lock (_stateLock)
            {
                _lastNotifiedFieldSignature = string.Empty;
                _lastScannedScreenHash = 0;
                _consecutiveZeroDetections = 0;
                _suppressScanUntilUtc = DateTime.UtcNow.AddSeconds(2);
            }
        }

        private void MonitorTick(object? state)
        {
            if (!_settingsManager.IsActionAutoDetectEnabled) return;
            if (_settingsManager.ActionMappings.Count == 0) return;
            if (Volatile.Read(ref _isExecuting) == 1) return;
            if (IsPillCurrentlyVisible?.Invoke() == true) return;
            if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ScanActiveWindowIfNeededAsync(force: false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ActionService] Monitor error: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _isScanning, 0);
                }
            });
        }

        /// <summary>
        /// 手動テストまたは即時スキャン用
        /// </summary>
        public async Task<ActionDetectionContext?> ScanActiveWindowNowAsync()
        {
            if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
                return null;

            try
            {
                return await ScanActiveWindowIfNeededAsync(force: true);
            }
            finally
            {
                Interlocked.Exchange(ref _isScanning, 0);
            }
        }

        private async Task<ActionDetectionContext?> ScanActiveWindowIfNeededAsync(bool force)
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return null;

            if (_settingsManager.IsActionOnlyOnBrowsers && !IsBrowserProcess(pid))
            {
                NotifyContextChangedIfNeeded(string.Empty);
                return null;
            }

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return null;

            string contextKey = BuildContextKey(hwnd, title);
            bool contextChanged = NotifyContextChangedIfNeeded(contextKey);

            if (!force)
            {
                if (DateTime.UtcNow < _suppressScanUntilUtc)
                {
                    return null;
                }

                lock (_stateLock)
                {
                    if (_dismissedContextKeys.Contains(contextKey))
                    {
                        return null;
                    }
                }

                // タブ切り替え直後はページ描画が落ち着くまで約 80ms 待機
                if (contextChanged || (DateTime.UtcNow - _contextStableSinceUtc).TotalMilliseconds < 80)
                {
                    return null;
                }
            }

            if (!GetWindowRect(hwnd, out RECT rect)) return null;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 200 || height < 200) return null;

            var bounds = new Rectangle(rect.Left, rect.Top, width, height);
            var capturedBitmap = _screenshotService.CaptureRegionToSoftwareBitmap(bounds, _reuseOcrBitmap);
            if (capturedBitmap == null) return null;
            _reuseOcrBitmap = capturedBitmap;

            ulong screenHash = ComputeSoftwareBitmapQuickHash(capturedBitmap);
            if (!force)
            {
                lock (_stateLock)
                {
                    if (_lastScannedScreenHash == screenHash && _lastActiveContextKey == contextKey)
                    {
                        // 画面に変化がなければ OCR をスキップ
                        return null;
                    }
                    _lastScannedScreenHash = screenHash;
                }
            }

            var lines = await _ocrService.ExtractLinesWithBoundsAsync(capturedBitmap);
            if (lines.Count == 0) return null;

            // OCR 解析中に自動入力が開始／完了していた場合は古い結果を破棄（2重検知・2重入力防止）
            if (!force && (Volatile.Read(ref _isExecuting) == 1 ||
                           DateTime.UtcNow < _suppressScanUntilUtc ||
                           IsPillCurrentlyVisible?.Invoke() == true ||
                           GetForegroundWindow() != hwnd))
            {
                return null;
            }

            var detected = DetectFormFields(lines, title, bounds.Location, _settingsManager.ActionMappings);

            // 同一コンテキストで既に自動入力済みの項目は、カーソル点滅や OCR 誤認識で再検知・重複入力されないよう除外
            // （フォーム送信後などで一時的にフォーム外画面へ遷移した場合は履歴を自動クリア）
            bool isFormScreen = (!string.IsNullOrWhiteSpace(title) &&
                (title.Contains("Google フォーム", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("Google Forms", StringComparison.OrdinalIgnoreCase))) ||
                lines.Any(l => FormContextHints.Any(h => l.Text.Contains(h, StringComparison.OrdinalIgnoreCase)));

            lock (_stateLock)
            {
                if (!isFormScreen)
                {
                    _filledFieldIdsByContext.Remove(contextKey);
                }
                else if (_filledFieldIdsByContext.TryGetValue(contextKey, out var filledIds) && filledIds.Count > 0)
                {
                    // 「フォームをクリア」等で「回答を入力」プレースホルダーが明確に再出現した項目は入力済み履歴を自動解除
                    foreach (var f in detected)
                    {
                        if (f.HasPlaceholder)
                        {
                            filledIds.Remove(f.FieldId);
                        }
                    }
                    detected = detected.Where(f => !filledIds.Contains(f.FieldId)).ToList();
                }
            }

            WriteScanDiagnosticLog(title, bounds, lines, _settingsManager.ActionMappings, detected);
            if (detected.Count == 0)
            {
                lock (_stateLock)
                {
                    if (_lastNotifiedContextKey == contextKey)
                    {
                        _consecutiveZeroDetections++;
                        // 1回の OCR 揺らぎで即リセットされないよう、連続2回 0件を観測した場合のみリセット
                        if (_consecutiveZeroDetections >= 2)
                        {
                            _lastNotifiedFieldSignature = string.Empty;
                        }
                    }
                }
                return null;
            }

            string fieldSignature = string.Join("|", detected.Select(f => f.FieldId).OrderBy(id => id, StringComparer.Ordinal));
            if (!force)
            {
                lock (_stateLock)
                {
                    _consecutiveZeroDetections = 0;
                    if (_lastNotifiedContextKey == contextKey && _lastNotifiedFieldSignature == fieldSignature)
                    {
                        // 既に同じ項目セットを検知・通知済みのため繰り返し発火しない
                        return null;
                    }
                    _lastNotifiedContextKey = contextKey;
                    _lastNotifiedFieldSignature = fieldSignature;
                }
            }

            var context = new ActionDetectionContext(hwnd, title, detected);
            DetectionReady?.Invoke(context);
            return context;
        }

        private bool NotifyContextChangedIfNeeded(string newKey)
        {
            bool changed = false;
            lock (_stateLock)
            {
                if (_lastActiveContextKey != newKey)
                {
                    _lastActiveContextKey = newKey;
                    _lastScannedScreenHash = 0;
                    _lastNotifiedContextKey = string.Empty;
                    _lastNotifiedFieldSignature = string.Empty;
                    _consecutiveZeroDetections = 0;
                    _contextStableSinceUtc = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                ContextInvalidated?.Invoke();
            }
            return changed;
        }

        private static unsafe ulong ComputeSoftwareBitmapQuickHash(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
        {
            ulong hash = 14695981039346656037UL;
            try
            {
                using var buffer = bitmap.LockBuffer(Windows.Graphics.Imaging.BitmapBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                var desc = buffer.GetPlaneDescription(0);
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                if (byteAccess != null)
                {
                    byteAccess.GetBuffer(out byte* data, out uint capacity);
                    uint step = Math.Max(16u, capacity / 512u);
                    for (uint i = 0; i < capacity; i += step)
                    {
                        hash ^= data[i];
                        hash *= 1099511628211UL;
                    }
                }
                hash ^= ((ulong)desc.Width << 32) | (uint)desc.Height;
            }
            catch { }
            return hash;
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        private sealed record ParsedActionMapping(
            ActionFieldMapping Mapping,
            string PlainValue,
            string[] Keywords);

        private sealed record FieldMatchCandidate(
            ParsedActionMapping ParsedMapping,
            OcrLineBox LabelLine,
            OcrLineBox? PlaceholderLine,
            string DisplayLabel,
            int MatchQuality,
            int Score);

        /// <summary>
        /// OCR 行と登録済みマッピングから、登録順やフォームの順番に依存せず
        /// 画面上に存在する未入力フォーム項目を検出する
        /// </summary>
        public static IReadOnlyList<DetectedActionField> DetectFormFields(
            IReadOnlyList<OcrLineBox> lines,
            string windowTitle,
            Point windowOrigin,
            IReadOnlyList<ActionFieldMapping> mappings)
        {
            if (lines == null || lines.Count == 0 || mappings == null || mappings.Count == 0)
                return Array.Empty<DetectedActionField>();

            // 1. フォーム存在チェック（フォーム固有シグナルが一切ない通常ページ・記事は無視）
            bool hasFormTitle = !string.IsNullOrWhiteSpace(windowTitle) &&
                (windowTitle.Contains("Google フォーム", StringComparison.OrdinalIgnoreCase) ||
                 windowTitle.Contains("Google Forms", StringComparison.OrdinalIgnoreCase));

            bool hasFormSignalInOcr = lines.Any(l =>
            {
                string norm = l.Text.Trim();
                return FormContextHints.Any(h => norm.Contains(h, StringComparison.OrdinalIgnoreCase));
            });

            if (!hasFormTitle && !hasFormSignalInOcr)
            {
                return Array.Empty<DetectedActionField>();
            }

            // 2. 有効なマッピングと複数キーワードを前処理
            var parsedMappings = new List<ParsedActionMapping>(mappings.Count);
            var seenMappingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in mappings)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id) || !seenMappingIds.Add(m.Id)) continue;
                string plain = m.PlainValue;
                if (string.IsNullOrWhiteSpace(m.Keywords) || string.IsNullOrEmpty(plain)) continue;

                string[] kws = m.Keywords
                    .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (kws.Length == 0) continue;

                parsedMappings.Add(new ParsedActionMapping(m, plain, kws));
            }

            if (parsedMappings.Count == 0)
                return Array.Empty<DetectedActionField>();

            // 3. 画面上の全 OCR 行を Y 座標順に整列し、質問ラベル候補行を特定する
            var orderedLines = lines
                .OrderBy(l => l.Bounds.Top)
                .ThenBy(l => l.Bounds.Left)
                .ToList();

            var questionBoundaryLines = new HashSet<OcrLineBox>();
            OcrLineBox? lastBoundaryLine = null;
            foreach (var line in orderedLines)
            {
                string raw = line.Text.Trim();
                if (string.IsNullOrEmpty(raw) || IsPlaceholderLine(raw)) continue;

                string clean = NormalizeLabel(raw);
                if (string.IsNullOrEmpty(clean) || clean.Length > 40) continue;

                // 直前の質問ラベル行のすぐ下（45px以内・左端が近い）に続く行は同一質問カード内の補足説明文であり、
                // 次の質問境界ではないためスキップする（「7桁の出席番号〜です。」等で入力値検出が分断されるのを防ぐ）
                if (lastBoundaryLine != null &&
                    Math.Abs(line.Bounds.Left - lastBoundaryLine.Bounds.Left) <= 40 &&
                    line.Bounds.Top - lastBoundaryLine.Bounds.Bottom <= 45)
                {
                    continue;
                }

                bool endsWithRequiredStar = raw.EndsWith("*", StringComparison.Ordinal) || raw.EndsWith("＊", StringComparison.Ordinal);
                bool matchesAnyMapping = parsedMappings.Any(pm => EvaluateKeywordMatch(clean, pm.Keywords, out _) > 0);
                if (endsWithRequiredStar || matchesAnyMapping)
                {
                    questionBoundaryLines.Add(line);
                    lastBoundaryLine = line;
                }
            }

            // 4. 各質問ラベルと直下の「回答を入力」プレースホルダーから標準的な縦オフセットを事前計測
            int? observedPlaceholderDeltaY = null;
            foreach (var line in orderedLines)
            {
                if (!questionBoundaryLines.Contains(line)) continue;
                var ph = FindDirectPlaceholderForLabel(line, orderedLines, questionBoundaryLines, null);
                if (ph != null)
                {
                    int centerY = ph.Bounds.Top + ph.Bounds.Height / 2;
                    int delta = centerY - line.Bounds.Bottom;
                    if (delta >= 20 && delta <= 100)
                    {
                        observedPlaceholderDeltaY = Math.Clamp(delta, 30, 85);
                        break;
                    }
                }
            }

            // 5. 登録順やキーワード記述順に一切影響されないよう、全 (マッピング × 画面上の行) 候補をスコア評価
            var candidates = new List<FieldMatchCandidate>();

            foreach (var line in orderedLines)
            {
                string rawText = line.Text.Trim();
                if (string.IsNullOrEmpty(rawText) || IsPlaceholderLine(rawText)) continue;
                if (FormContextHints.Any(h => rawText.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;

                string cleanLabel = NormalizeLabel(rawText);
                if (string.IsNullOrEmpty(cleanLabel) || cleanLabel.Length > 40) continue;

                // このラベル行から「次の質問ラベル行の手前まで」の直下領域を取得（別質問の入力欄を絶対に奪わない）
                var belowLines = GetBoundedBelowLines(line, orderedLines, questionBoundaryLines);
                var placeholderLine = belowLines.FirstOrDefault(b => IsPlaceholderLine(b.Text));

                // プレースホルダーが直下にない場合のタイトル行・説明文フィルタ
                if (placeholderLine == null && IsLikelyNonQuestionTitleOrSentence(cleanLabel))
                {
                    continue;
                }

                foreach (var pm in parsedMappings)
                {
                    int matchQuality = EvaluateKeywordMatch(cleanLabel, pm.Keywords, out string matchedKw);
                    if (matchQuality <= 0) continue;

                    // 既に同じ入力値が入っている場合はスキップ（「回答を入力」がある場合は確実に未入力）
                    if (placeholderLine == null && IsAlreadyFilledWithValue(belowLines, pm.PlainValue))
                    {
                        continue;
                    }

                    // スコア計算：
                    // - 直下に「回答を入力」プレースホルダーが存在する質問カードを最優先 (+1000)
                    // - 完全一致 (300) > 先頭/装飾除去一致 (200) > 部分一致 (100)
                    // - 余計な説明文より短い純粋なラベルを優先 (-cleanLabel.Length)
                    int score = (placeholderLine != null ? 1000 : 0)
                                + (matchQuality * 100)
                                - cleanLabel.Length;

                    candidates.Add(new FieldMatchCandidate(
                        pm,
                        line,
                        placeholderLine,
                        pm.Keywords[0],
                        matchQuality,
                        score));
                }
            }

            if (candidates.Count == 0)
                return Array.Empty<DetectedActionField>();

            // 6. スコア降順（同スコアなら画面の上から順）で、マッピング・ラベル行・プレースホルダー行を1対1に確定
            var orderedCandidates = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.LabelLine.Bounds.Top)
                .ThenBy(c => c.LabelLine.Bounds.Left)
                .ToList();

            var results = new List<DetectedActionField>();
            var usedFieldIds = new HashSet<string>(StringComparer.Ordinal);
            var usedLabelLines = new HashSet<OcrLineBox>();
            var usedPlaceholderLines = new HashSet<OcrLineBox>();

            foreach (var cand in orderedCandidates)
            {
                string mappingId = cand.ParsedMapping.Mapping.Id;
                if (usedFieldIds.Contains(mappingId)) continue;
                if (usedLabelLines.Contains(cand.LabelLine)) continue;

                OcrLineBox? ph = cand.PlaceholderLine;
                if (ph != null && usedPlaceholderLines.Contains(ph))
                {
                    ph = null;
                }

                int fallbackDeltaY = observedPlaceholderDeltaY ?? Math.Max(46, (int)(cand.LabelLine.Bounds.Height * 2.3));
                Point targetRelative = ph != null
                    ? new Point(ph.Bounds.Left + Math.Min(32, Math.Max(12, ph.Bounds.Width / 2)),
                                ph.Bounds.Top + ph.Bounds.Height / 2)
                    : new Point(cand.LabelLine.Bounds.Left + 32,
                                cand.LabelLine.Bounds.Bottom + fallbackDeltaY);

                Point screenPoint = new Point(windowOrigin.X + targetRelative.X, windowOrigin.Y + targetRelative.Y);

                results.Add(new DetectedActionField(
                    mappingId,
                    cand.DisplayLabel,
                    DpapiHelper.MaskValue(cand.ParsedMapping.PlainValue),
                    cand.ParsedMapping.PlainValue,
                    screenPoint,
                    HasPlaceholder: ph != null));

                usedFieldIds.Add(mappingId);
                usedLabelLines.Add(cand.LabelLine);
                if (ph != null)
                {
                    usedPlaceholderLines.Add(ph);
                }
            }

            // 画面の上から順に入力できるよう Y 座標順に整列
            return results.OrderBy(r => r.TargetScreenPoint.Y).ToList();
        }

        private static bool IsPlaceholderLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return FormPlaceholderHints.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsLikelyNonQuestionTitleOrSentence(string cleanLabel)
        {
            string[] nonQuestionHints =
            {
                "フォーム", "form", "アンケート", "記録され", "関連付け",
                "パスワード", "送信しない", "アカウント", "必須の質問"
            };
            return nonQuestionHints.Any(h => cleanLabel.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        private static void WriteScanDiagnosticLog(
            string windowTitle,
            Rectangle bounds,
            IReadOnlyList<OcrLineBox> lines,
            IReadOnlyList<ActionFieldMapping> mappings,
            IReadOnlyList<DetectedActionField> detected)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Caiqelle");
                string path = System.IO.Path.Combine(dir, "action_last_scan.log");
                var sb = new StringBuilder();
                sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"Title: {windowTitle}");
                sb.AppendLine($"Bounds: {bounds}");
                sb.AppendLine($"Mappings ({mappings.Count}):");
                foreach (var m in mappings)
                {
                    sb.AppendLine($"  - Id={m.Id} Keywords=[{m.Keywords}] HasPlainValue={!string.IsNullOrEmpty(m.PlainValue)}");
                }
                sb.AppendLine($"OcrLines ({lines.Count}):");
                foreach (var l in lines)
                {
                    sb.AppendLine($"  - [{l.Bounds.X},{l.Bounds.Y},{l.Bounds.Width}x{l.Bounds.Height}] \"{l.Text}\"");
                }
                sb.AppendLine($"Detected ({detected.Count}):");
                foreach (var d in detected)
                {
                    sb.AppendLine($"  - Id={d.FieldId} Label=[{d.Label}] Point=({d.TargetScreenPoint.X},{d.TargetScreenPoint.Y})");
                }
                System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static bool IsAlreadyFilledWithValue(IReadOnlyList<OcrLineBox> belowLines, string plainValue)
        {
            if (string.IsNullOrEmpty(plainValue) || belowLines.Count == 0) return false;
            string compactPlain = plainValue.Replace(" ", string.Empty).Replace("　", string.Empty);

            foreach (var b in belowLines)
            {
                string raw = b.Text.Trim();
                if (string.IsNullOrEmpty(raw) || IsPlaceholderLine(raw)) continue;

                // 点滅キャレットが文字に重なって '|' 等が混入した場合の正規化
                string text = raw.Replace("|", string.Empty).Replace("｜", string.Empty).Replace("¦", string.Empty);

                if (text.Contains(plainValue, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(compactPlain))
                {
                    string compactText = text.Replace(" ", string.Empty).Replace("　", string.Empty);
                    if (compactText.Contains(compactPlain, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static List<OcrLineBox> GetBoundedBelowLines(
            OcrLineBox labelLine,
            IReadOnlyList<OcrLineBox> orderedLines,
            HashSet<OcrLineBox> questionBoundaryLines)
        {
            int searchTop = labelLine.Bounds.Bottom;
            int defaultBottom = labelLine.Bounds.Bottom + Math.Max(150, labelLine.Bounds.Height * 6);
            int searchLeft = labelLine.Bounds.Left - 120;
            int searchRight = Math.Max(labelLine.Bounds.Right + 300, labelLine.Bounds.Left + 460);

            // 次の質問ラベル行が存在する場合、その上端より下へは絶対に越境しない
            int nextQuestionTop = int.MaxValue;
            foreach (var other in orderedLines)
            {
                if (ReferenceEquals(other, labelLine)) continue;
                if (!questionBoundaryLines.Contains(other)) continue;
                if (other.Bounds.Top < labelLine.Bounds.Bottom + 8) continue;
                if (other.Bounds.Left < searchLeft || other.Bounds.Left > searchRight) continue;

                if (other.Bounds.Top < nextQuestionTop)
                {
                    nextQuestionTop = other.Bounds.Top;
                }
            }

            int searchBottom = Math.Min(defaultBottom, nextQuestionTop - 2);

            return orderedLines.Where(other =>
                !ReferenceEquals(other, labelLine) &&
                other.Bounds.Top >= searchTop - 4 &&
                other.Bounds.Top <= searchBottom &&
                other.Bounds.Left >= searchLeft &&
                other.Bounds.Left <= searchRight)
                .OrderBy(other => other.Bounds.Top)
                .ToList();
        }

        private static OcrLineBox? FindDirectPlaceholderForLabel(
            OcrLineBox labelLine,
            IReadOnlyList<OcrLineBox> orderedLines,
            HashSet<OcrLineBox> questionBoundaryLines,
            HashSet<OcrLineBox>? usedPlaceholders)
        {
            var below = GetBoundedBelowLines(labelLine, orderedLines, questionBoundaryLines);
            return below.FirstOrDefault(b =>
                (usedPlaceholders == null || !usedPlaceholders.Contains(b)) &&
                IsPlaceholderLine(b.Text));
        }

        /// <summary>
        /// ラベル文字列に対して登録キーワード群（順番不問）の最高一致度を返す
        /// 3: 完全一致, 2: 敬称・番号・括弧装飾を除いた一致または先頭一致, 1: 部分一致, 0: 不一致
        /// </summary>
        private static int EvaluateKeywordMatch(string cleanLabel, string[] keywords, out string bestKeyword)
        {
            bestKeyword = string.Empty;
            if (string.IsNullOrEmpty(cleanLabel) || keywords == null || keywords.Length == 0)
                return 0;

            string compactLabel = cleanLabel.Replace(" ", string.Empty).Replace("　", string.Empty);
            string coreLabel = StripLabelDecorations(compactLabel);

            int bestQuality = 0;
            foreach (var k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                string trimK = k.Trim();
                string compactK = trimK.Replace(" ", string.Empty).Replace("　", string.Empty);
                if (string.IsNullOrEmpty(compactK)) continue;

                int quality = 0;
                if (cleanLabel.Equals(trimK, StringComparison.OrdinalIgnoreCase) ||
                    compactLabel.Equals(compactK, StringComparison.OrdinalIgnoreCase))
                {
                    quality = 3;
                }
                else if (coreLabel.Equals(compactK, StringComparison.OrdinalIgnoreCase) ||
                         compactLabel.StartsWith(compactK, StringComparison.OrdinalIgnoreCase) ||
                         coreLabel.StartsWith(compactK, StringComparison.OrdinalIgnoreCase))
                {
                    quality = 2;
                }
                else if (cleanLabel.Contains(trimK, StringComparison.OrdinalIgnoreCase) ||
                         compactLabel.Contains(compactK, StringComparison.OrdinalIgnoreCase))
                {
                    quality = 1;
                }

                if (quality > bestQuality)
                {
                    bestQuality = quality;
                    bestKeyword = trimK;
                }
            }

            return bestQuality;
        }

        private static string StripLabelDecorations(string compactLabel)
        {
            string s = compactLabel;

            // 先頭の質問番号 ("1.", "Q1.", "問1" 等) を除去
            int idx = 0;
            if (idx < s.Length && (s[idx] == 'Q' || s[idx] == 'q' || s[idx] == '問'))
            {
                idx++;
            }
            while (idx < s.Length && char.IsDigit(s[idx]))
            {
                idx++;
            }
            if (idx > 0 && idx < s.Length && (s[idx] == '.' || s[idx] == '．' || s[idx] == ':' || s[idx] == '：' || s[idx] == ')' || s[idx] == '）'))
            {
                s = s[(idx + 1)..];
            }

            // 末尾の「必須」や括弧書き補足 ("（フルネーム）", "(漢字)" 等) を除去
            if (s.EndsWith("必須", StringComparison.Ordinal))
            {
                s = s[..^2];
            }
            int parenIdx = s.IndexOfAny(new[] { '(', '（', '[', '【' });
            if (parenIdx > 0)
            {
                s = s[..parenIdx];
            }

            // 先頭の敬称 ("お", "ご", "御") を除去
            if (s.Length > 1 && (s[0] == 'お' || s[0] == 'ご' || s[0] == '御'))
            {
                s = s[1..];
            }

            return s.Trim();
        }

        private static string NormalizeLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw.Trim();
            s = s.TrimEnd('*', '＊', ':', '：', ' ');
            return s.Trim();
        }

        /// <summary>
        /// ユーザーの承認後に、対象ウィンドウの同一性を再検証したうえで自動入力を実行する
        /// </summary>
        public async Task<bool> ExecuteFillAsync(ActionDetectionContext context)
        {
            if (context == null || context.Fields.Count == 0) return false;
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return false;

            string? previousClipboardText = null;
            bool hadPreviousText = false;
            var fillLog = new StringBuilder();
            fillLog.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            fillLog.AppendLine($"TargetHwnd: 0x{context.WindowHwnd.ToInt64():X} Title: {context.WindowTitle} Fields: {context.Fields.Count}");

            try
            {
                // 1. 実行直前にフォアグラウンドウィンドウの同一性を検証し、必要時のみフォーカス復帰を待機
                IntPtr fg = GetForegroundWindow();
                if (fg != context.WindowHwnd)
                {
                    SetForegroundWindow(context.WindowHwnd);
                    await Task.Delay(70);
                    fg = GetForegroundWindow();
                }
                else
                {
                    await Task.Delay(20);
                }

                if (fg != context.WindowHwnd)
                {
                    fillLog.AppendLine($"Abort: Foreground mismatch (actual=0x{fg.ToInt64():X})");
                    return false;
                }

                string currentTitle = GetWindowTitle(fg);
                if (!string.Equals(currentTitle, context.WindowTitle, StringComparison.Ordinal))
                {
                    fillLog.AppendLine($"Abort: Title mismatch (actual={currentTitle})");
                    return false;
                }

                IntPtr ownHwnd = GetOwnOwnerHwnd();
                fillLog.AppendLine($"OwnHwnd: 0x{ownHwnd.ToInt64():X}");

                // 2. 元のクリップボードテキストを退避
                if (_settingsManager.ActionRestoreClipboardAfterFill)
                {
                    (hadPreviousText, previousClipboardText) = TryGetClipboardText(ownHwnd);
                }

                // 3. 各入力欄へ順番に入力（クリックでフォーカス確定 → クリップボード設定 → Ctrl+V）
                for (int i = 0; i < context.Fields.Count; i++)
                {
                    var field = context.Fields[i];
                    IntPtr loopFg = GetForegroundWindow();
                    if (loopFg != context.WindowHwnd)
                    {
                        fillLog.AppendLine($"[{i}] Abort: Foreground changed to 0x{loopFg.ToInt64():X}");
                        return false;
                    }

                    Point targetPoint = field.TargetScreenPoint;
                    if (i > 0)
                    {
                        // 前の項目入力によるバリデーションエラー表示等でレイアウトが上下にズレる場合があるため、
                        // 現在の画面から対象入力欄の最新座標を再検出する
                        Point? refreshed = await TryRefreshFieldScreenPointAsync(context.WindowHwnd, context.WindowTitle, field.FieldId);
                        if (refreshed.HasValue)
                        {
                            fillLog.AppendLine($"[{i}] Refreshed [{field.Label}] point: ({targetPoint.X},{targetPoint.Y}) -> ({refreshed.Value.X},{refreshed.Value.Y})");
                            targetPoint = refreshed.Value;
                        }
                    }

                    if (!SetClipboardTextProtected(ownHwnd, field.PlainValue, excludeFromHistory: true))
                    {
                        fillLog.AppendLine($"[{i}] Abort: SetClipboardTextProtected failed for [{field.Label}]");
                        return false;
                    }

                    SetCursorPos(targetPoint.X, targetPoint.Y);
                    SendMouseMoveNudge();
                    await Task.Delay(15);
                    await SendMouseLeftClickAsync();
                    await Task.Delay(70);

                    await SendCtrlVAsync();
                    bool hasNextField = i + 1 < context.Fields.Count;
                    await Task.Delay(hasNextField ? 160 : 90);
                    fillLog.AppendLine($"[{i}] Filled [{field.Label}] at ({targetPoint.X},{targetPoint.Y}) len={field.PlainValue.Length}");
                }

                // 入力直後の再描画を短時間だけ抑制し、同じ項目が再検知されないよう直近コンテキストと入力済み項目を維持
                _suppressScanUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
                string filledContextKey = BuildContextKey(context.WindowHwnd, context.WindowTitle);
                lock (_stateLock)
                {
                    _lastScannedScreenHash = 0;
                    _lastNotifiedContextKey = filledContextKey;
                    _lastNotifiedFieldSignature = string.Join("|", context.Fields.Select(f => f.FieldId).OrderBy(id => id, StringComparer.Ordinal));

                    if (!_filledFieldIdsByContext.TryGetValue(filledContextKey, out var filledSet))
                    {
                        filledSet = new HashSet<string>(StringComparer.Ordinal);
                        _filledFieldIdsByContext[filledContextKey] = filledSet;
                    }
                    foreach (var f in context.Fields)
                    {
                        filledSet.Add(f.FieldId);
                    }
                }
                _notificationService.ShowNotification($"{context.Fields.Count}件のフォーム項目を入力しました", "\uE73E");
                fillLog.AppendLine("Completed: true");
                return true;
            }
            catch (Exception ex)
            {
                fillLog.AppendLine($"Exception: {ex}");
                Debug.WriteLine($"[ActionService] ExecuteFill error: {ex.Message}");
                return false;
            }
            finally
            {
                WriteFillDiagnosticLog(fillLog.ToString());
                if (_settingsManager.ActionRestoreClipboardAfterFill)
                {
                    IntPtr ownHwnd = GetOwnOwnerHwnd();
                    if (hadPreviousText && previousClipboardText != null)
                    {
                        SetClipboardTextProtected(ownHwnd, previousClipboardText, excludeFromHistory: false);
                    }
                    else
                    {
                        ClearClipboard(ownHwnd);
                    }
                }
                Interlocked.Exchange(ref _isExecuting, 0);
            }
        }

        private async Task<Point?> TryRefreshFieldScreenPointAsync(IntPtr hwnd, string windowTitle, string fieldId)
        {
            try
            {
                if (!GetWindowRect(hwnd, out RECT rect)) return null;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width < 200 || height < 200) return null;

                var bounds = new Rectangle(rect.Left, rect.Top, width, height);
                var captured = _screenshotService.CaptureRegionToSoftwareBitmap(bounds, _reuseOcrBitmap);
                if (captured == null) return null;
                _reuseOcrBitmap = captured;

                var lines = await _ocrService.ExtractLinesWithBoundsAsync(captured);
                if (lines.Count == 0) return null;

                var detected = DetectFormFields(lines, windowTitle, bounds.Location, _settingsManager.ActionMappings);
                var match = detected.FirstOrDefault(f => string.Equals(f.FieldId, fieldId, StringComparison.Ordinal));
                return match?.TargetScreenPoint;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFillDiagnosticLog(string content)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Caiqelle");
                string path = System.IO.Path.Combine(dir, "action_last_fill.log");
                System.IO.File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch { }
        }

        private static IntPtr GetOwnOwnerHwnd()
        {
            try
            {
                var mainWindow = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow;
                if (mainWindow != null)
                {
                    return WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        private static string BuildContextKey(IntPtr hwnd, string title) => $"{hwnd.ToInt64()}|{title}";

        private static bool IsBrowserProcess(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;
                return BrowserProcessNames.Any(b => name.Equals(b, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        #region Win32 Input & Protected Clipboard

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private static readonly uint CF_CAN_INCLUDE_IN_HISTORY = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        private static readonly uint CF_CAN_UPLOAD_TO_CLOUD = RegisterClipboardFormat("CanUploadToCloudClipboard");

        private static (bool hasText, string? text) TryGetClipboardText(IntPtr ownerHwnd)
        {
            try
            {
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return (false, null);
                if (!OpenClipboard(ownerHwnd)) return (false, null);
                try
                {
                    IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                    if (hData == IntPtr.Zero) return (false, null);
                    IntPtr ptr = GlobalLock(hData);
                    if (ptr == IntPtr.Zero) return (false, null);
                    try
                    {
                        return (true, Marshal.PtrToStringUni(ptr));
                    }
                    finally
                    {
                        GlobalUnlock(hData);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return (false, null);
            }
        }

        private static bool SetClipboardTextProtected(IntPtr ownerHwnd, string text, bool excludeFromHistory)
        {
            for (int i = 0; i < 15; i++)
            {
                if (OpenClipboard(ownerHwnd))
                {
                    try
                    {
                        EmptyClipboard();
                        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                        if (!SetClipboardBytes(CF_UNICODETEXT, bytes)) return false;

                        if (excludeFromHistory)
                        {
                            byte[] zeroDword = BitConverter.GetBytes(0);
                            if (CF_CAN_INCLUDE_IN_HISTORY != 0) SetClipboardBytes(CF_CAN_INCLUDE_IN_HISTORY, zeroDword);
                            if (CF_CAN_UPLOAD_TO_CLOUD != 0) SetClipboardBytes(CF_CAN_UPLOAD_TO_CLOUD, zeroDword);
                        }
                        return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                Thread.Sleep(20);
            }
            return false;
        }

        private static void ClearClipboard(IntPtr ownerHwnd)
        {
            try
            {
                if (OpenClipboard(ownerHwnd))
                {
                    try { EmptyClipboard(); }
                    finally { CloseClipboard(); }
                }
            }
            catch { }
        }

        private static bool SetClipboardBytes(uint format, byte[] data)
        {
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (hGlobal == IntPtr.Zero) return false;
            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            try { Marshal.Copy(data, 0, ptr, data.Length); }
            finally { GlobalUnlock(hGlobal); }

            if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            return true;
        }

        private static void SendMouseMoveNudge()
        {
            var input = new INPUT[1];
            input[0].type = INPUT_MOUSE;
            input[0].U.mi.dwFlags = MOUSEEVENTF_MOVE;
            input[0].U.mi.dx = 0;
            input[0].U.mi.dy = 0;
            SendInput(1, input, Marshal.SizeOf<INPUT>());
        }

        private static async Task SendMouseLeftClickAsync()
        {
            var down = new INPUT[1];
            down[0].type = INPUT_MOUSE;
            down[0].U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            SendInput(1, down, Marshal.SizeOf<INPUT>());

            await Task.Delay(25);

            var up = new INPUT[1];
            up[0].type = INPUT_MOUSE;
            up[0].U.mi.dwFlags = MOUSEEVENTF_LEFTUP;
            SendInput(1, up, Marshal.SizeOf<INPUT>());
        }

        private static async Task SendCtrlVAsync()
        {
            const ushort VK_CONTROL = 0x11;
            const ushort VK_V = 0x56;

            var ctrlDown = new INPUT[1];
            ctrlDown[0].type = INPUT_KEYBOARD;
            ctrlDown[0].U.ki.wVk = VK_CONTROL;
            SendInput(1, ctrlDown, Marshal.SizeOf<INPUT>());
            await Task.Delay(10);

            var vPress = new INPUT[2];
            vPress[0].type = INPUT_KEYBOARD;
            vPress[0].U.ki.wVk = VK_V;
            vPress[1].type = INPUT_KEYBOARD;
            vPress[1].U.ki.wVk = VK_V;
            vPress[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(2, vPress, Marshal.SizeOf<INPUT>());
            await Task.Delay(10);

            var ctrlUp = new INPUT[1];
            ctrlUp[0].type = INPUT_KEYBOARD;
            ctrlUp[0].U.ki.wVk = VK_CONTROL;
            ctrlUp[0].U.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(1, ctrlUp, Marshal.SizeOf<INPUT>());
        }

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        #endregion

        public void Dispose()
        {
            StopMonitoring();
            _reuseOcrBitmap?.Dispose();
            _reuseOcrBitmap = null;
        }
    }
}
