> 本文为 research/evidence/star_systems.md 的中文翻译。

# 星系、星图与阵营 (Star Systems, Starmap & Factions)

事实来源：仅当前代码。行号引用为 `file:line`，截至本次研究时点（分支 `master`）。

## 系统概览 (System Overview)

战略星图是一张由 `datum/star_system` 节点（“星系”）构成的图，铺在 (x,y) 网格上，带命名
`adjacency_list` 边（“跳线/jumplines”）。`SSstar_system`（子系统，`nsv13/code/controllers/subsystem/starsystem.dm`）
拥有这张图、`datum/faction` 列表，以及 AI 对 AI 的舰队战斗节奏。`datum/starmap` 风格的数据被
提供给 `Starmap` 控制台（`nsv13/code/modules/overmap/starmap.dm`），玩家借此选择跳跃目标。
阵营 datum 定义关系、哪个阵营往哪里派舰队，以及胜利票数。

**不存在 `datum/starmap` 类型。** “星图”是 (a) `SSstar_system.systems` 列表，加上 (b) 磁盘上的一个 JSON 文件，
会被解析成星系。范围说明：`datum/starsystem_manager` 是一个仅限管理员使用的 TGUI（非面向玩家）。

权威默认地图 = `config/starmap/starmap_default.json`（`starsystem.dm:1019+` 中硬编码的 `/datum/star_system/*` 子类型
*仅作为解码失败的兜底*，其中包含一份完全不同的、更旧的星系列表——
见 STARSYS-002）。

## 核心玩法循环 (Core Gameplay Loop)

1. 回合开始；星图模式将玩家主船移入其 `starting_system`（`overmap_mode.dm:188-204`）。
2. 玩家打开 FTL Navigation / Starmap 控制台，选择一个**可见且相邻**的星系，若 FTL 驱动器已充能且在射程内
   则跳跃（STARSYS-006）。旅行耗时 `dist/(speed)` 分钟，该星系会卸载/重载其 Z。
3. 每个星系会预生成内容（异常、小行星、商人、预设舰队），并可能是战斗地点。
4. 独立地，`SSstar_system` 每约 3 分钟在*未加载*的星系上运行 AI 对 AI 舰队战斗（STARSYS-008），
   且各阵营每 N 分钟尝试派出一支新舰队（STARSYS-010）。
5. 摧毁辛迪加 AI 飞船会增加 credit `bounty_pool`（STARSYS-011）；清除星系/舰队会给予阵营
   票/影响力（STARSYS-012）。

## 机制 (Mechanics)

### STARSYS-001
断言：`datum/star_system` 是一个带名图节点，具有战略状态以及一个惰性加载的物理 Z 层。
玩家可感知的后果：星系是你在其间旅行的单位；每个星系都可以容纳飞船/异常/小行星，这些内容
只有在有人在场时才“存在”（占据真实 Z）。
证据：`starsystem.dm:439-485`（变量：`name/x/y/adjacency_list/alignment/owner/hidden/sector/threat_level/
is_capital/fleet_type/system_traits/system_type/preset_trader/wormhole_connections/startup_proc/system_contents/
contents_positions/enemy_queue/occupying_z/fleets/enemies_in_system/already_announced_combat`）。`occupying_z` 的注释：
"Z-levels are 'held' by ships"（`starsystem.dm:475`）。
条件：`sector` 默认为 1；`sector`/`SECTOR_*` = 1 Sol、2 Neutral、3 Syndicate（`__DEFINES/starsystem.dm:13-15`）。
例外 / 覆写：`visitable` 存在，但只被遗留 FTL 路径读取（`ftl_legacy.dm:214,252`），现代控制台/`drive.dm`
不读取；可视为对当前旅行实际未使用。
置信度：HIGH（高）

