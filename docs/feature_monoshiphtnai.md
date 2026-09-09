# Mono 舰船 HTN AI 结构

## 范围

本文描述 Mono 的智能攻击舰船 AI，以 GUST 原有的 `NpcStationAiAttackerStaticSmart` 为例。它说明从 AI 核心、HTN 原型、目标查询，到驾驶、瞄准和开火的调用链。

NSV 的 `NsvBluespacePatrolCore` 保留这套 Mono 执行层，只替换根 HTN、攻击 compound 和目标查询原型；见文末“NSV 接入点”。

## 总体链路

```text
AI core entity
  └─ HTN.rootTask
      └─ AttackerShuttleSmartStaticCompound
          ├─ UtilityOperator(NearbyShuttleTargets)
          │   └─ 选择 ShipNpcTarget 实体，写入 Target / TargetCoordinates
          └─ ShuttleAttackSmartStaticCompound
              ├─ ShipMoveToOperator
              │   └─ ShipSteeringSystem → shuttle thrust / rotation inputs
              └─ ShipFireGunsOperator
                  └─ ShipTargetingSystem → FireControlSystem → GunComponent
```

HTN 规划与执行由通用 `HTNSystem` 调度；舰船移动、炮塔瞄准和发射均由 Mono 的专用 system/operator 执行。

## 1. 核心原型与根任务

GUST 的旧 map marker `SpawnMobAttackerCoreStaticSmart` 会生成：

```yaml
- type: entity
  id: NpcStationAiAttackerStaticSmart
  parent: [NpcStationAiAttackerSmart, BaseFactionGearOtherFactionT3]
  components:
  - type: HTN
    rootTask:
      task: AttackerShuttleSmartStaticCompound
```

位置：`Resources/Prototypes/_Mono/Entities/Mobs/NPCs/ai.yml:177-186`。

继承链提供核心的 HTN、供电、锚定和操舰所需的基础组件：

```text
NpcStationAiRammer
  └─ NpcStationAiAttackerSmart
      └─ NpcStationAiAttackerStaticSmart
```

`NpcStationAiRammer` 的定义位于同文件开头。静态智能攻击核心仅覆写 `HTN.rootTask`，不自行实现移动或武器逻辑。

## 2. HTN 原型

### 根计划

`AttackerShuttleSmartStaticCompound` 位于 `Resources/Prototypes/_Mono/NPCs/Shuttle/specific.yml:122-130`：

1. 用 `UtilityOperator` 执行 `NearbyShuttleTargets`；
2. 在成功选择目标后进入 `ShuttleAttackSmartStaticCompound`。

### 攻击计划

`ShuttleAttackSmartStaticCompound` 位于同文件 `:132-172`。它要求 blackboard 中有 `TargetCoordinates`，然后依次运行：

1. `ShipMoveToOperator`
   - 始终面向目标；
   - 规避 projectile；
   - 目标距离 `750`，容差 `150`；
   - 进入射程时最大速度 `4`；
   - `shutdownState: PlanFinished`，所以移动层会持续维持该距离。
2. `ShipFireGunsOperator`
   - `leadingAccuracy: 0.6`；
   - 目标 key 为 `TargetCoordinates`；
   - 内置 `UtilityService`，持续使用 `NearbyShuttleTargets` 刷新 `Target` 与 `TargetCoordinates`。

根计划只负责首次选目标；攻击计划内的 service 负责在战斗中重新选择目标。

## 3. 目标查询与目标语义

### 候选来源

`NearbyShuttleTargets` 定义在 `Resources/Prototypes/_Mono/NPCs/Shuttle/shuttle.yml:117-131`。它使用 `NearbyNpcTargetsQuery`，在默认 `4000` 范围内枚举带有 `ShipNpcTargetComponent` 的实体。

候选筛选实现：`Content.Server/NPC/Systems/NPCUtilitySystem.cs:540-560`。

会排除：

- 与 AI 同一个 grid 上的实体；
- 不符合 `ShipNpcTarget.NeedGrid` 的实体；
- 超过范围的实体；
- 标记 `needPower: true` 但未供电的实体；
- 所在 grid 被 blacklist 排除的实体；当前 blacklist 使用 `ShuttleAIIgnore` tag。

`NearbyNpcTargetsQuery` 本身明确标有“未来应使用 faction”的 TODO：`Content.Server/NPC/Queries/Queries/NearbyHostileShuttlesQuery.cs:8-19`。**当前 Mono 查询不判断阵营。**

### 控制台、人，还是 grid？

目标是一个带 `ShipNpcTargetComponent` 的**实体**，不是整艘 grid：

- 驾驶控制台 `BaseComputerShuttle` 带有该组件，且要求供电。`Resources/Prototypes/Entities/Structures/Machines/Computers/computers.yml:125-136`
- Mono 火控控制台也带有该组件且要求供电。`Resources/Prototypes/_Mono/Entities/Structures/Machines/FireControl/gunnery.yml:240-243`
- 人类基础原型也带该组件，但设置 `needGrid: NoGrid`。`Resources/Prototypes/Entities/Mobs/Species/base.yml:228-237`

