# NSV 持久星区休眠目标设计

## 目标与当前基线

目标是在**同一次服务器运行期间**保留已访问星区的 map、grid、entity、UID、库存、损伤和地图修改，同时暂停无人地图的大部分实时模拟。

当前代码已提供单向休眠生命周期：

- 创建并缓存 template/node sector，重复访问复用同一 map 和实体实例；
- FTL 只进入 `Ready` 目标，并维护 arrival reservation、返回地址和 faction 恢复；
- 普通空置不会自动销毁 cached sector，`TryDispose()` 仅用于明确的最终销毁；
- LifecycleSystem 每 5 秒通过 `AllEntityQueryEnumerator` 重建按 map UID 索引的 registry 快照；
- registry 聚合存活玩家、断线宽限期内角色、distinct AI 舰 grid 和玩家派系舰，并计算固定起点、只延长不缩短的动态 deadline；
- 空闲 `Ready` sector 会进入仍然运行的 `PreparingSleep`，deadline 到达后通过冻结前后两次权威复检并提交 paused `Sleeping`；
- 冻结失败会立即 unpause、恢复 `Ready` 并清空本轮时间窗口；
- 生命周期状态变化会刷新受影响 sector 内已打开的导航控制台；
- `nsvsectormonitor` 可查看现有 sector 状态、集合计数、休眠保持剩余秒数，以及总量/活跃量软容量。

当前 P4 已实现 `Ready → PreparingSleep → Sleeping → Waking → Ready`、must-run task blocker registry、统一 `RequestWake()`，并将缓存 sector 获取、FTL arrival、玩家重新附着/重连和存活角色跨 map 转移接入唤醒门禁。P5 验收聚焦多轮状态保持、失败注入、pause/unpause 路径审计和 display-only 容量监控；不要求额外的性能或 registry 扫描复杂度验证。以下内容同时记录当前行为与后续目标：

```text
周期 registry 扫描
→ ActiveLivingPlayers 派生计数
→ 动态 SleepDeadline
→ 冻结提交状态机
→ 统一 RequestWake() 解冻接口
```

本文不承诺跨服务器重启恢复，也不在首版实现后台舰队战斗、完整快照卸载或磁盘存档。

## 代码边界

当前相关代码：

- `Content.Server/_NSV/Bluespace/Sectors/NsvBluespaceSectorSystem.cs`
- `Content.Server/_NSV/Bluespace/Sectors/NsvBluespaceSectorLifecycleSystem.cs`
- `Content.Server/_NSV/Bluespace/Sectors/NsvBluespaceSectorTravelSystem.cs`
- `Content.Server/_NSV/Bluespace/Sectors/NsvBluespaceSectorInstanceComponent.cs`

目标职责划分：

| 系统 | 职责 |
| --- | --- |
| SectorSystem | 创建、装配、注册和最终销毁物理 sector |
| LifecycleSystem | 周期扫描、休眠资格、deadline，以及后续 pause/unpause、进入门禁和唤醒 |
| TravelSystem | 真实 shuttle FTL、返回地址、抵达预留和失败清理 |
| 后续 StrategicSystem | 仅处理 map 外纯数据舰队和世界时间，不直接修改实时 map |

`OwnedGrids` 是旧生成记录，将从归属和生命周期协议中废弃。它和 `OwnedEntities` 都不能作为玩家数量、休眠资格或舰船实际位置的权威来源；物理位置以 `Transform.MapUid` 为准。

## 引擎语义

休眠使用：

```csharp
_map.SetPaused(mapId, true);
_map.SetPaused(mapId, false);
```

原生 pause 会递归暂停 map transform 子树，普通实体查询、物理和多数实体系统会跳过暂停实体，因此可以停止大部分 AI、steering、碰撞和移动。

但 pause 不是全局事务：

- `AllEntityQuery` 和直接 UID 查询仍可访问暂停实体；
- 全局 timer、map 外 controller 和任务系统不会自动暂停；
- `PhysicsComponent.IgnorePaused` 可以绕过普通物理暂停；
- pause/unpause 会同步触发事件；
- 任意直接 `SetPaused(false)` 都可能绕过生命周期门禁。

