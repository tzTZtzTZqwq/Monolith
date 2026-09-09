# NSV 货物交易系统架构设计

## 状态与范围

本文档是 `_NSV` 节点市场、货物购买与战利品出售的实现设计，服务于游戏循环中的补给、打捞和经济环节。目前仅为设计，文中新增类型尚未实现。

目标：

1. 不同战略节点提供不同商品；
2. 同一商品在不同节点可以有不同价格；
3. 同一节点可以按商品类别使用不同收购系数；
4. 交易状态跟随玩家舰船跨节点存活，不依赖临时 sector map；
5. 最大限度复用现有 Frontier Cargo、Market、Bank、Pricing 与 CrateMachine 能力；
6. 所有报价、余额变更、商品生成与货物删除均由服务器授权。

边界：

- 不修改 Salvage、GameTicker、通用 NPC 系统；
- 不要求修改通用 `CargoSystem` 或 `_NF/Market` 才能完成第一版；
- 新组件、系统和原型位于 `_NSV` 命名空间与资源目录；
- 第一版不实现动态库存、供需曲线、玩家间交易、赊账和异步运输队列。

## 核心决策

```text
市场内容定义
  NsvCargoMarketPrototype
          ▲
          │ ProtoId 引用
星图节点 NsvBluespaceStarmapNodeDefinition
          │
          │ StarmapId + NodeId 解析当前市场
          ▼
服务器 NsvCargoMarketSystem
     ├─ 买入：市场报价 → 舰船共享余额 → CrateMachine 出货
     └─ 卖出：卖货垫实体 → PricingSystem → 市场规则 → 舰船共享余额
          │
          ▼
玩家舰 grid 上的 NsvCargoHubComponent
```

### 数据归属

| 数据 | 归属 | 生命周期 |
| --- | --- | --- |
| 商品目录、基础市场系数、分类收购规则 | `NsvCargoMarketPrototype` | 静态原型，全服共享 |
| 节点使用哪个市场 | 星图节点的 `Market` 原型引用 | 静态原型 |
| 舰船余额、累计买入和卖出 | 玩家舰 grid 上的 `NsvCargoHubComponent` | 跟随舰船跨 FTL 存活 |
| 当前节点身份 | sector map 上已有的 `NsvBluespaceSectorInstanceComponent` | 随临时 sector 创建和回收 |
| 购物车 | `NsvCargoMarketSystem` 按 `(ConsoleUid, ActorUid)` 保存 | UI 会话状态，关闭 UI/离线/换节点时清除 |
| 动态库存 | 第一版不存在 | 后续独立设计 |

sector map 只提供 `StarmapId` 和 `NodeId`，不保存市场目录、订单、余额或成交记录。它可能在无人使用两分钟后销毁，因此不能作为经济状态持有者。

## 复用现有实现

### 购买侧

现有 `_NF/Market` 已具备可直接复用的客户端和数据结构：

| 能力 | 现有位置 | NSV 用法 |
| --- | --- | --- |
| 商品、数量、单价 DTO `MarketData` | `Content.Shared/_NF/Market/MarketData.cs` | 用于向现有市场 UI 投影节点报价 |
| 市场 BUI state | `Content.Shared/_NF/Market/BUI/MarketConsoleInterfaceState.cs` | 直接发送 NSV 服务器生成的目录和购物车状态 |
| 购物车消息 | `Content.Shared/_NF/Market/Events/MarketConsoleCartMessage.cs` | NSV system 在自己的 console 组件上处理 |
| 购买消息 | `Content.Shared/_NF/Market/Events/MarketPurchaseMessage.cs` | NSV system 在自己的 console 组件上处理 |
| 客户端市场窗口 | `Content.Client/_NF/Market/BUI/MarketConsoleBoundUserInterface.cs` | NSV 控制台的 `UserInterface` 直接配置此类型 |
| 货箱生成设备 | `CrateMachineComponent` + `MarketItemSpawnerComponent` | NSV 购买成功后使用现有设备投递货箱 |