因此，正常情况下 AI 更可能选择敌舰内的驾驶或火控控制台；脱离 grid、漂在太空中的角色也可成为候选。它不会因为“有人正在操作控制台”而获得额外优先级。

### 评分

`NearbyShuttleTargets` 使用两项 consideration：

- `TargetInverseDistanceCon`：距离越近分数越高；
- `TargetIsAliveOrNACon`：有 mob state 的目标必须存活；机器和控制台没有 mob state，天然通过。

评分实现位于 `Content.Server/NPC/Systems/NPCUtilitySystem.cs:309-324`、`:432-455`。任一 consideration 得分为零会淘汰该候选。

## 4. Blackboard 数据

`UtilityOperator` 将选中的实体及坐标写入 HTN blackboard：

```text
Target             EntityUid
TargetCoordinates  EntityCoordinates(target, Vector2.Zero)
```

`TargetCoordinates` 是目标实体的局部原点，不是 grid 中心、炮台位置或预先计算的拦截点。`UtilityOperator` 实现见 `Content.Server/NPC/HTN/PrimitiveTasks/Operators/UtilityOperator.cs:14-46`。

攻击计划的 `UtilityService` 用相同 key 刷新目标。若火控 operator 发现坐标已经变化，会结束当前任务，让 HTN 计划切换到新的目标。`ShipFireGunsOperator.cs:102-137`。

## 5. 移动与驾驶

`ShipMoveToOperator` 位于 `Content.Server/_Mono/NPC/HTN/Operators/ShipMoveToOperator.cs`：

1. 读取 `TargetCoordinates`；
2. 为 AI core 添加/更新临时 steering 状态；
3. 将配置的距离、速度、朝向、orbit、碰撞与 projectile 规避参数交给 `ShipSteeringSystem`；
4. 任务结束或计划被替换时停止 steering。

它默认要求 AI core 锚定；若 core 有 `ApcPowerReceiverComponent`，也要求其供电。

`ShipSteeringSystem` 位于 `Content.Server/_Mono/NPC/HTN/ShipSteeringSystem.cs`。它从 AI core 找到 parent grid，并将控制结果写入该 grid 的 shuttle input。目标必须与舰船在同一个 map；FTL 或跨 map 时不会继续判定为正常抵达。

## 6. 火控、瞄准与发射

`ShipFireGunsOperator` 位于 `Content.Server/_Mono/NPC/HTN/Operators/ShipFireGunsOperator.cs`：

1. 从 blackboard 读取 `TargetCoordinates`；
2. 在 AI core 上添加或更新 `ShipTargetingComponent`；
3. 设置 `LeadingAccuracy`；
4. operator shutdown 时移除 targeting component，停止 AI 火控。

`ShipTargetingSystem` 位于 `Content.Server/_Mono/NPC/HTN/ShipTargetingSystem.cs`。每帧它会：

1. 取得 AI core 的 parent grid 与目标实体；
2. 确认双方未删除、拥有物理组件且处于同一 map；
3. 目标在 grid 上时使用目标 grid 速度，否则使用目标实体速度；
4. 结合 `LeadingAccuracy` 平滑估计速度；
5. 周期性扫描本舰 grid 内的 `FireControllableComponent`；
6. 对可用炮计算提前量并调用 `FireControlSystem.AttemptFire()`。

炮必须仍存在、锚定且具有 `GunComponent`。Hitscan 会受最大距离限制；抛射物会在横向速度、拦截时间与寿命不满足时放弃开火。实际发射仍受舰船火控、武器供电、冷却和炮塔自身条件约束。

## 7. 通用 HTN 调度

通用 `HTNComponent` 保存 `rootTask`、计划冷却与重规划设置；`HTNSystem` 负责在 CPU job queue 中规划、执行当前 plan，以及在 plan 保留时运行 services。

关键文件：

- `Content.Server/NPC/HTN/HTNComponent.cs`
- `Content.Server/NPC/HTN/HTNSystem.cs`
- `Content.Server/NPC/Queries/UtilityService.cs`

无需为每种舰船重写此调度器。新的舰船行为通常只需要更换核心原型、compound、utility query 或 operator 参数。

## NSV 接入点

NSV 当前采用的分层是：

```text
_NSV
  NsvBluespacePatrolCore
  NsvBluespacePatrolCompound
  NsvBluespacePatrolAttackCompound
  NsvNearbyShipTargetsQuery

复用 Mono
  ShipMoveToOperator
  ShipFireGunsOperator
  ShipSteeringSystem
  ShipTargetingSystem
  FireControlSystem
  gun / turret / shuttle physics
```

相关文件：

- `Resources/Prototypes/_NSV/Bluespace/AI/ship_ai.yml`
- `Resources/SharedMaps/_NSV/Bluespace/gust.yml`

NSV 已通过 `NsvNearbyShipTargetsQuery` 完成 faction-aware 候选筛选：它保留 Mono 的距离、存活、grid、供电与 `ShuttleAIIgnore` 规则，并只接受 NSV relation resolver 判定为 hostile 的 `NsvShipTarget`。这不会修改 Mono 的通用查询、驾驶或火控系统。
