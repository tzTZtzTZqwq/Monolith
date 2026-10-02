# NSV 主游戏循环设计

版本：初稿 v1 · 2026-09-23

本文是目标设计，不代表接口已经实现；代码片段是接口草案。现状描述以本工作区源码核对为准（feature-maingameloop，基于 `origin/main` @ `2bd2b3c171`），引用行号可能随修改移动。

## 1. 状态与修订范围

本文回答一个问题：**要让 Monolith 跑起 NSV 式的“一局游戏主循环”，相比 `docs/nsv_game_loop_design.md` 的路线图和 `docs/NSV_prototype_ref/01`、`/02` 所记录的 NSV13 原型玩法，当前实现缺什么，按什么顺序补。**

- 玩法参照：`docs/NSV_prototype_ref/01-Round-Flow-回合流程.md`（原型 `SSovermap_mode` 回合流程）与 `02-Star-Systems-星系星图与阵营.md`（原型星图与阵营）。原型的架构与 monolith/NSV14 不同，**仅作玩法参照**，不照搬其 DataModel。
- 现状基线：`docs/nsv_game_loop_design.md`（已交付纵向切片）与既有 `feature_nsvstrategicoperations.md`（战略操作协调，尚未实现的舰队物化协议）、`feature_nsvfleetserialization.md`。
- 本文覆盖“回合级目标、压力与推进”这一层；具体战斗、损管、货物经济细节各自另有文档。

**当前一句话结论：** Monolith 已有服务端权威的“一跳一次遭遇”纵向切片（星图 → FTL → PatrolContract → 摧毁目标 → 撤离），但**还没有把单次遭遇串成一局游戏的回合骨架**——没有回合目标、没有胜负或结束条件、没有随时间施加压力的机制、也没有第二类遭遇。本设计的核心是补这条“回合骨架”。

## 2. 参照：NSV13 原型的主循环（来自 ref 01 / 02）

原型把一局游戏组织成三层：

```text
大厅 → 选星图模式(overmap gamemode) → 下达目标 → 船只 FTL 在星图节点间移动
     → 完成目标(票数/跳跃数/清场/护航) → 投票：继续深入 or 返航
     → 返航抵达 Outpost 45 → 宣布胜利、结束回合
```

可移植的机制，按对“一局游戏体验”的贡献排序：

| 原型机制 | 出处 | 玩法作用 |
| --- | --- | --- |
| 每局固定叠加一个“执行 6–10 次 FTL 跳跃”基线目标 | ROUND-001 | 保证任何模式都有推进节奏，玩家不会原地卡死 |
| 基于票数（tickets/影响力）的胜负，Patrol 目标 700 票 | ROUND-009/014, STARSYS-012 | 给“打多少、打什么”一个累积计分口径 |
| 随时间被动累积的 `threat_elevation` + 击杀提升威胁 | ROUND-007 | 后期敌人变强，制造“见好就收”的压力 |
| 惰性目标时的 reminder 递增催办（扣票、甚至生成封锁舰队） | ROUND-006 | 惩罚闲置，推动玩家持续行动 |
| 完成后投票“继续 / 返航”，延长局追加更难目标 | ROUND-010/011 | 提供风险选择的收束点 |
| 返航抵达 `STARSYSTEM_END_ON_ENTER` 即结束回合（胜利） | ROUND-010, STARSYS-013 | 明确的胜利终点 |
| 阵营按计时器生成 AI 舰队、AI 对 AI 在无人星系逐回合结算 | STARSYS-008/010 | 让背景世界自行演化，玩家到达时已是一局进行中的战争 |
| 多星图模式（Patrol/Armada/Boarding/Courier/Shakedown） | ref 01 对比表 | 一局玩法的多样性来源 |
| 主船被毁 = 失败结束 | ROUND-012 | 失败条件 |

值得注意的“原型自身也有的缺陷”，不应照抄：`bounty_pool` 的支付 proc 从未被调用（STARSYS-011，死代码）；`next_difficulty_increase` 是死变量（ROUND-007）；`fleet_trait` 中立区判定恒为真（ref 02 待解问题 5）。参照玩法时要**实现其意图，而非复制其 bug**。

