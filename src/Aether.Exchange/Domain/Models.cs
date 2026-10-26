namespace Aether.Exchange;

// —— 交易所领域 DTO（纯数据载体，无行为；零外部依赖）——

/// <summary>登录结果：含订单回报匹配键 FrontId/SessionId/MaxOrderRef。</summary>
public sealed record ExchangeLoginResult(
    string TradingDay, string LoginTime, int FrontId, int SessionId, string MaxOrderRef);

/// <summary>资金账户。</summary>
public sealed record Account
{
    public string AccountId { get; init; } = "";
    public double Balance { get; init; }
    public double Available { get; init; }
    public double WithdrawQuota { get; init; }
    public double FrozenMargin { get; init; }
    public double FrozenCash { get; init; }
    public double Commission { get; init; }
    public double CurrMargin { get; init; }
    public double PositionProfit { get; init; }
    public double CloseProfit { get; init; }
}

/// <summary>持仓。</summary>
public sealed record Position
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public PosiDirection PosiDirection { get; init; }
    public HedgeFlag HedgeFlag { get; init; }
    public PositionDate PositionDate { get; init; }
    public int YdPosition { get; init; }
    public int TotalPosition { get; init; }
    public int TodayPosition { get; init; }
    public double UseMargin { get; init; }
    public double PositionProfit { get; init; }
    public double CloseProfit { get; init; }
}

/// <summary>报单（订单）。</summary>
public sealed record Order
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public int FrontId { get; init; }
    public int SessionId { get; init; }
    public string OrderSysId { get; init; } = "";
    public Direction Direction { get; init; }
    public OffsetFlag OffsetFlag { get; init; }
    public OrderPriceType PriceType { get; init; }
    public double LimitPrice { get; init; }
    public int VolumeTotalOriginal { get; init; }
    public int VolumeTraded { get; init; }
    public int VolumeTotal { get; init; }
    public byte OrderStatusRaw { get; init; }
    public string InsertTime { get; init; } = "";
    public string UpdateTime { get; init; } = "";
    public string StatusMsg { get; init; } = "";
}

/// <summary>成交。</summary>
public sealed record Trade
{
    public string InstrumentId { get; init; } = "";
    public string OrderRef { get; init; } = "";
    public string TradeId { get; init; } = "";
    public string OrderSysId { get; init; } = "";
    public Direction Direction { get; init; }
    public OffsetFlag OffsetFlag { get; init; }
    public double Price { get; init; }
    public int Volume { get; init; }
    public string TradeTime { get; init; } = "";
    /// <summary>交易所代码（2026-10-24：查成交记录的 CTP 字段面带交易所；成交生成时由合约元数据补）。</summary>
    public string ExchangeId { get; init; } = "";
    /// <summary>成交日期 yyyyMMdd（日历日——夜盘与交易日不同；成交生成时由时钟补）。</summary>
    public string TradeDate { get; init; } = "";
}

/// <summary>Tick 行情。</summary>
public sealed record Tick
{
    public string InstrumentId { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public string TradingDay { get; init; } = "";
    public string ActionDay { get; init; } = "";
    public string UpdateTime { get; init; } = "";
    public int UpdateMillisec { get; init; }
    public double LastPrice { get; init; }
    public long Volume { get; init; }
    public double OpenInterest { get; init; }
    public double Turnover { get; init; }
    public double BidPrice { get; init; }
    public int BidVolume { get; init; }
    public double AskPrice { get; init; }
    public int AskVolume { get; init; }
    public double PreSettlement { get; init; }
    public double PreClose { get; init; }
    public double OpenPrice { get; init; }
    public double Highest { get; init; }
    public double Lowest { get; init; }
}

/// <summary>合约静态元数据。</summary>
public sealed record Instrument
{
    public string InstrumentId { get; init; } = "";
    /// <summary>合约中文名（2026-10-26 起服务端携带——源在 InstrumentCatalog 品种表；空 = 无名）。</summary>
    public string Name { get; init; } = "";
    public string ExchangeId { get; init; } = "";
    public double VolumeMultiple { get; init; }
    public double PriceTick { get; init; }
    public double PreSettlement { get; init; }
    public double BasePrice { get; init; }
}

/// <summary>下单请求。</summary>
public sealed class OrderRequest
{
    public required string BrokerId { get; init; }
    public required string InvestorId { get; init; }
    public required string InstrumentId { get; init; }
    public required Direction Direction { get; init; }
    public required OffsetFlag OffsetFlag { get; init; }
    public required OrderPriceType PriceType { get; init; }
    public required double LimitPrice { get; init; }
    public required int Volume { get; init; }
    public TimeCondition TimeCondition { get; init; } = TimeCondition.GFD;
    public string? ClientOrderRef { get; init; }
}

/// <summary>下单结果。</summary>
public sealed record OrderInsertResult(string ClientOrderRef, int RequestId);

/// <summary>撤单定位键（FrontId/SessionId/OrderRef 唯一定位一笔报单）。</summary>
public sealed record CancelKey(
    string BrokerId, string InvestorId, int FrontId, int SessionId, string ClientOrderRef, string ExchangeId);
