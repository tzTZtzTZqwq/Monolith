# NSV EncounterSystem 架构

## 目的

`EncounterSystem` 负责临时蓝空间 sector 中一次具体事件的生命周期：激活对象、生成事件专属内容、建立本次关系、追踪目标、开放撤离并产生一次性的结果。

它与 `SectorSystem` 的边界是：

```text
Sector    = 临时 map、基础模块、布局、通用内容和回收
Encounter = 本次事件的对象、关系、目标、参与者和结果
```

基础 sector 可以包含中继站、环境 GUST、陨石场和跳跃点；Encounter 决定哪些对象在本次合同中生效、谁是指定目标、哪些 faction 关系应启用，以及任务何时完成。

## 非目标

EncounterSystem 不应：

- 接管 `GameTicker`、Salvage 或通用 NPC faction/targeting；
- 重写现有船只操纵、HTN、火控、炮台或 FTL 实现；
- 把所有事件内容硬编码进 `NsvBluespaceSectorSystem.TryApplyModule()`；
- 在 M3 实现货币、掉落分配、货舱提取或合同商店。这些属于 M4 的 Extraction、Loot 和 Reward。

## 所有权与系统边界

| 系统 | 责任 |
| --- | --- |
| `NsvBluespaceSectorSystem` | 创建/缓存/销毁 map，生成基础模块，登记 sector-owned 内容；战略节点按 `StarmapId + NodeId` 隔离实例 |
| `NsvBluespaceEncounterSystem` | 创建、激活、推进、完成和清理 encounter |
| `NsvBluespaceFactionSystem` | 通过 `SetFaction`、`SetSectorRelation` 等现有 API 应用或恢复事件关系 |
| `NsvBluespaceSectorTravelSystem` | 为 foreign shuttle 执行普通入图、相邻节点 FTL 或外部返航；对两种离开方式查询 EncounterSystem 授权 |
| `NsvBluespaceNavigationConsoleSystem` | 投影服务器权威的星图/encounter 快照，并处理节点选择或显式返航请求；不拥有合同逻辑 |
| `NsvBluespaceNavigationCartridgeSystem` | 向 NSV 导航 PDA 程序推送只读 sector/encounter 快照；无任何跳跃控制 |
| 现有 HTN / ship systems | 根据 NSV faction resolver 进行索敌、操纵、瞄准和开火 |

Sector map 删除仍是所有内容的最终清理机制。EncounterSystem 只额外记录自己生成或激活的对象，以支持完成、失败或取消时的早期清理与结果判定。

## 当前实现的数据模型

### Encounter 原型

当前已注册一个最小 `nsvBluespaceEncounter` 原型：

```yaml
- type: nsvBluespaceEncounter
  id: NSVPatrolContract
  targetFaction: NSVHostile
```

M3 首版只用它声明合同身份和目标 grid faction；尚未实现 YAML 多态 Objective、专属 spawn、奖励、失败策略或任意 ECS 事件声明。

战略星图节点通过 `encounterPool` 引用 encounter 原型。`NsvBluespaceSectorSystem.TryGetOrCreateNode()` 以节点稳定 `seed` 对 pool 做确定性选择，并将结果保存到 `NsvBluespaceSectorInstanceComponent.EncounterDefinitionId`；同一节点回收后重建仍选择同一个 definition。空 pool 会保存空 definition，首次 shuttle 抵达时不会创建 controller。当前只有 `NSVPatrolContract` 已有 arrival activator；未来 pool 成员必须提供相应的激活系统。legacy template sector 不使用节点 pool，仍维持现有 PatrolContract 默认路径。

### Encounter 控制器

首次 foreign shuttle **实际完成 FTL 并抵达** Ready sector 时，`NsvBluespacePatrolContractSystem` 读取该 sector 的 `EncounterDefinitionId`。战略节点只有在该值为 `NSVPatrolContract` 时才会创建服务器权威 controller；空 pool 节点不创建合同。创建时 controller entity 写入 `NsvBluespaceEncounterComponent`：