## 3. Monolith 现状对照

### 3.1 已具备（可直接复用的地基）

| 能力 | 位置 | 状态 |
| --- | --- | --- |
| 服务端权威战略星图（节点、双向边、坐标、threat/reward/fuelCost、seed、encounterPool、market） | `NsvBluespaceStarmapPrototype`（Shared），`starmap.yml` 13 节点 | 已实现，节点点击与 FTL 流程已游戏内验证 |
| FTL 航行：仅邻居可达、active contract 阻止相邻跳跃与返航 | 导航控制台 + `NsvBluespaceSectorTravelSystem` | 已实现 |
| `StarmapId + NodeId` 作为运行时 sector 身份，节点键控缓存、两分钟回收 | `NsvBluespaceSectorSystem` | 已实现 |
| 遭遇状态机与撤离门禁 | `NsvBluespaceEncounterComponent`：`Pending → Active → ObjectiveComplete → ExtractionOpen → Failed/Disposed` | 已实现 |
| 一种遭遇：PatrolContract（锁定唯一敌对 GUST 的 patrol core，摧毁即完成） | `NsvBluespacePatrolContractSystem`，唯一原型 `NSVPatrolContract` | 已实现 |
| 世界内反馈：目标红色 Star 雷达标记 + 参与者 PDA 铃声通知 | 同上 + `NsvBluespaceNavigationCartridge` | 已实现（视觉验证待重启游戏） |
| NSV 私有阵营关系 resolver（实体优先、root grid fallback、有向默认、sector-local override） | `NsvBluespaceFactionSystem`，`factions.yml`（NSVNeutral/Hostile/Federal/Player） | 已实现 |
| 舰队身份/位置权威 + 数据态抽象战斗**原语** | `NsvFleetRegistrySystem`：`TryApplyAbstractDamage`、`TrySetFloorCount`、`GetCombatPower`、`CanResolveAbstractCombat` | 系统已实现，**但无任何调用方驱动 AI 对 AI 战斗** |
| 商路市场经济 | `NsvCargoMarketPrototype` + `_Mono` cargo 价格系统 | 已实现（见 `feature_nsvcargodesign.md`） |

> **核对结论 — 星图到底几个节点：** `docs/nsv_game_loop_design.md` 自身对节点数描述不一致：「当前可玩纵向切片」一节把 `Home / Asteroid / Distress / PiratePatrol / UnknownSignal` 列为「战略节点」（读起来像 5 个节点），而 M6 一节写「13 节点四集群」。对照当前 `Resources/Prototypes/_NSV/Bluespace/Starmap/starmap.yml` 的 `NSVBluespaceStrategicMap`：**实际是 13 个节点**——`Sol`、`alpha-1..3`、`beta-1..2`、`charlie-1..4`、`delta-1..3`，分 Sol / alpha / beta / charlie / delta 四集群。前述那 5 个名字其实是节点的 **`type` 字段取值**（该文件共用到 `Home`、`Asteroid`、`PiratePatrol`、`UnknownSignal`、`Distress` 五种 type），不是节点数量。**以设计文档 M6 段的「13 节点」为准，其纵向切片一节的措辞已过时。** 另有一个 `NSVBluespaceTestStarmap`（仅 `SharedAlpha`/`SharedBeta` 2 节点）是集成测试夹具，不被任何跳跃门控制台引用，不计入可玩星图。

### 3.2 缺失（本设计要补的）

