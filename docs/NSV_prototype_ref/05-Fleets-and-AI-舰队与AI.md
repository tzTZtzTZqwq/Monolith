> 本文为 research/evidence/fleets_ai.md 的中文翻译。

# 舰队、NPC 飞船与 AI (Fleets, NPC Ships & AI)

真相来源：仅当前代码（分支 `master`）。行号引用为本次整理的 `file:line`。
主要文件：`nsv13/code/modules/overmap/ai-skynet.dm`、`fleet_types.dm`、`fleet_combat/combat_handling.dm`、
`fleet_combat/dice_datum.dm`、`types/*.dm`、`lance.dm`、`traders.dm`、`traders_items.dm`、
`fleet_ftl_pathfinding.dm`、`nsv13/code/__DEFINES/skynet.dm`、`__DEFINES/fleets.dm`、
`nsv13/code/controllers/subsystem/overmap_mode.dm`、`.../starsystem.dm`、`.../factions.dm`。

## 系统概览 (System Overview)

有两个相互独立的层：

1. **战术 AI（“SKYNET 2”）** —— 一个 `ai_controlled = TRUE` 的 `obj/structure/overmap` 每个物理 tick 运行一个*效用评分（utility-scoring）*目标循环（通过 `slowprocess()` 调用 `ai_process()`，`physics.dm:135`）。每个 tick 它对每个 `datum/ai_goal` 子类型评分并执行得分最高的一个（`choose_goal()`，`ai-skynet.dm:1276-1294`）。目标涵盖：重新武装/维修、搜索并摧毁、蜂群/骑枪、神风、登船、保卫补给、撤退、巡逻、拍蝇（反战机）以及静止。AI 驱动移动（`move_toward`/`circle_around`/`move_away_from`）并发射其武器 datum（`ai_fire`/`ai_elite_fire`）。

2. **战略舰队层** —— 一个 `datum/fleet`（`ai-skynet.dm:23-66`）是 `taskforces`（`fighters`/`destroyers`/`battleships`/`supply`）的容器。它按计时器在 `datum/star_system` 图上行走，且仅当其所在星系未被加载时，战斗才以抽象的骰子掷点结算（`fleet_combat/`）。舰队由 `datum/faction`（`factions.dm:70`）创建并派出，规模随 `SSovermap_mode` 缩放。

舰队中的飞船始终是 AI（`ai_controlled`）；少数子类型是*可登船*的（拥有 `possible_interior_maps`，因此船员可以发起登船行动——系统 17）。两艘超级飞船 `fistofsol` 与 `battleship` 位于生成黑名单上。

## 核心玩法循环 (Core Gameplay Loop)

1. `SSstar_system.fire()`（每个子系统 tick）为每个阵营调用 `faction.send_fleet()`（`starsystem.dm:60-61`）。拥有基地星系的阵营每 `fleet_spawn_rate`（辛迪加 30 分钟，NT 40 分钟；`factions.dm:155,158,133`）向中立区/自己星系生成一支舰队。
2. `fleet.New()` → `assemble()` 从 `applied_size` 所定规模的类型列表实例化飞船（`ai-skynet.dm:773-862`）。
3. 舰队 `move()` 在一个随机化计时器上运行（默认 5-10 分钟，`ai-skynet.dm:61-64`），依据 `fleet_trait`（invasion/neutral-zone/border-patrol/defense）或经由通往 `goal_system` 的 Dijkstra 路径选择下一个星系。
4. 若抵达星系有玩家飞船，`encounter()` 会向它们喊话/挑衅（`ai-skynet.dm:265-268,681-733`）。玩家随后在已加载的 Z 层中与 AI 战斗。
5. 若该星系**未**加载且驻有两支敌对阵营的舰队，`handle_combat()` 每 `COMBAT_CYCLE_INTERVAL` = 180 秒结算一回合骰子（`starsystem.dm:46-58`、`combat_handling.dm:15`）。
6. 摧毁（或 IFF 骇入）一支舰队中的每艘飞船会调用 `defeat()` → 公告、玩家奖励信号、阵营票据损失、威胁上升，以及一笔信用点赏金支付（`ai-skynet.dm:296-345`、`starsystem.dm:371-379`）。

## 机制 (Mechanics)