```text
SectorMap
DefinitionId
State
ObjectiveTarget
Participants
PendingReturns
ReturnedParticipants
RelationOverrides
```

controller UID 是当前运行时 encounter 的关联 ID。`NsvBluespaceSectorInstanceComponent.EncounterController` 只索引该 sector 当前的共享 controller；后续 shuttle 抵达时加入同一个 controller，不会创建第二个合同。controller 不作为 map Transform 子节点存在，sector 与 controller 通过 UID 双向关联。

当前状态机为：

```text
Pending → Active → ObjectiveComplete → ExtractionOpen
Pending / Active → Failed → Disposed
ExtractionOpen → Disposed
任何状态 → Disposed
```

PatrolContract 在同一次受 guard 的状态转换中经过 `ObjectiveComplete` 并立即进入 `ExtractionOpen`。目前没有 `Settled`、奖励或持久化结果；`State == Active` 是销毁事件的幂等 guard。

### Encounter 成员标记

当前运行时成员使用：

```text
NsvBluespaceEncounterMemberComponent
  Controller
  Role = Participant | ObjectiveTarget | Spawn | Loot
```

M3 首版为参与 shuttle 写入 `Participant`，为指定 patrol core 写入 `ObjectiveTarget`。目标 core 另有专用 marker：

```text
NsvEncounterPatrolCoreObjectiveComponent
  Controller
  BlipGrid
```

`BlipGrid` 记录指定 GUST 的 root grid。激活时 `NsvBluespacePatrolContractSystem` 在该 grid 上安装 `RadarBlipComponent`（红色 Star，Box2 ±4.5），使所有雷达控制台都能把合同目标显示为特殊图案；完成时移除该组件，map 回收或 grid 删除也会经组件 shutdown 自动广播移除。blip 复用 Mono 的 `RadarBlipSystem`，不修改其查询逻辑。

`NsvBluespacePatrolContractSystem` 用该 marker 筛选 `EntityTerminatingEvent`；系统按组件类型统一订阅，而不是为每艘 GUST 动态注册独立订阅。

## 后续 encounter 生成能力

当前 PatrolContract 只选择 sector 已在初始化时生成的 hostile GUST，不会在 Ready map 中再生成内容。后续 encounter 应能在 sector 已 `Ready` 后生成埋伏 GUST、合同旗舰、残骸、黑匣子、事件跳跃点或局部陨石场。

该后续 adapter 应复用 NSV 的 station、ship、entity 和 asteroid generator，以及布局/安全距离规则；不复制一套独立的 map loader 或 ship generator：

```text
Encounter definition
  → 安全 placement 规划
  → 复用现有 generator definition
  → 记录 sector ownership + encounter ownership
  → 写入 controller UID / member role
```

它必须适用于 map 已初始化后的生成；不要直接调用假设 `DoMapInitialize()` 前运行的 `SectorSystem.TryApplyModule()`。placement 应避开 sector 入口安全区、既有基础 grid、其他 active encounter 内容和 foreign player shuttle 的当前坐标；内容 seed 应由 sector seed 与运行时 encounter identity 稳定派生。

## Faction 与激活

Encounter 不直接修改 faction component 字段，而是调用 `NsvBluespaceFactionSystem` 的现有 API。

激活时可以：

1. 指定目标并为其写入指向 controller 的 member / objective marker；
2. 将参与 shuttle 登记为 encounter participant；
3. 用 sector-local relation override 调整本次战斗关系；
4. 激活或生成本次事件的船只；
5. 在失败、处置或将来完成结算时清除仅属于本 encounter 的 relation override。

当前 `PatrolContract` 实现步骤 1–3：它复用 sector 已生成的 GUST，不在 Ready map 中额外生成船只。

