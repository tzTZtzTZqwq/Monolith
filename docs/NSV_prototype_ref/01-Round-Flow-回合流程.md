> 本文为 research/evidence/round_flow.md 的中文翻译。

# 回合流程与星图游戏模式 (Round Flow & Overmap Game Modes)

## 系统概览 (System Overview)

NSV13 在原版 SS13 的 `/datum/game_mode` ticker 之上叠加了一个**并行的“星图游戏模式（overmap gamemode）”**。原版 ticker 依旧运行 lobby -> setup -> playing -> finished -> restart 的流程，选择一个基础模式（服务器配置 `MASTER_MODE secret`），并掌管回合结束/重启。星图层是一个独立子系统 `SSovermap_mode`（`/datum/controller/subsystem/overmap_mode`，`nsv13/code/controllers/subsystem/overmap_mode.dm`），它从 `/datum/overmap_gamemode` 子类型中挑选**自己**的游戏模式、向船员下达目标、缩放敌人难度，并能独立地强制原版 ticker 结束回合。

关键入口：
- `SSticker`（`code/controllers/subsystem/ticker.dm`）：第 298 行在 `setup()` 内调用
  `SSovermap_mode.setup_overmap_mode()`；第 352 行在游戏进入 `GAME_STATE_PLAYING` 后调用
  `SSovermap_mode.start_reminder()`。
- `SSovermap_mode.Initialize()`（overmap_mode.dm:60-147）在服务器启动 / 子系统初始化时运行
  （`init_order = INIT_ORDER_OVERMAP_MODE = -120`，即非常靠后），挑选模式，并快照
  已连接玩家数量。
- `SSstar_system`（`nsv13/code/controllers/subsystem/starsystem.dm`）持有星图、阵营、
  `return_system`，以及仅由 PvP 使用的 `time_limit`。

## 核心玩法循环 (Core Gameplay Loop)

1. 服务器启动；`SSovermap_mode.Initialize()` 按地图黑名单/白名单和玩家数量过滤 `/datum/overmap_gamemode` 子类型，然后加权挑选一个。若候选池为空，则默认为 **Patrol（巡逻）**（overmap_mode.dm:118-145）。
2. 大厅倒计时结束；基础 ticker 运行 `pre_setup()`（基础反派对选择）再运行 `setup()`。
   `setup()` 调用 `SSovermap_mode.setup_overmap_mode()`（ticker.dm:298），该过程实例化模式的
   目标、将主船 + 采矿船移动到模式的 `starting_system`，并应用 `starting_faction`
   （overmap_mode.dm:149-204）。
3. 游戏进入 playing；`SSovermap_mode.start_reminder()`（ticker.dm:352）为任务简报安排
   `announce_delay`（3 MINUTES，overmap_mode.dm:34,277）后执行，然后将其作为一条
   "Mission Briefing" 指挥报告打印到通讯控制台（overmap_mode.dm:277-303）。
4. `SSovermap_mode.fire()` 在 playing 期间每秒 tick 一次（overmap_mode.dm:218）。每 10
   分钟它上调 threat_elevation、重新计算难度，并调用 `mode.check_completion()`。
   按其自身调度，它还会运行 "reminder" 催促循环。
5. 船员完成目标 -> `mode.check_completion()` 调用 `victory()` -> 要么触发
   "Press On Or Return Home?" 投票（首次完成），要么强制进行一次 FTL 返航跳跃（若本局
   已被延长）（overmap_mode.dm:509-526）。
6. 将主船送回 Outpost 45（标记为 `STARSYSTEM_END_ON_ENTER`）会设置
   `GLOB.crew_transfer_risa = TRUE`（ftl_jump.dm:47-59），这使得原版基础模式的
   `check_finished()` 返回 true（game_mode.dm:315），回合以新闻报道 SHIP_VICTORY
   结束。

## 机制 (Mechanics)