### FLEET-001
断言：目标选择是一个对所有 `datum/ai_goal` 子类型的逐 tick 效用投票；得分最高的正值获胜，并有回退机制以确保 AI 总会有*某个*目标。
面向玩家的后果：敌方行为是涌现式的——一艘受伤/缺弹的飞船会明显脱离，一架弹尽的蜂群战机会明显撞击，一艘航母会明显逃跑；你可以通过剥离敌方补给船来诱使这些行为。
证据：`GLOB.ai_goals` 由 `subtypesof(/datum/ai_goal) - typesof(/datum/ai_goal/human)` 一次性填充（`ai-skynet.dm:1278-1281`）；循环选取 `newScore > best_score`（`1284-1294`）；分数位于 `__DEFINES/skynet.dm:2-10`（`MAXIMUM 1000, SUPERCRITICAL 500, CRITICAL 100, SUPERPPRIORITY 75, HIGH_PRIORITY 60, PRIORITY 50, DEFAULT 25, LOW_PRIORITY 15, VERY_LOW_PRIORITY 5`）。除非 `ai_controlled` 且 flag 位匹配，否则 `check_score` 返回 0（`871-876`）。由 `ai_process`（`1490-1503`）调用，而后者由每个物理 tick 的 `slowprocess` 调用（`physics.dm:122-135`）。
条件：需要 `required_ai_flags` 的目标对缺少该位的飞船不可见。
例外 / 覆写：孤舰（无舰队、无骑枪）——`seek` 目标返回 `AI_SCORE_MAXIMUM`（1000），因此孤立的 AI 总是猎杀。带 supply flag 的飞船被排除在 `seek`/`defend` 之外。
置信度：HIGH（高）

### FLEET-002
断言：数值目标层级（按代码实际评分方式）。
面向玩家的后果：可预测敌方舰队接下来会做什么。
证据（每个目标的 `check_score`）：
- `stationary` = 1001（MAXIMUM+1），仅 `AI_FLAG_STATIONARY`（`1264-1273`）。
- 孤立 `seek` = 1000；舰队 `seek` = 25（`932-948`）。
- `kamikaze` = 500（SUPERCRITICAL），仅 `AI_FLAG_SWARMER`（`1063-1081`）。
- battleship `defend` = 100（除非受损/缺弹 → 49）；`retreat`（supply）= 100（`1156-1187`）。
- `rearm` = 75（低 HP）/ 50（低弹药）（`894-908`）。
- `board` = 60；supply `patrol` = 60（`1088-1101`、`1204-1219`）。
- `swarm` = 30（一旦编入骑枪则为 50）；非 supply `patrol` = 25；`seek/flyswatter` = 50（`965-977`、`1247-1262`）。
- `defend`（非 battleship）= 15（`1117`）。
条件：平局按列表顺序打破（用 `>` 而非 `>=`），因此先创建的目标赢得平局。
例外 / 覆写：许多状态下目标返回 0（不可见），例如当不存在补给船或你是最后一艘补给船时 `rearm` = 0（`897-901`）；没有补给线时 `defend` = 0（`1159-1161`）。
置信度：HIGH（高）

### FLEET-003
断言：目标选择会扫描*当前已加载星系中的每一艘飞船*，并对其施加射程/可见性/阵营过滤。
面向玩家的后果：AI 只能看到物理存在于其星系 Z 层的飞船；处于 `sensor_profile` 之内的隐形/潜行飞船以及未加载星系对它是不可见的。
证据：`seek_new_target()` 复制 `current_system.system_contents`，打乱，并返回第一艘满足以下条件的飞船：不在黑名单、不是 `src`、位于 `max(max_tracking_range, ship.sensor_profile)` 之内、阵营不同、`z` 相同、`sensor_visibility >= SENSOR_VISIBILITY_TARGETABLE`（0.70）、不是 `essential`、通过可选的质量/内部过滤（`ai-skynet.dm:1717-1746`）。成功后它调用 `add_enemy()` 和 `fleet.start_reporting(ship, src)`。
条件：`max_tracking_range` 默认 50（`1303`）；某些主力舰覆写（kadesh 70，fistofsol 90；`syndicate.dm:498,535`）。一次新的 `send_radar_pulse()` 会在 5 秒内将 `max_tracking_range` **翻倍**并增加传感器轮廓惩罚（使发波者远更容易被看到）——`radar.dm:82-91`、`RADAR_VISIBILITY_PENALTY` = 5 秒。
例外 / 覆写：若什么都没找到，它会回退到 `fleet.shared_targets` 中的任意条目（其他队友报告的目标）（`1742-1745`）。`warcrime_blacklist`（逃生舱、小行星、独立商人）被跳过（`1326,1723`）。`essential`/`nodamage` 的空间站永不被作为目标（`1315-1316,1369,1407,1583,1727`）。
置信度：HIGH（高）

### FLEET-004
断言：AI 战斗开火使用带射程惩罚的武器选择；“精英”飞船用*所有*火炮进行齐射（alpha-strike）。
面向玩家的后果：`AI_FLAG_ELITE` 主力舰（Nightmare/Stalker/Kadesh/精英航母、Solgov interdictor、海盗炮艇）每 tick 的威胁远高于同等非精英船体。
证据：`ai_fire()` 选择 `get_ai_range_penalty` 最低的那一门 AI 可控武器并发射（`1366-1397`）。`ai_elite_fire()` 循环*每一门* AI 武器并发射每一门 `can_fire` 的（`1404-1426`）。若 `overmap_dist > max_weapon_range`（默认 50；fistofsol 85），两者都会中止并清除 `last_target`（`1372-1377,1410-1415`）。
条件：弹药耗尽的武器改为调用 `try_initiating_resupply()`（`1383-1387`）。
例外 / 覆写：PDC 炮座自动拦截来袭导弹（`autonomy.dm:55-69`）；flak 有 FULL_AUTONOMY 的地面目标处理（`autonomy.dm:26-52`）。
置信度：HIGH（高）

