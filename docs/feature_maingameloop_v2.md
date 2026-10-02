# NSV 主游戏循环 —— 实现步骤 (v2)

版本：v2 · 2026-09-23 · 基线工作区 `feature-maingameloop` @ `2bd2b3c171`

本文是 `feature_maingameloop.md`（v1，缺口分析 + 粗排序 P0–P6）的**执行版**。v1 回答「缺什么、按什么顺序」；v2 回答「**每一步具体怎么落地**」——要新建哪些系统 / 组件 / 原型、订阅或抛出哪些事件、加哪些数据字段、验收标准是什么，并把每一步映射回 NSV13 逆向参考（`docs/NSV_prototype_ref/01、02、05、06`）。

> 原为纯研究/文档任务（只给接口草案）；**S1–S7 已实施，本文对应小节已改写为「实现落点 + 状态」以反映真实代码**，S8 部分实施（投票本体随 S3 落地），S9/S10 仍是计划。行号会随修改移动，引用截至上述基线。
>
> **进度速览（2026-10-02）：** ✅ S1 回合骨架 · ✅ S2 跳跃目标 · ✅ S3 三条结束条件（投票 / 旗舰被毁 Defeat / Home 抵达 Victory）· ✅ S4 击毁计分 + Home 胜利分数门禁 + Score 实时上屏 + 遭遇 Reward 权重结算（③零和按设计不做）· ✅ S5 击毁抬威胁 + 被动增长 + Threat 实时上屏 + 完成目标降威胁 · ✅ S6 遭遇类型扩充（可扩展事件分派 + Destroy / ClearSystem / Hold；Courier 待 cargo 交付信号）· ✅ S7 生成器 + faction-on-wake + 首访物化 + AI-vs-AI 抽象战斗驱动器 + 双阵营播种（舰型配比模板未做）· 🟡 S8 投票已做、extend 追加目标/抬威胁未做 · ⬜ S9/S10 延后。
>
> **跨步骤遗留（非某一步的验收项）：** G7 `FuelCost` 仍只显示不扣除；「全目标→投票」与「Home 返航（有分数门禁）」两条胜利路径未互斥、投票路径不看分数；旗舰只能 admin verb 指定（无玩家主船自动进场）；击毁计分不判击杀者/阵营；campaign 目标仅 `PerformJumps` 一种。

## 1. 与 v1 的关系 / 阅读顺序

- 先读 v1 的第 2–3 节（可移植机制筛选 + 现状对照）建立全局图景。
- v2 用相同的缺口编号（G1–G8）作为锚，把每个缺口拆成有序的工程步骤 S1–S10。
- 参考映射标注为 `ROUND-xxx / STARSYS-xxx / FLEET-xxx / MISSION-xxx`，对应四篇 ref 的机制条目。

## 2. 现状锚点（已核对源码，实现须建立在这些真实类型上）

| 已有类型 / 事实 | 位置 | 实现时怎么用 |
| --- | --- | --- |
| `NsvBluespaceStarmapPrototype`，生产图 `NSVBluespaceStrategicMap` **13 节点**（Sol、alpha-1..3、beta-1..2、charlie-1..4、delta-1..3），另有测试夹具 `NSVBluespaceTestStarmap`（2 节点） | `starmap.yml`；`NsvBluespaceStarmapPrototypes.cs` | 节点已带 `Threat/Reward/FuelCost/faction/market/seed/encounterPool/connections`，S4/S5/S7 直接消费，无需扩图 |
| `Reward` / `FuelCost` **仅在导航控制台 BUI 显示**，运行时不做任何扣除或结算 | `NsvBluespaceNavigationConsoleSystem.cs:234-235`；`NsvBluespaceNavigationConsoleUi.cs:35-36` | S4（计分）/ 后续 fuel 扣除要**新加**消费逻辑，字段本身已就位（对应 G7） |
| `NsvBluespaceEncounterPrototype` 字段原仅 `ID / Name(LocId) / Objective(LocId) / TargetFaction` | `NsvBluespaceEncounterPrototypes.cs`；`encounters.yml` | S6 已加 `Kind`（`NsvBluespaceEncounterKind`，缺省 Destroy） |
| `NsvBluespaceEncounterComponent` 状态机 `Pending→Active→ObjectiveComplete→ExtractionOpen→Failed/Disposed` | `Content.Server/_NSV/Bluespace/...` | S3/S6 复用，不重写 |
| `NsvBluespacePatrolContractSystem`：锁定唯一 hostile GUST patrol core，终止即完成 | 同上 | S6 泛化为多 `ObjectiveKind` 的一种 |
| `NsvBluespaceFactionSystem` + `factions.yml`（`NSVNeutral/NSVHostile/NSVFederal/NSVPlayer`） | 同上 | S7 舰队生成的阵营判定、S4 零和计分的敌我关系 resolver |
| `NsvFleetRegistrySystem` 抽象战斗**原语**：`CanResolveAbstractCombat(node)`、`TryApplyAbstractDamage`、`TrySetFloorCount`、`GetCombatPower`、`InstantiateNodeFleets`、`SerializeSectorFleets` | `NsvFleetRegistrySystem.cs:408/464/502/525/179/152` | S7 的地基；**当前无任何外部调用方**，S7 就是补这个调用方 |
| `RoundStartedEvent`（raise 于 `GameTicker.RoundFlow.cs:454`）、`RoundEndedEvent`（`:652`） | `Content.Shared/GameTicking/RoundRestartedEvent.cs` 定义 | S1 的接入点，订阅即可，**不改 GameTicker** |
| `Content.Server/_NSV/` 对 `GameTicker`/回合/计分**零引用** | grep 确认 | 整个回合骨架 + 计分层是绿地新建 |

## 3. 从 ref 提炼的「回合骨架」最小机制集

