namespace FastDOM.Core.Enums;

public enum OrderSide { Buy, Sell }

public enum OrderType
{
    Market,
    Limit,
    StopMarket,
    StopLimit,
    MarketableLimit,
    Bracket,
    OCO,
    OSO
}

public enum TimeInForce { Day, GTC, IOC, FOK, GTD }

public enum OrderSession { Normal, AM, PM, Seamless }

public enum AssetType { Equity, Option, Future, ETF, MutualFund }

public enum OrderStatus
{
    Draft,
    Validating,
    RejectedLocally,
    Submitting,
    Submitted,
    Accepted,
    Working,
    PartiallyFilled,
    Filled,
    CancelPending,
    Cancelled,
    ReplacePending,
    Replaced,
    BrokerRejected,
    Unknown,
    Error
}

public enum OrderSource
{
    DomClick,
    HotButton,
    Hotkey,
    OrderTicket,
    ApiTest,
    System
}

public enum TradingMode
{
    Simulation,
    SchwabLive,
    AlpacaPaper,
    AlpacaLive
}

public enum PositionSide { Flat, Long, Short }