### FLEET-005
断言：舰队编成为 `destroyers = max(round(size/2),1)`、`battleships = max(round(size/4),1)`、`supply = max(round(size/4),1)`（round = BYOND round，.5 向上取整）。
面向玩家的后果：一支“size 5”的舰队约为 3 艘驱逐舰 + 1 艘战列舰 + 1 艘补给船（约 5 个船体），外加战斗中发射的任何战机。规模同时驱动难度与奖励。
证据：`assemble()`（`ai-skynet.dm:819-862`，注释 `837-842`）。若 `allow_difficulty_scaling`，`applied_size` 取自 `SSovermap_mode.mode.difficulty`，否则用固定的 `size`；然后 `+ round(threat_elevation / TE_POINTS_PER_FLEET_SIZE)`（100）并以 `FLEET_DIFFICULTY_EASY`（2）为下限（`773-795`、`134-153`）。
条件：`mode.difficulty = clamp(ceil(players/10), 1, 5) + escalation`，每 10 分钟重新计算（`overmap_mode.dm:365-370`）。因此按人数缩放的舰队规模为 1-5 + 威胁。固定规模的 boss 舰队忽略此规则（`allow_difficulty_scaling = FALSE`）并使用 `FLEET_DIFFICULTY_*` 常量（EASY 2 … DEATH 30；`skynet.dm:16-22`）。
例外 / 覆写：`SCALE_FLEETS_WITH_POP` 定义存在但无处读取（死代码）。
置信度：HIGH（高）

### FLEET-006
断言：舰队在非战斗时会补充回威胁所规定的兵力。
面向玩家的后果：让一支舰队长时间独处，它会恢复损失；其“奖励”也会随之缩放。
证据：`move()` 首先调用 `try_threat_elevation_reinforce()`（`171-172`）。若星系已加载（`occupying_z`）、当前处于冲突中，或 `world.time < last_encounter_time + TE_REINFORCEMENT_DELAY`（15 分钟），则中止，并要求 `can_reinforce`（NT 舰队为 FALSE）（`134-142,152`）。然后它将每个 taskforce 补足到目标数量，生成新飞船（`154-168`）。
条件：`last_encounter_time` 在战斗时或在某艘飞船被移除时设置（`298`、`689`）。
例外 / 覆写：`threat_elevation_allowed = FALSE` 的舰队（NT，以及 PVP 模式中的所有舰队）忽略威胁项（`152`、`786-789`）。
置信度：HIGH（高）

### FLEET-007
断言：舰队依据 `fleet_trait` 在星图中旅行，而非追猎玩家——拦截舰队除外。
面向玩家的后果：大多数舰队四处游荡/巡逻，可以被避开或伏击；拦截舰队（`/datum/fleet/interdiction*`，Kadesh/Capiens）会横跨银河追击主飞船。
证据：`move()` 依据 `fleet_trait` 分支（`ai-skynet.dm:190-232`）：`FLEET_TRAIT_DEFENSE` 从不移动；`NEUTRAL_ZONE` 只去无阵营/已绘图星系；`BORDER_PATROL` 只去己方阵营星系；`INVASION` 避开己方阵营。目标寻路通过 `navigate_to` 使用 Dijkstra `find_route`（`fleet_ftl_pathfinding.dm:13-94`）。`/datum/fleet/interdiction/move` 每次移动设置 `goal_system = hunted_ship.current_system`（`279-284`），其中被猎杀的飞船是主 overmap（`286-288`）。
条件：舰队会停留在 `world.time < last_encounter_time + combat_move_delay`（默认 10 分钟；拦截 6）期间不离开，或如果舰队中有任何东西对 `COMSIG_GLOB_CHECK_INTERDICT` 以 WEAK/STRONG 作答（`233-238`；`interdiction.dm:15-22`）。
例外 / 覆写：对于辛迪加/海盗阵营，一次移动会播报 `"Typhoon drive signatures detected in [system]"`，除非 `hide_movements`/星系隐藏（`262-264`）。
置信度：HIGH（高）

### FLEET-008
断言：玩家的“幽灵船”遭遇由舰队播种，并需要一个人数阈值。
面向玩家的后果：存活玩家 >15 时，一支交战舰队可在战斗中途生成一艘被幽灵驾驶的敌方飞船；低于 10 时从不如此。
证据：`encounter()`：若目标是敌对、`alpha >= 150`、未被管理禁用，则有固定 **20%** 概率（`!prob(20)` 则 return）轮询幽灵；`player_check > 15` 解锁战机+驱逐舰+战列舰，`> 10` 只解锁战机，否则中止并记录日志（`ai-skynet.dm:681-733`）。
条件：`override_ghost_ships` 管理开关可禁用它（`695-697`）。
例外 / 覆写：若所选飞船列表为空，则使用 `default_ghost_ship`（`716-719`）。
置信度：HIGH（高）

