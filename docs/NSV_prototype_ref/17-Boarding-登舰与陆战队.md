> 本文为 research/evidence/boarding.md 的中文翻译。

# 登舰、外遣队与陆战队行动

范围：物理上将人员移出/移上舰船——敌对登舰者（KNPC + 幽灵）、幽灵角色防守方、
敌舰内部空间生成、“hammerlock” EWAR 登舰流程、运输机，以及登舰撞针。班组装备
属于系统 23；打捞计算机/EWAR 控制台基础属于系统 11/18；舰队 AI 攻击性属于系统 5。
下文进行交叉引用，不重复。

## 系统概述

代码中存在两个方向相反的“登舰”，不应混淆：

1. **敌对登舰者登上玩家舰船。** 射程内的一艘 AI 舰船对玩家舰船调用
   `spawn_boarders()`，它会生成 KNPC 士兵，或将该角色提供给幽灵（死亡玩家）。
   这些人通过一个放置在玩家舰船上的补给舱“着陆区”抵达。
2. **船员登上敌舰。** 船员必须 (a) 使敌舰瘫痪，(b) 操作打捞/EWAR 控制台对其
   “hammerlock” 并将其内部空间加载到一个保留 Z 上，(c) 驾驶运输机飞过去并对接，
   (d) 穿过内部空间作战，(e) 用多功能工具破解敌方 IFF 控制台以翻转其阵营。
   这是登舰游戏模式的胜利条件。

登舰鱼叉（`nsv13/code/game/machinery/boarding_harpoon.dm`）是一个 **裸的、无功能的
对象**——仅有 name/desc/icon，没有 proc，没有调用者（见 BOARD-014）。物理登舰是
通过对接 + 内部加载完成的，而非通过鱼叉这个名字。

## 核心玩法循环

- **防守（船员）：** AI 登舰舰船接近至 ≤8 星图格 → 舱体降落到你的舰船上 →
  KNPC（和/或幽灵玩家）使用 `patrol_node` 地标巡逻/作战 → 船员击退他们。
- **进攻（船员）：** 将敌方打至 <50% 完整度 → EWAR“打捞”控制台锁定它
  （`hammerlocked=TRUE`，内部加载，AI 控制禁用）→ 装载陆战队/运输机 → 对接 →
  突袭内部空间（KNPC，可能有幽灵角色防守方）→ 抵达 IFF 控制台 →
  多功能工具破解（2 分钟）→ 阵营翻转 → `COMSIG_SHIP_BOARDED` → 目标完成。
- **幽灵中场循环：** 具有 Boarder 角色偏好的死亡玩家会被询问是否成为
  辛迪加/海盗登舰队，或者可以点击一艘已被登舰的活跃辛迪加舰船上的低温生成器来
  防守它。

## 机制

### BOARD-001 — 敌对 AI 舰船在靠近时登上玩家舰船
断言：一艘带有 `AI_FLAG_BOARDER` 标志的 AI 星图舰船会作为高优先级 AI 目标接近其
目标，并在 8 格以内时调用 `try_board()`。
面向玩家的影响：某些敌舰（例如 Astartes 级陆战护卫舰、一种海盗登舰变体）会刻意
追击玩家舰船并投放一支登舰队，而非仅仅开火。
证据：`nsv13/code/modules/overmap/ai-skynet.dm:1087-1115`（`/datum/ai_goal/board`、
`required_ai_flags = AI_FLAG_BOARDER`；`check_score`/`action`）；`can_board` 在 :1558
（`overmap_dist(ship, src) > 8` → FALSE，要求目标有 `length(occupying_levels)`）；
`try_board` 在 :1567-1575。标志定义于 `nsv13/code/__DEFINES/skynet.dm:53`；在
`syndicate.dm:381` 和 `spacepirates.dm:78` 上设置。
条件：`can_board` 还会受 `next_boarding_time`/`next_boarding_attempt` 冷却的
门限约束。`next_boarding_time` 是星图类型上的 `static` 变量（`overmap.dm:256`），
因此它是一把 **共享的** 30 分钟锁定；`next_boarding_attempt` 是每艘舰 5 分钟。
例外 / 覆写：`try_board` 只有在实际生成时才设置
`next_boarding_time = world.time + 30 MINUTES` 和
`next_boarding_attempt = world.time + 5 MINUTES`。该目标要能评分，必须拥有
`AI_FLAG_BOARDER`。
置信度：HIGH（高）。

