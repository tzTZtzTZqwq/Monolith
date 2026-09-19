# NSV 战略操作队列与舰队物化设计

## 状态

本文描述 NSV 星图战略舰队、星区生命周期和实体物化之间的目标架构。

当前已经存在：

- `Content.Server/_NSV/Bluespace/Strategy/NsvFleet.cs`：最小舰队数据模型；
- `Content.Server/_NSV/Bluespace/Strategy/NsvFleetShip.cs`：最小舰船数据模型；
- `NsvBluespaceSectorLifecycleSystem`：`Ready → PreparingSleep → Sleeping → Waking → Ready` 生命周期；
- 星图节点、节点连接、星区实例、FTL、阵营和 encounter 基础设施。

当前尚未实现：

- 全局战略操作队列；
- 舰队物化与去物化系统；
- 舰队跨节点战略时间推进；
- live grid 与战略舰船数据的稳定绑定；
- 舰船战损、库存和设备状态的完整数据快照。

因此，本文是后续实现契约，不表示所有接口已经存在。

## 目标

1. 将星区休眠、星区唤醒、舰队出发和舰队抵达统一为有序的战略操作。
2. 保证同一逻辑舰队不会同时拥有两份可写权威表示。
3. 活跃星区中的舰队使用真实 grid；休眠或未物化节点中的舰队使用纯数据。
4. 操作失败时不得留下半支舰队、重复舰船、失效节点位置或无法唤醒的星区。
5. 玩家相关唤醒可以取消尚未提交的自动休眠，但不能中断已经越过提交点的事务。
6. 舰队航行不长期占用全局队列；出发与抵达分别作为原子事务。

## 非目标

首版不要求：

- 跨服务器重启恢复；
- 完整序列化每个实体和组件；
- map 外逐弹丸或逐组件战斗模拟；
- 多线程并行战略事务；
- 在同一事务中等待数分钟的舰队航行；
- 自动把所有带 AI core 的现有舰船识别为战略舰队。

## 当前数据模型

### NsvFleet

当前字段：

```csharp
public sealed class NsvFleet
{
    public string Id = string.Empty;
    public ProtoId<NsvBluespaceFactionPrototype> Faction = string.Empty;
    public ProtoId<NsvBluespaceStarmapPrototype> StarmapId = string.Empty;
    public string CurrentNodeId = string.Empty;
    public string? DestinationNodeId;
    public float Fatigue;
    public List<NsvFleetShip> Ships = new();
}
```

### NsvFleetShip

当前字段：

```csharp
public sealed class NsvFleetShip
{
    public string Id = string.Empty;
    public ResPath GridPath;
}
```

该模型现在只能稳定标识舰队、阵营、星图位置、目的地、疲劳度和舰船地图。若去物化后重新按 `GridPath` 加载，舰船会恢复为地图默认状态；当前模型不能保存战损、库存、弹药或设备损毁。

## 建议增加的舰队状态

```csharp
public enum NsvFleetState
{
    AtNode,
    Traveling,
    Materializing,
    Live,
    Dematerializing,
    Destroyed,
}
```

`NsvFleet` 后续应增加：

```csharp
public NsvFleetState State;
public uint Revision;
```

含义：

| 状态 | 权威表示 | 说明 |
| --- | --- | --- |
| `AtNode` | 数据 | 舰队停留在节点，没有可运行的 live grid |
| `Traveling` | 数据 | 舰队正在星图边上航行 |
| `Materializing` | 操作事务 | 正在从数据生成 grid，外部不得修改 |
| `Live` | 实体 | 真实 grid 是战损、装备和位置的权威来源 |
| `Dematerializing` | 操作事务 | 正在从 grid 生成数据快照 |
| `Destroyed` | 数据 | 舰队不再允许物化或航行 |

`Revision` 每次成功提交战略修改后递增。操作在准备阶段保存预期 revision，并在提交前复检，防止陈旧操作覆盖新状态。

## 核心不变量

