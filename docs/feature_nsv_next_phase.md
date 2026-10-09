# NSV 下一阶段：对照 NSV13 原型的缺口与优先级

版本：2026-10-06 · 基线分支 `feature-maingameloop` @ `d8ee4b5579`

本文对照 `docs/NSV_prototype_ref/` 全部 27 篇 NSV13 逆向参考文档，列出**本分支和 Monolith 底座都还没有**、且对 NSV 核心循环（一艘船员齐备的战舰穿越星图、战斗、完成 campaign）重要的机制，按优先级排序。主循环 S1–S7 的现状见 `feature_maingameloop_v2.md`。

每一项缺口都用 grep 在 `Content.Server/_NSV`、`_Mono`、`_NF`、`_Crescent` 等目录核实过。引用的行号截至上述基线。

## 1. 总的判断

舰船层面的硬件，Monolith 基本都已具备：

| 已有能力 | 位置 |
| --- | --- |
| 火控与制导 | `Content.Server/_Mono/FireControl` |
| 护盾（含功率消耗、过载） | `_Mono/ShipShields`、`_Crescent/ShipShields` |
| 雷达点、热信号探测、隐形 | `_Mono/Radar`、`_Mono/Detection`、`_Mono/CloakHeat` |
| 近防炮、flak 弹、诱饵弹、可击落导弹、导弹锁定告警 | `90mm_ammo.yml`、`flarelauncher.yml`、`_Mono/TargetSeekingAlert` |
| 修船工具（快照式整体重建） | `Content.Shared/_Mono/ShipRepair` |
| 装填链 | `_Mono/AmmoLoader` |
| 反应堆 | AME、TEG、`_FarHorizons` 裂变反应堆 |
| 舰载钻机、打捞 | `_Mono/Drill`、`_NF/Salvage` |
| 小队与 Overwatch | `Content.Server/_Rat/Squad` |
| 禁止 FTL 的基础组件 | `Content.Shared/Shuttles/Components/NoFTLComponent.cs` |

缺的主要是 **NSV 特有的规则层**，分三块：

1. **「一艘船员齐备的战舰」这个前提本身还不成立**：没有开局全员登舰，没有战舰岗位。
2. **伤害不会转化成船员要处理的任务**：没有故障、降级、濒死窗口。
3. **星图是静态的**：舰队不移动，没有追猎、拦截和催促。

## 2. 缺口清单

成本标记：S = 小（一两个系统内的改动）、M = 中、L = 大。

### 第一梯队：没有它们，NSV 核心循环立不起来

#### N1. 开局全员在旗舰出生 + 战舰岗位与权限