`NSVHostile` 与 `NSVFederal` 的默认关系仍彼此 hostile，但当前测试 sector 只生成一艘 `NSVHostile` GUST，作为唯一的 PatrolContract 指定目标，因此没有环境 Federal GUST 干扰。`PatrolContract` 仍会显式写入并记录 `NSVFederal → NSVHostile = Neutral` 与 `NSVHostile → NSVFederal = Neutral` 两条 sector-local override；controller 失败或 sector 回收时只清除这两条由合同安装的 override。`ExtractionOpen` 仍保留 override，直到后续结算或 map 回收。`NSVHostile ↔ NSVPlayer` 不受影响，participant 仍是目标的合法敌对方。

## Objective

首个已实现 Objective 是 `PatrolContract` 的“摧毁指定 GUST”。它以指定 `NsvBluespacePatrolCore` 在 controller 为 `Active` 期间终止为完成条件：

```text
Encounter Active
  → 指定一艘 GUST 的 patrol core
  → core 被销毁
  → ObjectiveComplete
  → ExtractionOpen
```

### 绑定 GUST patrol core

GUST 的 root grid 不是 AI 实体。其 `NsvBluespacePatrolCoreSpawner` 在地图初始化时生成 `NsvBluespacePatrolCore`；该 core 带有 `NsvBluespaceShipAiCoreComponent`、`HTNComponent`、`NsvShipTargetComponent`，并从 Mono 的 `NpcStationAiRammer` 继承 `ApcPowerReceiverComponent`。

Encounter 激活时先选定一艘 GUST root grid，再按 `TransformComponent.GridUid` 查找该 grid 上唯一的 `NsvBluespaceShipAiCoreComponent`。找到的 core UID 同时写入 controller 的 `ObjectiveTarget`，并挂运行时 marker，例如：

```text
NsvEncounterPatrolCoreObjectiveComponent
  Controller = <encounter controller UID>
```

一个 core 只能属于一个 active encounter objective；若已有 marker，不能被第二个 encounter 再次指定。

### 事件订阅与幂等完成

`NsvBluespacePatrolContractSystem` 在 `Initialize()` 时按 marker 组件类型统一订阅；它不为每一艘船分别注册订阅。只有带 objective marker 的 core 触发事件时，handler 才会收到：

```csharp
SubscribeLocalEvent<NsvEncounterPatrolCoreObjectiveComponent, EntityTerminatingEvent>(
    OnPatrolCoreTerminating);
```

handler 必须依次验证：

1. marker 指向的 controller 仍存在；
2. controller 的 `State == Active`；
3. 终止的 UID 等于 controller 保存的 `ObjectiveTarget`；
4. 所属 sector 的 `State == Ready`。

仅满足这些条件时才调用唯一的状态转换入口：

```text
Active → ObjectiveComplete → ExtractionOpen
```

状态 guard，而不是事件本身，保证重复终止通知、事件生成物 cleanup 和后续 map 删除都不会重复完成或结算合同。

`EntityTerminatingEvent` 只说明 core 被删除，不提供可靠的击杀归因。M3 的最小规则可以接受“指定 core 在 Active 期间被摧毁”作为完成；若将来要求必须由 participant 击毁，则需在伤害流程中额外记录最后有效攻击方，再在 core 终止时验证归属。

### 断电型目标（后续）

“关闭敌方 AI core”可复用同一个 marker 并订阅 `PowerChangedEvent`：

```csharp
SubscribeLocalEvent<NsvEncounterPatrolCoreObjectiveComponent, PowerChangedEvent>(
    OnPatrolCorePowerChanged);
```

当 `args.Powered == false` 时，表示该 core 的 `ApcPowerReceiverComponent` 断电。该事件是**设备级**而非整艘 grid 级：监听 core 代表 AI 失电，监听 RTG 或反应堆代表发电设备失效，二者不是同一 Objective。激活时还必须直接读取一次当前 `Powered` 状态，因为已断电的设备不会产生新的状态变化事件。

