namespace FastDOM.Core.Models;

public class HotkeyConfig
{
    public List<HotkeyBinding> Bindings { get; set; } = HotkeyBinding.Defaults();
    public bool GlobalHotkeysEnabled { get; set; } = false;
    public int DebounceMs { get; set; } = 250;
    public int DangerousActionDoublePressMs { get; set; } = 500;
}

public class HotkeyBinding
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string KeyGesture { get; init; }
    public required string ActionType { get; init; }
    public bool RequireDoublePress { get; set; } = false;
    public bool RequireConfirmation { get; set; } = false;
    public int CooldownMs { get; set; } = 0;
    public bool IsEnabled { get; set; } = true;
    public bool IsDangerous { get; set; } = false;

    public static List<HotkeyBinding> Defaults() =>
    [
        new() { Id = "buy_mkt",        Label = "Buy Marketable Limit",   KeyGesture = "Ctrl+B",       ActionType = "BuyMarketableLimit" },
        new() { Id = "sell_mkt",       Label = "Sell Marketable Limit",  KeyGesture = "Ctrl+S",       ActionType = "SellMarketableLimit" },
        new() { Id = "flatten",        Label = "Flatten Symbol",          KeyGesture = "Ctrl+F",       ActionType = "FlattenSymbol",      RequireDoublePress = true, IsDangerous = true },
        new() { Id = "reverse",        Label = "Reverse Position",        KeyGesture = "Ctrl+R",       ActionType = "ReversePosition",    RequireDoublePress = true, IsDangerous = true },
        new() { Id = "cancel_sym",     Label = "Cancel All Symbol",       KeyGesture = "Ctrl+X",       ActionType = "CancelAllSymbol" },
        new() { Id = "cancel_all",     Label = "Cancel All Account",      KeyGesture = "Ctrl+Shift+X", ActionType = "CancelAllAccount",   RequireConfirmation = true, IsDangerous = true },
        new() { Id = "size_1",         Label = "Share Size Preset 1",     KeyGesture = "Ctrl+D1",      ActionType = "SetSizePreset1" },
        new() { Id = "size_2",         Label = "Share Size Preset 2",     KeyGesture = "Ctrl+D2",      ActionType = "SetSizePreset2" },
        new() { Id = "size_3",         Label = "Share Size Preset 3",     KeyGesture = "Ctrl+D3",      ActionType = "SetSizePreset3" },
        new() { Id = "price_up",       Label = "Increase Order Price",    KeyGesture = "Ctrl+Up",      ActionType = "IncreasePriceByTick" },
        new() { Id = "price_down",     Label = "Decrease Order Price",    KeyGesture = "Ctrl+Down",    ActionType = "DecreasePriceByTick" },
        new() { Id = "emergency",      Label = "Emergency: Flatten+Cancel",KeyGesture = "Ctrl+Shift+E", ActionType = "EmergencyFlattenCancel", RequireDoublePress = true, IsDangerous = true },
    ];
}