> **实现状态：🟡 测试版已实现（2026-10-08），纯 YAML，不改 C#；2026-10-09 用户游戏内验证通过（开局出生、门禁、导航台、跃迁核心和电力都能用）。** 用户定：纯战役、旗舰用 jupiterG、岗位先只有舰长和船员。Debug 构建下开发配置（development.toml）会把 game.map 设成 NFDev，需先 `forcemap NsvJupiterG`。
> - **新预设 `MonoNsvFlagship`（别名 `nsvflagship`）**：只有 `NsvCampaign` 规则，`supportedMaps: NsvFlagshipMapPool` 强制用旗舰地图；不带 NFAdventure（前哨站、船坞、买船）和各种事件调度器。旧的 `MonoNsvCampaign`（NFAdventure + campaign）保持不动。
> - **游戏地图 `NsvJupiterG`**（`Resources/Prototypes/_NSV/Maps/flagship.yml`）：`isGrid: true` 加载 jupiterG 这个单网格，关闭随机旋转和偏移；`gridComponents` 直接给船加 IFF 和 `NsvCampaignFlagship`，**自动成为 campaign 旗舰**，不再需要管理员 verb。站点 key 必须是船上 `BecomesStation` 的 `Jupiter`。
> - **站点 `NsvFlagshipStation`**：继承基础站点、岗位出生、船员记录、扇区服务（缺了扇区服务，银行和记录会静默失效）。
> - **岗位**（`Resources/Prototypes/_NSV/Roles/Jobs/flagship_jobs.yml`，部门 `NsvFlagship`）：`NsvCaptain` 1 人（AllAccess + General + Pirate + GrandVizier + PDVCommand），`NsvCrew` 不限（General + Pirate）；都不设游玩时长要求。权限按 jupiterG 原有的海盗门禁配，不改门。
> - **出生点**：新增 `SpawnPointNsvCaptain` / `SpawnPointNsvCrew`；jupiterG 上原来的 2 个海盗船长点换成舰长点，6 个海盗 + 6 个大副点合并成 12 个船员点，位置不动；保留原有的 1 个 late-join 点。
> - **测试**：`NsvFlagshipGameMapTest`（按游戏地图加载后成为站点、两个岗位和名额、自动旗舰和 IFF、各岗位都有出生点）、`NsvJupiterGMapTest`（地图加载无错误、跃迁核心/控制台/导航台各一台）。
> - **已知限制**：服务器设置了 `game.map` 会覆盖预设的地图选择；世界生成仍会在船附近放小行星等杂物；没有撤离船，回合靠 campaign 规则结束。岗位、装备、游玩时长要求都是测试版，之后再细化。
- **参考：** 25 JOB-001/005/007，22 SEC-012。
- **NSV13：** 全体玩家开局就是同一艘战舰的船员。新增 Munitions（军械）部门和 Bridge、Pilot、MAA、军械技师等岗位；军械类权限决定谁能用火控、战斗机和弹药设备。
- **现状：**
  - `Resources/Prototypes/_NSV/game_presets.yml` 仍以 NFAdventure 为底，玩家在前哨站出生，然后自己买船。
  - 旗舰只能用管理员 verb 指定（`NsvCampaignFlagshipComponent` 的注释："there is no automatic player-ship entry yet"）。
  - 没有 Pilot / Gunner / Engineer 这类战舰岗位。Mono 的派系岗位可以参考：`Resources/Prototypes/_Mono/Roles/Jobs/TSFMC/`。
- **为什么重要：** 「全员守一艘船」是所有 NSV 玩法的前提。旗舰判负、损管、多人分工都依赖它。做好之后旗舰可以自动指定，不再需要管理员 verb。
- **成本：** M–L，主要工作是旗舰地图、出生点和岗位原型。

#### N2. 武器故障 + 子系统功能降级
- **参考：** 10 MUNI-002/006/007，15 号，12 QUAD-004；也是 `nsv_game_loop_design.md` 中 M2 的硬性要求。
- **NSV13：** 火炮每打 N 发会故障拒射，要拧开面板上油才能恢复；舷侧炮会积碳，弹药架会卡壳。
- **现状：**
  - 炮台、推进器、护盾发生器只有「完好」和「被摧毁」两种状态（`base_launcher.yml` 里是 4000 伤害阈值），没有「受损 → 离线 → 修好后恢复」的中间态。
  - `Content.Shared/_Mono/Weapons/Ranged/Overheat/GunOverheatComponent.cs` 只降射速和精度，不会锁死武器，而且**没有任何原型用到它**。
  - ShipRepair 是整体快照重建，不是逐个子系统修复。
- **为什么重要：** M2 的完成条件是「损伤会降低火力或机动，维修能恢复」，现在做不到。做了之后 Gunner 和 Engineer 才有真正的分工。
- **成本：** 故障锁定 S，功能降级 M。

#### N3. 结构临界倒计时
- **参考：** 03 OMAP-005 / OVERMAP-007，15 DAMCTRL-011，09 COMBAT-018。
- **NSV13：** 船体结构归零不会立刻沉没，而是进入 15 分钟倒计时，期间舱内随机爆炸；船员把结构修回 20% 以上就能解除。
- **现状：** `NsvCampaignRuleSystem.OnFlagshipTerminating` 在旗舰实体被删除时立刻判负，没有缓冲。全舰也没有一个整体结构数值。
- **为什么重要：** 这是损管玩法的高潮，Engineer 的「救舰」时刻全靠它。
- **成本：** M，需要先定义「全舰结构度」，例如按网格瓦片或关键实体的完好比例。与 N1（自动旗舰）、N2（降级）天然配合。

#### N4. FTL 充能、燃料与电力

