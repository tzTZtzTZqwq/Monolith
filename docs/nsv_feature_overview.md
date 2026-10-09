# NSV 战役模式：已实现功能总览

版本：2026-10-09 · 分支 `feature-maingameloop` @ `bd23a5e998`

这是一份「现在有什么、怎么开、怎么测」的总览。设计和推导过程见：

- `feature_maingameloop_v2.md`：主循环 S1–S10（回合骨架、计分、威胁、遭遇、AI 舰队）。
- `feature_nsv_next_phase.md`：对照 NSV13 原型的缺口 N1–N17 与实施顺序。

## 1. 现状一句话

一局 `nsvflagship` 就是一艘旗舰 jupiterG：全员按岗位出生在船上，听取任务简报，给 CTLA-160 跃迁核心充能、加等离子燃料，在 13 节点的蓝空星图上跳跃，完成三种遭遇合约攒分，同时应付后台 AI 舰队、越来越高的威胁和 Naval Command 的催促；最后回 Home 判胜，或旗舰被毁、跃迁核心离线 3 分钟判负。

## 2. 怎么开一局

管理员控制台：

```
setgamepreset nsvflagship
restartroundnow
```

- **Debug 构建要先 `forcemap NsvJupiterG`**：Debug 会加载 `Resources/ConfigPresets/Build/development.toml`，里面 `game.map = "NFDev"` 会覆盖预设的地图池，还关掉了大厅（`cvar game.lobbyenabled true` 可打开）。Release 构建不加载这份配置。
- 另有旧预设 `nsvcampaign`：Frontier 标准玩法（前哨站、自己买船）上叠加 campaign 规则，保留作对照。
- 推荐启动命令（服务器窗口崩溃后不关）：
  `cmd /c 'dotnet build SpaceStation14.slnx -c Debug && start "Server" cmd /k dotnet run --project Content.Server -c Debug --no-build && start "Client" dotnet run --project Content.Client -c Debug --no-build'`
- 服务器日志：本地 `bin/Content.Server/server_config.toml` 的 `[log] enabled = true`，日志在 `bin/Content.Server/data/logs/`。

## 3. 一局的流程（玩家视角）

| 阶段 | 发生什么 | 实现 |
| --- | --- | --- |
| 开局 | 全员在 jupiterG 上按岗位出生；船自动成为 campaign 旗舰并带 IFF | 预设 `MonoNsvFlagship`、游戏地图 `NsvJupiterG`（N1） |
| 简报 | 开局 3 分钟后 Naval Command 公告：目标、胜利条件、失败条件 | N6 |
| 目标 | 本局随机 6–10 次跳跃（PerformJumps） | S2 |
| 跃迁 | 导航台选相邻节点；要求跃迁核心**和**跃迁控制台都有电、核心充满（60 秒）、燃料够（节点 fuelCost × 1 张等离子板材）；返航按当前节点扣燃料 | N4 |
| 遭遇 | alpha-3 巡逻（打掉目标核心）、beta-1 清场（打掉所有敌舰核心）、charlie-4 坚守（敌火下撑过倒计时）；完成前不能离开；完成加节点 Reward 分 | S6 |
| 计分 | 完成遭遇加节点分，击毁带计分组件的敌舰 +1；分数实时显示在导航台 Campaign 面板 | S4 |
| 威胁 | 25 分钟宽限后每分钟 +1，击毁 +1，完成目标 −3，最低 0；威胁越高，后台敌舰越多 | S5、S7 |
| 催促 | 每 15 分钟无进展升一级：警告 → 扣分 ×3 → 派封锁舰队到船员所在星区（敌舰核心是拦截器，打掉才能跳走） | N6、N7 |
| 结束 | ① 全目标完成后投票「继续 60 分钟 / 结束」；② 分数 ≥ 5 时跳回 Home 节点判胜；③ 旗舰网格被删除判负；④ 跃迁核心被毁或断电持续 3 分钟判负 | S3、S4、N3 |

## 4. 星图（`NSVBluespaceStrategicMap`，13 节点）

| 节点 | 类型 | 阵营 | 遭遇 | 奖励 |
| --- | --- | --- | --- | --- |
| Sol、alpha-1 | Home | 联邦 | 无（回这里判胜） | 0 |
| alpha-3 | PiratePatrol | 敌对 | 巡逻 Patrol | 4 |
| beta-1 | PiratePatrol | 敌对 | 清场 ClearSystem | 2 |
| charlie-4 | PiratePatrol | 敌对 | 坚守 Hold | 3 |
| 其余 8 个 | 小行星 / 求救 / 未知信号 | 中立 | 无 | 1–3 |

