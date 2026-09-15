# NSV 动态 Sector 休眠与状态保持

## 范围与结论

本文分析 NSV 动态 bluespace sector 在同一次服务器运行期间实现状态连续性的方案。目标是让有玩家的 sector 保持完整实时模拟，无人 sector 停止高成本更新，同时保留舰船、残骸、库存、损伤和地图修改。

本文是设计分析，相关休眠系统尚未实现。

推荐将功能分为两个层级：

1. **首版：保留地图和实体，使用引擎原生 map pause**
   - 实现成本和风险可控；
   - 保留现有 `EntityUid`、组件运行时状态和实体引用；
   - 可以停止大部分 AI、物理、碰撞和普通实体更新；
   - 满足同一次服务器运行期间的状态连续性。
2. **后续：序列化、删除地图并重新加载**
   - 可释放地图占用的内存；
   - 需要解决运行时字段、跨地图引用、power/node network、station、docking、任务和 timer 的恢复；
   - 不适合作为首版。

当前 NSV sector 在空置两分钟后会直接销毁，而不是休眠：

- 空置检测位于 `Content.Server/_NSV/Bluespace/Sectors/NsvBluespaceSectorTravelSystem.cs`；
- 最终通过 `NsvBluespaceSectorSystem.TryDispose()` 调用 `DeleteMap()`；
- 再次访问同一模板或节点时会从 prototype 和 seed 重新生成，因此之前的破坏、移动和拾取状态不会保留。

首版最自然的改动是将“空置后销毁”替换成“空置后暂停”，保留真正销毁作为管理员操作、回合结束清理或内存回收机制。

## 当前 NSV sector 生命周期

`NsvBluespaceSectorInstanceComponent` 当前状态包括：

```text
Requested
Planning
Applying
Ready
Draining
Disposed
Failed
```

创建流程：

```text
规划布局
  → 创建未初始化、暂停的 map
  → 按 module 加载 grid、实体和小行星
  → DoMapInitialize
  → SetPaused(false)
  → Ready
```

实例组件当前记录：

```text
TemplateId / StarmapId / NodeId / Seed
MapId
OwnedGrids / OwnedEntities
ForeignGrids / PendingArrivals
ReturnDestinations
ForeignGridFactionSnapshots
RelationOverrides
EncounterController
```

离开与销毁流程：

```text
最后一个 ForeignGrid 离开
  → PendingArrivals 为空
  → 等待 2 分钟
  → Ready → Draining
  → encounter PreDispose
  → DeleteMap
```

现有空置判断实际上统计的是外来 shuttle grid，而不是地图上的玩家。它会导致：

- 无人的废弃 shuttle 持续阻止销毁；
- 未登记为 `ForeignGrid` 的玩家或实体无法阻止销毁；
- 无法区分“必须保留”和“必须持续模拟”。

## 引擎 map pause 能力

RobustToolbox 已提供地图级暂停：

```csharp
_map.SetPaused(mapId, true);
_map.SetPaused(mapId, false);
```

`SharedMapSystem.SetPaused()` 会递归设置 map entity 及其所有 transform 子实体的 paused 状态。

普通 `EntityQuery` 和 `EntityQueryEnumerator` 默认排除 paused entity，因此大多数以下更新会停止：

- NSV 舰船 AI；
- steering 与 targeting；
- 普通物理求解和碰撞；
- 普通移动控制；
- 使用实体查询的 timer/despawn；
- 大部分 power consumer、supplier 和 battery 更新；
- 其他按实体查询运行的实时系统。

暂停不会删除或重建实体，因此会原样保留：

- `EntityUid` 和 transform parent；
- grid、舰船、空间站和残骸；
- tile 与建筑修改；
- 容器、库存和装备；
- 弹药、武器状态和冷却组件；
- 护盾、损伤和供电组件状态；
- faction；
- encounter/objective 的现有 UID 引用；
- 已删除实体的删除事实。

但 map pause 不是绝对的全局 scheduler freeze：

- `AllEntityQuery` 不自动排除 paused entity；
- `Timer.Spawn` 等全局 timer 不属于特定 map；
- `PhysicsComponent.IgnorePaused` 可以绕过普通暂停行为；
- nullspace 中的 encounter controller 不属于 sector map；
- atmos、network 或其他系统可能在组件之外维护缓存和队列。

因此，首版仍需审计特殊查询和全局回调，并通过 profiler 验证休眠后的实际 CPU 降幅。