### ROUND-001
断言：每一个星图回合，无论游戏模式为何，都会在模式固定/随机目标之外，额外获得一个基线“执行 N 次 FTL 跳跃”目标。
玩家可感知的后果：任务简报中总是会列出一个跳数任务（目标随机 6-10 跳），与模式专属目标并列。
证据：`/datum/overmap_gamemode` 的 `New()` 设置 `objectives = list(/datum/overmap_objective/perform_jumps)`
（overmap_mode.dm:416-419）；随后 `setup_overmap_mode()` 执行 `mode.objectives += mode.fixed_objectives`
并追加随机目标（overmap_mode.dm:164-185）；`perform_jumps/instance()` 设置
`target = rand(minimum_jumps=6, maximum_jumps=10)` 并注册 `COMSIG_SHIP_ARRIVED`
（perform_jumps.dm:8-16）。没有任何子类型清空 `objectives`。
条件：每次主船 FTL 抵达时 perform_jumps 递增；同时调用
`update_reminder(objective=TRUE)`。
例外 / 覆写：Shakedown 的随机池中也包含 perform_jumps，因此一次 shakedown 可能
摇出两个跳跃目标。
置信度：HIGH（高）

### ROUND-002
断言：星图游戏模式的选择是加权的，并会按地图黑名单/白名单和玩家数量过滤，也可被管理员或 PvP 覆写。
玩家可感知的后果：船员拿到哪个任务取决于服务器配置权重以及启动时已连接玩家的数量。
证据：`Initialize()`（overmap_mode.dm:60-147）：移除 `whitelist_only` 模式；应用
`SSmapping.config.omode_blacklist` / `omode_whitelist`；统计带 client 的 `/mob/dead/new_player`
计入 `player_check`；剔除低于 `required_players` 或高于 `max_players` 的模式（来自按键列表
配置 `omode_min_pop` / `omode_max_pop`，否则用子类型初始值）；根据 `omode_probability` 或
`selection_weight` 构建加权 `mode_select` 列表并 `pick()` 一个。
配置项：`nsv13/code/controllers/configuration/entries/game_options.dm:21-32`。
配置值：`config/game_options.txt:673-697`。
条件：`forced_mode`（管理员）绕过选择；PvP（`/datum/game_mode/pvp/pre_setup`）
会用 `galactic_conquest` 覆写 `SSovermap_mode.mode`（pvp.dm:110）。
例外 / 覆写：候选池为空时，默认为 `patrol`（overmap_mode.dm:143-145）。
置信度：HIGH（高）

### ROUND-003
断言：有效选择权重/最小/最大人口来自服务器配置文件，以 `config_tag` 的小写形式为键，覆写子类型的默认值。
玩家可感知的后果：例如 Courier/Shakedown 仅在玩家数 ≤10 时可用，Boarding
需要 14，Armada 需要 15，Patrol 需要 10。
证据：配置 `config/game_options.txt:673-697`（每个 tag 的 OMODE_PROBABILITY/MIN_POP/MAX_POP）；
按键列表键在 `config_entry.dm:181` 中小写化；查询在 overmap_mode.dm:72-116。
条件：配置键在文件中使用大写，但会与小写的 `config_tag` 匹配（"patrol"、"boarding"、"courier"、"armada"、"shakedown"、"hardmode"、"conquest"）。注意文件
键 `PVP` 映射到基础游戏模式，而非 `conquest` 星图标签。
例外 / 覆写：`PVP` 概率为 0；galactic_conquest 的 `config_tag = "conquest"` 没有
配置项，因此它永远不会作为普通星图模式被摇出。
置信度：HIGH（高）

### ROUND-004
断言：已连接玩家的快照是在子系统启动时取得的，而非回合开始时，并会被复用于目标资格过滤。
玩家可感知的后果：`required_players`/`maximum_players` 与启动时人口不匹配的随机目标会从池中被静默移除。
证据：`player_check` 在 `Initialize()` 中递增（overmap_mode.dm:94-96）；在
`setup_overmap_mode()` 中被复用于随机目标过滤（overmap_mode.dm:168-179）。
条件：由于 `Initialize()` 在启动/大厅阶段运行，这可近似视为本回合的人口。
置信度：MEDIUM（中）（时间由 init_order 与大厅状态推断；并非由注释断言）

