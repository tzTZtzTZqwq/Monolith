# NSV Ship AI 控制参考

本文档说明 NSV 独立 AI（`NsvShipAiSystem`）通过 **steering** 和 **targeting** 两个子系统实际能控制哪些东西，以及要新做一个 AI 策略需要改哪些内容。

所有字段名、行号对应当前代码，改动代码后请同步更新本文档。

---

## 1. 架构：决策 / 执行分离

```
NsvShipAiSystem (决策/生产者)
  每帧: 选目标 + 设战术参数
        │  _steering.Steer(core, coords)  ──►  ShipSteererComponent   (挂在 core 上)
        │  _targeting.Target(core, coords) ─►  ShipTargetingComponent (挂在 core 上)
        ▼
执行/消费者:
  · ShipSteeringSystem   —— 由 MoverController 每 tick 泵 GetShuttleInputsEvent 驱动，产出推力/转向/刹车
  · ShipTargetingSystem  —— 自己 Update() 遍历所有 ShipTargetingComponent，按每门炮弹道开火
```

- AI **从不**直接碰 thruster / velocity / 开火，只往两个组件写"意图"。
- 相关文件：
  - 决策：`Content.Server/_NSV/NPC/NsvShipAiSystem.cs`、`NsvShipAiComponent.cs`
  - 执行：`Content.Server/_Mono/NPC/HTN/ShipSteeringSystem.cs`(798 行)、`ShipSteererComponent.cs`
  - 执行：`Content.Server/_Mono/NPC/HTN/ShipTargetingSystem.cs`(207 行)、`ShipTargetingComponent.cs`

### 两个子系统的驱动方式不同（重要）

| | steering | targeting |
| --- | --- | --- |
| 挂载入口 | `Steer(core, coords)` | `Target(core, coords)` |
| 是否注册 pilot | **是**，`_mover.AddPilot` → `PilotedShuttleComponent.InputSources` | **否** |
| 谁触发计算 | `MoverController` 每 tick raise `GetShuttleInputsEvent`（多 pilot 输入求平均） | `ShipTargetingSystem.Update()` 自己遍历组件 |
| 停止 | `Stop(core)` → `RemComp<ShipSteererComponent>` | `Stop(core)` → `RemComp<ShipTargetingComponent>` |

### 调用契约

- `Steer` / `Target` 都要**每帧**用目标实时坐标重新调用（`NsvShipAiSystem` 就是这么做的），因为它们只是写入当前坐标；`InputSources` 是 `HashSet`，重复调不会重复注册。
- 没目标时调 `Stop`：steering 移除组件后，下一 tick MoverController 见 `GotInput=false` 会自动把 core 从 `InputSources` 摘掉。
- **不要**同时在一个 core 上挂 HTN 和 `NsvShipAi`：两者都会写同一套 steerer/targeting 组件互相打架。`NsvShipAiSystem.OnMapInit` 会 strip 掉继承来的 `HTNComponent`。

---

## 2. 通过 steering 能控制的东西

调用 `_steering.Steer(core, coordinates)` 返回 `ShipSteererComponent`，随后设其字段。所有默认值来自 `ShipSteererComponent.cs`。

### A. 核心战术（AI 常调）

| 字段 | 类型 | 默认 | 作用 |
| --- | --- | --- | --- |
| `Coordinates` | EntityCoordinates | (Steer 设) | 移动目标点 |
| `Mode` | ShipSteeringMode | `GoToRange` | 运动形状：`GoToRange` 到达即停；`Orbit`(CCW) / `OrbitCW` 绕圈**永不停** |
| `Range` | float | 5 | 期望距离；轨道模式下即轨道半径（取 `Range±RangeTolerance` 的中值） |
| `RangeTolerance` | float? | null | 距离容差带；null 时进 range 即判完成 |
| `InRangeMaxSpeed` | float? | null | 到达时允许的最大速度，null=不看速度 |
| `AlwaysFaceTarget` | bool | false | 始终把船头对准目标 |
| `InRangeRotation` | Angle? | null | 到达后转到某个**世界**朝向（判定优先于 AlwaysFaceTarget） |
| `FacingCoordinates` | EntityCoordinates? | null | **覆盖朝向**：朝这个点，而移动仍去 `Coordinates`（边退边打） |
| `TargetRotation` | float(度) | 0 | 朝向相对运动方向的偏移；broadside 侧身=90 |
| `OrbitOffset` | Angle | 30° | 轨道模式每帧沿圆周前进的角度（越大绕得越快） |
| `LeadingEnabled` | bool | true | 移动时是否匹配/预判目标速度 |