## 推荐状态机

保留 `Ready` 作为内部的 `ACTIVE`，避免重命名影响现有 travel、encounter 和 UI 代码。建议增加：

```text
Ready             完整实时模拟，对应 ACTIVE
PreparingSleep    阻止新写入并二次检查休眠条件
Sleeping          map 已暂停
Waking            停止战略结算并恢复实时状态
Draining          最终销毁
```

状态转换：

```text
Applying → Ready
Ready → PreparingSleep → Sleeping
Sleeping → Waking → Ready
Ready/Sleeping → Draining → Disposed
```

`PreparingSleep` 和 `Waking` 用于防止快速进出、FTL 到达和后台结算造成重复切换。

## 休眠协调系统

建议新增独立的 `NsvBluespaceSectorSleepSystem`，不要将全部逻辑继续放入 travel system。

其职责包括：

- 维护 active/sleeping sector 注册表；
- 统计地图上的连接玩家；
- 检查 pending arrival、FTL 和 sleep blocker；
- 执行进入休眠和恢复；
- 管理战略层与实时层的结算所有权；
- 提供管理员强制休眠、唤醒和最终销毁接口；
- 记录状态切换原因、时间和 generation。

暂停后普通实体查询不会再枚举到 sector map 本身，因此协调系统不能只使用：

```csharp
EntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>()
```

应使用 `NsvBluespaceSectorSystem` 已有的 active sector 字典，或由 sector 创建/销毁事件维护独立的 map UID 注册表。唤醒必须通过已知 map UID 直接访问 Sleeping sector。

## 休眠条件

建议条件为：

```text
sector.State == Ready
AND 没有连接玩家的 AttachedEntity 位于该 map
AND PendingArrivals.Count == 0
AND 没有正在进入或离开的 FTL transition
AND 不存在有效的 sleep blocker
AND 空置时间达到 SleepDelay
```

玩家统计应使用 `IPlayerManager.Sessions` 与 `AttachedEntity`，再检查实体的 `Transform.MapUid`。`ForeignGrids` 仍用于 sector travel、faction 和 encounter bookkeeping，但不应单独代表玩家数量。

### 保留与阻止休眠分离

应区分：

```text
NsvSectorPersistent
    对象必须保留，但不一定要求地图继续实时模拟

NsvSectorSleepBlocker
    对象或任务存在时，地图不能进入休眠
```

建议语义：

| 对象 | 必须保留 | 默认阻止休眠 |
| --- | ---: | ---: |
| 断线玩家身体 | 是 | 否 |
| 玩家舰船 | 是 | 否 |
| 任务目标核心 | 是 | 否 |
| 正在执行 FTL 的 shuttle | 是 | 是 |
| 不允许暂停的任务倒计时 | 是 | 是 |
| 管理员测试对象 | 可配置 | 可配置 |

关键任务对象不应因地图休眠被清理，但是否保持地图活跃应由独立规则决定。

## 进入休眠

推荐流程：

```text
Ready → PreparingSleep
  → 阻止新的 arrival 和战略写入
  → 再次检查玩家、PendingArrivals、FTL 和 blocker
  → 处理明确允许清理的瞬时对象
  → 创建或更新战略层记录
  → State = Sleeping
  → SetPaused(mapId, true)
```

必须在真正暂停前进行第二次条件检查，因为初次检查后可能出现：

- 玩家重新连接；
- shuttle 开始到达；
- 新任务创建；
- encounter 状态变化；
- 管理员添加 blocker。

如果检查失败，应回到 `Ready`，不得留下部分清理或半初始化的战略状态。

## 恢复地图

当前 `NsvBluespaceSectorSystem.TryGetOrCreate()` 只接受 `Ready` sector。增加 Sleeping 后应改为：

```text
找到 Ready sector
  → 直接返回

找到 Sleeping sector
  → TryWakeSector
  → 唤醒完成后返回

找到 PreparingSleep/Waking sector
  → 等待、排队或拒绝重复切换

没有现有 sector
  → 正常创建
```

恢复顺序：

```text
Sleeping → Waking
  → 战略层进入 Transition，停止后台 tick
  → 应用累计任务、增援和移动结果
  → 处理战略层已摧毁对象的 tombstone
  → SetPaused(mapId, false)
  → 刷新需要重建的 AI 感知和系统缓存
  → Waking → Ready
  → 允许 FTL arrival 或玩家进入
```

