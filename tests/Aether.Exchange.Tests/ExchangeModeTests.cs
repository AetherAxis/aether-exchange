using Aether.Exchange;
using Aether.Exchange.Storage;

namespace Aether.Exchange.Tests;

/// <summary>
/// 交易所模式（价格发现归簿——2026-10-03 起恒为唯一模式）：行情 = 订单簿顶 + 成交流、
/// 成交驱动最新价、市价单空簿拒单、市价残量作废（2026-09-28 起做市/噪声 = 外部客户端策略，服务端无内嵌机器人）。
/// ag2610 种子元数据：昨结算 6990 / tick 1 / 乘数 15 / 保证金率 12%（做市报价断言的确定性来源）。
/// <para>（2026-10-23 自 monorepo 迁入：原「订阅即收到盘口快照_wire级路由」用例走 RemoteCtpSession
/// （Front 仓的 ICtpSession 适配器）——随 Session 契约测试迁 ctp-net-front/tests，此处不再持协议侧依赖。）</para>
/// </summary>
public class ExchangeModeTests
{
    /// <summary>交易所核心（已激活 ag2610、上市表 = [ag2610]——做市报价由外部客户端策略供给，服务端无内嵌机器人）。</summary>
    private static async Task<SimTradingCore> NewExchangeCoreAsync()
    {
        var core = await SimHarness.NewCoreAsync(SimHarness.Options(), seedMaker: false);
        core.ActivateMarket(SimHarness.Instrument);
        return core;
    }

