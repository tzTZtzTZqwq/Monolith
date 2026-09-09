# NSV 蓝空间扇区系统

## 范围

本文件描述目前位于 `_NSV` 命名空间与资源目录中的蓝空间扇区系统。它是独立于 Salvage、主游戏地图生命周期、通用 NPC 阵营及目标选择系统的增量功能。

系统可按需创建临时蓝空间 map，在其中生成模块化内容，并通过正常 Shuttle FTL 流程让外部舰船进入、在相邻战略节点间航行或返回原出发地。固定的 YAML 星图定义节点、图连接、威胁/奖励/燃料元数据、阵营、内容模板、encounter pool 与稳定 seed；扇区无人使用两分钟后自动回收。

## 目录

```text
Content.Shared/_NSV/Bluespace/
  Sectors/
    NsvBluespaceSectorPrototypes.cs          扇区、模块、生成器与阵营原型定义
    NsvBluespaceFactionComponent.cs          grid / entity 的 NSV faction membership
    NsvBluespaceShipAiCoreComponent.cs       NSV 舰船 AI 核心标记
    NsvBluespaceNavigationConsoleUi.cs       导航控制台网络 UI 消息
    NsvBluespaceNavigationCartridgeUi.cs     NSV 导航 PDA 程序的只读 UI 状态
  Starmap/
    NsvBluespaceStarmapPrototypes.cs         固定星图、节点定义与图验证
  Encounters/
    NsvBluespaceEncounterPrototypes.cs       PatrolContract encounter 原型定义

Content.Server/_NSV/Bluespace/
  Sectors/
    NsvBluespaceSectorLayoutPlanner.cs       确定性模块位置规划
    NsvBluespaceSectorSystem.cs               map 创建、生成调度、缓存和销毁
    NsvBluespaceFactionSystem.cs              faction 解析、关系覆盖与 patrol 重规划
    NsvBluespaceSectorInstanceComponent.cs    每个扇区 map 的运行时状态
    NsvBluespaceSectorTravelSystem.cs         FTL 进入、返回与无人回收
    NsvBluespaceNavigationConsoleSystem.cs    控制台请求处理
    NsvBluespaceNavigationCartridgeComponent.cs  NSV 导航 PDA 程序标记
    NsvBluespaceNavigationCartridgeSystem.cs  向 PDA 程序推送只读快照与完成通知
    NsvBluespaceJumpPointComponent.cs         跳跃点的 sector template 配置
    Generators/
      NsvBluespaceAsteroidFieldGenerator.cs  陨石场生成
      NsvBluespaceStationGenerator.cs        station grid 生成
      NsvBluespaceShipGenerator.cs           ship grid 生成
      NsvBluespaceEntityGenerator.cs         单实体生成
  Encounters/
    NsvBluespaceEncounterComponent.cs        controller、成员 marker 与状态数据
    NsvBluespaceEncounterSystem.cs            通用合同生命周期与撤离授权
    NsvBluespacePatrolContractSystem.cs       GUST 目标选择与 patrol core objective

Content.Client/_NSV/Bluespace/Sectors/
  NsvBluespaceNavigationConsoleBoundUserInterface.cs
  NsvBluespaceNavigationConsoleWindow.xaml / .xaml.cs
  NsvNavigationUi.cs                          PDA 程序 UIFragment
  NsvBluespaceNavigationCartridgeUiFragment.xaml / .xaml.cs

Resources/Prototypes/_NSV/Bluespace/
  AI/ship_ai.yml                              NSV 巡逻舰 HTN 计划与核心原型
  Sectors/sectors.yml                         测试扇区、station、ship、asteroid-field 模块
  Starmap/starmap.yml                         固定战略星图与节点图连接
  Factions/factions.yml                       NSV faction ID 与定向默认关系
  Encounters/encounters.yml                   NSVPatrolContract 定义
  Entities/jump_points.yml                    导航控制台与跳跃点实体
  Entities/cartridges.yml                     NSV 导航 PDA 程序实体

Resources/SharedMaps/_NSV/Bluespace/
  gust.yml                                    NSV GUST grid；直接生成 NSV 巡逻核心
  station.yml                                 保留的最小 station grid fixture（当前模板未引用）
  ship.yml                                    保留的最小 ship grid fixture（当前模板未引用）

Resources/Locale/en-US/_NSV/bluespace/
  navigation-console.ftl                       导航控制台本地化

Content.Tests/_NSV/Bluespace/
  Sectors/NsvBluespaceSectorLayoutPlannerTest.cs
  Sectors/NsvBluespaceStarmapPrototypeTest.cs

Content.IntegrationTests/Tests/_NSV/Bluespace/
  Sectors/NsvBluespaceSectorSystemTest.cs
  Sectors/NsvBluespaceStarmapSystemTest.cs
  Sectors/NsvBluespaceNavigationCartridgeTest.cs
  Encounters/NsvBluespaceEncounterSystemTest.cs
Content.IntegrationTests/Tests/_NSV/NPC/
  NsvShipTargetQueryTest.cs
```