四篇 ref 描述了一整套原型玩法。**只移植构成「一局有始有终、有推进、有压力」所必需的最小子集**，其余（多模式、PvP、boss 舰队、登舰、货运校验细节）延后。下表是选型结论：

| 要移植的机制 | ref 依据 | monolith 落点（步骤） |
| --- | --- | --- |
| 每局固定叠加「执行 N 次 FTL 跳跃（6–10）」基线目标 | ROUND-001, MISSION-008 | S2 |
| 目标 datum 有 `status`（INPROGRESS/COMPLETED/FAILED/OVERRIDE）、fixed + random 两类 | MISSION-001 | S1/S2 |
| 基于「票数/影响力」的累积胜利分（原型 Patrol 700） | ROUND-009, STARSYS-012, MISSION-006 | S4 |
| **目标从不直接付 credits**；奖励 = 推进胜利分 + 完成时降威胁 | MISSION 目标目录「奖励说明」 | S4/S5（设计铁律） |
| `threat_elevation`：宽限期后被动增长、击杀 +、完成目标 −、下限 0、驱动舰队规模 | ROUND-007, FLEET-018, MISSION-005 | S5 |
| 返航抵达 Home 即结束回合（胜利） | ROUND-010, STARSYS-013 | S3 |
| 主船被毁 = 失败结束 | ROUND-012 | S3 |
| 阵营按计时器从中立区生成 AI 舰队，辛迪加领 `goal_system` | STARSYS-010, FLEET-001/005 | S7 |
| 仅在**无玩家在场**的节点、每 180s 抽象骰子结算 AI-vs-AI | STARSYS-008, FLEET-009 | S7 |
| 舰队编成 destroyers/battleships/supply，规模 = 难度 + 威胁 | FLEET-005 | S7 |
| 惰性目标催办升级（reminder） | ROUND-006, MISSION-003 | S9（延后） |
| 完成后「继续/返航」投票延长局 | ROUND-010/011, MISSION-004 | S8（延后） |

**明确不移植（原型自身的死代码 / bug，勿照抄，详见 §7）：** `bounty_pool` 支付（STARSYS-011 从未被调用）、`next_difficulty_increase`（ROUND-007 死变量）、`fleet_trait` 中立区判定恒真（STARSYS 待解 5）、`ai_behaviour` AI 侵略性开关（FLEET-021 死代码）、`nsv_mission` 空间站任务层（MISSION-017/018 已弃用）、无衰减无显示的威胁（ROUND-007 只做一半）。

## 4. 实现步骤

排序原则：**每一步结束都能在游戏内玩到 / 观察到一段新东西**，且尽量只依赖已存在的地基。每步给出 `目标 / 新建或改动 / 事件 / 数据 / 验收 / ref`。

### S1 — 回合场景骨架（对应 G1）

> **玩起来是什么样：** 每局游戏都是清清楚楚的一整局——开局给你一份任务简报，全程记录你的进度（做了哪些目标、攒了多少分、当前有多危险），一局结束时干净收场、不残留到下一局。

> **实现状态：** ✅ 已实现（2026-09-24），编译通过、Content.Tests 全绿、原型序列化 integration test OK、游戏内确认能渲染。

- **落点（与原计划的差异）：** 实际类名 `NsvCampaignRuleComponent` / `NsvCampaignRuleSystem`，放在 `Content.Server/_NSV/GameRule/`（**非**原计划的 `NsvBluespaceCampaignRuleComponent` + `.../Bluespace/Round/`）。
- **接入方式：** 用 SS14 原生 game rule 机制（D1 拍板选 GameRule，而非直接订阅 `RoundStartedEvent`）。`NsvCampaignRuleSystem : GameRuleSystem<NsvCampaignRuleComponent>`（`sealed partial`），override `Started`/`Ended`。原型 `NsvCampaign`（`Resources/Prototypes/_NSV/GameRules/campaign.yml`）；专属 admin-only preset `MonoNsvCampaign`（别名 `nsvcampaign`，`game_presets.yml`，镜像 MonoStandard 规则 + 追加 NsvCampaign）。
- **数据（`NsvCampaignRuleComponent`，`[Access(typeof(NsvCampaignRuleSystem))]`）：** `Phase`（Briefing/Active/Extending/Ended）、`Outcome`（None/Victory/Defeat）、`List<NsvCampaignObjective> Objectives`、`int Score`、`float ThreatElevation`。字段访问受限，所有写逻辑必须留在 system。
- **网络 / UI：** 回合状态 **复用蓝空导航控制台 BUI**（不另起 HUD）——SECTOR 屏加 Campaign 面板。shared DTO `NsvCampaignSummaryState` + `NsvCampaignObjectiveReadout`（`NsvBluespaceNavigationConsoleUi.cs`）只携带 loc-key 字符串，enum→loc 映射（`GetPhase`/`GetObjectiveLabel`/`GetObjectiveStatus`）留在 server system，避免 Server→Shared enum 耦合。
- **实时刷新（2026-09-24 完成）：** `NsvCampaignRuleSystem` 暴露 `event Action? CampaignDisplayChanged`，在 `AwardKill`（score）/`AdjustThreatElevation`（threat）/`ActiveTick` 被动增长 三处值真变时触发；`NsvBluespaceNavigationConsoleSystem` 订阅它 `RefreshAllConsoles`（遍历所有 jump point，campaign 面板全局不绑 sector）。Score/Threat 现已实时上屏；tally 仍靠跳跃自身的 sector 刷新。
- **ref：** ROUND-001/002（硬编码单一「Patrol 式」场景，不做加权多模式）、MISSION-001。

### S2 — 基线跳跃目标 + 目标抽象（对应 G1）

> **玩起来是什么样：** 不管这局玩什么，都有一个保底任务「完成 6–10 次跳跃」，给你一个明确的前进节奏，不会开局发懵不知道干嘛。每跳一次进度 +1，跳够就打勾完成。