> **实现状态：✅ 已实现（2026-10-07）。** 用户选定复用 CTLA-160 重型蓝空核心的图像和机器。
> - **原型 `NSVBluespaceDriveCore`**（`Resources/Prototypes/_NSV/Bluespace/Drive/drive.yml`）：继承 `MachineHeavyFTLDriveCore`，保留外观、2500 耐久和原有 `FTLDrive`；耗电改为两档（充能时 `chargingLoad` 15 kW，充满后 `idleLoad` 1.5 kW，都写在 drive.yml 里），充能和护盾、武器抢电（普通控制台 FTL 照常可用）；新增 `NsvBluespaceDrive` 组件和只收等离子的材料存储（上限 30 张板材，手持板材点击驱动器即可装填）。
> - **原型 `NSVBluespaceDriveConsole`（2026-10-08 补上，用户要求与原版一样成对）：** 继承 CTLA-160 控制台，跃迁要求同一艘船上核心**和**控制台都在、都有电，控制台被毁同样跳不了。点开是新的状态窗口（充能进度条、板材 x/30、核心和控制台的供电、当前能否跃迁及原因）；原来的电力监控挪到右键菜单「Open power monitor」。
> - **`NsvBluespaceDriveSystem`**（`Content.Server/_NSV/Bluespace/Sectors/`）：有电时充能，`nsv.bluespace.drive.charge_time`（默认 60s）充满；断电按同样速度流失；驱动器被毁，充能和燃料一起丢失。
> - **跃迁条件**（`NsvBluespaceSectorTravelSystem` 的三条路径）：船上有一台**有电、充满**的驱动器，且燃料 ≥ 节点 `fuelCost` × `nsv.bluespace.drive.fuel_per_cost`（默认 100 单位 = 1 张板材）。跳往节点按目标节点计费；**返航按当前节点计费**（用户选择，与普通跃迁相同）；进入模板星区不耗燃料但要充能。检查在创建目标星区之前完成，失败没有副作用；FTL 真正启动后才清空充能、扣燃料。
> - **开关：** `nsv.bluespace.drive.required`（默认 true）。关掉则恢复「任何船都能跳」。集成测试默认关闭（`PoolManager.Cvars`），驱动器测试自行打开。
> - **导航台：** 星图页节点详情里新增「Jump core」一行：未安装 / 无电 / 充能百分比 / 就绪，以及板材数；燃料不够的节点不可选，跃迁和返航按钮按条件禁用，尝试跃迁时弹出具体原因（缺核心、无电、充能中 x%、燃料 x/y）。充能每跨过 5% 刷新一次控制台。
> - **注意：** 在 N1（旗舰）落地前，玩家船上默认没有这台驱动器。默认开启时，要先用管理员生成 `NSVBluespaceDriveCore` 装到船上，或把 `nsv.bluespace.drive.required` 设为 false。
> - **测试：** `NsvBluespaceDriveTest.DriveChargesWhilePoweredAndJumpSpendsChargeAndFuel`、`TravelRefusedWithoutDriveBeforeCreatingSector`。
- **参考：** 04 FTL-003/004/005，13 ENG-017/023，02 STARSYS-006。
- **NSV13：** 跃迁前要由工程部门给驱动塔供燃料和电力，塔越多充得越快；塔被打掉，充能流失、跃迁取消。跃迁和护盾、武器争同一份电力预算。
- **现状：**
  - `Content.Shared/_Mono/Ships/FTLDriveComponent.cs` 只有射程、冷却、启动时间，不耗电也不耗燃料。
  - `CanFTL`（`ShuttleSystem.FasterThanLight.cs`）不检查资源。
  - 星图节点配了 `fuelCost`，但只在导航台显示，全仓库没有任何地方扣除（G7 遗留）。
- **为什么重要：** 「跳一次」会变成全船协作的事；撤退开始有代价，「继续深入还是返航」的取舍才有分量。
- **成本：** S–M。

### 第二梯队：让 campaign 有压力和变化

