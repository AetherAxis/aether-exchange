# Aether.Exchange 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

CTP 本地仿真交易所，开发与 E2E 的「真实柜台替身」。宿主与组合根在 `../aether-apps/Aether.Exchange.Host`（默认 `127.0.0.1:17800`）。

- 不引 CTP SDK / `Ctp.Net`：Domain 类型自持，协议映射归 `ctp-net-front`。
- 不引 `Aether.Ledger`：链只进宿主，提币经注入的 `SimWithdrawHandler`。
- 不引兄弟仓：测试同仓自持，CI 无须 checkout。

## 2. 项目组成

| 项目 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `src/Aether.Exchange` | 撮合/账户/持仓/风控/行情/时钟/合约种子 | `SimTradingCore`、`OrderBook`、`SimAccount`、`SimPositionManager`、`SimRiskEngine` | 无（仅 BCL） |
| `src/Aether.Exchange.Rpc` | 长度前缀 + JSON 帧；路由与请求应答 | `SimFraming`、`SimWire`、`SimRpcServer`、`SimRpcClient` | `Aether.Exchange` |
| `src/Aether.Exchange.Storage` | SQLite 8 表落盘、幂等迁移 | `SimStore`、`SqliteStoreBase`、`SqliteMigrations` | 核心 + `Microsoft.Data.Sqlite` |
| `tests/Aether.Exchange.Tests` | 138 例：撮合/生命周期/会计/费用/风控/认证/持久化 | `SimHarness` | 上述三项目 |

## 3. 关键设计决策

### 3.1 核心零依赖，不认识 CTP 协议类型

- **决策**：Domain DTO 与 byte 枚举自带；错误码自定义 90001–90019。
- **理由**：协议映射归前置；官方码表离线不可核实，假造会误导排查。
- **代价**：char↔enum 对齐与跨类码表维护。

### 3.2 价格发现归簿，行情 = 簿顶 + 成交流

- **决策**：`OrderBook` 唯一价格源；walk/回放已删；市价无对手拒 90016；tick 由成交与簿变更触发。
- **理由**：价格须来自真实挂单，否则仿真价与成交价两张皮。
- **代价**：空闲无 tick（订阅先 `Activate`）；流动性全靠外部挂单。

### 3.3 单锁串行 + 事件锁外触发 + 应答先于推送

- **决策**：变更全在 `_gate` 串行、事件锁外；本 op 推送先捕获、应答后下发；成交先于终态。
- **理由**：回调可重入，持锁会自死锁；客户端应答后才注册 CancelKey。
- **代价**：单锁不扩横向；顺序只保本连接。

### 3.4 落盘尽力而为，失败绝不拒单

- **决策**：`ISimStore` fire-and-forget，异常只 `Trace`；核心默认 `NullSimStore`。
- **理由**：诊断工具不该因磁盘故障拒单；默认不落盘让单测零磁盘。
- **代价**：崩溃丢最后几笔；重启簿空、非终态单作废。

### 3.5 资金只能从链上充值铸造，初始 0

- **决策**：初始资金 0；AQ 8 位原子；现金与 `sim_quote_minted` 分账；提现闸门=静态可用。
- **理由**：送现金会让币价失去意义；台账独立，重启对账不重复入账。
- **代价**：沙盒须显式设 1e6；退役字段仅剩 schema 兼容。

### 3.6 账户自助开户 + 双密码域

- **决策**：首登开户、密码必填（空则 90017）；PBKDF2；明文域/前置密文域各一列；5 次失败锁 60s。
- **理由**：前置与钱包页两扇门，单列互斥曾断在最后一焊。
- **代价**：失败计数内存态；无 token/租约。

## 4. 运行与验证

