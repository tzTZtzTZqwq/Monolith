# NSV 新舰船 Standalone AI

## 范围与当前状态

本文描述新的 NSV 舰船战斗 AI：`NsvShipAiSystem` 与 `NsvShipAiComponent`。

该系统是服务器权威的 standalone 决策层，不使用通用 HTN planner。它负责：

- 基于 NSV faction 关系选择敌对目标；
- 感知本舰武器射程与护盾压力；
- 动态调整交战距离并在护盾崩溃时撤退；
- 根据敌群几何选择攻击侧位；
- 让同 faction 舰船形成隐式编队，分散目标与攻击角；
- 在配置的地图半径外逐渐偏向地图中心；
- 将移动、避障、弹道提前量和开火交给现有 Mono 舰船系统执行。

当前实现已经具备完整的独立运行能力和集成测试，但生产 sector 尚未全部切换到它：

- `gust.yml` 当前仍使用 `NsvBluespacePatrolCoreSpawner`，生成旧 NSV HTN core；
- `hunter.yml` 当前仍直接放置旧 HTN `NsvBluespaceBroadsideCore`；
- `NsvBluespaceStandaloneCore`、`NsvBluespaceStandaloneBroadsideCore` 与 standalone spawner 已定义，可供手动生成、测试或后续地图切换；
- 不应把“standalone 系统已实现”理解为“所有 NSV sector 舰船已经默认使用 standalone 系统”。

旧 Mono/NSV HTN 舰船 AI 另见 `docs/feature_old_monoshiphtnai.md`。

## 系统边界

总体调用链：

```text
NsvShipAiComponent（配置与运行时状态）
  └─ NsvShipAiSystem（感知、索敌、战术、编队）
      ├─ NsvBluespaceFactionSystem（阵营与敌对关系）
      ├─ ShipSteeringSystem（推力、旋转、保距、环绕、避障）
      └─ ShipTargetingSystem（逐炮提前量与开火）
          └─ FireControlSystem / GunComponent
```

`NsvShipAiSystem` 不直接：

- 控制 thruster；
- 修改 grid 速度或物理状态；
- 计算每门炮的最终拦截解；
- 绕过武器供电、冷却、射界或弹药规则；
- 重写 Mono 的碰撞与 projectile avoidance。

因此，新 AI 只替换“做什么”的决策层，继续复用 Mono 已有的“如何操船与开火”的执行层。

主要文件：

- `Content.Server/_NSV/NPC/NsvShipAiComponent.cs`
- `Content.Server/_NSV/NPC/NsvShipAiSystem.cs`
- `Content.Server/_NSV/NPC/NsvAiKeys.cs`
- `Content.Server/_NSV/NPC/HTN/NsvShipTargetComponent.cs`
- `Resources/Prototypes/_NSV/Bluespace/AI/ship_ai.yml`

## Core 原型

### 普通 standalone core

`NsvBluespaceStandaloneCore` 的核心配置为：

```yaml
- type: entity
  id: NsvBluespaceStandaloneCore
  parent: NpcStationAiAttackerStaticSmart
  components:
  - type: NsvShipAi
    blacklist:
      tags:
      - ShuttleAIIgnore
  - type: NsvBluespaceShipAiCore
  - type: NsvShipTarget
    needPower: true
```

父原型仍提供锚定、供电和 Mono 舰船 core 的基础组件。若继承链带入 `HTNComponent`，`NsvShipAiSystem` 会在 `NsvShipAiComponent` 启动时移除 HTN，避免两个系统同时向相同的 steering/targeting 状态写命令。

### Broadside standalone core

`NsvBluespaceStandaloneBroadsideCore` 仅通过数据覆盖普通 core：

```yaml
- type: NsvShipAi
  steeringMode: OrbitCW
  targetRotation: 90
  engageRange: 450
  autoEngageRange: false
  engageRangeTolerance: 50
  inRangeMaxSpeed: null
  leadingAccuracy: 0.4
```

它使用 Mono `ShipSteeringMode.OrbitCW` 环绕目标，并以 90 度朝向偏移进行侧舷作战。没有单独的 Broadside C# AI system。

### 测试 core