### STARSYS-002
断言：当星图文件存在时，星系从 JSON 加载，而非从硬编码类型加载。
玩家可感知的后果：默认回合使用 `starmap_default.json` 中的星系列表，它与
硬编码兜底列表不同。
证据：`SSstar_system.instantiate_systems(_source_path = SSmapping.config.starmap_path)` 读取该文件
（`starsystem.dm:131-183`）。若 `!fexists`，它会替换为 `config/starmap/starmap_default.json`（`starsystem.dm:136-138`）。
只有当 `json_decode` 抛错时，它才调用 `instantiate_systems_backup()`，后者 new 出每一个 `/datum/star_system`
子类型（`starsystem.dm:139-144, 186-192`）。地图配置设置 `starmap_path`（例如 `_maps/galactica.json:31` =
`config/starmap/starmap.json`）；随附文件是 `starmap_default.json`，因此运行时回退到它。
`SSmapping.config.return_system` 默认 `"Outpost 45"`（`code/datums/map_config.dm:29`，starmap_path 在 `:28`）。
条件：`starmap.json` 可能存在于持久化服务器上（由 `save()` 在 `starsystem.dm:199-263` 保存）。
例外 / 覆写：若 JSON 能解析但某个单独星系抛错，该星系会被跳过并给出管理员
消息，而非整体兜底（`starsystem.dm:179-182`）。
置信度：HIGH（高）

### STARSYS-003
断言：`system_contents` / `occupying_z` / `contents_positions` 实现了惰性 Z 层预留与静滞。
玩家可感知的后果：从无人停留的星系跳走会“冻结”其 NPC 并记住它们的位置；下次
抵达时会把它们精确解冻在原位。
证据：`star_system/add_ship` 从第一艘有 Z 的飞船设置 `occupying_z`（或调用 `OM.get_reserved_z()`），
然后调用 `restore_contents()`（`ftl_jump.dm:1-46`）。`restore_contents()` 先生成任何 `enemy_queue`，然后从 nullspace 强制移回缓存内容并重启动理
（`ftl_jump.dm:68-86`）。`remove_ship()` 将每个剩余的 `X.x/X.y` 缓存到 `contents_positions`，将它们 `moveToNullspace()`、停止处理，并在没有
持有者残留时清零 `occupying_z`（`ftl_jump.dm:88-142`）。`move_existing_object` 在来源无位置时缓存一个随机位置
（`starsystem.dm:318-347`）。
条件：若另有玩家飞船仍持有该 Z，`remove_ship` 会交换 `reserved_z` 值，而非拆除
该层（`ftl_jump.dm:95-104`）。
例外 / 覆写：当目标没有 `occupying_z` 时 `spawn_ship` 会排入 `enemy_queue`
（`starsystem.dm:306-316`）；异常会立即被存入 nullspace（`starsystem.dm:351-366`）。
置信度：HIGH（高）

### STARSYS-004
断言：飞船的**抵达位置取决于其 `faction`**：友方（NT/SolGov）生成在左半区，其他所有飞船生成在右半区，无阵营飞船则任意位置生成。
玩家可感知的后果：敌对舰队和 IFF 伪造的飞船会明显从星系对侧抵达。
证据：`ftl_jump.dm:24-33`：空 `faction` → `locate(rand(40, maxx-39), ...)`；`"nanotrasen"`/`"solgov"` →
`x` 在 `40..round(maxx/2)-10`；否则 → `x` 在 `round(maxx/2)+10..maxx-39`。最终 turf 是
`orange(15, destination)` 中的随机格（`ftl_jump.dm:38-39`）。
条件：仅通过 `star_system/add_ship` 生效；管理员 `spawn_ship` 使用其自己的随机/居中放置。
例外 / 覆写：`SSstar_system.add_ship(OM, target, system_override)` 则依据 `OM.starting_system`
并将飞船登记进 `ships[]` 表（`starsystem.dm:391-398`）——这是“登记”路径（在 LateInitialize 时由
`overmap.dm:379` 调用，以及战机/登舰内部 `fighters_launcher.dm:319`、`interiors.dm:123`），
与 `ftl_jump.dm` 中“放入星系”的路径不同。
置信度：HIGH（高）