| # | 缺口 | 说明 | 原型对应 |
| --- | --- | --- | --- |
| G1 | **无回合级 gamemode / 目标层** | `Content.Server/_NSV` 内对 `GameTicker`、回合状态、胜负零引用；PatrolContract 是逐 sector 的，不构成“本局要做什么”。 | ROUND-001/009/014 |
| G2 | **无胜负与回合结束条件** | 没有返航即结束、没有票数达标、没有主船被毁即失败，也没有“继续 / 返航”的收束点。 | ROUND-010/011/012 |
| G3 | **无动态压力** | 节点 `threat` 是 YAML 静态值，无随时间/击杀增长；无 reminder 催办；无难度随人口缩放。 | ROUND-006/007/008 |
| G4 | **遭遇只有一种** | `encounters.yml` 仅 `NSVPatrolContract`，且目标固定为唯一 GUST patrol core；无护航、配送、防守、登舰、清场等类型。 | ref 01 模式对比表 |
| G5 | **无 AI 舰队生成与 AI 对 AI 结算** | 抽象战斗原语已就位但无调用方；没有按计时器生成敌方舰队、没有无人星系逐回合战斗。事件：`contested`、`send_fleet`、`handle_combat` 在 Monolith 均不存在。 | STARSYS-008/009/010 |
| G6 | **无奖励 / 战利品 / 结算** | 节点 `reward` 字段未支付；无击杀归因、无掉落、无 M4 结算；PatrolContract 完成只开撤离门，不给任何收益。 | ROUND-009, STARSYS-011 |
| G7 | **fuel 未扣除、reward 未使用** | `fuelCost`/`reward` 是数据字段，运行时不读。 | STARSYS-006 旅行成本 |
| G8 | **无多玩家岗位与损管切片** | M2（Pilot/Gunner/Engineer + 最小损管）尚未制作，见 `nsv_game_loop_design.md`。 | 属 NSV 协作核心，非原型 ref 01/02 范围 |

## 4. 目标与非目标

### 目标

1. 一个**回合骨架**：一局开始时确定目标，玩家行动推进目标，达成后能收束（结束或延长）。
2. 一个**最小压力源**：让“继续深入”与“立刻返航”产生取舍，而不是无限安全刷。
3. 遭遇类型从 1 种扩到“**至少 3 种有区别的玩法**”，覆盖歼灭、非歼灭、补给三类。
4. 复用现有星图/FTL/遭遇/阵营地基，**不重写**这些系统。
5. 所有新增状态仍由服务端权威，可被现有测试框架覆盖。

### 非目标

- PvP / Galactic Conquest（ref 01 的 PvP 模式）；本设计只做 PvE。
- 星图持久化、跨服务器重启恢复、动态占领。
- 约 60 节点的完整原版星图；先扩到可支撑一局的规模即可（见开放问题 Q3）。
- 修改 `GameTicker` 核心、通用 NPC faction、Salvage、通用 targeting（沿用 `nsv_game_loop_design.md` 的边界承诺）。
- 完整“去物化”与真实舰船战略迁移；该能力属于 `feature_nsvstrategicoperations.md`，本设计只消费其“数据态舰队”作为 AI 对 AI 的载体。

## 5. 核心机制设计（草案）

### 5.1 回合目标层（对应 G1/G2）

新增一个 `_NSV` 私有的回合协调层，**不侵入 `GameTicker`**，而是订阅其回合开始/结束，并让 NSV 自己的状态驱动胜负：

```text
NSV 回合开始（订阅 GameTicker Started）
  → 固定叠加“执行 N 次 FTL 跳跃”基线目标（N ∈ [6,10]，参照 ROUND-001）
  → 叠加一个模式目标（首版只需一种：票数达标 / 清场）
  → 玩家完成 → 打开“继续 / 返航”收束
  → 返航抵达 Home 节点 → 结束回合（胜利）
  → 主船被毁 → 失败结束
```

设计取舍：**首版不做多模式加权选择**（ROUND-002/003），只硬编码一种默认模式，把“模式多样性”留到遭遇类型扩充之后再谈。理由：模式选择在没有多种遭遇与目标类型时可玩性为零，只是配置负担。

胜负口径建议沿用原型**票数**概念（ROUND-009）：把“击毁敌对舰”“完成配送”“护航存活”等折算为票，达到阈值即进入收束。票数比“清空地图”更容易做成可持续累积、可暂停/恢复的一局。

### 5.2 压力机制（对应 G3）

两种，任一即可产生“见好就收”的压力，建议先做前者：

