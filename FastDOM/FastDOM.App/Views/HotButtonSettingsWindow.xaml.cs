using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastDOM.App.Services;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;

namespace FastDOM.App.Views;

public partial class HotButtonSettingsWindow : Window
{
    private readonly List<HotButtonConfig> _source;
    private readonly ConfigManager _configManager;

    public ObservableCollection<HotButtonEdit> Edits { get; }

    public HotButtonSettingsWindow(List<HotButtonConfig> buttons, ConfigManager configManager)
    {
        _source = buttons;
        _configManager = configManager;
        Edits = new ObservableCollection<HotButtonEdit>(buttons.Select(b => new HotButtonEdit(b)));
        InitializeComponent();
        DataContext = this;

        // Populate template category picker
        foreach (var cat in HotButtonTemplates.Categories)
            CategoryBox.Items.Add(cat);
        if (CategoryBox.Items.Count > 0)
            CategoryBox.SelectedIndex = 0;
    }

    // ── Detail panel binding ───────────────────────────────────────────────

    private void ShowDetailFor(HotButtonEdit? edit)
    {
        if (edit == null)
        {
            DetailGrid.Visibility    = Visibility.Collapsed;
            NoSelectionText.Visibility = Visibility.Visible;
            return;
        }

        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailGrid.Visibility      = Visibility.Visible;

        LabelBox.DataContext    = edit;
        ColorBox.DataContext    = edit;
        ShortcutBox.DataContext = edit;
        EnabledBox.DataContext  = edit;
        ScriptBox.DataContext   = edit;
    }

    // ── DataGrid ───────────────────────────────────────────────────────────

    private void ButtonsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = ButtonsGrid.SelectedItem as HotButtonEdit;
        int idx = sel != null ? Edits.IndexOf(sel) : -1;

        DeleteButton.IsEnabled   = sel != null;
        MoveUpButton.IsEnabled   = idx > 0;
        MoveDownButton.IsEnabled = idx >= 0 && idx < Edits.Count - 1;