1. 一个 `FleetId` 在任意时刻只有一个可写权威表示。
2. `Live` 舰队必须能找到其所有 live grid；`AtNode` 和 `Traveling` 舰队不得存在可运行的 live grid。
3. `Materializing` 和 `Dematerializing` 状态只能由当前战略操作修改。
4. 同一个 `FleetId + ShipId` 最多对应一个 live root grid。
5. 星区进入 `Sleeping` 前，所有需要去物化的战略舰队必须完成数据提交。
6. 星区进入 `Ready` 前，所有要求实时存在的节点舰队必须完成物化。
7. 舰队跨节点期间只保存数据状态，不持有源星区或目的星区的长期操作锁。
8. 操作失败不得递增 fleet revision，也不得改变权威表示。
9. 自动休眠不能删除不属于战略舰队的普通模板舰船或 encounter 目标。
10. 所有休眠、唤醒和舰队位置写入必须经过统一操作系统；其他系统只能提交请求或读取快照。

## Live grid 绑定

仅凭 `NsvBluespaceShipAiCoreComponent` 无法判断舰船属于哪个战略舰队。模板舰、encounter 舰和战略舰队都可能带 AI core。

应在物化出的 root grid 上添加服务器组件：

```csharp
[RegisterComponent]
public sealed partial class NsvFleetShipComponent : Component
{
    public string FleetId = string.Empty;
    public string ShipId = string.Empty;
}
```

建议路径：

```text
Content.Server/_NSV/Bluespace/Strategy/NsvFleetShipComponent.cs
```

该组件用于：

- 从 live grid 找回 `NsvFleet` 和 `NsvFleetShip`；
- 防止同一舰船重复物化；
- 休眠时只去物化受战略系统管理的舰船；
- 从 `OwnedGrids` 移除正确的 root grid；
- 诊断数据舰队与 live grid 的一致性。

## 全局战略操作队列

建议新增：

```text
Content.Server/_NSV/Bluespace/Strategy/NsvStrategicOperationSystem.cs
Content.Server/_NSV/Bluespace/Strategy/NsvStrategicOperation.cs
Content.Server/_NSV/Bluespace/Strategy/NsvFleetMaterializationSystem.cs
```

第一版采用严格串行队列：

```csharp
private readonly Queue<NsvStrategicOperation> _pending = new();
private NsvStrategicOperation? _active;
```

任何时刻只有一个战略操作可以处于准备、提交或回滚阶段。当前星图规模较小，严格串行优先保证正确性。后续只有在实际性能分析证明需要时，才升级为按 fleet/sector 资源键并行。

### 操作类型

```csharp
public enum NsvStrategicOperationType
{
    SleepSector,
    WakeSector,
    DepartFleet,
    ArriveFleet,
    MaterializeFleet,
    DematerializeFleet,
}
```

其中 `MaterializeFleet` 和 `DematerializeFleet` 可以先作为 Sleep/Wake/Arrive 内部子事务，不一定对所有外部系统公开。

### 操作状态

```csharp
public enum NsvStrategicOperationState
{
    Queued,
    Preparing,
    Committing,
    RollingBack,
    Completed,
    Cancelled,
    Failed,
}
```

### 操作记录

操作至少应记录：

```csharp
public Guid OperationId;
public NsvStrategicOperationType Type;
public NsvStrategicOperationState State;
public string? FleetId;
public EntityUid? SourceSector;
public EntityUid? DestinationSector;
public uint? ExpectedFleetRevision;
public uint? ExpectedSectorEpoch;
public bool CancelRequested;
public string? Failure;
```

`OperationId` 提供幂等性。重复提交同一 ID 时返回已有操作，而不是重复生成或删除舰船。

## 原子性定义

全局队列只保证操作顺序，不自动提供事务。

每个操作必须使用：

```text
Prepare
→ Commit
```

失败时：

```text
Prepare
→ Rollback
```

### Prepare

