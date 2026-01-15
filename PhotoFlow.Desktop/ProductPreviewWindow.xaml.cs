using PhotoFlow.Desktop.Models;
using PhotoFlow.Processing.Services;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace PhotoFlow.Desktop;

public partial class ProductPreviewWindow : Window
{
    private readonly string _barcode;
    private readonly Func<ProcessingProfile, Task<string>> _renderAsync;

    private ProcessingProfile _current;
    private CancellationTokenSource? _debounceCts;

    private bool _uiReady;
    private bool _isRendering;

    public ProcessingProfile? SelectedProfile { get; private set; }
    public bool ApplyForSession { get; private set; }
    public bool ApplyForCurrentProduct { get; private set; }

    public ProductPreviewWindow(string barcode, ProcessingProfile initial, Func<ProcessingProfile, Task<string>> renderAsync)
    {
        InitializeComponent();

        _barcode = barcode ?? "";
        _renderAsync = renderAsync ?? throw new ArgumentNullException(nameof(renderAsync));
        _current = initial?.Clone() ?? new ProcessingProfile();

        ProductText.Text = string.IsNullOrWhiteSpace(_barcode) ? "" : _barcode;

        Loaded += async (_, _) =>
        {
            _uiReady = true;

            // init UI from profile
            ThresholdSlider.Value = Clamp(_current.WhiteThreshold, (int)ThresholdSlider.Minimum, (int)ThresholdSlider.Maximum);
            FeatherSlider.Value = Clamp(_current.Feather, (int)FeatherSlider.Minimum, (int)FeatherSlider.Maximum);

            ThresholdValue.Text = ThresholdSlider.Value.ToString("0", CultureInfo.InvariantCulture);
            FeatherValue.Text = FeatherSlider.Value.ToString("0", CultureInfo.InvariantCulture);

            SelectMethodFromProfile(_current);
            UpdateManualControlsEnabled();

            // hook changes after we set initial values
            ThresholdSlider.ValueChanged += (_, _) => OnManualChanged();
            FeatherSlider.ValueChanged += (_, _) => OnManualChanged();

            await RenderNowAsync("Preview loaded");
        };

        Closed += (_, _) =>
        {
            try { _debounceCts?.Cancel(); } catch { }
            try { _debounceCts?.Dispose(); } catch { }
        };
    }

