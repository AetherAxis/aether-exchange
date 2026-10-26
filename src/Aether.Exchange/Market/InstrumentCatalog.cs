using System;
using System.Collections.Generic;
using System.Linq;

namespace Aether.Exchange;

/// <summary>
/// 内置合约表种子 + 「未知合约」的兜底合成。
/// <para>
/// **生产品种表只带 ax 一个品种**（2026-10-26 用户定调「不要乱七八糟的品种，只一个」）——
/// 仿真柜台实际只上市 ax001（上市表默认）。需要别的品种（host 上架自定义产品、测试夹具）
/// 经 <see cref="RegisterVariety"/> 注册，不往生产表里塞。
/// </para>
/// </summary>
public static class InstrumentCatalog
{
    /// <summary>未知合约的默认乘数（上市表自定义 id 经合成兜底时使用）。</summary>
    public const double DefaultUnknownVolumeMultiple = 10;

    /// <summary>未知合约的默认保证金率。</summary>
    public const double DefaultUnknownMarginRate = 0.10;

    /// <summary>未知合约的默认最小变动价位。</summary>
    public const double DefaultUnknownPriceTick = 1;

    /// <summary>主力月份后缀（2610/2612/2701…）从代码中剥掉得到品种前缀，用于查品种元数据。</summary>
    public static string VarietyOf(string instrumentId)
    {
        var i = 0;
        while (i < instrumentId.Length && !char.IsDigit(instrumentId[i])) i++;
        return i == 0 ? instrumentId : instrumentId[..i];
    }

    /// <summary>
    /// 品种级元数据（不含具体月份）——月份只影响代码，不影响乘数/保证金率/最小变动价位，
    /// 故未知月份能按品种自动派生（<c>ax001</c> 之外的 ax 合约同样落表）。
    /// <para>
    /// <c>ax</c> = Aether 币价合约（批 4 现货 → 2026-10-10 币通道退役 + 开空解禁后的最终形态）：
    /// AQ 计价的保证金品种，与期货完全一致（买=开多/卖=开空/双向持仓/10% 保证金=10 倍杠杆）。
    /// 交易所单资产化——只收付计价币 AQ；coin 不进交易所，场内头寸全部纸面。
    /// 1 手 = 1 币名义；基价 100（冷启动记价基准，运行后订单簿价格发现）。maker/taker 费率
    /// BTC 对标（挂 2bp / 吃 5bp）。PriceTick=0.01（币价分级精度）。
    /// <see cref="SimInstrument.Name"/>：Aether币价期货（2026-10-26 服务端合约中文名）。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, SimInstrument> Varieties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ax"] = new SimInstrument { InstrumentId = "ax", Name = "Aether币价期货", ExchangeId = "AETHER", VolumeMultiple = 1, MarginRate = 0.10, PriceTick = 0.01, BasePrice = 100, PreSettlement = 100, IsSpot = true, MakerCommissionRate = 0.0002, TakerCommissionRate = 0.0005 },
    };

    /// <summary>
    /// 注册自定义品种（品种表的扩展口——host 上架自定义产品 / 测试夹具注入；生产表只带 ax）。
    /// 键 = 品种前缀（<see cref="SimInstrument.InstrumentId"/>），重复注册覆盖。须在任何核心
    /// 初始化之前调用（表无锁——设计为启动期一次性注册）。
    /// </summary>
    public static void RegisterVariety(SimInstrument variety) =>
        Varieties[variety.InstrumentId] = variety;

    /// <summary>带月份的种子合约（默认装载集合）——只种真实的那个：ax001。</summary>
    public static IReadOnlyList<SimInstrument> Seed() => new[] { With("ax", "ax001") };

    /// <summary>
    /// 查不到精确合约时按品种派生一个（<c>ag2602</c> → 用 <c>ag</c> 的元数据；ax001 → ax 的元数据 + 名）。
    /// 都不认识 → 返回 null，由调用方决定拒绝还是兜底合成。
    /// </summary>
    public static SimInstrument? DeriveByVariety(string instrumentId, IReadOnlyDictionary<string, SimInstrument> known)
    {
        if (known.TryGetValue(instrumentId, out var exact)) return exact;
        var variety = VarietyOf(instrumentId);
        return Varieties.TryGetValue(variety, out var v) ? v with { InstrumentId = instrumentId } : null;
    }

    /// <summary>完全不认识的合约：用下单价合成一个能撮合的最小合约（严格模式下不走这里）。无名。</summary>
    public static SimInstrument Synthesize(string instrumentId, double referencePrice)
    {
        var price = referencePrice > 0 ? referencePrice : 100;
        return new SimInstrument
        {
            InstrumentId = instrumentId,
            // 交易所未知时留空——CTP 的撤单定位键用 ExchangeId 仅为回显，空串不影响撤单（按 FrontId/SessionId/CancelKey 定位）
            ExchangeId = "",
            VolumeMultiple = DefaultUnknownVolumeMultiple,
            MarginRate = DefaultUnknownMarginRate,
            PriceTick = DefaultUnknownPriceTick,
            BasePrice = price,
            PreSettlement = price,
        };
    }

    /// <summary>
    /// 老库兜底补名（2026-10-26 加 Name 字段前的持久化合约读出来 Name 为空）：品种表里有名的（ax 家族）
    /// 按品种名补，其余保持无名。幂等——已有名的原样返回。
    /// </summary>
    public static SimInstrument WithBackfilledName(SimInstrument instrument)
    {
        if (instrument.Name.Length > 0) return instrument;
        var variety = VarietyOf(instrument.InstrumentId);
        return Varieties.TryGetValue(variety, out var v) && v.Name.Length > 0
            ? instrument with { Name = v.Name }
            : instrument;
    }

    private static SimInstrument With(string variety, string instrumentId)
    {
        var v = Varieties[variety];
        return v with { InstrumentId = instrumentId };
    }
}
