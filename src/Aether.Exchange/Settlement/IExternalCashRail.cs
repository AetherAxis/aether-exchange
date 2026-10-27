namespace Aether.Exchange;

/// <summary>
/// 外部结算轨道：场内现金与某个外部账本（公链/另一条虚拟币/任意外部清算源）之间的入金与出金。
/// <para><b>为什么有这个端口</b>：交易所核心不认识任何具体币种——它只有「计价币现金」（<see cref="SimTradingCore.CreditQuoteDeposit"/>
/// 铸、<see cref="SimTradingCore.WithdrawQuoteCash"/> 扣）。具体某条链怎么连、地址什么格式、怎么归账、
/// 有没有确认数，全部收敛到本接口的实现里；换一条轨道 = 换一个实现 + 一个配置值，<b>本仓核心与
/// 各引擎仓都不用改</b>。</para>
/// <para><b>两个方向的责任划分</b>：</para>
/// <list type="bullet">
/// <item><b>入金</b>：轨道自己侦听/轮询外部账本，发现到账后调 <see cref="SimTradingCore.CreditQuoteDeposit"/>——
/// 归账模型（memo / 独立地址 / 链上解析）是轨道私事，交易所不感知。</item>
/// <item><b>出金</b>：交易所把「谁、去哪、多少」交给 <see cref="WithdrawAsync"/>；<b>扣减场内现金必须在签名之前</b>
/// （扣不掉就不签——venue 不欠这个账户那么多可提现金）。</item>
/// </list>
/// <para><b>生命周期</b>：<see cref="StartAsync"/> 失败 = 宿主启动失败（接了轨道就是硬依赖，不允许
/// 「带病启动、静默不结算」）。</para>
/// </summary>
public interface IExternalCashRail : IAsyncDisposable
{
    /// <summary>轨道标识（配置值，如 <c>ledger-aq</c>）——日志与配置校验用。</summary>
    string RailId { get; }

    /// <summary>外部资产符号（如 <c>AQ</c>）——日志/对账口径用。</summary>
    string AssetSymbol { get; }

    /// <summary>连上外部账本 + 首轮对账 + 开始巡检。失败抛出（宿主启动失败）。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 提现出金：扣减场内现金 → 外部账本签名出金，返回外部交易号/哈希。
    /// <para>实现必须保证「现金扣不动就不签名」；余额不足抛 <see cref="ExchangeBusinessException"/>。</para>
    /// </summary>
    /// <param name="accountId">场内账户键</param>
    /// <param name="destination">外部账本地址（格式由轨道自己解释）</param>
    /// <param name="amountAtoms">金额（现金原子，1e8 = 1 现金）</param>
    /// <param name="asset">资产标识（wire 原样传入——合法取值由轨道定义并校验）</param>
    Task<string> WithdrawAsync(string accountId, string destination, ulong amountAtoms, string asset,
        CancellationToken ct = default);

    /// <summary>同步对账一轮（运维/测试用——确定性驱动，不等内部定时器）。</summary>
    void ReconcileNow();
}
