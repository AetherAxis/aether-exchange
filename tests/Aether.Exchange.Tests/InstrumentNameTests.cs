using Aether.Exchange;
using Xunit;

namespace Aether.Exchange.Tests;

/// <summary>
/// 服务端合约中文名（2026-10-26）：**只给自有品种配**（ax → Aether币价期货）——真实品种不由仿真
/// 臆造名（用户定调「种子只需要真实的那个，别瞎加东西」）。老库无名合约经品种表兜底补名。
/// </summary>
public class InstrumentNameTests
{
    [Fact]
    public void ax家族派生携带中文名()
    {
        var derived = InstrumentCatalog.DeriveByVariety("ax001", new Dictionary<string, SimInstrument>());

        Assert.NotNull(derived);
        Assert.Equal("Aether币价期货", derived.Name);
        Assert.Equal("ax001", derived.InstrumentId);
    }

    [Fact]
    public void 真实品种派生保持无名()
    {
        var derived = InstrumentCatalog.DeriveByVariety("ag2602", new Dictionary<string, SimInstrument>());

        Assert.NotNull(derived);
        Assert.Equal("", derived!.Name);   // 白银之类不臆造——服务端只认 ax 的名
    }

    [Fact]
    public void 老库无名合约按品种兜底补名_幂等()
    {
        var stored = new SimInstrument { InstrumentId = "ax001", Name = "", ExchangeId = "AETHER", BasePrice = 100 };

        var backfilled = InstrumentCatalog.WithBackfilledName(stored);
        Assert.Equal("Aether币价期货", backfilled.Name);

        var again = InstrumentCatalog.WithBackfilledName(backfilled);
        Assert.Same(backfilled, again);   // 已有名原样返回（幂等，不重复落盘）

        var real = InstrumentCatalog.WithBackfilledName(
            new SimInstrument { InstrumentId = "ag2610", Name = "", ExchangeId = "SHFE", BasePrice = 7000 });
        Assert.Equal("", real.Name);      // 真实品种无名可补——保持无名
    }

    [Fact]
    public void 种子清单只种真实的那个且带名()
    {
        var seed = InstrumentCatalog.Seed();

        var only = Assert.Single(seed);
        Assert.Equal("ax001", only.InstrumentId);
        Assert.Equal("Aether币价期货", only.Name);
        Assert.Equal("AETHER", only.ExchangeId);
    }
}