### STARSYS-005
断言：`SSstar_system` 在启动时初始化星系、阵营、敌人类型以及中立区列表。
玩家可感知的后果：每回合都会加载整个银河、构建 3 个阵营 datum，并预先计算哪些
星系是用于舰队生成的中立区。
证据：`starsystem.dm:63-79` —— `instantiate_systems()`、`return_system = system_by_id(return_system)`、
`enemy_types = subtypesof(/obj/structure/overmap/syndicate/ai)` 减去 `enemy_blacklist`
（`fistofsol`、`battleship`，`starsystem.dm:12,67-69`），然后对每个 `subtypesof(/datum/faction)` 执行 `new` 并对每个执行
`setup_relationships()`，再对每个 `sector == SECTOR_NEUTRAL` 的星系执行 `neutral_zone_systems += S`。
条件：`wait = 10` ds；`init_order = INIT_ORDER_STARSYSTEM`。查询：`faction_by_name`（不区分大小写）、
`faction_by_id`、`system_by_id`（匹配 `name`）。
置信度：HIGH（高）

### STARSYS-006
断言：玩家只能跳跃到**相邻**且在 FTL 驱动器 `max_range` 之内、同时驱动器处于 `FTL_STATE_READY` 的星系；FTL 安全覆写会绕过这些门控。
玩家可感知的后果：跳线是唯一的常规旅行路线；除非有人禁用 FTL 安全
（emag/管理员），否则你无法在地图上自由跳跃。
证据：`starmap.dm:222` —— `can_jump = (current_system.dist(selected) < linked.ftl_drive?.max_range &&
ftl_state == FTL_STATE_READY && LAZYFIND(current_system.adjacency_list, selected.name)) || (ftl_safety_override && ...
!= FTL_STATE_JUMPING)`。`max_range` 默认 30000（`FTL/components/drive.dm:37`）；网格坐标约为 0-140，因此相邻
（而非射程）才是真正的约束。`select_system` 设置 `selected_system`（`starmap.dm:66-72`）；`jump` 调用
`linked.ftl_drive.jump(selected_system)`（`starmap.dm:73-84`）。`public` 控制台设置 `can_control_ship = FALSE`
→ `can_jump = FALSE`（`starmap.dm:31-34, 225-227`）。
条件：旅行时间 = `curr.dist(target) / (drive_speed * 10)` 分钟（`ftl_jump.dm:249-252`）。`max_range` 在驱动器 2 级时翻倍，
3 级时三倍（`drive.dm:195-207`）。
例外 / 覆写：`ftl_safety_override`（由钥匙卡认证控制台 `security_levels/keycard_authentication.dm:199` 设置）
启用基于蓄能的紧急跳跃：导航请求确认，`ftl_core` 必须确认充能 ≥25%（`drive.dm:297-323`）。
`force_return_jump`（回合召回）跳往 `return_system` 并设置驱动器 `lockout`（`ftl_jump.dm:172-194`）。
置信度：HIGH（高）

### STARSYS-007
断言：控制台暴露两个标签页（SHIPINFO=0、STARMAP=1）外加一个逐星系详情屏（2）；隐藏星系与属于另一 `sector` 的星系会被从地图上过滤掉。
玩家可感知的后果：玩家只能看到当前扇区的非隐藏星系；点击一个星系会显示其
距离/阵营，以及跳跃是否合法。
证据：`starmap.dm:1-2`（SHIPINFO/STARMAP）、`starmap.dm:139-140` 跳过 `system.hidden || system.sector !=
current_sector`；`screen = 2` 由 `select_system` 设置（`starmap.dm:71`）。虫洞边（单向、
非互惠相邻）除非当前星系的相邻列表包含它，否则隐藏，绘制为淡紫色
`#BA55D3`（`starmap.dm:175-199`）。扇区切换是玩家的自由操作（`sector` action，`starmap.dm:61-65`）。
置信度：HIGH（高）