## 架构边界

NSV 扇区代码不修改或扩展以下通用系统：

- `Content.Server/Salvage` 与 Salvage 远征奖励、计时和地图流程；
- `GameTicker` 的主站图创建、回合生命周期；
- `Content.Server/NPC`、`Content.Shared/NPC` 的 NPC faction 或 targeting；
- 通用 Shuttle FTL 实现。

NSV 不调用或修改通用 NPC faction API。`NsvBluespaceFactionComponent` 表示成员归属，既可放在 root grid，也可放在单个实体；实体成员归属优先于所在 grid。

## Faction 与动态关系

`NsvBluespaceFactionSystem` 是 NSV 唯一的 faction authority。faction YAML 保存定向默认关系：未配置的方向是 `Neutral`，`A -> B` 与 `B -> A` 独立。当前 `NSVHostile` 与 `NSVPlayer` 彼此默认 hostile，`NSVHostile` 与 `NSVFederal` 也彼此默认 hostile，`NSVNeutral` 未配置敌对关系。

每个 sector map 的 `NsvBluespaceSectorInstanceComponent` 保存运行时 relation overrides。resolver 的优先级是：该扇区 override、source faction 的 YAML 默认关系、`Neutral`。它只会解析处于同一 NSV sector map 的双方；缺少 faction 的实体或 grid 均会 fail-closed。覆盖状态不网络同步，也不跨扇区保存，map 被回收时自动消失。当前创建的 NSV sector 会在加载模块前显式写入定向覆盖 `NSVHostile -> NSVPlayer = Hostile`；因此该关系可在 sector map 的 `RelationOverrides` View Variables 中直接检查，而不只依赖 YAML 默认值。

玩法代码必须通过 `SetFaction`、`ClearFaction`、`SetSectorRelation` 和 `ClearSectorRelation` 变更状态；FTL 旅行系统在 foreign grid 已脱离 sector map 后仅使用 `RestoreFaction` 写回入场 snapshot。每一次实际变更都会唤醒并重规划同一扇区的 `NsvBluespaceShipAiCore`，因此巡逻舰会在下一次 HTN 更新中停止或恢复索敌。`NsvNearbyShipTargetsQuery` 保留其现有距离、grid、供电与 blacklist 过滤，并额外只保留 resolver 判定为 `Hostile` 的目标；Mono 查询与 Mono faction 系统不受影响。

## 扇区生命周期

每个临时扇区 map 都有 `NsvBluespaceSectorInstanceComponent`，其状态枚举定义于 `NsvBluespaceSectorInstanceComponent.cs`：

```text
Requested, Planning, Applying, Ready, Draining, Disposed, Failed
```

当前实际使用的转换为：组件默认 `Requested`，创建 map 后直接设为 `Applying`；生成成功时变为 `Ready`，生成失败时变为 `Failed`；可回收的 ready 扇区在删除 map 前变为 `Draining`。`Planning` 与 `Disposed` 已保留在枚举中，但当前实现尚未赋值使用。

实际创建有两条路径：

1. `NsvBluespaceSectorSystem.TryGetOrCreate()` 是 legacy template 路径，按模板 ID 查找活动扇区；若已有处于 `Ready` 的 map，直接复用。
2. `TryGetOrCreateNode(starmapId, nodeId)` 先解析固定星图节点，再以 **`StarmapId + NodeId`** 查找活动实例；节点的 template 只决定生成内容，不能作为战略身份。
3. 两条路径都会将模板模块转换为布局定义，并以对应 seed 调用布局器。节点路径使用 YAML 的稳定 `seed`，同一节点回收后重建仍获得相同布局；相同模板的不同节点保持独立 map、controller 和 participant 集合。
4. 使用 `MapSystem.CreateMap(..., false)` 创建暂停、未初始化的 map。
5. 初始化运行时 instance 并写入 `NSVHostile -> NSVPlayer = Hostile` 的 sector-local relation override；节点路径额外记录 `StarmapId`、`NodeId` 和由节点 encounter pool 稳定选出的 definition。
6. 在 `DoMapInitialize()` 前逐个运行生成器。任一生成器失败或抛出异常时删除整个 map。
7. 所有生成器成功后，初始化 map、解除暂停并将状态设为 `Ready`。