> **实现状态：** ✅ 已实现（2026-09-24），编译通过；集成测试 `NsvCampaignRuleTest.PerformJumpsCompletesOnArrivals` 通过。

- **目标抽象：** `NsvCampaignObjective`（普通类，**非** `[Access]` 限制，`Content.Server/_NSV/GameRule/Components/`）：`Kind`（`NsvCampaignObjectiveKind`，目前仅 `PerformJumps`）、`Status`（InProgress/Completed/Failed/Override）、`Target`、`Tally`。
- **实例化：** `Started` 加一个 PerformJumps 目标，`Target = RobustRandom.Next(6, 11)`（6–10 闭区间）。`Tally` 从 **0** 起——**未**照抄原型 tally=-1（本模型无开局强制调动跳，无需排除）。
- **推进逻辑：** `NsvCampaignRuleSystem.NotifyJumpArrived()` 遍历激活 campaign，in-progress PerformJumps 目标 `Tally++`，达 `Target` 翻 Completed（不回退、不超过 Target），随后调 `TryStartOutcomeVote`。逻辑留在 system 因组件 `[Access]` 限制。
- **接线：** `NsvBluespaceSectorTravelSystem.OnFtlCompleted` 的「抵达 sector」分支（紧接遭遇分派 `_encounters.DispatchArrival` 之后，S6 前为 `_patrolContracts.OnSectorArrival`）调 `_campaign.NotifyJumpArrived()`（Home 节点改调 `NotifyHomeArrival`，见 S3）。仅 FTL 落到带 `NsvBluespaceSectorInstanceComponent` 的 sector 才计数；返航走 else 分支不计。
- **验收：** 已由集成测试覆盖（0→Target-1 保持 InProgress、第 Target 次翻 Completed、之后不再推进）。
- **ref：** ROUND-001, MISSION-008/001。

### S3 — 全目标完成 →「继续 / 收束」投票（对应 G2）

> **玩起来是什么样：** 达成本局所有目标后不强制结束，全员投票决定：继续深入再干一会，还是就此收工。给一局一个有张力的收尾抉择。

> **实现状态：** ✅ 已实现（2026-09-24），编译通过（client + server）。投票有两条触发路径（自动 + 管理员手动），主船终止订阅按用户范围暂不做。注：会话把本步编号为 S3，对应本文原始规划的 **S8**（投票延长局）；原 S3 的「结束条件」降级为下方 ⏳ 待办。

- **触发路径 1（自动，全目标完成）：** `NsvCampaignRuleSystem.TryStartOutcomeVote`（每次 `NotifyJumpArrived` 推进后调用），双门禁：`component.Outcome`(None→Victory) 一次性 guard + 全目标 Completed 才发起。
- **触发路径 2（管理员手动）：** `NsvCampaignRuleSystem.ForceOutcomeVote()`——找第一个 `Outcome==None` 的激活 campaign，**绕过**「全目标完成」检查直接发起（admin override，用于不刷满目标即验证投票流程），仍保留 `Outcome==None` guard 防重复。入口挂在 NSV sector monitor 管理面板：新按钮 `StartVoteButton`（loc `nsv-sector-monitor-start-vote`）→ shared 消息 `StartOutcomeVoteRequest` → `NsvSectorMonitorEui.HandleMessage` 调 `ForceOutcomeVote()`。两条路径共用私有 `StartOutcomeVote(component)`（设 Outcome=Victory + 建投票 + 接 OnFinished）。
- **投票：** 经 `IVoteManager.CreateVote`（照 `VoteManager.DefaultVotes.cs` restart vote 范式），两选项 extend/end，Duration 60s，`SetInitiatorOrServer(null)` 服务器发起。loc 在 `Resources/Locale/en-US/_NSV/gamerules/campaign.ftl`（`nsv-campaign-vote-*`）。
- **结算（OnFinished）：** `Winner is "extend"` → `Phase=Extending` + 公告 + `Timer.Spawn(60min, () => _roundEnd.EndRound())`；否则（含平票 / end 多数）立即 `RoundEndSystem.EndRound()`。**决策规则：需 extend 显式多数才延长，平票默认结束。**
- **依赖：** `NsvCampaignRuleSystem` 现为 `sealed partial`、[Dependency] 字段非 readonly（避免 RA0049/RA0051），新增依赖 `IChatManager` / `IVoteManager` / `RoundEndSystem`。
- **⏳ 待办（原 S3 结束条件，部分实现）：** ✅ **失败收束已做（2026-09-24）** —— 管理员用右键 verb「Designate campaign flagship」把某个实体（如主船主控制台）标记为旗舰，该实体 `EntityTerminatingEvent` 触发即 `Outcome=Defeat` + 公告 + `RoundEndSystem.EndRound()`（`NsvCampaignFlagshipComponent` 标记组件 [Access]-锁 NsvCampaignRuleSystem；`NsvCampaignFlagshipVerbSystem` 在 `Content.Server/_NSV/Administration/` 镜像 `NsvFactionVerbSystem`，admin-gated + `HasActiveCampaign` 门禁，**不解析到 grid**、标记右键的确切实体；campaign 侧 `SetFlagship`/`ClearFlagship`/`IsFlagship`/`GetOutcome` 访问器 + `OnFlagshipTerminating` 处理器，Defeat guard 为 `Outcome==None`，只订 `EntityTerminatingEvent` 不订 `PowerChanged`——断电不算失败，只有摧毁算；loc 在 campaign.ftl `nsv-campaign-flagship-*` / `nsv-campaign-defeat-flagship-lost`；集成测试 `NsvCampaignRuleTest.FlagshipLostConcludesInDefeat`）。因目前无玩家主船自动进场机制，暂用 admin verb 指定（用户拍板）。✅ **胜利收束已做（2026-09-24）** —— 返航 FTL 抵达 `type: Home` 节点（如 `Sol`）即 `Outcome=Victory` + `Phase=Ended` + 公告 `nsv-campaign-victory-home` + `RoundEndSystem.EndRound()`。落点：`NsvCampaignRuleSystem.NotifyHomeArrival()`（Victory guard 为 `Outcome==None`，不覆盖已 Defeat / 已在投票的 Victory）；接线在 `NsvBluespaceSectorTravelSystem.OnFtlCompleted` 的 sector-instance 分支——新私有 `IsHomeNode(sector)`（`_sectors.TryGetStarmap(sector.StarmapId) → starmap.TryGetNode(sector.NodeId) → node.Type == Home`，节点空的模板 sector 恒 false）分流：Home → `NotifyHomeArrival()`，否则 → `NotifyJumpArrived()`。集成测试 `NsvCampaignRuleTest.HomeArrivalConcludesInVictory`。**已知设计取舍（尚未 gate）：** 直飞 Home 可在不完成任何目标时立即通关（trivial early win）；未与「全目标完成→投票」路径互斥（先到者定胜负）。若要禁止空手通关，在 `NotifyHomeArrival` 加「全目标 Completed 才判胜」门禁。**S3 结束条件三条（投票收束 / 旗舰被毁 Defeat / Home 抵达 Victory）现已全部落地。**
- **游戏内验证：** 管理面板 `StartVoteButton` 就是不刷满目标验证投票流程的入口（管理员用 `nsvsectormonitor` 打开）。
- **ref：** ROUND-010/011/012, STARSYS-013, MISSION-004。

