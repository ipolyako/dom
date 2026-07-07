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

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public void Arm() { IsArmed = true; _logger.LogInformation("Hotkeys ARMED"); }
    public void Disarm() { IsArmed = false; _logger.LogInformation("Hotkeys DISARMED"); }

    /// <summary>
    /// Call from WPF KeyDown handler. Returns the action type if a hotkey matched and fired.
    /// </summary>
    public string? ProcessKeyDown(KeyEventArgs e, bool isTextBoxFocused)
    {
        if (!IsArmed) return null;
        if (isTextBoxFocused) return null;

        var gesture = BuildGestureString(e);
        var binding = _config.Bindings.FirstOrDefault(b =>
            b.IsEnabled && string.Equals(b.KeyGesture, gesture, StringComparison.OrdinalIgnoreCase));

        if (binding == null) return null;

        if (binding.RequireDoublePress)
        {
            var now = DateTime.UtcNow;
            if (_lastPressTimes.TryGetValue(binding.Id, out var last) &&
                (now - last).TotalMilliseconds <= _config.DangerousActionDoublePressMs)
            {
                _lastPressTimes.Remove(binding.Id);
                _logger.LogInformation("Hotkey double-press confirmed: {Action}", binding.ActionType);
                HotkeyFired?.Invoke(binding.ActionType);
                return binding.ActionType;
            }
            else
            {
                _lastPressTimes[binding.Id] = now;
                _logger.LogDebug("Hotkey first press (waiting for double): {Action}", binding.ActionType);
                return null;
            }
        }

        _logger.LogInformation("Hotkey fired: {Gesture} → {Action}", gesture, binding.ActionType);
        HotkeyFired?.Invoke(binding.ActionType);
        return binding.ActionType;
    }

    private static string BuildGestureString(KeyEventArgs e)
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

    public void Dispose() { }
}