`nsvBluespaceStarmap` 原型在加载时验证节点 ID、有限二维坐标、非负 threat / reward / fuel cost、无重复或自环连接、全部链接目标存在，以及每条连接均为双向连接。

运行时组件还记录：

| 字段 | 含义 |
| --- | --- |
| `OwnedGrids` | station 和 ship generator 加载的根 grid |
| `OwnedEntities` | 陨石及 entity generator 生成的实体 |
| `ForeignGrids` | 从外部 FTL 到此扇区的玩家或其他舰船 |
| `PendingArrivals` | 已开始 FTL、尚未抵达的外部舰船 |
| `ReturnDestinations` | 外部舰船返回原出发地图时使用的原始坐标；节点间移动会完整保留它 |
| `ForeignGridFactionSnapshots` | 进入首个战略节点前的 faction snapshot；节点间移动不覆盖它 |
| `StarmapId` / `NodeId` | 非空时标识该实例所属的固定星图节点；二者而非 template 构成战略身份 |
| `EncounterDefinitionId` | 从节点 encounter pool 按稳定 seed 选出的 encounter；空值表示该节点不激活合同 |
| `EncounterController` | 当前共享 encounter 的 server controller UID；没有合同则为 `EntityUid.Invalid` |

legacy `TryGetOrCreate()` 实例仍按 **template ID** 缓存，以保持旧 debug/fixture 调用方的共享行为。战略节点实例始终按 **`StarmapId + NodeId`** 缓存：即使两个节点复用同一 template，也不会共享 map、seed、controller、participants 或清理状态。

## 布局规划

`NsvBluespaceSectorLayoutPlanner` 只负责决定每个模块实例的中心点、占用半径和稳定内容 seed；它不生成实体或网格。

规划规则：

- 同一个 sector seed 与同一组模块得到相同的模块顺序、位置和 `ContentSeed`。
- 必需模块优先；同优先级模块按模块 ID 排序，避免原型列表顺序改变结果。
- 每个实例到扇区中心的最小距离为 `EntrySafeRadius + FootprintRadius`，中心保留为外来舰船 FTL 抵达区。
- 两个实例的中心距离至少为：

  ```text
  first.FootprintRadius + second.FootprintRadius
  + max(first.MinimumDistance, second.MinimumDistance)
  ```

- 可选模块在 `MaxPlacementAttempts` 次尝试仍无法放置时被跳过。
- 必需模块无法放置，或模块基础配置无效时，整个布局失败。
- 每个成功 placement 获得独立的 `ContentSeed`，供对应内容生成器使用。

## 原型与生成器

扇区模板和模块定义在 `NsvBluespaceSectorPrototypes.cs` 中。模块共有的布局字段为：

| 字段 | 含义 |
| --- | --- |
| `minCount` / `maxCount` | 该模块在布局中的随机实例数量 |
| `footprintRadius` | 布局占用圆的半径 |
| `minimumDistance` | 与其他模块保持的附加安全距离 |
| `required` | 无法放置时是否使整个扇区创建失败 |
| `maxPlacementAttempts` | 单个布局实例的最大采样次数 |

内容由必填的多态 `generator` 字段决定，而不是旧式 `kind + gridPath + entityPrototype` 联合字段。

### 陨石场

```yaml
- type: nsvBluespaceSectorModule
  id: ExampleAsteroidField
  generator: !type:NsvBluespaceAsteroidFieldGeneratorDefinition
    radius: 56
    density: 0.001
    minimumSpacing: 16
    maxSpawnAttempts: 24
    asteroidTypes:
    - prototype: AsteroidDebrisMedium
      weight: 1
  minCount: 4
  maxCount: 6
  footprintRadius: 56
  minimumDistance: 64
```

`NsvBluespaceAsteroidFieldGenerator` 使用 placement 的 `ContentSeed` 新建本地随机数生成器。目标陨石尝试数量为：

```text
ceil(density * π * radius²)
```

每颗陨石在圆形 field 内均匀采样，并与该 field 内已生成的陨石保持 `minimumSpacing`。某一颗在其 `maxSpawnAttempts` 内找不到位置时会被跳过，已生成陨石保留；因此高密度配置不会无限循环。

生成器拒绝以下配置：