### ROUND-005
断言：任务简报在回合开始约 3 分钟后作为一份打印的指挥报告送达，目标也会被单独打印。
玩家可感知的后果：船员在通讯控制台上读到 "Mission Briefing: XYZ-<roundid>" 报告；对于
延长后的简报，标题会带 "-Ext." 后缀。
证据：`start_reminder()` 在 `announce_delay=3 MINUTES` 后调度 `announce_objectives()`
（overmap_mode.dm:34,273-275）；`announce_objectives()`（overmap_mode.dm:277-303）通过
`print_command_report` 打印 `mode.brief` + 每个 `O.brief`，并调用一次 `O.print_objective_report()`。
注意：`print_command_report` 的目标是原版通讯控制台报告系统。
条件：首次打印之后追加的目标会再次打印报告，但会跳过重复的逐目标报告（`announced_objectives` 守卫，overmap_mode.dm:299）。
例外 / 覆写：Courier 调试模式会把 `announce_delay` 缩短到 10 秒（courier.dm:31）。
置信度：HIGH（高）

### ROUND-006
断言：当目标没有进展时，"reminder" 系统会按计时器催促船员，并带有模式专属的升级机制；某些模式在战斗时会重置计时器，而非目标进展时。
玩家可感知的后果：来自 "Naval Command" 的不断升级的优先级公告；对于基础
模式，提醒 2-4 各扣 25 NT 影响力（tickets），提醒 5 会在船员当前所在星系生成一支辛迪加封锁舰队。
证据：`fire()` 中的 reminder 循环（overmap_mode.dm:230-271）；`setup_overmap_mode()` 将
`objective_reminder_setting` 映射为 `objective_resets_reminder`（0）、`combat_resets_reminder`（1）、
`combat_delays_reminder`+`combat_delay`（2）或 `objective_reminder_override`（3）
（overmap_mode.dm:153-162）。`update_reminder()`（overmap_mode.dm:305-319）。基础后果
`consequence_one..five`（overmap_mode.dm:427-451）；默认提醒文本（overmap_mode.dm:409-414）。
条件：`objective_reminder_interval` 默认 15 MINUTES；armada 为 3 MINUTES；每个模式都可
覆写提醒文本。战斗进入通过 AI 瞄准 MAIN_OVERMAP 时调用的 `update_reminder()` 实现
（ai-skynet.dm:1619-1622）。
例外 / 覆写：提醒 1-5 在层数 1..5 上依次触发，然后回绕到 0。回合延长
之后，消息会变为自动召回倒计时（overmap_mode.dm:256-271）。
置信度：HIGH（高）

### ROUND-007
断言：`threat_elevation` 与 `mode.difficulty` 都会缩放敌方舰队；威胁随时间的被动增长（宽限期之后）以及击杀敌人而上升，并因完成目标而下降。
玩家可感知的后果：回合后期、以及击杀大量敌人之后，敌对舰队会变大；
完成目标会让未来的敌人变小。
证据：定义 `nsv13/code/__DEFINES/skynet.dm:26-36`（`TE_INITIAL_DELAY 25 MIN`、
`TE_THREAT_PER_HOUR 100`、`TE_OBJECTIVE_THREAT_NEGATION 50`、`TE_POINTS_PER_FLEET_SIZE 100`、
`TE_FLEET_THREAT_DYNAMIC`、`TE_FLEET_KILL_THREAT 10`）。被动增长在 `fire()` 中每 10 分钟一次
（overmap_mode.dm:221-225）。目标成功时在 `check_completion()` 中下降
（overmap_mode.dm:476-478）。应用于舰队于 `ai-skynet.dm:146-153` 和 `777-789`
（`applied_size += round(threat_elevation / TE_POINTS_PER_FLEET_SIZE)`）。
条件：被动威胁仅在 `world.time > TE_INITIAL_DELAY`（25 分钟）之后开始。启用
`hard_mode_enabled` 时，被动增益为每 10 分钟 `TE_THREAT_PER_HOUR/2`，正常为 `/6`
（快 3 倍）（overmap_mode.dm:222-225）。`threat_elevation` 下限为 0
（`modify_threat_elevation`，overmap_mode.dm:213-216）。PvP 会禁用舰队威胁
（`threat_elevation_allowed = FALSE`，ai-skynet.dm:786-787）。
例外 / 覆写：声明的 `next_difficulty_increase = 30 MINUTES` 变量在任何地方都
未被读取——周期性难度递增并非由它驱动。
置信度：HIGH（高）