1. **威胁累积**：在节点静态 `threat` 之外，增加一个回合级 `threatElevation`，随时间被动增长（宽限期后）、并对击杀增量。它参与敌方舰队规模计算。注意实现承诺：**必须有明确的衰减与显示**，否则玩家无法据其决策——这正是原型 ROUND-007 只做了一半的地方。
2. **催办升级**：目标长时间无进展时逐级 raised 提醒，必要时扣票。原型用“生成封锁舰队”作为最终升级（ROUND-006），可延后。

首版建议：只做 5.2.1 的被动增长 + 一个可见的威胁读数，reminder 系统延后。

### 5.3 遭遇类型扩充（对应 G4）

`NsvBluespaceEncounterPrototype` 目前只有 `objective` + `targetFaction` 两个语义字段。扩充前先确认一个**完成条件的可扩展表达**（是继续用“ObjectiveTarget 实体被销毁”，还是引入 `objectiveKind` 枚举区分歼灭/抵达/持有/计时）。

首批建议三种，覆盖不同决策：

| 类型 | 完成条件 | 新增玩家决策 |
| --- | --- | --- |
| PatrolContract（现有） | 摧毁唯一 target | 已具备 |
| Courier（配送） | 把货物送达目标节点并交付 | 路线规划、是否绕路、货损风险 |
| ClearSystem（清场） | 清除节点内全部敌对队 | 数量压力、弹药与损管权衡 |

登舰 / 防守 / 护航可作第二批。

### 5.4 AI 舰队与 AI 对 AI（对应 G5）

**这是把“一局进行中的战争”做出来的关键，也是当前最大的空白。** 抽象战斗原语已在 `NsvFleetRegistrySystem` 就位（`CanResolveAbstractCombat(node)` 已实现正确的不变量：节点有 Live 舰或活跃 encounter 则禁止结算）。缺的是：

1. 一个**舰队生成器**：按计时器在中立/敌对节点生成纯数据态敌方舰队（参照 STARSYS-010）。
2. 一个**抽象战斗驱动器**：周期性对敌对舰队共处的节点投骰结算，调用 `TryApplyAbstractDamage`（参照 STARSYS-008 的 180s 节奏）。
3. `NsvFleetRegistrySystem` 头注释已声明“fleet grouping and scheduling belong to the strategy system and are out of scope here”——即生成与调度是**另一个系统**的职责，现在那个系统还不存在。

### 5.5 奖励与结算（对应 G6/G7）

要在做 G1 票数之前先定：**票/奖励的权威必须来自任务状态，而非击杀当帧加钱**（沿用 `nsv_game_loop_design.md` M4 的结论）。需一并定义原型缺失的结算边界：`Settled` 状态、超时、断线、舰船被毁、未拾取战利品、重复领取防护。

### 5.6 收束与结束（对应 G2）

返航结束需要一个“Home 节点抵达即结束”的钩子（原型 `STARSYSTEM_END_ON_ENTER`）。Monolith 的 `Sol` 节点即 Home，但**当前抵达 Sol 无任何特殊处理**（`_NSV` 内无任何 end-on-enter 相关代码）。需要一个 `_NSV` 侧的“节点特质 / 抵达回调”，不修改通用 sector 系统。

回合生命周期接入点已确认可用：SS14 已有 `RoundStartedEvent`（`GameTicker.RoundFlow.cs` raise，`Content.Shared/GameTicking/RoundRestartedEvent.cs` 定义）与 `RoundEndedEvent`（`GameTicker.RoundFlow.cs` raise）。NSV 回合协调层应订阅这两个事件，无需改动 `GameTicker`。

## 6. 实施顺序（建议）

以“每一步结束都能在游戏内玩到一段新东西”为排序原则：