`NsvBluespaceSectorTravelSystem.TryStartArrival()` 必须在地图完成唤醒后才能调用 `FTLToCoordinates()`。

玩家断线后重新连接也是独立唤醒入口。如果玩家的 `AttachedEntity` 位于 Sleeping map，应在允许玩家操作前唤醒该 map。

## 状态保持

### 首版内存保持

首版无需为每个组件建立手写 snapshot。现有 ECS 状态继续保存在原实体中：

```text
位置与朝向      TransformComponent
库存            ContainerManager
弹药            Gun/Magazine/AmmoProvider/Container
阵营            NsvBluespaceFactionComponent
生命和损伤      各实体 Damageable、结构与舰船组件
地图修改        grid tile 与现有实体树
任务状态        encounter 和 objective 组件
自定义状态      原组件运行时字段
```

由于不会重新运行 sector generator：

- 已摧毁实体不会复活；
- 已迁出的实体不会在原 sector 再生成；
- 新进入实体不会因模板加载而复制；
- 基础模板不会覆盖已有 tile 或建筑修改。

### 逻辑唯一 ID

`EntityUid` 只保证当前 `EntityManager` 生命周期内的运行时身份。纯内存休眠时它保持稳定，但战略层、UNLOADED 和未来跨重启存档需要独立逻辑 ID。

建议仅给战略重要的根对象分配 ID：

```text
sector map
主要舰船 grid
空间站 grid
重要残骸
encounter controller
objective target
需要跨 sector 移动的战略舰队
```

不建议给每一面墙、子弹和普通物品生成 GUID。

组件和注册表可采用：

```text
NsvPersistentIdComponent
  Id: Guid

LogicalIdRegistry
  LogicalId
    → Live EntityUid?
    → CurrentSectorId
    → Prototype/VesselId
    → Alive / Destroyed / Migrated
    → Generation
```

必须保留 destroyed tombstone，以区分：

```text
对象尚未生成
对象已经被摧毁
```

同一逻辑 ID 在任何时刻最多只能对应一个 live entity。

## 瞬时对象清理

不应在休眠时删除所有带 `TimedDespawnComponent` 的实体，因为它可能包含仍有玩法意义的对象，例如手雷、导弹、鱼雷或任务载体。

推荐：

1. 保留现有两分钟空置宽限期，让大部分弹丸和视觉效果自然结束；
2. 增加明确的 `NsvSectorSleepTransientComponent`；
3. 或使用经过审核的组件/原型白名单；
4. 删除前排除 player、persistent 和 encounter member；
5. 清理结果必须是幂等的。

建议默认规则：

| 类型 | 休眠处理 |
| --- | --- |
| 普通短寿命弹丸 | 清理 |
| 纯视觉特效 | 清理 |
| 临时 AI 路径缓存 | 清空或忽略 |
| 持续光束/爆炸载体 | 按具体类型审核 |
| 手雷、导弹、鱼雷 | 不通过通用 TimedDespawn 规则删除 |
| 玩家和容器内容 | 永不删除 |
| encounter/objective 对象 | 永不通过通用规则删除 |

如果不清理某个弹丸，map pause 会精确冻结它，恢复后它会继续运动。这保持了状态一致性，但是否符合视觉和玩法预期需要按武器类型决定。

## 后台战略模拟

后台模拟不应通过周期性唤醒整个 map 运行几帧实现。暂停实体默认不参加普通查询，且临时唤醒会重新启动物理、AI、power 和其他实时系统。

应将战略状态放在 nullspace manager、全局系统或独立 registry 中：

```text
NsvSectorStrategicState
  SectorLogicalId
  SimulationOwner
  LastUpdateTime
  StrategicTickInterval
  FleetRecords
  ReinforcementTimers
  MissionState
  PendingResults
```

实时和战略模拟必须互斥：

```text
SimulationOwner.Tactical
SimulationOwner.Transition
SimulationOwner.Strategic
```

规则：

```text
Ready       只能由实时系统结算
Sleeping    只能由战略系统结算
Transition  两边都不能结算
```

战略层不应在每个低频 tick 直接修改 paused map 内大量实体。更安全的做法是累计离散结果，在 Waking 阶段一次性应用。

首版适合支持：

- 任务全局截止时间；
- 增援倒计时；
- faction 战略计数；
- 完全抽象化的 NPC fleet 路线；
- 不要求映射具体 tile 和设备损伤的事件。

首版不适合支持：