### STARSYS-008
断言：AI 舰队对舰队的战斗以共享同一星系的舰队之间的骰子投掷结算，每 `COMBAT_CYCLE_INTERVAL`（180 秒）一次，且**仅当没有玩家实际身处该星系时**。
玩家可感知的后果：幕后的战争会自行消耗舰队；你当前所处的星系永远不会承受
“桌面推演”式战斗伤害（你自己打）。
证据：节奏 —— `starsystem.dm:46-58` 在 `world.time >= next_combat_cycle` 时对每个 `contested_systems` 条目触发
`SS.handle_combat()`，然后设置 `next_combat_cycle = world.time + COMBAT_CYCLE_INTERVAL`。定义
`COMBAT_CYCLE_INTERVAL 180 SECONDS`（`__DEFINES/starsystem.dm:3`）。战斗 —— `combat_handling.dm:15-43`：若 `<2` 支舰队则从
contested 中移除；`if(occupying_z) return COMBAT_SKIPPED`；若无 `check_conflict_status()` 则移除；否则
每支舰队向一支对立 `faction_id` 的舰队开火。伤害数学：`ship_fire` 投掷 targetting/evade/armor/damage 骰子
（命中 `affinity_flags` 时 x1.5），并将 `damaging_roll * DICE_DAMAGE_MULTIPLIER(20)` 作为 `overmap_heavy` BRUTE
施加（`combat_handling.dm:67-116`）。
条件：战斗循环还会在某个星系新“宣告战斗”时，向启用了公告音的玩家播放提示音
（`starsystem.dm:49-57`）。
例外 / 覆写：`enable_npc_combat`（默认 TRUE）门控整段逻辑（`starsystem.dm:21,46`）。
`/datum/star_system/staging/handle_combat` 是空操作（`starsystem.dm:1016-1017`）。
置信度：HIGH（高）

### STARSYS-009
断言：`contested_systems` 由舰队在其身处（或移入）某个含有对立阵营舰队的星系时填充，并由 `handle_combat` 清空。
玩家可感知的后果：你可能看到会幕后自动结算的“热点”星系集合，恰好就是对立
舰队共存的星系集合。
证据：在 `assemble()`（`ai-skynet.dm:859-861`）与 `move()`（`ai-skynet.dm:270-272`）中，当
`current_system.check_conflict_status()` 时添加；若任意两支舰队 `faction_id` 不同，`check_conflict_status()` 返回 TRUE
（`combat_handling.dm:45-53`）。在 `handle_combat` 中移除（`combat_handling.dm:17,25`）。
置信度：HIGH（高）

### STARSYS-010
断言：各阵营在其 `next_fleet_spawn` 计时器到期时各自独立尝试生成一支新舰队；舰队从**中立区**（扇区 2）生成，辛迪加舰队会得到一个要占领的 `goal_system`。
玩家可感知的后果：被“占领”的星系和游荡的敌人会随时间累积，来自任何拥有
（或能生成到）中立区星系的阵营——而非仅来自预设地图。
证据：`starsystem.dm:60-61` 每 tick 对每个阵营调用 `F.send_fleet()`。`factions.dm:70-123`：
`next_fleet_spawn = world.time + max(fleet_spawn_rate ± jitter, minimum_spawn_interval)`；候选星系 = `SSstar_system.neutral_zone_systems` 中
`!hidden` 且匹配该阵营 alignment 或为 `unaligned`/`uncharted` 者，
排除主船当前所在星系；设置 `mission_sector = TRUE`、刷新小行星、挑选舰队类型
（除非 `force`，否则加入 `randomspawn_only_fleet_types`；若难度 ≥
`elite_difficulty_threshold` 则加入 `elite_fleet_types`）。对于一支尚未处于中立区、非强制的辛迪加舰队，它会挑选一个非辛迪加的中立区星系作为
`goal_system`（`factions.dm:111-120`）。
条件：速率 —— NT `fleet_spawn_rate = 40 MINUTES`、辛迪加 `30 MINUTES`、默认 10 MINUTES（`factions.dm:22,
133,158`）。在 Galactic Conquest（pvp）中，NT 速率复制自辛迪加，辛迪加设为 2 HOURS
（`pvp.dm:135-138`）。
例外 / 覆写：`F.destroy()` 通过 `faction?.lose_influence(reward)` 将 `reward` 记入敌人
（`ai-skynet.dm:342`）；`reward` 随舰队规模缩放（`ai-skynet.dm:785`）。
置信度：HIGH（高）

