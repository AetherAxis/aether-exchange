# 交易所侧 tick 的产生语义（2026-10-29 查证）

> 状态：**现状记录 + 一条建议**（未改代码）。归属：`src/Aether.Exchange/Market/SimMarketDataEngine.cs`。
> 下游影响：`../../ctp-net-front/doc/md-tick-delivery.md`（前置逐字节去重的必要性）、`../../ctp-net-core/doc/md-tick-dedup.md`（客户端「同值去重」的误判来源）。

## 1. 什么时候发

tick **事件驱动**，两个入口：

| 触发 | 代码 | 说明 |
|---|---|---|
| 成交 | `RecordTrade`（`:87`）→ `EmitTick`（`:99`） | 更新最新价/最高/最低/开市价/累计量后**立即**发一帧（含成交后的簿顶） |
| 簿变更 | `OnBookChanged`（`:106`）→ `EmitTick`（`:109`） | 新挂单/撤单/成交离簿后发一帧；**未激活**（没人订阅过、没人碰过）的合约不发 |

`EmitTick` → `RaiseTick`（`:143`）组出 `Tick`，然后 `TickGenerated?.Invoke`（`:167`）——交易所本身**不去重、不节流**，来一次变更就发一帧。

## 2. 发的内容

`RaiseTick` 填的字段（`:143-166`）：交易日/动作日、`UpdateTime`（`_clock.TickTime`，秒）、`UpdateMillisec`（`_clock.Milliseconds`）、最新价（按最小变动价位取整）、累计成交量、买一卖一价与**聚合量**、持仓量、昨结算/昨收/开/高/低。

因此**「可见内容」= 最新价 + 累计成交量 + 持仓量 + 买一价量 + 卖一价量 + 秒级时间**。任何不改变这 6 项的操作（第 2 档以下的撤改、同价位的换手到别的队列……）都会产出一帧**除时间外完全相同**的 tick。

这正是下游两个现象的来源：

- 前置必须做逐字节去重，否则带宽和客户端处理量被「无效快照」放大（实测窗口内前置吞掉 35 帧，见 `../../ctp-net-front/doc/md-tick-delivery.md` §5）；
- 客户端若按「6 项内容相同」判重，就会把**真实发生过的事件**丢掉（实测 11/382；该层已于 2026-10-29 移除，见 `../../ctp-net-core/doc/md-tick-dedup.md` §2）。

## 3. 建议：把「去重」挪到这一层（可选，非紧急）

现在去重分散在下游两层，各有各的判据。更干净的位置是**源头**：只有「上报字段真的变了」才发。

```
OnBookChanged(inst):
    top = 簿顶;  snap = (last, volume, oi, bid, bidvol, ask, askvol, 秒)
    if snap == lastEmitted[inst]: suppressedTicks++; return
    lastEmitted[inst] = snap
    RaiseTick(...)
```

要点：

1. **与真实交易所一致**：真实 CTP 的深度快照同样只在可上报内容变化时才推；不推「无变化快照」不是失真，是更接近真实。
2. **必须带上秒**：`UpdateTime` 是上报字段之一，同一内容跨秒应视为新快照（下游也是这么理解的）。
3. **只做一个全局判据**，下游那两层就可以退化成兜底（前置）或直接删掉（客户端）。
4. **加一个计数器**（`suppressedTicks`）并定期打进交易所日志：它是「簿变更 / 有效快照」的比例，做市商阶梯密度调参时的有用指标。
5. **不要顺手改毫秒**：`Tick.UpdateMillisec` 有值，但前置帧模板 `BuildMdTick` 没有毫秒槽（`updateTime` 只有 8 字节 HH:MM:SS），所以毫秒目前到不了客户端。要毫秒得先扩模板，属于另一件事。

风险提示：这条改动会让「每簿变更必有一帧」的现有假设失效——如果 `SimTradingCore` 或 Lean 侧有依赖「一单一 tick」的算法（例如按 tick 数计时），要先查一遍再动。当前两个 bot（`market-maker` / `botnoise`）用的是 `_clock` 定时，不依赖 tick 条数。

## 4. 相关

- 前置转发与逐字节去重：`../../ctp-net-front/doc/md-tick-delivery.md`
- 客户端同值去重的误判：`../../ctp-net-core/doc/md-tick-dedup.md`
- 直连旁路（谁在下单不影响 tick 生产）：`../../aether-ctp-hosts/doc/direct-bot-bypass.md`