### S4 — 计分（胜利分 / tickets）（对应 G2/G6）

> **玩起来是什么样：** 你做的每件事都折算成一个累积的「战功分」——摧毁敌舰、完成遭遇都加分，分数攒够了才允许返航通关。分数随时可见，暂停或继续都不会丢。

> **实现状态：** ✅ 已实现（2026-09-26）——击毁计分组件、Home 返航胜利分数门禁（①）、遭遇 `Reward` 权重结算（②，`NsvCampaignRuleSystem.NotifyEncounterComplete`，S6 后由 `NsvBluespaceEncounterSystem.Complete` 对所有 kind 统一调用）、Score 实时上屏（④）均已落地；③零和按设计不做。尚未游戏内验证击毁实际加分。

- **已实现——击毁计分组件：** `NsvCampaignKillRewardComponent`（`Content.Server/_NSV/GameRule/Components/`，`[DataField] int Score=1` + 运行时 `bool Rewarded` 一次性 guard，`[Access]` 限 NsvCampaignRuleSystem）。`NsvCampaignRuleSystem.Initialize` 订阅两事件 → `AwardKill`：`EntityTerminatingEvent`（终止即算）与 `PowerChangedEvent && !Powered`（断电算——此事件只对带电力接收组件的实体触发，天然满足「需电力且断电才算」）。取最先发生的一次，恢复供电不扣分/不再加分。加分打到所有激活 campaign 的 `Score`。
- **已挂载：** `- type: NsvCampaignKillReward`（默认 1 分）贴在 `NsvBluespaceStandaloneCore`（`Resources/Prototypes/_NSV/Bluespace/AI/ship_ai.yml`），级联到子原型（Broadside 战斗核 + SpinTest/KeepDistance 测试核）。
- **决策：** 首版「任何销毁都计分」，不做击杀者/阵营判定（隔离见 D5，击杀归属留到 S7 抽象战斗上线后再收紧）。
- **设计铁律（MISSION「奖励说明」）：** **目标不直接付 credits**；目标「奖励」= 推进胜利分 +（S5）完成时降威胁。credits/战利品是另一条线（`feature_nsvcargodesign`），权威来自任务状态、防重复结算。
- **⏳ 待办：** ①✅ **已做（2026-09-24）** —— `NsvCampaignRuleSystem.NotifyHomeArrival` 现在读 CVar `nsv.campaign.victory_score_threshold`（默认 5），仅 `Score >= threshold` 才收束胜利；分数不足时公告 `nsv-campaign-home-insufficient`（带 score/threshold），返航是 no-op。测试 `NsvCampaignRuleTest.HomeArrivalConcludesInVictory`（不足→None、够→Victory）。②✅ **已做（2026-09-26）** —— `Reward` 节点字段升级为「完成 encounter 的计分权重」，测试 `EncounterCompletionAwardsNodeReward`。③可选零和（STARSYS-012，击败阵营转分给敌对方）——按设计不做。④✅ 分数实时刷新 BUI（`CampaignDisplayChanged`）。
- **ref：** ROUND-009, STARSYS-012, MISSION-006。

### S5 — 威胁累积（对应 G3）

> **实现状态（2026-09-26 更新）：** ✅ 已实现。下方「未做」两项均已于 2026-09-24/26 补齐：完成目标降威胁（`NsvCampaignObjective.ThreatNegated` 一次性 guard + CVar `nsv.campaign.objective_threat_negation`，测试 `CompletingObjectiveEasesThreatOnce`）与实时推送（`CampaignDisplayChanged`）。以下为 2026-09-24 原始记录。**已做：** ①击毁抬威胁——`NsvCampaignKillThreatComponent`（`[DataField] float Threat=1` + 一次性 `Contributed` guard，镜像 KillReward 的 `EntityTerminatingEvent` + `PowerChangedEvent && !Powered` 检测），贴在 `NsvBluespaceStandaloneCore` 级联子原型；`NsvCampaignRuleSystem.ContributeKillThreat → AdjustThreatElevation`（下限 0）。②**被动增长**——`NsvCampaignRuleSystem.ActiveTick`（GameRule 基类每 tick 对激活 rule 的钩子）累计 `component.ActiveTime`，过宽限期（CVar `nsv.campaign.threat_grace_period`，默认 1500s=25min）后每 `threat_growth_interval`（默认 60s）加 `threat_growth_amount`（默认 1）；用 frametime 累加器（`ThreatGrowthAccumulator`）范式，`interval<=0` 时跳过。威胁读数已进 `NsvCampaignSummaryState`（导航台 Campaign 面板可见）。测试 `NsvCampaignRuleTest.ThreatGrowsPassivelyAfterGrace`（grace=0 跑几 tick→威胁 >0）。**未做：** ①完成目标降威胁（防刷的历史水位逻辑）；②实时推送（威胁变化只在导航台刷新事件时重建 state，非即时）。**范围提醒（用户 2026-09-24 拍板）：** 威胁如何驱动生成节奏/规模属未来 strategy 系统，spawner 消费点（读 threat 缩放 target）已备好，别主动去接。

