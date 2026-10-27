# Aether.Exchange

CTP 本地仿真交易所——撮合核心（订单簿价格发现归簿 + 成交流驱动行情）+ 账户/持仓/风控 + SQLite 落盘 + RPC 线协议。开发与 E2E 的"真实柜台替身"：行情 = 簿顶 + 成交流（事件驱动），合约/账户自举。

## 组成

| 项目 | 说明 |
|---|---|
| `src/Aether.Exchange` | 撮合核心：账户/持仓/报单/订单簿（价格发现归簿）/风控（SimErrorCodes 90001-90011）/合约表种子（zx001）/行情引擎/仿真时钟。零网络依赖（与传输方式无关） |
| `src/Aether.Exchange.Rpc` | 线协议：4 字节小端长度前缀 + UTF-8 JSON 帧（SimFraming）+ 信封与错误码（SimWire，90012-90019）+ 服务端（SimRpcServer）+ 客户端（SimRpcClient） |
| `src/Aether.Exchange.Storage` | SQLite 落盘（SimStore + SqliteStoreBase/SqliteMigrations）——全部「尽力而为」，落盘失败绝不拒单 |

宿主在 aether-apps 仓：`../aether-apps/Aether.Exchange.Host`（默认 127.0.0.1:17800）。

## 配置（环境变量，全部在 host 进程读）

- `AETHER_CTP_SIM_ADDR`：监听端点（默认 127.0.0.1:17800）
- `AETHER_CTP_SIM_DB`：SQLite 路径（默认 ~/.aether/ctp-sim.db）
- `AETHER_CTP_SIM_BALANCE`：新账户初始资金水龙头（**默认 0 = 平台真相**——比特币模型；仅纯期货开发沙盒「不起链桥、测策略」时显式设置）
- `AETHER_SETTLEMENT_RAIL`：**外部结算轨道**（`ledger-aq` 虚拟币线 | `none` 不接）。不设时按旧手感推导：有 custody seed → `ledger-aq`，否则 `none`。配错值直接启动失败（不静默降级成「不结算」）
- `AETHER_LEDGER_CUSTODY_SEED` / `AETHER_LEDGER_NODE_ADDR`：`ledger-aq` 轨道的参数（设 seed 才接链；未配置 = 不影响交易链路）

> **换一条外部账本（另一种虚拟币）**：本仓加一个 `IExternalCashRail` 实现（或在 ⑧ 宿主里加适配器）+ 一个 `AETHER_SETTLEMENT_RAIL` 取值即可——
> 本仓核心与各引擎仓都不用改。端口定义见 `src/Aether.Exchange/Settlement/IExternalCashRail.cs`；
> 现有的 `ledger-aq` 适配器是 ⑧ 的 `Aether.Exchange.Host/LedgerCashRail`（唯一同时认识两侧的类）。

## 核心设计

- **价格发现归簿**：撮合权威在订单簿，行情由簿顶 + 成交流派生（做市/吃单 = 外部客户端策略）
- **资金模型（比特币模型）**：新账户初始资金 0，场内现金只能从链上 AQ 充值铸造进来——币是唯一稀缺物，首登白送现金会令币价失去意义（充提现对账由宿主侧结算轨道承担，核心只暴露 `CreditQuoteDeposit` / `WithdrawQuoteCash`）
- **事件在锁外触发**：回调可能重入核心（客户端在回调里立刻下单是合法用法），持锁触发会自死锁
- **前置从不直连撮合**：前置只做线协议转译（真实世界拓扑镜像：一个交易所 + N 券商前置）

## 构建与测试

```bash
dotnet test Aether.Exchange.slnx    # tests/Aether.Exchange.Tests：138 例（撮合/订单生命周期/会计不变量/费用/风控/认证/持久化/RPC wire）
```

测试**同仓自持**（只引 src 三项目，不依赖兄弟仓）——CI 无须 checkout 兄弟仓即可全量跑。
协议侧接缝（RemoteCtpSession/ICtpSession 契约）的测试在 ctp-net-front/tests（适配器属彼仓）。
落盘用例经宿主同款接线显式注入 `SimStore.ForFile`（核心默认 NullSimStore 不落盘——宿主语义见 Exchange.Host）。

## 来历

2026-10-19 拆自 Aether monorepo `business/CtpExchange/`（Acme.Ctp.Sim / Simulator / Sim.Server 三项目吸收合并）；2026-10-20 全仓改名 `Acme.Ctp.Sim → Aether.Exchange.Rpc`、撤单参数 `CtpOrderRef → CancelKey`。完整决策记录见工作区根 `MIGRATION.md`。