NSV 控制台**不挂**现有服务端 `MarketConsoleComponent`：该组件和 `MarketSystem` 依赖 `_station.GetOwningStation()` 与 `CargoMarketDataComponent`，并把购物车保存在控制台组件上，不符合玩家舰归属和多用户购物车隔离要求。

同一个实体可以使用现有 `MarketConsoleBoundUserInterface`，但由 `NsvCargoMarketConsoleComponent` 和 `NsvCargoMarketSystem` 处理消息。客户端 BUI 不依赖服务端组件类型，也不硬编码特定实体 UID。

### 出售侧

复用：

- `CargoPalletComponent` 与 `CargoPalletSell` 原型作为卖货区域；
- `PricingSystem.GetPriceWithVendingDiscount(entity, grid)` 作为完整基础卖价；
- `EntityWhitelistSystem` 执行市场收购规则；
- `CargoSellBlacklistComponent` 执行绝对禁卖；
- 现有容器、mob 和锚定实体过滤语义。

不直接使用现有 `CargoPalletConsoleComponent` 的出售执行逻辑，因为它最终生成 Credit 现金堆，而本设计使用玩家舰共享账户；同时它只支持一个控制台级市场系数，不能表达武器、矿石和补给使用不同收购系数。

NSV 独立实现卖货控制台的收集、报价和提交，但不复制 `PricingSystem`。现有 `CargoSystem.GetPalletGoods()` 是私有方法，无法在不修改通用 Cargo 的前提下调用，因此附近卖货垫和实体收集需要在 `_NSV` system 内按相同规则实现。

## 市场原型

### 文件位置

```text
Content.Shared/_NSV/Cargo/NsvCargoMarketPrototype.cs
Resources/Prototypes/_NSV/Cargo/markets.yml
```

星图节点只增加一个可选引用：

```csharp
[DataField]
public ProtoId<NsvCargoMarketPrototype>? Market;
```

### 建议 Schema

```yaml
- type: nsvCargoMarket
  id: NSVHomeMarket
  name: nsv-cargo-market-home-name

  buy:
    defaultMultiplier: 1.0
    offers:
    - product: EmergencyRepairKitCargoProduct
      multiplier: 0.9
      maxPerTransaction: 5
    - product: ShipAmmoBundleCargoProduct
      multiplier: 1.0
      maxPerTransaction: 10

  sell:
    defaultMultiplier: null # 未匹配规则的物品拒收
    rules:
    - whitelist:
        tags:
        - Ore
      multiplier: 1.2
    - whitelist:
        components:
        - Gun
      multiplier: 0.8
```

星图引用：

```yaml
- type: nsvBluespaceStarmap
  id: NSVBluespaceStrategicMap
  nodes:
  - id: Home
    market: NSVHomeMarket
```

### 买入报价

每个 offer 引用现有 `CargoProductPrototype`。商品实体和共享基准买价分别来自：

```text
CargoProductPrototype.Product
CargoProductPrototype.Cost
```

实际买价：

```text
CargoProduct.Cost
× Market.Buy.DefaultMultiplier
× Offer.Multiplier
```

所有乘数默认 `1.0`。最终金额在服务器上使用 checked 运算计算并限制到可表示的正整数。

为了复用现有 `MarketConsoleCartMessage`，一个市场内不得有两个 offer 指向同一个 `CargoProduct.Product` 实体原型。客户端消息传的是实体 prototype ID，服务器据此反查唯一 offer，并重新计算价格。

`maxPerTransaction` 是第一版的可购买上限，不代表动态库存。每次打开市场都可再次购买；动态库存属于后续功能。

### 卖出规则

完整基础卖价不是单纯的 `StaticPrice`，而是：

```text
PricingSystem.GetPriceWithVendingDiscount(entity, cargoHubGrid)
```

该函数包括材料、溶液、stack、`StaticPrice`、容器内容和 vending discount。

实际卖价：

```text
完整基础卖价 × 第一个匹配的 SellRule.Multiplier
```

规则按 YAML 顺序匹配，第一条匹配即停止。`defaultMultiplier` 的语义：