### ROUND-008
断言：基础难度每 10 分钟根据 ALIVE 玩家人数重新计算，钳制在 1-5，再加上管理员的 `escalation` 值；它不是单调的“随时间递增”计时器。
玩家可感知的后果：舰队规模更多跟随船员存活情况而非流逝时间；存活玩家越少 -> 难度越低。
证据：`difficulty_calc()`（overmap_mode.dm:365-370）：`CLAMP(CEILING(players/10,1),1,5)` 然后
`+= escalation`。在 `fire()` 中每 10 分钟调用一次（overmap_mode.dm:226）。`escalation` 可由管理员
经 `overmap_mode_controller` 调整（钳制 -5..5，overmap_mode.dm:609-618）。
条件：`get_active_player_count(TRUE, FALSE, FALSE)` = 仅存活（`__HELPERS/game.dm:379`）。
置信度：MEDIUM（中）（“随时间递增”的玩家可感知效果来自 ROUND-007 的威胁，
而非此重算）

### ROUND-009
断言：基于票数的胜利：船员通过累积阵营 "tickets" 获胜；原版 Patrol 目标是达到 700 NT 票。
玩家可感知的后果：击杀敌对舰队（以及 NT 空间站任务）会奖励走向目标的胜利票。
证据：`/datum/overmap_objective/tickets` 的 `target = F.tickets + ticket_amount`，其中
`ticket_amount = 700`；当 `F.tickets >= target` 时 `check_completion()` 完成
（tickets.dm:1-28）。票通过在被击败阵营上调用 `lose_influence` 获得，这会记入
该阵营的敌人（factions.dm:61-68）；舰队奖励 `reward = 100`，乘以
`applied_size`（ai-skynet.dm:59,785），在舰队 `destroy()`/`defeat` 时应用（ai-skynet.dm:340-344）。
空间站任务（`/datum/nsv_mission/payout`）也会增加 `ticket_bounty`（missions.dm:71-75）。
条件：Patrol 的 `fixed_objectives = tickets/nt`（patrol.dm:15）。阵营 `tickets` 初始为 0
（factions.dm:16）。
例外 / 覆写：`tickets` 的 desc 字符串 `Acquire ticket_amount Tickets` 是一个未填充的
模板（仅为外观）。若船员闲置，`consequence_two..four` 会扣除 25 票。
置信度：HIGH（高）

### ROUND-010
断言：通过返航跳跃结束回合：胜利后船员投票；选择返回（或已完成的延长回合）会强制主船 FTL 跳往 `return_system`（Outpost 45），其抵达即结束回合。
玩家可感知的后果：先是 "Mission Complete - Vote Pending"，随后若返回，飞船跳回家；进入 Outpost 45 会宣布成功并结束回合。
证据：`victory()`（overmap_mode.dm:509-526）揭示 `SSstar_system.return_system`，发起
"Press On Or Return Home?" 投票（首次）或调用 `OM.force_return_jump()`。
`force_return_jump()`（ftl_jump.dm:172-194）设置 `SSovermap_mode.already_ended = TRUE`，锁定
驱动器，并跳回家。前哨（`STARSYSTEM_END_ON_ENTER`，
starsystem.dm:1093-1101）上的 `after_enter()` 设置 `GLOB.crew_transfer_risa = TRUE` 并调用
`SSticker.mode.check_finished()`（ftl_jump.dm:47-59）。`check_finished()` 在
`crew_transfer_risa` 时返回 TRUE（game_mode.dm:310-316）。
条件：若飞船仍在战斗中，`victory()`/延长的自动召回会等到
`enemies_in_system` 为空（overmap_mode.dm:256-271, 522-526）。若已处于跳跃中，返航跳跃也会被阻止/重试
（ftl_jump.dm:178-181）。
例外 / 覆写：`SSovermap_mode.admin_override` 抑制结束
（overmap_mode.dm:511-513）。导航 UI 仅在 `already_ended || round_extended` 时允许手动跳向前哨
（`return_jump_check`，starmap.dm:239-240）。
置信度：HIGH（高）