- 非有限、负数或超过模块 `footprintRadius` 的 `radius`；
- 非有限或负数的 density / minimum spacing；
- 空类型表、非正权重、无效实体原型；
- 非正的 `maxSpawnAttempts`；
- 目标数量超过 10,000。

权重选择、局部位置和生成顺序在相同 placement seed 下可复现。`AsteroidDebris*` 原型自身若依赖引擎全局随机数，其内部 blob/tile 形状不保证逐 tile 可复现。

### 空间站

```yaml
- type: nsvBluespaceSectorModule
  id: NSVBluespaceRelayStation
  generator: !type:NsvBluespaceStationGeneratorDefinition
    gridPath: /Maps/_Mono/POI/tsfmcoutpost.yml
    faction: NSVNeutral
  minCount: 1
  maxCount: 1
  footprintRadius: 96
  minimumDistance: 96
  required: true
```

`NsvBluespaceStationGenerator` 使用 `MapLoaderSystem.TryLoadGrid()` 在 map 初始化前将指定 SharedMap grid 加载到 placement 坐标。生成器会：

1. 验证 faction prototype；
2. 加载恰有一个 grid 的 map 文件；
3. 将根 grid 写入 `OwnedGrids`；
4. 在根 grid 上添加 `NsvBluespaceFactionComponent` 并设置 faction ID。

### 舰船

```yaml
- type: nsvBluespaceSectorModule
  id: NSVBluespacePatrolShip
  generator: !type:NsvBluespaceShipGeneratorDefinition
    gridPath: /SharedMaps/_NSV/Bluespace/gust.yml
    faction: NSVHostile
  minCount: 1
  maxCount: 2
  footprintRadius: 48
  minimumDistance: 64
```

`NsvBluespaceShipGenerator` 验证 faction 后使用 `MapLoaderSystem.TryLoadGrid()` 加载 NSV 的 ship grid，将根 grid 写入 `OwnedGrids`，并在根 grid 上设置 `NsvBluespaceFactionComponent`。它不再替换地图内的 AI 实体。

`Resources/SharedMaps/_NSV/Bluespace/gust.yml` 是 Mono GUST grid 的受控副本，只把 `SpawnMobAttackerCoreStaticSmart` 替换为 `NsvBluespacePatrolCoreSpawner`。该 NSV spawner 继承 Mono 的视觉和生成器设置，但将 `ConditionalSpawner.prototypes` 改为 `NsvBluespacePatrolCore`；因此 `TryLoadGrid()` 初始化该 grid 时直接生成 NSV 核心，无需依赖运行时删除 Mono 核心。Mono 后续调整 GUST 地图时，必须有意识地合并到此副本。

`NsvBluespacePatrolCore` 继承 Mono 的攻击核心硬件，但将 HTN 根计划替换为 NSV 的 `NsvBluespacePatrolCompound`。该计划继续复用 Mono 的 `ShipMoveToOperator`、`ShipFireGunsOperator`、火控、炮台和船只物理系统；初始索敌与持续刷新均经 `NsvNearbyShipTargetsQuery`。该 NSV 查询复制 Mono 的距离、存活与 `ShuttleAIIgnore` 规则，并在将候选交给现有移动和开火系统前调用 NSV faction resolver；它只接受同一扇区中关系为 hostile 的 `NsvShipTarget`。

### 单实体

`NsvBluespaceEntityGeneratorDefinition` 适用于跳跃点和未来事件实体：

```yaml
generator: !type:NsvBluespaceEntityGeneratorDefinition
  entityPrototype: NSVBluespaceJumpPoint
```

`NsvBluespaceEntityGenerator` 验证实体原型，在 placement 坐标生成实体，并将其登记到 `OwnedEntities`。

## 当前测试扇区内容

`Resources/Prototypes/_NSV/Bluespace/Sectors/sectors.yml` 定义 `NSVBluespaceTestSector`：

- 一个必需的 `NSVBluespaceRelayStation`，加载 `/Maps/_Mono/POI/tsfmcoutpost.yml`，faction 为 `NSVNeutral`；
- 一个 `NSVBluespacePatrolShip`，加载 `/SharedMaps/_NSV/Bluespace/gust.yml`，faction 为 `NSVHostile`；
- 该 GUST 由地图内的 `NsvBluespacePatrolCoreSpawner` 直接生成带 `NsvShipTarget` 的 `NsvBluespacePatrolCore`；
- 四至六个按 density 生成的 `NSVBluespaceAsteroidField`。