节点从遭遇池里抽取用的是原型里固定的 seed，每局结果相同，所以每个节点只放一种遭遇。

## 5. 后台世界（战略层，S7）

- **数据舰队：** 每 3 分钟在没去过的敌对节点补足敌舰（数量随威胁增长，每节点最多 4 艘）；联邦节点（Sol、alpha-1）另有 2 艘联邦驻军。舰船以「数据」形式停放在一张暂停的暂存地图上，不参与物理模拟。
- **AI 对 AI 抽象战斗：** 每 3 分钟，在无人的联邦节点上让敌对双方掷骰交战，输家掉 34% 地板，掉光即被摧毁；不计入玩家分数。
- **物化：** 玩家第一次跳到节点、或唤醒休眠星区时，驻留舰船会被放回真实地图，带上阵营，按受损程度残缺。
- **已知局限：** 舰队不会在星图上移动（N5 未做），被打掉的会被生成器补回。

## 6. 旗舰 jupiterG 与岗位

地图：`Resources/Maps/_Mono/Supercapitals/jupiterG.yml`（用户在 jupiter 基础上改造，61 × 83 格，约 6,200 个实体）。船上有 CTLA-160 跃迁核心与控制台、蓝空导航台、2 个驾驶台、3 个火炮控制台、18 座炮塔、7 组 SMES、主力舰护盾、医疗和化学、市场与卖货台。

| 岗位 | 名额 | 出生位置 |
| --- | --- | --- |
| 舰长 Captain | 1 | 舰长室 |
| 舰桥军官 Bridge Officer | 2 | 舰桥 |
| 战术军官 Tactical Officer | 3 | 舰桥 2、火炮服务器机房 1 |
| 军械技师 Munitions Technician | 2 | 禁闭室 |
| 总工程师 Chief Engineer | 1 | 工程部 |
| 工程师 Engineer | 3 | 工程部 2、AME 室 1 |
| 医疗官 Medical Officer | 2 | 医疗部 |
| 船员 Crew | 不限 | 原有 12 个出生点 |

门禁暂未分区：全员都能开船上的门（门是海盗门禁，所有岗位都带 Pirate）；舰长另有全部权限，工程和医疗带本部门权限组。无游玩时长要求。

**改地图注意：** 文件是 CRLF 换行，Git Bash 的 `sed -i` 会把它全转成 LF；用脚本改要按字节处理。在地图编辑器里保存前先从磁盘重新载入，否则会覆盖掉外部改动（已发生过两次）。

## 7. 管理员工具

**`nsvsectormonitor` 管理面板**（三个标签页，顶栏显示星区容量、上一次操作结果、刷新按钮）：

- **Campaign：** 阶段、胜负、分数、威胁、campaign 时间、简报状态、提醒级别和倒计时、延长剩余、目标进度；分数和威胁可 ±1/±5 或直接设定；播报测试按钮（发简报、发下一级提醒、派封锁舰、强制投票），触发的都是真实效果。
- **Sectors：** 所有星区实例的状态、网格和实体数、休眠倒计时等。
- **Starmap & fleets：** 星图、各节点驻留的数据舰、在节点上生成数据舰、把数据舰移到别的节点。

**其他：**

- 右键实体 → 「Designate campaign flagship」：手动指定旗舰（`nsvflagship` 预设下船已自动指定，不用手动）。
- 跃迁控制台右键 → 「Open power monitor」：原来的电力监控。
- `vv` 改组件字段：比如把跃迁核心 `NsvCampaignCriticalSystem` 的 `GracePeriod` 改小，快速测试判负。

## 8. CVar 一览