### FLEET-009
断言：NPC 对 NPC 的舰队战斗是仅在未加载星系中结算的抽象骰子。
面向玩家的后果：你永远看不到两支 AI 舰队对射；它们在你身处别处时在屏幕外无声地交换伤害，而你跳入后会发现战后残局（或一个胜者）。
证据：若 `occupying_z`（已加载），`handle_combat()` 返回 `COMBAT_SKIPPED`（`combat_handling.dm:21-22`），需要 ≥2 支 `faction_id` 不同的舰队（`16-27,45-53`）。`fleet_fire` → 每艘开火飞船对一个随机目标飞船 `ship_fire`（`55-64`）。`ship_fire` 掷点：目标掷点（`target_dice × rand(1,target_roll) + target_bonus`）对闪避，然后护甲对伤害；亲和性（匹配 `affinity_flags`）将目标与伤害掷点乘以 1.5（`67-114`）。最终的 `damaging_roll × DICE_DAMAGE_MULTIPLIER`（20）作为 `overmap_heavy` 钝击伤害（`114`）。
条件：在 `SSstar_system` 的 `COMBAT_CYCLE_INTERVAL` = 180 秒周期上对 `contested_systems` 运行（`starsystem.dm:46-58`）。
例外 / 覆写：骰子档案通过 `combat_dice_type` 逐船体设置（`dice_datum.dm`），例如战机有 `affinity_flags = AI_FLAG_SWARMER`，轰炸机 `AI_FLAG_BATTLESHIP`，驱逐舰/巡洋舰/航母擅长对付 supply。
置信度：HIGH（高）

### FLEET-010
断言：击毁一支舰队的所有飞船 → `defeat()` 支付一笔信用点赏金、让阵营损失票据，并*提高*威胁；只有玩家在场才触发奖励路径。
面向玩家的后果：每摧毁一支辛迪加舰队都会给船员一笔现金回报，但也会升级 `threat_elevation`，因此击毁舰队会让未来的舰队更大。
证据：`remove_ship()` → 当 `all_ships` 为空时 `defeat()`（`296-308`）。`defeat()` 给每艘在场的玩家飞船授予 `COMSIG_SHIP_KILLED_FLEET`（`335`），执行 `faction.lose_influence(reward)`（`342`），并由于 `TE_FLEET_THREAT_DYNAMIC = TRUE` 执行 `modify_threat_elevation(TE_FLEET_KILL_THREAT * applied_size)`（10 × size）（`343-344`、`skynet.dm:31-32`）。每艘飞船在 Destroy 时还将其 `bounty` 加入 `SSstar_system.bounty_pool`（`syndicate.dm:101-103`）；资金池会定期拆分到货物/其他账户（`starsystem.dm:371-379`）。
条件：`reward` 默认 100 × applied_size（`59,785`）；海盗 35，tortuga 200。
例外 / 覆写：若星系中没有玩家飞船，则无影响力/威胁变化（`340`）。`/datum/fleet/solgov/earth/defeat()` 强制回合结束（“foothold situation”）（`347-351`）。
置信度：HIGH（高）

### FLEET-011
断言：登船/骇入一艘飞船的 IFF 会翻转其阵营并将其从舰队中移除（计入舰队败亡）。
面向玩家的后果：你可以*偷走*一艘敌方战舰而非摧毁它，且它不再计入你正在清剿的舰队。
证据：`add_ship` 注册 `COMSIG_SHIP_BOARDED` → `remove_ship`（`ai-skynet.dm:807`）。IFF 控制台 `hack()` 发送 `COMSIG_SHIP_BOARDED`，然后翻转 `OM.faction` 辛迪加↔nanotrasen（`iff_console.dm:124-140`）。
条件：`remove_ship` 也在单纯 `QDELETING` 时触发（`806`）。
例外 / 覆写：以辛迪加身份登上主飞船会触发 Solgov 猎手舰队（`iff_console.dm:141+`、`fleet_types.dm:292-357`）。
置信度：HIGH（高）

### FLEET-012
断言：骑枪（Lances）是自动组成的、最多 5 艘 `AI_FLAG_SWARMER` 飞船的队伍，共享一个目标。
面向玩家的后果：战机/轰炸机蜂群会集中火力并围绕一个队长重新集结，而不是四处游荡。
证据：`datum/lance` `maximum_members = 5`，队长 = 第一个成员，队长死亡时重新选取（`lance.dm:6-43`）。`swarm` 目标加入一个有空位的骑枪或创建一个（`ai-skynet.dm:981-989`）。成员若自己没有目标，则采纳 `L.lance_target`，并转告自己找到的目标（`994-1017`）。
条件：距目标 4 格以内时，蜂群飞船只设置 `desired_angle` 并停止移动（横移/撞击）（`1019-1021`）。
例外 / 覆写：`regroup_swarm` 集结点优先级 = supply > battleships > destroyers（`1029-1061`）。若队长失去目标，则清除该目标并让蜂群重新集结（`998-1004`）。
置信度：HIGH（高）