TSFMC 前哨站是完整的 Mono POI，含供电、气压、设施和原有防御内容；GUST 是紧凑的 Mono 武装无人机，含 `WeaponTurretShard`、RTG 和推进器。NSV 使用其受控的 `_NSV` GUST 副本；该副本在 map 初始化时直接生成 `NsvBluespacePatrolCore`，其 HTN 计划和查询原型位于 `_NSV`，而船只的驾驶、火控和武器仍复用 Mono 系统。TSFMC 和 GUST 均维持单 root grid。

`Resources/SharedMaps/_NSV/Bluespace/station.yml` 和 `ship.yml` 仍保留为最小有效 `Grid` 格式 fixture，供将来独立测试或替换内容使用，但当前测试扇区不引用它们。

## FTL 旅行与导航控制台

入口实体定义在 `Resources/Prototypes/_NSV/Bluespace/Entities/jump_points.yml`：

- `NSVBluespaceNavigationConsole`：可交互 BUI 控制台；当前配置 `starmapId: NSVBluespaceStrategicMap`；
- `NSVBluespaceJumpPoint`：同样携带星图配置的 marker；
- `templateId` 仅保留给旧的 direct-sector/debug fixture 路径，不是战略节点配置。

### 服务器权威 UI 与战略图

客户端 `NsvBluespaceNavigationConsoleBoundUserInterface` 保持原有蓝空间跳跃窗口，并增加同窗的自绘战略图。服务器通过 `NsvBluespaceNavigationConsoleSystem` 推送只读快照，节点 DTO 仅包含节点 ID、`LocId`、类型、二维位置、threat、reward、fuel cost、faction 文本、encounter 名称、连接 ID、当前/可选状态；不传 map UID、controller/objective UID、seed、返航坐标或 faction snapshot。

玩家点击图中节点只更新本地选择和详情，随后点击跳跃按钮才发送 `NsvBluespaceJumpRequestMessage(destinationNodeId, false)`；显式返航按钮发送 `NsvBluespaceJumpRequestMessage(null, true)`。服务器不信任客户端选择，收到请求后重新验证 shuttle 状态、星图和节点存在性、所在 map、图连接、encounter 授权以及 FTL 可用性。

节点颜色为：当前节点绿色、可选节点蓝色、不可选节点灰色、已选节点黄色。命中检测使用 `args.RelativePosition * UIScale` 与节点本地像素坐标比较；自绘控件的 `Draw` 坐标空间本身即控件本地像素坐标，不需要额外加 `PixelPosition`（曾因误加导致显示位置整体下移、点击命中不到，已修复）。

### 旅行规则

1. 舰船在普通或 debug map 使用配置了星图的控制台时，可选择任一存在的节点。当前 map 没有 `NsvBluespaceSectorInstanceComponent` 即视为外部出发地；系统保存该 map 内当前位置和 faction snapshot，但不检查节点连接。
2. 舰船处于同一星图的节点 sector 时，只能选择 `nsvBluespaceStarmap` 中与当前 `NodeId` 互相连接的相邻节点。请求相同节点、未知节点、不同星图或未登记为该 sector foreign grid 的舰船都会被拒绝。
3. 邻接检查在创建目的节点 map 之前执行；伪造或非相邻请求不会分配 map。
4. 节点间航行保留进入第一个节点时的原始返航坐标和 faction snapshot。源节点在 FTL 启动时移除 foreign-grid ownership，目的节点只在 `FTLCompletedEvent` 后写入 `ForeignGrids`、设置临时 `NSVPlayer` faction 并成为当前节点。
5. 若当前节点有 encounter，节点间航行和“返回出发地”都先调用 `NsvBluespaceEncounterSystem.CanReturn()`：participant 在 `Pending` / `Active` 时被拒绝，`ExtractionOpen` 与 `Failed` 可离开。无 encounter controller 的节点允许继续航行或返航。
6. `PiratePatrol` 节点从其 pool 稳定选择 `NSVPatrolContract`；空 pool 节点不会创建 controller。旧 template 路径继续使用原有 PatrolContract 默认行为。
7. 外部返航的 FTL 完成后才恢复初始 faction snapshot，并由 encounter 记录 `ReturnedParticipants`。FTL 无法创建组件时立即撤销目的节点的预约、返航坐标和 faction snapshot；运行时更新也会清理不再具有 `FTLComponent` 的遗留预约。
8. 节点 sector `Ready`、没有 foreign grid、也没有预约入场时开始计时。两分钟后 `NsvBluespaceSectorSystem.TryDispose()` 会先 dispose encounter controller，再删除 map；再次访问同一节点将使用其稳定 seed 重建独立实例。

