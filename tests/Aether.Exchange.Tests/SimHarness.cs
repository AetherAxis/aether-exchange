using System.Collections.Concurrent;
using Aether.Exchange;
using Aether.Exchange.Rpc;
using Aether.Exchange.Storage;

namespace Aether.Exchange.Tests;

/// <summary>
/// 单测夹具：确定性时钟 + 纯内存存储 + maker 账户双边挂单供给流动性。
/// <para>
/// 三条确定性来源：①<see cref="SimOptions.DatabasePath"/> = null（不落盘）；②固定时钟指向交易时段内的一个周三；
/// ③价格全部来自显式挂单（价格发现归簿——测试放进簿里的单子决定一切成交价）。
/// </para>
/// <para>
/// 域类型与 CTP 协议类型解耦：工厂方法返回 <see cref="OrderRequest"/>（域）而非 `Ctp.Net.CtpOrderRequest`，
/// 撤单键用 <see cref="CancelKey"/> 而非协议侧报单引用——撮合核心自持领域类型，协议映射由 Front 层负责。
/// </para>
/// </summary>
internal static class SimHarness
{
    /// <summary>2026-09-23 是周三，10:00 落在日盘 [09:00,10:15) 内 ⇒ <c>IsTradingTime</c> 为 true。</summary>
    public static readonly DateTime TradingNow = new(2026, 9, 23, 10, 0, 0);

    public const string Instrument = "ag2610";
    public const string Account = "tester";
    public const string Broker = "9999";

    /// <summary>ag2610 的交易所（品种种子表里 ag 属上期所）——撤单定位键的回显项。</summary>
    public const string Exchange = "SHFE";

    /// <summary>maker 双边挂单价（买 <see cref="Bid"/> / 卖 <see cref="Ask"/>）——市价单按对手侧成交的确定性来源。</summary>
    public const double Bid = 6999;
    public const double Ask = 7001;
    public const double Last = 7000;

    /// <summary>ag2610 的合约元数据（<c>InstrumentCatalog</c> 种子）：乘数 15 / 保证金率 12% / 变动价位 1。</summary>
    public const double VolumeMultiple = 15;
    public const double MarginRate = 0.12;
    public const double CommissionRate = 0.00005;

    /// <summary>
    /// 沙盒入金量：测试侧**显式充值**（<see cref="Fund"/>）的默认数额。
    /// 平台没有「开户赠金」——新账户余额恒 0，场内现金只能由 <c>CreditQuoteDeposit</c> 进来，
    /// 这里就是那笔「链上 AQ 真实转账」在测试里的替身。
    /// </summary>
    public const double SandboxDeposit = 1_000_000;

    /// <summary>测试登录密码（密码必填——2026-09-28 起 `Login` 无密码一律拒 90017）。</summary>
    public const string TestPassword = "test-pw";

    /// <summary>maker 账户（「庄家」的替身：双边挂限价单供给流动性——价格发现归簿下流动性唯一来源是挂单）。</summary>
    public const string MakerAccount = "maker";

    /// <summary>maker 双边挂单的每侧手数（30×2 手 × 单手保证金 ≈ 75.6 万 < 初始 100 万——双边同时冻结须留足余量）。</summary>
    public const int MakerVolume = 30;

    /// <summary>默认参数：不落盘 + ag2610 上架（上市闸门恒生效——测试合约必须在上市表内）。
    /// 资金不在这里给：<see cref="NewCoreAsync"/> 用 <see cref="Fund"/> 显式入金。</summary>
    public static SimOptions Options() => new() { DatabasePath = null, ListedInstruments = new[] { Instrument } };

    /// <summary>每个 core 上已入过金的账户（<see cref="Fund"/> 幂等：重复调用是空操作）。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimTradingCore, HashSet<string>> Funded = new();

    /// <summary>
    /// 入金（<c>CreditQuoteDeposit</c> = 平台唯一入金通道，1 AQ = 1 现金）：测试用它替身「链上 AQ 真实转账」。
    /// 同一 core 上同一账户只入一次——要追加资金直接调 <c>CreditQuoteDeposit</c>（那是另一笔转账）。
    /// </summary>
    public static void Fund(SimTradingCore core, string accountId, double tokens = SandboxDeposit)
    {
        var funded = Funded.GetOrCreateValue(core);
        if (!funded.Add(accountId)) return;
        core.CreditQuoteDeposit(accountId, (ulong)(tokens * SimTradingCore.QuoteAtomsPerToken));
    }

    /// <summary>
    /// 本套件里作为参与者下单的沙盒账户（每个 = 一笔显式充值；<see cref="SeedMaker"/> 与
    /// <see cref="Account"/> 另行入金）。刻意**不含**：<c>legacy</c>（旧库价值由 store 快照注入）、
    /// <c>nobody</c>（未开户断言余额 0）。
    /// </summary>
    public static readonly string[] SandboxAccounts =
        ["alice", "bob", "carol", "dave", "erin", "noise", "po", "rest", "t", "hit", "x", "self"];

    /// <summary>给沙盒参与者全体入金（等价「每个参与者各转一笔 AQ 进来」）。</summary>
    public static void FundSandbox(SimTradingCore core, double tokens = SandboxDeposit)
    {
        Fund(core, Account, tokens);
        foreach (var account in SandboxAccounts) Fund(core, account, tokens);
    }

