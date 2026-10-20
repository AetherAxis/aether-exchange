namespace Aether.Exchange;

// —— 交易所领域枚举（byte 基，值镜像 CTP 协议 char 码，便于 Ctp.Net.Front Adapter 强转）——

/// <summary>买卖方向。</summary>
public enum Direction : byte { Buy = (byte)'0', Sell = (byte)'1' }

/// <summary>开平标志。</summary>
public enum OffsetFlag : byte
{
    Open = (byte)'0',
    Close = (byte)'1',
    CloseToday = (byte)'3',
    CloseYesterday = (byte)'4',
}

/// <summary>报单价格类型。</summary>
public enum OrderPriceType : byte { Market = (byte)'0', Limit = (byte)'2' }

/// <summary>
/// 有效期类型。'1'/'2'/'3' 对齐真实 CTP；<see cref="PostOnly"/> ('4') 为本地协议扩展值。
/// </summary>
public enum TimeCondition : byte { IOC = (byte)'1', FOK = (byte)'2', GFD = (byte)'3', PostOnly = (byte)'4' }

/// <summary>持仓方向。</summary>
public enum PosiDirection : byte { Net = (byte)'1', Long = (byte)'2', Short = (byte)'3' }

/// <summary>投机套保标志。</summary>
public enum HedgeFlag : byte { Speculation = (byte)'1', Arbitrage = (byte)'2', Hedge = (byte)'3' }

/// <summary>持仓日期（今/昨）。</summary>
public enum PositionDate : byte { Today = (byte)'1', History = (byte)'2' }