### PDA 导航程序（cartridge）客户端

`NsvBluespaceNavigationCartridge` 的客户端由 `NsvNavigationUi`（UIFragment 包装）持有 `NsvBluespaceNavigationCartridgeUiFragment`。实现时有两个非直觉的坑，已修复并留作后续 PDA 程序的参考：

1. **`Setup` 会被反复调用，不能无条件重建 fragment。** PDA 的任何 loader 状态推送（`PdaUpdateState`：插拔笔、开关手电筒等）都会进入 `CartridgeLoaderBoundUserInterface.UpdateState` 的 cartridge 分支并重新调用 `RetrieveCartridgeUI` → `Setup()`。而 attach 逻辑对"同类型 fragment 已挂载"会提前返回，新实例永远不会被挂载；结果是 `_fragment` 字段指向游离实例、屏幕上显示的还是旧实例，后续所有 cartridge 状态更新全部被游离实例吞掉（症状：标题正常、值停在初始"—"）。修复是 `Setup` 里复用未 `Disposed` 的 fragment（见 `NsvNavigationUi.cs`），PDA UI 关闭重开时旧实例会被 Dispose，重建逻辑依然成立。
2. **GridContainer 里的值标签不能用 `ClipText`。** Robust `Label.MeasureOverride` 对 `ClipText` 标签返回最小宽度 0，`GridContainer` 按列内最大期望尺寸定列宽 → 值列宽为 0，文本被裁剪到完全不可见（数据其实已经写入 `Label.Text`）。值标签应使用与导航控制台相同的 `HorizontalExpand="True" + Align="Right"` 且不设 ClipText。

cartridge 快照的服务端状态推送（`UpdateCartridgeUiState`）由 `NsvBluespaceNavigationCartridgeTest` 覆盖：安装 → 激活 → 未入扇区默认值 → 入扇区参与者状态 → Objective 完成后撤离开放，各阶段断言 UI state。

## 新增内容的步骤

1. 在 `Resources/Prototypes/_NSV/Bluespace/` 添加或扩展 NSV 原型，而不是修改 `_Mono` 或通用内容。
2. 对新场景添加 `nsvBluespaceSectorModule`，选择现有 generator definition；若新增 generator 类型，同时实现 NSV 私有服务器生成器并在 `NsvBluespaceSectorSystem.TryApplyModule()` 中调度。
3. 为 grid 内容在 `Resources/SharedMaps/_NSV/` 创建恰有一个 root grid 的 SharedMap。
4. 每个 station 或 ship faction 都应在 `Factions/factions.yml` 中注册。
5. 根据实际最大尺寸设置 `footprintRadius` 和 `minimumDistance`，确保布局器为内容预留足够范围。
6. 扩展 `Content.Tests/_NSV` 的纯规划测试，并为实际加载、ownership、faction 和失败回滚添加 `Content.IntegrationTests/Tests/_NSV` 覆盖。

## 已实现与已验证

### 已实现