### STARSYS-011
断言：`bounty_pool` 随辛迪加 AI 飞船被摧毁而累积 credits，但支付 proc 从未在代码中被调用，因此该池（显然）永远不会被自动发放。
玩家可感知的后果：摧毁辛迪加 AI 飞船会静默累积金钱，在随附构建中从未被公告/支付。
证据：递增 —— 每个 `/obj/structure/overmap/syndicate/ai/Destroy()` 执行 `SSstar_system.bounty_pool += bounty`
（`types/syndicate.dm:101-103`；各职业 `bounty` 值为 250-20000，例如 `:87,173`）。支付 proc
`bounty_payout()` 将 `bounty_pool/2` 拆入 `ACCOUNT_CAR` 与 `ACCOUNT_MUN` 并清零（`starsystem.dm:370-379`）。
全仓库搜索 `bounty_payout` **只返回定义**——未找到调用者。
条件：不适用。
例外 / 覆写：**推断**（未经证实）：可能本意是在别处调用/为遗留代码。向
文档编写者标记为很可能已死。置信度：MEDIUM（中）。
置信度：MEDIUM（中）

### STARSYS-012
断言：阵营 "tickets"（影响力）是用于在回合时间限制时判定胜者的分数；失去票会直接把它们交给你的敌人。
玩家可感知的后果：完成任务/夺取星系会提升你阵营的分数并降低敌人的；
游戏可以以分数判定的胜者结束，而非仅靠目标。
证据：`get_winner()` 返回 `tickets` 最高的阵营（`starsystem.dm:381-389`）。`fire()`：若
设置了 `time_limit` 且到达，则 `get_winner()`；pvp 模式设置 `mode.winner` 并重新检查，否则 `SSticker.force_ending`
（`starsystem.dm:36-44`）。`gain_influence(v)` = `tickets += v`（`factions.dm:67-68`）。`lose_influence(v)` =
`tickets -= v`，然后每个 `relationships[F] <= RELATIONSHIP_ENEMIES(0)` 的阵营获得 `v`（`factions.dm:61-65`）。
增益：任务完成 `F.gain_influence(ticket_bounty)`（`datums/missions.dm:75`）、星系夺取物品
（`pvp/items.dm:136,208`）、目标（`objectives/tickets.dm`）。损失：每个提醒周期未完成目标
（`overmap_mode.dm:431-439`，各 25 ×3）。pvp 也在 `tickets >= 700` 时获胜（`overmap_mode.dm:479-484`）。
条件：`time_limit` 默认 `FALSE`（`starsystem.dm:18`）；仅 pvp/Galactic Conquest 设置它，为
`world.time + 2 HOURS + 30 MINUTES`（`pvp.dm:41,133`）。
置信度：HIGH（高）

### STARSYS-013
断言：某些星系拥有特殊的“启动 proc”与特性：Outpost 45 在进入时结束回合；Dolos/Abassi 在进入时授予成就；The Badlands/Lite 程序化生成一个随机子扇区。
玩家可感知的后果：抵达 Outpost 45（通常是返航目标）会结束回合；Dolos/Abassi 与
Badlands 区域是脚本化的场景桥段，而非仅仅地图节点。
证据：`parse_startup_proc` 将 `STARTUP_PROC_TYPE_BRASIL/_LITE/_DOLOS/_ABASSI` 映射到 `generate_badlands()`、
`generate_litelands()`、`register_dolos_achievement()`、`register_abassi_achievement()`（`starsystem.dm:492-507`）。
进入时结束回合通过 `after_enter()` 中的 `system_traits & STARSYSTEM_END_ON_ENTER`——仅对 `MAIN_OVERMAP` 生效，设置
`GLOB.crew_transfer_risa`、`check_finished()`、`SHIP_VICTORY`（`ftl_jump.dm:52-59`）。特性定义于
`__DEFINES/flags.dm:188-191`。JSON 中：Outpost 45 的 `system_traits:15`，以及设置处的 `preset_trader`/`is_capital`。
Badlands 生成器 new 出 `rand(17,25)`（完整）或 `rand(8,15)`（精简）个 `/datum/star_system/random` 节点，在跳线上做 Dijkstra 树
+ 松弛，并将一个随机入口星系连接到 Sol（`starsystem.dm:1257-1451` / `1453-1644`）。
条件：异常/小行星/虫洞生成由 `STARSYSTEM_NO_ANOMALIES/_NO_ASTEROIDS/_NO_WORMHOLE`
门控（`starsystem.dm:580-583`）；虫洞以 15% 概率被添加到随机星系（`generate_anomaly`，`starsystem.dm:893-898`）。
置信度：HIGH（高）

