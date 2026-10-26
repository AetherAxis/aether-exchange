using Aether.Exchange;

namespace Aether.Exchange.Tests;

/// <summary>
/// 测试夹具品种注册（<see cref="InstrumentCatalog.RegisterVariety"/>）——生产品种表 2026-10-26 起
/// 只带 ax（用户定调「不要乱七八糟的品种，只一个」），测试用的 ag/zx 元数据（全部数值断言按此校准）
/// 由本模块在程序集加载时注册。值 = 被移除前的品种表原值，一个字符没动。
/// </summary>
internal static class TestVarietyInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        // ag：SimHarness.Instrument = ag2610 的元数据（×15 / 12% / tick 1 / SHFE / 基价 7000 / 昨结 6990）
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "ag", ExchangeId = "SHFE",
            VolumeMultiple = 15, MarginRate = 0.12, PriceTick = 1,
            BasePrice = 7000, PreSettlement = 6990,
        });
        // zx：PostOnlyTests / MakerTakerFeeTests 的 zx001 元数据（×10 / 10% / tick 1 / SIM / 基价 3800 / 昨结 3798 /
        // maker 2bp / taker 5bp）
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "zx", ExchangeId = "SIM",
            VolumeMultiple = 10, MarginRate = 0.10, PriceTick = 1,
            BasePrice = 3800, PreSettlement = 3798,
            MakerCommissionRate = 0.0002, TakerCommissionRate = 0.0005,
        });
    }
}