- 临时 sector 的创建、legacy template 缓存、node-keyed 战略缓存、确定性模块布局、station/ship/asteroid/entity generator、owned 内容登记和 map 删除；
- 固定 `NSVBluespaceStrategicMap`：`Home`、`Asteroid`、`Distress`、`PiratePatrol`、`UnknownSignal` 五个节点，包含本地化名称/说明、二维位置、threat/reward/fuel、faction、稳定 seed、template、encounter pool 和双向连接；`Asteroid` 与 `UnknownSignal` 故意复用同一 template，以验证节点身份隔离；
- 节点图原型加载验证：节点 ID、有限位置、非负数值、链接存在性、去重、自环拒绝和双向边；
- 服务器权威的节点选择、debug/普通地图任意入图、node-to-node 邻接限制、显式返航、FTL completion 后提交节点 ownership，以及对两种离开方式统一的 encounter extraction 门禁；
- 同一导航窗口中的战略图、节点详情和显式返航入口；
- NSV GUST 受控副本及 `NsvBluespacePatrolCore` 直接生成，避免生成 Mono `NpcStationAiAttackerStaticSmart`；
- 独立于 Mono faction 的 NSV membership、定向默认关系、sector-local runtime relation override，以及变更后的 NSV patrol HTN 唤醒和重规划；新建 sector 会显式安装 `NSVHostile -> NSVPlayer = Hostile` override；
- 独立的 `NsvShipTarget` / `NsvNearbyShipTargetsQuery`。它保留 Mono 的距离、grid、供电和 blacklist 规则，但只接受同一 sector 中 resolver 判定为 `Hostile` 的目标；`NsvBluespacePatrolCore` 自身也带 `NsvShipTarget`，使无人 GUST 可被其他巡逻 core 选中；
- foreign shuttle 在进入 sector 后临时获得 `NSVPlayer`，离场时从 FTL map 阶段恢复其原有 faction，或移除临时 membership；
- M3 最小 `NSVPatrolContract`：首艘实际抵达的 shuttle 创建共享 server controller，后续 shuttle 加入为 participant；合同选择已生成 hostile GUST 的 patrol core，指定 core 终止时幂等地进入 `ExtractionOpen`。完成前阻止 participant 返航，完成后只在 FTL 实际抵达原 map 时记录其撤离；active 期间临时将 `NSVFederal ↔ NSVHostile` 设为 `Neutral`，sector 删除前清理 controller 与该 relation override；
- 目标世界内雷达标记：PatrolContract 激活时在指定 GUST root grid 上安装 `RadarBlipComponent`（红色 Star），任何带雷达的 shuttle 控制台都能看到特殊目标图案；Objective 完成时移除，map 回收或 grid 删除经组件 shutdown 自动清理；
- NSV 导航 PDA 程序：`NsvBluespaceNavigationCartridge` 是标准 CartridgeLoader 程序（`NsvNavigationUi` fragment + `NsvBluespaceNavigationCartridgeSystem`），显示当前 sector 状态、encounter 名称/Objective/状态/参与者/撤离资格，不含跳跃控制；Objective 完成时向处于参与者 grid 上的该程序 PDA 发送 `SendNotification`，触发 PDA 自带的铃声与弹窗提醒；
- NSV patrol 继续复用 Mono 的移动、转向、火控和炮台开火系统，仅替换 HTN 根计划和目标查询。

### 自动化测试覆盖

以下测试与命令覆盖当前实现；`NsvBluespaceEncounterSystemTest` 已按单艘 hostile GUST 的测试场景调整：

- `Content.Tests` 的 `NsvBluespace` 筛选覆盖确定性布局、中心净空、模块间距、生成失败语义，以及星图的 reciprocal graph lookup 与单向边拒绝；
- `Content.IntegrationTests` 的 `NsvShipTargetQueryTest`、`NsvBluespaceSectorSystemTest` 与 `NsvBluespaceEncounterSystemTest` 覆盖 NSV target marker 对应关系、默认 hostile/neutral、实体 membership 覆盖 grid membership、定向 relation override、跨 sector fail-closed、GUST core 生成、relation 变更后的 HTN replan、真实 FTL 入场/返航的 faction 恢复，以及 PatrolContract 的共享 controller、唯一目标 core completion、撤离门禁和 map disposal；
- `NsvBluespaceStarmapSystemTest` 覆盖相同 template 的节点 map 隔离与重用（经测试专用星图 `NSVBluespaceTestStarmap` 的两个共享 `NSVBluespaceAsteroidSector` 节点）、稳定 seed、普通 map 进入指定节点、非相邻节点拒绝、active encounter 同时阻止相邻航行与返航、目标完成后进入相邻空 encounter 节点（`Asteroid` 节点现使用 `NSVBluespaceHunterSector`，确认空 encounter pool 下即使场上有敌对猎手舰也不创建门禁），以及外部返航后的 faction 清理；
- `NsvBluespaceNavigationCartridgeTest` 覆盖 PDA 导航程序的服务端状态推送管线（安装、激活、入扇区、Objective 完成各阶段的 UI state）。该测试也确立了两个 NSV 集成测试约定：测试结尾必须让 shuttle 返航并 `TryDispose` template 缓存扇区（pool 复用服务器进程，残留实例会污染后续测试）；FTL 抵达后约 10 秒内 shuttle 仍处于 `FTLComponent` 冷却（`CanFTL` 返回"Currently in FTL"），需要等待冷却结束才能再次发起 FTL。

```powershell
# 确定性布局、星图图验证、中心净空、模块间距、可选/必需模块失败语义
dotnet test .\Content.Tests\Content.Tests.csproj -c Debug --no-restore `
  --filter "FullyQualifiedName~NsvBluespace" -v:q