现有两个非战斗 smoke-test 原型：

- `NsvBluespaceSpinTestCore`：原地持续旋转，用于验证旋转控制链；
- `NsvBluespaceKeepDistanceTestCore`：与其他 grid 保持指定 hull-to-hull 距离，用于验证 AI 喂航点、Mono steering 执行的架构。

## 运行循环与更新频率

每个带 `NsvShipAiComponent` 的 core 在服务器 `Update()` 中依次执行：

```text
检查 core 所在 grid 与供电
  → 测试模式（如有）
  → 每 3 秒刷新本舰感知
  → 每 DecisionInterval 做目标与战术决策
  → 每帧追踪活动目标坐标
  → 向 ShipSteeringSystem / ShipTargetingSystem 写入命令
```

默认频率：

- 感知刷新：3 秒；
- 决策间隔：0.3 秒；
- 目标坐标追踪与 steering/targeting 参数更新：每帧。

昂贵的实体查询、武器扫描和威胁扫描不会全部放在每帧路径上。每帧路径主要读取缓存并合成导航坐标。

## 供电与停机

AI core 必须：

- 位于一个 grid 上；
- 在具有供电接收组件时处于供电状态。

core 离开 grid 或断电时，系统会：

- 停止 steering；
- 停止 targeting；
- 清除当前目标；
- 重置 decision/perception accumulator；
- 重置编队槽位和缓存威胁方向。

断电 core 不会加入其他舰船的隐式编队，也不会继续占用目标认领。

`NsvBluespaceStandaloneCore` 自身同时带有：

```yaml
- type: NsvShipTarget
  needPower: true
```

因此其 core 断电后也不再是其他 NSV AI 的有效目标。

## Faction 与地图上下文

### Faction 解析

`NsvBluespaceFactionSystem.TryGetFaction()` 按以下顺序解析：

1. 实体自身的 `NsvBluespaceFactionComponent`；
2. 实体所在 grid 的 faction；
3. 均不存在时视为无 faction。

给舰船设置 faction 时通常应设置在 root grid 上，这样舰内的 core、控制台和其他目标 marker 都能通过 grid fallback 获得相同 faction。

### 有效 faction map

双方只有处于同一个有效 faction map 时才能成为敌对目标。有效上下文有两种：

- 完整 `NsvBluespaceSectorInstanceComponent`；
- 普通地图上的轻量 `NsvBluespaceFactionMapComponent`。

普通地图不会为了启用舰船 faction 而伪装成完整 sector。管理员可对地图内实体或 grid 使用“Enable NSV Factions on Map”，之后再设置：

- `NSVPlayer`
- `NSVHostile`
- `NSVFederal`
- `NSVNeutral`

普通 faction map 使用 faction prototype 的默认关系；当前默认包括：

- `NSVPlayer ↔ NSVHostile`：敌对；
- `NSVFederal ↔ NSVHostile`：敌对；
- 未声明关系：中立。

完整 sector 还可以通过 relation override 临时覆盖默认关系。Standalone AI 会在下一次决策时读取最新关系；旧 HTN core 则会由 faction system 唤醒并重新规划。

## 目标组件与合法性

候选目标必须带有 `NsvShipTargetComponent`：

```text
NeedPower  是否要求目标实体供电
NeedGrid   OnGrid | Either | NoGrid
```

默认 `NeedGrid = OnGrid`。

Standalone AI 会排除：

- 已删除或正在删除的目标；
- 不符合 `NeedGrid` 的目标；
- 本舰 grid 上的目标；
- `NeedPower = true` 且断电的目标；
- 目标 grid 命中 `Blacklist` 的目标；
- 不在相同 map 的目标；
- faction resolver 未判定为 hostile 的目标；
- 超过 `SearchRange` 的目标。

默认 blacklist 排除带 `ShuttleAIIgnore` tag 的 grid。

### 目标身份

最终 steering/targeting 仍追踪具体的 `NsvShipTarget` 实体坐标，但评分、威胁去重和编队目标认领使用：

```text
目标在 grid 上 → grid UID
目标不在 grid 上 → 目标实体 UID
```

