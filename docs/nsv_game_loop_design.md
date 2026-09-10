# NSV 多人太空船游戏循环路线图

## 目标

```text
港口补给 → FTL 出航 → 扇区遭遇 → 舰战与损管
→ 完成目标 / 打捞 → FTL 返航 → 修船、补给、升级 → 更高风险任务
```

设计重点：FTL 的风险选择、NSV13 的多人岗位协作、SS14 的连锁故障，以及 Monolith 的港口与长期经济。

## 当前可玩纵向切片

| 范围 | 状态 |
| --- | --- |
| 共享临时 Sector、模块布局、地图生成与两分钟回收 | 已实现；自动化覆盖布局、生成和回收路径，仍需更多游戏内多人 smoke test |
| FTL 进入、相邻节点航行、返航、导航控制台 | 已实现；普通/debug map 可进入任意节点，节点 sector 仅能前往图连接的邻居；active contract 同时阻止相邻航行与返航 |
| 测试扇区 | TSFMC 前哨站、1 艘 `NSVHostile` GUST、4–6 个陨石场 |
| NSV playtest 船 | `kestrel-G`（免费）与 `Spekter-G`（price 1）已加入 shipyard console 可购买；二者均指向 `_NSV` 的 spekter grid 受控副本，并通过 `minPriceMarkup: 0` 显式豁免 `NoShipyardShipArbitrage` 套利断言（评估价约 252551，在有真实经济的服务器上可被拆解刷钱，仅限 playtest） |
| GUST | `_NSV` GUST 副本直接生成 `NsvBluespacePatrolCore`；NSV HTN、faction-aware 目标查询及 Mono 移动/火控复用均已实现 |
| NSV faction | 已实现实体优先、root grid fallback、有向默认关系与 sector-local runtime override |
| M3 PatrolContract | 已实现最小服务器权威流程：共享 controller、唯一 hostile patrol core Objective、幂等完成、节点/返航撤离门禁、外部返航记录与 sector 回收清理；导航控制台会显示星区介绍、合同目标、状态与撤离资格；目标 GUST 激活时获得红色 Star 雷达标记，参与者 PDA 可安装 NSV 导航程序查看只读状态并在完成时收到铃声通知；PDA 程序的服务端状态推送管线已有集成测试覆盖（`NsvBluespaceNavigationCartridgeTest`） |
| M6 战略星图 | 固定 YAML 图和五个节点已实现；节点身份、稳定 seed、双向边验证、node-keyed sector cache、服务端授权、战略图 BUI 与自动化覆盖已具备。节点点击坐标问题已修复（绘制曾误加 `PixelPosition` 导致显示位置低于命中位置），游戏内节点选择已验证通过 |
| 世界内目标标记、奖励、打捞、经济 | 未实现 |

当前可实际游玩的服务器权威闭环是：

```text
普通 / debug map 打开导航控制台
  → 选择任意战略节点并 FTL 进入
  → 到达 PiratePatrol 时自动激活 PatrolContract
  → 摧毁唯一 hostile GUST 的 patrol core
  → ExtractionOpen
  → 前往相邻节点，或 FTL 返回最初出发 map
```

战略节点包含 `Home`、`Asteroid`、`Distress`、`PiratePatrol`、`UnknownSignal`。节点 YAML 提供二维位置、threat、reward、fuel cost、faction、sector template、稳定 seed、encounter pool 与双向连接；`StarmapId + NodeId` 是运行时 sector identity，template 仅决定内容。`Asteroid` 节点使用 `NSVBluespaceHunterSector`（舷侧猎手舰 + 小行星场，threat 3，无 encounter 门禁）；相同 template 复用的隔离由测试专用星图 `NSVBluespaceTestStarmap` 的两个共享节点覆盖。

进入时，导航控制台会显示星图和节点信息；节点内只会将相邻节点标为可选。点击节点先更新客户端选择，随后点击跳跃按钮才发送目的地请求；服务器会重新执行节点、图边、encounter、FTL 与 shuttle 所属验证。节点点击曾因绘制坐标误加 `PixelPosition` 而整体下移、命中不到，该问题已修复并在游戏内验证通过；绘制与命中检测现在都使用控件本地像素坐标。抵达带 `NSVPatrolContract` 的节点后，控制台会刷新合同的目标、状态、参与者和撤离资格。当前仍没有世界内目标图标、完成弹窗或奖励界面。GUST 继续由 `NsvBluespacePatrolCore` 驾驶：它继承 Mono 核心的硬件和操纵层，但使用 `_NSV` 的根 HTN 计划、攻击计划和 `NsvNearbyShipTargetsQuery`。该查询保留距离、存活、grid、供电和 `ShuttleAIIgnore` 规则，并只接受 NSV relation resolver 判定为 hostile 的 `NsvShipTarget`；不修改 Mono 的通用 `NearbyNpcTargetsQuery`。