### STARSYS-014
断言：星系在创建时预生成内容：商人（预设或 10% 随机）、异常/小行星（15 秒计时器），以及由 `threat_level` 选择的随机“星系效果”包。
玩家可感知的后果：跳入一个星系可能让你落入星云/黑洞/辐射场并伴随相应
事件，并可能出现一个供补给/交易的商人。
证据：`star_system/New` 生成 `preset_trader` 空间站并在 15 秒后调度 `generate_anomaly`/`spawn_asteroids`
（`starsystem.dm:560-583`）。`generate_anomaly` 按威胁挑选 `system_type`：NONE→safe/nebula/gas/ice；
UNSAFE(2)→debris/nebula/hazardous；DANGEROUS(4)→quasar/radioactive/blackhole，然后 `apply_system_effects`
（`starsystem.dm:893-957`）。`apply_system_effects` 按 tag 设置 `event_chance`（默认 15，最高 100）与 `possible_events`，
设置 `parallax_property`，并可生成敌人/水雷/舰队（`starsystem.dm:819-891`）。
条件：威胁常量 `THREAT_LEVEL_NONE 0 / UNSAFE 2 / DANGEROUS 4`（`__DEFINES/starsystem.dm:6-8`）。
例外 / 覆写：误跳/紧急跳跃可能把你丢进一个随机的同扇区星系（`jump_mishap_helpers.dm:1-16`）。
置信度：HIGH（高）

## 阵营 (Factions)

阵营 datum 是 `subtypesof(/datum/faction)`，在启动时实例化（`starsystem.dm:70-74`）。只存在**三个**
（`factions.dm`）：nanotrasen、syndicate、pirate。`FACTION_ID_SOLGOV`/`FACTION_ID_UNATHI` 被定义
（`__DEFINES/overmap.dm:55-56`）并被引用为 NT 的 `preset_allies`，但**不存在对应的阵营 datum**，
因此那些盟友条目解析为 null 并被跳过（`factions.dm:48-52`）。SolGov/Whiterapids 仅作为星系
`alignment` 与 `datum/fleet` alignment 存在，而非阵营。

关系分数：`ALLIES 200 / NEUTRAL 100 / DISTRUST 50 / ENEMIES 0 / HATRED -100`（`factions.dm:2-6`）。
`setup_relationships` 默认每一对为 NEUTRAL，然后盟友→ALLIES，敌人→HATRED（`factions.dm:43-59`）。
预设敌人获得的是 `HATRED`（而非 `ENEMIES`）；`lose_influence` 传播器仅在 `<= ENEMIES(0)` 时触发。

| 阵营 | id | 盟友 | 敌人 | fleet_spawn_rate | 在玩法中的角色 |
|---|---|---|---|---|---|
| Nanotrasen | FACTION_ID_NT (1) | solgov, unathi（仅 id，无 datum） | syndicate, pirate | 40 MIN | 玩家的默认阵营；生成友方 `nanotrasen/light`（高难度时 +elite）；其 `victory()` 揭示 Outpost 45 并授予船员成就（`factions.dm:126-148`） |
| Syndicate | FACTION_ID_SYNDICATE (2) | pirate | nanotrasen | 30 MIN | 主要敌对者；生成 `neutral/boarding/wolfpack/conflagration` 舰队，难度 ≥ 3 时为 elite；`victory()` 公告失败并揭示 O45（`factions.dm:150-169`） |
| Tortuga Raiders (pirate) | FACTION_ID_PIRATES (5) | syndicate | nanotrasen | 10 MIN（默认） | 第三方掠夺者；`pirate/scout` + `pirate/raiding` 舰队（`factions.dm:171-177`） |

解读：“relations” 在机制上仅通过 `lose_influence`（敌人获得你失去的票）与
UI 起作用。对原始关系分数没有通用的战斗反应——`encounter()` 中的实际敌意由匹配
`alignment` 字符串 + `federation_check()` 决定（STARSYS-015）。