- 正数：未匹配任何规则的商品按该系数收购；
- `null`：未匹配商品拒收；
- 市场没有 `sell`：该市场不提供出售服务。

绝对禁卖检查优先于市场规则：

1. 实体或任意容器子实体带 `CargoSellBlacklistComponent` → 整个根实体拒收；
2. 活着的 mob 拒收；
3. 锚定实体拒收；
4. 不满足可出售递归检查 → 拒收；
5. 按 sell rules 查找价格系数。

白名单和黑名单可以同时存在，必须定义优先级，不能以“二者互斥”简化。第一版的市场 sell rule 只需要 whitelist；绝对禁止统一通过 `CargoSellBlacklistComponent`。若未来需要市场级 blacklist，再增加以下顺序：

```text
absolute CargoSellBlacklist
→ market blacklist
→ market whitelist override
→ ordered whitelist rules
→ defaultMultiplier
```

### 无市场节点

节点没有 `Market` 引用时：

- 购买目录为空；
- 出售不可用；
- UI 显示该节点没有可用市场。

不允许回落到“任何地点都能卖出且凭空获得资金”的默认行为。

## 组件与挂载位置

### 新增组件

| 组件 | 挂载位置 | 职责 |
| --- | --- | --- |
| `NsvCargoHubComponent` | 指定玩家舰 grid | 舰船共享余额、累计买入、累计卖出；交易设备归属根 |
| `NsvCargoMarketConsoleComponent` | 买货控制台 | 标记由 NSV system 处理现有 Market BUI 消息；配置 CrateMachine 查找距离 |
| `NsvCargoSellConsoleComponent` | 卖货控制台 | 配置卖货垫查找距离；处理估价与出售 |

第一版不需要 `CargoHubMemberComponent`。控制台和出货机通过 `Transform.GridUid` 找到同一 grid 上的 `NsvCargoHubComponent`。

不要在舰船进入 sector 时自动添加 CargoHub。只有明确指定的玩家舰原型拥有它，NPC 舰、sector-owned grid、测试 grid 和其他玩家舰不会因为 FTL 自动获得市场账户。

### 复用组件

| 组件 | 挂载位置 | 用途 |
| --- | --- | --- |
| `CrateMachineComponent` | 买货出货机 | 生成购买货箱 |
| `MarketItemSpawnerComponent` | 同一出货机 | 暂存本次同步交付的 `MarketData` |
| `CargoPalletComponent` | `CargoPalletSell` 地板垫 | 标记卖货区域 |
| `CargoSellBlacklistComponent` | 明确不可出售的任务/系统实体 | 绝对禁止出售 |
| `ApcPowerReceiverComponent` | 两类控制台与出货机 | 供电约束 |
| `AccessReaderComponent` | 两类控制台 | 舰员权限约束 |
| `UserInterfaceComponent` | 两类控制台 | BUI 配置 |
| `NsvBluespaceSectorInstanceComponent` | sector map | 只用于读取 `StarmapId` / `NodeId` |

`IgnoreMarketModifierComponent` 不能用于防止出售；它只控制现有 Frontier 市场系数是否应用。

### 推荐实体结构

```text
玩家舰 grid
├─ ShuttleComponent
├─ NsvCargoHubComponent
│
├─ NSV 买货控制台
│  ├─ NsvCargoMarketConsoleComponent
│  ├─ UserInterface → MarketConsoleBoundUserInterface
│  ├─ ActivatableUI
│  ├─ AccessReader
│  └─ ApcPowerReceiver
│
├─ CrateMachine
│  ├─ CrateMachineComponent
│  └─ MarketItemSpawnerComponent
│
├─ NSV 卖货控制台
│  ├─ NsvCargoSellConsoleComponent
│  ├─ UserInterface
│  ├─ ActivatableUI
│  ├─ AccessReader
│  └─ ApcPowerReceiver
│
└─ CargoPalletSell
   └─ CargoPalletComponent
```

sector map：

```text
NsvBluespaceSectorInstanceComponent
├─ StarmapId
└─ NodeId
```

sector map 上不增加市场组件。

## 市场上下文解析