因此必须审计上述路径。无法安全暂停的任务应注册专用 must-run blocker，而不是依赖地图碰巧持续运行。

## 核心不变量

1. `PreparingSleep` 期间地图仍然 unpaused；只有 `Sleeping` 的 map 才 paused。
2. `ActiveLivingPlayers > 0` 时不能进入或保持休眠准备。
3. 迁移提交、arrival reservation 和 must-run task blocker 是硬 blocker。
4. AI 舰和无人玩家派系舰只延长休眠前时间，不永久阻止休眠。
5. 所有实时访问都必须先通过 `RequestWake()`；调用方不得直接 unpause。
6. 冻结前重新计算权威状态，pause 同步回调后再次验证。
7. sleep/wake 保留同一 map 和实体实例，不重新运行模板，也不增加 `InstanceGeneration`。
8. 普通空置只能休眠，不能调用最终销毁。
9. 只读观察永不因观察本身 wake/unpause；观察者可以查看 paused sector 的冻结状态，但不属于实时模拟参与者。

## 生命周期状态机

```text
Applying → Ready

Ready
  └─ 周期扫描发现无活跃存活玩家且无硬 blocker
       → PreparingSleep

PreparingSleep
  ├─ 玩家回来或出现硬 blocker → Ready
  ├─ deadline 未到             → PreparingSleep
  └─ deadline 到达并通过冻结复检
       → Sleeping

Sleeping
  └─ RequestWake(reason) → Waking → Ready

Ready/Sleeping
  └─ 明确最终销毁授权 → Draining → 删除 map 与 instance entity
```

| 状态 | MapPaused | 外部实时写入 |
| --- | --- | --- |
| Ready | false | 经正常门禁允许 |
| PreparingSleep | false | 拒绝新进入；地图内既有模拟和 encounter 状态转换继续运行，玩家或硬 blocker 会取消准备 |
| Sleeping | true | 禁止 |
| Waking | true，直到恢复提交 | 排队，只有恢复协调器可写 |
| Draining | 终止流程 | 拒绝；销毁完成后 instance entity 不再存在 |

`TransitionEpoch` 只在冻结提交、wake 或最终销毁尝试时增加，用于拒绝旧回调；进入 `PreparingSleep` 本身不增加 epoch。

## 周期扫描

LifecycleSystem 使用可配置的 `LifecycleScanInterval` 扫描统一 sector registry，建议默认 5 秒。Sleeping map 不能依赖普通 `EntityQueryEnumerator` 发现，因为普通查询会跳过暂停实体。

每轮先单次遍历权威数据，按 `MapUid` 聚合：

```text
MapUid → ActiveLivingPlayers
MapUid → ActiveAIShips
MapUid → HasPlayerFactionShip
MapUid → MigrationOrArrivalBlockers
MapUid → MustRunTaskBlockers
```

成本应接近：

```text
O(Sectors + Sessions + AIShips)
```

禁止对每个 sector 分别遍历所有玩家和 AI 舰船。

扫描逻辑：

```text
Ready:
    玩家数 > 0 或有硬 blocker → 保持 Ready
    否则 → 计算 deadline，进入 PreparingSleep

PreparingSleep:
    玩家数 > 0 或有硬 blocker → 取消准备，回到 Ready
    deadline 未到 → 保持 PreparingSleep
    deadline 到达 → TryCommitSleep()

Sleeping:
    保持 paused 和 Sleeping，清除 countdown
    RequestWake() 立即执行 Waking → unpause → Ready
    若扫描发现玩家、arrival 或 must-run blocker，则调用 RequestWake(Reconciliation) 兜底
    若无 blocker 但被外部直接 unpause，则重新 pause，避免逻辑状态和 map 状态分裂
```

事件可以即时更新缓存或触发后续唤醒，但周期扫描和冻结前完整重算才是最终真相，避免漏事件导致永久不休眠或错误冻结。

## ActiveLivingPlayers

