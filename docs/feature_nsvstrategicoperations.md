# NSV 战略操作协调与舰队物化设计

版本：修订版 v3 · 2026-09-19

## 1. 状态与修订范围

本文是目标设计，不代表接口已经实现；代码片段是接口草案。现有基础包括 `NsvFleet`、`NsvFleetShip`、星图节点与连接、sector 实例、FTL、阵营、encounter，以及 `Ready → PreparingSleep → Sleeping → Waking → Ready` 生命周期。

本版替代原有“全局唯一活动操作”方案，改为：

- 每个星区独立协调生命周期；
- 每支舰队独立归属操作；
- 涉及多个对象时一次取得全部资源预约；
- 主线程分帧准备、短提交、提交后清理；
- 全局工作预算只分配执行时间，不承担正确性或执行顺序。

在此基础上明确物化期间的休眠保护、唤醒恢复顺序、航次身份、失败边界、可恢复发布日志、唯一权威来源、节点抽象战斗资格、协作让出、独立 EntryIntent 和实例代次。第 16 节的当前代码差异经本工作区源码核对（约 2026-09-20）。

持久化基线保守：普通休眠保留实体，不强制去物化；去物化是独立能力，仅在快照与引用安全满足要求时开启。该选择保留 `feature_persistentsectors.md` 的实体状态保持承诺，不授权删除重建已有舰船。

## 2. 目标与非目标

### 目标

1. 不同星区的操作可交错推进，不被一个慢操作全局阻塞。
2. 同一舰队只有一个权威表示；操作不能重复生成或删除舰船。
3. 同一星区的战略实体修改与生命周期转换先串行协调。
4. 物化期间不得进入或提交自动休眠。
5. 玩家 Wake 可以取消提交前的自动 Sleep；提交后按顺序恢复。
6. 航行不持有源、目标星区的长期预约。
7. 失败可诊断；NotStarted 可回滚，InProgress 向前恢复，Committed 后激活与清理可重试。

### 非目标

- 多线程修改 ECS；
- 跨服务器重启恢复、通用完整实体序列化；
- map 外逐弹丸模拟；
- 自动将所有 AI core 舰船纳入战略系统；
- 首版支持任意拆裂、停靠、俘获舰船的数据化；
- 通过重载模板隐式修复战损或补充物资。

## 3. 系统职责

| 系统 | 职责 |
| --- | --- |
| SectorSystem | 创建、缓存、注册和最终销毁 sector |
| LifecycleSystem | 唯一的 map pause/unpause、进入门禁和生命周期转换执行者 |
| FleetRegistry | 舰队、舰船身份、节点驻留索引和实体绑定 |
| FleetOperationSystem | 出发、抵达、物化、去物化的计划、状态与结果 |
| ResourceCoordinator | 原子取得/释放资源预约，记录操作 owner |
| FleetMaterializationSystem | 隔离生成、验证、发布绑定和清理 |
| StrategicTimeSystem | 推进航行时间，提交到期抵达请求 |
| WorkScheduler | 按优先级和每帧预算推进可执行步骤 |
| TravelSystem | 真实 FTL、进入预留、等待与失败清理 |

可以先在少量系统中实现这些职责，不要求每一项都是独立类。战略系统通过生命周期接口请求实时 map 变更，不直接调用 `SetPaused`。

## 4. 数据模型与权威表示

现有 `NsvFleet` 包含 Id、Faction、StarmapId、CurrentNodeId、DestinationNodeId、Fatigue 和 Ships；`NsvFleetShip` 仅包含 Id 与 GridPath，不能恢复已有舰船的战损、库存或设备状态。

建议将稳定舰队状态与操作阶段分开：

```csharp
public enum NsvFleetState
{
    AtNode,     // 节点上的纯数据舰队
    Traveling,  // 航线上的纯数据舰队
    Live,       // ECS 权威；所在 map 可以暂停
    Destroyed,
}

// 以下为 NsvFleet 的新增字段草案
public NsvFleetState State;
public ulong Revision;
public Guid? ActiveOperationId; // 仅由 ResourceCoordinator 维护的诊断缓存
public string? CurrentNodeId; // 替换原不可空字段，空字符串非法
public NsvFleetTravel? Travel;
```

`Materializing`、`Dematerializing` 只是操作阶段展示，不是稳定权威。发布权威随 PublishState 转移（见不变量 10）：NotStarted 时旧数据/原实体仍是权威，InProgress 时以不可变 PublishPlan 向前完成，Committed 后目标表示公开。

`Live` 表示保有实体，不等于正在模拟。Sleeping map 上的 Live 舰队保留原 UID 和状态，唤醒时直接恢复，不再生成一份。

### 航次

```csharp
public sealed class NsvFleetTravel
{
    public Guid TravelId;
    public string OriginNodeId = string.Empty;
    public string DestinationNodeId = string.Empty;
    public TimeSpan DepartureTime;
    public TimeSpan ArrivalTime;
}
```

时间使用服务器运行期单调战略时钟。星区暂停不停止航行；服务器全局暂停是否停止战略时钟，首版统一为停止。实际引擎时钟接入需验证。

CurrentNodeId 必须改为可空，禁止用空字符串表达合法节点或未知位置。旧 DestinationNodeId 迁移到 Travel，不保留第二个可写目的地字段。

| Fleet.State | CurrentNodeId | Travel | 正式实体绑定 |
| --- | --- | --- | --- |
| AtNode | 必须为有效节点 | null | 无 |
| Traveling | null | 必须存在，含有效起点和终点 | 无 |
| Live | 必须为有效节点 | null | 有有效绑定，或明确的 Missing 异常 |
| Destroyed | null | null | 无；最后位置另存诊断字段 |