每次打开 UI、修改购物车、购买、估价和出售时，服务器重新解析：

```text
ConsoleUid
  → Transform.GridUid
  → grid 上必须有 NsvCargoHubComponent
  → Transform.MapUid
  → map 上必须有 NsvBluespaceSectorInstanceComponent
  → sector.ForeignGrids 必须包含该 grid
  → StarmapId + NodeId
  → starmap node
  → node.Market
  → NsvCargoMarketPrototype
```

`ForeignGrids` 检查确保只有登记进入该节点的外来玩家舰可使用舰载市场；sector-owned NPC 舰、敌舰和 station grid 不会因为处于同一 map 而获得交易资格。

第一版直接通过 `IPrototypeManager.TryIndex()` 和 starmap 的节点字典解析市场，不增加额外 snapshot cache。当前节点数很少，原型索引和节点查找均为常数时间；直接解析也天然反映原型热重载。只有实际性能分析证明需要时才增加缓存及失效逻辑。

## 购买流程

### UI 状态

NSV 服务端把节点 offer 投影为现有 `MarketData`：

```text
Prototype  = CargoProduct.Product
Price      = 服务端计算后的实际单价
Quantity   = maxPerTransaction - 当前用户购物车数量
```

为避免现有 UI 二次乘价：

```text
MarketConsoleInterfaceState.MarketModifier = 1.0
TransactionCost = 0（第一版）
```

价格、显示目录和余额都来自服务器 state。客户端显示价格不构成购买授权。

### 购物车

购物车按用户隔离：

```text
(ConsoleUid, ActorUid) → List<MarketData>
```

它只用于 UI，不预留库存。以下情况清除：

- UI 关闭；
- 玩家断线或实体删除；
- 控制台删除；
- 玩家舰离开当前节点；
- 市场原型发生变化。

不能使用现有 `MarketConsoleComponent.CartDataList`，否则同一控制台的多名用户会共享购物车。

### 提交购买

```text
收到 MarketPurchaseMessage
  1. BUI 框架确认 Actor 是该 UI 的订阅者
  2. 验证控制台可用、供电、锚定和 AccessReader
  3. 重新解析 CargoHub、当前节点和 MarketPrototype
  4. 逐项验证商品仍在目录、数量范围和实际服务器单价
  5. 查找同 grid 内最近且空闲的 CrateMachine
  6. checked 计算总价，确认 CargoHub.Balance 足够
  7. 从 CargoHub 扣款
  8. 写入 MarketItemSpawner.ItemsToSpawn 并启动 CrateMachine
  9. 成功后累计 LifetimePurchases 并清除该用户购物车
```

第一版没有 pending 订单。出货机不可用时不扣款。若扣款后启动 CrateMachine 意外失败，必须在同一请求处理中补偿退款并清空临时 spawn list。

后续只有明确需要延迟运输时才增加订单状态机：

```text
Pending → Claimed → Fulfilled
                  └→ Refunded
```

## 出售流程

### 估价

```text
收到 NsvCargoSellAppraiseMessage 或打开 UI
  1. 重新解析 CargoHub 与当前节点市场
  2. 在控制台配置半径内查找同 grid 的 CargoPalletSell
  3. 收集垫上 Dynamic / Sundries 实体
  4. 去重并执行递归可出售检查
  5. 为每个根实体匹配节点 sell rule
  6. PricingSystem 计算完整基础价
  7. 乘规则系数，向 UI 返回总额、实体数量和市场名
```

### 提交出售

```text
收到 NsvCargoSellRequestMessage
  1. 重复完整估价和过滤，不信任客户端金额
  2. 若没有有效实体则返回
  3. Raise EntitySoldEvent（保留现有市场/日志扩展兼容性）
  4. 递归收集根实体及容器内容
  5. 删除全部出售实体
  6. 向同 grid 的 CargoHub 增加实际收入
  7. 累计 LifetimeSales，刷新 UI
```

出售系统必须防止同一个实体被两个重叠卖货垫重复计价。使用一个 `HashSet<EntityUid>` 去重；服务器 ECS 事件顺序执行，第一笔出售删除实体后，后续请求重新估价将得到空集合。