`ActiveLivingPlayers` 按 session 去重，并从控制关系、生命状态、连接状态和受控实体的实际 `Transform.MapUid` 派生，不能使用 HashSet 数量或简单进出事件累计代替。

| 玩家状态 | 阻止休眠 |
| --- | --- |
| 在线、存活并控制本地图内角色 | 是 |
| 昏迷、倒地但仍存活 | 是 |
| 跨地图远程控制本地图内存活单位 | 是 |
| 已死亡，尸体留在地图 | 否 |
| 普通幽灵、`AdminObserver`、`ReplayObserver`、`PreviewObserver` | 否；永不因附着、移动、跟随、传送或重连本身唤醒 |
| 管理员/监控相机、只读 `ViewSubscriptions`、remote eye | 否；只观察 paused sector 的冻结状态 |
| 掉线但角色仍存活 | 仅在 `DisconnectedPlayerGrace` 内 |
| 正在抵达本地图 | 由 arrival reservation 阻止 |

当前实现按 `NetUserId` 去重，只有 session attached、物理位于该 sector、非 ghost 且生命状态为 `Alive` 或 `Critical` 的真实游戏角色计入。dead、带 `GhostComponent` 的普通/管理员/回放观察者，以及无 qualifying `MobStateComponent` 的 `PreviewObserver` 均不计入；`AdminObserver.CanGhostInteract` 和 `Physics.IgnorePaused` 不会把观察者变成生命周期 blocker。断线时保存受控实体和时间，30 秒宽限期比较集中在单一函数中；每轮扫描重新检查该实体当前的生命状态和 `Transform.MapUid`。

只读 `ViewSubscriptions` 与 `EyeComponent.Target` 不改变受控角色的物理 sector，也不直接计入玩家数。管理员相机、监控相机、remote eye 和其他订阅视角可以查看 paused sector，但看到的是冻结 ECS/PVS 状态，订阅本身不得调用 wake。确实需要地图继续运行的管理工具或任务必须注册专用 must-run blocker 或通过合法调用方显式请求 `RequestWake()`。

## 休眠前时间计算

玩家数归零只表示可以进入 `PreparingSleep`，不表示立即 pause。

```text
SleepDelay = BaseSleepDelay
           + min(ActiveAIShips, AIShipCountCap) × PerAIShipDelay
           + (HasPlayerFactionShip ? PlayerShipDelay : 0)

SleepDelay = clamp(SleepDelay, MinSleepDelay, MaxSleepDelay)
SleepDeadline = SleepEligibleSince + SleepDelay
```

首版建议：

```text
LifecycleScanInterval = 5 秒
BaseSleepDelay        = 30 秒
PerAIShipDelay        = 10 秒
AIShipCountCap        = 12
PlayerShipDelay       = 90 秒
MinSleepDelay         = 30 秒
MaxSleepDelay         = 5 分钟
```

规则：

- `SleepEligibleSince` 在进入 `PreparingSleep` 时固定；
- 后续复杂度增加时可以把 deadline 延长为 `SleepEligibleSince + NewDelay`；
- deadline 不缩短，也不能改成 `now + NewDelay`；
- 所有延长都受 `MaxSleepDelay` 限制；
- 准备被取消后清除本轮时间，下次重新计算。

这避免 AI 数量抖动或持续生成实体形成无限滑动 deadline。

当前实现把 `ActiveLivingPlayers > 0` 和 pending arrival 作为 P3 硬 blocker。无 blocker 的 `Ready` sector 会立即进入未暂停的 `PreparingSleep` 并保留时间窗口；AI 舰按 distinct `GridUid` 计数，玩家派系舰按 grid 的当前 faction 判定，两者只延长 deadline。资格失效时恢复 `Ready`、清空时间，再次满足条件时从新的 `SleepEligibleSince` 起算。`ForeignGrids` 仅作为监控指标，不单独阻止休眠。

## 冻结提交

deadline 到达后执行 `TryCommitSleep()`。当前 P3 提交顺序为：

