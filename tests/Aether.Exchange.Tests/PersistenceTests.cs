using Aether.Exchange;
using Aether.Exchange.Rpc;
using Aether.Exchange.Storage;

namespace Aether.Exchange.Tests;

/// <summary>
/// 落盘与重启续用：仿真柜台的价值一半在「今天跑完，明天接着跑」——
/// 而这条路径**没有任何编译期检查**（存储是 <see cref="ISimStore"/> 端口，
/// 核心对「有没有真落盘」无感知），只能靠真实的落盘 → 重开验证。
/// <para>
/// 另钉住两个**序列**：报单号与成交号。状态（账户/持仓/报单）恢复对了但序列没续上，
/// 症状是「重启后第一张单重号」与「重启后成交记录被静默覆盖」——两者都不报错。
/// </para>
/// <para>（2026-10-23 自 monorepo 迁入：原「仿真库落盘后重启账本可续用」经 RemoteCtpSession
/// （Front 仓适配器）驱动——改用 <see cref="SimRpcClient"/>（Exchange.Rpc 裸线）等价驱动。）</para>
/// </summary>
public class PersistenceTests
{
    /// <summary>临时目录下的仿真库路径（绝不碰用户真实的 ctp-sim.db）。</summary>
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"aether-ctp-sim-{Guid.NewGuid():N}", "ctp-sim.db");

    /// <summary>落盘到指定文件 + ag2610 上架 + maker 双边挂单（撮合价可预期）。</summary>
    private static SimOptions OptionsFor(string dbPath)
        => new() { DatabasePath = dbPath, ListedInstruments = new[] { SimHarness.Instrument }, InitialBalance = SimHarness.InitialBalance };

    private static async Task<SimTradingCore> OpenCoreAsync(string dbPath)
    {
        var core = new SimTradingCore(OptionsFor(dbPath), new SimClock(() => SimHarness.TradingNow), store: SimStore.ForFile(dbPath));
        await core.InitializeAsync();
        SimHarness.SeedMaker(core);   // 重启后挂单不复活 ⇒ 每次开核都补双边流动性
        SimHarness.Login(core);
        return core;
    }

    [Fact]
    public async Task 仿真库落盘后重启账本可续用()
    {
        // 这条钉的是「host 重启（核心重建 + InitializeAsync 恢复）后账本续用」——独立 host 形态下
        // 落盘由 host 进程负责（LoopbackSim.Dispose = 停 listener + core.Dispose 同步 flush），
        // 重启 = 同一 db 再起一个 LoopbackSim。这条链路没有任何编译期检查，只能靠真实的落盘→重开验证。
        var dir = Path.Combine(Path.GetTempPath(), $"aether-ctp-sim-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(dir, "ctp-sim.db");
        Directory.CreateDirectory(dir);
        try
        {
            // maker 双边挂单供给流动性（撮合价可预期——LoopbackSim 默认种子）
            var configure = (Func<SimOptions, SimOptions>)(o => o with { DatabasePath = dbPath });

            using (var first = LoopbackSim.Start(configure))
            using (var client = await LoginClientAsync(first))
            {
                await client.InsertOrderAsync(SimHarness.Market(Direction.Buy, 2));
                Assert.Single(await client.QueryOrdersAsync(null));

                Assert.True(File.Exists(dbPath), $"仿真库应落在指定路径：{dbPath}");
            }   // Dispose：client 断开 → server 停 → core.Dispose（同步 flush）——确定性落盘点

            using (var second = LoopbackSim.Start(configure))
            using (var client2 = await LoginClientAsync(second))
            {
                // 同一账号的报单与持仓跨会话仍在（单号也从上次的序号续排，不重号）
                Assert.Single(await client2.QueryOrdersAsync(null));
                Assert.Equal(2, (await client2.QueryPositionsAsync(null)).Single().TotalPosition);

                var next = await client2.InsertOrderAsync(SimHarness.Market(Direction.Buy, 1));
                Assert.NotEqual("000000000001", next.ClientOrderRef);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<SimRpcClient> LoginClientAsync(LoopbackSim sim)
    {
        var client = new SimRpcClient("127.0.0.1", sim.Server.LocalEndpoint!.Port);
        await client.ConnectAsync();
        await client.LoginAsync(new LoginArg(SimHarness.Broker, SimHarness.Account, SimHarness.TestPassword));
        return client;
    }

    [Fact]
    public async Task 重启后报单号从上次的序号续排()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using (var first = await OpenCoreAsync(dbPath))
            {
                // 挂单不成交（限价低于买一）——目的是**占据**两个单号
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(Direction.Buy, 1, 6900));
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(Direction.Buy, 1, 6901));
                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                // 单号必须续上：重号会让 Accept 覆盖历史单，且客户端侧单号重复
                var next = second.InsertOrder(SimHarness.Account, SimHarness.Limit(Direction.Buy, 1, 6902));

                Assert.Equal("000000000003", next.ClientOrderRef);
                Assert.Equal(3, second.QueryOrders(SimHarness.Account).Count);

                // 单号互异是硬约束（历史 2 笔 + 新的 1 笔）
                var refs = second.QueryOrders(SimHarness.Account).Select(o => o.OrderRef).ToList();
                Assert.Equal(3, refs.Distinct(StringComparer.Ordinal).Count());
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 重启后未成交挂单作废且冻结额清零()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using (var first = await OpenCoreAsync(dbPath))
            {
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(Direction.Buy, 1, 6900));
                var before = first.QueryAccount(SimHarness.Account);
                Assert.True(before.FrozenMargin > 0, "挂单应冻结保证金");

                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                // 报单本身留着（历史可见），但状态作废、残量归零
                var order = Assert.Single(second.QueryOrders(SimHarness.Account));
                Assert.Equal((char)SimOrderStatus.NoTradeNotQueueing, (char)order.OrderStatusRaw);
                Assert.Equal(0, order.VolumeTotal);
                Assert.Contains("作废", order.StatusMsg);

                // 冻结额必须清零——否则重启后可用资金凭空少一块（挂单已不在，钱得有去处）
                var after = second.QueryAccount(SimHarness.Account);
                Assert.Equal(0, after.FrozenMargin, 2);
                Assert.Equal(0, after.FrozenCash, 2);
                Assert.Equal(after.Balance, after.Available, 2);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 重启后成交号续排不覆盖上次成交()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            string firstTradeId;
            using (var first = await OpenCoreAsync(dbPath))
            {
                var trades = new Collector<Trade>();
                first.TradeFilled += (a, t) => { if (a == SimHarness.Account) trades.Add(t); };

                first.InsertOrder(SimHarness.Account, SimHarness.Market(Direction.Buy, 1));
                firstTradeId = Assert.Single(trades.Snapshot).TradeId;

                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                var trades = new Collector<Trade>();
                second.TradeFilled += (a, t) => { if (a == SimHarness.Account) trades.Add(t); };

                second.InsertOrder(SimHarness.Account, SimHarness.Market(Direction.Buy, 1));
                var secondTradeId = Assert.Single(trades.Snapshot).TradeId;

                // 成交号从 1 重新开始会静默覆盖上次的成交行（sim_trade 主键含 trade_id）
                Assert.NotEqual(firstTradeId, secondTradeId);
                await second.FlushAsync();
            }

            // 两条成交都还在（没被覆盖）
            var ids = await SimStore.ForFile(dbPath).LoadTradeIdsAsync(SimHarness.Account);
            Assert.Equal(2, ids.Count);
            Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }
}