> **玩起来是什么样：** 你在外面待得越久、打得越多，敌人就越来越强、越来越多。屏幕上有个实时可见的「危险等级」，逼你做「再深入捞一票，还是见好就收赶紧撤」的取舍。

- **目标：** 制造「见好就收」的压力，让「继续深入」与「立刻返航」成为真实取舍。
- **新建：** Rule 上的 `ThreatElevation`（float）。规则（照 ROUND-007 / FLEET-018 / MISSION-005，但补齐原型缺的衰减与显示）：
  - 宽限期（原型 `TE_INITIAL_DELAY` 25 min）后被动增长（每固定间隔 +固定量）；
  - 击败敌方舰队 +（`kill_threat × 规模`）；
  - **每新完成一个目标 −**（`objective_negation`，只对超过历史水位的新完成生效，防刷）；
  - **下限 0**；
  - **必须可见**：推到 BUI 的回合摘要（这是原型 ROUND-007 只做一半、被明确点名的坑）。
- **消费：** `ThreatElevation` 参与 S7 的舰队规模计算（`appliedSize += round(threat / perFleetSize)`）。
- **验收：** 威胁读数随时间增长、击杀上升、完成目标下降、永不为负，且 BUI 实时显示。
- **ref：** ROUND-007, FLEET-018, MISSION-005。

### S6 — 遭遇类型扩充：EncounterKind（对应 G4）

> **玩起来是什么样：** 星图上的任务不再只有「摧毁指定目标」一种。新增「清场」（干掉某星系全部敌人）和「坚守」（在敌火下撑过倒计时才能撤离）等玩法，每种带来不一样的打法和节奏。

> **实现状态：** ✅ 已实现（2026-10-02），Content.Server / IntegrationTests 0 error；集成测试 11/11 全绿（新增 2 + Destroy 回归 2 + campaign 7）。与下方原计划的差异：枚举命名为 **`NsvBluespaceEncounterKind`**（不叫 `ObjectiveKind`，避免与 campaign 的 `NsvCampaignObjectiveKind` 撞名）；第三种 kind 落地为 **Hold（坚守）** 而非 Courier（Courier 依赖 cargo 交付信号，仍是 TODO）。尚未游戏内验证。
>
> - **可扩展分派（加新 kind = 枚举值 + 一个订事件的 kind 系统 + 原型/loc/节点池，零改 travel 与分派器）：** `NsvBluespaceEncounterSystem.DispatchArrival(sectorMap, shuttle)` 集中解析定义（`sector.EncounterDefinitionId`；节点空的模板 sector 回退 `NSVPatrolContract`；有星图节点但池为空则无遭遇），广播 `NsvBluespaceEncounterArrivalEvent(SectorMap, Shuttle, DefinitionId, Kind)`（`NsvBluespaceEncounterEvents.cs`）。`NsvBluespaceSectorTravelSystem.OnFtlCompleted` 只调 `DispatchArrival`，不再依赖任何 kind 系统。各 kind 系统 `if (ev.Kind != Mine) return;` 后自行 `TryGetOrCreate` + `TryActivate`，激活失败 → `Fail`。
> - **集中完成副作用：** `TryCompleteObjective(controller, target)`（单目标 kind，校验 `ObjectiveTarget`）与 `TryComplete(controller)`（无目标 kind，只校验 Active）共用私有 `OpenExtraction` → `Complete`：campaign `NotifyEncounterComplete`（节点 Reward 入 Score）+ 向参与者导航 cartridge 推完成通知。原 `PatrolContractSystem.NotifyObjectiveComplete` 已删除。
> - **Destroy**（`NsvBluespacePatrolContractSystem`）：逻辑不变，只改为订阅到达事件。
> - **ClearSystem**（`NsvClearSystemContractSystem`）：激活时快照节点内所有 `TargetFaction` grid 上的 AI core（`NsvEncounterClearTargetComponent` 贴 core，控制器侧 `NsvEncounterClearObjectiveComponent.RemainingTargets`），红星 blip，Federal↔Hostile 置 Neutral（经 `TrackRelationOverride` 回滚）；core `EntityTerminatingEvent` 时移出集合，空即 `TryComplete`。快照语义：激活后才生成的舰不追加；零目标 → Fail。
> - **Hold**（`NsvHoldContractSystem`）：激活时 `EndTime = CurTime + nsv.bluespace.encounter.hold_duration`（默认 300s），**不**中和关系（敌人持续进攻）；`Update` → `CheckHolds`（internal，供测试直调）在到时且仍有参与者存在时 `TryComplete`。张力来自既有 `CanReturn` 闸：Active 期间无法 FTL 撤离。增援波次刷怪留作扩展点（注释内写明复用 `NsvBluespaceShipGenerator` 序列）。
> - **数据：** `encounters.yml` 新增 `NSVClearSystemContract` / `NSVHoldContract`；`beta-1` 池 = `[ClearSystem, Hold]`，`alpha-3` 池追加 `ClearSystem`；loc 在 `navigation-console.ftl`（完成通知标题改为通用的 "Contract Complete"）。
> - **测试：** `NsvClearSystemContractSystemTest`（手工搭 2 艘敌舰——没有任何模板会生成 >1 艘敌舰；第一艘毁仍 Active，第二艘毁 → ExtractionOpen + Score+2）、`NsvHoldContractSystemTest`（Active 时 `CanReturn==false`、到时前 check 无效、到时 → ExtractionOpen + Score+2 + 可撤离）。
>
> 以下为原始计划，保留作对照。

