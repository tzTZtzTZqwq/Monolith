# 网格质量计入锚定物体（方案 A）设计文档

## 目标

让 grid（船体）的物理质量在“地块数量”之外，额外计入**锚定的物体**（墙体、机器等），
使更满载/更重装的船在 FTL、推进和机动上体现出真实的“更重”。

范围限定（本次只做这些）：

- 只计入 **锚定实体**（`Transform.Anchored == true`）且带物理 body、`FixturesMass > 0` 的实体。
- 典型对象：墙体（`Wall*`）、锚定机器/家具、锚定结构。
- **不计入** 非锚定的松散物品、生物、掉落物；它们本就随实体各自的 body 参与物理。

## 现状机制：为什么锚定物体现在不计入 grid 质量

1. grid 质量只来自**地块 fixture**。`ShuttleSystem.OnGridFixtureChange`
   （`Content.Server/Shuttles/Systems/ShuttleSystem.cs:121-128`）在 `GridFixtureChangeEvent`
   时，把每个地块 chunk fixture 的密度设为 `TileDensityMultiplier = 0.5f`
   （同文件 `:85`）。于是 `grid._mass = 0.5 × 地块总面积`。

2. 实体一旦锚定，`SharedTransformSystem.AnchorEntity` 会先把它的 body 设为
   `BodyType.Static`，其 fixture 仍留在**该实体自己的 body** 上，从不并入 grid body。

3. `SharedPhysicsSystem.ResetMassData`
   （`RobustToolbox/Robust.Shared/Physics/Systems/SharedPhysicsSystem.Components.cs:287-364`）
   只遍历 **grid 自身的 fixtures** 求和（`Σ density × area`）。锚定实体的 fixture 不在其中，
   所以永远不进入 grid 质量。

4. 读数差异（`PhysicsComponent.Physics.cs`）：
   - `Mass`（`:132`）对 `Static/Kinematic` body 返回 `0`，对 `Dynamic` 返回真实 `_mass`。
     飞行中的 shuttle 是 `Dynamic`（`ShuttleSystem.Enable` 设 `BodyType.Dynamic`），所以 FTL 读到的是真实值。
   - `FixturesMass`（`:125`）无视 body 类型，始终返回真实 `_mass`。撞击代码用它来读锚定实体的质量。

## 关键约束（决定实现方式）

- **`_mass` 是 `internal`（`PhysicsComponent.Physics.cs:134`），没有 public setter。**
  行内注释明确说明它被有意做成只读（直接改它不会同步更新惯性）。→ content 侧**无法**直接写 grid 质量。
- **`ResetMassData` 每次 fixture 变化都会从头重算 `_mass`**，任何手写的质量都会在下一次
  地块/fixture 变动时被抹掉，并同步刷新 `_invMass`（推进用）与惯性/质心。
- 结论：content 侧唯一稳定的杠杆是 **调整地块 fixture 的密度**。抬高地块密度后，
  `ResetMassData` 自然会把这部分额外质量算进 `grid._mass`，且 `_invMass`、惯性自动一致。

## 要订阅的事件

| 事件 | 触发时机 | 载荷 | 本方案用途 | 频率 |
| --- | --- | --- | --- | --- |
| `TransformComponent.AnchorStateChangedEvent`（by-ref，`TransformComponent.cs:589`） | 实体锚定/解锚，或被删除/移入 nullspace（`Detaching=true`） | `Entity`、`Transform`、`Anchored`、`Detaching` | 锚定→加质量；解锚/Detaching→减质量 | 玩家驱动，稀疏；建造/拆解时成簇 |
| `TransformComponent.ReAnchorEvent`（by-ref，`TransformComponent.cs:609`） | 锚定实体从一个 grid 重锚到另一个 grid | `Entity`、`OldGrid`、`Grid`、`TilePos`、`Xform` | 从 `OldGrid` 减、向 `Grid` 加 | 罕见（跨 grid 建造/对接） |
| `MassDataChangedEvent`（by-ref，`MassChangedEvent.cs:15`） | **已锚定**实体自身质量变化（如机器装填/清空） | `Entity<PhysicsComponent,FixturesComponent>`、`OldMass`、`NewMass`、`MassChanged` | 用 `NewMass-OldMass` 修正累计值 | 取决于内容；可作为二期精确化 |
| `GridFixtureChangeEvent`（**ShuttleSystem 已订阅**，`ShuttleSystem.cs:109`） | grid 地块 fixture 增删（挖矿、建造、受损） | `NewFixtures` | 重设基础密度时把已累计的锚定质量重新折算进密度 | 地块编辑时，中等 |
| `GridInitializeEvent` / grid 上线一次性扫描 | grid 加载完成 | grid uid | **一次性**遍历 grid 上已锚定实体，播种累计值 | 每 grid 一次 |