### FLEET-013
断言：一架完全弹尽、且其舰队**没有**补给船的蜂群飞船会神风。
面向玩家的后果：从一个战机密集的舰队中剥离补给航母，会把弹尽的战机变成自杀式撞击。
证据：`kamikaze/check_score` 要求 `AI_FLAG_SWARMER`，若舰队仍有补给船、仍有 `shots_left`、或没有 `last_target`，则返回 0；否则返回 500（`ai-skynet.dm:1063-1081`）。`kamikaze/action` 调用 `move_toward(last_target, ram_target = TRUE)`（`1083-1085`）。
条件：`ram_target` 还会启用一个类似 `ignore_all_collisions` 的加速路径（`1632-1657`）。
例外 / 覆写：未发现。
置信度：HIGH（高）

### FLEET-014
断言：航母会主动为 15 格内的友方 AI 飞船补给与维修（若同阵营，也会维修玩家）。
面向玩家的后果：敌方航母会让其护航舰的导弹/鱼雷/HP 保持满额，因此优先集火航母是一种有效战术。
证据：对 `can_resupply` 飞船（`carriers`、`dreadnought`、`alicorn`、`fistofsol`、Solgov vnc/carrier）的 `ai_process` 会在 `current_system` 中找到一艘缺弹/缺 HP 的同阵营飞船，位于 `resupply_range`（15）内，然后在 5 秒后 `resupply()` 将导弹/鱼雷/`shots_left` 补满至最大值并 `try_repair(max_integrity * 0.1)`（`1526-1556`）。飞船也会在 `ai_self_resupply()` 中自我补给，填充每种弹药池的 25%（`1429-1438`）。
条件：不会治疗一切皆满的飞船（`1535-1536`）。
例外 / 覆写：`rearm` 目标将受损/缺弹飞船*送往*一艘补给船，并在 `AI_PDC_RANGE`（12）内刹车（`888-931`）；带 supply flag 的飞船若是仅存的一艘，则无法给自己重新武装（`900`）。
置信度：HIGH（高）

### FLEET-015
断言：带 supply flag 的飞船很怯懦：它们会逃离跟踪范围内的任何敌对目标，否则就慢速巡逻。
面向玩家的后果：敌方航母/无畏舰会逃跑而非作战，把护航舰一并拖走。
证据：`retreat/check_score`（仅 supply）清除其目标，调用 `seek_new_target(max_distance = max_tracking_range)`，若敌人靠得那么近则返回 100，否则返回 5（`1172-1187`）；若敌人在武器射程内，`retreat/action` 设置 `brakes` 和 `move_away_from(foo)`（`1191-1197`）。`patrol` 给补给船 60，除非正在 `resupplying`（`1213-1218`）。非补给巡逻返回 25（`1200-1219`）。
条件：巡逻在抵达时若 `mines_left >= 1` 会布设一颗水雷（`1227-1228`、`1749-1756`）。
例外 / 覆写：一旦威胁离开武器射程，`retreat/action` 会提前返回（漂移），让舰队重整（`1195-1196`）。
置信度：HIGH（高）

### FLEET-016
断言：`fistofsol` 与 `battleship` 被列入正常生成的黑名单，只通过脚本化 boss 舰队出现。
面向玩家的后果：你永远不会随机撞上 5000 HP 的战列舰；它们只出现在脚本化事件中（Armada 模式、残局）。
证据：`SSstar_system.enemy_blacklist = list(/obj/structure/overmap/syndicate/ai/fistofsol, /obj/structure/overmap/syndicate/ai/battleship)`（`starsystem.dm:12`）；在初始化时从 `enemy_types` 中减去（`67-69`）。两者都仅被 boss 舰队 datum 引用：`earthbuster`（battleship）和 `unknown_ship`/`fistofsol_boss`（`fleet_types.dm:118-124,173-191`）。
条件：`enemy_types`（剩余部分）否则**未被使用**——代码库中不存在消费者——因此该黑名单在很大程度上只是防御性文档。
例外 / 覆写：管理员可通过 `add_blacklist` 添加黑名单（`126-130`）。
置信度：HIGH（高）

### FLEET-017
断言：存在一批固定规模、由脚本触发的 boss/硬核舰队。
面向玩家的后果：某些星系会事件式生成一个固定的、不缩放的噩梦（Rubicon、Dolos Remnants、the Fist of Sol、the Alicorn、the Earthbuster Armada）。
证据：`rubicon`（kadesh，VERY_HARD=10）、`remnant`/`dolos`（WHAT_ARE_YOU_DOING=25）、`unknown_ship`（battleship，size 1）、`fistofsol_boss`（Fist of Sol + 精英航母，size 1）、`hostile/alicorn_boss`（Alicorn，size 1）、`earthbuster`（INSANE=15，由 battleship 率领）——全部 `allow_difficulty_scaling = FALSE`（`fleet_types.dm:109-202`）。`armada` 模式在其最后提醒时生成 `earthbuster`，并以 `interdiction/stealth` 作先锋（`armada.dm:66-92`）；`consequence_five` 向玩家所在星系空降一支 `interdiction` 舰队（`overmap_mode.dm:441-451`）；`clear_system/dolos` 在实例化时生成一支 `remnant` 舰队（`rubicon.dm:49-51`）。
条件：`fistofsol_boss` 仍把一个常量 `faction = FACTION_ID_SYNDICATE` 赋给它，但 `fleet/New()` 无论如何都会从 `faction_id` 覆写 `faction`（`ai-skynet.dm:784`）——仅为表面效果。
例外 / 覆写：`rubicon`/`dolos` 的清理星系目标是标准残局（hardmode 会修正它们，`hardmode.dm:14`）。
置信度：HIGH（高）