### 唯一权威与派生缓存

- `NsvFleet.State + CurrentNodeId + Travel` 是运行期逻辑位置权威，不提供跨重启持久化。
- 节点驻留索引只是派生缓存（收录 AtNode/Live），可从已提交 fleet 完整重建。不一致时以已提交 fleet 为准修复并记录诊断。
- InProgress 的 fleet 不按中间字段参与索引或玩法查询；读取返回 Busy，reconciliation 先按 PublishPlan 完成再重建索引。
- 位置修改只经 FleetRegistry 发布 API；批量 Wake 对全部参与 fleet 用同一发布日志与可见性屏障。
- ResourceCoordinator 的 owner 是操作归属唯一权威；ActiveOperationId 只是协调器维护的只读诊断缓存，不一致不授予权限。

Revision 只在成功发布战略修改后递增，准备/回滚不递增。它不替代实时实体快照冻结——日常战损不一定更新 revision。

### 舰船绑定

```csharp
[RegisterComponent]
public sealed partial class NsvFleetShipComponent : Component
{
    public string FleetId = string.Empty;
    public string ShipId = string.Empty;
    public ulong BindingGeneration;
}
```

舰船数据至少增加：

```csharp
public enum NsvFleetShipState
{
    Available, // 可由数据物化
    Live,
    Destroyed,
    Missing,   // 应有实体却未找到，不自动重生
}
// NsvFleetShip 新增字段
public NsvFleetShipState State;
public ulong BindingGeneration;
public string? RepresentationFailure;
public bool CanDematerialize;
```

Unsupported 是能力/失败原因，不覆盖 Live、Available 等存在状态；改用 CanDematerialize=false 加 RepresentationFailure 表达，以便区分“不支持去物化的船是否仍有实体”。CanDematerialize 须经当前引用与快照检查，不是永久授权。

Registry 维护 `(FleetId, ShipId) → (root grid, generation)`。BindingGeneration 存于舰船数据，组件与绑定记录是其副本；每次成功物化用 PublishPlan 固定的新代次，重放不再递增。旧实体删除回调须同时匹配 UID、generation 和删除原因才影响当前绑定。

Destroyed 只由明确损失事件确认，Missing 不推导 Destroyed；整支舰队 Destroyed 需全部有效舰船确认损失，不得因暂时找不到绑定而判定。

临时实体与退休待清理实体不进入正式绑定。OwnedGrids 仅作过渡兼容记录；实际位置以 Transform.MapUid 为准，战略归属以稳定身份和 registry 为准。

## 5. 核心不变量

1. 每支舰队只有一个权威表示；临时实体不可参与玩法。
2. 同一 FleetId/ShipId 最多有一个正式 root grid 绑定。
3. Live 的每艘有效舰船必须有可解释的绑定或明确的损失/异常记录。
4. AtNode/Traveling 不得拥有可运行的正式实体；退休实体始终隔离。
5. 每个资源只能被一个顶层操作预约；内部子步骤继承同一 owner。
6. 持有 MaterializationLease 的 Ready 星区不得进入 PreparingSleep。
7. Sleep 提交前后都复检 blocker、epoch、owner 和玩家/迁移状态。
8. Wake 完成全部必要恢复后才解除暂停并开放进入门禁。
9. 普通 sleep/wake 保留现有实体；不能重载模板重置状态。
10. NotStarted 阶段失败可回滚且不改变稳定权威；InProgress 只能按 PublishPlan 向前恢复；Committed 后只恢复激活或清理，不恢复旧权威。
11. 正常结束或安全回滚后释放预约与 blocker；InProgress/RecoveryRequired 保留必要 owner 与门禁直至安全恢复，不能通过通用 finally 或超时强行释放。
12. 暂停不是绝对写隔离；直接 UID 查询、全局任务和同步回调仍需遵守门禁。
13. Node 是节点写入唯一排他资源；lifecycle owner 必须等于其顶层 OperationId，不是独立竞争的锁。
14. 存在 Live 舰队、实时 encounter、表示转换或未完成恢复的节点禁止普通抽象战斗。
15. 周期扫描只派生生命周期请求，不绕过 Node 预约直接写状态。

## 6. 按资源协调，不设全局唯一活动操作

### 资源键

首版使用：

- `Fleet(fleetId)`；
- `Node(starmapId, nodeId)`。

Node 是逻辑星区槽位，即使尚未创建 map 也存在。创建、销毁实例和发布节点驻留集合都经过该资源，避免“抵达判断没有 map，同时另一路正在创建 map”的竞态。

### 实例身份与代次

Node registry 永久保留本回合的 `Next/LastInstanceGeneration`，不能随 map 销毁而清零。每次为同一 Node 分配全新 map 实例时，在 Node owner 下递增单调 generation；创建失败已消耗的代次不复用。普通 sleep/wake 不递增。

当前代次同时写入 Node registry 实例记录和 `NsvBluespaceSectorInstanceComponent.InstanceGeneration`；registry 是分配权威，component 是校验副本。操作保存并复检 `(NodeKey, MapUid, InstanceGeneration, TransitionEpoch)`，对无实例节点保存“无实例”前提及 registry 代次。

TransitionEpoch 标识同一实例内转换尝试，不能替代实例代次。重建 map 后所有旧计划、进入 token 和完成回调失效，不能悄悄绑定新实例；等待者必须明确重新请求。

### 最小预约集合