> 说明：地图加载时已经处于锚定状态的实体，`AnchorStateChangedEvent` 不保证补发，
> 所以必须有一次 grid-init 全量扫描来播种初值，之后再靠增量事件维护。

## 实现策略

### 数据

新增一个 server 端 system（建议 `Content.Server/_NSV/Shuttles/NsvGridMassSystem.cs`），
为每个 grid 维护一个累计器（组件或字典）：

```text
NsvGridExtraMassComponent { float AnchoredMass; }   // 挂在 grid 上
```

`AnchoredMass` = 该 grid 上所有“计入”锚定实体的 `FixturesMass` 之和。

### 计入判定

一个实体计入当且仅当：`Anchored == true` 且有 `PhysicsComponent` 且 `FixturesMass > 0`。
（墙体/机器满足；`ShuttleComponent` 网格本身、`MapComponent` 排除。）

### 注入：把 `AnchoredMass` 折算进地块密度

grid 的 `_mass = Σ fixture.Density × fixture.Area`。设地块总面积为 `A`，则令

```text
density = TileDensityMultiplier + AnchoredMass / A
```

对 grid 的每个地块 fixture 调用 `_physics.SetDensity(...)`（触发 `FixtureUpdate → ResetMassData`）。
`A` 可在同一次遍历里用各 fixture 的形状面积求和得到，无需额外簿记。

**关键落点**：把这段计算并入现有 `ShuttleSystem.OnGridFixtureChange`（或紧随其后执行的处理器），
这样每当地块变化重设基础密度时，会用当前 `AnchoredMass` 重新折算，避免被 `0.5` 覆盖。

### 各事件处理

- **AnchorStateChangedEvent**
  - `Anchored && !Detaching` 且实体计入：`AnchoredMass += FixturesMass`，重算密度。
  - `!Anchored` 或 `Detaching`：`AnchoredMass -= 记录的贡献`，重算密度。
    （建议在组件上记录每个实体上次计入的贡献值，避免解锚时质量已变导致减错。）
- **ReAnchorEvent**：`OldGrid.AnchoredMass -= m; Grid.AnchoredMass += m;` 两侧各重算一次密度。
- **MassDataChangedEvent**（二期）：若实体当前锚定，`AnchoredMass += (NewMass - OldMass)`，重算密度。
- **grid 上线**：遍历 grid 子实体，累加所有计入锚定实体，做一次密度折算。

### 质心与惯性

均匀抬高密度会把额外质量“摊”在全船，质心与真实分布略有偏差，但 `_invMass`/惯性由
`ResetMassData` 自洽计算，对 FTL/推进足够。如需更精确的质心，可改为**按 chunk fixture 分别注入**
（把每块锚定质量加到其所在 chunk 的密度上）——列为可选精度升级，不在首版。

### 备选：引擎 hook（不选，仅记录）

在 `ResetMassData` 求和末尾加一个 grid 级 `AdditionalMass` 再累加。更精确、更省事，
但需要改 vendored 的 RobustToolbox，增加上游合并成本。除非首版精度不达标，否则不采用。

## 性能预计

- **无逐帧开销**：不订阅 tick，纯事件驱动。
- **事件频率**：锚定/解锚是玩家动作，稳态下极稀疏；建造/拆解会成簇但量级小。地块变动（挖矿/建造/受损）中等。
- **单次事件成本**：一次密度折算 = 对 grid 地块 fixtures 做一遍 `SetDensity`，随后 `ResetMassData`
  再遍历一次 fixtures，合计 `O(gridChunkFixtures)`。大船的 chunk fixture 数量有限（按 chunk 而非按 tile），
  远小于地块数，单次仍是毫秒级以下。