Prepare 可以读取、验证和建立临时计划，但不能改变权威状态或删除实体。

例如舰队物化 Prepare：

- 验证 fleet revision；
- 验证舰队状态和节点；
- 验证阵营与所有 `GridPath`；
- 规划每艘舰船的位置；
- 检查没有重复 live 绑定。

### Commit

Commit 只能执行 Prepare 已证明可行的修改。进入 Commit 后不再接受取消；所有可能失败的业务验证应在 Prepare 完成。

### Rollback

Rollback 必须删除本次操作生成的临时 grid、恢复状态并释放 lifecycle blocker。不能依赖下一次 registry scan 自动修复。

## 提交点

每个操作必须有明确提交点：

- 提交点之前：允许 `CancelRequested`，可以完整回滚；
- 提交点之后：操作必须完成，新请求只能排在其后；
- 不允许在删除一半舰船后响应取消。

对于 Sleep，提交点是：

```text
map 已暂停
→ 权威 blocker 与 epoch 最终复检通过
→ 所有舰队数据快照均已成功建立
→ 即将切换数据权威并删除 live grid
```

## 请求优先级与合并

建议优先级：

1. 玩家进入、FTL 和重连需要的 `WakeSector`；
2. 舰队出发或抵达；
3. 自动 `SleepSector`。

优先级只能影响尚未开始的操作，不能抢占 `Committing` 操作。

同资源请求应合并：

| 已存在操作 | 新请求 | 处理 |
| --- | --- | --- |
| `SleepSector` | 相同 sector 的 `SleepSector` | 返回已有 handle |
| `WakeSector` | 相同 sector 的 `WakeSector` | 返回已有 handle |
| 未提交 `SleepSector` | 相同 sector 的 `WakeSector` | 设置 Sleep 的 `CancelRequested` |
| 已提交 `SleepSector` | 相同 sector 的 `WakeSector` | Wake 排在 Sleep 后 |
| `MaterializeFleet` | 相同舰队的物化 | 返回已有 handle |
| `DepartFleet` | 相同舰队的第二次出发 | 拒绝 |

## 场景一：AI 舰队进入活动星区

舰队抵达不应直接调用 `MapLoaderSystem`。战略系统提交：

```text
ArriveFleet(fleetId)
```

### Prepare

1. 验证舰队是 `Traveling`；
2. 验证 `DestinationNodeId` 存在且与目标一致；
3. 验证目标节点属于同一 `StarmapId`；
4. 查找目标节点当前 sector；
5. 判断目标是否需要实时物化；
6. 若需要物化，准备所有舰船的位置和加载计划。

### Commit

共同修改：

```text
CurrentNodeId = DestinationNodeId
DestinationNodeId = null
Fatigue += 航行疲劳
Revision++
```

目标星区确实活动时：

```text
Traveling
→ Materializing
→ 加载全部 grid
→ 设置 faction
→ 添加 NsvFleetShipComponent
→ 登记 OwnedGrids 和 live 映射
→ Live
```

目标星区休眠、尚未创建或正在无人休眠倒计时时：

```text
Traveling
→ AtNode
```

舰队到达本身不应自动唤醒无人星区。玩家抵达、任务要求实时运行或管理员请求才触发唤醒和物化。

## 场景二：带 AI 舰队的星区休眠

当前 `NsvBluespaceSectorLifecycleSystem.RefreshRegistry()` 在 sleep deadline 到达后直接调用 `TryCommitSleep()`。目标实现应改为提交或合并：

```text
SleepSector(mapUid)
```

### Prepare

1. 验证 sector 是 `PreparingSleep`；
2. 保存 `TransitionEpoch`；
3. 验证没有存活玩家、pending arrival 或 must-run blocker；
4. 找出 sector 内所有带 `NsvFleetShipComponent` 的战略舰船；
5. 按 `FleetId` 分组；
6. 验证每个 `ShipId` 唯一并存在于对应数据模型；
7. 验证没有不支持去物化的 encounter 引用；
8. 验证所有舰队状态都是 `Live`。