### ROUND-011
断言：回合延长：首次胜利投票可以通过授予“额外目标”（清除 Rubicon，或票 + 升级的舰队）来延长回合，而非结束。
玩家可感知的后果：船员可以选择继续游玩；提醒系统切换为
自动召回倒计时，并加入更难的目标。
证据：投票选项在 vote.dm:265-267 中添加；在 vote.dm:183-193 中处理，设置
`round_extended = TRUE`，重置 `already_ended`/`objectives_completed`，调用
`request_additional_objectives()`。该 proc（overmap_mode.dm:321-363）将旧目标标记为
`ignore_check`，添加 `clear_system/rubicon`（若有敌人且玩家 >10），否则添加一个 `tickets` 目标
外加 `F.send_fleet(custom_difficulty = mode.difficulty+1)` 且 `escalation += 1`，并将提醒
重置为 10 MINUTE 的硬间隔。延长回合的自动召回循环：overmap_mode.dm:256-271。
条件：若船员反而选择返回，回合正常结束。
置信度：HIGH（高）

### ROUND-012
断言：回合可因飞船被毁、丢失一个防守星系（armada）、SolGov 母星舰队陷落，以及 PvP 结果/超时而被迫结束。
玩家可感知的后果：摧毁主船（反应堆死亡/毁灭）或 Armada 防御失败会以失败/核爆结束回合。
证据：主星图被毁 -> `SSticker.force_ending = 1`（overmap.dm:542-548；
ai_interiors.dm 的 `decimate_area` 44-51；SolGov 地球舰队在 ai-skynet.dm:347-351 被击败）。
Armada 失败 -> `system_defence_armada/check_completion` 调用 `SSovermap_mode.mode.defeat()`
（system_defence_armada.dm:18-26）。`defeat()` 宣布并设置 `SSticker.force_ending`
（overmap_mode.dm:528-531）。PvP 时间限制与旗舰损失在 pvp.dm / starsystem.dm:36-44 中处理。
条件：`defeat()` 只会触发一次；若 `already_ended`，`check_completion` 会提前返回。
置信度：HIGH（高）

### ROUND-013
断言：PvP（Galactic Conquest）是一个独立的基础游戏模式（`/datum/game_mode/pvp`），它用 `galactic_conquest` 替换星图模式，禁用威胁/提醒压力，生成一艘专用的辛迪加舰船地图，并设定 2 小时 30 分的硬性时间限制。
玩家可感知的后果：两支舰队在星系间战斗，用信标翻转领土。
证据：pvp.dm:11-49（`config_tag = "pvp"`、`required_players = 24`、`required_enemies = 12`、
`time_limit = 2 HOURS + 30 MINUTES`、三个按人口解锁的 PvP 地图）。`pre_setup()` 从
`_maps/map_files/Instanced/<map>.json` 实例化 syndiship，设置
`SSovermap_mode.mode = new/datum/overmap_gamemode/galactic_conquest`（pvp.dm:87-123）。
`post_setup()` 设置 `SSstar_system.time_limit`，交换舰队生成率，并注册一个敌对
环境（禁止撤离）（pvp.dm:131-140）。
条件：`galactic_conquest` 设置 `objective_reminder_setting = 3`（禁用提醒）
（galactic_conquest.dm:10）。
例外 / 覆写：`time_limit` 到期会通过 `starsystem.fire()` 结束回合（starsystem.dm:36-44）。
置信度：HIGH（高）