### FLEET-018
断言：威胁等级随回合时间与玩家行为被动上升，并直接抬高舰队规模。
面向玩家的后果：船员越久不活动、越频繁光顾辛迪加商店并击毁舰队，生成的舰队就越大。
证据：`SSovermap_mode.fire()` 在 `TE_INITIAL_DELAY`（25 分钟）之后每 10 分钟加 `TE_THREAT_PER_HOUR/6`（约 16.7）（`overmap_mode.dm:218-228`）；hardmode 使用 `/2`（约 3 倍速率）。击毁一支舰队加 `10 × applied_size`（`ai-skynet.dm:344`）；从辛迪加军火商处购买每次加 `TE_SYNDISHOP_PENALTY`（20），除非买家驾驶辛迪加飞船（`traders.dm:203-208`）。威胁向 `applied_size` 加 `round(threat/100)`（`ai-skynet.dm:789`）。
条件：每个新完成的目标*降低* `TE_OBJECTIVE_THREAT_NEGATION`（50）的威胁（`overmap_mode.dm:476-478`）。
例外 / 覆写：威胁永不低于 0（`overmap_mode.dm:216`）；PVP 模式与 NT 舰队禁用它。
置信度：HIGH（高）

### FLEET-019
断言：商人是静止的、不可移动且非常坚固的空间站，用部门信用点向船员出售随机化库存。
面向玩家的后果：船员可以在回合中途通过呼叫控制台购买武器/弹药/战机/维修/矿物，但该站不能被击杀以获取战利品，且仅在通讯范围内开放商店。
证据：`datum/trader` 持有 `stonks`/`sold_items`/`special_offers`；`stock_items()` 将价格随机化为 ×½–×4、库存为 ×½–×2（`traders.dm:43-67`）。作为 `/obj/structure/overmap/trader`（`MASS_IMMOBILE`、`obj_integrity` 3000、`can_move()` 返回 FALSE）经由 JSON 中的 `preset_trader` 或每星系 10% 掷点生成（`starsystem.dm:570-578,1326-1335`；`traders_items.dm:3-30`）。购买需要一次呼叫（`try_hail` 需要 `ACCESS_CARGO`/`ACCESS_SYNDICATE`/`ACCESS_HEADS`）且在范围内（`dist < 30`；`traders.dm:373-374`），并动用 `ACCOUNT_CAR`（NT）或 `ACCOUNT_SYN`（`traders.dm:442-459`）。
条件：商店每 10-20 分钟补货（`traders.dm:326-330`）。特别优惠按阵营票据解锁（`traders.dm:60-66`）。
例外 / 覆写：`Randy Random`（海盗）库存 5 件完全随机的物品（最高 100M 信用点）（`traders.dm:280-324`）。它们也是有效的货运目的地（货运鱼雷目标）。
置信度：HIGH（高）

### FLEET-020
断言：登船舰通过补给舱投放幽灵或 KNPC 来物理登上玩家飞船。
面向玩家的后果：`AI_FLAG_BOARDER` 飞船（Astartes 陆战连护卫舰、海盗登船变体）会靠上来，并把一支登船队投进你的内部。
证据：`board` 目标（得分 60）移动至 8 格内并调用 `try_board`（`1088-1115`）。`try_board` 限制为每 5 分钟一次尝试、每 30 分钟一次玩家登船者生成，然后调用 `ship.spawn_boarders(null, faction)`（`1558-1575`）。`spawn_boarders` 需要 ≥5 名存活玩家，在 ≥20 时轮询幽灵，否则生成 KNPC；数量 = `ceil(1 + difficulty/2)`（`boarding.dm:12-41`）。（完整的登船处理 = 系统 17。）
条件：目标必须拥有 `occupying_levels`（一个内部）且位于 8 格内（`1558-1562`）。
例外 / 覆写：管理 `override_ghost_boarders` 开关可禁用它（`boarding.dm:13-15`）。
置信度：MEDIUM（中）（目标/触发已证实；内部登船流程不在范围内）

