using PhotoFlow.Core.Services;
using PhotoFlow.Desktop.Models;
using PhotoFlow.Ingest.FolderIngest;
using PhotoFlow.Licensing.Services;
using PhotoFlow.Processing.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PhotoFrame = PhotoFlow.Core.Domain.Frame;
using PhotoFlow.Licensing.Services;

namespace PhotoFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly ILicensingService _licensing;

    private IWorkspaceLayout _workspace;
    private ISessionManager _sessions;
    private IncomingFolderWatcher _watcher;
    private IFrameProcessor _processor;

    private readonly List<ExportPresetItem> _presetItems = new();

    private readonly HashSet<string> _defaultSelectedNames = new(StringComparer.OrdinalIgnoreCase)
{
    "Square 2000 JPG (White)",
    "Thumb 320 PNG (Transparent)"
};


    private string _incomingFolder;
    private string _settingsPath;

    // keep explicit workspace root so we can show/apply it safely
    private string _workspaceRoot;

    private bool _uiReady = false;
    private bool _isApplyingSettings = false;

    // ===== session-level profile (set once via first preview) =====
    private ProcessingProfile _sessionProfile = new ProcessingProfile
    {
        Method = BackgroundMethod.SimpleOverexposedToTransparent,
        WhiteThreshold = 245,
        Feather = 10
    };

    private bool _sessionProfileLocked = false;

    // ===== per-product overrides (Apply for current product) =====
    private readonly Dictionary<string, ProcessingProfile> _productOverrides = new(StringComparer.OrdinalIgnoreCase);

    // ===== thumbs per barcode (we show only active) =====
    private readonly Dictionary<string, List<ThumbnailItem>> _thumbsByBarcode = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();

    private readonly SemaphoreSlim _processGate = new(1, 1);
    private CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var appRoot = Path.Combine(documents, "PhotoFlow");
        Directory.CreateDirectory(appRoot);

        _settingsPath = Path.Combine(appRoot, "settings.json");

        // load settings early to get workspace/incoming
        var settings = LoadSettings();

        _workspaceRoot = !string.IsNullOrWhiteSpace(settings.WorkspaceRoot)
            ? settings.WorkspaceRoot!
            : Path.Combine(appRoot, "workspace");

        _incomingFolder = !string.IsNullOrWhiteSpace(settings.IncomingFolder)
            ? settings.IncomingFolder!
            : Path.Combine(appRoot, "incoming");

        Loaded += (_, _) =>
        {
            _uiReady = true;
            Log("UI loaded.");
            RefreshUI();
            RefreshThumbsForActiveBarcode();
            BarcodeTextBox.Focus();
            BarcodeTextBox.SelectAll();
        };

#if DEBUG
    // Debug: ако има лиценз файл -> Offline, иначе Dev
    var licPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PhotoFlow", "Licenses", "photoflow.license.json");

    _licensing = File.Exists(licPath)
        ? new OfflineLicensingService()
        : new DevLicensingService();
#else
        // Release/MSIX: винаги Offline
        _licensing = new OfflineLicensingService();
