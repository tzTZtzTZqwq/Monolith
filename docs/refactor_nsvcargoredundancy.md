# NSV Cargo 去冗余重构方案

## 状态与范围

本文档只描述**去冗余重构**方案，不含新功能。目标是在**不改变对外行为**的前提下，收敛 `Content.Server/_NSV/Cargo` 三个系统里重复的账户运算、价格运算、失败枚举、上下文结构与买卖骨架。

覆盖文件：

- `Content.Server/_NSV/Cargo/NsvCargoMarketSystem.cs`（571 行，冗余重灾区）
- `Content.Server/_NSV/Cargo/NsvCargoPurchaseSystem.cs`（632 行）
- `Content.Server/_NSV/Cargo/NsvCargoSellSystem.cs`（371 行）

不覆盖：客户端 GUI、原型 schema、市场平衡数值。

## 前置阻塞项：GUI 方向矛盾（必须先定，不在本次重构内解决）

两份设计文档对“买货用哪个 GUI”给出冲突方案，且都与现状不符：

| 来源 | 买货 GUI 主张 | 现状 |
| --- | --- | --- |
| `feature_nsvcargodesign.md` | 复用 `_NF/Market` BUI | **与现状一致**：`NsvCargoPurchaseSystem` 投影 `MarketData` 给 Market BUI |
| `remake_nsvcargoconsole.md` | 改用 Cargo 订单 BUI，并“删除重复 NSV 购买 GUI” | 客户端 `_NSV/Cargo/` 下**只有卖货窗口**，不存在待删的买货 GUI |

结论：

- remake 计划第 7 步“删除重复的 NSV 购买 GUI”指向一个不存在的对象。
- remake 计划实质是把买货从 Market BUI **换轨**到 Cargo 订单 BUI，这是重写，不是去冗余。

**本重构方案假定维持现状（Market BUI），不触碰 GUI 轨道。** 是否换轨是独立决策，需单独拍板；在此之前不改买货 UI 投影逻辑。

## 冗余清单与收敛方案

### R1 账户三方法收敛（收益最高，风险最低）

现状：`TryDebit` / `TryCredit` / `TryRefund`（Market 系统 211–323 行，约 110 行）共用同一骨架——`ValidAccount` → `ValidAmount` → checked 加减 → 边界校验 → `catch OverflowException`。差异仅三点：余额是加还是减、更新 `LifetimePurchases` 还是 `LifetimeSales`、refund 额外要求 `LifetimePurchases >= amount`。

方案：抽一个私有核心，用增量参数表达三种操作。

```csharp
private bool TryMutateAccount(
    NsvCargoHubComponent hub,
    int balanceDelta,       // 买 = -amount, 卖/退 = +amount
    int purchasesDelta,     // 买 = +amount, 退 = -amount, 卖 = 0
    int salesDelta,         // 卖 = +amount, 其余 = 0
    out NsvCargoFailure failure)
```

- `TryDebit(hub, amount)` → `TryMutateAccount(hub, -amount, +amount, 0)`
- `TryCredit(hub, amount)` → `TryMutateAccount(hub, +amount, 0, +amount)`
- `TryRefund(hub, amount)` → 先校验 `LifetimePurchases >= amount`（refund 专属前置，保留 `InvalidRefund` 语义），再 `TryMutateAccount(hub, +amount, -amount, 0)`
- 三个公有方法保留为薄 wrapper，签名不变——调用方零改动。

净减约 60 行。行为完全等价（同样的 checked、同样的 cap、同样的枚举返回）。

### R2 价格三方法收敛

现状：`TryGetBuyUnitPrice` / `TryGetSellAmount` / `TryCalculateTotal`（130–209 行）共用——输入 `IsFinite && > 0` → 运算 → `IsFinite && > 0 && <= Cap` → `Ceiling`/`Floor` → `checked (int)` → `catch`。差异仅在舍入方向和输入个数。

方案：抽一个 `TryFiniteToCappedInt(double raw, bool ceil, out int result)` 处理“有限性 + 正数 + cap + 舍入 + checked cast”的公共尾段；三个公有方法只保留各自的输入校验和 `raw` 计算，尾段统一调用。

- `TryCalculateTotal` 是纯 int 乘法，不涉及舍入，可只共用 cap/overflow 校验部分，或保持独立（收益小，视实现整洁度决定）。

净减约 30 行。注意保留 buy 用 `Ceiling`、sell 用 `Floor` 的既有取整方向，不能统一。

### R3 死字段 HubUid

现状：`NsvCargoMarketContext` 构造时 `gridUid` 同时传给 `GridUid` 和 `HubUid`（Market 系统 84–96 行），二者**永远相等**。`CargoHub` 组件挂在 grid 上（设计文档已确认“控制台通过 `Transform.GridUid` 找同 grid 的 CargoHub”），不存在独立的 hub 实体。

方案：删除 `NsvCargoMarketContext.HubUid` 与 `NsvCargoMarketFingerprint.HubUid`，所有 `context.HubUid` 引用改为 `context.GridUid`。