#### N5. 舰队在星图上移动、追猎与增援
- **参考：** 05 FLEET-006/007，04 FTL-019，02 STARSYS-010。
- **NSV13：** 舰队每 5–10 分钟沿跳线移动；拦截舰队会持续朝玩家所在星系重算路线；脱战后补回兵力。
- **现状：** `NsvFleetRegistrySystem.TryFtlShip` / `TryMoveShipToNode` 唯一的调用方是管理员界面（`NsvSectorMonitorEui`）。`NsvStrategyFleetSpawnerSystem` 只给静态节点补兵，舰队从不移动。
- **为什么重要：** 没有这一层，星图只是一张静态关卡表；有了它，路线选择才有压力和风险。也能解决抽象战斗目前「生成器补员抵消损耗、前线永远打不完」的问题。
- **成本：** M（寻路加计时器，复用现成 API）。

#### N6. 任务简报 + 目标停滞催促

> **实现状态：✅ 已实现（2026-10-06）。** 落点在 `NsvCampaignRuleSystem`：
> - **简报：** campaign 开始 `nsv.campaign.briefing_delay`（默认 180s，负数关闭）后，以「Naval Command」名义全服公告一次，列出回合目标、胜利条件（回 Home 且分数 ≥ 阈值）和失败条件（旗舰被毁）。
> - **催促：** `nsv.campaign.reminder_interval`（默认 900s，≤0 关闭）内没有目标进展就发下一级提醒：第 1 级只警告；第 2–4 级每次扣 `reminder_score_penalty`（默认 1）分，最低到 0；第 5 级广播 `NsvCampaignBlockadeEvent`，由 `NsvCampaignBlockadeSystem` 往船员当前星区派封锁舰队（`blockade_size` 艘，默认 2；距船员 `blockade_distance` 米，默认 400），每艘的 AI 核心都是 FTL 拦截器（见 N7）。船员不在任何蓝空星区时改为加威胁（`blockade_fallback_threat`，默认 5）。之后从第 1 级循环。
> - **算作进展的事：** 跳跃计入目标、完成遭遇。两者都会把停滞计时和提醒级别清零。
> - **只在胜负未定时运行：** 投票开始（Outcome 已定）或进入延长阶段后不再催促。
> - **封锁舰的生成方式：** 直接把 gust_2 加载进船员所在的活星区（与星区舰船生成器相同）。船员所在星区本来就醒着，不必绕道暂存地图（早先担心停放会让锚定实体脱离网格，已实测证伪）。舰船登记为该节点驻留舰，随星区休眠、唤醒。
> - **测试：** `NsvCampaignRuleTest.BriefingAnnouncedAfterDelay`、`StalledObjectivesEscalateAndProgressResets`，`NsvFtlInterdictionTest.BlockadeArrivesAtCrewSectorAndInterdicts`。
- **参考：** 01 ROUND-005/006，06 MISSION-002/003（即 v2 计划中的 S9）。
- **NSV13：** 开局约 3 分钟打印任务简报；之后每 15 分钟没有进展就升级警告，第 5 次直接在船员所在星系刷一支封锁舰队。
- **现状：** `NsvCampaignRuleSystem` 只有被动威胁增长，没有简报，没有催促，也没有惩罚。
- **为什么重要：** 防止原地挂机；新玩家开局就知道该干什么。
- **成本：** S，性价比最高。

#### N7. 拦截舰阻断跃迁（+ 紧急跃迁）

> **实现状态：✅ 拦截已实现（2026-10-06）；紧急跃迁未做。** 新组件 `NsvFtlInterdictorComponent` + `NsvFtlInterdictionSystem`（`Content.Server/_NSV/Bluespace/Sectors/`）：
> - **判定：** 同一张地图上存在带拦截器组件、未被删除、**有电**、且阵营与该船敌对的实体时，船被拦截。组件挂在 AI 核心上，所以打掉或打断电核心即可解除。
> - **拦截范围：** 蓝空导航的「跳往节点」「返航」两条路径（`NsvBluespaceSectorTravelSystem`），以及普通穿梭机控制台 FTL（订阅 `ConsoleFTLAttemptEvent`）。
> - **导航台：** 被拦截时跳跃和返航按钮禁用，撤离一栏显示 "Interdicted: destroy the hostile interdictor to jump out."；拦截器出现或消失时自动刷新所在星区的控制台。
> - **目前的来源：** N6 的封锁舰队。组件可以直接写进任意原型，给特定敌舰或遭遇加拦截。
> - **测试：** `NsvFtlInterdictionTest.HostileInterdictorBlocksFtlUntilDestroyed`（友方拦截器不拦、敌方拦截器同时挡住控制台 FTL、删掉核心后解除）。
- **参考：** 04 FTL-011/012/006/007。
- **NSV13：** 同星系有敌方拦截舰时，正常跃迁被阻断；紧急跃迁需要双卡授权，能绕过拦截，代价是随机落点加结构损伤。
- **现状：** 只有遭遇状态会限制撤离。`ConsoleFTLAttemptEvent` 的订阅者只有 `_NF/ForceAnchorSystem`、Nukeops 和 Salvage，NSV 没有订阅。
- **为什么重要：** 制造「先打掉拦截舰才能走」的明确战术目标，同时给绝境留一条逃生路。
- **成本：** 拦截 S（订阅事件即可）；紧急跃迁 M，可以后做。