| 操作 | 资源 |
| --- | --- |
| AtNode 出发 | Fleet + 源 Node，用于移出驻留索引 |
| 抵达，目标不物化 | Fleet + 目标 Node |
| 抵达并物化 | Fleet + 目标 Node |
| Live 出发/去物化 | Fleet + 源 Node |
| 普通保留实体的 Sleep | Node |
| Wake 并物化数据舰队 | Node + 本次物化的所有 Fleet |
| 独立批量去物化 | Node + 本次处理的所有 Fleet |

航行不预约源或目标。跨区拆成出发和抵达，不同时持有两端数分钟。

### 取得规则

`TryAcquireAll(operationId, keys)` 一次取得全部资源，失败时一个也不持有；资源键去重排序后检查登记，主线程内不跨帧、不调用外部回调。

禁止持有部分资源等待其余。Wake 先读候选舰队集合，尝试一次取得 Node 与全部 Fleet，再复检集合；不同则释放重试，不能拿着 Node 等 Fleet。

纯资源验证成功不代表 ECS 状态安全，仍需生命周期门禁、冻结和提交复检。

生命周期 owner 是当前 Node 预约的别名（见不变量 13），由 ResourceCoordinator 提供而非独立获取；Wake/Sleep/Materialization 子步骤继承同一 OperationId，MaterializationLease 是该 owner 下的休眠保护记录而非可等待的锁。

同一 Node 的战略操作首版串行，不同 Node 可交错。registry 服务可共享，但不存在全局 `_active`。

## 7. 操作状态、请求合并与调度

```csharp
public enum NsvOperationPhase
{
    Pending,
    WaitingResources,
    Preparing,
    Staging,
    Validating,
    Publishing,
    CleaningUp,
    RollingBack,
    Completed,
    Cancelled,
    Failed,
    RecoveryRequired,
}
```

操作记录至少包含：OperationId、类型、资源键、舰队/航次 ID、预期 revision、实例身份与 epoch、阶段、取消标志、临时实体集合、PublishState、不可变 PublishPlan、步骤日志、结构化失败原因、重试次数与时间戳。

重复 OperationId 返回已有 handle。语义合并另行处理：同 Node 的 Wake 合并；同一 Fleet/TravelId 的 Arrive 合并；同舰队同目标同版本的物化合并；不同目的地的重复出发返回 Busy/Conflict。

玩家 Wake/EntryIntent 优先于普通舰队工作，自动 Sleep 最低；为等待较久的任务提供公平调度。对已经持有竞争 Node 的低优先级操作实施协作让出：

| 阶段 | 高优先级玩家请求到来时 |
| --- | --- |
| Pending / WaitingResources | 取消当前尝试或延后调度；保留应重试的业务意图 |
| Preparing / Staging | 设置 YieldRequested，当前有界 chunk 完成后安全回滚并释放资源 |
| Validating 且 PublishState=NotStarted | 允许取消；写 InProgress 前最后检查取消/让出标志 |
| Publishing / InProgress 恢复 | 禁止抢占、禁止转交 owner，优先向前完成 |
| CleaningUp | 可靠隔离退休/临时对象后移交独立清理记录，释放不再需要的 Node |

让出不是强行释放：未隔离完的对象、未完成回滚仍受原 owner 保护（见不变量 11）。移交清理记录含旧 UID/generation 与操作 ID，防止新操作清理错对象。玩家请求可共享现有物化结果时，优先合并或提升其优先级，避免反复取消重建同一舰队。

等待结果公开 BlockingOperationId、阶段、等待时长、原因和 CanYield；不可拆分加载明确返回等待原因，不承诺固定延迟。取消低优先级尝试不得丢失已到期航次，玩家恢复后重新规划。

WorkScheduler 限制每帧加载、快照和清理预算，让其他资源操作取得进展。单次不可拆分的底层 grid 加载仍可能卡顿，预算调度不消除此风险。

同步回调只记录请求或失效信号，不递归推进另一操作的发布。涉及同一资源的子事务由父操作直接驱动，不能入队后再让父操作持锁等待它。

## 8. 物化与预备休眠的明确协议

### 两类保护必须分开

| 机制 | 意义 | 是否自动唤醒 |
| --- | --- | --- |
| ResourceLease | 战略修改独占权 | 否 |
| MaterializationLease | 保持已活动地图，阻止自动休眠 | 否 |
| MustRun/进入需求 | 玩家、FTL 或任务需要实时地图 | 按统一 RequestWake 协议处理 |

现有 RegisterMustRunTaskBlocker 会触发 Wake，因此不能直接用来代替非唤醒物化保护。

普通物化只能通过 `TryAcquireMaterializationLease` 在 Ready 上取得保护。它验证资源 owner、实例身份、epoch、map 未暂停和门禁。检查与登记不能跨帧；可能触发同步回调的后续步骤仍要复检。

取得后保护一直持续到完整发布或完整回滚；Sleep 资格检查必须将其视为硬 blocker。临时实体若回滚尚未隔离完毕，不能先释放保护。

### PreparingSleep 已先发生时

本版统一采用以下策略，替代此前“是否物化”存在歧义的表述：

1. 尚未创建目标实体的无人 AI 抵达，可只提交 AtNode 数据，不取消休眠。
2. PreparingSleep 内既有实体继续实时模拟；新 AtNode 舰队尚未进入战术场景，不参与实时或抽象战斗。
3. 若准备正常完成，它留在 Sleeping 节点等待以后物化。
4. 若玩家/任务取消准备，生命周期先关闭进入门禁，恢复协调器物化该节点的待进入舰队，再开放 Ready；不能简单改 Ready 后遗忘它们。
5. 准备恢复期间节点写入由同一 Node owner 串行；后到请求等待，然后按新状态重新判断。