影响点（需全量替换）：`CommitOrder` 的 `delivery.HubUid = context.HubUid`、`OnDeliveryTerminating` 的 hub 解析。这些地方拿 HubUid 去 `TryComp<NsvCargoHubComponent>`——改成 GridUid 后语义不变（组件就在 grid 上）。

注意：`NsvCargoDeliveryComponent.HubUid` 是持久化到组件的字段，本项只删 context/fingerprint 里的冗余副本，delivery 组件字段是否一并改名为 GridUid 单独评估（涉及序列化，谨慎）。

### R4 失败枚举分级

现状：`NsvCargoFailure` 27 个成员，但玩家侧只有 2–3 个通用 popup（`nsv-cargo-purchase-rejected` / `nsv-cargo-sell-rejected`）。细粒度只在 `LogCargoAction` 的 admin log 里体现。

评估：这是**可接受的“为日志服务”的细分**，不是纯冗余——admin log 靠它区分拒绝原因。**不建议删减枚举**。仅做两件轻量整理：

- 确认每个枚举值确有 `LogCargoAction` 或分支实际产生它，删除从不被赋值的死值（需 grep 核对，例如确认 `OfferAmbiguous`、`InvalidEntity` 等都有产生路径）。
- 在枚举上加一行注释说明“用户只见通用 popup，细分仅供 admin log”，避免后人误以为要为每个值做本地化。

此项收益低、优先级最低，可并入 R1/R2 提交或单独略过。

### R5 买卖系统公共骨架

现状：`NsvCargoPurchaseSystem` 与 `NsvCargoSellSystem` 重复：

- `OnUiOpened`：`TryResolveContext` + `ValidateConsoleRequest` 双校验 → 失败发 disabled state，成功刷新。
- `OnPowerChanged`：断电即 `CloseUi`。
- 请求入口开头的 `ValidateConsoleRequest` → `TryResolveContext` 两步（Sell 已抽成 `ValidateRequest`，Purchase 内联重复了两次）。

方案（保守）：

- 在 `NsvCargoMarketSystem` 增一个 `TryValidateAndResolve(actor, console, out context, out failure)`，合并“先 `ValidateConsoleRequest` 再 `TryResolveContext`”的固定二连。Purchase 的 `OnCartMessage`/`OnPurchaseMessage` 和 Sell 的 `ValidateRequest` 都改调它。
- `OnPowerChanged` 逻辑一行，两处各自保留即可，不值得抽基类。

不建议引入共享抽象基类（`NsvCargoConsoleSystem<T>` 之类）——两系统的 UI key、消息类型、后续分支差异大，基类会引入泛型约束和虚方法开销，得不偿失。R5 只做“二连校验”的方法级收敛。

## 已在 working tree 的零散清理（保留，并入本次）

当前未提交改动（-10 行）已朝去冗余方向：

- 删 `OnEntityTerminating` / `OnFtlStarted` 的 `_carts.Count == 0` 早退（`RemoveCartsWhere` 内部已有同样的空检查，属重复守卫）。
- `NsvCargoSellSystem.RefreshState` 去掉未使用的 `actor` 参数。
- `SendDisabledState` 三行收一行。
- `MarketId` 类型 `string?` → `ProtoId<NsvCargoMarketPrototype>?`（连带 fingerprint），去掉 `.ToString()`。

这些与 R1–R5 不冲突，作为同一重构的一部分保留。

## 建议实施顺序

1. **R1 账户收敛** —— 独立、纯内部、零调用方改动，先做先验证。
2. **R2 价格收敛** —— 同上，注意保留 Ceiling/Floor 方向。
3. **R3 删 HubUid 死字段** —— 涉及跨文件替换，但语义等价。
4. **R5 二连校验收敛** —— 方法级，改动面中等。
5. **R4 枚举注释/死值清理** —— 收益最低，可选。

每步单独可编译、可提交。R1/R2 是主要收益（约减 90 行且消除最刺眼的复制粘贴）。

## 验证

- `dotnet build Content.Server` 每步 0 error。
- 跑现有 NSV cargo 集成/单元测试（若有）：`dotnet test Content.IntegrationTests --filter "FullyQualifiedName~NsvCargo"` 与 `Content.Tests` 中 cargo 相关。
- 重点回归：买货扣款/退款金额、卖货计价、余额边界（cap/overflow）、购物车隔离——这些正是 R1/R2 触碰的路径，行为必须逐位一致。
- 若无现成测试覆盖账户/价格运算，补最小单元测试锁定 `TryMutateAccount` 与价格尾段的等价性后再重构。

## 明确不做

- 不换买货 GUI 轨道（Market BUI ↔ Cargo 订单 BUI 是独立决策，见前置阻塞项）。
- 不删 `NsvCargoFailure` 有效枚举值（它们服务 admin log）。
- 不引入共享抽象基类。
- 不改原型 schema、市场数值、本地化文案。
- 不动 `NsvCargoDeliveryComponent` 的持久化字段（除非 R3 单独评估后决定）。