#### N8. 更多种类的回合目标
- **参考：** 06 MISSION-007/011/013/014，19 CARGO-006~010，20 RES-006~008。
- **NSV13：** 除跳跃计数外，还会随机加上击毁 N 支舰队、捕获指定敌舰、清空指定星系、货运投递、异常扫描等目标。
- **现状：** `NsvCampaignObjectiveKind` 只有 `PerformJumps`。Destroy / ClearSystem / Hold 是节点内的局部遭遇，不是回合目标。降威胁的钩子已有（`NsvCampaignObjective.ThreatNegated`）。
- **建议：** 先做「击毁 N 支舰队」（复用击毁计分路径）；货运投递复用卖货台加白名单校验，不必做货运鱼雷；异常扫描可接研究点。
- **为什么重要：** 现在每局玩法重复，延长投票后也没有新目标可加（对应 S8 剩余项）。货舱、科研、航线规划都能借此参与进来。
- **成本：** 每种 M。

### 第三梯队：丰富玩法，可以往后放

| # | 机制 | 参考 | 现状 | 成本 |
| --- | --- | --- | --- | --- |
| N9 | 单舰战备状态：General Quarters / Condition Zebra（红警全舰播报、红灯、自动关防火门、按警报开门禁） | 22 SEC-004/005/006 | 警报等级挂在整个星区服务上（`_NF/SectorServices/services.yml`），不是按舰船分 | S–M |
| N10 | 舰船账户的稳定收入（定期拨款 / 合同结算） | 19 CARGO-003/005/011 | `NsvCargoHubComponent.Balance` 只在卖货和退款时增加；遭遇奖励和击毁奖励都只加 Score | S |
| N11 | 自主近防 / AMS 模式（自动反导、自动反舰、flak 常开） | 11 PD-005/006/008 | 近防硬件都有，但 `_Mono/FireControl` 没有任何自动开火逻辑 | M |
| N12 | 机械化修复套件（耗电耗材的「井」+ 每侧泵，按侧分配修复预算，过度会过热） | 15 NANO-001~004，12 HULL-008 | 只有手持、按次数消耗的 ShipRepair 工具 | M |
| N13 | 多种舰型编成 + 补给舰/撤退等 AI 分工 + 按在线人数缩放难度 | 05 FLEET-002/005/014/015，01 ROUND-008 | 只有 gust_2 一种舰型；生成规模只看威胁，不看人数 | L（人数缩放单独做 S） |
| N14 | 打残 → 锁定 → 夺舰（关 AI + 改 IFF 阵营） | 18 SALVAGE-002/006/008，11 PD-010，17 BOARD-009 | 物理对接、破门已能做；`_Mono/GridClaimer` 只有通用认领，无「关 AI + 改阵营」钩子 | M |
| N15 | 敌方 AI 登舰（NPC 突击队，规模随难度） | 17 BOARD-001/002/016，24 ANTAG-001 | `_Mono`、`_NSV` 无任何登舰逻辑；可复用 Mono 人形佣兵 NPC、`GridRaiderComponent` | L（只做 NPC 登舰舱 M） |
| N16 | 幽灵玩家驾驶敌舰 / 敌舰冷冻舱幽灵角色 | 24 ANTAG-002/003/012，05 FLEET-008 | `_NSV` 无 GhostRole；需把敌舰从 `NsvShipAiSystem` 交给玩家 | S–M |
| N17 | 敌舰动态播报、接敌喊话、按阵营决定入场方向 | 05 FLEET-007，02 STARSYS-004，04 FTL-013，08 SENSOR-014 | 只有导航台 popup；舰队物化统一放在同一锚点 | S |