这里将“战略抵达”和“战术可交互”明确区分。短暂数据驻留是显式规则；首版不允许其攻击、被攻击或领取实时遭遇收益。若玩法要求倒计时期间所有抵达立即参战，应改为显式取消准备后物化，不能两种语义混用。

### 冲突处理表

| 发生时机 | 处理 |
| --- | --- |
| 已持有物化保护，扫描尝试 PreparingSleep | 扫描保持 Ready，不启动 deadline |
| PreparingSleep 先发生，物化尚未开始 | 普通 AI 保持数据；玩家需求走恢复门禁 |
| 隔离预加载期间计划失效，未取得目标保护 | 丢弃临时计划或重规划，不修改目标 |
| 持有保护却发现 PreparingSleep | 协议异常；停止新写入，生命周期纠正状态并复检，不静默继续 |
| Sleep 已持有转换权或已提交 | 不抢占；等待完成后重新判断 |
| 物化已经发布 | 完成清理；不能取消已发布舰队 |

物化保护释放后重新派生休眠资格。不得遗留旧的已过期 deadline 导致刚完成物化就错误提交休眠。若持续物化使地图长期活动，报告原因；不能靠超时强制冻结中间状态。

### 节点级抽象战斗资格

不能仅以 Sleeping 或无人判定可抽象结算（见不变量 14）。只要节点存在任何 Live 表示（含暂停舰船）、尚未清理且可能交互的实体、实时 encounter、物化/去物化、InProgress 发布或未完成恢复，`CanResolveAbstractCombat(node)` 返回 false。该规则覆盖 Ready/PreparingSleep/Sleeping/Waking。

只有全部相关参与者均为纯数据、无实时引用和恢复工作、节点不处于转换时才可结算。结算须一次预约 Node 与全部参战 Fleet，发布前复检资格，不信缓存布尔标志；参与的普通模板舰/encounter 同样计入，不能只扫描战略舰队。混合节点新到的 AtNode 舰队只进待物化集合，不攻击、不被攻击、不领收益；冻结 Live 舰队不能仅在数据副本上扣血。

以后允许 Sleeping 节点战略战斗时，须先安全去物化全部实时舰队并适配 encounter 引用；不能安全转换时暂停结算。此规则不阻止独立的航行计时。

## 9. 原子性：准备、隔离、发布、清理

### 准备与隔离

验证舰队、航次、资源、位置计划和模板；加载全部临时 grid，设置阵营并验证恢复数据。临时对象不注册为正式舰队、不触发 encounter 目标登记、不允许 AI/物理/网络交互。

优先在隔离暂停环境生成。仅标记 paused 不能保证加载回调无副作用，必须审计 generator 和组件初始化路径，提供 staging 标记或等效隔离机制。未具备隔离能力前不得宣称支持跨帧原子物化。

实际加载放在 staging，不放在“保证成功”的发布步骤。最终位置、碰撞空间和动态约束在发布前复检；必要时重新选点。

### 可恢复发布日志

主线程短调用不能保证 ECS 原子性。本协议提供运行期可重放提交，不承诺进程崩溃或跨重启恢复。

```csharp
public enum NsvPublishState
{
    NotStarted,
    InProgress,
    Committed,
}
```

Operation 保存不可变 PublishPlan 和独立可变进度日志。PublishPlan 在写入 InProgress 前完整构造，至少包含：

- OperationId、PlanId、资源键与实例身份四元组；
- 每支舰队预期旧 revision、固定目标 revision、旧已提交快照；
- 目标 State、CurrentNodeId、Travel（含需移除的精确 TravelId）及目标疲劳等绝对值；
- 舰船目标状态、固定 generation、UID 与完整目标绑定集合；
- 节点派生索引的目标差异与可重建依据；
- 临时实体、旧实体/退休实体集合及其隔离/退休目标；
- 组件赋值、encounter 注册/移除目标及稳定幂等键；
- 提交后通知 outbox、需要激活的对象和清理任务。

Wake 涉及多支舰队时保存全部 FleetDelta，不能每完成一支就提前对外可见。进度日志包括每步 Pending/Done、最后错误和重放次数；日志是恢复线索，不取代逐步检查真实目标状态。

### 固定发布顺序与屏障

1. 在 NotStarted 完成所有业务验证、隔离检查及取消复检。相关 owner 持有完整资源；打开发布屏障，冻结会被切换权威的对象，准备好不可变计划和通知 outbox。
2. 先记录 PublishState=InProgress，再执行任何正式状态写入。从这一刻起不接受取消、不回滚到旧权威。
3. 幂等设置临时组件、generation、目标 parent/位置和隔离状态；确认旧对象已隔离。此阶段仍不可参与模拟或对外发布。
4. 通过 Registry 发布 API 设置目标 fleet/ship 记录和绑定；Revision 设置为计划中固定值，疲劳设置为目标绝对值，禁止 `++` 或 `+=` 重放。只移除计划匹配的航次。
5. 按目标 fleet 记录更新/重建节点索引，并以幂等键登记 encounter 目标关系。登记必须受屏障控制，不触发奖励、生成或战斗；无法适配的外部注册是开启该路径前的阻塞条件。
6. 逐项验证目标后置条件，最后记录 PublishState=Committed。这里是对外逻辑提交标记，不是解除暂停的指令。
7. 保持必要门禁，按可重试步骤完成实体激活、可见性与 Wake 的 unpause。完成一致性检查后开放读取/进入，排放提交通知，交接清理工作。