这避免一艘船上的多个 target marker 被当成多艘独立威胁，也避免多艘 AI 因选择同一 grid 上不同控制台而绕过编队目标分散。

## 目标评分

有效候选的基础评分为：

```text
score = (目标舰最长武器射程 + 100)
        / (距离² + TargetDistanceOffset)
```

默认 `TargetDistanceOffset = 50000`。

含义：

- 距离越近，评分越高；
- 目标舰武器射程越长，威胁价值越高；
- 无可扫描武器的目标仍保留基础值 100；
- 当前目标乘以 `TargetStickiness = 1.35`，减少近似目标间反复切换；
- 完全同分时选择较低 EntityUid，保证结果确定。

目标舰武器射程使用短期缓存，避免同一 grid 被多艘 AI 重复高频扫描。

## 武器射程感知

系统扫描本舰 grid 内锚定、可用且带 `GunComponent` 的 `FireControllableComponent`，取最长有效射程。

射程计算：

```text
Hitscan    = HitscanBasicRaycastComponent.MaxDistance
Projectile = Gun.ProjectileSpeedModified × TimedDespawn.Lifetime
```

弹匣/弹壳武器通过 `SharedGunSystem.GetBulletPrototype()` 解析实际发射物，而不是错误地读取 cartridge 外壳的生命周期。

如果没有可扫描武器：

- `CachedWeaponRange = 0`；
- 即使 `AutoEngageRange = true`，仍回退到固定 `EngageRange`。

## 护盾压力与软撤退

### Shield stress

每 3 秒扫描本舰 grid 内所有 `ShipShieldEmitterComponent`：

```text
stress = max(emitter.Damage / emitter.DamageLimit)
```

规则：

- stress 被限制在 `0..1`；
- 任一 emitter 处于 `Recharging` 时直接视为 `stress = 1`；
- 没有护盾发生器时 `stress = 0`；
- 当前没有可靠的 grid 级 hull damage 指标，因此船体损伤不参与 stress。

结果同时写入运行时缓存和 blackboard：

```text
ShieldStress
WeaponRange
Withdrawing
```

### 动态交战距离

当 `AutoEngageRange = true` 且存在可扫描武器时：

```text
engageRange = weaponRange × (RangeScale + stress × StressRangeScale)
```

默认值：

```text
RangeScale       = 0.6
StressRangeScale = 0.45
```

因此：

| Shield stress | 交战距离系数 |
| ---: | ---: |
| 0 | 0.600 |
| 0.5 | 0.825 |
| 0.8 | 0.960 |
| 0.85 | 0.9825 |

默认 `WithdrawStressThreshold = 0.85`，达到该值后会直接进入完全撤退，不再使用常规交战距离。

健康护盾船与无护盾船在该层行为相同。无护盾船的 stress 永远为 0，因此目前不会因船体受损触发软撤退或完全撤退。

### 完全撤退

达到撤退阈值后，AI：

- 停止争夺常规攻击侧位；
- 根据附近全部有效敌对 grid 的加权方向计算远离敌群的方向；
- 在该方向设置默认 `WithdrawDistance = 1000m` 的航点；
- 开启碰撞与 projectile avoidance；
- 保留当前目标坐标给 facing 与 targeting，继续边退边打；
- 对撤退航点继续应用地图软边界。

威胁权重使用：

```text
weight = 1 - (distance / ThreatMaxDistance) ^ ThreatDistancePower
```

默认：

```text
ThreatMaxDistance   = 1500m
ThreatDistancePower = 2
```

近处敌舰对撤退方向影响更大。

## 威胁几何与攻击侧位

每次决策会扫描 `ThreatMaxDistance` 内的合法敌对目标，并按 grid 身份去重。

缓存结果包括：

- `CachedThreatDir`：其他敌对 grid 的合成方向；
- `CachedWithdrawDir`：远离全部敌对 grid 的方向；
- `CachedOtherThreats`：除当前目标外的敌对 grid 数量。

对于 `GoToRange` 模式：

1. 两个或更多其他威胁：使用敌群合成方向作为侧位基准；
2. 一个其他威胁：使用本舰相对当前目标的方向作为基准；
3. 编队规模大于 1，即使只有当前目标，也使用目标相对方向；
4. 单舰且没有其他威胁：不创建侧位航点，保持直接接近目标的原行为。

