using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

public class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly HotkeyConfig _config;

    public bool IsArmed { get; private set; } = true;
    public bool GlobalHotkeysEnabled => _config.GlobalHotkeysEnabled;

    public event Action<string>? HotkeyFired;

    // Tracks last-press time per action for double-press detection
    private readonly Dictionary<string, DateTime> _lastPressTimes = [];
    private readonly object _pressGate = new();

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public void Arm() { IsArmed = true; _logger.LogInformation("Hotkeys ARMED"); }
    public void Disarm() { IsArmed = false; _logger.LogInformation("Hotkeys DISARMED"); }

    public string? ProcessKeyDown(KeyEventArgs e)
    {
        if (!IsArmed) return null;

        var gesture = BuildGestureString(e);
        var binding = _config.Bindings.FirstOrDefault(b =>
            b.IsEnabled && string.Equals(b.KeyGesture, gesture, StringComparison.OrdinalIgnoreCase));

        if (binding == null) return null;

        if (binding.RequireDoublePress)
        {
            var now = DateTime.UtcNow;
            lock (_pressGate)
            {
                if (_lastPressTimes.TryGetValue(binding.Id, out var last) &&
                    (now - last).TotalMilliseconds <= _config.DangerousActionDoublePressMs)
                {
                    _lastPressTimes.Remove(binding.Id);
                    _logger.LogInformation("Hotkey double-press confirmed: {Action}", binding.ActionType);
                    HotkeyFired?.Invoke(binding.ActionType);
                    return binding.ActionType;
                }

                _lastPressTimes[binding.Id] = now;
                _logger.LogDebug("Hotkey first press (waiting for double): {Action}", binding.ActionType);
                return null;
            }
        }

        _logger.LogInformation("Hotkey fired: {Gesture} → {Action}", gesture, binding.ActionType);
        HotkeyFired?.Invoke(binding.ActionType);
        return binding.ActionType;
    }

    public static string BuildGestureString(KeyEventArgs e)
    {
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(e.Key.ToString());
        return string.Join("+", parts);
    }

    public HotkeyBinding? FindBinding(string actionType) =>
        _config.Bindings.FirstOrDefault(b => b.ActionType == actionType);

    // Maps fixed hotkey ActionType strings → HotButtonAction enum.
    // Kept here so both HotButtonsViewModel and HotButtonSettingsWindow share one source of truth.
    public static readonly IReadOnlyDictionary<string, FastDOM.Core.Models.HotButtonAction> ActionTypeMap =
        new Dictionary<string, FastDOM.Core.Models.HotButtonAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["BuyMarketableLimit"]  = FastDOM.Core.Models.HotButtonAction.BuyMarketableLimit,
            ["SellMarketableLimit"] = FastDOM.Core.Models.HotButtonAction.SellMarketableLimit,
            ["FlattenSymbol"]       = FastDOM.Core.Models.HotButtonAction.Flatten,
            ["ReversePosition"]     = FastDOM.Core.Models.HotButtonAction.Reverse,
            ["CancelAllSymbol"]     = FastDOM.Core.Models.HotButtonAction.CancelSymbol,
            ["CancelAllAccount"]    = FastDOM.Core.Models.HotButtonAction.CancelAll,
        };

    public void Dispose() { }
}
