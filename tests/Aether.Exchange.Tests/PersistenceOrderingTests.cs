using Aether.Exchange;

namespace Aether.Exchange.Tests;

/// <summary>
/// 落盘顺序钉子（open-issues #24，2026-10-30 probe1 实测）。
/// <para>
/// 现象：交易所内存里早已终结的报单，<c>sim_order</c> 表里还写着「已报入 / 成交 0」。
/// 实测样本 ref <c>000000000002</c>——成交了（<c>sim_trade</c> 有 99.37×1、持仓 1 手），
/// 报单行从 15:29 起一直没变过；清场时交易所对 43 笔"僵尸单"全回
/// <c>90009 当前状态「已撤销」不可撤</c>。**内存对、库是旧的**。
/// </para>
/// <para>
/// 危害不在平时：交易所重启时 <c>SimOrderManager.Restore</c> 拿库当真相源，
/// 写着 <c>'3'</c> 的已终结单会**复活成活单**。
/// </para>
/// <para>
/// 根因：旧 <c>Persist</c> 是 <c>Task.Run</c> 火并忘，同一行的「插入」与「撤单/成交更新」
/// 是两个并发任务，谁先落盘不定；<c>ON CONFLICT … DO UPDATE</c> 于是把新状态盖回旧快照。
/// 这里用一个"第一次写必然慢"的存储把那个竞态放大成确定性：串行链下顺序恒定，
/// 并发下第一条写会落到最后。
/// </para>
/// </summary>
public sealed class PersistenceOrderingTests
{
    /// <summary>记录每次 <c>SaveOrderAsync</c> 的 (orderRef, status)，并让**第一次**写慢 80ms。</summary>
    private sealed class SlowFirstStore : ISimStore
    {
        // 注意带上 accountId：maker 的报单号也是 000000000001（各账户独立编号），只按 Ref 过滤会把庄家的写混进来。
        private readonly List<(string Account, string Ref, string Status)> _writes = new();
        private int _first = 1;

        public IReadOnlyList<(string Account, string Ref, string Status)> Writes
        {
            get { lock (_writes) return _writes.ToArray(); }
        }

        public async Task SaveOrderAsync(string accountId, Order order, CancellationToken ct = default)
        {
            // 只拖慢**被测账户的第一笔**落盘（maker 的种子挂单也走这里，别让它抢走这次延迟）：
            // 旧实现下这笔慢写会排到后面的快写之后，记录顺序被打乱。
            if (accountId == SimHarness.Account && Interlocked.Exchange(ref _first, 0) == 1)
                await Task.Delay(80, ct);
            lock (_writes) _writes.Add((accountId, order.OrderRef, ((char)order.OrderStatusRaw).ToString()));
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SimAccountSnapshot>> LoadAccountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SimAccountSnapshot>>(Array.Empty<SimAccountSnapshot>());
        public Task<IReadOnlyList<SimPositionSnapshot>> LoadPositionsAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SimPositionSnapshot>>(Array.Empty<SimPositionSnapshot>());
        public Task<IReadOnlyList<Order>> LoadOrdersAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyList<SimInstrument>> LoadInstrumentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SimInstrument>>(Array.Empty<SimInstrument>());
        public Task<IReadOnlyList<string>> LoadTradeIdsAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task SaveAccountAsync(SimAccountSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task SavePositionsAsync(string accountId, IReadOnlyList<SimPositionSnapshot> positions, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveTradeAsync(string accountId, Trade trade, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveInstrumentAsync(SimInstrument instrument, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<QuoteMintedSnapshot>> LoadQuoteMintedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<QuoteMintedSnapshot>>(Array.Empty<QuoteMintedSnapshot>());
        public Task SaveQuoteMintedAsync(string accountId, long mintedAtoms, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveTickAsync(Tick tick, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Tick>> LoadRecentTicksAsync(string instrumentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Tick>>(Array.Empty<Tick>());
    }

    [Fact]
    public async Task 同一报单的多次落盘必须按内存变更顺序应用()
    {
        var store = new SlowFirstStore();
        var core = new SimTradingCore(SimHarness.Options(), new SimClock(() => SimHarness.TradingNow), store: store);
        await core.InitializeAsync();
        SimHarness.FundSandbox(core);
        SimHarness.SeedMaker(core);
        SimHarness.Login(core);

        // 买价挂在 maker 的买价上（= Bid，不与卖侧交叉）⇒ 只入簿、不成交，随后撤掉。
        var placed = core.InsertOrder(SimHarness.Account, SimHarness.Limit(Direction.Buy, 2, SimHarness.Bid));
        var order = Assert.Single(core.QueryOrders(SimHarness.Account), o => o.OrderRef == placed.ClientOrderRef);
        core.CancelOrder(SimHarness.Account,
            new CancelKey(SimHarness.Broker, SimHarness.Account, order.FrontId, order.SessionId, order.OrderRef, order.ExchangeId));

        await core.FlushAsync();

        var mine = store.Writes.Where(w => w.Account == SimHarness.Account && w.Ref == order.OrderRef).ToList();
        Assert.True(mine.Count >= 2, $"该报单应至少落盘两次（插入 + 撤单），实际 {mine.Count} 次：{string.Join(",", mine)}");
        Assert.Equal("3", mine[0].Status);                 // 先插入（未成交在队）
        Assert.Equal("5", mine[^1].Status);                // 最后一次必须是撤单——被旧值盖回去就是这里挂
        // 第一次之后不许再出现旧状态 '3'（那正是"新值被旧快照覆盖"的形态）
        // 一旦离开 '3'（终结），后面绝不许再写出 '3'——那正是"旧快照盖掉新值"的形态。
        // （第一次终结之前的重复 '3' 是无害的幂等写，不算违规。）
        var firstTerminal = mine.ToList().FindIndex(w => w.Status != "3");
        Assert.True(firstTerminal > 0, $"必须先是 '3' 再终结——全序列：{string.Join(",", mine)}");
        for (var i = firstTerminal; i < mine.Count; i++)
            Assert.True(mine[i].Status != "3", $"第 {i + 1} 次落盘又回到了旧状态 '3'——全序列：{string.Join(",", mine)}");
    }
}