**Mode 语义**：mode 只决定"去哪个点"，其余（导航、避障、躲弹、转向、刹车）全在 mode 无关的 `ProcessMovement`（ShipSteeringSystem.cs:240）里。
- `GoToRange`（:194）：进入 range 带 + 速度够慢 + 朝向对 → `Status=InRange` 收工。
- `Orbit` / `OrbitCW`（:224-231）：取"目标→船"的方位、绕目标转 `OrbitOffset`、投影到半径 `midRange`，得到轨道上前方一点；`false` 表示永不完成。

### B. 规避 / 物理调参（一般用默认）

| 字段 | 默认 | 作用 |
| --- | --- | --- |
| `AvoidProjectiles` | false | 躲 shipgun 弹丸 |
| `AvoidCollisions` | true | 躲障碍 / grid |
| `AvoidanceNoRotate` | false | 规避时不因避障额外转向 |
| `BaseEvasionTime` | 4 | 即使静止也向前预判这么多秒的碰撞 |
| `EvasionSectorCount` / `EvasionSectorDepth` | 24 / 2 | 规避扇区数 / 层数 |
| `EvasionBuffer` | 3 | 把船体放大多少来算规避 |
| `ProjectileSearchBounds` | 896 | 搜弹半径 |
| `MaxObstructorDistance` / `MinObstructorDistance` | 800 / 20 | 障碍搜索远 / 近界 |
| `GridSearchBuffer` / `GridSearchDistanceBuffer` | 312 / 96 | grid 规避搜索扩展 |
| `EmpThreat` / `GridThreat` | 50 / 5 | EMP 弹 / 撞击威胁权重 |
| `BrakeThreshold` | 0.3 | 越高越不愿用刹车 |
| `AnchorMaxVelocity` | 5 | 低于此速不用 anchor dampening |
| `MaxRotateRate` | null | 判定"静止"的最大角速度 |
| `RotationTolerance` | 0.0333 | 朝向容差（rad） |
| `RotationCompensation` / `RotationCompensationGain` | 0 / 0.1 | 转向控制积分项，别手调 |

### C. 完成条件 / 只读

| 字段 | 默认 | 作用 |
| --- | --- | --- |
| `FinishOnCollide` | true | 撞到目标算完成 |
| `NoFinish` | false | 即使达成也不判完成（用于持续避障） |
| `Status` | Moving | 只读：`Moving` / `InRange` |

### 当前 `NsvShipAiSystem` 实际设了哪些

只设了：`Range`、`RangeTolerance`、`InRangeMaxSpeed`（来自组件 DataField）+ **硬编码**的 `AlwaysFaceTarget=true`、`AvoidProjectiles=true`、`Mode=GoToRange`（NsvShipAiSystem.cs:82-87）。其余全走组件默认。

---

## 3. 通过 targeting 能控制的东西

调用 `_targeting.Target(core, coordinates)` 返回 `ShipTargetingComponent`。控制面比 steering 小得多——它只做"对着一个共享坐标、按每门炮弹道开火"。

| 字段 | 类型 | 默认 | 作用 |
| --- | --- | --- | --- |
| `Target` | EntityCoordinates | (Target() 设) | 开火瞄准点 |
| `LeadingAccuracy` | float | 1 | 对 **on-grid** 目标的提前量精度（0..1，越高预判越准） |
| `OffgridLeadingAccuracy` | float | 1 | 对 **off-grid**（小/机动）目标的提前量精度 |
| `WeaponCheckSpacing` | float | 3 | 每隔多少秒重扫一次本 grid 的火炮 |

内部状态（**不是**控制项）：`CurrentLeadingVelocity`（速度估计）、`Cannons`（缓存炮列表）、`WeaponCheckAccum`。