1. 确认 registry、map instance 和 `PreparingSleep` 状态仍然有效，并取得单一 transition owner 防止同步重入。
2. 从当前 session、断线宽限记录、角色生命状态、实际 `Transform.MapUid` 和 pending arrival 重新派生硬 blocker。
3. 若出现玩家或 arrival，保持 map unpaused，恢复 `Ready` 并清空时间窗口。
4. 确认 map 尚未暂停后调用 `SetPaused(true)`。
5. 处理同步 pause 回调，再次验证 transition owner、instance 状态、map paused、玩家和 arrival。
6. 若复检失败，立即 `SetPaused(false)`、恢复 `Ready` 并清空时间窗口。
7. 校验通过后提交 `Sleeping`，registry 在同一调用中同步更新、清除 countdown，并通知导航显示刷新。

该提交片段不能跨到下一次生命周期扫描。验证失败时保留原 map 和实体；不得删除地图后从模板重建来掩盖失败。P4 已引入 `TransitionEpoch`，冻结和 wake 尝试都会递增 epoch，并用 transition owner 与 epoch 拒绝同步回调中的过期提交。must-run task blocker 按 sector 和 owner 去重，注册时立即唤醒，注销后下一轮扫描可重新进入休眠准备。

## 统一解冻接口

所有玩家重连、FTL、管理员转移、任务实时化和需要真实场景的战略请求都必须调用：

```text
RequestWake(sectorId, reason, requester)
```

| 当前状态 | 结果 |
| --- | --- |
| Ready | 幂等成功 |
| PreparingSleep | 取消 deadline，恢复 Ready |
| Sleeping | 关闭门禁，进入 Waking 并执行恢复 |
| Waking | 合并到现有 transition，不重复 unpause |
| Applying | 排队或等待 Ready |
| Draining/Failed | 拒绝或返回失败 |

Sleeping 的恢复顺序：

1. 关闭进入门禁，增加 `TransitionEpoch`，提交 `Waking`。
2. 校验 map、registry、实体引用和等待请求。
3. 在 paused map 中完成允许的恢复工作。
4. 调用 `SetPaused(false)` 并处理同步 unpause 回调。
5. 提交 `Ready`，开放门禁并完成合并请求。

解冻由接口立即触发，不等待下一次周期扫描。状态事件只通知转换进度，不能成为第二套 pause/unpause 执行路径。当前 P4 的 map pause API 为同步调用，因此 `Waking` 通常只在同一调用栈内短暂存在；重入请求会合并到当前 wake owner，不会重复 unpause。

FTL 会以 shuttle 为 requester 请求 `Arrival` wake；只有目标同步提交 `Ready` 后才取得 arrival reservation、写入 travel 数据并启动真实迁移。失败或取消时必须释放预留。玩家重新附着/重连和存活 `ActorComponent` 的跨 map parent change 会立即请求 wake，周期扫描仍负责漏事件 reconciliation。管理员驱动真实 `Alive`/`Critical` 角色进入 sector 同样遵守该门禁；aghost 的移动、跟随、传送以及相机/remote-eye 订阅不代表真实角色进入，因此不得 wake。

## 保留、时间与销毁

休眠保留全部 map ECS 状态，包括 UID、tile、库存、弹药、护盾、设备损伤、位置和已删除对象的结果。未阻止休眠不表示可以删除对象。

默认语义：

| 内容 | Sleeping 时行为 |
| --- | --- |
| 普通 NPC、AI 战斗、火灾、设备过程 | 随 map 暂停 |
| 实体冷却和 lifetime | 应保持剩余模拟时间，需审计 pause-aware 实现 |
| 世界时间任务 | 由专门的 map 外系统结算 |
| 无法安全暂停的任务 | 必须注册专用 must-run blocker |

`TryDispose()` 与休眠不同，只能用于管理员放弃、回合结束或其他明确授权的最终销毁。销毁前仍需检查玩家、mind、迁移、任务引用和子树删除影响。