### 冻结与最终复检

```text
_map.SetPaused(mapId, true)
→ 复检 TransitionEpoch
→ 复检 sector.State == PreparingSleep
→ 复检 arrival/player/must-run blocker
→ 检查 CancelRequested
```

暂停后再建立舰船快照，保证所有舰船数据来自同一模拟时刻。

### Commit

```text
fleet.State = Dematerializing
→ 写回全部 NsvFleetShip 数据
→ fleet 数据成为权威
→ 从 OwnedGrids 移除舰船 root grid
→ 删除或排队删除 live grid
→ 清理 live 映射
→ fleet.State = AtNode
→ fleet.Revision++
→ sector.State = Sleeping
```

任何舰队快照失败时，必须在删除任何 grid 前失败并调用现有 `RollbackSleep()`：

```text
丢弃临时快照
→ unpause map
→ sector.State = Ready
→ fleet 保持 Live
```

## 场景三：休眠过程中收到唤醒

### Sleep 尚未越过提交点

```text
WakeSector 到达
→ Sleep.CancelRequested = true
→ Sleep 回滚
→ 保留 live fleet grids
→ sector 回到 Ready
→ Wake 完成或成为 no-op
```

### Sleep 已越过提交点

```text
Sleep 必须完成
→ sector = Sleeping
→ WakeSector 随后执行
→ unpause
→ 重新物化节点舰队
→ sector = Ready
```

不能在已经写回一部分舰队数据或删除一部分 grid 后强行取消。

## 场景四：星区唤醒

`WakeSector` 的目标流程：

```text
Sleeping
→ Waking
→ unpause map
→ 查找该 StarmapId + NodeId 上需要实时存在的 AtNode 舰队
→ 原子物化所有目标舰队
→ Ready
```

建议将节点舰队物化放在 `NsvBluespaceSectorLifecycleSystem.TryWakePausedSector()` 解除暂停之后、设置 `Ready` 之前。

若任意舰队物化失败：

```text
删除本轮已生成的所有 grid
→ 所有舰队保持 AtNode
→ 重新 pause map
→ sector 回到 Sleeping
```

外部实体只有在 `Ready` 后才能进入。

## 场景五：舰队跨星区

整个航行过程不能占据队列。跨区必须拆成两个事务。

### DepartFleet

```text
AtNode 或 Live
→ 若 Live，冻结并去物化源舰队
→ CurrentNodeId 保留为出发节点或转入航线状态
→ DestinationNodeId = 目标节点
→ State = Traveling
→ 记录出发时间和预计抵达时间
→ Revision++
```

首版尚未定义 travel 时间字段；实现航行前需要补充出发时间或预计抵达时间。

### 战略时间推进

```text
Traveling
→ 只由战略系统推进
→ 不占用全局操作队列
```

### ArriveFleet

到达时间满足后提交独立 `ArriveFleet`，更新节点位置，并依据目标 sector 活跃状态决定保持数据还是物化。

## 物化事务

建议接口：

```csharp
public bool TryPrepareMaterialization(
    NsvFleet fleet,
    EntityUid sectorMap,
    Vector2 anchorPosition,
    out NsvFleetMaterializationPlan plan,
    out string? failure);

public bool CommitMaterialization(
    NsvFleetMaterializationPlan plan,
    out string? failure);

public void RollbackMaterialization(
    NsvFleetMaterializationPlan plan);
```

物化应复用舰船 generator 的底层 grid 加载能力：

```text
TryLoadGrid
→ SetFaction
→ 添加 NsvFleetShipComponent
→ OwnedGrids.Add
```

当前 `NsvBluespaceShipGenerator.TryGenerate()` 只接受静态 module definition。后续应抽出接收 `GridPath`、faction 和 position 的底层方法，使静态模块生成和战略舰队物化共享同一实现。

## 去物化事务

建议接口：