侧位方向按下式旋转：

```text
attackAngle = OrbitSign × (90° + FleetAngleOffset)
```

生成的导航点为：

```text
targetPosition + attackVector × engageRange
```

舰船向该侧位航点移动，但 `FacingCoordinates` 与 targeting 仍指向真实目标，而不是侧位点。

`Orbit`/`OrbitCW` 模式不会使用这套 `GoToRange` 侧位航点；它们直接把真实目标交给 Mono orbit steering。隐式编队的目标分散仍然适用，但 `FleetAngleOffset` 不改变当前 Orbit/OrbitCW 的环绕槽位。

## 隐式编队

系统不创建 fleet entity。每个 standalone core 在自己的决策 tick 中构建局部编队视图。

编队成员必须：

- 位于同一 map；
- 能解析到相同 faction；
- 距离不超过本舰 `FleetRange`；
- core 仍存在且处于供电状态。

默认 `FleetRange = 2500m`。

### UID 槽位

成员按 EntityUid 建立稳定顺序：

```text
FleetIndex = 比本舰 UID 更低的成员数量
FleetSize  = 当前局部编队成员数
```

攻击角偏移：

```text
FleetAngleOffset = clamp(
    (FleetIndex - (FleetSize - 1) / 2) × FleetSpreadStep,
    -FleetSpreadMax,
    +FleetSpreadMax)
```

默认：

```text
FleetSpreadStep = 35°
FleetSpreadMax  = 130°
```

三舰编队的默认偏移为：

```text
-35° / 0° / +35°
```

### 目标认领降权

为避免所有舰船集中攻击同一目标，较高 UID 舰船会读取较低 UID 编队成员已经锁定的目标：

```text
score /= 1 + claimed × FleetTargetPenalty
```

默认 `FleetTargetPenalty = 0.75`。

只考虑较低 UID 成员可以建立确定的优先顺序，避免两艘船同时因为对方的选择而反复换目标。该分配不是中央调度器，通常会在若干错开的决策周期内收敛。

## 地图软边界

地图可带有 `NsvShipAiMapComponent`：

```text
LeashRadius   可空；null 表示完全不限制
LeashStrength 默认 0.6
```

只有舰船到地图坐标 `(0,0)` 的距离超过 `LeashRadius` 时，软边界才生效。

对于仍有目标的舰船：

```text
leashWeight = clamp(
    LeashStrength × (distance - LeashRadius) / LeashRadius,
    0,
    1)

navigationWaypoint = lerp(
    tacticalWaypoint,
    mapCenter,
    leashWeight)
```

行为特征：

- 刚越过半径时只有轻微向心偏置；
- 越远时向地图中心的权重越高；
- 不是瞬间传送或硬速度限制；
- targeting 和开火仍使用原战斗目标；
- `AlwaysFaceTarget` 开启时仍朝向原目标；
- 撤退航点也受相同边界影响。

没有活动目标且舰船位于边界外时，AI 直接向地图中心导航，并以 `LeashRadius` 作为停止距离；位于边界内且没有目标时则停止 steering。

配置来源：

- 普通地图通过管理员启用 NSV faction 时，如果尚未设置半径，自动使用 3000m；
- sector template 可配置 `shipAiLeashRadius` 与 `shipAiLeashStrength`；
- 当前 NSV sector template 均显式配置 `shipAiLeashRadius: 3000`；
- template 未提供半径时，不创建限制，符合“没有获得值就不限制”。

## Steering 与朝向

正常作战时交给 `ShipSteeringSystem` 的主要参数：

- `Range` / `RangeTolerance`
- `InRangeMaxSpeed`
- `AlwaysFaceTarget`
- `AvoidProjectiles`
- `TargetRotation`
- `ShipSteeringMode`
- 可选 `FacingCoordinates`

`AlwaysFaceTarget = true` 且 `TargetRotation = 0` 表示舰首对敌；`TargetRotation = 90` 表示侧舷对敌。

当侧位航点或地图软边界改变移动目标时，系统会把 `FacingCoordinates` 单独保留为真实目标，从而避免舰船错误地朝向导航航点。