## 边界与前提

- `_NSV` 不修改 Salvage、`GameTicker`、通用 NPC faction 或通用 targeting。
- `SectorSystem` 只生成和回收世界，不处理任务、胜负、奖励或 AI 决策。
- legacy direct-sector 路径按模板 ID 共享，首个请求的 seed 决定活动实例布局；战略星图路径改用 `StarmapId + NodeId` 缓存，节点 YAML 的 seed 决定可重建布局。`PerMission`、`PerParty` 和持久实例仍是后续独立架构决策。
- TSFMC 前哨站带原生设施与防御内容；Encounter 必须明确其是安全港、第三方还是可攻击目标。

## 里程碑

### M0 — 固化 Sector / FTL

核心实现已交付；剩余工作是补齐自动化与游戏内验证：

- 控制台 BUI 请求和权限；
- FTL 进入、返航、多舰共享与两分钟回收；
- 生成器中途失败后的 map 回滚；
- 再次进入已回收扇区会创建新实例。

**完成条件：** 一艘具备 FTL 的玩家船可稳定进出扇区；多艘船共享同一活动 map；异常和空扇区不会泄漏 map 或预约记录。

### M1 — NSV Faction Relation 与目标适配（已实现）

`NsvBluespaceFactionSystem` 已提供 NSV 私有的有向关系 resolver：

```text
Friendly / Neutral / Hostile
未配置关系 = Neutral
实体 faction 优先；否则读取 root grid faction
sector-local override 优先于 YAML 默认关系
```

`NsvNearbyShipTargetsQuery` 已将 faction-aware 筛选接到 NSV patrol HTN：它不修改 Mono 的通用 `NearbyNpcTargetsQuery`，也不重做 GUST 的移动、瞄准或火控。

**已达成的完成条件：** hostile faction 的 GUST 可锁定玩家舰；同 faction 舰船不会被锁定；`ShuttleAIIgnore` 仍始终排除；默认关系、关系方向、实体覆盖 grid 归属和 sector-local override 均有集成覆盖。

### M2 — 一场可玩的舰战

只支持一艘玩家船对一艘 GUST，并提供三个持续职责：

| 岗位 | 最小决策 |
| --- | --- |
| Pilot | 接近、拉开距离、制造射界、撤退 |
| Gunner | 等待射界、瞄准、射击与冷却管理 |
| Engineer | 处理至少一种会降低战力的故障 |

最小损管应与此里程碑一起交付：例如炮台断电、推进器损坏或局部失压；不能先宣称 Engineer 角色成立、却在后续里程碑才有可维修故障。

**完成条件：** 舰船损伤会实际降低火力或机动；维修能恢复能力；玩家可击毁目标并在合同开放撤离后返航。被迫返航、提前撤离和失败结果属于后续 encounter settlement 规则。

### M3 — Encounter 与 Objective（最小运行时已实现）

`Sector` 与 `Encounter` 已在运行时分离：

```text
Sector = 地图与基础模块
Encounter = 激活对象、敌对关系、目标、结果
```

当前 `PatrolContract` 在第一艘 shuttle **实际抵达** Ready sector 后创建共享 server controller；后续 shuttle 加入同一 contract。controller 指定扇区唯一 hostile GUST 的 patrol core，并以其 `EntityTerminatingEvent` 完成 Objective：

```text
Active
  → 指定 core 终止
  → ObjectiveComplete
  → ExtractionOpen
  → participant 可正常 FTL 返航
```

状态 guard 保证 Objective 只推进一次；sector 回收会在删除 map 前 dispose controller，避免 map cleanup 被判为成功。合同 active 时返航被拒绝，成功后只在 FTL 实际抵达原 map 时记录 participant 已撤离。

**尚未实现：** 击杀归因、post-Ready encounter spawn、`Settled`、断线或舰船被毁规则、奖励、掉落和战利品。世界内反馈已提供最小版本：激活时目标 GUST root grid 安装红色 Star 雷达 blip（完成或 map 回收时移除）；参与者可以安装 NSV 导航 PDA 程序（`NsvBluespaceNavigationCartridge`）查看只读 sector/encounter 快照，Objective 完成时收到 PDA 铃声通知。

### M4 — Extraction、Loot 与 Reward

```text
击毁敌舰 → 残骸 / 物资 → 玩家装船 → FTL 返回 → 合同结算 + 出售
```