收入进入 CargoHub，不生成 Credit 现金堆。未来若需要可抢夺的实体现金，可在 CargoHub 增加显式“取款”功能，而不是让购买和出售使用不同货币轨道。

## 服务器权威与安全

Robust BUI 已验证消息发送者是已订阅 Actor，并根据 UI 配置处理距离关闭；NSV system 仍必须验证交易领域状态。

每次请求必须重新检查：

- `args.Actor` 有效；
- 控制台仍可用、有电且锚定；
- `AccessReader` 允许 Actor 操作；
- 控制台和 CargoHub 位于同一 grid；
- grid 是当前 sector 的 `ForeignGrid`；
- 当前节点、市场和 offer 没有在 UI 打开后发生变化；
- 商品和数量合法；
- 乘法、加法使用 checked 或显式上限，防止整数溢出；
- 买入出货机可用；
- 卖出实体仍位于有效卖货垫上。

购买请求只传实体 prototype ID 和数量（通过现有 `MarketConsoleCartMessage`）；购买提交只发送现有空载 `MarketPurchaseMessage`。服务器不接收客户端价格、余额、节点 ID、CargoHub UID 或出货机 UID。

卖出请求不传金额或实体 UID 列表，只表达“重新估价”或“出售当前有效卖货垫上的内容”。

所有成功和拒绝交易记录 admin log：操作者、grid、`StarmapId + NodeId`、市场 ID、商品/实体数量、金额和失败原因类别。

## 原型校验

`ISerializationHooks.AfterDeserialization` 只负责不依赖其他 prototype manager 的局部校验：

- 市场 ID 与 offer 结构；
- 乘数有限且大于零；
- `maxPerTransaction` 大于零且有合理上限；
- 同一市场内 offer 引用不重复；
- sell rule 顺序和本地字段合法。

跨原型校验在所有原型加载完成后的 `Content.Tests` 中执行：

- 节点引用的 `NsvCargoMarketPrototype` 存在；
- buy offer 引用的 `CargoProductPrototype` 存在；
- `CargoProduct.Product` 实体原型存在；
- 同一市场内不存在两个 offer 指向同一实体 product；
- whitelist 中引用的 component、tag 和 prototype 合法。

不能在 `AfterDeserialization` 中通过 `IPrototypeManager` 校验其他原型是否存在。

## 防套利与战利品边界

不要给整个 sector 生成树统一添加 `CargoSellBlacklistComponent`。这样既无法可靠覆盖所有子实体和后续生成物，也会把“拆敌舰火炮作为战利品”一并禁止。

第一版规则：

- AI 核心、encounter objective、控制器和系统实体显式挂 `CargoSellBlacklistComponent`；
- 市场 `sell.rules` 默认拒收，只放行设计上允许成为战利品的类别；
- 火炮、弹药、矿石等通过 tag/component/prototype whitelist 放行；
- 墙、地板和普通舰船机器若未匹配任何规则则无法出售；
- 容器内存在禁卖实体时，整个根容器拒收，不能用箱子绕过；
- 无限重复刷新的 sector 战利品是否构成可接受的经济循环，由 encounter 成本、战斗损耗和后续 loot budget 控制，而不是用 blanket blacklist 解决。

如果后续需要严格区分“玩家原装火炮”和“合法敌舰战利品”，新增显式 `NsvCargoLoot` tag 或 marker，由 encounter/loot 生成流程添加，市场只收带该标记的物品。

## 首批市场建议

| 节点 | 市场 | 买入 | 卖出 |
| --- | --- | --- | --- |
| Home | `NSVHomeMarket` | 修理材料、基础弹药、常规补给；基准价 | 收矿物与合法战利品；保守系数 |
| PiratePatrol | `NSVPirateMarket` | 违禁装备、廉价武器；商品级折扣或溢价 | 高价收武器和违禁品，不收普通机器 |
| Distress | `NSVDistressMarket` | 医疗、救援和应急维修品；急用溢价 | 收医疗物资和救援相关货物 |
| Asteroid | 可无市场或矿业市场 | 采矿消耗品 | 高价收矿石，低价或拒收其他物品 |
| UnknownSignal | 无市场 | 不可买 | 不可卖 |