实际推力、旋转 PID、碰撞规避和 projectile avoidance 仍完全由 Mono steering 处理。

## Targeting 与开火

活动目标每帧以实时 `EntityCoordinates` 交给 `ShipTargetingSystem`。AI 只配置：

```text
LeadingAccuracy
```

默认 `LeadingAccuracy = 0.6`。

后续流程仍由 Mono 系统负责：

- 读取目标与目标 grid 速度；
- 平滑估计速度；
- 为每门炮计算拦截提前量；
- 检查 hitscan 或 projectile 是否能在有效时间内命中；
- 调用 `FireControlSystem.AttemptFire()`；
- 服从武器供电、弹药、冷却与炮塔限制。

新 AI 没有复制一套独立火控，也不直接生成 projectile。

## 配置表

### NsvShipAiComponent

| 字段 | 默认值 | 含义 |
| --- | ---: | --- |
| `SearchRange` | 4000 | 索敌半径 |
| `Blacklist` | 空 | 排除目标 grid 的 whitelist/blacklist 配置 |
| `EngageRange` | 750 | 关闭自动射程或无可扫描武器时的固定距离 |
| `EngageRangeTolerance` | 150 | 距离容差 |
| `InRangeMaxSpeed` | 4 | 判定抵达交战距离时允许的最大速度；null 表示不限制 |
| `LeadingAccuracy` | 0.6 | 交给 targeting 的提前量平滑精度 |
| `SteeringMode` | `GoToRange` | 保距、Orbit 或 OrbitCW |
| `TargetRotation` | 0 | 相对目标的朝向偏移角 |
| `AlwaysFaceTarget` | true | 机动时是否持续朝向目标 |
| `AvoidProjectiles` | true | 是否启用 projectile avoidance |
| `AutoEngageRange` | true | 是否根据武器射程和护盾压力计算距离 |
| `RangeScale` | 0.6 | 无护盾压力时的武器射程系数 |
| `StressRangeScale` | 0.45 | 最大护盾压力带来的额外射程系数 |
| `WithdrawStressThreshold` | 0.85 | 切换完全撤退的压力阈值 |
| `ThreatMaxDistance` | 1500 | 威胁方向扫描距离 |
| `ThreatDistancePower` | 2 | 威胁距离衰减指数 |
| `WithdrawDistance` | 1000 | 撤退航点距离 |
| `DecisionInterval` | 0.3 | 决策间隔 |
| `TargetStickiness` | 1.35 | 当前目标评分保持倍率 |
| `TargetDistanceOffset` | 50000 | 目标距离评分分母偏置 |
| `OrbitSign` | 1 | 攻击侧位旋转方向，+1 逆时针、-1 顺时针 |
| `FleetRange` | 2500 | 隐式编队成员搜索距离 |
| `FleetTargetPenalty` | 0.75 | 每个较低 UID 认领者造成的目标降权 |
| `FleetSpreadStep` | 35 | 相邻编队槽位角度间隔 |
| `FleetSpreadMax` | 130 | 最大槽位偏移角 |
| `Blackboard` | 空 | 策略扩展与运行时状态存储 |
| `TestSpinSpeed` | null | 测试用固定旋转速度 |
| `TestKeepDistance` | null | 测试用 grid 间最小间距 |

### NsvShipAiMapComponent

| 字段 | 默认值 | 含义 |
| --- | ---: | --- |
| `LeashRadius` | null | 地图软边界半径；null/非正值表示不限制 |
| `LeashStrength` | 0.6 | 超出边界后向中心插值的强度 |

## Blackboard

当前 canonical key 定义在 `NsvAiKeys`：

```text
ShieldStress  float，本舰护盾压力 0..1
WeaponRange   float，本舰最长武器射程
Withdrawing   bool，是否达到完全撤退阈值
```

当前核心战斗流程直接读取组件缓存；blackboard 保留这些状态是为了后续策略、人格或外部调试扩展。YAML 可预置其他值，但异构值必须带显式类型标签。

## 管理员测试流程

在普通地图测试 standalone AI：