### BOARD-002 — KNPC 登舰者生成（回退 / 低人口路径）
断言：若没有询问到有效的幽灵候选者（或玩家人数 < `min_players_for_ghosts`），
敌对登舰者会作为一个降落在目标舰船上的补给舱内的 KNPC `human/ai_boarder` 生物
生成。
面向玩家的影响：一个舱体/系留缆降落在玩家舰船上，一队武装 NPC 下船并猎杀船员；
不需要幽灵。
证据：`boarding.dm:38-40`（`if(!length(candidates)) return spawn_knpcs(...)`）；
`spawn_knpcs` `boarding.dm:77-126`。阵营到生物表 :81-95（syndicate → 手枪/smg/
霰弹枪子类型；pirate → 手枪/自动手枪；greytide → assistant；nanotrasen → ert
变体；也有 zombies/droid/catgirl 可用）。生成舱 :115-126——`syndicate_odst` 或
`pirate_odst` 补给舱，生物在其中 `/area/centcom/supplypod/supplypod_temp_holding`
内创建，然后向 LZ 发射一个 `/obj/effect/pod_landingzone`。
条件：需要来自 `get_overmap() == src` 的 `patrol_node` 地标的 `possible_spawns`
（`:100-108`）；若没有，则 **抛出异常**（“add some patrol nodes!”）。只有辛迪加
阵营会传达一个“系留缆正在连接”的声音提示；海盗没有。
例外 / 覆写：未知阵营字符串会在 :98 处 `throw`。若为 null，`amount` 默认
`CEILING(1 + difficulty/2, 1)`。
置信度：HIGH（高）。

### BOARD-003 — 幽灵角色玩家登舰者及其门限
断言：只有当该舰的活跃-afk-人类 活跃玩家数量 ≥ 20 时，才会向玩家提供登舰者；
随后进行一次询问，招募具有中场“Boarder”角色偏好的幽灵。
面向玩家的影响：在低人口时，敌对登舰由 KNPC 完成，而非玩家。在高人口时，真正的
玩家可以作为一支有名称、有装备的登舰队入侵舰船。
证据：`boarding.dm:12-41`。`min_players = 5` 会完全中止；`min_players_for_ghosts
= 20` 控制询问。`player_check = get_active_player_count(alive_check=TRUE,
afk_check=TRUE, human_check=TRUE)`（:16）。询问：
`pollCandidatesForMob("Do you want to play as a boarding team member?",
ROLE_OPERATIVE, /datum/role_preference/midround_ghost/boarder, 10 SECONDS, src)`
（:37）。偏好 datum：`nsv13/code/modules/antagonists/role_preference/role_midrounds.dm:5-7`
（名称 “Boarder”，antag `/datum/antagonist/traitor/boarder`）。
条件：管理员开关 `SSovermap_mode.override_ghost_boarders` 禁用所有登舰者（返回
FALSE，:13-15）。要求目标舰船有 `length(occupying_levels)` 以及一个有效的
`choose_boarding_location` 地格（否则抛出 / 中止）。
例外 / 覆写：管理员动词“toggle_ghost_boarders”（`overmap_mode.dm:751-754`）。
置信度：HIGH（高）。

### BOARD-004 — 玩家登舰者生成、装备与队伍呼号
断言：辛迪加玩家登舰者作为人类生成在一个已加载的 `syndicate_boarding_pod` 地图
内部，被赋予一个以随机挑选的队伍命名的随机呼号，并从角色套件中装备；海盗登舰者
生成在一个 `spacepirate_boarding_pod` 内，配有海盗名称。
面向玩家的影响：登舰者拥有连贯的队伍身份（“Abassi-1”、“Abassi-Lead”）以及
不同的套件（辛迪加为 SMG/霰弹枪/医疗；海盗为工兵/炮手）。
证据：`boarding.dm:136-196`。队伍列表 `DROP_TROOPER_TEAMS`（:1，约 31 个名称）。
辛迪加路径 :140-167——在 `target` 加载 `/datum/map_template/syndicate_boarding_pod`，
队长获得 `/datum/outfit/syndicate/odst/smg`，其余从 `{smg, shotgun, medic}` 中
挑选；名称设为 `"[team]-[callsign]"`；
`add_antag_datum(/datum/antagonist/traitor/boarder)`；assigned_role 为
“Syndicate Boarder”；传达登舰舱声音；聊天：“Cripple [station_name()]...”。
海盗路径 :169-195——`spacepirate_boarding_pod`，队长装备
`/datum/outfit/pirate/space/boarding/lead`，其余为工兵/炮手，名称来自
`PIRATE_NAMES_FILE`，`add_antag_datum(/datum/antagonist/pirate/boarder)`。
条件：在 `choose_boarding_location(occupying z-levels)` 生成——一个随机基本方位
边缘地格，距世界边缘约 `TRANSITIONEDGE+10`（`:52-69`）。若候选者耗尽，循环会
提前中断。
例外 / 覆写：海盗船员有意 **不** 向目标通报（“Crew intentionally isn't informed
of boarding pirates”，:195）。辛迪加登舰修正仅在 `faction_selection ==
"syndicate"` 时给予；其他阵营永远不会到达此 proc（它们使用 KNPC）。
置信度：HIGH（高）。