- `dotnet test Aether.Exchange.slnx`；CI 用 `-c Release` 构建后 `--no-build` 跑。
- 配置由宿主读环境变量：`AETHER_CTP_SIM_ADDR`/`_DB`（默认 `~/.aether/ctp-sim.db`，与真实 `ctp.db` 分文件）/`_BALANCE`/`_BOARD`。
- 测试确定性：`DatabasePath = null`、固定时钟（2026-09-23 周三 10:00）、价格全部来自显式挂单；落盘用例注入 `SimStore.ForFile`。
- CI：ubuntu + .NET 10.0.x，注入 GitHub Packages 凭据。
- 计数：118 个 `[Fact]` + 2 个 `[Theory]`（20 条 `InlineData`）= 138。

## 5. 升级记录（2026-10-24）

`Directory.Packages.props` 的 `git diff` 只有版本号变化，csproj 无内联版本。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.0 | 10.0.13 | assets 解析 10.0.13，连带 `SQLitePCLRaw.bundle_e_sqlite3/2.1.13` |
| Microsoft.NET.Test.Sdk | 17.14.1 | 18.11.0 | 主版本跨越；assets 解析 18.11.0 |
| xunit.runner.visualstudio | 3.1.4 | 4.0.1 | 主版本跨越；assets 解析 4.0.1 |
| coverlet.collector | 6.0.4 | 10.1.0 | 跨多个主版本；CI 尚未 `--collect` |
| xunit | 2.9.3 | 2.9.3（未动） | 避免双跳版 |

验证边界：未执行 `dotnet build/test`；`bin/Release` 与 assets 时间戳 02:02 可证还原与构建完成；138 例全过以重跑/CI 为准。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | Rpc | 注释称只引 `Ctp.Net`，实际无包引用且与服务端同程序集 | 拆出 wire 项目 | 多一项目，宿主引用需改 |
| 2 | P1 | 核心 + Rpc | 无账户租约/踢人；重复报单号只能拒单；wire 测试记录双客户端撞号 | 登录租约；报单号收敛到账号级 | 行为变更，需并发用例 |
| 3 | P1 | 账本 | 金额全 `double`，`Round(2)` 只在边界；`atoms / 1e8` 丢原子精度 | 内部改 `decimal` 或 long 原子数 | 类型面广，需双读写迁移 |
| 4 | P2 | CI | coverlet 已升 10.1.0，但 Test 步无 `--collect` | 加覆盖率收集，先只报告 | 构建变慢 |
| 5 | P2 | 重启语义 | `InitializeAsync` 作废非终态报单；`OrderBook` 不变量④「重启簿为空」 | 落盘簿重建，或 README 写明「重启 = 清场」 | 簿/冻结一致性易破 |
| 6 | P2 | 推送 | 出站无界 Channel；每股成交都发 tick 并落盘，无背压 | 有界通道 + 慢客户端合并/丢帧 | 客户端需快照兜底 |
| 7 | P3 | 协议/存储 | `WithdrawArg` 仍收 `asset="coin"`；`sim_spot_minted` 等无消费者 | 标 deprecated 并迁移下线 | 老库兼容 |
| 8 | P3 | 风控 | 注释「未做自成交检查/频率/持仓限额」；自成交是跳过而非拒绝 | 补 STP 与单笔上限 | 默认开启影响做市 bot |

## 7. 本轮升级落地（2026-10-24）

- **CS8604 真修**：`SimTradingCore.ActivateMarket` 对 `instrumentId ?? ""` 归一后再用于 `Contains/Resolve/Activate/OnBookChanged`（清构建才暴露的空引用告警）。
- **xUnit 分析器告警清零**：`Assert.Single(x.Where(...))` → `Assert.Single(x, pred)`、`Assert.Empty(x.Where(...))` → `Assert.DoesNotContain(x, pred)`。
- **CI 覆盖率留档**：Test 步加 `--collect:"XPlat Code Coverage"` + `upload-artifact`（撮合/会计不变量是回归重灾区，需要有基线）。

**未做（有意）**：§6 的撮合重启语义文档化（要先把「重启后订单簿/挂单如何恢复」定成产品口径，不宜由升级顺手定义）。