- **目标：** 从「唯一 PatrolContract（摧毁指定 core）」扩到至少三种有区别的完成条件，覆盖歼灭 / 清场 / 配送。
- **新建 / 改动：**
  - `NsvBluespaceEncounterPrototype`（当前仅 `ID/Name/Objective/TargetFaction`，见 §2）新增一个 `ObjectiveKind` 枚举字段（`Destroy`（现状默认）/ `ClearSystem` / `Courier`），以及各 kind 所需的少量参数字段（如 Courier 的 `DeliverToNodeType` 或目标节点标签、ClearSystem 的「敌对队清零判据」）。旧原型 `NSVPatrolContract` 不写该字段时默认 `Destroy`，保持向后兼容且不改现有行为。
  - `NsvBluespacePatrolContractSystem` 泛化为「按 `ObjectiveKind` 分派完成判据」的一种：`Destroy` = 指定 core 终止（现状逻辑原样保留）；`ClearSystem` = 节点内全部 hostile 舰/队清零（复用 `NsvBluespaceFactionSystem` 的 hostile 判定 + 存活查询）；`Courier` = 货物送达目标节点并交付（依赖 cargo 交付信号，见 `feature_nsvcargodesign`，首版可先只落地枚举与 Destroy/ClearSystem 两种，Courier 挂 TODO）。
- **事件：** 各 kind 复用现有 encounter 状态机（`Pending→Active→ObjectiveComplete→ExtractionOpen`，见 §2）不重写；只替换「什么条件触发 ObjectiveComplete」这一步。`ClearSystem` 需要一个「节点 hostile 计数归零」的检查点（订阅 `EntityTerminatingEvent` 后重算，或轮询）。
- **数据：** 新增 `encounters.yml` 原型条目（如 `NSVClearSystemContract`），并把它们填进对应节点的 `encounterPool`（目前只有 `alpha-3` 挂 `NSVPatrolContract`；`beta-1` 等 hostile 节点可挂 ClearSystem）。
- **验收：** 至少两种 kind 能在游戏内跑通完成流程（Destroy 回归不变、ClearSystem 清零即完成）；`ObjectiveKind` 缺省安全（不写=Destroy）。
- **ref：** MISSION-001（objective datum 有 status + kind），ref 01 模式对比表（Patrol/Courier/ClearSystem 的玩法差异）。

### S7 — AI 舰队生成 + AI 对 AI 抽象战斗（对应 G5）

> **玩起来是什么样：** 你没去过的星系里，各阵营的 AI 舰队会自己生成、自己开战、互相消耗。等你跳过去时那里可能已经打成一团——世界是活的，不是等你到场才启动的布景。

> **实现状态（2026-09-24）：** 🟡 **地基已落地**——`NsvStrategyFleetSpawnerSystem` 已实现并验证：定时（`nsv.bluespace.strategy.fleet_spawn_interval`，默认 180s）在敌对（NSVHostile）、未物化的战略图节点上生成**纯数据态**敌舰，数量 = `clamp(baseline + round(threat / perShip), 0, maxPerNode)`（四个值全走 CVar），无 campaign 不生成、幂等到上限、已物化节点（含 Sleeping）跳过。走方案 A（spawn 到 paused scratch map → RegisterShip → 立即 TrySerializeShip 到 holding map → 删 scratch）。**单一舰型**（`/SharedMaps/_NSV/Bluespace/gust_2.yml`，改用非-HTN 的 `NsvBluespaceStandaloneCore`——地图内 `missingComponents: HTN` 显式剥 HTN，配 RTG+APU 供电、9 推进器、1× 激光炮塔；原 `gust.yml` 是 HTN patrol core），暂无编成模板。附带修复 registry 跨回合泄漏（订阅 `RoundRestartCleanupEvent`）。3 个集成测试全绿、Content.Server 0 error。**未做（S7 下一步）见下方三项缺口。**

> **首访物化修复（2026-09-24）：** 原先 `InstantiateNodeFleets`（把节点上积累的数据态舰物化成活 grid）**只**由 wake 路径（`NsvBluespaceSectorLifecycleSystem.TryWakePausedSector`，Sleeping→Waking）调用；节点**首次**物化走的是 `NsvBluespaceSectorSystem.TryGetOrCreate`（CreateMap→跑生成器→Ready），**不**调它。后果：spawner 在玩家到访前攒的背景舰，首访时不会出现，要等「访问→睡→再唤醒」一个周期才冒出来——直接违背「第一次跳过去就看到活的世界」的承诺。**已修**：首创路径在 `DoMapInitialize` 之后、`SetPaused(false)` 之前也调一次 `InstantiateNodeFleets`（与 wake 同样在 unpause 前物化，ship 经 `FindFreeSpot` 扇开不叠图），背景舰与 encounter 自身生成的舰叠加。回归测试 `NsvBluespaceSectorSystemTest.FirstMaterializationInstantiatesResidentDataFleet`。