### STARSYS-015
断言：敌意/抵达行为由*字符串* `faction`/`alignment` 与 `federation_check` 驱动，而非关系表；Nanotrasen 与 SolGov 被硬编码为互为朋友。
玩家可感知的后果：悬挂 NT/SolGov IFF 的飞船会被 NT 与 SolGov 舰队都视为友方，并从左侧生成；
其他任何飞船都被视为潜在敌人，从右侧生成。
证据：若 `OM.faction == alignment || federation_check(OM)`，`fleet/encounter` 会致意；否则若 `OM.alpha >= 150`
则嘲讽并记录 `last_encounter_time`（`ai-skynet.dm:681-692`）。若一方是 `solgov` 另一方是
`nanotrasen`，`federation_check` 返回 TRUE（`ai-skynet.dm:752-771`）。抵达侧：`ftl_jump.dm:28-31`（STARSYS-004）。
舰队共享的 `datum/faction` 存储为 `F.faction`，并在被摧毁时用于 `lose_influence`
（`ai-skynet.dm:110, 342`）。
条件：`enemies_in_system` 追踪排除 NT/SolGov：`if(OM.faction != "nanotrasen" && OM.faction != "solgov")`
（`starsystem.dm:340`，`ai-skynet.dm:803`）。
例外 / 覆写：IFF 控制台（对**emag 过的**控制台使用 multitool，`iff_console.dm:79-95,124-162`）会翻转
syndicate↔nanotrasen 以及 pirate→nanotrasen；将 MAIN_OVERMAP 从 nanotrasen 翻转为 syndicate 会在一个随机邻近星系生成
`/datum/fleet/solgov/interdiction`（“Code Charlie Foxtrot”）。
置信度：HIGH（高）

### STARSYS-016
断言：阵营还拥有一个“tickets ≥ 700”的竞争性胜利以及一个每阵营的 `victory()`；玩家阵营是 `starting_faction`（所有随附星图模式中均为 nanotrasen）。
玩家可感知的后果：在 Galactic Conquest 中任一方都可凭分数获胜；作为 NT 获胜会揭示 Outpost 45，
以进行船员转移结局。
证据：`factions.dm:29-34` 的 `victory()` 基类设置 `mode.winner`。`overmap_mode.dm:479-484` 在
`tickets >= 700` 时宣告胜者。`starting_faction` = `"nanotrasen"`（patrol/shakedown/hardmode/galactic_conquest/courier/boarding/
armada，`nsv13/code/game/gamemodes/overmap/*.dm`）。主船的阵营由 `mode.starting_faction` 设置
（`overmap_mode.dm:194-195`）。
置信度：HIGH（高）

## 默认星系 (Default Systems)（来自 `config/starmap/starmap_default.json`）

扇区 1（Sol / SolGov）：Sol（首都，`fleet/solgov/earth`）、Ross 154、Barnard's Star、Alpha Centauri、Sirius
（商人 minsky）、Wolf 359、Lalande 21185（超门，`fleet/nanotrasen/border`）、**Outpost 45**（隐藏，traits 15 =
END_ON_ENTER；这是默认的 `return_system`）。

扇区 2（中立 / “Rosetta Cluster”）：Feliciana（超门）、Argo（商人 czanekcorp）、Orion's Arm、Keid、Caph、
Alpha Carinae、Helios Sigma、**Medea**（`fleet/nanotrasen/border/defense`，商人 armsdealer）、Gliese 581-G、Tortuga
（`fleet/pirate/tortuga`）、Feaf、Chalawan、Atik、Outpost 73、Abious II（启动 BRASIL_LITE）、Beta Centauri、Abious I、
Ethor、Zurs、Priar、Viiman、Karip、Arbeia、Zharkov、Bemike（商人 shallowstone/independent）、UY Scuti、**Rubicon**
（`fleet/rubicon`，经由相邻关系的类超门）、Veneto（黑洞）、Remus、Lombard、Economus（超门）、Volsci、
Lesziru、Zeta Reticuli（商人 randy）、Zalosi、Guriibuu、Mediolanum。