1. 对地图中的实体或 grid 打开管理员 verbs；
2. 选择“Enable NSV Factions on Map”；
3. 将玩家舰 grid 设置为 `NSVPlayer`；
4. 将 AI 舰 grid 设置为 `NSVHostile`；
5. 在 AI 舰上生成 `NsvBluespaceStandaloneCore`；
6. 确保 core 锚定并供电；
7. 确保目标舰上存在供电的 `NsvShipTarget` marker；
8. 观察索敌、保距、侧位、开火和软边界行为。

若只刷 core 而未启用 faction map 或未给双方 grid 设置敌对 faction，AI 不会获得合法目标。

## 测试覆盖

`Content.IntegrationTests/Tests/_NSV/NPC/NsvShipAiFleetTest.cs` 当前覆盖：

- 同 map、同 faction core 的 UID 槽位与攻击角展开；
- 异 faction、异 map、超出 FleetRange 的成员排除；
- 多舰对等目标分配；
- 断电 core 停机且不加入编队；
- `needPower` 目标断电后被排除；
- cartridge 武器通过实际 projectile lifetime 计算射程；
- `weaponRange × (0.6 + 0.45 × stress)` 交战距离公式；
- map leash 未配置时不改变行为，配置后渐进偏向中心；
- 无目标时返回地图边界；
- 单舰、无其他威胁时保持直接接近。

`Content.IntegrationTests/Tests/_NSV/Bluespace/Sectors/NsvBluespaceSectorSystemTest.cs` 还覆盖：

- 普通地图管理员 faction backend 会添加轻量 map marker；
- 普通 faction map 默认获得 3000m leash；
- sector template 的 leash 配置写入 map component；
- sector 内 faction、AI core 与地图生命周期不回归。

最近一次定向验证结果：

```text
Content.Server Debug isolated build: 0 errors
NsvShipAiFleetTest: 9 passed, 0 failed
```

默认服务器输出当时被正在运行的 `Content.Server` 锁定，因此验证使用系统临时目录的隔离 build artifacts，没有终止用户的服务器进程。

## 已知限制

1. **生产 sector 尚未默认切换**
   - GUST 与 Hunter 当前仍使用旧 NSV HTN core；standalone 原型主要用于测试与后续接入。

2. **无 hull stress**
   - grid 没有可直接使用的统一 `DamageableComponent`；无护盾船不会因船体损伤撤退。

3. **隐式编队不是中央 fleet controller**
   - 没有编队实体、leader、cohesion、队形模板或集中式 Hungarian 分配；每艘船按本地范围和 UID 独立收敛。

4. **Orbit 模式没有独立角槽**
   - FleetAngleOffset 当前只参与 GoToRange 的攻击侧位航点；Orbit/OrbitCW 继续由 Mono 绕真实目标运行。

5. **目标评分维度有限**
   - 当前只考虑目标武器射程、距离、stickiness 和 fleet claims；没有 hull、任务优先级、火力状态或目标类型权重。

6. **软边界中心固定**
   - 当前中心是 map coordinate `(0,0)`，不是动态战场中心、玩家舰位置或 sector 模块质心。

7. **执行能力受 Mono 系统约束**
   - AI 能否转向、避障、追上目标或开火，最终仍取决于舰船 thruster、质量、供电、炮塔射界和 Mono steering/targeting 的能力。

## 后续接入建议

将 sector 舰船切换到 standalone AI 时，应显式修改对应 map/core 引用并重新验证 encounter：

```text
gust.yml
  NsvBluespacePatrolCoreSpawner
    → NsvBluespaceStandaloneCoreSpawner

hunter.yml
  NsvBluespaceBroadsideCore
    → NsvBluespaceStandaloneBroadsideCore
```

切换时必须确认：

- 每艘目标舰仍只有一个可作为 encounter objective 的 `NsvBluespaceShipAiCoreComponent`；
- core 仍有正确的 `NsvShipTarget needPower`；
- encounter 摧毁 core 的完成判定不受影响；
- map 初始化后不存在旧 HTN core 或旧 spawner；
- GUST、Hunter 的武器射程与战术参数分别适合新公式；
- 多舰、单舰、无目标、断电和 sector 回收路径全部通过。