内存休眠不会释放大部分实体内存，因此 `nsvsectormonitor` 提供两个 server-only、archived 的 display-only 软容量：持久 sector 总数默认 100，活跃 sector 数默认 15。总数包含所有仍存在的 `NsvBluespaceSectorInstanceComponent`，包括 `Sleeping`；活跃数仅包含 `Applying`、`Ready`、`PreparingSleep` 和 `Waking`，排除 `Sleeping`、`Draining` 和 `Failed`。

超过软容量时，面板以警告颜色显示 current / maximum，但该值不得用于拒绝 sector 创建、缓存访问、wake、FTL 或其他生命周期操作。容量阈值只用于管理员观察；不得删除已有 sector，也不得通过从模板重建来替代持久世界。

## 验收测试

### 生命周期

- registry 扫描能发现 Ready、PreparingSleep 和 Sleeping sector；
- 多玩家只离开一部分时不进入准备；
- 昏迷玩家阻止休眠，尸体和所有只读观察者不阻止；
- `MobObserver`、`AdminObserver`、`ReplayObserver`、`PreviewObserver` 的附着、重连和跨 map 移动不会唤醒 Sleeping sector；
- 管理员相机/direct view subscription 和 remote-eye target 可建立观察，但 sector 保持 paused；
- 真实 `Alive`/`Critical` 角色的附着或物理进入仍立即唤醒；
- 断线宽限期、重连、复活、换角色和远程控制正确更新计数；
- 漏失事件后，周期扫描能从权威状态修正结果。

### Deadline 与 blocker

- 空地图、AI 舰和玩家派系舰得到正确 delay；
- AI 数量有 cap，deadline 有最小值和最大值；
- deadline 从固定起点有限延长，不滑动、不缩短；
- 玩家、迁移预留或 task blocker 会取消准备；
- pause 同步回调中新出现的 blocker 会撤销冻结。

### Wake 与迁移

- `RequestWake()` 对各状态符合表中语义，重复请求不会重复 unpause；
- 玩家重连、管理员移动和 FTL 不等待扫描周期；
- FTL 完成、失败、取消和重复回调都正确释放预留；
- Sleeping map 未 Ready 前没有外部实体写入。

### 状态保持与容量监控

- 多次 sleep/wake 后 map、UID、tile、库存、损伤和位置保持；
- 已删除、迁出和新增实体不会复活、残留或重复；
- 总量统计包含七种 lifecycle 状态，活跃量只包含 `Applying`、`Ready`、`PreparingSleep` 和 `Waking`；
- `Sleeping` sector 仍计入总量并显示在管理面板；
- 运行时 CVar 修改会反映到面板 current / maximum 摘要；
- 即使两个软容量均为 0，创建、sleep、wake 和 FTL 仍不受阻止。

## 实施顺序

1. **已实现：** registry 周期扫描和按 map UID 的基础状态聚合。
2. **已实现：** `ActiveLivingPlayers`、30 秒断线宽限、AI/玩家派系舰聚合和动态 deadline。
3. **已实现：** 未暂停的 `PreparingSleep`、自动 deadline 提交、实际 map pause、冻结前后复检和失败回滚。
4. **已实现：** 建立统一 `RequestWake()`、`Waking`/`TransitionEpoch`、must-run blocker，并迁移缓存获取、FTL、玩家重连与存活角色跨 map 转移路径。
5. **P5：** 补充多轮状态保持、wake 失败注入、pause/unpause 路径审计和 display-only 容量监控；不要求额外性能或扫描复杂度验证。
6. 废弃 `OwnedGrids` 的生命周期用途，并替换依赖它的玩法查询。

## 后置能力

以下能力不属于首版生命周期改造：

- map 外纯数据舰队演化和抽象战斗；
- 战略到达、逻辑驻留与实体物化；
- 删除物理 map 后的完整快照恢复；
- 跨服务器重启、跨版本存档和引用 remap；
- 保留真实 grid 的后台迁移和战损。

后续战略系统只能处理 map 外纯数据状态。实时 map 的 pause、wake、门禁和物化仍由生命周期系统负责；同一个 logical ship 不得同时存在纯数据和 live grid 两份可写表示。