同步回调只能写入延迟事件记录，不能重新进入相关发布。正常读 API 在屏障存在时返回 Busy 或明确的旧已提交只读快照，禁止消费中间 ECS 字段；所有相关直接 UID 写入路径必须适配门禁。

步骤执行成功但 Done 尚未写入也可能抛异常，因此“已经执行过”不能仅依赖步骤位。每一步必须通过稳定键/目标值检查实现幂等；encounter 登记用 upsert，通知使用 `(OperationId, EventKind, Target)` 去重。没有幂等适配器的外部副作用不能靠 try/catch 获得一次执行语义。

### Reconciliation 与恢复方向

| PublishState | 恢复规则 |
| --- | --- |
| NotStarted | 可安全撤销准备，恢复旧已提交状态 |
| InProgress | 保留完整 owner、屏障与计划；逐步检查并向前执行到目标状态，禁止猜测回滚 |
| Committed | 不重做业务增量；补完激活、通知和清理，不再物化一份舰船 |

InProgress 异常将阶段标为 RecoveryRequired，但 PublishState 仍为 InProgress；同一 owner 重放，资源不先释放再由普通操作接管。遇到无法可靠恢复的缺失实体或不匹配的 revision/generation，保留隔离并报告人工修复，不覆盖未知新状态、不重载模板猜测补全。

Committed 后激活失败同样保留新权威，受控隔离并重试。日志至少保留至激活和清理完成并超过重试窗口；运行期日志丢失不是自动回滚的依据。

### 清理

旧实体在去物化发布前已停止交互，发布后成为退休对象。延迟删除失败可重试；其隔离必须独立于源 map 暂停，否则源 map 唤醒会让旧副本复活。

只要退休实体已可靠隔离，可释放无关资源让其他操作继续；否则保留相关门禁进入受控恢复。删除回调携带绑定代次和原因，不能当作新舰船战斗死亡。

## 10. 主要操作流程

### 10.1 ArriveFleet

请求携带 FleetId 与 TravelId。复检航次、目标、到期时间及 revision，取得 Fleet + 目标 Node。

| 目标状态 | 行为 |
| --- | --- |
| 无实例 / Sleeping | 原子提交 AtNode，不创建、不唤醒 |
| PreparingSleep | 按第 8 节提交待进入的 AtNode |
| Ready | 取得物化保护，staging 全部舰船，然后一次发布 Live 与到达结果 |
| Applying / Waking / 生命周期提交中 | 释放预约并等待转换，再重新判断 |
| Draining / Failed | 返回明确原因；不反复自动创建替代实例 |

活动目标物化失败且 PublishState=NotStarted：保持 Traveling 和原航次，不增加疲劳、不清空目的地、不递增 revision。ArrivalTime 已到表示待抵达，调度器按退避策略重试，不能每帧新建操作。

成功时才移除 Travel、加入目标驻留索引、累计一次航行疲劳、递增 revision。重复抵达通过 TravelId 与完成记录返回已有结果。

### 10.2 SleepSector

首版默认保留全部实体：取得 Node → 复检休眠资格和 deadline → LifecycleSystem 冻结 → 同步回调后复检 → 提交 Sleeping。

Sleep 不给自己注册 must-run 或物化 blocker。map 暂停与最终复检是一个不跨帧的短片段；不能让 PreparingSleep 在多个帧里处于 paused 状态。

提交前收到 Wake：取消 Sleep，恢复协调器处理待进入舰队后开放 Ready。提交后收到 Wake：Sleep 完成后执行 Wake。

舰队数据压缩不在此首版路径。以后可在已经 Sleeping 的地图上作为独立去物化事务执行；NotStarted 失败保留原实体和 Sleeping；InProgress 失败须先向前恢复，不能再回滚成原实体权威。普通暂停不依赖该能力。

### 10.3 WakeSector

读取需要物化的 AtNode 舰队集合，一次取得 Node + 全部目标 Fleet，并复检。已有 Live 舰队不重新生成。

Sleeping → Waking，map 保持暂停、门禁关闭；准备所有新实体，完成恢复验证，统一发布绑定与舰队状态，然后解除暂停，处理同步回调并最终开放 Ready。

此路径使用 Wake owner 内部授权，不调用只允许 Ready 的普通 MaterializationLease，也不排入依赖自身的子操作。

PublishState=NotStarted 时失败：清理本轮临时实体，数据舰队仍 AtNode，原实体不变，回到 Sleeping。InProgress 异常按第 9 节向前完成；Committed 后 unpause 或回调失败：不能把新 Live 舰队退回 AtNode；保持已发布实体，重新隔离地图并进入恢复状态，重试唤醒时复用它们。无法可靠隔离时标记 Failed 并拒绝进入，报告具体故障。

从 PreparingSleep 取消准备时，如果存在待进入舰队，同样进入有 owner 的恢复过程。可以先由生命周期暂停再分帧恢复；暂停前后复检玩家与迁移需求，等待方保持门禁，不允许已进入玩家被长期困在恢复过程。没有待进入舰队时可同步恢复 Ready。

需要全部舰队物化的 Wake 若遇到坏模板，首版明确返回失败并停止无界自动重试；不得静默漏掉敌方舰队后报告成功。管理员修复模板或显式移除故障舰队后可重试。之后若需要降级唤醒，应另定玩法规则。

### 10.4 DepartFleet

AtNode：取得 Fleet + 源 Node，验证航线，发布 Traveling、TravelId 和出发/到达时间，移出驻留索引。