    // -------------------- UI handlers (must match XAML names) --------------------

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RenderNowAsync("Refreshing...");
    }

    private void MethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        var tag = GetSelectedMethodTag();
        ApplyMethodTagToProfile(tag);

        UpdateManualControlsEnabled();
        DebounceRender("Method changed...");
    }

    private void PresetClean_Click(object sender, RoutedEventArgs e)
    {
        // "Clean" = по-малко агресивно, по-стегнат ръб
        ThresholdSlider.Value = 248;
        FeatherSlider.Value = 6;
        OnManualChanged();
    }

    private void PresetStrong_Click(object sender, RoutedEventArgs e)
    {
        // "Strong" = по-агресивно махане на фон
        ThresholdSlider.Value = 242;
        FeatherSlider.Value = 12;
        OnManualChanged();
    }

    private void ApplyProduct_Click(object sender, RoutedEventArgs e)
    {
        SelectedProfile = BuildProfileFromUi();
        ApplyForCurrentProduct = true;
        ApplyForSession = false;

        DialogResult = true;
        Close();
    }

    private void ApplySession_Click(object sender, RoutedEventArgs e)
    {
        SelectedProfile = BuildProfileFromUi();
        ApplyForSession = true;
        ApplyForCurrentProduct = false;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedProfile = null;
        ApplyForSession = false;
        ApplyForCurrentProduct = false;

        DialogResult = false;
        Close();
    }

    // -------------------- internals --------------------

    private void OnManualChanged()
    {
        if (!_uiReady) return;

        ThresholdValue.Text = ThresholdSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        FeatherValue.Text = FeatherSlider.Value.ToString("0", CultureInfo.InvariantCulture);

        DebounceRender("Updating preview...");
    }

    private void DebounceRender(string status)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();

        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                await Dispatcher.InvokeAsync(async () => await RenderNowAsync(status));
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task RenderNowAsync(string status)
    {
        if (_isRendering) return;

        _isRendering = true;
        try
        {
            StatusText.Text = status;

            var profile = BuildProfileFromUi();
            var outPath = await _renderAsync(profile);

            if (string.IsNullOrWhiteSpace(outPath) || !File.Exists(outPath))
            {
                StatusText.Text = "Preview failed: output file missing.";
                return;
            }

            PreviewImage.Source = LoadBitmapNoCache(outPath);
            StatusText.Text = "Ready.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            _isRendering = false;
        }
    }

    private ProcessingProfile BuildProfileFromUi()
    {
        var p = _current.Clone();

        p.WhiteThreshold = (byte)Clamp((int)Math.Round(ThresholdSlider.Value), 0, 255);
        p.Feather = (byte)Clamp((int)Math.Round(FeatherSlider.Value), 0, 255);

        var tag = GetSelectedMethodTag();
        ApplyMethodTagToProfile(tag, p);

        return p;
    }

    private void UpdateManualControlsEnabled()
    {
        var tag = GetSelectedMethodTag();

        // Manual controls are enabled when method is "Manual" (legacy tag)
        // OR when selected enum is one of the classic threshold-based ones.
        bool isManual = IsManualSelection(tag);

        ThresholdSlider.IsEnabled = isManual;
        FeatherSlider.IsEnabled = isManual;

        ThresholdLabel.Opacity = isManual ? 1.0 : 0.55;
        FeatherLabel.Opacity = isManual ? 1.0 : 0.55;
        ThresholdValue.Opacity = isManual ? 0.9 : 0.55;
        FeatherValue.Opacity = isManual ? 0.9 : 0.55;
    }

    private void SelectMethodFromProfile(ProcessingProfile profile)
    {
        // 1) direct match: enum name equals Tag
        var methodName = profile.Method.ToString();
        foreach (var item in MethodCombo.Items)
        {
            if (item is ComboBoxItem cbi)
            {
                var tag = (cbi.Tag?.ToString() ?? "").Trim();
                if (string.Equals(tag, methodName, StringComparison.OrdinalIgnoreCase))
                {
                    MethodCombo.SelectedItem = cbi;
                    return;
                }
            }
        }

        // 2) legacy mapping (if your tags are Manual / AI_FAST / AI_QUALITY / AI_BEST)
        var preferredLegacyTag = GetLegacyTagForMethod(profile.Method);
        if (!string.IsNullOrWhiteSpace(preferredLegacyTag))
        {
            foreach (var item in MethodCombo.Items)
            {
                if (item is ComboBoxItem cbi)
                {
                    var tag = (cbi.Tag?.ToString() ?? "").Trim();
                    if (string.Equals(tag, preferredLegacyTag, StringComparison.OrdinalIgnoreCase))
                    {
                        MethodCombo.SelectedItem = cbi;
                        return;
                    }
                }
            }
        }

        // 3) fallback: Manual
        foreach (var item in MethodCombo.Items)
        {
            if (item is ComboBoxItem cbi)
            {
                var tag = (cbi.Tag?.ToString() ?? "").Trim();
                if (string.Equals(tag, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    MethodCombo.SelectedItem = cbi;
                    return;
                }
            }
        }
    }

    private void ApplyMethodTagToProfile(string tag) => ApplyMethodTagToProfile(tag, _current);

    private void ApplyMethodTagToProfile(string tag, ProcessingProfile profile)
    {
        if (profile == null) return;

        var t = (tag ?? "").Trim();

        // ---- legacy tags -> enum names ----
        if (string.Equals(t, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            // "Manual" means threshold-based remover (default)
            if (TrySetMethod(profile, "SimpleOverexposedToTransparent"))
                return;

            // if enum name differs in your code, this fallback keeps current method
            return;
        }

        if (string.Equals(t, "AI_FAST", StringComparison.OrdinalIgnoreCase))
        {
            if (TrySetMethod(profile, "AiModNetFast"))
                return;

            // fallback: try enum parse directly
        }

        if (string.Equals(t, "AI_QUALITY", StringComparison.OrdinalIgnoreCase))
        {
            if (TrySetMethod(profile, "AiU2NetQuality"))
                return;
        }

        if (string.Equals(t, "AI_BEST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "AI_BIREFNET", StringComparison.OrdinalIgnoreCase))
        {
            // prefer BiRefNet if it exists in your enum
            if (TrySetMethod(profile, "AiBiRefNet")) return;
            if (TrySetMethod(profile, "AiBiRefNetTiny")) return;

            // if not yet added -> fallback to best available
            if (TrySetMethod(profile, "AiU2NetQuality")) return;
        }

        // ---- direct enum parse (supports using enum names as tags) ----
        try
        {
            if (Enum.TryParse(t, ignoreCase: true, out BackgroundMethod parsed))
                profile.Method = parsed;
        }
        catch { }
    }

    private static bool TrySetMethod(ProcessingProfile profile, string enumName)
    {
        try
        {
            if (Enum.TryParse(enumName, ignoreCase: true, out BackgroundMethod parsed))
            {
                profile.Method = parsed;
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool IsManualSelection(string tag)
    {
        var t = (tag ?? "").Trim();

        if (string.Equals(t, "Manual", StringComparison.OrdinalIgnoreCase))
            return true;

        // If Tag is enum name, check if it's one of the classic (threshold-based) methods
        try
        {
            if (Enum.TryParse(t, ignoreCase: true, out BackgroundMethod parsed))
            {
                var name = parsed.ToString();
                return string.Equals(name, "SimpleOverexposedToTransparent", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "AggressiveOverexposedToTransparent", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }

        return false;
    }

    private static string? GetLegacyTagForMethod(BackgroundMethod method)
    {
        var name = method.ToString();

        if (string.Equals(name, "AiModNetFast", StringComparison.OrdinalIgnoreCase))
            return "AI_FAST";

        if (string.Equals(name, "AiU2NetQuality", StringComparison.OrdinalIgnoreCase))
            return "AI_QUALITY";

        if (string.Equals(name, "AiBiRefNet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "AiBiRefNetTiny", StringComparison.OrdinalIgnoreCase))
            return "AI_BEST";

        if (string.Equals(name, "SimpleOverexposedToTransparent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "AggressiveOverexposedToTransparent", StringComparison.OrdinalIgnoreCase))
            return "Manual";

        return null;
    }

    private string GetSelectedMethodTag()
    {
        return (MethodCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Manual";
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

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
}