> **faction-on-wake（2026-09-24 完成，编译 0 error + 5 集成测试全绿）：** 数据舰物化后现在会带上 NSVHostile 阵营，与玩家交火。做法：`NsvFleetShip` 加**不透明** `string? Faction` 字段（null=不动，默认；registry 从不解读它，只存与回传），spawner 在 `RegisterShip` 后置 `ship.Faction = "NSVHostile"`；`NsvFleetRegistrySystem.TryInstantiateShip` 在 ship 转 Live 后广播 `NsvFleetShipInstantiatedEvent(ShipId, RootGrid)`（`[ByRefEvent]`，定义在 `NsvFleetShip.cs`），spawner 订阅→`OnShipInstantiated`：仅当 `ship.Faction` 非 null 才 `_factions.SetFaction(rootGrid, faction)`。**关键安全阀：** `SerializeSectorFleets` 也会把 sector 自身的活 grid（可能含玩家船/encounter 舰）登记成数据舰经同一 `InstantiateNodeFleets` 物化，这些记录 `Faction==null`，故唤醒时**绝不**被 SetFaction——不会把玩家船变敌对。SetFaction 需 faction-enabled map（holding map 没有），故必须等物化到活 sector map 才生效。首创+wake 两条物化路径都吃这一事件，首访物化的背景舰同样正确上阵营。测试：`NsvStrategyFleetSpawnerTest.FactionOnWakeStampsTaggedShip`（tagged→上 NSVHostile）+ `FactionOnWakeLeavesUntaggedShipUntouched`（untagged→不动）。

- **目标：** 补上 `NsvFleetRegistrySystem` 抽象战斗原语**当前唯一缺的调用方**，让无玩家节点的背景世界自行演化——玩家抵达时已是一局进行中的战争。这是 v1 点名的最大空白。
- **新建（两个独立系统，对应 registry 头注释「grouping/scheduling 属于 strategy 系统、不在 registry 范围」）：**
  - `NsvStrategyFleetSpawnerSystem`（舰队生成器）：按计时器在中立/敌对节点生成**纯数据态**敌方舰队（走 `NsvFleetRegistrySystem` 的注册/序列化路径，不物化 grid）。编成 destroyers/battleships/supply，规模 = 难度基线 + `ThreatElevation`（`appliedSize += round(threat / perFleetSize)`，消费 S5 的读数）。阵营用 `NsvBluespaceFactionSystem` 判定（辛迪加/GUST 系走 hostile）。
  - `NsvAbstractCombatSystem`（抽象战斗驱动器）：固定周期（原型 180s，STARSYS-008）遍历「有 ≥2 个敌对阵营舰队共处」的节点，**先过 `CanResolveAbstractCombat(node)` 门禁**（该方法已实现正确不变量：节点有 Live 舰或活跃 encounter 则返回 false，见 `NsvFleetRegistrySystem.cs:525`——**严格遵守、不绕过**），再用 `GetCombatPower` 投骰比较、调 `TryApplyAbstractDamage(shipId, floorsLost, out _)`（`:408`）扣抽象战力；一方归零即判负、可给玩家阵营记分（S4）并抬威胁（S5）。
- **事件：** 生成器挂一个周期 timer（或订阅现有 sector tick）；战斗驱动器同理。二者都**只读写数据态记录**，不触碰物化 grid（`TryApplyAbstractDamage` 只改 `DataTargetFloorCount`，物化时才由 `TrySetFloorCount` 落到 grid）。
- **数据：** 生成节奏 / 规模系数 / perFleetSize 做成可调常量或 CVar，便于 playtest。舰队编成模板另起 YAML（可后置）。
- **验收：** 无玩家节点每周期发生一次 AI-vs-AI 结算、失败方舰队战力下降直至消失；玩家在场的节点**从不**发生抽象结算（`CanResolveAbstractCombat` 返回 false）；威胁上升会让新生成舰队规模变大。
- **ref：** STARSYS-008（180s 抽象结算节奏）/ STARSYS-010（阵营计时生成）、FLEET-001/005（编成与规模 = 难度+威胁）/ FLEET-009（AI-vs-AI 骰子结算）。
- **S7 剩余缺口（地基之上的下一增量）：**
  1. ✅ **faction-on-wake（2026-09-24 已完成，见上方状态块）：** 数据舰唤醒后已正确带 NSVHostile 阵营并与玩家交火；安全阀确保 sector-parked 玩家/encounter 舰不被误 faction。
  2. ✅ **`NsvAbstractCombatSystem`（2026-09-25 已完成）：** CVar `nsv.bluespace.strategy.combat_interval` 驱动，按节点分组 Available 数据舰，门禁 `CanResolveAbstractCombat` 且已物化节点须 Sleeping；power 加权骰子，负方掉层、归零 `TryDestroyDataShip`；阵营敌对读原型 `Relations`。顺带修复 parked grid 上炮塔脱锚导致战力恒 0（`FullComplementTurretCount` 在注册时拍照）。AI-vs-AI 死亡不计分（`DataStateDeathDoesNotScore`）。测试 `NsvAbstractCombatSystemTest`。
  3. 🟡 **编成：** spawner 已同时播种 NSVFederal 驻军 + NSVHostile（双阵营）；**destroyers/battleships/supply 配比 YAML 未做**，仍是单一舰型 `gust_2`。

## 5. 延后步骤（S1–S7 收束后再做，先占位不展开）

### S8 — 完成后「继续 / 返航」投票延长局（对应 G2 收束点）

> **玩起来是什么样：** 达成通关条件后不强制结束，全员投票决定：继续深入（追加更难的目标、更高的回报）还是立刻返航落袋为安。给一局一个有张力的收尾抉择。

> **实现状态：** 🟡 投票本体已在 **S3** 提前实现（extend 60min / end）；本步剩余的「extend 追加更难目标 + 抬威胁基线」尚未做。

- **目标：** 达成收束阈值后不强制结束，给玩家一个「继续深入（追加更难目标）/ 立刻返航（落袋为安）」的风险选择。
- **要点：** 投票交互已在 S3 落地（复用 `Phase = Extending`，S1 已预留）。**剩余工作：** extend 目前只延长 60min，未追加更难目标、未抬威胁基线——补这两项时扩 `TryStartOutcomeVote`。返航胜利路径（Home 抵达钩子）已在 S3 落地。
- **ref：** ROUND-010/011, MISSION-004。