### BOARD-005 — 登舰者目标
断言：辛迪加登舰者通过 traitor-boarder datum 获得一个劫持目标（偶尔还有殉道）；
海盗登舰者共享一个队伍“掠夺”目标。
面向玩家的影响：辛迪加登舰者被引导去夺取舰船；海盗被引导去窃取战利品。
证据：`nsv13/code/modules/antagonists/boarders/boarders.dm:9-27`——
`/datum/antagonist/traitor/boarder/forge_human_objectives()` 首先添加
`/datum/objective/hijack`（然后 return），因此首次生成时总会添加一个劫持目标；
殉道分支（20% `prob`，`martyr_compatible`）只有在劫持已存在时才可达。
`should_equip = FALSE`（:5），因此没有上行链路。劫持文本是通用的
“`Hijack the emergency shuttle...`”（`code/game/gamemodes/objective.dm:408-415`），
且 `check_completion` 要求紧急穿梭机终局。海盗：
`pirate_boarders.dm`——`/datum/antagonist/pirate/boarder/greet()`，队伍
`/datum/team/pirate/boarder`，`forge_objectives()` 添加
`/datum/objective/loot/plunder`（说明“Loot and pillage the ship, transport 50000
credits worth of loot.”，附带一条 `//replace me` 注释）。
条件：`forge_human_objectives` 覆写父 traitor proc；父 `on_gain` 调用
`forge_traitor_objectives`（`datum_traitor.dm:19-26,67-72`）。
例外 / 覆写：劫持目标文本是原版的站点穿梭机文本，并未描述 NSV 夺舰目标——很可能
不匹配（见待解问题）。海盗掠夺目标的货舱绑定到 `/area/shuttle/pirate` 中的一个
`piratepad_control`，而对于由舱体生成的登舰者，该物件可能不存在。
置信度：MEDIUM（中）（目标归属已被证实；它“是否有效”在游戏中属解读）。

### BOARD-006 — 登舰舱模板
断言：玩家登舰者抵达时身处于一个在生成地格加载的小型预制舱地图内部。
面向玩家的影响：登舰者出现在一个带有生存装备的密封舱内，然后破舱而出。
证据：`nsv13/code/modules/mapping/custom_map_template.dm:5-11`
（`/datum/map_template/syndicate_boarding_pod` → `_maps/templates/boarding_pod.dmm`；
`spacepirate_boarding_pod` → `_maps/templates/pirate_pod.dmm`）。舱体内容
`_maps/templates/boarding_pod.dmm`——塑钛舱、外部玻璃气闸
（`req_one_access_txt = "150"`）、生存睡眠舱、生存舱桌（核操作手册、指针器对、
撬棍、辛迪加核指针器、GPS）、维和路障。
条件：`currentPod.load(target, TRUE)`——`centered=TRUE`（`boarding.dm:143,171`）。
例外 / 覆写：辛迪加舱仅用于辛迪加分支中任何非海盗 `faction_selection`；
其他阵营不生成舱体。
置信度：HIGH（高）。

### BOARD-007 — 敌方内部空间生成与生命周期（hammerlock 核心）
断言：一艘可登舰敌舰的内部空间是一张完整的地图模板，加载到 **属于登舰者
（玩家舰船）的一个保留 Z 层级** 上，而非敌舰自己的 Z。
面向玩家的影响：登舰实际上将敌舰内部空间“实例化”到你舰船旁边，以便陆战队可以
物理走入/降落到其中；释放锁定会清除内部空间。
证据：`nsv13/code/modules/overmap/boarding/interiors.dm`：
- `get_boarding_level()`（:64-72）保留一个空闲登舰 Z 或
  `add_new_initialized_zlevel(..., ZTRAITS_BOARDABLE_SHIP)`；空闲列表
  `free_boarding_levels`。