扇区 3（辛迪加终局）：Aeterna Victrix、Demon's Maw、Phobos、Deimos、**Dolos Remnants**（启动 DOLOS，
traits 5）、**Abassi**（首都，隐藏，启动 ABASSI）、Oasis Fidei（隐藏，`fleet/remnant`）、Mediolanum、Romulus
（超门）。

注意：“Rosetta Cluster” 仅出现在星系的 `desc` 文本中；并不存在以此命名的星系。`Outpost 45` 在
JSON 中的 traits = 15 = NO_ANOMALIES|NO_ASTEROIDS|NO_WORMHOLE|END_ON_ENTER。各模式起始位置：Argo（patrol、shakedown、
courier、boarding、armada、galactic_conquest）、Medea（hardmode）；采矿船起始于 Lalande 21185
（`types/miningships.dm:46`），除非是 “Staging”（见 `types/*` 注释）。

## 跨系统依赖 (Cross-System Dependencies)

- **FTL 通行（系统 4）**：`drive.dm`/`ftl_jump.dm` 实现跳跃；`star_system/add_ship` 与
  `remove_ship` 是星图↔星系的交接。控制台上的 `cancan` 规则（STARSYS-006）是门控。遗留
  驱动器（`ftl_legacy.dm`）是一个替代实现，也是 `visitable` 的唯一读取者。
- **舰队（系统 5）**：`datum/fleet`（定义于 `ai-skynet.dm`，子类型在 `fleet_types.dm`）拥有 `current_system`、
  `goal_system`、移动/寻路（`fleet_ftl_pathfinding.dm` 的 `navigate_to`）。舰队将自身加入星系的
  `fleets` 列表，驱动 `contested_systems`，设置 `mission_sector`，并生成 `enemies_in_system`。阵营生成
  舰队（STARSYS-010）；舰队经 `starsystem`/`combat_handling` 结算战斗（STARSYS-008）。
- **任务/目标（超出范围）**：`objectives/board_ship.dm:38` 设置
  `target_system.objective_sector`；`datums/missions.dm` 授予票；`overmap_mode.dm` 驱动提醒/胜利条件并将主船移动到
  `starting_system`。
- **映射/配置**：`code/datums/map_config.dm` 提供 `starmap_path` + `return_system`；每个 `_maps/*.json`
  （例如 `galactica.json`）可覆写 `starmap_path`。`SSmapping.config.return_system` 在 init 时被解析为
  `SSstar_system.return_system`（STARSYS-005），并用于回合召回与 blacksite 虫洞
  （`starsystem.dm:874`）。
- **管理员**：`datum/starsystem_manager`（管理员 TGUI）可以跳跃/删除舰队、创建对象、隐藏星系以及切换
  扇区；不是玩家机制。

## 待解问题 (Open Questions)

1. `SSstar_system.bounty_payout()` 在何处（若有）被调用？全仓库搜索只找到定义——
   很可能是死代码，但需确认不存在反射/`callproc` 路径。（STARSYS-011）
2. `visitable` 仅被遗留 FTL 读取；在记录“你无法跳入超空间”之类的规则之前，确认遗留驱动器在任何随附地图上
   是否仍可触及。（STARSYS-001/006）
3. `SSmapping.config.return_system` 解析 bug：当存在 JSON 键 `return_system` 时，`map_config.dm:192-193` 写入的是 `starmap_path`（而非 `return_system`）
   ——核实哪些地图受影响以及有效的返回星系是什么。
4. 硬编码的 `/datum/star_system` 兜底列表（Argo/Ariel/Ida/Foothold/Sion/Muir/Beylix/Sebacien/Tortuga/
   Rubicon/等）与 JSON 默认值实质不同。确认没有随附配置触发兜底
   （即 `starmap_default.json` 总能解码），因此文档应描述 JSON 星系。
5. `fleet_trait` 的 `FLEET_TRAIT_NEUTRAL_ZONE` 移动测试 `if(sys.alignment != "unaligned" || "uncharted")` 于
   `ai-skynet.dm:193-195` 恒为真（条件中是字符串字面量）——看起来是个 bug；影响中立区
   舰队游荡的位置。值得核实其意图。