### ROUND-014
断言：Galactic Conquest 的胜利 = 某阵营达到 700 票（或旗舰被毁 / 超时）；星图的 `check_completion` 将任意阵营的 700 票视为胜利。
玩家可感知的后果：星系通过信标翻转，每个 100 分；率先到 700
（通常是 7 次占领）的一方获胜。
证据：`check_completion` 的 PvP 分支：对任意阵营 `if(F.tickets >= 700) mode.winner = F`
（overmap_mode.dm:479-484）。信标占领奖励 `points_per_capture = 100` 并翻转
`owner`/`alignment`（items.dm:101,125-136,202-208）。NT 信标采用 60 秒计时 / 3 分钟冷却，
辛迪加为 150 秒 / 7 分钟（items.dm:102-105,199-200）。旗舰被毁 -> `force_win` /
`force_loss` -> 设置 `winner` -> `check_finished()`（pvp.dm:147-166）。
条件：`galactic_conquest` 还携带一个 `tickets/nt` 固定目标（目标 NT 0+700）
（galactic_conquest.dm:11），因此星图目标与 PvP 检查可以各自独立触发。
例外 / 覆写：`set_round_result()` 按胜者阵营 id 报告
（NT / pirates / syndicate）（pvp.dm:180-202）。
置信度：HIGH（高）（items.dm:101 中有一条代码注释写着 "1000 points to win"，它是
过时的；星图检查为 700，与模式描述一致）

### ROUND-015
断言：Hardmode（“Dolos Assault”）仅限白名单，通常无法通过随机选择触及；它通过一个管理员/投票开关启用，该开关同时强制切换星图模式、解锁 hardmode 研究、生成一支敌方舰队，并（回合前）将基础模式切换为 `secret_extended`。
玩家可感知的后果：一个更难的变体，带有 Rubicon+Dolos 清除目标、最多 25 名
必需玩家、威胁上升快 3 倍，以及新的可建造火炮。
证据：hardmode.dm:3-15（`whitelist_only = TRUE`、`required_players = 25`、
`fixed_objectives = clear_system/rubicon + clear_system/dolos`、`starting_system = "Medea"`）。
`toggle_hardmode()`（hardmode.dm:17-41）调用 `force_mode(hardmode)`、
`SSresearch.hardmode_tech_enable()`，在 Rubicon 生成 `/datum/fleet/interdiction/light`，并在
回合尚未开始时设置 `GLOB.master_mode = "secret_extended"`。禁用则回退到 patrol 并
撤退残党舰队。
条件：`whitelist_only` 模式在 `Initialize()` 中被剥离（overmap_mode.dm:78-79），且没有
任何随附地图 JSON 列出 `omode_whitelist`，因此它永远不会随机摇出；可通过
`toggle_hardmode` 或 "hardmode" 投票路径触及（vote.dm:194-196）。
例外 / 覆写：管理员的 `change_gamemode` 明确将 hardmode 排除在其池之外
（overmap_mode.dm:628）。
置信度：HIGH（高）

### ROUND-016
断言：在 hardmode/回合延长之后，回合成一局“持续”的 secret_extended 回合，没有可击杀的敌对者，因此基础 `check_finished` 永远不会因反派死亡而自动结束。
证据：配置 `config/game_options.txt` 中 `MASTER_MODE secret`、`CONTINUOUS SECRET_EXTENDED`；
hardmode 在回合前设置 `master_mode = "secret_extended"`（hardmode.dm:30-31）。
条件：结束由星图流程（返航跳跃 / 失败）驱动，而非反派死亡。
置信度：MEDIUM（中）（依赖配置；服务器运营者可能更改 MASTER_MODE）

### ROUND-017
断言：玩家如何进入回合（仅限关联）：玩家在大厅中以 `/mob/dead/new_player` 等待，ticker 将他们计入 `player_check`；职业在星图设置之后由 `SSjob.DivideOccupations()` 分配；PvP 辛迪加船员在 `post_setup` 中所分配的职业偏好里挑选一个偏好的角色。
证据：`player_check` 统计带 client 的 `/mob/dead/new_player`（overmap_mode.dm:94-96）；
`SSjob.DivideOccupations` 在 `setup_overmap_mode()` 之后调用（ticker.dm:298-300）；PvP 角色
分配通过 `preferred_syndie_role` 与 `assign_jobs()`（pvp.dm:56-84, roles.dm）。
条件：完整的职业/大厅机制属于系统 25（超出范围）。
置信度：HIGH（高）（仅为关联）

## 游戏模式对比 (Gamemode Comparison)