### FLEET-021
断言：`ai_behaviour` / `AI_AGGRESSIVE` / `AI_PASSIVE` 对 overmap AI 飞船而言实际上是死代码。
面向玩家的后果：敌方 AI 飞船*始终*咄咄逼人，并按阵营猎杀玩家，无论任何“谨慎”设定；overmap 上没有先开火/后开火的开关。
证据：`ai_behaviour` 被声明（`ai-skynet.dm:1300`）并在许多飞船类型上被赋值（`= AI_AGGRESSIVE`），但唯一读取侵略性特征的代码是用于*人类* NPC 的 `datum/component/knpc`（`knpc.dm:7`）；没有消费者以 `ai_behaviour` 来门控 overmap 的目标获取。敌意完全来自 `seek`/`add_enemy` 的阵营检查。
条件：无。
例外 / 覆写：未发现。
置信度：MEDIUM（中）（否定性断言；在 `nsv13/` 上进行了广泛 grep）

## 敌方飞船目录 (Enemy Ship Catalogue)

仅列实际生成类型（即被某个 `*/ai` 舰队类型列表或 boss 舰队引用）。生命值为 `max_integrity`；武器为 `apply_weapons` 集合。`Boardable` = 拥有 `possible_interior_maps`。

| 类型（路径末尾） | 阵营 | 角色 / flags | ~生命值 | 武器 | AI/可登船 |
|---|---|---|---|---|---|
| syndicate/ai (`Mako`) | syndicate | 驱逐舰 | 300 | gauss, pdc | AI, 可登船 |
| syndicate/ai/mako_carrier (`Sturgeon`) | syndicate | 驱逐舰，发射战机 | 400 | gauss, pdc | AI, 可登船 |
| syndicate/ai/mako_flak (`Mauler`) | syndicate | 驱逐舰，水雷 | 300 | gauss, pdc, flak | AI |
| syndicate/ai/conflagration (`Hellfire`) | syndicate | 驱逐舰，hellfire 鱼雷 | 900 | torp, missile, gauss, pdc | AI, 可登船 |
| syndicate/ai/conflagration/elite (`Nightmare`) | syndicate | 精英驱逐舰 | 1200 | 同上，弹药更多 | AI, 可登船 |
| syndicate/ai/destroyer (`Hammerhead`) | syndicate | 驱逐舰 | 500 | missile, gauss, pdc | AI, 可登船 |
| syndicate/ai/destroyer/elite | syndicate | 精英驱逐舰 | 900 | missile, gauss, pdc, torp | AI |
| syndicate/ai/destroyer/flak | syndicate | 驱逐舰，反战机 | 450 | mac, gauss, flak×2, pdc | AI |
| syndicate/ai/cruiser (`Barracuda`) | syndicate | 战列舰 | 550 | gauss, pdc, missile, torp, mac | AI |
| syndicate/ai/cruiser/elite | syndicate | 精英战列舰 | 1000 | 同上 + missile | AI |
| syndicate/ai/carrier (`redtip`) | syndicate | 补给，发射战机 | 600 | aa, flak, gauss | AI, 可登船 |
| syndicate/ai/carrier/elite | syndicate | 精英补给，战机+轰炸机 | 1400 | aa, flak, gauss | AI, 可登船 |
| syndicate/ai/battleship (`SSV Sol's Revenge`) | syndicate | boss 战列舰（已列黑名单） | 5000 | aa×2, flak, mac | AI |
| syndicate/ai/assault_cruiser (`Inquisitor`) | syndicate | 驱逐舰 | 800 | aa, flak, gauss | AI |
| syndicate/ai/assault_cruiser/boarding_frigate (`Astartes`) | syndicate | 反战机 + 登船者 | 750 | aa, flak, gauss, missile | AI, 可登船 |
| syndicate/ai/gunboat | syndicate | 反战机 | 450 | aa, flak, gauss, missile | AI |
| syndicate/ai/submarine (`Aspala`) | syndicate | 驱逐舰，隐形，水雷 | 500 | light cannon, disruptor torp, flak, gauss, missile | AI |
| syndicate/ai/submarine/elite (`Stalker`) | syndicate | 精英，隐形，phase-2 | 1000 | 同上，弹药更多 | AI |
| syndicate/ai/kadesh | syndicate | boss 战列舰，拦截 | 1200 | aa, mac, vls, flak×2, gauss, missile | AI |
| syndicate/ai/fistofsol | syndicate | boss，发射战机+轰炸机（已列黑名单） | 5000 | twinmac, hailstorm, quadgauss, pdc, flak×3, missile | AI |
| syndicate/ai/fighter | syndicate | 蜂群战机 | 75 | light cannon, missile | AI |
| syndicate/ai/bomber | syndicate | 蜂群轰炸机 | 100 | light cannon, torp | AI |
| spacepirate/ai (`Space Pirate`) | pirate | 反战机，随机配装 | 400 | 5 套随机组合之一 | AI |
| spacepirate/ai/boarding | pirate | 登船者 | 400 | 随机组合 | AI |
| spacepirate/ai/nt_missile | pirate | 鱼雷艇 | 525 | 随机组合，30 torp/missile | AI |
| spacepirate/ai/syndie_gunboat | pirate | 精英战列舰 | 350 | aa, dirty mac, gauss, pdc | AI |
| spacepirate/ai/dreadnought | pirate | 精英补给（旗舰骰） | 5000 | aa, torp, railgun, flak×2, gauss, pdc | AI |
| nanotrasen/ai (`raptor`) | nanotrasen | 驱逐舰（友方） | 300 | gauss, pdc | AI |
| nanotrasen/frigate/ai | nanotrasen | 驱逐舰 | 500 | torp, missile, gauss, pdc | AI |
| nanotrasen/patrol_cruiser/ai | nanotrasen | 战列舰+驱逐舰 | 450 | mac, gauss, pdc | AI |
| nanotrasen/heavy_cruiser/ai | nanotrasen | 战列舰 | 800 | mac, gauss, pdc | AI |
| nanotrasen/battlecruiser/ai | nanotrasen | 战列舰 | 450 | mac, gauss, pdc | AI |
| nanotrasen/battleship/ai | nanotrasen | 战列舰 | 1000 | mac, gauss, pdc | AI |
| nanotrasen/carrier/ai | nanotrasen | 补给，战机 | 700 | mac, aa, flak, gauss | AI |
| nanotrasen/ai/fighter (`Viper`) | nanotrasen | 蜂群战机 | 75 | light cannon, missile | AI |
| nanotrasen/solgov/ai | solgov | 驱逐舰（激光） | 500 | burst phaser, phaser, phaser_pd, laser_ams, shields | AI |
| nanotrasen/solgov/vnc/ai | solgov | 驱逐舰+发射战机 | 250 | burst phaser, phaser_pd, laser_ams, torp, shields | AI |
| nanotrasen/solgov/aetherwhisp/ai | solgov | 驱逐舰 | 750 | 中型套件 + shields | AI |
| nanotrasen/solgov/carrier/ai | solgov | 补给，战机 | 1000 | 中型套件 + shields | AI |
| nanotrasen/solgov/ai/interdictor (`Capiens`) | solgov | 精英，拦截 | 1200 | + flak×2 | AI |
| nanotrasen/solgov/ai/fighter (`Peregrine`) | solgov | 蜂群精英战机 | 125 | burst phaser ×3, shields | AI |
| hostile/ai/alicorn (`SGV Alicorn`) | hostile | boss，隐形，战机 | 4750 | vls, torp, quadgauss, prototype_bsa, flak×3 | AI |
| hostile/ai/fighter (`Rattlesnake`) | hostile | 蜂群战机 | 115 | light cannon, missile, torp | AI |