低优先的小项：护盾「容量 vs 回充」调节旋钮（12 SHLD-001/003，S）；装填手解保险步骤（10 MUNI-012，S）。

## 3. 建议刻意不做的

| 类别 | 例子 | 理由 |
| --- | --- | --- |
| 与真实网格物理重复 | 牛顿飞行、惯性阻尼、撞角加成（07）；四象限装甲、装甲板计数、中继弹丸、hullburn（12、15）；星图与舰内分层、跑步机 Z 层（03） | Monolith 是真实网格，墙体和爆炸天然有方向性和舱内后果；扇区实例化已替代分层 |
| BYOND 专属系统 | Stormdrive 控制棒、RBMK、nucleium、受限等离子、PDSR 气体生态（12、13、14） | 底座已有 AME、TEG、裂变反应堆，再写一套代价大、收益小 |
| 已有等价物的武器细节 | 弹药状态机细节、导弹装配线、MPAC、轨道炮锻弹、开炮震伤耳膜（10）；BSA 等锁定特定地图的战略武器（27） | 弹匣、装填器、工作台配方、易爆弹药已覆盖 |
| 已有等价物的感知层 | DRADIS 双阈值、主动 ping、数据链（08） | Mono 雷达 + 热信号已等价；电子战只在做 N14 时顺带 |
| 部门内容（Monolith 已覆盖） | 医疗外科（21）、研究科技树（20，商人卖设计盘可当市场商品）、安保细节（22）、全息地图（26，已有 StationMap/NavMap）、军衔与换装等岗位杂项（25） | SS14 / Monolith 已有对应系统 |
| 非 PvE campaign 模式 | PvP 银河征服、Hardmode、Badlands 程序星区（01、02）；血裔等反派（24） | 都是另开的模式 |
| 原型自身的死代码 / 未实现 | bounty_pool（02，从未被调用）；玩家布雷（只有 AI 能布）；登舰鱼叉（NSV13 本身未实现） | 照抄无意义 |
| 高成本特效 | 误跳的相位幽灵、小行星实化（04） | 与核心循环无关；做紧急跃迁时只保留「随机落点 + 船体损伤」 |
| 次要载具与采矿 | 战斗机弹射器、挂点、座舱私有气体（14、16）；采矿磁铁、小行星笼、独立采矿舰（18） | Monolith 小飞船有真实内部和对接；采矿不是 NSV 核心循环 |

可以复用为遭遇内容的：Fist of Sol 这类 Boss 舰可直接做成 ClearSystem 遭遇；拦截场（`NoFTLComponent`）可做成遭遇修饰。

## 4. 建议实施顺序

| 阶段 | 内容 | 理由 |
| --- | --- | --- |
| P1 ✅ | N6 简报 + 催促，N7 拦截舰（2026-10-06 完成） | 都是 S，在现有 campaign / 遭遇代码上马上见效 |
| P2 | N1 旗舰出生 + 岗位 | 后续一切的前提；完成后旗舰自动指定 |
| P3 | N2 故障 + 降级，N3 临界倒计时，N4 FTL 资源 | 一起交付 M2 的 Engineer 与损管玩法 |
| P4 | N5 舰队移动，N8 更多回合目标 | 星图动起来，每局有变化；补上 S8「extend 追加目标」 |
| P5 | 第三梯队按需挑选 | N9/N10/N17 便宜且独立，可穿插；N14/N15 放在 M4 之后 |

## 5. 与现有文档的关系

- `feature_maingameloop_v2.md`：S1–S7 已完成，S8 剩余项并入 N8，S9 即 N6，S10（多岗位 + 损管）即 N1 + N2 + N3。
- `nsv_game_loop_design.md`：M2 的 Pilot / Gunner / Engineer 与损管对应 P2–P3。
- `feature_nsvcargodesign.md`：N10 稳定收入、N8 货运投递目标与之衔接。