        ShowDetailFor(sel);
    }

    // ── Toolbar buttons ───────────────────────────────────────────────────

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var newEdit = HotButtonEdit.CreateNew();
        Edits.Add(newEdit);
        ButtonsGrid.SelectedItem = newEdit;
        ButtonsGrid.ScrollIntoView(newEdit);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonsGrid.SelectedItem is HotButtonEdit sel)
            Edits.Remove(sel);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonsGrid.SelectedItem is not HotButtonEdit sel) return;
        int i = Edits.IndexOf(sel);
        if (i > 0) { Edits.Move(i, i - 1); ButtonsGrid.SelectedItem = sel; }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonsGrid.SelectedItem is not HotButtonEdit sel) return;
        int i = Edits.IndexOf(sel);
        if (i < Edits.Count - 1) { Edits.Move(i, i + 1); ButtonsGrid.SelectedItem = sel; }
    }

    // ── Template picker ───────────────────────────────────────────────────

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is string cat)
        {
            TemplateBox.ItemsSource = HotButtonTemplates.GetTemplates(cat);
            if (TemplateBox.Items.Count > 0)
                TemplateBox.SelectedIndex = 0;
        }
    }

    private void ShortcutBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // Clear field on Backspace or Delete
        if (key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete)
        {
            if (ButtonsGrid.SelectedItem is HotButtonEdit edit) edit.KeyboardShortcut = null;
            ShortcutConflict.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        // Ignore standalone modifier keys
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
            return;

        var gesture = HotkeyService.BuildGestureString(e);
        if (ButtonsGrid.SelectedItem is HotButtonEdit editTarget)
        {
            editTarget.KeyboardShortcut = gesture;
            var warning = GetShortcutConflict(gesture, editTarget.Id);
            ShortcutConflict.Text = warning;
            ShortcutConflict.Visibility = warning != null
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
    }

    // Returns a warning string if the gesture conflicts with a fixed hotkey or another button, null if clean.
    private string? GetShortcutConflict(string gesture, string currentEditId)
    {
        // Check fixed hotkey bindings
        var fixedBinding = _configManager.HotkeyConfig.Bindings.FirstOrDefault(b =>
            b.IsEnabled &&
            string.Equals(b.KeyGesture, gesture, StringComparison.OrdinalIgnoreCase));
        if (fixedBinding != null)
            return $"⚠ '{gesture}' is also a fixed hotkey ({fixedBinding.ActionType}). Hot button will take priority.";

        // Check other buttons for duplicate shortcuts
        var dupe = Edits.FirstOrDefault(ed =>
            ed.Id != currentEditId &&
            string.Equals(ed.KeyboardShortcut, gesture, StringComparison.OrdinalIgnoreCase));
        if (dupe != null)
            return $"⚠ '{gesture}' is already assigned to '{dupe.Label}'.";

        return null;
    }

    private void ApplyTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not HotButtonTemplate tpl) return;
        if (ButtonsGrid.SelectedItem is not HotButtonEdit edit) return;
        edit.Script = tpl.Script;
        if (string.IsNullOrWhiteSpace(edit.Label) || edit.Label == "New Button")
            edit.Label = tpl.Name;
    }

    private void ClearScript_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonsGrid.SelectedItem is HotButtonEdit edit)
            edit.Script = null;
    }

    // ── Save / Cancel / Reset ─────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ButtonsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // Collect shortcut conflicts before saving
        var conflicts = new List<string>();
        var seenGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in Edits.Where(ed => !string.IsNullOrWhiteSpace(ed.KeyboardShortcut)))
        {
            var gesture = edit.KeyboardShortcut!;
            var fixedBinding = _configManager.HotkeyConfig.Bindings.FirstOrDefault(b =>
                b.IsEnabled &&
                string.Equals(b.KeyGesture, gesture, StringComparison.OrdinalIgnoreCase));
            if (fixedBinding != null)
                conflicts.Add($"  '{gesture}' on '{edit.Label}' conflicts with fixed hotkey '{fixedBinding.ActionType}' (hot button wins)");

            if (seenGestures.TryGetValue(gesture, out var firstLabel))
                conflicts.Add($"  '{gesture}' is assigned to both '{firstLabel}' and '{edit.Label}'");
            else
                seenGestures[gesture] = edit.Label;
        }

        if (conflicts.Count > 0)
        {
            var msg = "Shortcut conflicts detected:\n\n" + string.Join("\n", conflicts) +
                      "\n\nSave anyway?";
            var result = MessageBox.Show(msg, "Shortcut Conflicts",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _source.Clear();
        int order = 0;
        foreach (var edit in Edits)
        {
            var cfg = edit.ToHotButtonConfig();
            cfg.DisplayOrder = order++;
            _source.Add(cfg);
        }

        _configManager.SaveAll();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        Edits.Clear();
        foreach (var b in _source)
            Edits.Add(new HotButtonEdit(b));
    }
}

// ── HotButtonEdit view-model ───────────────────────────────────────────────

public enum HotButtonQtyMode { UseShareSize, Fixed, PercentOfPosition }

public class HotButtonEdit : INotifyPropertyChanged
{
    public string Id { get; }

    public static IReadOnlyList<HotButtonQtyMode> QtyModes { get; } =
        [HotButtonQtyMode.UseShareSize, HotButtonQtyMode.Fixed, HotButtonQtyMode.PercentOfPosition];

    public static IReadOnlyList<HotButtonAction> AvailableActions { get; } =
        Enum.GetValues<HotButtonAction>().ToList();

    private string _label;
    private string _color;
    private bool _isEnabled;
    private HotButtonAction _action;
    private HotButtonQtyMode _qtyMode;
    private string _qtyRawValue;
    private string? _script;
    private string? _keyboardShortcut;

    // ── Bindable properties ──────────────────────────────────────────────

    public string Label
    {
        get => _label;
        set { _label = value; OnChanged(nameof(Label)); }
    }

    public string Color
    {
        get => _color;
        set { _color = value; OnChanged(nameof(Color)); OnChanged(nameof(ColorBrush)); }
    }

    public Brush ColorBrush
    {
        get
        {
            try { return (Brush)new BrushConverter().ConvertFrom(_color)!; }
            catch { return Brushes.Gray; }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnChanged(nameof(IsEnabled)); }
    }

    public HotButtonAction Action
    {
        get => _action;
        set { _action = value; OnChanged(nameof(Action)); OnChanged(nameof(ActionDisplay)); OnChanged(nameof(ScriptPreview)); }
    }

    public string ActionDisplay => _action.ToString();

    public string? Script
    {
        get => _script;
        set { _script = value; OnChanged(nameof(Script)); OnChanged(nameof(ScriptPreview)); }
    }