断电通常不应立即成为永久完成。建议进入 `DisabledPending` 并开始短暂确认计时；确认期内恢复供电则取消，持续断电至期限后才 `ObjectiveComplete`。Mono 的 GUST 移动与火控 operator 会检查 AI core 的供电，故 core 断电能够作为“使 AI 失能”的有效条件，但不代表整艘舰的所有设备都已失电。

### 清理顺序

sector map 回收也会使 core 收到 `EntityTerminatingEvent`。当前 `NsvBluespaceSectorSystem.TryDispose()` 在删除 map 前将 sector 设为 `Draining`，并调用 `EncounterSystem.PreDisposeSector()`；它将关联 controller 先转为 `Failed` / `Disposed`、清理合同 relation override 并解除 sector 索引。之后终止事件到达时 controller 已不再是 `Active`，不会误判为任务完成。

必须区分：

- 销毁指定 core：完成；
- 销毁同 faction 的其他 GUST：不完成；
- 指定 core 在 `Active` 期间被任意来源终止：完成；M3 不追踪击杀归因，Federal ↔ Hostile neutral override 只用于排除环境 GUST 的自动干扰；
- map 回收或目标消失：失败或 disposed，不能当作成功。

后续 Objective 可复用同一接口：护送/存活、扫描、拾取并撤离、倒计时防守、拖航与救援。

## 撤离与结算

当前 travel 系统在 foreign shuttle 离开 sector 前都会向 EncounterSystem 查询授权；它适用于显式返回原出发 map 和相邻战略节点 FTL：

```text
普通 / debug map → 任意存在的战略节点
战略节点 → 仅图连接的邻居，且先检查 encounter 授权
战略节点 → 原出发 map，且先检查 encounter 授权

Pending / Active
  → 已登记参与者的两类离开均被拒绝
ExtractionOpen
  → 已登记参与者可前往相邻节点或返回出发地
Failed
  → 已登记参与者可离开，避免被困
没有 controller
  → 允许离开
```

图连接检查发生在目的节点创建前，不能通过请求非相邻节点分配 map。相邻节点 FTL 保留最初外部出发地的坐标和 faction snapshot；只有抵达目标节点的 `FTLCompletedEvent` 才提交其 foreign-grid ownership。无 controller 的空 encounter-pool 节点不会阻挡移动。

`PendingReturns` 在 participant 从有 controller 的 sector 开始获准离开时记录；`ReturnedParticipants` 只在 `FTLCompletedEvent` 确认该 participant 抵达非 sector 的外部 map 后更新。节点间移动不写入已返航结果，从而保留“返回最初出发地”这一独立结算语义。`Disposed` controller 不再提供授权，sector 删除也不会尝试让其中的 shuttle 正常返回。

M3 当前固定的结果规则是：指定 core 在 `Active` 期间终止时，仅一次地写入全局 Objective result 并进入 `ExtractionOpen`；每艘已登记 participant 独立获得离开授权。没有 `Settled` 状态、奖励、掉落归属或跨 round 结果。首艘返航、全员返航、超时、断线和舰船被毁如何影响未来结算，仍留给 M4 的 Extraction、Loot 和 Reward 设计。

## NPC 休眠约束

Mono NPC 系统会在约 4000 格内没有存活玩家时移除 AI core 的 `ActiveNPCComponent` 并清空 HTN plan。故当前无人 GUST 可以作为 encounter target，但不会在没有活玩家的情况下持续模拟自主战斗。

Encounter 设计必须明确选择其一：

- 保留该优化：事件只在玩家接近时推进战斗；
- 对激活的 encounter core 设定专用唤醒策略，使背景舰战能持续模拟。

该选择独立于 `NsvShipTarget` 和 faction resolver。

## 已实现的首个里程碑：PatrolContract

当前最小交付流程：