- 玩家实际舰船的抽象战斗；
- tile、炮塔和弹药级后台损伤；
- docked grid 的跨 sector 战略迁移；
- 通过临时唤醒执行完整 AI 战斗。

## 需求映射

| 需求 | 首版可行性 | 实现方式 |
| --- | --- | --- |
| R1 ACTIVE/SLEEPING | 高 | `Ready/Sleeping` + `SetPaused` |
| R1 UNLOADED | 低 | 后续自定义 snapshot 和恢复 |
| R2 休眠条件 | 高 | 玩家检测、PendingArrivals、blocker、宽限期 |
| R3 状态保留 | 很高 | 保留 paused map，不重建实体 |
| R4 实体数据 | 很高 | 原 ECS 组件继续存在；重要根对象增加逻辑 ID |
| R5 瞬时清理 | 中 | 明确 marker 或审核过的白名单 |
| R6 后台模拟 | 部分 | 简单计时可行；抽象战斗困难 |
| R7 唯一性 | 高 | 内存保持 UID；战略层使用 logical ID 和 tombstone |
| R8 关键对象保护 | 高 | Persistent 与 SleepBlocker 分离 |
| R9 安全切换 | 中高 | PreparingSleep/Waking、generation、二次检查 |
| R10 停止高成本更新 | 高但非绝对 | map pause + 特殊系统审计和性能验证 |

## 安全切换与幂等性

建议每个 sector 保存：

```text
TransitionGeneration
LastStateChange
SimulationOwner
PendingWakeReason
```

规则：

- 相同 generation 的 sleep/wake 请求只能提交一次；
- `Sleeping` 再次休眠直接成功，不重复清理；
- `Ready` 再次唤醒直接成功，不重复应用战略结果；
- Waking 期间 arrival 排队或失败，不能直接进入暂停地图；
- PreparingSleep 期间出现玩家或 arrival 时回滚到 Ready；
- 战略结果带结算 generation，防止恢复失败后重复伤害或重复增援；
- `TryDispose()` 只用于真正销毁，不参与普通休眠。

对于内存暂停，`SetPaused()` 本身没有复杂的序列化失败路径。未来实现 UNLOADED 时，应采用 staging load：新实例和引用全部恢复成功后，再原子替换 registry 中的 live UID。

## 测试建议

扩展 NSV sector integration test，至少覆盖：

1. 无玩家经过延迟后进入 Sleeping；
2. 多名玩家只离开一部分时不休眠；
3. `PendingArrivals` 存在时不休眠；
4. sleep blocker 存在时不休眠；
5. 玩家重连或 FTL 到达前自动唤醒；
6. 睡眠前后关键实体 `EntityUid` 不变；
7. 库存、弹药、损伤、位置和 faction 不变；
8. 修改过的 tile 不恢复模板值；
9. 已删除实体不重新生成；
10. 睡眠期间 NSV AI accumulator 和物理位置不变化；
11. 快速反复 sleep/wake 不重复执行；
12. encounter controller、participant 和 objective UID 仍有效；
13. 同一个 logical ID 不会注册两个 live entity；
14. transient cleanup 不删除玩家、任务对象和容器内容；
15. sector 最终销毁仍正确清理 active registry 和 encounter；
16. profiler 确认 sleeping map 的 AI、physics 和 collision 成本显著下降。

## 推荐实施顺序

### 阶段 1：内存休眠

```text
扩展 sector state
  → 玩家和 blocker 检测
  → map pause/unpause
  → travel 唤醒接入
  → reconnect 唤醒
  → 状态保持和快速切换测试
```

### 阶段 2：逻辑身份

```text
关键实体 PersistentId
  → global registry
  → destroyed/migrated tombstone
  → encounter/objective 引用适配
```

### 阶段 3：性能与容量

```text
明确 transient cleanup
  → 审计 AllEntityQuery/global timer/IgnorePaused
  → CPU 和内存指标
  → 最大 sleeping sector 数或 LRU 策略
```

### 阶段 4：简单战略模拟

```text
Tactical/Strategic 独占结算
  → 任务时间
  → 增援
  → faction 状态
  → 完全抽象化的 NPC fleet movement
```

### 阶段 5：UNLOADED 评估

```text
自定义 snapshot
  → entity reference remap
  → power/station/docking 恢复
  → mission 和 timer 恢复
  → staging load 与失败回滚
```

### 阶段 6：长期扩展

```text
抽象战斗
跨 sector 实体迁移
磁盘存档
版本迁移
跨服务器重启恢复
```