### S9 — 惰性目标催办升级（reminder）（对应 G3 补充）

> **玩起来是什么样：** 如果你长时间原地摸鱼不推进目标，系统会逐级催你——先提示，再扣分，最后甚至派封锁舰队来堵你，逼你别磨蹭。

- **目标：** 目标长时间无进展时逐级提醒，惩罚闲置、推动持续行动。
- **要点：** 计时器驱动的分级 reminder（BUI 提示 → 扣分 → 最终「生成封锁舰队」，最后一级依赖 S7）。**不照抄**原型 `next_difficulty_increase` 死变量（见 §7）。
- **ref：** ROUND-006, MISSION-003。

### S10 — M2 多玩家岗位 + 最小损管（对应 G8，独立切片可并行）

> **玩起来是什么样：** 一艘船上飞行 / 炮手 / 工程三个岗位各有真实分工，工程师要处理会削弱战力、可修复的故障——让多人协作真正成立，而不是一个人全包。

- **目标：** Pilot/Gunner/Engineer 三岗位有真实取舍，且 Engineer 有可修的、会降战力的故障。
- **要点：** 与回合骨架正交，可与 S1–S7 并行；损管故障必须与岗位同批交付（不能先宣称 Engineer 成立、故障留到以后）。详见 `nsv_game_loop_design.md` M2。
- **ref：** 属 NSV 协作核心，非 ref 01/02 回合流程范围。

## 6. 数据模型增量汇总（所有步骤新增的字段 / 类型一览）

| 载体 | 新增 | 步骤 | 备注 |
| --- | --- | --- | --- |
| `NsvBluespaceCampaignRuleComponent`（新建，Server） | `Phase` / `Objectives` / `Score` / `ThreatElevation` / `Outcome` | S1/S4/S5 | 回合权威状态容器，挂在 GameRule 上 |
| `NsvCampaignObjective`（新建，轻量抽象） | `Status`(INPROGRESS/COMPLETED/FAILED/OVERRIDE) / `Target` / `Tally` / `Kind` | S2 | 首个实现 `PerformJumps`；照 MISSION-001 |
| shared 回合摘要组件 / BUI 状态片段（新建） | 目标清单 + 分数 + 威胁读数 | S1/S5 | 挂现有导航控制台 / 战略图 BUI，威胁**必须可见** |
| `NsvBluespaceEncounterPrototype`（改） | `Kind`（`NsvBluespaceEncounterKind`：Destroy/ClearSystem/Hold） | S6 | 缺省=Destroy，向后兼容 |
| `encounters.yml`（改） | `NSVClearSystemContract` / `NSVHoldContract` + 填入节点 `encounterPool` | S6 | `alpha-3`=[Patrol, ClearSystem]，`beta-1`=[ClearSystem, Hold] |
| `NsvCCVars`（改） | `nsv.bluespace.encounter.hold_duration`（300s） | S6 | Hold 计时 |
| `NsvStrategyFleetSpawnerSystem` / `NsvAbstractCombatSystem`（新建，Server） | 生成节奏 / 规模系数 / perFleetSize（常量或 CVar） | S7 | registry 原语的调用方；不物化 grid |
| 节点 YAML `Reward` / `FuelCost` | 语义升级：从「纯 BUI 显示」→ 参与计分 / （后续）扣 fuel | S4/G7 | 字段已存在，补消费逻辑 |

## 7. 反模式：不照抄原型的死代码 / bug

实现「意图」而非复制原型缺陷（v1 §2、ref 已点名）：

- `bounty_pool` 支付 proc 从未被调用（STARSYS-011 死代码）→ 计分/奖励走**任务状态权威**，不做未接线的奖池。
- `next_difficulty_increase` 死变量（ROUND-007）→ 威胁增长用真实计时器驱动（S5），不留死变量。
- `fleet_trait` 中立区判定恒为真（STARSYS 待解 5）→ S7 生成节点判定要真正读 faction/type，别写恒真分支。
- `ai_behaviour` AI 侵略性开关（FLEET-021 死代码）→ 首版不做该开关，避免引入未接线状态。
- `nsv_mission` 空间站任务层（MISSION-017/018 已弃用）→ 不移植。
- 无衰减、无显示的威胁（ROUND-007 只做一半）→ S5 **必须**同时做衰减下限（0）与 BUI 显示。

## 8. 开放决策（实现前需拍板）

| # | 决策 | 建议 / 现状 |
| --- | --- | --- |
| D1 | 回合骨架接入点：GameRule 的 started/ended vs 直接订阅 `RoundStartedEvent`/`RoundEndedEvent` | 二者皆可用（均已存在、由 GameTicker raise，见 §2）；实现前审计选其一，倾向 GameRule（天然回合作用域 + 清理） |
| D2 | 胜利收束口径：票数达标 / 清指定节点 / 跳跃数达标 / 任一完成 | 建议票数（S4），因与奖励耦合，须先定 P2 结算口径 |
| D3 | 「主船」定义（S3 失败判定的落点） | 最简起点：发起 encounter 的首个 participant shuttle；多船时的权威归属待定 |
| D4 | 奖励结算权威：任务控制器 vs 经济系统记「已结算」 | 权威来自任务状态，防重复领取（沿用 M4 结论） |
| D5 | 抽象战斗对玩家局的隔离强度：`CanResolveAbstractCombat`「玩家在场即禁」是否足够 | 首版够用（该不变量已实现）；更强隔离后置 |
| D6 | 13 节点能否支撑 S7 的 AI 演化（原型约 60 节点） | 先在 13 节点验证生成/结算跑通，扩图成本 vs 收益另评 |