- `ai_load_interior(boarder, map_path_override)`（:74-116）：拒绝
  `NO_INTERIOR`/`INTERIOR_DYNAMIC` 目标（:80）；获取
  `boarder.get_boarding_level()`；预地图加载检查（:91）要求一个保留 Z、非空
  `possible_interior_maps`、空 `occupying_levels`，且没有现存的
  `active_boarding_target`；设置 `hammerlocked=TRUE`、
  `current_system = boarder.current_system`，通过 `SL.linked_overmap = src` 链接
  保留 Z，追加到 `occupying_levels`，若没有则在
  `locate(20, world.maxy/2, z)` 添加一个默认 `docking_point`，然后
  `load_interior(...)`。
- `kill_boarding_level(boarder)`（:8-53）将其拆除：对于 `INTERIOR_EXCLUSIVE`
  将 `SL.linked_overmap` 置空，清空所有地格，将该 Z 归还到
  `free_boarding_levels`，并 qdel 该模板；打开 `boarding_interior`/
  `roomReservation`。从 `overmap.dm:566`（舰船 Destroy）和打捞控制台释放处调用。
- `post_load_interior()`（:207-220）在随机 `ifflocs` 地格生成一个 IFF 控制台：
  辛迪加用 `/obj/machinery/computer/iff_console/boarding`（预先 emag），
  nanotrasen/solgov 阵营舰船用普通 `/obj/machinery/computer/iff_console`；剩余的
  iffloc 获得空计算机框架。
条件：目标必须为 `INTERIOR_EXCLUSIVE` 且具有非空 `possible_interior_maps`
（在 `types/syndicate.dm` 中按舰船类型分配，例如 mako/carrier/destroyer/
marine_frigate）。`interior_status` 状态机：
`INTERIOR_NOT_LOADED → LOADING → READY → DELETING → DELETED`。
例外 / 覆写：`interior_mode` 在初始化时推导：
`possible_interior_maps?.len ? INTERIOR_EXCLUSIVE : NO_INTERIOR`
（`overmap.dm:479`）；`INTERIOR_DYNAMIC` 用于运输机
（`dropship_types.dm:49`）。`choose_interior` 支持管理员
`map_path_override`。
另外 `get_overmap_level()`（:118-124）给被登舰的舰船自己的“跑步机”保留 Z，
并将其添加到恒星系中。
置信度：HIGH（高）。

### BOARD-008 — 打捞/EWAR 控制台是登舰的“钥匙”
断言：面向船员的、开启一艘敌舰以供登舰的唯一触发器是打捞计算机的 `salvage`
动作，它要求目标低于 50% 完整度且处于同一星系/Z，然后调用 `ai_load_interior`
并禁用敌方 AI。
面向玩家的影响：你无法登上一艘完好的舰船；你必须先将其击打至 <50%，然后运行
EWAR。在 hammerlocked 期间，敌方自己的炮火/AI 关闭，其内部空间为你的陆战队
存在。
证据：`nsv13/code/game/machinery/computer/salvage.dm`。
`required_damage_percentage = 50`（:8）；`max_salvage_range = 20`（:7）已声明但
在过滤中 **未被引用**（死变量）。目标列表过滤器（:59-61）：与 `linked` 相同
`OM.z`、`interior_mode == INTERIOR_EXCLUSIVE`、传感器可见性
`> SENSOR_VISIBILITY_FAINT`、尚未是活跃目标。伤害门限（:93）：
`(OM.obj_integrity*100/initial(OM.obj_integrity)) > 50` → 拒绝。成功时（:97-103）：
`DISABLE_BITFIELD(..., DAMAGE_DELETES_UNOCCUPIED)`、`OM.ai_load_interior(linked)`、
`linked.active_boarding_target = OM`、`OM.ai_controlled = FALSE`，无线电
“Bluefor strike teams cleared for approach.”。冷却 `salvage_cooldown = 5 MINUTES`
（静态 `can_salvage`，:10-11）。`stop_salvage`（:110-132）通过
`kill_boarding_level` 释放锁定，若注册的 `COMSIG_SHIP_RELEASE_BOARDING` 处理器
返回阻止则拒绝，并警告“ALL BOARDERS WILL BE KILLED”。
条件：控制台必须位于带 `ZTRAIT_OVERMAP` 的 Z 上（:72）——“so you can't brasil
yourselves if boarding with the DS.”。`OM.ghost_controlled` 目标被拒绝（:83）。
例外 / 覆写：采矿子类型使用不同的无线电频道（`headset_cargo`）。`loot` 目标舰船
通过 board_ship 目标注册一个 `COMSIG_SHIP_RELEASE_BOARDING` 阻止。
置信度：HIGH（高）。