    /// <param name="fundSandbox">给 <see cref="SandboxAccounts"/> 全体入金（认证类用例传 false——
    /// 它们要钉的正是「首登开户 = 0 余额」）。</param>
    public static async Task<SimTradingCore> NewCoreAsync(
        SimOptions? options = null,
        bool seedMaker = true,
        DateTime? now = null,
        bool fundSandbox = true)
    {
        var core = new SimTradingCore(options ?? Options(), new SimClock(() => now ?? TradingNow));
        await core.InitializeAsync();
        // 开户余额恒 0（平台没有赠金）⇒ 需要下单的账户先显式入金。
        if (fundSandbox)
        {
            FundSandbox(core);
            Fund(core, MakerAccount);   // maker 是流动性来源（seedMaker=false 的用例也会单侧引用它）
        }
        if (seedMaker) SeedMaker(core);
        return core;
    }

    /// <summary>maker 账户双边挂单（买 <see cref="Bid"/> × volumePerSide / 卖 <see cref="Ask"/> × volumePerSide）——「盘口注入」的簿版替身。</summary>
    public static void SeedMaker(SimTradingCore core, int volumePerSide = MakerVolume)
    {
        Fund(core, MakerAccount);   // 双边挂单要冻结保证金（开户余额恒 0 ⇒ 先入金，等价「maker 自己的 AQ 转账」）
        core.InsertOrder(MakerAccount, Limit(Direction.Buy, volumePerSide, Bid));
        core.InsertOrder(MakerAccount, Limit(Direction.Sell, volumePerSide, Ask));
    }

    public static ExchangeLoginResult Login(SimTradingCore core) => core.Login(Broker, Account, TestPassword);

    /// <summary>已登录核心（绝大多数用例的起点；seedMaker 决定是否注入双边流动性）。</summary>
    public static async Task<SimTradingCore> NewLoggedInCoreAsync(SimOptions? options = null, bool seedMaker = true, DateTime? now = null, bool fundSandbox = true)
    {
        var core = await NewCoreAsync(options, seedMaker, now, fundSandbox);
        Login(core);
        return core;
    }

    // ── 域类型工厂方法（交易所核心解耦 CTP 协议类型后改用）──

    public static OrderRequest Limit(
        Direction direction,
        int volume,
        double price,
        OffsetFlag offset = OffsetFlag.Open,
        TimeCondition timeCondition = TimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = OrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static OrderRequest Market(
        Direction direction,
        int volume,
        OffsetFlag offset = OffsetFlag.Open,
        TimeCondition timeCondition = TimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = OrderPriceType.Market,
            LimitPrice = 0,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static CancelKey RefOf(Order order)
        => new(Broker, Account, order.FrontId, order.SessionId, order.OrderRef, order.ExchangeId);
}

/// <summary>线程安全的回报收集器——断言时必须先取快照。</summary>
internal sealed class Collector<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_gate) _items.Add(item);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public IReadOnlyList<T> Snapshot
    {
        get { lock (_gate) return _items.ToList(); }
    }
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }

    public static async Task<int> QuiesceAsync(Func<int> count, int quietMs = 250)
    {
        await Task.Delay(quietMs);
        return count();
    }
}

/// <summary>
/// 回环仿真：测试进程内起「host 侧」listener——跨线契约测试，无需外部进程。
/// Dispose = 停 listener + core.Dispose（同步 flush——重启续用测试的落盘点）。
/// <para>（2026-10-23 分仓：原版还带 <c>RemoteCtpSession</c>（ICtpSession 适配器）——
/// 那是 Front 仓的客户端半边，Session 级契约测试已随之迁 ctp-net-front/tests；
/// 本仓只留 交易所核心 + RPC server 的裸线形态。）</para>
/// </summary>
internal sealed class LoopbackSim : IDisposable
{
    public SimTradingCore Core { get; }
    public SimRpcServer Server { get; }

    private LoopbackSim(SimTradingCore core, SimRpcServer server)
    {
        Core = core;
        Server = server;
    }

    public static LoopbackSim Start(Func<SimOptions, SimOptions>? configure = null, bool seedMaker = true)
    {
        var options = configure is null ? SimHarness.Options() : configure(SimHarness.Options());
        // 宿主接线语义：DatabasePath 非空 ⇒ 注入 SimStore（核心默认 NullSimStore 不落盘）
        var store = string.IsNullOrWhiteSpace(options.DatabasePath) ? null : SimStore.ForFile(options.DatabasePath);
        var core = new SimTradingCore(options, store: store);
        core.InitializeAsync().GetAwaiter().GetResult();   // xunit 无同步上下文 ⇒ sync-over-async 安全
        SimHarness.FundSandbox(core);                       // 开户余额恒 0 ⇒ 显式入金（链上充值替身）
        if (seedMaker)
        {
            SimHarness.Fund(core, SimHarness.MakerAccount);
            SimHarness.SeedMaker(core);
        }
        var server = new SimRpcServer(core, "127.0.0.1", 0);
        server.Start();
        return new LoopbackSim(core, server);
    }

    public void Dispose()
    {
        try { Server.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
    }
}
