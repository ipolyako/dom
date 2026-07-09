namespace FastDOM.App.Services;

public record HotButtonTemplate(string Name, string Description, string Script);

public static class HotButtonTemplates
{
    public static readonly IReadOnlyList<string> Categories =
    [
        "Market Orders",
        "Limit Orders",
        "Dollar Amount",
        "Scale Out",
        "Cancel & Replace",
        "Risk-Based Entry",
        "Bracket Orders",
        "Position Management",
        "Dialog-Based",
    ];

    private static readonly Dictionary<string, List<HotButtonTemplate>> _catalog = new()
    {
        ["Market Orders"] =
        [
            new("Buy Market",         "Buy configured share size at market",    "BUY SIZE MKT"),
            new("Sell Market",        "Sell configured share size at market",   "SELL SIZE MKT"),
            new("Buy 100 Market",     "Buy exactly 100 shares at market",       "BUY 100 MKT"),
            new("Sell 100 Market",    "Sell exactly 100 shares at market",      "SELL 100 MKT"),
        ],
        ["Limit Orders"] =
        [
            new("Buy at Ask",         "Buy SIZE shares at the ask",             "BUY SIZE LMT ASK"),
            new("Sell at Bid",        "Sell SIZE shares at the bid",            "SELL SIZE LMT BID"),
            new("Buy at Bid",         "Passive buy at bid",                     "BUY SIZE LMT BID"),
            new("Sell at Ask",        "Passive sell at ask",                    "SELL SIZE LMT ASK"),
            new("Buy Ask+0.05",       "Buy with $0.05 slippage allowance",      "BUY SIZE LMT ASK+0.05"),
            new("Sell Bid-0.05",      "Sell with $0.05 slippage allowance",     "SELL SIZE LMT BID-0.05"),
        ],
        ["Dollar Amount"] =
        [
            new("Buy $500",           "Buy $500 worth at market",               "BUY $500 MKT"),
            new("Buy $1000",          "Buy $1000 worth at market",              "BUY $1000 MKT"),
            new("Buy $2500",          "Buy $2500 worth at market",              "BUY $2500 MKT"),
            new("Buy $1000 at Ask",   "Buy $1000 worth at the ask",             "BUY $1000 LMT ASK"),
            new("Buy $2500 at Ask",   "Buy $2500 worth at the ask",             "BUY $2500 LMT ASK"),
            new("Sell $500",          "Sell $500 worth at market",              "SELL $500 MKT"),
            new("Sell $1000",         "Sell $1000 worth at market",             "SELL $1000 MKT"),
        ],
        ["Scale Out"] =
        [
            new("Sell 10% MKT",       "Scale out 10% at market",                "SELL PCT:10 MKT"),
            new("Sell 25% MKT",       "Scale out 25% at market",                "SELL PCT:25 MKT"),
            new("Sell 33% MKT",       "Scale out 33% at market",                "SELL PCT:33 MKT"),
            new("Sell Half MKT",      "Sell 50% of position at market",         "SELL HALF MKT"),
            new("Sell 50% MKT",       "Scale out 50% at market",                "SELL PCT:50 MKT"),
            new("Sell 10% Bid",       "Scale out 10% at bid (passive)",         "SELL PCT:10 LMT BID"),
            new("Sell 25% Bid",       "Scale out 25% at bid (passive)",         "SELL PCT:25 LMT BID"),
            new("Sell 33% Bid",       "Scale out 33% at bid (passive)",         "SELL PCT:33 LMT BID"),
            new("Sell Half Bid",      "Sell 50% of position at bid (passive)",  "SELL HALF LMT BID"),
            new("Cover 10% MKT",      "Cover 10% of short at market",           "BUY PCT:10 MKT"),
            new("Cover Half MKT",     "Cover 50% of short at market",           "BUY HALF MKT"),
            new("Cover 33% MKT",      "Cover 33% of short at market",           "BUY PCT:33 MKT"),
        ],
        ["Cancel & Replace"] =
        [
            new("Cancel All",                "Cancel all working orders",              "CANCEL ALL"),
            new("Cancel Buy Orders",         "Cancel buy orders only",                "CANCEL BUY"),
            new("Cancel Sell Orders",        "Cancel sell orders only",               "CANCEL SELL"),
            new("Cancel All + Buy MKT",      "Cancel all then buy at market",
                "CANCEL ALL\nBUY SIZE MKT"),
            new("Cancel All + Sell MKT",     "Cancel all then sell at market",
                "CANCEL ALL\nSELL SIZE MKT"),
            new("Cancel Buys + Rebid",       "Cancel buys then re-enter at bid",
                "CANCEL BUY\nBUY SIZE LMT BID"),
            new("Cancel Sells + Reask",      "Cancel sells then re-offer at ask",
                "CANCEL SELL\nSELL SIZE LMT ASK"),
            new("Cancel All + Buy Ask",      "Cancel all then chase at ask",
                "CANCEL ALL\nBUY SIZE LMT ASK"),
        ],
        ["Risk-Based Entry"] =
        [
            new("Risk $50 Buy",       "Size for $50 loss at stop (stop ASK-0.50)",    "BUY RISK:$50 LMT ASK STOP ASK-0.50"),
            new("Risk $100 Buy",      "Size for $100 loss at stop (stop ASK-1.00)",   "BUY RISK:$100 LMT ASK STOP ASK-1.00"),
            new("Risk $250 Buy",      "Size for $250 loss at stop",                   "BUY RISK:$250 LMT ASK STOP ASK-1.00"),
            new("Risk $500 Buy",      "Size for $500 loss at stop",                   "BUY RISK:$500 LMT ASK STOP ASK-1.00"),
            new("Risk $50 Sell",      "Size for $50 loss at stop (short, BID+0.50)",  "SELL RISK:$50 LMT BID STOP BID+0.50"),
            new("Risk $100 Sell",     "Size for $100 loss at stop (short)",           "SELL RISK:$100 LMT BID STOP BID+1.00"),
            new("Risk $250 Sell",     "Size for $250 loss at stop (short)",           "SELL RISK:$250 LMT BID STOP BID+1.00"),
        ],
        ["Bracket Orders"] =
        [
            new("Buy + 1:1 Bracket",
                "Buy at ask, stop -$1, target +$1 (1:1)",
                "BUY SIZE LMT ASK\nBRACKET STOP ENTRY-1.00 T1 ENTRY+1R"),
            new("Buy + 1:2 Bracket",
                "Buy at ask, stop -$1, target +$2 (1:2)",
                "BUY SIZE LMT ASK\nBRACKET STOP ENTRY-1.00 T1 ENTRY+2R"),
            new("Buy + 3-Target Bracket",
                "Buy at ask with 3 scaled targets at 1R/2R/3R",
                "BUY SIZE LMT ASK\nBRACKET STOP ENTRY-1.00 T1 ENTRY+1R T2 ENTRY+2R T3 ENTRY+3R"),
            new("Risk $100 + 1:2 Bracket",
                "Risk-sized buy with stop and 2R target",
                "BUY RISK:$100 LMT ASK STOP ASK-1.00\nBRACKET STOP ENTRY-1.00 T1 ENTRY+2R"),
            new("Risk $250 + 3-Target",
                "Risk $250 buy with 3 scaled exit targets",
                "BUY RISK:$250 LMT ASK STOP ASK-1.00\nBRACKET STOP ENTRY-1.00 T1 ENTRY+1R T2 ENTRY+2R T3 ENTRY+3R"),
            new("Short + 1:2 Bracket",
                "Sell at bid, stop +$1, cover target at -$2",
                "SELL SIZE LMT BID\nBRACKET STOP ENTRY+1.00 T1 ENTRY-2R"),
            new("Short + 3-Target Bracket",
                "Short with 3 scaled cover targets",
                "SELL SIZE LMT BID\nBRACKET STOP ENTRY+1.00 T1 ENTRY-1R T2 ENTRY-2R T3 ENTRY-3R"),
        ],
        ["Dialog-Based"] =
        [
            new("Risk Buy + 5 Scaled Targets",
                "Prompts for stop price → sizes by $300 risk → market buy → stop → 5 sell limits at 20% each (1R/2R/3R/4R/5R)",
                "DIALOG STOP\nBUY RISK:$300 MKT STOP $STOP\nBRACKET STOP $STOP T1 ENTRY+1R PCT:20 T2 ENTRY+2R PCT:20 T3 ENTRY+3R PCT:20 T4 ENTRY+4R PCT:20 T5 ENTRY+5R PCT:20"),
            new("Risk Sell Short + 5 Scaled Covers",
                "Prompts for stop price → sizes by $300 risk → market sell short → stop → 5 cover limits at 20% each",
                "DIALOG STOP\nSELL RISK:$300 MKT STOP $STOP\nBRACKET STOP $STOP T1 ENTRY-1R PCT:20 T2 ENTRY-2R PCT:20 T3 ENTRY-3R PCT:20 T4 ENTRY-4R PCT:20 T5 ENTRY-5R PCT:20"),
            new("Risk Buy + 3 Equal Targets",
                "Prompts for stop price → sizes by $300 risk → market buy → stop → 3 sell limits at 33% each",
                "DIALOG STOP\nBUY RISK:$300 MKT STOP $STOP\nBRACKET STOP $STOP T1 ENTRY+1R PCT:33 T2 ENTRY+2R PCT:33 T3 ENTRY+3R PCT:34"),
            new("Custom Risk $ Buy + 5 Targets",
                "Same as above but uses $500 risk — edit the script to change",
                "DIALOG STOP\nBUY RISK:$500 MKT STOP $STOP\nBRACKET STOP $STOP T1 ENTRY+1R PCT:20 T2 ENTRY+2R PCT:20 T3 ENTRY+3R PCT:20 T4 ENTRY+4R PCT:20 T5 ENTRY+5R PCT:20"),
        ],
        ["Position Management"] =
        [
            new("Flatten",            "Cancel all and flatten position at market", "FLAT MKT"),
            new("Reverse",            "Reverse position (cancel + double size)",   "REVERSE MKT"),
            new("Secure Position",
                "Prompts for stop price and surrounds current position with five 20% OCO target/stop tranches",
                "DIALOG STOP\nSECURE STOP $STOP"),
            new("Cancel + Flatten",   "Cancel all working orders then flatten",
                "CANCEL ALL\nFLAT MKT"),
        ],
    };

    public static IReadOnlyList<HotButtonTemplate> GetTemplates(string category)
    {
        if (_catalog.TryGetValue(category, out var list)) return list;
        return [];
    }
}