采矿船（`types/miningships.dm`）是玩家 `MAIN_MINING_SHIP` 船体（Rocinante/Nostromo/Rig），不是 AI 敌人。

## 跨系统依赖 (Cross-System Dependencies)

- **SSovermap_mode / 威胁（系统：回合流程、星系）：** 难度、threat_elevation、escalation 驱动 `applied_size`；贯穿 FLEET-005/006/018 进行补充。
- **星系 / 阵营（`starsystem.dm`、`factions.dm`）：** 拥有图、contested_systems 列表、各阵营舰队类型池（辛迪加池 `fleet_types.dm`/`factions.dm:155`），以及 180 秒战斗节奏。
- **飞船移动 / 物理（`physics.dm`、`overmap.dm`）：** `ai_process` 由 `slowprocess` 调用；`move_toward` 对 `current_system.system_contents` 做射线检测。
- **弹药/武器：** `overmap_ship_weapon` datum、`weapon_control_flags & OSW_CONTROL_AI`、弹药池（`shots_left`/`missiles`/`torpedoes`）、`try_repair`。
- **传感器/隐形（`radar.dm`）：** `sensor_profile`、雷达脉冲双倍射程、submarine/alicorn 上的 cloak_factor。
- **登船（`boarding/boarding.dm`、`knpc.dm`、`ai_interiors.dm`）：** 完整流程 = 系统 17。
- **目标（`gamemodes/overmap/objectives/`）：** `destroy_fleets`、`clear_system`、`system_defence_armada`、`board_ship` 都以舰队败亡/登船信号为关键。
- **商人（`traders.dm`/`_items.dm`）：** 独立经济；与货运投递目标和威胁存在交叉链接。

## 开放问题 (Open Questions)

1. `ai_self_resupply()`（仅补 25%）有调用者吗？Grep 未发现——很可能是死代码；航母改用 `resupply()` 路径。（要确认需对整个调用树 grep 调用者。）
2. `SCALE_FLEETS_WITH_POP` 定义似乎未被读取；没有代码路径依据它分支。
3. `enemy_types`（辛迪加子类型减去黑名单）没有消费者；因此黑名单只是信息性的。
4. `slowprocess` 的确切 tick 间隔（进而目标重新评估频率）取决于 `SSphysics_processing` 节奏；此处未测量。
5. `fistofsol_boss`/`alicorn_boss`/`unknown_ship` 舰队 datum 是否曾被当前游戏模式在回合中途实例化（armada 模式使用 `earthbuster`；其余可能仅为管理/遗留）。
6. `navigate_to` 失败（`plotted_course = FALSE`）与 `move()` 的回退随机选取之间的交互——似乎已被处理（`ai-skynet.dm:182-212`），但未详尽追踪。