- **grid-init 扫描**：每 grid 一次，`O(grid 上实体数)`，仅在加载时。
- **内存**：每 grid 一个组件（约一个 `float` 加可选的“每实体贡献”字典）。

主要风险点是“建造时成簇的锚定事件都触发全 grid 密度重算”。若实测有抖动，可**合帧**：
事件里只标脏，在 system `Update` 末尾对脏 grid 各做一次折算。首版可先不合帧，实测后再定。

## 下游影响（提高 grid 质量会改变这些）

- **FTL 资格与航程**：`ShuttleConsoleSystem.FTL.cs:191-192`，`body.Mass < ShuttleFTLMassThreshold`
  且航程随 `body.Mass` 缩放；更重的船更早触限/航程更短。
- **FTL 燃料/调整质量**：`ShuttleConsoleSystem.FTL.cs:231` 与 `ShuttleSystem.FasterThanLight.cs:698`
  的 `shuttlePhysics.Mass * drive.DriveMassMultiplier`。
- **FTL 硬上限**：`ShuttleSystem.FasterThanLight.cs:256` 的 `shuttlePhysics.Mass > FTLMassLimit`。
- **分裂碎片的雷达标签隐藏阈值**：`ShuttleSystem.IFF.cs:21-38`（`GridSplitEvent` 处理）。
  注意质量**不决定是否分裂**——分裂由引擎按几何连通性（`GridFixtureSystem.CheckSplit`）触发，与质量无关。
  此处只在分裂**之后**，对 `Mass ≤ HideSplitGridsUnder` CVar 的小碎片打 `IFFFlags.HideLabel`（雷达不显示名字）。
  抬高质量会让带锚定物的碎片更易超过阈值、更少被隐藏——纯雷达显示影响，不碰玩法。
- **推进/机动**：物理岛求解器用 `_invMass`（由 `ResetMassData` 设定）把推力冲量转成加速度，
  质量↑ → 同等推力下加速度/转向↓（这正是本方案想要的“真实感”）。
- **撞击 `Impact`：不受影响、也不会双计**。`GetRegionMass`（`ShuttleSystem.Impact.cs:346-370`）
  用的是 `ContentTileDefinition.Mass` + 实体 `FixturesMass` 的**独立**模型，不读 `grid._mass`，
  且本来就已经把锚定实体的 `FixturesMass` 算进去了。我们只改 grid body 的密度/`_mass`，与之解耦。

> 平衡影响：现有 shipyard 估价、`FTLMassLimit`、`splitMass` 都是按“仅地块”的旧质量调过的。
> 计入锚定物体后整体质量抬升，可能需要复核这些常量（尤其 `FTLMassLimit`），否则老船可能突然超限。
> 这属于数值调参，列入实现后的验证项。

## 验证计划

- **单元/集成测试**（`Content.IntegrationTests`）：
  - 空 grid vs 同 grid 锚定若干墙体后，`PhysicsComponent.Mass` 增量 ≈ 墙体 `FixturesMass` 之和。
  - 解锚/删除锚定实体后质量回落到基线。
  - 地块增删后，锚定质量仍保留（不被基础密度覆盖）。
  - `ReAnchor` 跨 grid 后两侧质量各自正确。
  - 已锚定实体质量变化（`MassDataChangedEvent`）后累计值修正（若做二期）。
- **游戏内 smoke**：给一艘船加装大量墙/机器，确认 FTL 航程/燃料、加速与转向随之变化；
  确认撞击表现不受本改动异常影响。

## 未决 / 范围外

- 只做墙体与锚定物体；非锚定松散物不计。
- 首版用均匀密度折算（质心近似）；按 chunk 精确注入列为可选升级。
- 引擎 `AdditionalMass` hook 作为备选，非首版。
- `FTLMassLimit` / `splitMass` / 估价等平衡常量的复核作为实现后的数值调参项。
