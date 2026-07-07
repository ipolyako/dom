using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;

namespace FastDOM.App.Views;

public partial class HotkeySettingsWindow : Window
{
    private readonly HotkeyConfig _config;
    private readonly ConfigManager _configManager;
    private HotkeyBindingEdit? _rebindTarget;

    public ObservableCollection<HotkeyBindingEdit> Edits { get; }

    public HotkeySettingsWindow(HotkeyConfig config, ConfigManager configManager)
    {
        _config = config;
        _configManager = configManager;
        Edits = new ObservableCollection<HotkeyBindingEdit>(
            config.Bindings.Select(b => new HotkeyBindingEdit(b)));
        InitializeComponent();
        DataContext = this;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_rebindTarget == null) { base.OnPreviewKeyDown(e); return; }

        // Ignore standalone modifier key presses
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                   or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin)
            return;

        // Escape cancels the rebind without changing the gesture
        if (e.Key == Key.Escape)
        {
            _rebindTarget.IsRebinding = false;
            _rebindTarget = null;
            e.Handled = true;
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        parts.Add(e.Key.ToString());

        _rebindTarget.KeyGesture = string.Join("+", parts);
        _rebindTarget.IsRebinding = false;
        _rebindTarget = null;
        e.Handled = true;
    }

    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotkeyBindingEdit edit }) return;

        // Cancel any active rebind first
        if (_rebindTarget != null)
        {
            _rebindTarget.IsRebinding = false;
            _rebindTarget = null;
        }

        _rebindTarget = edit;
        edit.IsRebinding = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.Bindings = Edits.Select(edit => new HotkeyBinding
        {
            Id                   = edit.Id,
            Label                = edit.Label,
            KeyGesture           = edit.KeyGesture,
            ActionType           = edit.ActionType,
            RequireDoublePress   = edit.RequireDoublePress,
            RequireConfirmation  = edit.RequireConfirmation,
            IsEnabled            = edit.IsEnabled,
            IsDangerous          = edit.IsDangerous,
            CooldownMs           = edit.CooldownMs,
        }).ToList();
        _configManager.SaveAll();
        Close();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        Edits.Clear();
        foreach (var b in HotkeyBinding.Defaults())
            Edits.Add(new HotkeyBindingEdit(b));
    }
}

public class HotkeyBindingEdit : INotifyPropertyChanged
{
    public string Id                { get; }
    public string Label             { get; }
    public string ActionType        { get; }
    public bool   IsDangerous       { get; }
    public int    CooldownMs        { get; }
    public bool   RequireConfirmation { get; set; }

    private string _keyGesture;
    private bool   _requireDoublePress;
    private bool   _isEnabled;
    private bool   _isRebinding;

    public string KeyGesture
    {
        get => _keyGesture;
        set { _keyGesture = value; OnPropertyChanged(nameof(KeyGesture)); OnPropertyChanged(nameof(GestureDisplay)); }
    }

    // Separate display property so "Press key..." is shown without corrupting the stored value
    public string GestureDisplay => _isRebinding ? "Press key…" : _keyGesture;

    public bool RequireDoublePress
    {
        get => _requireDoublePress;
        set { _requireDoublePress = value; OnPropertyChanged(nameof(RequireDoublePress)); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
    }

    public bool IsRebinding
    {
        get => _isRebinding;
        set { _isRebinding = value; OnPropertyChanged(nameof(IsRebinding)); OnPropertyChanged(nameof(GestureDisplay)); }
    }

    public HotkeyBindingEdit(HotkeyBinding b)
    {
        Id                  = b.Id;
        Label               = b.Label;
        ActionType          = b.ActionType;
        IsDangerous         = b.IsDangerous;
        CooldownMs          = b.CooldownMs;
        RequireConfirmation = b.RequireConfirmation;
        _keyGesture         = b.KeyGesture;
        _requireDoublePress = b.RequireDoublePress;
        _isEnabled          = b.IsEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