### BOARD-009 — IFF 夺取完成登舰
断言：夺取一艘被登舰的舰船通过用多功能工具破解其（预先 emag 的）IFF 控制台
2 分钟完成，这会翻转舰船的 `faction` 并发出 `COMSIG_SHIP_BOARDED`。
面向玩家的影响：抵达舰桥 IFF 控制台并手持多功能工具 2 分钟的陆战队员夺取了该舰；
syndicate→nanotrasen、pirate→nanotrasen、nanotrasen→syndicate（后者还会生成一支
Solgov 拦截舰队以及一则“contact lost”公告）。
证据：`nsv13/code/game/machinery/iff_console.dm`。`hack_goal = 2 MINUTES`（:17）；
`multitool_act` 仅在 `EMAGGED` 时有效（:80）——登舰子类型为 `start_emagged = TRUE`
（:65-67）。`hack()`（:124-162）：`SEND_SIGNAL(OM, COMSIG_SHIP_BOARDED)`；
通过 switch 翻转阵营；对于 `role == MAIN_OVERMAP` 的 nanotrasen→syndicate 会创建
`/datum/fleet/solgov/interdiction` 并
`priority_announce("Contact with [station_name] lost...")`。`Destroy`/
`LateInitialize`（:33-49）若该 OM 仍 `hammerlocked`，则重新 emag 一个重建的控制台。
条件：控制台必须为 `EMAGGED`（登舰控制台生成时即如此）；进行中的 `hacking` 会
阻止并发的破解；`process()` 在破解期间周期性地发出“Unauthorized IFF transponder
access”无线电警告（:69-77，`hack_goal` 在失败的 `do_after` 时倒计时）。
例外 / 覆写：阵营翻转回退对于未知阵营重置为 `initial(OM.faction)`（:161-162）。
夺取会将舰船从其敌方舰队中移除——
`RegisterSignal(member, COMSIG_SHIP_BOARDED, remove_ship)`（`ai-skynet.dm:807`）。
置信度：HIGH（高）。

### BOARD-010 — board_ship 目标与登舰游戏模式
断言：登舰游戏模式实例化一艘特定的辛迪加“CALLANADMIN”舰船，并交给船员任务：
登上它、击杀防守者、翻转其 IFF；当其阵营变为起始阵营时完成触发。
面向玩家的影响：一个脚本化的单目标夺取任务。
证据：`nsv13/code/game/gamemodes/overmap/boarding_gamemode.dm`——
`/datum/overmap_gamemode/boarding`、`starting_faction = "nanotrasen"`、
`fixed_objectives = list(/datum/overmap_objective/board_ship)`、
`required_players = 14`。
`nsv13/code/game/gamemodes/overmap/objectives/board_ship.dm`：
`instance()` 从 `GLOB.boardable_ship_types` 中挑选一个类型，将其标记为
`block_deletion`/`essential`，注册 `COMSIG_SHIP_BOARDED → check_completion` 和
`COMSIG_SHIP_RELEASE_BOARDING → release_boarding`，立即调用
`target_ship.ai_load_interior(main_overmap)`（:17），命名并将其放置在某个第二星区
非敌方星系中，并附加一支防御舰队。`check_completion()`（:60-66）：若
`target_ship.faction == mode.starting_faction` → 状态 COMPLETED，清除标志。
`release_boarding()`（:68-72）：除非完成/管理员覆写，否则返回
`COMSIG_SHIP_BLOCKS_RELEASE_BOARDING`，因此打捞锁定无法被提前释放。
条件：`GLOB.boardable_ship_types` = syndicate/ai、mako_carrier、conflagration、
destroyer（`nsv13/code/_globalvars/ships.dm:3`）。
例外 / 覆写：`block_deletion`/`essential` 阻止该舰被删除；管理员状态
“Victory Override”（`STATUS_OVERRIDE`）也允许释放。
置信度：HIGH（高）。