```csharp
public bool TryPrepareDematerialization(
    EntityUid sectorMap,
    out NsvFleetDematerializationPlan plan,
    out string? failure);

public void CommitDematerialization(
    NsvFleetDematerializationPlan plan);

public void CancelDematerialization(
    NsvFleetDematerializationPlan plan);
```

Prepare 必须完成所有实体到数据的映射和快照验证。Commit 不应继续执行可能失败的业务查询。

## 对外请求 API

建议使用操作 handle，而不是只返回 `bool`：

```csharp
public NsvStrategicOperationHandle RequestSectorSleep(EntityUid sectorMap);

public NsvStrategicOperationHandle RequestSectorWake(
    EntityUid sectorMap,
    NsvBluespaceSectorWakeReason reason,
    EntityUid? requester);

public NsvStrategicOperationHandle RequestFleetDeparture(
    string fleetId,
    string destinationNodeId);

public NsvStrategicOperationHandle RequestFleetArrival(string fleetId);

public bool TryGetOperationResult(
    NsvStrategicOperationHandle handle,
    out NsvStrategicOperationResult result);
```

请求状态至少区分：

```csharp
public enum NsvStrategicOperationRequestStatus
{
    Completed,
    Queued,
    Rejected,
}
```

FTL、玩家重连和管理员移动在收到 `Queued` 时，必须保留 arrival reservation 或等待状态，直到操作完成通知；不能把 `Queued` 当成 `Ready`。

## 与生命周期 blocker 的关系

物化、去物化以外的战略操作可能需要阻止目标 sector 在处理中自动休眠。现有生命周期提供：

```csharp
RegisterMustRunTaskBlocker(mapUid, owner, out failure)
UnregisterMustRunTaskBlocker(mapUid, owner)
```

该接口要求 owner 是实体。实现时可以为活动操作创建临时 operation entity，并将其注册到涉及的 source/destination sector。

`SleepSector` 本身不能注册一个阻止自己休眠的 blocker；它通过生命周期 transition owner 和 epoch 获得独占权。

所有完成、失败和取消路径都必须释放 blocker。

## Encounter 边界

如果 encounter controller 的 `ObjectiveTarget` 指向即将删除的舰队 core，直接去物化会留下失效 UID。

首版策略：

1. 只去物化带 `NsvFleetShipComponent` 的战略舰队；
2. 若 live fleet 被活动 encounter 引用，则拒绝该舰队的去物化；
3. sector 保持 `Ready`，或让 encounter 注册 must-run blocker；
4. 等抽象 encounter resolver 完成后，再允许 encounter 舰队数据化。

普通模板舰船和未登记战略身份的 AI 舰继续随 map pause，不由舰队去物化系统删除。

## 舰船快照演进

当前 `NsvFleetShip` 只能记录 `Id` 和 `GridPath`。

### 第一阶段

只记录舰船是否存活：

```csharp
public bool Destroyed;
```

存活舰船重新物化时按 `GridPath` 完整加载，局部战损会被重置。该行为必须在玩法设计和管理诊断中明确显示为限制。

### 第二阶段

增加抽象战斗需要的字段，例如：

```csharp
public float HullIntegrity;
public float CombatPower;
public float Supply;
```

### 第三阶段

根据实际需求保存关键设备、弹药、库存和武器状态。不要默认尝试序列化整张 grid 的每个组件；只有跨重启或完整快照恢复确有需求时再引入通用实体快照。

## 故障与回滚要求

### 物化部分成功

```text
舰船 1 成功
舰船 2 成功
舰船 3 失败
```

必须：

```text
删除舰船 1、2
→ fleet 保持 AtNode
→ 不登记任何 live 映射
→ 不递增 revision
```

### 去物化快照失败

```text
舰船 1、2 快照成功
舰船 3 无法匹配 ShipId
```

必须：

```text
丢弃所有临时快照
→ 不删除任何 grid
→ fleet 保持 Live
→ sector 休眠回滚
```