    [Fact]
    public async Task 市价单对手侧空簿被拒()
    {
        var core = await NewExchangeCoreAsync();

        var ex = Assert.Throws<ExchangeBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 1)));
        Assert.Equal(SimErrorCodes.NoCounterparty, ex.ErrorId);

        // 拒单 = 未接受——bob 无报单无持仓
        Assert.Empty(core.QueryOrders("bob", null));
        Assert.Empty(core.QueryPositions("bob", null));
    }

    [Fact]
    public async Task 未上市合约下单被拒_即便品种可派生()
    {
        // 上市表 = 唯一可交易集合——rb2701 虽可从 rb 品种派生元数据，但未上架 ⇒ 拒 90002。
        // （上架是显式决定——「下单即合成合约」的沙盒语义已随 walk 模式删除。）
        var core = await NewExchangeCoreAsync();   // 上市表 = [ag2610]

        var ex = Assert.Throws<ExchangeBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Limit(Direction.Buy, 1, 7000, instrument: "rb2701")));
        Assert.Equal(SimErrorCodes.UnknownInstrument, ex.ErrorId);
        Assert.Contains("未上市", ex.ErrorMsg);
        Assert.Empty(core.QueryOrders("bob", null));
    }

    [Fact]
    public async Task 成交tick落盘并跨重启恢复行情状态()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sim-tick-{Guid.NewGuid():N}.db");
        try
        {
            var options = SimHarness.Options() with { DatabasePath = db };
            var core = new SimTradingCore(options, store: SimStore.ForFile(db));
            await core.InitializeAsync();
            SimHarness.FundSandbox(core);   // 开户余额恒 0 ⇒ 手工建核的用例显式入金（链上充值替身）
            core.ActivateMarket(SimHarness.Instrument);
            core.InsertOrder("alice", SimHarness.Limit(Direction.Sell, 2, 6992));   // 对手挂单（做市商角色——外部客户端语义）
            core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 2));           // 成交 @6992 ×2
            core.Dispose();                                                        // 同步 flush

            // 落盘可查：store 直读，最新一条 = 该笔成交的快照（真实交易消息）
            var store = SimStore.ForFile(db);
            await store.InitializeAsync();
            var ticks = await store.LoadRecentTicksAsync(SimHarness.Instrument, 10);
            Assert.NotEmpty(ticks);
            Assert.Equal(6992, ticks[^1].LastPrice, 6);
            Assert.Equal(2, ticks[^1].Volume);

            // 重启恢复：最新价从最后成交续上（不回昨结算基准 6990）
            var revived = new SimTradingCore(options, store: SimStore.ForFile(db));
            await revived.InitializeAsync();
            Assert.Equal(6992, revived.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice, 6);

            // 累计量延续：重启后激活的快照帧携带恢复后的累计成交量（不归零）
            var after = new Collector<Tick>();
            revived.TickProduced += after.Add;
            revived.ActivateMarket(SimHarness.Instrument);
            var snapshotTick = after.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
            Assert.Equal(2, snapshotTick.Volume);
        }
        finally { try { File.Delete(db); } catch { } }
    }

    [Fact]
    public async Task 未上市合约订阅不激活()
    {
        var core = await NewExchangeCoreAsync();

        Assert.False(core.ActivateMarket("rb2701"));   // 不激活 ⇒ 无行情状态
        Assert.Null(core.Market.GetQuote("rb2701"));
    }

    [Fact]
    public async Task 自己的挂单不算对手_市价单仍被拒()
    {
        var core = await NewExchangeCoreAsync();

        // bob 自己挂卖——自成交跳过 ⇒ 对 bob 的市价买而言仍无对手
        core.InsertOrder("bob", SimHarness.Limit(Direction.Sell, 1, 7000));
        var ex = Assert.Throws<ExchangeBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 1)));
        Assert.Equal(SimErrorCodes.NoCounterparty, ex.ErrorId);
    }

    [Fact]
    public async Task 挂单入簿即推tick_簿顶是真实挂单()
    {
        var core = await NewExchangeCoreAsync();
        var ticks = new Collector<Tick>();
        core.TickProduced += ticks.Add;

        core.InsertOrder("alice", SimHarness.Limit(Direction.Buy, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(Direction.Buy, 3, 7000));   // 同价档——顶档量聚合

        // 行情 = 簿顶（不再是合成 mid∓tick）：买一 7000 × 5，卖侧空（真实 CTP 单边市场语义）
        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(7000, last.BidPrice, 6);
        Assert.Equal(5, last.BidVolume);
        Assert.Equal(0, last.AskPrice, 6);
        Assert.Equal(0, last.AskVolume);
    }

    [Fact]
    public async Task 成交驱动最新价_累计量为真实成交()
    {
        var core = await NewExchangeCoreAsync();
        var ticks = new Collector<Tick>();
        core.TickProduced += ticks.Add;

        core.InsertOrder("alice", SimHarness.Limit(Direction.Buy, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(Direction.Buy, 3, 7000));
        core.InsertOrder("dave", SimHarness.Market(Direction.Sell, 5));   // 吃掉两笔挂单（maker 价 7000）

        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(7000, last.LastPrice, 6);   // 最新价 = 成交价（不是游走值）
        Assert.Equal(5, last.Volume);            // 累计量 = 真实成交手数（不是随机累计）
        Assert.Equal(0, last.BidPrice, 6);       // 挂单全成离簿——买侧空
    }

    [Fact]
    public async Task 市价单无界吃全簿多档()
    {
        var core = await NewExchangeCoreAsync();
        var trades = new Collector<(string AccountId, Trade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(Direction.Sell, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(Direction.Sell, 3, 7001));
        core.InsertOrder("dave", SimHarness.Market(Direction.Buy, 5));   // 交易所模式可吃边界 = 全簿（无庄家报价界）

        var daveTrades = trades.Snapshot.Where(t => t.AccountId == "dave").Select(t => t.Trade).ToList();
        Assert.Equal(2, daveTrades.Count);
        Assert.Equal(7000, daveTrades[0].Price, 6);   // 价低档先
        Assert.Equal(2, daveTrades[0].Volume);
        Assert.Equal(7001, daveTrades[1].Price, 6);
        Assert.Equal(3, daveTrades[1].Volume);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("dave", null)).OrderStatusRaw);
    }

    [Fact]
    public async Task FOK簿量不足整单作废()
    {
        var core = await NewExchangeCoreAsync();
        var trades = new Collector<(string AccountId, Trade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(Direction.Sell, 3, 7000));
        core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 5, timeCondition: TimeCondition.FOK));   // 簿只有 3 手

        // FOK 不能全成 ⇒ 整单作废（无庄家兜底可补量）
        Assert.Equal(SimOrderStatus.NoTradeNotQueueing, Assert.Single(core.QueryOrders("bob", null)).OrderStatusRaw);
        Assert.DoesNotContain(trades.Snapshot, t => t.AccountId == "bob");
        // alice 的挂单原封不动
        Assert.Equal(SimOrderStatus.NoTradeQueueing, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);
    }

    [Fact]
    public async Task 市价残量作废不挂单()
    {
        var core = await NewExchangeCoreAsync();

        core.InsertOrder("alice", SimHarness.Limit(Direction.Sell, 2, 7000));
        core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 5));   // 簿只有 2 手——残量 3 手作废（不挂单）

        var bobOrder = Assert.Single(core.QueryOrders("bob", null));
        Assert.Equal(SimOrderStatus.PartTradedNotQueueing, bobOrder.OrderStatusRaw);   // '2' 部分成交不在队列
        Assert.Equal(2, bobOrder.VolumeTraded);
        Assert.Equal(0, bobOrder.VolumeTotal);   // 残量作废 ⇒ 剩余清零（与重启作废路径同款语义）
        // 残量不入簿：买侧仍空
        Assert.Equal(0, core.Market.GetQuote(SimHarness.Instrument)!.Value.BidPrice, 6);
    }

    [Fact]
    public async Task 订阅即推当前快照tick()
    {
        var core = await NewExchangeCoreAsync();

        // 订阅前簿里已有挂单（挂单时的 tick 没人听）
        core.InsertOrder("alice", SimHarness.Limit(Direction.Buy, 5, 7000));

        var ticks = new Collector<Tick>();
        core.TickProduced += ticks.Add;
        var ok = core.ActivateMarket(SimHarness.Instrument);   // 订阅 ⇒ 立即推快照

        Assert.True(ok);
        var snapshot = Assert.Single(ticks.Snapshot, t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(7000, snapshot.BidPrice, 6);
        Assert.Equal(5, snapshot.BidVolume);
    }

    [Fact]
    public async Task 持仓盈亏按最新成交价()
    {
        var core = await NewExchangeCoreAsync();

        core.InsertOrder("alice", SimHarness.Limit(Direction.Sell, 1, 7000));
        core.InsertOrder("bob", SimHarness.Market(Direction.Buy, 1));       // bob 多头 @7000
        core.InsertOrder("carol", SimHarness.Limit(Direction.Sell, 1, 7010));
        core.InsertOrder("dave", SimHarness.Market(Direction.Buy, 1));      // 最新价推到 7010

        // bob 持仓浮盈 = (7010 − 7000) × 15 × 1 = 150——盯市价是**最新成交价**（不是游走值）
        Assert.Equal(150, core.QueryAccount("bob").PositionProfit, 6);
    }
}