# 目标查询、动态 faction、扇区 FTL、PatrolContract、战略节点旅行与 PDA 导航程序状态推送
dotnet test .\Content.IntegrationTests\Content.IntegrationTests.csproj -c Debug --no-restore `
  --filter "FullyQualifiedName~NsvShipTargetQueryTest|FullyQualifiedName~NsvBluespaceSectorSystemTest|FullyQualifiedName~NsvBluespaceEncounterSystemTest|FullyQualifiedName~NsvBluespaceStarmapSystemTest|FullyQualifiedName~NsvBluespaceNavigationCartridgeTest" -v:q

# Shared 以外的编译检查
dotnet build .\Content.Server\Content.Server.csproj -c Debug --no-restore /clp:ErrorsOnly
dotnet build .\Content.Client\Content.Client.csproj -c Debug --no-restore /clp:ErrorsOnly
```

### 已通过的游戏内 smoke test

- 在共享测试 sector 中，`RelationOverrides` 显式包含 `NSVHostile -> NSVPlayer = Hostile`；
- `NsvBluespacePatrolCore` 已能获取玩家船上的 `NsvShipTarget`、建立 HTN plan，并使用 GUST 炮台正常攻击；
- 新 GUI 的战略图节点选择与跳跃/返航按钮已通过游戏内验证；
- PDA 导航程序：经游戏内 debug 工具确认 fragment 修复后 `Label.Text` 已收到正确的 sector/encounter 值（此前值停留在"—"）；值标签的 `ClipText` 布局修复后的最终视觉验证仍待重启游戏确认。

## 未完成或待游戏内验证

- **无玩家自律战斗：** Mono NPC 休眠机制会在约 4000 格内没有存活玩家时移除 `ActiveNPCComponent` 并清空 HTN plan；因此当前单艘目标 GUST 只在玩家接近时保证会被正常驱动，尚未验证或调整无活玩家时持续自主战斗的玩法策略。
- **Encounter 内容与目标：** 当前仅有一个共享 `NSVPatrolContract`，且只能选择 sector 初始化时已生成的 hostile GUST patrol core；尚无 post-Ready spawn adapter、多态 YAML Objective、断电/扫描/护送/拾取等 Objective。目标雷达 marker 与 PDA 状态程序已实现、状态推送已由集成测试覆盖，但目标雷达图案、PDA 界面值显示（ClipText 修复后）与完成铃声通知均未在游戏内最终确认；尚无接受/取消合同的交互或奖励 UI。
- **Encounter 结算与参与者边界：** `ExtractionOpen` 只开放返航并记录已抵达的 participant；未实现 `Settled`、奖励、战利品、击杀归因、超时、participant 断线或舰船被毁的结果规则。
- **巡逻舰多目标战斗边界：** 当前测试 sector 只保留一艘指定 hostile GUST；多艘 NPC faction 战、目标切换、目标失效后的 HTN service 刷新及长时间战斗表现，需在恢复多舰测试内容时单独验证。
- **战略图 BUI 端到端流程：** 服务器快照、蓝/灰/绿/黄节点投影、节点详情、节点/返航请求和服务端授权已实现，节点点击与选择已通过游戏内验证（坐标问题已修复，诊断线已移除）；debug map 任意入图、图边限制、encounter 门禁及显式返航的完整游戏内 smoke test 仍待完成。
- **完整旅行与回收 smoke test：** 自动化覆盖普通 map 进入战略节点、邻接限制、encounter 门禁、节点间 faction/返航数据保留和外部返航；仍需在游戏内确认共享扇区、多艘船并发进出，以及 ready sector 无 foreign grid 后的两分钟自动回收。
- **生成失败回滚：** 已测试无效 asteroid 权重会被拒绝；尚未覆盖每一种 generator 在部分内容生成后失败时的 ownership/map 回滚细节。
- **动态阵营玩法入口：** `SetFaction`、`ClearFaction`、`SetSectorRelation` 和 `ClearSectorRelation` 已存在，但尚未提供游戏内管理命令、BUI 或事件脚本来驱动这些变化。
- **可视化：** 目标雷达标记与 PDA 只读快照已实现；faction、relation override 与 diplomacy 状态仍只有服务器判定，没有任何客户端展示。
- `Planning` 与 `Disposed` 仍是保留的生命周期枚举值，运行时尚未进入这两个状态。
- `_NSV` 的最小 station/ship fixture 仍只作备用测试；当前节点模板依赖 Mono station、受控的 NSV GUST 地图和 NSV Hunter 舰船地图（`Asteroid` 节点）。legacy template sector 仍按 template ID 共享，活动期间不会为同一模板重新选择 seed；战略星图节点按 `StarmapId + NodeId` 独立缓存并使用 YAML 稳定 seed。