Live：先验证出发资格并执行真实离场准备。FTL 预热期间可以记录舰队出发意图，但不得持有 Node 预约等待数分钟；需要维持运行时使用明确的可取消任务 blocker。真正离场提交前重新取得所需预约并验证状态。

必须检查玩家/mind/受保护角色、停靠/拖曳、任务引用、FTL 条件、快照能力和船体完整性。无法安全抽象的舰船返回 Unsupported，不能通过删除船体实现逃离。

冻结所选舰队并取得一致快照后发布 Traveling；随后隔离并清理旧实体。冻结方案须覆盖附属对象、全局任务与跨舰交互，不能假设停用 AI 就足够。该能力未实现时首版只允许纯数据舰队战略出发；真实舰船仍使用原有真实迁移流程。

## 11. 快照、引用和舰船边界

普通休眠保留实体，因此快照不完整不会阻止休眠。encounter 引用本身禁止删除目标，但只有其控制器必须持续运行时才要求 must-run blocker；可安全暂停的遭遇不应被永久保活。

去物化接口必须接受明确的舰队/舰船集合，不能只接受 sectorMap 然后隐式处理整张图。

任何被 encounter controller、objective、participant、PendingReturns、任务组件或其他尚未适配的原始 UID 引用持有的舰船及其被引用子实体，均禁止去物化。当前需审计 ObjectiveTarget、Participants、PendingReturns、EncounterMember.Controller，以及巡逻任务对 OwnedGrids 的查询。只检查 AI core 的 ObjectiveTarget 不足以放行。

允许去物化前必须建立引用适配器：把舰船关联映射到 FleetId/ShipId，把具体设备/子实体关联映射到可稳定恢复的子对象键，重新物化时做 remap 并验证。未知外部引用视为 Unsupported，不删除后再等扫描修复。这不阻碍前八阶段，只阻止第九阶段提前启用。

舰船与舰队的合法状态组合见第 4 节；Destroyed 需全部有效舰船确认损失。

快照能力分阶段：

1. Id/GridPath/Destroyed：用于首次物化与纯数据推演，不允许恢复已有真实舰船。
2. 抽象舰船状态：HullIntegrity、CombatPower、Supply 等；只有玩法明确允许抽象且存在恢复规则时才能使用。
3. 已声明支持的设备、库存、弹药、损伤和引用恢复；不支持的内容阻止去物化。

首版一个逻辑舰船对应一个 root grid。拆裂后不复制身份；保留主船身份并登记碎片，无法无损归属时禁止去物化。core 丢失不自动等价于全舰 Destroyed。俘获需要显式阵营/舰队转移；意外删除需区分战损、去物化清理和管理员行为。

验证必须双向进行：预期舰船找实体，实体找舰船。不能因为扫描不到某艘船，就默认它已摧毁并在下次重新生成。异常先隔离并报告。

## 12. API 与外部等待协议

接口草案：

```csharp
NsvOperationHandle RequestFleetDeparture(string fleetId, string destinationNodeId);
NsvOperationHandle RequestFleetArrival(string fleetId, Guid travelId);
NsvOperationHandle RequestSectorWake(EntityUid mapUid, WakeReason reason, EntityUid? requester);
NsvOperationHandle RequestSectorSleep(EntityUid mapUid);
NsvOperationHandle RequestCancelPreparingSleep(NodeKey node, WakeReason reason);
EntryIntentToken CreateEntryIntent(NodeKey node, EntityUid requester, EntryReason reason);
EntryIntentStatus GetEntryIntentStatus(EntryIntentToken token);
PromotionResult TryPromoteEntryIntent(EntryIntentToken token, SectorIdentity expected);
void CancelEntryIntent(EntryIntentToken token, string reason);
void ReleaseArrivalReservation(ArrivalReservationToken token, string reason);
AcquireResult TryAcquireAll(Guid operationId, IReadOnlyList<ResourceKey> resources);
AcquireResult TryAcquireMaterializationLease(Guid operationId, SectorIdentity sector);
bool TryGetOperationResult(NsvOperationHandle handle, out NsvOperationResult result);
```

请求接受状态区分 Accepted、Merged、Rejected；操作结果区分 Pending、Succeeded、Cancelled、Failed、RecoveryRequired，并记录是否已发布与是否仍有清理任务。

### EntryIntent 与 ArrivalReservation 分离

EntryIntent 是新的独立登记表，不能提前写入现有 `_arrivalReservations` 或 PendingArrivals，也不能被“缺少 FTLComponent 即 stale”的现有清理逻辑处理。

EntryIntent 至少记录 TokenId、Requester、NodeKey、请求原因、WakeHandle、CreatedAt、状态、取消原因，以及解析后的目标实例身份。实例尚不存在时身份可空；实例一旦解析，NodeKey/MapUid/InstanceGeneration 即固定，重建不得静默改绑。TransitionEpoch 不是 intent 的永久固定身份：由关联 Wake 在成功完成时返回 Ready 对应 epoch，升级时对该 epoch 复检；同一实例后续转换使它过期时重新等待/验证，不把合法 Wake 自己递增 epoch 当作实例重建。

生命周期为 `WaitingWake → ReadyToPromote → Promoted`，终态另有 Cancelled/Failed/Expired。FTL 启动前即可存在，不要求 FTLComponent；有效 intent 是休眠硬 blocker。创建接口先把 intent 放入生命周期可见的独立请求登记表，再提交 Wake；它不是 Node 排他锁，不等待持锁操作完成才表达进入需求。Sleep 冻结最终复检必须读取该登记表，不能依赖旧扫描缓存。