| 名称 | config_tag | 必需玩家数（配置） | 权重（配置） | 起始星系 | 固定目标 | 胜/负条件 |
|---|---|---|---|---|---|---|
| Patrol | patrol | 10 | 5 | Argo | tickets/nt (700) | 700 NT 票 -> 投票/返回；飞船损失 = 失败 |
| Armada | armada | 15 | 0 | Argo | system_defence_armada（随机 NT 星系） | 防守目标星系；丢失即 defeat() |
| Boarding | boarding | 14 | 5 | Argo | board_ship（随机辛迪加飞船，IFF 翻转） | 夺取/黑入目标飞船 -> 胜利 |
| Courier | courier | 0（最大 10） | 5 | Argo | 无（3 个随机货物目标） | 交付全部 3 个货物目标 -> 胜利 |
| Galactic Conquest | conquest | （基础模式 pvp：24 / 12 敌人） | 0（tag 不在配置中） | Argo | tickets/nt (700) | 任意阵营到 700 票，或击杀旗舰 |
| Hardmode (Dolos Assault) | hardmode | 25 | 5（whitelist_only） | Medea | clear_system/rubicon + clear_system/dolos | 清除两个星系 -> 胜利；仅通过 toggle/白名单 |
| Shakedown | shakedown | 0（最大 10） | 5 | Argo | 无（3 个随机：jumps/destroy_fleets/apnw/scan） | 完成 3 个随机目标 -> 胜利 |

注意：所有行也都携带基线 perform_jumps 目标（ROUND-001）。
必需/权重值为有效配置文件值（`config/game_options.txt:673-697`），
其覆写各游戏模式文件中显示的子类型默认值。

## 跨系统依赖 (Cross-System Dependencies)

- **基础 ticker（`SSticker`）**：掌管大厅/回合开始/回合结束/重启；NSV 注入
  `setup_overmap_mode()` 与 `start_reminder()`。回合结束由 `crew_transfer_risa`
  （game_mode.dm:315）或 `force_ending` 驱动。
- **`SSstar_system`**：提供 `find_main_overmap()`/`find_main_miner()`、`return_system`、
  阵营、`time_limit`，以及星系（Argo、Medea、Rubicon、Dolos Remnants、Outpost 45）。
- **`SSmapping.config`**：`omode_blacklist`/`omode_whitelist`/`starmap_path`/`return_system`
  （map_config.dm:26-29,186-193）；所有地图 JSON 都将 `starmap_path` 指向
  `config/starmap/starmap.json`。
- **阵营（`factions.dm`）**：`tickets`、`send_fleet`、`lose_influence`/`gain_influence`；
  支撑 ROUND-009/014 的票数经济。
- **`/datum/fleet`（ai-skynet.dm）**：消耗 `mode.difficulty` + `threat_elevation` 来计算
  `applied_size`；被击败时奖励票。
- **研究（`SSresearch.hardmode_tech_enable/disable`）**：hardmode 解锁可建造火炮。
- **投票子系统（`SSvote`）**："Press On Or Return Home?" 与 "hardmode" 投票。
- **FTL（`ftl_jump.dm` / `starmap.dm`）**：返航跳跃、`STARSYSTEM_END_ON_ENTER`、导航门控。
- **货物/货运（`cargo/*`、munitions torpedos）**：通过
  `COMSIG_CARGO_DELIVERED` 的 courier 目标交付（torpedo_types.dm:212；_cargo.dm:187-190）。

## 待解问题 (Open Questions)

- `player_check` 是启动时快照；确认当服务器在两回合之间不重启时的行为（init 时机 vs 大厅重新填充）。
- `next_difficulty_increase` 是死代码；确认原本设计意图是否已被
  `difficulty_calc()` + 被动威胁取代。
- 每个 NSV 回合实际上摇出哪个基础 SS13 游戏模式（MASTER_MODE secret），以及它多频繁地
  在星图目标之前独立结束回合——需要配置 + 实机检查。
- `conquest` 星图标签与 `PVP` 配置键不匹配：确认没有运营者将二者对应。
- 随附地图是否曾设置 `omode_whitelist`/`return_system`（仓库 JSON 中未找到任何设置），
  因此 hardmode/Outpost-45 返回完全依赖配置/强制路径。
- 某模式的随机目标池被抽空时的确切行为（例如 courier 的有效货物目标 <3）——会记录为管理员消息并中断循环
  （overmap_mode.dm:176-178）。