```text
首次参与者实际进入包含 NSVPatrolContract 的 Ready sector
  → 创建一个共享 PatrolContract controller
  → 从 sector 已生成的 hostile GUST 中选择指定 patrol core
  → 给目标和参与 shuttle 写 controller member marker
  → 在指定 GUST root grid 上安装红色 Star 雷达 blip
  → 应用本次 Federal ↔ Hostile relation override
  → 监听指定 patrol core 销毁
  → 仅一次地标记 ObjectiveComplete 并进入 ExtractionOpen
  → 移除目标雷达 blip，并向参与者 PDA 的 NSV 导航程序发送完成通知
  → participant 可前往相邻节点或 FTL 安全返回原出发地
  → 仅外部返航完成后记录 ReturnedParticipants
```

后续 shuttle 抵达同一 sector 时只加入该 controller，不会创建第二个合同。当前不在 Ready map 中额外刷船或刷场景；post-Ready spawn adapter、掉落、奖励、合同商店、复杂任务 UI 与击杀归因均不在此范围。导航控制台已显示当前 `PatrolContract` 的名称、Objective、状态、参与者和撤离资格。

世界内反馈已实现两条：

1. **目标雷达标记：** 激活时在指定 GUST root grid 上安装 `RadarBlipComponent`（红色 Star），任何带 `RadarConsoleComponent` 的控制台都会把目标显示为特殊图案；Objective 完成或 map 回收时移除。
2. **NSV 导航 PDA 程序：** `NsvBluespaceNavigationCartridge` 是一个标准 CartridgeLoader 程序，显示当前 sector 状态、encounter 名称/Objective/状态/参与者/撤离资格，不含任何跳跃控制。`NsvBluespaceNavigationCartridgeSystem` 订阅与控制台相同的 display-change 事件并推送只读快照。Objective 完成时，系统向所有处于参与者 grid 上的该程序 PDA 调用 `CartridgeLoaderSystem.SendNotification`，由 PDA 自带的 ringtone/弹窗提醒玩家（PDA 通知需处于开启状态）。程序状态推送与完成后的 `CanExtract` 变化由 `NsvBluespaceNavigationCartridgeTest` 覆盖；客户端 fragment 的两个实现坑（`Setup` 重复调用导致 fragment 游离、GridContainer 值标签不能用 `ClipText`）的修复记录见 `docs/nsv-bluespace-sectors.md` 的 PDA 程序小节。

仍未实现：接受/取消合同的交互、奖励弹窗与合同商店 UI。

## 验证

当前集成测试已覆盖：

1. 首艘抵达创建 controller，后续 shuttle 加入同一 controller；
2. participant 与唯一 hostile patrol core 的 marker 都指向该 controller；
3. 指定 core 终止只将 controller 推进一次到 `ExtractionOpen`；
4. 完成前返航被拒绝，完成后 participant 可返航，并只在 FTL 完成后记录 `ReturnedParticipants`；
5. active contract 的 Federal ↔ Hostile relation override 为 `Neutral`；
6. sector map 回收会先 dispose controller，不产生成功结果，并清除合同 override；
7. 原有 sector FTL faction restore 流程会在 Objective 完成后继续恢复入场前的 faction；
8. 战略节点 pool 会稳定选择合同 definition；空 pool 节点不会创建 controller；
9. active contract 同时阻止相邻节点移动与原出发地返航，完成后两种离开方式都恢复可用。

```powershell
# encounter lifecycle、目标销毁、撤离/节点门禁、map 回收、faction restore 与战略节点旅行
dotnet test .\Content.IntegrationTests\Content.IntegrationTests.csproj -c Debug --no-restore `
  --filter "FullyQualifiedName~NsvBluespaceEncounterSystemTest|FullyQualifiedName~NsvBluespaceSectorSystemTest|FullyQualifiedName~NsvBluespaceStarmapSystemTest|FullyQualifiedName~NsvShipTargetQueryTest" -v:q
```

后续应补充：participant 断线或舰船被毁、目标在激活前提前消失、post-Ready encounter spawn 的 placement/ownership，以及背景 NPC wake 策略。