首版无需一次配置全部节点。建议先实现 Home 与 PiratePatrol，用同一商品在两个节点的不同报价验证架构。

## 测试策略

### Content.Tests

- 市场乘数非有限值、零值和负值被拒绝；
- `maxPerTransaction` 边界；
- 重复 offer 与重复 product entity 被拒绝；
- 无市场节点合法且买卖均不可用；
- 节点、市场、CargoProduct 和 product entity 跨原型引用有效；
- sell rule 第一匹配优先级稳定。

### Content.IntegrationTests

购买：

- Home 与 PiratePatrol 对同一商品返回不同服务器价格；
- 目录外商品和伪造 prototype ID 被拒；
- 超量、零量、负量和整数溢出输入被拒；
- CargoHub 余额不足不出货；
- CrateMachine 缺失、占用、断电时不扣款；
- 成功购买只扣一次款并生成一次货箱；
- 两名玩家使用同一控制台时购物车完全隔离；
- 打开 UI 后 FTL 到另一节点，旧购物车不能按旧价购买；
- NPC/sector-owned grid 上的控制台不能使用玩家节点市场。

出售：

- 同一实体在不同节点使用不同 sell rule 得到不同价格；
- 未匹配规则的实体保留且不计价；
- `CargoSellBlacklistComponent` 根实体和容器子实体均阻止出售；
- 合法火炮/矿石按规则出售并进入 CargoHub 余额；
- 重叠卖货垫不重复计价；
- 客户端伪造金额无效；
- 成功后根实体和容器内容全部删除；
- 无市场节点不可出售。

生命周期：

- shuttle 进入、节点间移动和返航后 CargoHub 余额保持；
- sector 销毁和重建不会保存购物车或改变 CargoHub；
- UI 关闭、Actor 删除、控制台删除和节点切换清除购物车；
- 测试结束时 shuttle 返航并 `TryDispose` sector，避免集成测试池污染。

## 实现阶段

### 阶段一：静态市场与解析

1. 新增 `NsvCargoMarketPrototype`、buy offer 和 sell rule 数据定义；
2. 星图节点增加可选 `Market` 引用；
3. 配置 Home 与 PiratePatrol 市场；
4. 完成局部和跨原型测试；
5. 实现无缓存的服务器市场上下文解析。

### 阶段二：舰船账户与购买

1. 在指定玩家舰原型上挂 `NsvCargoHubComponent`；
2. 新增 NSV 买货控制台组件和实体原型；
3. 复用 `_NF/Market` BUI、消息和 DTO；
4. 复用 CrateMachine 完成同步出货；
5. 完成余额、用户购物车和服务器校验测试。

### 阶段三：出售

1. 新增 NSV 卖货控制台组件、BUI state 和两种请求消息；
2. 复用 CargoPallet、PricingSystem 和 EntityWhitelist；
3. 收入进入 CargoHub；
4. 增加战利品边界和防套利测试。

### 阶段四：可见性与平衡

1. 星图节点详情显示市场名称、可买商品数和收购摘要；
2. PDA/舰船控制台显示 CargoHub 余额；
3. 游戏内验证多人购物车、节点换价、出货和卖货；
4. 根据实际战斗损耗调整商品价格和 sell rules。

## 显式不在首版范围

- 动态市场库存与补货；
- 成交量驱动的价格变化；
- pending 订单、运输时间和跨节点交货；
- 玩家之间直接交易；
- 赊账、贷款和利息；
- FTL 燃料实际扣除；
- Bounty 系统接入；
- sector 回收后仍持久的节点经济状态。

## 相关文档

- 战略节点、FTL 和 sector 生命周期：`docs/nsv-bluespace-sectors.md`；
- encounter 与撤离门禁：`docs/encountersystem.md`；
- 实现状态和里程碑：`docs/NSV_multiplayer_spaceship_game_loop_roadmap.md`。