    // Shows first line of script (truncated) or the legacy action name in gray.
    public string ScriptPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_script)) return $"[{_action}]";
            var first = _script.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return first.Length <= 60 ? first : first[..60] + "…";
        }
    }

    public string? KeyboardShortcut
    {
        get => _keyboardShortcut;
        set { _keyboardShortcut = value; OnChanged(nameof(KeyboardShortcut)); }
    }

    public HotButtonQtyMode QtyMode
    {
        get => _qtyMode;
        set
        {
            _qtyMode = value;
            OnChanged(nameof(QtyMode));
            OnChanged(nameof(QtyModeDisplay));
            OnChanged(nameof(QtyValue));
            OnChanged(nameof(QtyValueEditable));
            OnChanged(nameof(QtyValueFore));
        }
    }

    public string QtyRawValue
    {
        get => _qtyRawValue;
        set { _qtyRawValue = value; OnChanged(nameof(QtyRawValue)); OnChanged(nameof(QtyValue)); }
    }

    public string QtyModeDisplay => _qtyMode switch
    {
        HotButtonQtyMode.Fixed             => "Fixed",
        HotButtonQtyMode.PercentOfPosition => "% of Pos",
        _                                  => "Size Bar",
    };

    public string QtyValue => _qtyMode switch
    {
        HotButtonQtyMode.Fixed             => _qtyRawValue,
        HotButtonQtyMode.PercentOfPosition => $"{_qtyRawValue}%",
        _                                  => "—",
    };

    public bool QtyValueEditable => _qtyMode != HotButtonQtyMode.UseShareSize;
    public string QtyValueFore   => _qtyMode == HotButtonQtyMode.UseShareSize ? "#FF606060" : "#FFE0E0E0";

    // ── Conversion ────────────────────────────────────────────────────────

    public HotButtonConfig ToHotButtonConfig() => new()
    {
        Id               = Id,
        Label            = _label,
        Color            = _color,
        IsEnabled        = _isEnabled,
        Action           = _action,
        Script           = string.IsNullOrWhiteSpace(_script) ? null : _script,
        KeyboardShortcut = _keyboardShortcut,
        QuantityRule     = ToQuantityRule(),
    };

    public QuantityRule ToQuantityRule() => _qtyMode switch
    {
        HotButtonQtyMode.Fixed => new QuantityRule
        {
            Type        = QuantityRuleType.Fixed,
            FixedShares = int.TryParse(_qtyRawValue, out var n) ? n : 0,
        },
        HotButtonQtyMode.PercentOfPosition => new QuantityRule
        {
            Type              = QuantityRuleType.PercentOfPosition,
            PercentOfPosition = decimal.TryParse(_qtyRawValue, out var p) ? p : 100m,
        },
        _ => new QuantityRule { Type = QuantityRuleType.Fixed, FixedShares = 0 },
    };

    // ── Constructors ──────────────────────────────────────────────────────

    public static HotButtonEdit CreateNew() => new()
    {
        _label            = "New Button",
        _color            = "#FF2196F3",
        _isEnabled        = true,
        _action           = HotButtonAction.BuyMarket,
        _qtyMode          = HotButtonQtyMode.UseShareSize,
        _qtyRawValue      = "0",
        _script           = null,
        _keyboardShortcut = null,
    };

    public HotButtonEdit(HotButtonConfig b)
    {
        Id                = b.Id;
        _label            = b.Label;
        _color            = b.Color;
        _isEnabled        = b.IsEnabled;
        _action           = b.Action;
        _script           = b.Script;
        _keyboardShortcut = b.KeyboardShortcut;

        if (b.QuantityRule.Type == QuantityRuleType.PercentOfPosition)
        {
            _qtyMode     = HotButtonQtyMode.PercentOfPosition;
            _qtyRawValue = b.QuantityRule.PercentOfPosition.ToString("G");
        }
        else if (b.QuantityRule.FixedShares > 0)
        {
            _qtyMode     = HotButtonQtyMode.Fixed;
            _qtyRawValue = b.QuantityRule.FixedShares.ToString();
        }
        else
        {
            _qtyMode     = HotButtonQtyMode.UseShareSize;
            _qtyRawValue = "0";
        }
    }

    private HotButtonEdit()
    {
        Id           = Guid.NewGuid().ToString("N")[..8];
        _label       = "";
        _color       = "#FF2196F3";
        _qtyRawValue = "0";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