### BOARD-011 — 已登舰的活跃辛迪加舰船上的幽灵角色防守方
断言：某些辛迪加内部空间包含 `mob_spawn/human/syndicate/boarding` 低温舱，在
地图加载时它们会通知幽灵来生成为防守方船员。
面向玩家的影响：当陆战队登上一艘内部空间有这些生成器的舰船时，幽灵可以加入成为
辛迪加船员/舰长来防守该舰。
证据：`nsv13/code/modules/overmap/boarding/ghost_role_spawners.dm:
- `/obj/effect/mob_spawn/human/syndicate/boarding`（辛迪加船员，:44-51）——
  在 `LateInitialize`（:58-60）中
  `notify_ghosts("The crew has boarded a live Syndicate ship! The Syndicate navy is
  now recruiting...", action=NOTIFY_ATTACK)`。装备
  `/datum/outfit/syndicate_empty/boarding`（贝雷帽、辛迪加船员服、手枪、装有箱子
  + 战斗刀的辛迪加行李袋）。`id_access_list` 包含 syndicate、syndicate engineering、
  syndicate marine armoury。`assignedrole = "Syndicate Crew"`。
- `/obj/effect/mob_spawn/human/syndicate/boarding_captain`（:62-68）——舰长装备、
  辛迪加领袖权限。
生成器放置在 `_maps/templates/boarding/syndicate/marine_frigate.dmm` 和
`destroyer.dmm` 中。幽灵→生物是标准的 `/obj/effect/mob_spawn/attack_ghost` 流程
（`code/modules/awaymissions/corpse.dm:38-63`）→ `create()` 装备服装、应用
`assignedrole`、运行 `special()`。这些生成器 **没有 `antagonist_type`/`objectives`**，
因此幽灵船员是非 antagonist 的防守方。
条件：`attack_ghost` 要求回合已开始、`GHOSTROLE_SPAWNER` 标志开启、未使用
（`uses`）、未因 `banType` 被 jobban（此处为 null），且经过一次确认提示。
例外 / 覆写：这些生成器未设置 `use_cooldown`（默认为 FALSE），与许多 tg 生成器
不同——没有近期死亡冷却。
置信度：机制为 HIGH（高）；确切的局内出现频率为 MEDIUM（中）（取决于地图放置）。

### BOARD-012 — 运输机部署与对接
断言：`small_craft/transport` 运输机拥有一个可加载的内部空间（INTERIOR_DYNAMIC），
船员爬入其中；要登上另一艘舰船，驾驶员启用对接模式并飞入（更大的）目标，这会将
运输机转移到目标的内部/对接 Z 上；陆战队随后走出。
面向玩家的影响：陆战队登上一架运输机，它飞向目标星图对象、对接，乘客直接进入敌方
内部空间。
证据：`nsv13/code/game/general_quarters/dropship.dm`：
- `enter(user)` 将用户（以及被拖拽的原子）移动到一个随机
  `interior_entry_points` 地格（“乘客舱”）（:86-103）；`exit(user)` 将他们移动到
  `get_turf(src)`（:109-123）。
- `/turf/closed/indestructible/dropship/entry/Bumped` 在不在星图 Z 时调用
  `OM.exit(AM)`（:24-28）——踏入机库门地格会将你从运输机弹出。
`nsv13/code/game/general_quarters/dropship_types.dm`：
- `/obj/structure/overmap/small_craft/transport`（:4-51）：装甲、
  `max_integrity = 1000`、`interior_mode = INTERIOR_DYNAMIC`、
  `loadout_type = /datum/component/ship_loadout/utility`，包含一个
  `docking_computer`、`fuel_tank/tier2`、`ftl` 等。子类型：`starter`（NSV Sephora，
  dropship_main）、`syndicate`、`gunship`、`sabre`（+ mining/syndicate），
  各子类型有 `possible_interior_maps`。
- `post_load_interior()`（:61-73）将内部区域与飞行器连接并添加一个导航控制台。
对接逻辑：`nsv13/code/modules/overmap/fighters/fighters_launcher.dm`
`docking_act(OM)`（:339-358）需要一个带 `DC.docking_mode` 的
`docking_computer` 槽位，且仅当 `mass < OM.mass`（目标更大）时才对接，否则返回
FALSE → `transfer_from_overmap(OM)`。`transfer_from_overmap`（:370-389）将飞行器
放大、`forceMove` 到一个随机 `OM.docking_points` 地格，并将其添加到
`OM.overmaps_in_ship`。小行星会自动启用 `INTERIOR_DYNAMIC` 并通过
`on_dock_interior_load_finish` 对接（:361-368）。
条件：目标舰船必须有 `docking_points`；对于被登舰的敌舰，这些来自
`ai_load_interior`（默认 `locate(20, world.maxy/2, boarding_reservation_z)`，
interiors.dm:110-111）。
运输机舵控台：`/obj/machinery/computer/ship/helm/console/dropship`
（`dropship.dm:163-205`）——WASD 飞行指引，打开 `FighterControls`。
例外 / 覆写：`stop_piloting` 覆写为 `eject_mob = FALSE`（:207-208）——驾驶员
不会被抛出。基础 transport 上 `req_one_access` 为空（安保/陆战队可登舰）；
sabre/海盗变体限制访问。
置信度：这些 proc 为 HIGH（高）；陆战队“爬出进入敌方内部空间”的精确端到端
是否为预期路径为 MEDIUM（中）（由 `exit()` + 对接到登舰 Z 拼装而成）。

### BOARD-013 — 登舰撞针（友军火力控制）
断言：装有登舰撞针的武器只有在 away/登舰 Z 层级上才能自由开火；在友方主舰/采矿舰
上，除非军械库控制台解锁它们且警戒 ≥ 红色，或用户拥有军械库权限，否则被阻止。
面向玩家的影响：陆战队不能意外地（或蓄意地）用带登舰撞针的武器射击自己的舰船；
同一件武器一旦到了外部就能正常开火。
证据：`nsv13/code/game/machinery/computer/boarding_pin.dm`。
`GLOBAL_VAR_INIT(boarding_guns_z_locked, TRUE)`（:1）。`pin_auth`（:13-25）：
`allowed(user)`（武器许可 / ACCESS_ARMORY）→ TRUE；
`on_friendly_overmap(user)` → 若锁定则 FALSE，否则 `security_level >=
SEC_LEVEL_RED`；其他情况（登舰层级）TRUE。`on_friendly_overmap`（:27-33）仅对
`role == MAIN_OVERMAP` 或 `MAIN_MINING_SHIP` 为 TRUE。控制台
`/obj/machinery/computer/boarding_guns`（:47-93）：“Away only” 对
“General Quarters”，切换全局变量（`GLOB.boarding_guns_z_locked`）。
10 枚撞针的盒子 `:36-43`；陆战队步枪默认装有登舰撞针（`custom_guns.dm:128`）。
条件：`force_replace = TRUE`、`pin_removeable = TRUE`、
`req_one_access = list(ACCESS_ARMORY)`。
例外 / 覆写：控制台为 `INDESTRUCTIBLE` 且不可建造。
置信度：HIGH（高）。

### BOARD-014 — 登舰鱼叉未实现
断言：`/obj/machinery/boarding_harpoon` 没有任何行为——它只是一个可描述的物件。
面向玩家的影响：无；不存在鱼叉机制。登舰是通过 EWAR + 运输机对接完成的。
证据：`nsv13/code/game/machinery/boarding_harpoon.dm`（整个文件：仅 name、desc、
icon、icon_state）。全仓库搜索“harpoon”仅返回该文件以及 `interiors.dm:78` 中一条
无关注释。未找到 proc、构造函数或地图使用。
条件：不适用。
例外 / 覆写：不适用。
置信度：HIGH（高）。

### BOARD-015 — 敌舰上的 KNPC 战斗行为
断言：KNPC 登舰者运行一个评分 AI 目标系统（战斗/巡逻/呼叫增援/设置内部），通过
`patrol_node` 图游荡（包括通过梯子/楼梯跨 Z 层级），通过无线电呼叫增援，并能偷取
目标的 ID 以开门。
面向玩家的影响：登舰者猎杀并追击船员、聚集，并能掠夺通行徽章以深入舰船。
证据：`nsv13/code/modules/overmap/knpc.dm`。特性定义 `__DEFINES/knpc.dm` 及生物
特性 `knpc.dm:42`
（`KNPC_IS_DODGER|KNPC_IS_MERCIFUL|KNPC_IS_AREA_SPECIFIC`）；
syndicate/ERT 变体添加 `KNPC_IS_MARTIAL_ARTIST`（柔道摔 / 腕锁 / 膝击，:593-616）。
巡逻图：`get_next_patrol_node`（:718-757）在梯子、楼梯上使用 `pathfind_to`、
`travel` 以改变 Z。增援呼叫 `call_backup`（:633-667）：`; ` 前缀无线电，对
`KNPC_IS_AREA_SPECIFIC` 带区域名，其他 KNPC 路径寻向呼叫者，simple mob 用
`Goto`。ID 偷窃 `steal_id`（:81-97）。寻路失败退避 `pathfind_to`（:103-125）
带 `KNPC_TIMEOUT_BASE` 堆叠。`Initialize`（:51-65）应用难度减速：难度 1 →
移动/动作延迟 +2，2-3 → +1。
条件：`difficulty_override = TRUE` 的生物（僵尸、登舰机器人）忽略难度缩放。
巡逻节点驱动出生 LZ 选择（BOARD-002）与默认移动。
例外 / 覆写：`KNPC_IS_MERCIFUL` 多数存在；僵尸/机器人不同。
置信度：HIGH（高）（完整 AI 见系统 5 交叉引用）。

### BOARD-016 — 登舰者数量随难度缩放
断言：当未提供 `amount` 时，登舰小队规模 = `CEILING(1 + difficulty/2, 1)`。
面向玩家的影响：更大/更警戒的船员面对更大的登舰队。
证据：`boarding.dm:24-25` 和 :78-79 和 :137-138（三处调用点，完全相同）。
难度：`overmap_mode.dm:365-370` `difficulty_calc()` =
`CLAMP(CEILING(active_players/10, 1), 1, 5) + escalation`。
条件：计算值（难度 1..5 → amount 2,2,3,3,4；管理员 `escalation` ±5 可将难度推至
10 → amount 6）。每 10 分钟在 `fire()` 中通过 `difficulty_calc()` 重新计算。
例外 / 覆写：管理员 escalation 通过 Overmap Gamemode Controller 设置
（`overmap_mode.dm:609-618`）。硬核模式使威胁累积翻倍，但本身不改变此公式。
置信度：HIGH（高）。

## 跨系统依赖

- **系统 1/8（FTL/恒星系、星图、IFF）：** 夺取舰船会翻转 `faction`；舰船放置使用
  `SSstar_system`/`jump_end`。`GLOB.boardable_ship_types` 和 `instance_overmap`
  位于此处。NT→syndicate 翻转时的 Solgov 拦截响应在 `fleet_types.dm:344-354`。
- **系统 11/18（打捞/伤害）：** EWAR 控制台就是打捞计算机；伤害至 50% 的门限将
  登舰与伤害系统绑定。
- **系统 5（舰队 AI / KNPC）：** `AI_FLAG_BOARDER`、`ai_goal/board`、
  `can_board`/`try_board`、KNPC 目标/特性系统。
- **系统 13（战斗机/运输机）：** 运输机飞行、对接计算机、`transfer_from_overmap`、
  发射器机制、`FighterControls` TGUI（此处有意不详述）。
- **系统 23（班组）：** 班组售货机提供陆战队装备；在陆战队职业上
  `register_squad`（`midshipman.dm:120-131`）。此处不重复。
- **地图模板：** `_maps/templates/boarding/`（内部空间、运输机）、
  `boarding_pod.dmm`、`pirate_pod.dmm`；patrol_node 地标放置在
  `_maps/map_files/` 下的玩家舰船地图上。

## 待解问题

1. traitor/boarder 的 `forge_human_objectives` 总是添加一个 *站点穿梭机* 劫持目标，
   其 `check_completion` 查看 `SSshuttle.emergency`——在 NSV 星图上很可能永远无法
   满足。辛迪加登舰者的“胜利”实际上是任何目标，还是纯粹是 to_chat 目标？
2. 海盗掠夺目标绑定到 `/area/shuttle/pirate` 中的一个 `piratepad_control`；
   由舱体生成的海盗可能没有这样的平台——该目标是否曾经解决？
3. 登舰舱 `currentPod.load(target, TRUE)` 在随机世界边缘地格使用 `centered=TRUE`；
   9x9 的舱体是否保证能放入而不会裁切地图边界，未被核实。
4. EWAR 控制台上的 `max_salvage_range = 20` 已声明但在范围过滤中未使用——它是
   死代码，还是本意要应用在别处（例如 overmap_dist）？
5. 精确的端到端“陆战队从对接的运输机走入敌方内部空间”依赖于
   `exit()` → `get_turf(src)` 落在 `boarding_reservation_z` 内部空间上；在活跃
   路径中未被确认（除 `ai_load_interior` 中设置的默认 `docking_points` 外，没有
   直接的 proc 将对接触点地格链接到 `boarding_reservation_z`）。
6. `spawn_boarders` 询问使用 `ROLE_OPERATIVE`；请核实幽灵不会因无关的 operative
   封禁/配置而被静默门限限制。