### 删除排队期间

数据提交后若 live grid 使用延迟删除，grid 必须保持 paused 或显式禁用 AI/物理。此时数据已经是权威，旧 grid 只能等待清理，不能重新参加模拟。

## 可观察性

战略操作至少记录：

- operation ID；
- 类型和状态；
- fleet ID；
- source/destination sector；
- 排队时间、开始时间和完成时间；
- 失败原因；
- 是否发生 rollback；
- 操作前后的 fleet revision 和 sector epoch。

建议后续扩展 `nsvsectormonitor` 或新增管理命令，显示：

- 当前活动操作；
- 排队操作；
- 每个节点的数据舰队；
- 每个 live fleet 对应的 grid；
- 孤立 grid 和重复 `FleetId + ShipId`；
- 长时间停留在 `Materializing`/`Dematerializing` 的舰队。

## 测试计划

### 队列与合并

1. 重复 Sleep 请求只产生一个操作；
2. 重复 Wake 请求只产生一个操作；
3. 未提交 Sleep 被 Wake 取消；
4. 已提交 Sleep 完成后再执行 Wake；
5. 操作回调中提交新操作不会重入当前 Commit。

### 物化

1. `AtNode` 舰队在活动节点只物化一次；
2. 每艘舰船得到正确 faction、FleetId 和 ShipId；
3. 第 N 艘加载失败时删除本轮前 N-1 艘；
4. 物化失败不修改 fleet revision；
5. 休眠节点中的舰队抵达不会自动唤醒或生成 grid。

### 去物化与休眠

1. pause 后读取一致快照；
2. 所有舰队快照成功后才删除 grid；
3. 任意舰船验证失败时不删除任何 grid；
4. 去物化完成后 sector 才进入 `Sleeping`；
5. 唤醒后每个存活 `NsvFleetShip` 恰好生成一个 grid；
6. 模板 AI 舰和未标记舰船不会被误删；
7. encounter 引用的舰队阻止首版去物化。

### 跨节点

1. Depart 将 Live/AtNode 舰队转换为 Traveling；
2. 航行期间不占用全局队列；
3. Arrive 原子更新节点、目的地、疲劳度和 revision；
4. 活动目标节点物化，休眠目标节点保持数据；
5. 重复或陈旧 Arrive 被 revision 检查拒绝。

## 实施顺序

1. 为 `NsvFleet` 增加 `State` 和 `Revision`；
2. 新增 `NsvFleetShipComponent`；
3. 建立舰队 registry，按 ID 查询 `NsvFleet`；
4. 实现严格串行的全局操作队列、handle 和结果通知；
5. 从 ship generator 抽出通用 `TryGenerateGrid`；
6. 实现舰队物化事务及失败回滚；
7. 实现舰队去物化 Prepare/Commit/Cancel；
8. 将自动休眠接入 `SleepSector` 操作；
9. 将 `RequestWake`、FTL、重连和管理员移动接入 `WakeSector`；
10. 实现 Depart/Arrive 与战略时间推进；
11. 补充 encounter blocker、管理诊断和故障注入测试；
12. 性能验证后再决定是否从全局串行升级为资源级并行。

## 最终行为摘要

```text
AI 舰队进入活动星区
→ ArriveFleet
→ 数据状态提交
→ Materialize
→ Live

AI 舰队进入休眠星区
→ ArriveFleet
→ AtNode
→ 不唤醒、不生成实体

带战略舰队的星区休眠
→ SleepSector
→ pause
→ 快照全部舰队
→ 数据提交
→ 删除 live grids
→ Sleeping

休眠准备期间玩家要求唤醒
→ 提交点前取消 Sleep
→ 保留 live grids
→ Ready

休眠已经提交后要求唤醒
→ 完成 Sleep
→ WakeSector
→ 重新物化节点舰队
→ Ready

舰队跨星区
→ DepartFleet 原子提交
→ Traveling，不占队列
→ ArriveFleet 原子提交
```