**行为**（ShipTargetingSystem.Update / FireWeapons）：
- 每 tick 按 `leadingAccuracy` lerp 估计目标速度；每 `WeaponCheckSpacing` 秒重扫本 grid 的 `FireControllable` 炮。
- 对每门 **anchored** 炮独立算弹道：船线速度 + 该炮位置处的角速度分量、弹速、hitscan 最大射程、target-seeking 加速度、timed-despawn 寿命——够得着才 `AttemptFire`。
- 总闸：`FireControlSystem.CanFireWeapons(ship)` 为 false 则整船不开火。

**当前 `NsvShipAiSystem` 实际设了哪些**：只设了 `LeadingAccuracy`（NsvShipAiSystem.cs:92），没设 `OffgridLeadingAccuracy`。

---

## 4. 新做一个 AI 策略要做哪些内容

按"策略"差异的层级，代价从零代码到真逻辑递增。先判断你的策略属于哪一层。

### Tier 1 — 纯战术参数变体（轨道 / 贴脸 / 放风筝 / 不同交战距离）

steering 本身就支持（`Mode` + `Range` + `TargetRotation` + …）。但当前 `Mode` / `AlwaysFaceTarget` / `AvoidProjectiles` 在系统里**写死**，所以要先改成数据驱动，之后新策略 = 新 YAML 原型、零代码。

1. **组件** `NsvShipAiComponent.cs`（加 `using Content.Server._Mono.NPC.HTN;`）：
   ```csharp
   [DataField] public ShipSteeringMode SteeringMode = ShipSteeringMode.GoToRange;
   [DataField] public float TargetRotation = 0f;
   [DataField] public bool AlwaysFaceTarget = true;
   [DataField] public bool AvoidProjectiles = true;
   ```
2. **系统** `NsvShipAiSystem.cs:85-87` 改成透传 `ai.SteeringMode` / `ai.TargetRotation` / `ai.AlwaysFaceTarget` / `ai.AvoidProjectiles`。
3. **原型** 新增继承 `NsvBluespaceStandaloneCore` 的实体，只覆盖字段。例：轨道 broadside
   ```yaml
   - type: entity
     id: NsvBluespaceOrbitCore
     parent: NsvBluespaceStandaloneCore
     components:
     - type: NsvShipAi
       steeringMode: OrbitCW
       targetRotation: 90
       engageRange: 450
       engageRangeTolerance: 50
   ```
   （这正是 HTN 的 Patrol vs Broadside 的差别方式。）

### Tier 2 — 不同的选目标 / 开火决策（打最弱 / 威胁最高，而非最近）

需要代码，但局部。缝在 `NsvShipAiSystem.Decide()` 的打分行（NsvShipAiSystem.cs:117 `var score = -distSq;`）：加一个策略 enum DataField，`switch` 换评分函数（如读 `DamageableComponent` 算血量权重）。不碰执行层。

### Tier 3 — 有状态的行为（approach → brawl → retreat，按 hull / 护盾切换）

真正的 AI 逻辑，放在 `NsvShipAiSystem.Update()`：
- 组件加运行时状态字段（如 `public AiState State;`）。
- 每个 decision tick 读本船 `DamageableComponent`（hull）、Crescent `ShipShieldEmitterComponent`（护盾），据此翻 `Mode` / `Range`。
- **`FacingCoordinates` 在这里发挥作用**：血低时 `Coordinates` 设为远离威胁的点（撤退），同时 `FacingCoordinates` 仍指向目标（边退边开火）。
- 这是 `docs/nsv_ship_ai_v3.md` 的方向。

### 想加全新的运动**形状**（掠袭 / 8 字 / standoff-kite）

不是改 AI，而是在 `ShipSteeringSystem.ResolveDestination` 的 `switch (comp.Mode)`（:192）加一个 `case`，返回不同的目的地点。注意这在 `_Mono`（共享层），会影响所有用 steering 的东西（含玩家 HTN）。

### 什么时候才上重量级（可插拔 strategy 对象）

本仓库有这种模式（HTN operator / UtilityConsideration 都是 `!type:` 序列化类）。但除非有一堆策略要自由组合，否则先用 enum + `switch`；三个相似分支好过一个早产的接口。