| CVar | 默认值 | 作用 |
| --- | --- | --- |
| `nsv.campaign.victory_score_threshold` | 5 | 回 Home 判胜所需分数 |
| `nsv.campaign.threat_grace_period` | 1500 | 被动威胁增长前的宽限（秒） |
| `nsv.campaign.threat_growth_interval` | 60 | 威胁增长间隔（秒），≤0 关闭 |
| `nsv.campaign.threat_growth_amount` | 1 | 每次增长量 |
| `nsv.campaign.objective_threat_negation` | 3 | 完成目标时降低的威胁 |
| `nsv.campaign.extension_duration` | 3600 | 投票延长的时长（秒） |
| `nsv.campaign.briefing_delay` | 180 | 简报延迟（秒），负数关闭 |
| `nsv.campaign.reminder_interval` | 900 | 停滞催促间隔（秒），≤0 关闭 |
| `nsv.campaign.reminder_score_penalty` | 1 | 第 2–4 级提醒各扣的分 |
| `nsv.campaign.blockade_size` | 2 | 封锁舰队舰数 |
| `nsv.campaign.blockade_distance` | 400 | 封锁舰队到达距离（米） |
| `nsv.campaign.blockade_fallback_threat` | 5 | 船员不在星区时改加的威胁 |
| `nsv.bluespace.drive.required` | true | 跃迁是否必须有核心和控制台 |
| `nsv.bluespace.drive.charge_time` | 60 | 核心充满时间（秒） |
| `nsv.bluespace.drive.fuel_per_cost` | 100 | 每点 fuelCost 消耗的燃料单位（100 = 1 张板材） |
| `nsv.bluespace.encounter.hold_duration` | 300 | 坚守遭遇的倒计时（秒） |
| `nsv.bluespace.strategy.fleet_spawn_interval` | 180 | 后台舰队补足间隔（秒），≤0 关闭 |
| `nsv.bluespace.strategy.fleet_baseline_size` | 1 | 每个敌对节点的基础舰数 |
| `nsv.bluespace.strategy.fleet_threat_per_ship` | 5 | 每多少威胁多一艘 |
| `nsv.bluespace.strategy.fleet_max_per_node` | 4 | 每节点敌舰上限（联邦驻军另算） |
| `nsv.bluespace.strategy.federal_garrison_size` | 2 | 联邦节点驻军数 |
| `nsv.bluespace.strategy.combat_interval` | 180 | 抽象战斗间隔（秒），≤0 关闭 |
| `nsv.bluespace.strategy.combat_damage_fraction` | 0.34 | 每次战败损失的地板比例 |
| `nsv.bluespace.sectors.total_soft_capacity` | 100 | 星区总数软上限（只显示） |
| `nsv.bluespace.sectors.active_soft_capacity` | 15 | 活跃星区软上限（只显示） |

跃迁核心的两档耗电（充能 15 kW、充满 1.5 kW）和离线判负宽限（180 秒）不是 CVar，写在 `Resources/Prototypes/_NSV/Bluespace/Drive/drive.yml` 的原型字段里。

## 9. 关键文件索引

| 内容 | 位置 |
| --- | --- |
| campaign 规则（计分、威胁、投票、简报、催促、判负、管理接口） | `Content.Server/_NSV/GameRule/NsvCampaignRuleSystem*.cs`、`Components/` |
| 遭遇（分派、巡逻、清场、坚守） | `Content.Server/_NSV/Bluespace/Encounters/` |
| 星区、跃迁、导航台、拦截、跃迁核心 | `Content.Server/_NSV/Bluespace/Sectors/` |
| 舰队注册、生成器、抽象战斗、封锁舰队 | `Content.Server/_NSV/Bluespace/Strategy/` |
| 管理面板 | `Content.Server/_NSV/Administration/NsvSectorMonitorEui.cs`、`Content.Client/_NSV/Administration/UI/` |
| 预设 | `Resources/Prototypes/_NSV/game_presets.yml` |
| 旗舰游戏地图与站点 | `Resources/Prototypes/_NSV/Maps/flagship.yml` |
| 岗位与出生点 | `Resources/Prototypes/_NSV/Roles/Jobs/flagship_jobs.yml`、`Entities/Markers/flagship_spawn_points.yml` |
| 跃迁核心与控制台 | `Resources/Prototypes/_NSV/Bluespace/Drive/drive.yml` |
| 星图与遭遇 | `Resources/Prototypes/_NSV/Bluespace/Starmap/starmap.yml`、`Encounters/encounters.yml` |
| CVar | `Content.Shared/_NSV/CCVar/NsvCCVars.cs` |

## 10. 测试

```
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj -c Debug --no-build --filter "FullyQualifiedName~Tests._NSV|FullyQualifiedName~NsvCampaignRuleTest|FullyQualifiedName~NsvFlagshipGameMapTest"
```

截至本版本 126 个全部通过（约 2–4 分钟）。集成测试默认关闭 `nsv.bluespace.drive.required`（测试船上没有跃迁核心），驱动器测试自行打开。原型序列化测试有 2 个失败来自 main 上早已存在的问题（AI 核心的 HTN、`ArmorySmg` 缺失、空的星区模板 ID），与本分支无关。

## 11. 已知限制与下一步

- **舰队不移动**（N5）、**回合目标只有跳跃**（N8）、**没有武器故障和子系统降级**（N2）。
- **门禁未分区**、岗位装备是测试版；第二批岗位（大副、军需官、陆战队等）待对应玩法。
- 旗舰判负还没有「全舰结构度」；目前靠跃迁核心离线和网格被删除。
- 世界生成会在旗舰附近放杂物；没有撤离船。
- 舰船账户只有卖货收入（N10）。

完整清单与优先级见 `feature_nsv_next_phase.md`。