TryPromoteEntryIntent 由短生命周期操作取得 Node，复检 Ready、门禁、requester 和实例四元组，然后先建立 ArrivalReservation，再将 intent 标为 Promoted 并移除其 blocker，最后启动真实 FTL。全过程不能留下无 blocker 窗口。相同 token 重复升级返回同一 reservation。

ArrivalReservation 记录关联 IntentId、requester、实例身份与迁移 ID。FTLComponent 挂载之前处于显式 Starting 状态，不能被旧 stale 清理器删除；启动成功转 InFlight，与真实 FTL/迁移关联。启动失败立即释放，若短步骤中断则由 Starting 超时恢复清理；超时值须配置和测试。

取消/失败/完成均幂等释放：升级前释放 intent，升级后取消由迁移系统处理并释放 reservation，不能只删 intent 留下迁移。requester 删除、目标销毁和等待超时各有明确失败原因；WaitingWake 不因没有 FTLComponent 而失效。一个 token 的取消不取消其他等待者共享的 Wake。

RequestWake 从同步 bool 改为 handle：Accepted/Pending → Waking → 暂停下恢复 → 发布 → unpause → Ready → Succeeded。所有调用方必须适配 Pending；Succeeded 只能在门禁开放后返回，不能在 PublishState=Committed 时提前成功。

重连、玩家重新附着和管理员真实角色转移也需在进入前等待；只在 parent-change 事后观察到玩家不能保证异步门禁，需改造实际入口。周期扫描仅作兜底，不代替门禁。幽灵、相机和只读观察继续不触发 Wake。

单个等待者取消仅释放自己的意图；合并 Wake 有其他等待者时继续。所有等待者离开后，仅 PublishState=NotStarted 的恢复可请求取消；InProgress 必须向前完成，Committed 必须完成激活或受控恢复。

## 13. 故障、诊断与验收

### 失败分类

| 类型 | 策略 |
| --- | --- |
| Busy / 生命周期转换中 | 释放资源、等待状态通知并重试 |
| Revision/Epoch/实例身份变化 | 丢弃旧计划，重新规划 |
| 暂时加载失败 | 有上限的退避重试 |
| 坏模板 / 不支持快照 / 无法处理引用 | 明确失败，停止自动循环 |
| NotStarted 取消 | 完整回滚临时对象和保护 |
| 发布后清理失败 | 保留新权威，隔离旧对象，重试清理 |
| InProgress 不可预期异常 | RecoveryRequired，保留 owner/屏障，按 PublishPlan 幂等向前恢复 |

看门狗检测长时间 Staging、Publishing、CleaningUp；仅能取消可回滚阶段，不能超时删除预约让另一操作覆盖半完成发布。

诊断面板显示每个 Node/Fleet 的 owner、等待资源、阶段耗时、物化保护、进入意图、revision/epoch、航次、临时和退休实体数量、重试/恢复原因、重复或丢失绑定。操作结果记录保留策略须覆盖允许的请求重试窗口；回合结束统一清理。

### 必须覆盖的测试

1. A 星区物化分帧进行时，B 星区 Wake/Sleep 可继续推进。
2. 相同资源操作互斥，多资源预约失败不泄漏部分预约。
3. 重复 Wake、Arrive、物化请求不重复生成或累计疲劳。
4. 持有物化保护时扫描不能进入 PreparingSleep；发布/回滚后保护释放。
5. PreparingSleep 先发生，抵达保持数据；取消准备时待进入舰队被恢复协调器处理。
6. pause/unpause 同步回调改变 blocker 或提交请求，不造成递归发布或旧 epoch 提交。
7. 第 N 艘加载失败，前 N-1 艘不产生正式绑定或玩法副作用。
8. 活动目标抵达加载失败，原 Travel、节点、疲劳和 revision 保持不变。
9. 普通 sleep/wake 多轮后 UID、战损、库存和地图修改保持。
10. Wake 在 NotStarted 失败可回滚；InProgress 向前恢复；Committed 后 unpause 失败重试时不再次物化。
11. 退休 grid 延迟删除期间，即使源 map 解冻也不能重新运行。
12. 旧代次删除回调不影响新绑定；拆裂、俘获、异常删除不会让舰船复活。
13. FTL 等待、重连、取消和失败路径不泄漏进入意图/预留，未 Ready 不允许真实进入。
14. 无实例节点抵达与实例创建竞争时，节点索引和物化集合一致。
15. 战斗损伤发生在计划之后时，最终冻结快照能反映新状态。
16. 坏模板不会造成每帧无限重试；管理界面可定位阻塞舰队。
17. 每个发布步骤执行前后（包括完成副作用但未写 Done）注入异常；InProgress 重放最终达到同一目标，无重复疲劳、revision、generation、encounter 注册或通知。
18. Committed 标记后激活失败，重试不重建舰船；Wake 不提前返回 Succeeded。
19. 节点索引被损坏时从已提交 fleet 重建；InProgress fleet 不暴露中间位置；ActiveOperationId 错误不授予预约权。
20. Sleeping/PreparingSleep/Waking 的 Live+AtNode 混合节点禁止抽象战斗；纯数据节点取得资源并复检后才允许结算。
21. 不同 Node 的 Sleep/Wake 可同时处于跨帧阶段；扫描不绕过预约，生命周期 owner 永远等于 Node owner。
22. 玩家请求让出低优先级 Staging/Validating；InProgress 不抢占，可靠隔离的 CleaningUp 不长期占用 Node。
23. 无 FTLComponent 的 WaitingWake intent 不被 stale 清理；升级无 blocker 空隙，Starting 失败、重复升级和取消均无泄漏。
24. 同 Node 销毁重建后 generation 递增，旧回调/计划/token 被拒；sleep/wake 不递增实例代次。
25. Missing 舰船不自动重生；Unsupported 不抹掉其 Live 状态；任一未适配 encounter/任务 UID 引用阻止去物化。