## 根据当前玩法不适合直接实现的内容

### 所有访问过的 sector 永久保留

NSV sector 当前是短生命周期 encounter 实例。若所有访问过的节点永久留在内存，一局中会持续积累：

- entity 和 component；
- grid 和 tile；
- atmos 数据；
- network state；
- 系统外部缓存。

至少需要以下一种策略：

```text
最大 sleeping sector 数
LRU 最终销毁或卸载
只有指定持久节点允许休眠
已完成且无重要状态的 encounter 继续销毁
```

否则只是将 CPU 压力转换为不断增长的内存压力。

### 玩家离开后立即冻结所有后果

完全暂停可能允许或造成：

- 断线冻结危险；
- 火灾、泄漏和反应堆状态永久停止；
- 增援和任务倒计时停止；
- 炮弹在返回时突然继续飞行。

需要为每类系统明确使用实时模拟时间还是服务器全局时间。当前 patrol encounter 通常不允许目标完成前主动离开，但断线和非 encounter sector 仍需要处理。

### 对玩家实际舰船执行抽象战斗

当前舰船不是简单的 HP/DPS 单位：

- grid 没有统一 hull health；
- 损伤分布于 tile、结构和设备；
- 武器依赖供电、弹药、炮塔射界和 fire control；
- 护盾可能由多个 emitter 组成；
- crew 能维修、装填和关闭系统。

Standalone ship AI 目前也因为没有可靠的 grid hull damage 指标，只能使用 shield stress。将玩家舰压缩为抽象数值会造成较大的公平性和状态映射问题。

### 使用 station 体系作为 sector 舰船注册表

NSV 动态 sector generator 主要加载 grid、设置 faction 并记录 `OwnedGrids`，不会保证每艘舰船都成为逻辑 station。休眠与持久化应依赖 sector ownership 和 logical ID，而不是 `StationMemberComponent`。

## 很难实现或影响很大的内容

### UNLOADED 序列化与重建

地图删除会递归删除 transform 子树，重新加载后通常会发生：

- `EntityUid` 和 `MapId` 改变；
- 外部 `EntityCoordinates` 锚点失效；
- encounter/controller/objective UID 引用失效；
- station membership 需要重建；
- deed 和 shuttle record 可能指向旧实体；
- docking joint 需要重新建立；
- power/node network 存在序列化限制；
- 全局 timer 不可直接序列化；
- 未声明为可序列化的运行时字段丢失。

`NsvBluespaceSectorInstanceComponent` 当前的 ownership、arrival、return destination 和 encounter 字段主要是运行时集合，不能直接依靠通用 map YAML 自动恢复。

### 基础模板加修改差异

使用 prototype/seed 重建基础地图，再应用变化 diff，看似节省空间，但必须正确表达：

- 删除实体；
- 迁出实体；
- 新生成实体；
- 容器和实体引用；
- tile 和结构变化；
- prototype 更新后的兼容性。

任何遗漏都可能导致实体复活、重复或状态被模板覆盖。以后做存档时，完整 snapshot 加显式 sidecar 和版本迁移通常比任意 ECS delta 更可控。

### 休眠期间跨 sector 移动实际 grid

跨 sector 战略移动需要同步：

- 实际 grid 的 map parent；
- logical ID registry；
- faction；
- encounter membership；
- station membership；
- docked grids；
- 外部引用；
- 来源和目标 sector 的加载状态。

它不应被实现成单纯修改 `Transform` 或 `CurrentSectorId`。应在战略舰队模型和 logical ID registry 成熟后单独设计。

### 保证完全没有后台实时更新

`SetPaused()` 可以停止大部分更新，但无法自动约束所有：

- `AllEntityQuery`；
- 全局 timer callback；
- `IgnorePaused` physics body；
- nullspace controller；
- 系统外部缓存或队列。

R10 必须通过代码审计、集成测试和 profiler 验证，不能只以 map 的 paused flag 作为验收标准。

## 最终建议

R1–R5、R7–R10 的内存休眠版本与当前 NSV sector 架构兼容，应作为第一阶段实现。它可以在不重建实体的情况下保持地图修改和运行时状态，并显著减少无人 sector 的实时更新成本。

`UNLOADED`、玩家舰抽象战斗、休眠期间实际 grid 跨 sector 迁移和跨重启存档应明确后置。这些功能需要独立的持久身份、战略数据模型、引用恢复和版本迁移体系，不能作为 map pause 的简单扩展。