奖励权威应由任务状态决定，不直接在击杀时加货币。需处理提前 FTL、断线、扇区回收和未拾取战利品，防止重复领取。

**完成条件：** 一次 Patrol Contract 可获得一种可运输战利品与一次结算奖励，且无法重复结算。

### M5 — Repair / Refit

接入已有港口经济，先提供修理、弹药与一种舰船升级。资源必须有限，使“继续探索”与“立刻返航”存在取舍。

### M6 — 战略星图（核心实现完成，等待游戏内 UI 验证）

首版固定全局 `NSVBluespaceStrategicMap`，由 YAML 定义以下节点：

```text
Sol ─ alpha-1 ─ alpha-2 ─ alpha-3 ─┬─ beta-1 ─ beta-2 ─┐
                                  ├─ charlie-4 ─────────┼─ delta-1 ─ delta-2 ─ delta-3
                                  └─ charlie-1 ─ charlie-2 ─ charlie-3 ─┘(回到 charlie-4)
```

13 个节点分四个集群:`Sol`/`alpha-1` 为联邦安全区,`alpha-2` 小行星带,`alpha-3` 海盗巡逻(挂海盗市场),`beta-1` 由 Hunter 舰守卫,`charlie` 为中立未知信号群,`delta` 为求救信标集群(`delta-3` 是死胡同终点)。

每个节点定义本地化名称/介绍、二维 UI 坐标、类别、threat、reward、fuel cost、faction、sector template、稳定 seed、encounter pool 与 reciprocal connections。原型加载拒绝重复或空 node ID、非有限位置、负数值、重复/自环/未知链接以及单向边。

运行时以 `StarmapId + NodeId` 创建和缓存 map，不再以 template 混淆战略地点与生成内容；两个节点即使复用 template 也有独立 map、seed、encounter controller、participants 和两分钟 cleanup。普通或 debug map 可进入任意有效节点；节点内只能进入邻居。相邻请求在创建目的 map 前验证，并和原出发地返航一样都必须通过当前 encounter 的 extraction 门禁。

导航控制台同窗显示战略图。客户端只持有安全的节点投影，点击节点选择目的地并显示元数据，跳跃或返航请求再由服务器重新授权。`PiratePatrol` 的 pool 选择 `NSVPatrolContract`；空 pool 节点不创建合同。节点间航行不丢失首次入图时的返航坐标或 faction snapshot，只有 FTL 完成事件才提交目标节点 ownership。

自动化已经覆盖图结构验证、同 template 节点隔离、debug map 入图、非相邻拒绝、active encounter 同时阻止相邻移动和返航、完成后到空 encounter 节点、以及返回原 map。节点点击坐标问题已修复（绘制误加 `PixelPosition`），红线诊断已移除；新 GUI 的节点选择与按钮已通过游戏内验证，实际 FTL 流程与图边/门禁的端到端游戏内确认仍待完成。

**未实现：** fuel 尚未实际扣除，reward/经济/动态占领/任务选择和图的持久化均不在 M6 首版范围。

## M4 之后的首次多人试玩目标

```text
港口接 Patrol Contract
→ 补给
→ FTL 进入 Sector
→ 一艘敌对 GUST 攻击
→ Pilot / Gunner / Engineer 协作
→ 击毁或撤离
→ 打捞
→ 活着返航
→ 一次奖励结算
→ 修理与再次选择
```

观察：Pilot 是否持续决策、Gunner 是否依赖射界、Engineer 是否能改变战局、撤退是否有价值、额外战利品是否值得冒险。

## 下一步

1. 在游戏内补完 M6 smoke test 的剩余项：debug map 任意入图、节点内仅邻接可选、active encounter 阻止相邻跳跃与返航、ExtractionOpen 后两种离开方式恢复；节点点击与按钮已验证。
2. 在游戏内验证新的目标反馈：目标 GUST 的红色 Star 雷达标记、NSV 导航 PDA 程序的只读快照刷新（值显示的 fragment 游离与 ClipText 布局两个客户端 bug 已修复，数据通路已经游戏内 debug 工具确认，视觉与铃声验证待重启游戏）、Objective 完成时的 PDA 铃声通知。cartridge 目前只能通过生成 `NsvBluespaceNavigationCartridge` 实体获得，尚未加入 PDA 预装列表、购买渠道或地图摆放。
3. 制作 M2 的一船对一船战斗与最小损管纵向切片，给 Pilot、Gunner、Engineer 提供真实取舍。
4. 实现 M4 的 Extraction、Loot 与 Reward，并在此之前定义 `Settled`、超时、断线和舰船被毁的结算规则。