#endif

        LicenseStatusText.Text = _licensing.GetStatusText();
        Log("Licensing: " + _licensing.GetStatusText());



        _workspace = new WorkspaceLayout(_workspaceRoot);
        _sessions = new SessionManager(_workspace);
        _processor = new FrameProcessor(_workspace, _licensing);


        InitPresetsOnce();
        BindPresetsToUi();
        LoadSettingsApplySelection(); // exports selection only

        _watcher = new IncomingFolderWatcher(_incomingFolder);
        _watcher.Info += msg => Log(msg);
        _watcher.Error += msg => Log("ERROR: " + msg);
        _watcher.FileArrived += OnIncomingFile;
        _watcher.Start();

        FoldersStatusText.Text = $"Workspace: {_workspaceRoot}   |   Incoming: {_incomingFolder}";
        Log("Ready. Scan or type a barcode and press Enter.");
    }

    // ===== XAML event handlers =====

    private void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartOrSwitchFromInputs();
            e.Handled = true;
        }
    }

    private void Thumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ThumbnailItem item) return;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var session = _sessions.ActiveSession;
                if (session is null || !string.Equals(session.Barcode, item.Barcode, StringComparison.OrdinalIgnoreCase))
                {
                    Log("Thumb click: active product differs. Switch to that barcode first if you want to edit it.");
                    return;
                }

                var frame = session.Frames.FirstOrDefault(f =>
                    string.Equals(f.RawPath, item.FrameRawPath, StringComparison.OrdinalIgnoreCase));

                if (frame is null)
                {
                    Log("Thumb click: frame not found in active session.");
                    return;
                }

                await OpenPreviewForProductAsync(session.Barcode, frame);
            }
            catch (Exception ex)
            {
                Log("ERROR (thumb preview): " + ex.Message);
            }
        });
    }

    private void ExportCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings) return;
        SaveSettingsFromSelection();
    }

    private void ResetExports_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Reset exports to default selection?",
            "Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _isApplyingSettings = true;
        try
        {
            foreach (var p in _presetItems)
                p.IsSelected = _defaultSelectedNames.Contains(p.DisplayName);

            ExportsListBox.Items.Refresh();
            SaveSettingsFromSelection();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        Log("Exports reset to default.");
    }

    // IMPORTANT: this should open SettingsWindow (Folders UI), not raw dialogs
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // SettingsWindow must exist in PhotoFlow.Desktop namespace
            // and expose WorkspaceRoot/IncomingFolder + return DialogResult=true on Save.
            var w = new SettingsWindow(_workspaceRoot, _incomingFolder)
            {
                Owner = this
            };

            var ok = w.ShowDialog() == true;
            if (!ok) return;

            // validate paths
            var newWorkspaceRoot = (w.WorkspaceRoot ?? "").Trim();
            var newIncoming = (w.IncomingFolder ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newWorkspaceRoot) || !Directory.Exists(newWorkspaceRoot))
            {
                MessageBox.Show("Workspace folder is invalid.", "Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(newIncoming) || !Directory.Exists(newIncoming))
            {
                MessageBox.Show("Incoming folder is invalid.", "Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // persist + apply
            PersistFoldersAndSelection(newWorkspaceRoot, newIncoming);
            ApplyFoldersSoftRestart(newWorkspaceRoot, newIncoming);

            Log($"Folders saved: Workspace={newWorkspaceRoot} | Incoming={newIncoming}");
        }
        catch (Exception ex)
        {
            Log("ERROR (folders): " + ex.Message);
        }
    }

    private void PersistFoldersAndSelection(string newWorkspaceRoot, string newIncoming)
    {
        // preserve everything else; update folders + selection
        var settings = LoadSettings();
        settings.WorkspaceRoot = newWorkspaceRoot;
        settings.IncomingFolder = newIncoming;

        settings.SelectedExportNames = _presetItems
            .Where(p => p.IsSelected)
            .Select(p => p.DisplayName)
            .ToList();

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private void ApplyFoldersSoftRestart(string newWorkspaceRoot, string newIncomingFolder)
    {
        // 1) cancel processing and wait for gate (avoid restarting mid-process)
        try { _cts.Cancel(); } catch { }

        _processGate.Wait();
        try
        {
            // finalize active session (avoid mixing roots)
            var active = _sessions.ActiveSession;
            if (active != null)
            {
                _sessions.FinalizeActive();
                Log($"Session finalized due to folder change: {active.Barcode}");
            }

            // 2) stop watcher safely
            try
            {
                if (_watcher != null)
                {
                    _watcher.FileArrived -= OnIncomingFile;
                    _watcher.Dispose();
                }
            }
            catch { }

            // 3) reset CTS
            try { _cts.Dispose(); } catch { }
            _cts = new CancellationTokenSource();

            // 4) update roots
            _workspaceRoot = newWorkspaceRoot;
            _incomingFolder = newIncomingFolder;

            // 5) rebuild services
            _workspace = new WorkspaceLayout(_workspaceRoot);
            _sessions = new SessionManager(_workspace);
            _processor = new FrameProcessor(_workspace, _licensing);


            // 6) clear volatile UI state
            Thumbnails.Clear();
            _thumbsByBarcode.Clear();
            _productOverrides.Clear();
            _sessionProfileLocked = false;

            // 7) recreate watcher
            _watcher = new IncomingFolderWatcher(_incomingFolder);
            _watcher.Info += msg => Log(msg);
            _watcher.Error += msg => Log("ERROR: " + msg);
            _watcher.FileArrived += OnIncomingFile;
            _watcher.Start();

            // 8) update UI
            FoldersStatusText.Text = $"Workspace: {_workspaceRoot}   |   Incoming: {_incomingFolder}";
            RefreshUI();
            RefreshThumbsForActiveBarcode();

            BarcodeTextBox.Clear();
            ProductNameTextBox.Clear();
            BarcodeTextBox.Focus();

            Log("Folders applied. Pipeline restarted.");
        }
        finally
        {
            _processGate.Release();
        }
    }

    // ===== Barcode logic =====

    private void StartOrSwitchFromInputs()
    {
        var barcode = (BarcodeTextBox.Text ?? "").Trim();
        var name = ProductNameTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(barcode))
        {
            Log("Barcode is empty.");
            return;
        }

        var active = _sessions.ActiveSession;

        // New barcode => finalize old and start new
        if (active != null && !string.Equals(active.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
        {
            _sessions.FinalizeActive();
            Log($"Session finalized: {active.Barcode}");
        }

        var session = _sessions.BeginOrSwitch(barcode, name);
        Log($"Active: {session.Barcode}" + (string.IsNullOrWhiteSpace(session.ProductName) ? "" : $" ({session.ProductName})"));

        RefreshUI();
        RefreshThumbsForActiveBarcode();

        BarcodeTextBox.Clear();
        BarcodeTextBox.Focus();
    }

    private void RefreshThumbsForActiveBarcode()
    {
        var session = _sessions.ActiveSession;
        Thumbnails.Clear();

        if (session is null) return;

        if (_thumbsByBarcode.TryGetValue(session.Barcode, out var list))
        {
            foreach (var t in list.OrderByDescending(x => x.CreatedUtc))
                Thumbnails.Add(t);
        }
    }

    // ===== Incoming folder pipeline =====

    private void OnIncomingFile(string path)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var session = _sessions.ActiveSession;
                if (session is null)
                {
                    Log($"Incoming image ignored (no active barcode): {Path.GetFileName(path)}");
                    return;
                }

                var frame = _sessions.AddIncomingFile(path);
                Log($"Imported: {Path.GetFileName(frame.RawPath)}");
                RefreshUI();

                // Auto-preview ONLY ONCE per app session
                if (!_sessionProfileLocked)
                {
                    await OpenPreviewForProductAsync(session.Barcode, frame, autoLockSessionAfter: true);
                    _sessionProfileLocked = true;
                    Log("Session profile locked (no more auto-preview popups). Click a thumbnail if you need to adjust.");
                }

                await ProcessFrameAsync(frame);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
        });
    }

    private ProcessingProfile GetEffectiveProfileForBarcode(string barcode)
    {
        if (_productOverrides.TryGetValue(barcode, out var p))
            return p;

        return _sessionProfile;
    }

    /// <summary>
    /// Opens preview for current product and optionally reprocesses exports depending on user's Apply choice.
    /// </summary>
    private async Task OpenPreviewForProductAsync(string barcode, PhotoFrame frame, bool autoLockSessionAfter = false)
    {
        try
        {
            var previewDir = Path.Combine(_workspace.GetProcessedFolder(barcode), "preview");
            Directory.CreateDirectory(previewDir);

            var previewOut = Path.Combine(previewDir, "preview.png");

            async Task<string> RenderAsync(ProcessingProfile profile)
            {
                var exports = new List<ExportPreset>(); // preview: не правим exports


                if (exports.Count == 0)
                    exports = new List<ExportPreset> { new ExportPreset("Preview", 2000, 2000, ExportImageFormat.Png, 100) };

                bool whiteBackground = false;

                var opts = new ProcessingOptions(
                    BackgroundMethod: profile.Method,
                    SquareSize: 2000,
                    PaddingPercent: 0.10f,
                    WhiteBackground: whiteBackground,
                    WhiteThreshold: profile.WhiteThreshold,
                    Feather: profile.Feather,

                    SuppressGroundShadow: true,
                    ShadowWhiteThreshold: 245,
                    ShadowMaxAlpha: 120,
                    ShadowBottomPercent: 30,

                    Exports: exports
                );

                var outputs = await Task.Run(
                    () => _processor.ProcessAsync(_sessions.ActiveSession!, frame, opts, _cts.Token),
                    _cts.Token
                );

                if (outputs.Count == 0 || !File.Exists(outputs[0]))
                    throw new Exception("Preview output missing.");

                File.Copy(outputs[0], previewOut, true);
                return previewOut;
            }

            var initial = GetEffectiveProfileForBarcode(barcode);

            var w = new ProductPreviewWindow(barcode, initial, RenderAsync)
            {
                Owner = this
            };

            var ok = w.ShowDialog() == true;

            if (!ok || w.SelectedProfile == null)
            {
                Log("Preview closed (view only / canceled).");
                return;
            }

            if (w.ApplyForSession)
            {
                _sessionProfile = w.SelectedProfile.Clone();
                Log("Session profile saved: " + _sessionProfile);

                _productOverrides.Remove(barcode);

                await ReprocessCurrentProductAsync(barcode, _sessionProfile);

                if (autoLockSessionAfter) _sessionProfileLocked = true;
            }
            else if (w.ApplyForCurrentProduct)
            {
                var prodProfile = w.SelectedProfile.Clone();
                _productOverrides[barcode] = prodProfile;

                Log("Product override saved: " + prodProfile);

                await ReprocessCurrentProductAsync(barcode, prodProfile);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR (preview): " + ex.Message);
        }
    }

    private async Task ReprocessCurrentProductAsync(string barcode, ProcessingProfile profile)
    {
        var session = _sessions.ActiveSession;
        if (session is null || !string.Equals(session.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
        {
            Log("Reprocess skipped: product not active.");
            return;
        }

        if (session.Frames.Count == 0)
        {
            Log("Reprocess skipped: no frames.");
            return;
        }

        Log($"Reprocess started: {barcode} | frames={session.Frames.Count}");

        await _processGate.WaitAsync(_cts.Token);
        try
        {
            foreach (var frame in session.Frames.ToList())
            {
                var outputs = await ProcessFrameWithProfileAsync(session, frame, profile);
                UpdateThumbForFrame(barcode, frame.RawPath, outputs);
            }

            RefreshThumbsForActiveBarcode();
            Log("Reprocess done.");
        }
        catch (Exception ex)
        {
            Log("ERROR (reprocess): " + ex.Message);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<List<string>> ProcessFrameWithProfileAsync(
    PhotoFlow.Core.Domain.ProductSession session,
    PhotoFrame frame,
    ProcessingProfile profile)
    {
        var selected = _presetItems.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
            return new List<string>();

        var transparentPresets = selected.Where(p => !p.WhiteBackground).ToList();

        var whiteNormal = selected
            .Where(p => p.WhiteBackground && !p.ForcePureWhite && !p.KeepShadow)
            .ToList();

        var whitePure = selected
            .Where(p => p.WhiteBackground && p.ForcePureWhite)
            .ToList();

        var whiteKeepShadow = selected
            .Where(p => p.WhiteBackground && p.KeepShadow)
            .ToList();

        var allOutputs = new List<string>();

        async Task RunOnePassAsync(
            bool whiteBackground,
            bool suppressGroundShadow,
            bool forcePureWhiteBackground,
            List<ExportPresetItem> items)
        {
            if (items.Count == 0) return;

            var exports = items.Select(p => p.ToProcessingPreset()).ToList();

            var opts = new ProcessingOptions(
                BackgroundMethod: profile.Method,
                SquareSize: 2000,
                PaddingPercent: 0.10f,
                WhiteBackground: whiteBackground,
                WhiteThreshold: profile.WhiteThreshold,
                Feather: profile.Feather,

                SuppressGroundShadow: suppressGroundShadow,
                ShadowWhiteThreshold: 245,
                ShadowMaxAlpha: 120,
                ShadowBottomPercent: 30,

                ForcePureWhiteBackground: forcePureWhiteBackground,
                Exports: exports
            );

            var outputs = await Task.Run(
                () => _processor.ProcessAsync(session, frame, opts, _cts.Token),
                _cts.Token
            );

            allOutputs.AddRange(outputs);
        }

        // White exports: 3 режима
        await RunOnePassAsync(true, true, false, whiteNormal);
        await RunOnePassAsync(true, true, true, whitePure);
        await RunOnePassAsync(true, false, false, whiteKeepShadow);

        // Transparent exports
        await RunOnePassAsync(false, true, false, transparentPresets);

        return allOutputs;
    }





    private async Task ProcessFrameAsync(PhotoFrame frame)
    {
        await _processGate.WaitAsync(_cts.Token);
        try
        {
            var session = _sessions.ActiveSession;
            if (session is null)
            {
                Log("Processing skipped (no active session).");
                return;
            }

            var profile = GetEffectiveProfileForBarcode(session.Barcode);

            var outputs = await ProcessFrameWithProfileAsync(session, frame, profile);
            if (outputs.Count > 0)
            {
                AddOrUpdateThumb(session.Barcode, frame.RawPath, outputs);
            }

            RefreshUI();
        }
        catch (OperationCanceledException)
        {
            Log("Processing canceled.");
        }
        catch (Exception ex)
        {
            Log("ERROR (processing): " + ex.Message);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private void AddOrUpdateThumb(string barcode, string frameRawPath, List<string> outputs)
    {
        if (outputs == null || outputs.Count == 0) return;

        var selectedPresets = _presetItems.Where(p => p.IsSelected).ToList();
        var smallest = selectedPresets.OrderBy(p => (long)p.Width * p.Height).FirstOrDefault();

        string bestPath = outputs.First(); // master
        if (smallest != null)
        {
            var match = outputs.FirstOrDefault(p => p.IndexOf($"_{smallest.Width}x{smallest.Height}", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(match) && File.Exists(match))
                bestPath = match;
        }

        if (!_thumbsByBarcode.TryGetValue(barcode, out var list))
        {
            list = new List<ThumbnailItem>();
            _thumbsByBarcode[barcode] = list;
        }

        var existing = list.FirstOrDefault(t => string.Equals(t.FrameRawPath, frameRawPath, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new ThumbnailItem
            {
                Barcode = barcode,
                FrameRawPath = frameRawPath,
                Label = barcode,
                CreatedUtc = DateTime.UtcNow
            };
            list.Insert(0, existing);
        }

        existing.Source = LoadBitmapNoCache(bestPath);

        var active = _sessions.ActiveSession;
        if (active != null && string.Equals(active.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
        {
            if (!Thumbnails.Contains(existing))
                Thumbnails.Insert(0, existing);
        }

        while (list.Count > 80)
            list.RemoveAt(list.Count - 1);

        while (Thumbnails.Count > 80)
            Thumbnails.RemoveAt(Thumbnails.Count - 1);
    }

    private void UpdateThumbForFrame(string barcode, string frameRawPath, List<string> outputs)
    {
        if (!_thumbsByBarcode.TryGetValue(barcode, out var list))
            return;

        var item = list.FirstOrDefault(t => string.Equals(t.FrameRawPath, frameRawPath, StringComparison.OrdinalIgnoreCase));
        if (item == null)
            return;

        var selectedPresets = _presetItems.Where(p => p.IsSelected).ToList();
        var smallest = selectedPresets.OrderBy(p => (long)p.Width * p.Height).FirstOrDefault();

        string bestPath = outputs.FirstOrDefault() ?? "";
        if (smallest != null)
        {
            var match = outputs.FirstOrDefault(p => p.IndexOf($"_{smallest.Width}x{smallest.Height}", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(match) && File.Exists(match))
                bestPath = match;
        }

        if (!string.IsNullOrWhiteSpace(bestPath) && File.Exists(bestPath))
            item.Source = LoadBitmapNoCache(bestPath);
    }

    private static BitmapImage LoadBitmapNoCache(string path)
    {
        var bmp = new BitmapImage();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    // ===== Presets =====

    private void InitPresetsOnce()
    {
        _presetItems.Clear();

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 2000 JPG (White)",
            Width = 2000,
            Height = 2000,
            Format = ExportImageFormat.Jpeg,
            Quality = 92,
            WhiteBackground = true,
            IsSelected = true
        });
        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 2000 JPG (Pure White)",
            Width = 2000,
            Height = 2000,
            Format = ExportImageFormat.Jpeg,
            Quality = 92,
            WhiteBackground = true,
            ForcePureWhite = true,
            IsSelected = false
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 2000 JPG (White + Shadow)",
            Width = 2000,
            Height = 2000,
            Format = ExportImageFormat.Jpeg,
            Quality = 92,
            WhiteBackground = true,
            KeepShadow = true,
            IsSelected = false
        });


        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Thumb 800 JPG (White)",
            Width = 800,
            Height = 800,
            Format = ExportImageFormat.Jpeg,
            Quality = 90,
            WhiteBackground = true,
            IsSelected = false
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Thumb 320 PNG (Transparent)",
            Width = 320,
            Height = 320,
            Format = ExportImageFormat.Png,
            Quality = 100,
            WhiteBackground = false,
            IsSelected = true
        });


        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 1200 JPG (White)",
            Width = 1200,
            Height = 1200,
            Format = ExportImageFormat.Jpeg,
            Quality = 90,
            WhiteBackground = true
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 1600 JPG (White)",
            Width = 1600,
            Height = 1600,
            Format = ExportImageFormat.Jpeg,
            Quality = 90,
            WhiteBackground = true
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Thumb 600 JPG (White)",
            Width = 600,
            Height = 600,
            Format = ExportImageFormat.Jpeg,
            Quality = 85,
            WhiteBackground = true
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Master 3000 PNG (Transparent)",
            Width = 3000,
            Height = 3000,
            Format = ExportImageFormat.Png,
            Quality = 100,
            WhiteBackground = false
        });

        _presetItems.Add(new ExportPresetItem
        {
            DisplayName = "Square 2000 PNG (Transparent)",
            Width = 2000,
            Height = 2000,
            Format = ExportImageFormat.Png,
            Quality = 100,
            WhiteBackground = false
        });
    }

    private void BindPresetsToUi()
    {
        if (ExportsListBox != null)
            ExportsListBox.ItemsSource = _presetItems;
    }

    // ===== Settings (save/load) =====

    private void LoadSettingsApplySelection()
    {
        _isApplyingSettings = true;
        try
        {
            var settings = LoadSettings();
            if (settings.SelectedExportNames.Count == 0)
                return;

            var selectedSet = new HashSet<string>(settings.SelectedExportNames, StringComparer.OrdinalIgnoreCase);

            foreach (var p in _presetItems)
                p.IsSelected = selectedSet.Contains(p.DisplayName);

            ExportsListBox.Items.Refresh();
        }
        catch (Exception ex)
        {
            Log("ERROR (settings load): " + ex.Message);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveSettingsFromSelection()
    {
        try
        {
            // IMPORTANT: preserve folders if they already exist in settings
            var settings = LoadSettings();
            settings.SelectedExportNames = _presetItems.Where(p => p.IsSelected).Select(p => p.DisplayName).ToList();

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
            Log("Settings saved.");
        }
        catch (Exception ex)
        {
            Log("ERROR (settings save): " + ex.Message);
        }
    }

    // ===== UI helpers =====

    private void RefreshUI()
    {
        if (!_uiReady) return;

        var active = _sessions.ActiveSession;

        if (active is null)
        {
            ActiveSessionText.Text = "None (scan/enter barcode to start)";
            ActiveFramesText.Text = "";
        }
        else
        {
            ActiveSessionText.Text = $"{active.Barcode}" + (string.IsNullOrWhiteSpace(active.ProductName) ? "" : $" — {active.ProductName}");
            ActiveFramesText.Text = $"Frames: {active.Frames.Count}/10 (10 is recommended, not required)";
        }

        RecentListBox.Items.Clear();
        foreach (var s in _sessions.RecentSessions)
        {
            var line = $"{s.Barcode}  |  frames: {s.Frames.Count}" + (s.IsFinalized ? "  |  finalized" : "");
            RecentListBox.Items.Add(line);
        }
    }

    private void Log(string msg)
    {
        if (LogTextBox == null)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(msg));
            return;
        }

        var sb = new StringBuilder(LogTextBox.Text);
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        LogTextBox.Text = sb.ToString();
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _cts.Cancel();
            _cts.Dispose();

            _watcher?.Dispose();
        }
        catch { }

        base.OnClosed(e);
    }
}