| 阶段 | 内容 | 依赖 | 交付后可玩 |
| --- | --- | --- | --- |
| P0 | 回合骨架 + 基线跳跃目标 + Home 抵达结束钩子 + 票数计分（对接 PatrolContract 完成给票） | 现有切片 | 一局有开始/目标/结束，但只有一种遭遇 |
| P1 | 遭遇扩展：Courier + ClearSystem 两种类型 | P0 | 节点间出现不同玩法 |
| P2 | 奖励 / 战利品 / 结算规则（Settled、超时、断线） | P0 + `feature_nsvcargodesign` | 完成遭遇有实际收益，能交易 |
| P3 | AI 舰队生成 + AI 对 AI 抽象战斗驱动器 | 抽象战斗原语（已有）+ 数据态舰队 | 背景世界自行演化 |
| P4 | 威胁累积与难度缩放 | P3 | 后期压力，“见好就收”成立 |
| P5 | 威胁读数 UI、reminder 升级、延长局追加目标 | P4 | 完整的压力-反馈闭环 |
| P6 | M2 多玩家岗位 + 最小损管（独立切片，可与上并行） | 现有战斗系统 | Pilot/Gunner/Engineer 协作成立 |

关于 M6 战略星图：`nsv_game_loop_design.md` 的“下一步”列出的游戏内 smoke test 剩余项（debug map 任意入图、仅邻接可选、extraction 门禁、两种离开方式恢复）应作为 P0 的前置验收，不应与本设计并行分叉。

## 7. 边界与前提

- 沿用 `nsv_game_loop_design.md` 的边界：`_NSV` 不修改 Salvage、`GameTicker`、通用 NPC faction 或通用 targeting。
- 回合协调层通过**订阅**而非继承接入回合生命周期；如需在通用 `GameTicker` 加钩子，先核实是否已有可用的 `RoundStartedEvent`/`RoundEndedEvent`（实现前审计）。
- AI 对 AI 战斗只在**纯数据节点**进行，严格遵守 `CanResolveAbstractCombat` 的现有可能不变量，不复用/绕过它。
- 本文只定义“要补什么、按什么顺序”；每个阶段的接口草案应在对应实施前单独出文档（如 P3 需先出“战略舰队调度”文档，与 `feature_nsvstrategicoperations.md` 第 14 节的实施顺序对齐或合并）。

## 8. 与既有文档的关系

| 文档 | 关系 |
| --- | --- |
| `docs/nsv_game_loop_design.md` | 本设计在其既有纵向切片与 M0–M6 里程碑之上，补“回合骨架”层；其 M4（Extraction/Loot/Reward）被本文 P2 吸收 |
| `docs/feature_nsvstrategicoperations.md` | 定义舰队物化/去物化的完整协议；本文 P3 只消费其“数据态舰队”，不实现去物化 |
| `docs/feature_nsvfleetserialization.md` | 舰船序列化基线，被 P3 依赖 |
| `docs/feature_nsvcargodesign.md` | 货物经济，被 P2 依赖 |
| `docs/NSV_prototype_ref/01`、`/02` | 玩法参照来源；本文第 2 节是其可移植机制的筛选 |

## 9. 开放问题

1. ~~回合结束是否真的走 `GameTicker`？~~ **已确认：** `RoundStartedEvent` / `RoundEndedEvent` 均已存在并被 `GameTicker` raise，NSV 回合协调层订阅即可，不必自建状态机或改动 `GameTicker`。仍待定的是：NSV 自己的“胜利收束”如何在 `RoundEndedEvent` 之前请求结束回合（是否需要自行调用回合结束、或以什么信号驱动）。
2. **胜利条件首版选哪种？** 票数达标、清除指定节点、跳跃数达标，还是“任意一种完成即收束”？建议票数，但因与奖励系统耦合，须先定 P2 口径。
3. **默认星图规模：** 13 个节点（当前）能否支撑 P3 的 AI 舰队演化？原型默认约 60 节点。扩图成本与玩法收益需评估。
4. **奖励结算权威：** 由任务控制器还是经济系统记录“已结算”？重复领取如何防止。
5. **模式多样性何时做：** 建议等遭遇类型 ≥4 种后再引入，否则模式只是空壳。
6. **AI 对 AI 战斗是否可能影响玩家正在进行的一局**（例如玩家路过的节点被 AI 打空）？需明确“玩家在场则禁止抽象结算”就够了，还是需要更强的隔离。