## 14. 实施顺序

1. 建立稳定身份、节点索引、Live 绑定与 revision；明确 Live 可处于暂停地图。
2. 实现主线程 ResourceCoordinator、操作 handle、合并、状态通知与诊断。
3. 移除全局 `_sleepTransitionOwner` / `_wakeTransitionOwner`，统一为 ResourceCoordinator 的 Node owner；周期扫描只提交进入/取消 PreparingSleep 等意图，由持 Node 的生命周期操作复检和写入。保留普通 pause/unpause 状态保持。
4. 新增非自动唤醒的物化保护，接入所有休眠资格和提交复检路径。
5. 实现隔离生成、不可变 PublishPlan、PublishState 日志、幂等步骤和 reconciliation；通过逐步故障注入后才接首次物化。
6. 将同步 bool Wake 改为 handle，恢复完成后才 unpause；实现独立 EntryIntent、原子升级 ArrivalReservation、Starting 清理规则，并迁移全部调用方。
7. 实现纯数据 Depart/Arrive、TravelId、战略时钟和重复请求处理。
8. 接入工作预算、公平调度、故障注入和跨资源进展测试。
9. 在快照/引用能力满足后，另行启用已物化舰队去物化及真实离场转换。

## 15. 与持久星区文档的衔接

保持其普通休眠保存实体、只读观察不唤醒、OwnedGrids 不作为生命周期权威、软容量不驱逐地图等约束。

需要同步更新的接口契约如下，但本次不修改该文件：

- RequestWake 可异步完成；FTL/重连不能再假设同一调用栈必然 Ready。
- Waking 可以跨帧存在，保持门禁与暂停；解除暂停只在恢复发布之后。
- PreparingSleep 取消时，如有待进入数据舰队，先完成恢复协调再开放门禁。
- 硬 blocker 增加非自动唤醒的物化保护。
- 生命周期开始转换、实例创建销毁和节点驻留修改共享 Node 资源协调。

最终架构不要求所有操作排成一个全局顺序：冲突由资源归属决定，执行时间由工作预算分配，实体正确性由门禁、隔离、发布点和恢复协议保证。

## 16. 当前代码迁移检查表

以下路径均相对代码仓库；行号经本工作区源码核对（约 2026-09-20），可能随修改移动。此表区分现状与目标，不代表已实施。

| 审查指出的当前代码 | 必须完成的改造 | 验收条件 |
| --- | --- | --- |
| `NsvBluespaceSectorLifecycleSystem.cs:62–64,339–350,399–412` 的全局 `_sleepTransitionOwner` / `_wakeTransitionOwner` | 删除全局转换互斥；lifecycle owner 从 Node 资源归属取得 | 一个 Node 的恢复不拒绝其他 Node 的转换 |
| 同文件 `153–186` 扫描直接写 Ready/PreparingSleep | 扫描只派生 RequestSectorSleep/RequestCancelPreparingSleep；Sleep 请求内部区分进入倒计时与到期冻结，取得 Node 后重新计算资格 | 没有绕过 ResourceCoordinator 的生命周期写路径 |
| 同文件 `291–337,392–445` 同步 bool Wake，立即 unpause | 改成 handle；Waking 下保持暂停完成恢复，再发布、unpause、Ready | Pending 不被当成 Ready；恢复失败路径明确 |
| `NsvBluespaceSectorTravelSystem.cs:125–139,211–259,287–312` 同步 Wake 后预留，按缺少 FTLComponent 清理 stale | 新增独立 EntryIntent registry；升级后 reservation 有 Starting/InFlight 状态；清理按类型执行 | 等待 Wake 的 intent 不会被清掉，启动失败能释放 |
| `NsvBluespaceSectorInstanceComponent.cs:46–50` 只有 TransitionEpoch；`NsvBluespaceSectorSystem.cs:129–140,158–214` 用 `_activeNodeSectors` 保存实例 | Node registry 维护单调 InstanceGeneration，复制到 component，销毁保留代次计数 | 旧实例回调不能命中新实例 |
| `NsvFleetShip.cs:5–9` 只有 Id/GridPath | 新增存在状态、稳定 generation 与能力失败原因 | 缺失、确认损失、能力不支持可区分 |
| `NsvFleet.cs:12–14` 不可空 CurrentNodeId 和旧 DestinationNodeId | 迁移到第 4 节状态表，位置写入仅经 Registry | 无空字符串节点，无双目的地权威 |
| `NsvBluespaceEncounterComponent.cs:31–64` 保存 ObjectiveTarget/Participants/PendingReturns；EncounterMember 保存 Controller；`NsvBluespacePatrolContractSystem.cs:54–100` 查 OwnedGrids | 首版阻止这些未适配引用目标去物化；后续稳定键与子对象 remap 适配 | 不能以普通休眠已可用为由提前开启删除实体 |

生命周期相关文件位于 `Content.Server/_NSV/Bluespace/Sectors/`；舰队数据位于 `Content.Server/_NSV/Bluespace/Strategy/`；encounter 与巡逻任务位于 `Content.Server/_NSV/Bluespace/Encounters/`。

实现前必须审计全部 RequestWake、SetPaused、生命周期 State 赋值、fleet 位置赋值、进入预留、直接实体引用和 encounter 注册调用点。发布协议只对遵守屏障与幂等适配的路径成立，不能用文档声明代替调用方迁移。
