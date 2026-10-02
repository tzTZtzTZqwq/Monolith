> 本文为 research/evidence/missions_objectives.md 的中文翻译。

# 任务与目标 (Missions & Objectives)

系统 #6（任务与目标）的研究笔记。真相来源是写作时检出的 `D:\code\NSV13` 提交处的代码。补充 `round_flow.md`（该文件从高层覆盖游戏模式框架）；本文件深入探讨目标 datum 树以及货物/货运目标子系统。

## 系统概览 (System Overview)

NSV13 中有**两个并行的“任务”层**，其中只有一个处于活跃状态：

1. **Overmap 目标**（`/datum/overmap_objective`）—— 活跃层。一个 `/datum/overmap_gamemode` 声明 `fixed_objectives`（总是分配）和/或 `random_objectives`（一个池，从中抽取 `random_objective_amount` 个）。`SSovermap_mode.setup_overmap_mode()` 通过 `instance_objectives()` 实例化每个目标，并（可选地）打印它们。完成情况由 `SSovermap_mode` 每 10 分钟轮询，以及从通讯控制台按需轮询。证据：`nsv13/code/controllers/subsystem/overmap_mode.dm:149-211, 453-507`。

2. **空间站任务**（`/datum/nsv_mission`）—— **已弃用 / 死代码**。`nsv13/code/datums/missions.dm` 和 `nsv13/code/datums/components/missions.dm` 明确标注“This file is now deprecated by overmap gamemode cargo objectives”（`missions.dm:1`、`components/missions.dm:1`）。本应发布它们的商人 UI 钩子（`give_mission`、`trader.ui_act` 的 "mission" 分支）被注释掉（`nsv13/code/modules/overmap/traders.dm:385-419`）。`mission_rewards.dm` / `mission_cargos.dm` 包含只被这个死层使用的内容。不要把空间站任务描述为可玩内容。

Overmap 目标 datum 树（`/datum/overmap_objective`，定义于 `overmap_mode.dm:533-568`）：

- `tickets`（+ `tickets/nt`）
- `destroy_fleets`
- `perform_jumps`
- `scan`
- `apnw_efficiency`
- `board_ship`
- `system_defence_armada`
- `clear_system`（+ `clear_system/rubicon`、`/clear_system/dolos`）——“rubicon”目标
- `cargo`（大型子树；见下文）
- `custom`（管理创建）

状态码（`overmap_mode.dm:3-6`）：`STATUS_INPROGRESS 0`、`STATUS_COMPLETED 1`、`STATUS_FAILED 2`、`STATUS_OVERRIDE 3`（Victory Override = 通过胜利即时结束回合）。

## 核心玩法循环 (Core Gameplay Loop)

1. 回合开始时，`SSovermap_mode/Initialize` 选择一个模式（按配置加权，由 `required_players`/`max_players` 过滤）。`setup_overmap_mode()` 将 `fixed_objectives` 加入 `mode.objectives`，抽取随机项，然后 `instance_objectives()` 对每个调用 `O.instance()`（生成飞船、发信号、一次性设置）。主飞船 + 采矿船被传送到 `mode.starting_system`。（`overmap_mode.dm:60-205`。）
2. 约 3 分钟后（`announce_delay = 3 MINUTES`），`announce_objectives()` 打印一份指挥报告，列出 `mode.brief` + 每个 `O.brief`，并调用 `O.print_objective_report()` 以提供逐目标细节（货物目标会打印一份“Secure Supply Request Form”）。（`overmap_mode.dm:34, 273-303`。）
3. 船员在回合中执行任务。目标通过其自身信号（舰队击毁、FTL 抵达、异常扫描）或通过轮询（`apnw` 的 `process()`，或 10 分钟的 `mode.check_completion()`）完成。
4. 在空间站完成一次货运，或以其他方式推进进度，会重置提醒计时器。
5. 当每个目标都为 `STATUS_COMPLETED`（或作为非游戏模式的失败而被跳过）且没有游戏模式目标失败时，`check_finished`->`victory()` 触发：投票“Press On Or Return Home?”。继续 -> `request_additional_objectives()` 添加更多（Rubicon 清理，或票据 + 更难舰队）。返回 -> FTL 跃迁回家。（`overmap_mode.dm:453-526`。）
6. 闲置的船员会按 `objective_reminder_interval`（默认 15 分钟）被催促，并伴随逐步升级的后果。（`overmap_mode.dm:218-271, 427-451`。）

## 机制 (Mechanics)

### MISSION-001

断言：每个目标都是一个带数值 `status` 的 `/datum/overmap_objective`；进度任务更新 `tally` 并与 `target` 比较，`check_completion()` 在达成时把 `status` 翻为 1。
面向玩家的后果：船员的清单要么静默推进，要么瞬间“完成”；目标从不从已完成回退到未完成。
证据：基础 datum `overmap_mode.dm:533-568`；轮询循环 `overmap_mode.dm:464-475`。子类型：`destroy_fleet.dm:21-32`、`perform_jumps.dm:18-30`、`scan.dm:37-45`、`apnw_efficiency.dm:23-34`。
条件：`check_completion()` 由以下调用：(a) 子系统每 10 分钟，(b) `update_reminder` tick，(c) 通讯控制台，(d) 信号回调，(e) APNW 的 `process()`。（`overmap_mode.dm:218-271, 453-507`；`communications.dm:91,284,355`。）
例外 / 覆写：`scan`、`apnw_efficiency`、`tickets` 的 `binary = FALSE`（进度是“目标量”，而非 T/F），但仍以 `tally>=target` 完成。`status` 可由管理面板强制设为 0/1/2/3（`overmap_mode.dm:661-677`）。
置信度：HIGH（高）

### MISSION-002

断言：船员只能从打印的指挥报告 / 通讯控制台得知其目标；确切文本是 `O.brief`，货物目标还会额外打印一份逐目标表单。
面向玩家的后果：如果船员从不阅读通讯报告或补给请求表单，他们在世界中就没有一份该做什么的清单（目标 datum 本身没有 HUD）。
证据：`announce_objectives()` `overmap_mode.dm:277-303`（`print_command_report(..., TRUE)`）；`O.print_objective_report()` 默认在 `overmap_mode.dm:560` 为空；货物覆写打印表单 `cargo/_cargo.dm:130-148`。
条件：目标报告在公告时打印一次；若在公告后运行（管理回合中途添加），`instance()` 也会打印一份报告——`overmap_mode.dm:551-556`。
例外 / 覆写：管理员可添加一个带任意文本的 `custom` 目标（`overmap_mode.dm:562-568`）。
置信度：HIGH（高）

### MISSION-003

断言：未能取得进展会触发逐步升级的“提醒”公告；每个模式通过 `objective_reminder_setting` 选择四种行为之一。
面向玩家的后果：在默认模式下，船员每 `objective_reminder_interval` 被催促一次；到第 5 次提醒时，一支拦截舰队会生成在他们头上，且在部分模式下阵营会损失票据。
证据：设置 switch `overmap_mode.dm:153-162`；1-5 层逻辑 `overmap_mode.dm:238-255`；默认的 `consequence_two/three/four` 各调用 `F.lose_influence(25)`（`overmap_mode.dm:429-439`）；`consequence_five` 生成 `/datum/fleet/interdiction` 并重置层数（`overmap_mode.dm:441-451`）。`update_reminder(objective=TRUE)` 将层数重置为 0 并推迟计时器（`overmap_mode.dm:305-319`）。
条件：`objective_reminder_interval` 默认 15 MIN；armada 3 MIN；extension 硬性设为 10 MIN（`overmap_mode.dm:398, armada.dm:11, overmap_mode.dm:361`）。设置枚举：0 objectives-reset、1 combat-resets、2 combat-delays、3 disabled（`overmap_mode.dm:8-11, 153-162`）。
例外 / 覆写：`patrol`/`hardmode`/`galactic_conquest` 使用设置 1/1/3（`patrol.dm:12`、`hardmode.dm:10`、`galactic_conquest.dm:10`）。在一轮被延长（`round_extended`）后，提醒切换为一个自动召回倒计时，倒计时在战斗外到期时强制 `victory()`（`overmap_mode.dm:256-271`）。Armada 模式将所有后果覆写为空操作，除了 four/five（`armada.dm:44-74`）。
置信度：HIGH（高）

### MISSION-004

断言：完成所有目标 = 胜利；失去主飞船或 armada 的目标 = 失败。
面向玩家的后果：胜利会发起一个继续或回家的投票；失败会以一条“Mission Critical Failure”公告（“carbon asset liquidation”）立即结束回合。
证据：`check_completion()` 统计成功数，若 `objective_check >= objective_length && !failed`，授予一个成就并调用 `victory()`（`overmap_mode.dm:453-507`）；`victory()` 发起投票或 FTL 返航（`overmap_mode.dm:509-526`）；`defeat()` 公告并强制回合结束（`overmap_mode.dm:528-531`）。
条件：`ignore_check == TRUE` 的目标（在延长一轮时设置）即使 `STATUS_FAILED` 也不计为模式失败（`overmap_mode.dm:474, 321-323`）。
例外 / 覆写：`admin_override` 阻止胜利结束但不阻止公告（`overmap_mode.dm:509-513`）；`STATUS_OVERRIDE`（3）无视其他目标立即获胜（`overmap_mode.dm:466-468`）。`system_defence_armada` 直接调用 `defeat()`（`system_defence_armada.dm:18-26`）。Patrol 还向 Station 阵营的存活玩家授予 `crew_competent` 成就（`overmap_mode.dm:485-506`）。
置信度：HIGH（高）

### MISSION-005

断言：目标完成会降低“threat elevation”，而后者会抬高未来敌方舰队的规模。
面向玩家的后果：完成任务会让后续敌方舰队更小；闲置和击毁舰队会提高威胁并使它们更大（并生成一支拦截舰队）。
证据：`check_completion()` 施加 `modify_threat_elevation(-TE_OBJECTIVE_THREAT_NEGATION * (successes - highest_objective_completion))`（`overmap_mode.dm:476-478`）；`TE_OBJECTIVE_THREAT_NEGATION = 50`（`__DEFINES/skynet.dm:30`）。在 `TE_INITIAL_DELAY`（25 分钟）之后每 10 分钟被动增加 `TE_THREAT_PER_HOUR/6`（`overmap_mode.dm:221-225`；`skynet.dm:28-29`）。对非 NT 击杀获得 `TE_FLEET_KILL_THREAT (10) * applied_size`（`ai-skynet.dm:343-344`；`skynet.dm:31-32`）。威胁通过 `applied_size += round(threat_elevation / TE_POINTS_PER_FLEET_SIZE)` 影响舰队规模（`ai-skynet.dm:153,789`；`skynet.dm:34`）。
条件：只有高于此前最高水位的*新*成功才降低威胁，因此重复完成一个目标不会反复降低它。Hardmode 被动增益是 `/2` 而非 `/6`（`overmap_mode.dm:222-225`）。
例外 / 覆写：`modify_threat_elevation` 在 0 处钳制，永不为负（`overmap_mode.dm:213-216`）。
置信度：HIGH（高）

### MISSION-006 (tickets)

断言：`tickets` 目标是 Patrol/Galactic Conquest 的胜利条件：累积 `ticket_amount`（默认 700）**加到该阵营当前的票据数上**。
面向玩家的后果：在 Patrol 中，击毁敌对舰队（会消耗敌方阵营的票据并给你的阵营记分）以及完成 NT 空间站任务会推进数字；当 NT 票据达到 `F.tickets_at_start + 700` 时目标完成。
证据：`tickets.dm:1-28`；`New()` 设置 `target = F.tickets + ticket_amount`（`tickets.dm:9-14`）；`check_completion()` 在 `F.tickets >= target` 时设 `status=1`（`tickets.dm:16-23`）。票据通过 `faction.gain_influence`/`lose_influence` 变化（`factions.dm:61-68`）；击杀对被击败舰队所属阵营调用 `faction?.lose_influence(reward)`（`ai-skynet.dm:340-342`）。
条件：`tickets/nt` 设置 `assigned_faction = FACTION_ID_NT`、`extension_supported = TRUE`（`tickets.dm:25-28`）；无后缀的 `tickets` 默认为 NT（`tickets.dm:9-11`）。
例外 / 覆写：**文档/代码不匹配**：`patrol.desc` 声称“Acquire 1000 victory tickets”（`patrol.dm:8`），但 `ticket_amount` 是 700，且没有任何东西覆写它（grep `ticket_amount` -> 只有 `tickets.dm:6,13`）。desc 字符串 `Acquire ticket_amount Tickets` 也是一个未填充的模板（`tickets.dm:3`）。`lose_influence` 也会给阵营之敌同等数量的分数（`factions.dm:63-65`），因此在预设敌对阵营之间票据波动是零和的。
置信度：HIGH（高）（数值），MEDIUM（中）（船员能否感知到累计总数——未发现专用 UI）

### MISSION-007 (destroy_fleets / "Destroy fleets")

断言：船员必须摧毁 `rand(1,3)` 支敌方舰队；主飞船对任何敌对舰队的击杀都计入。
面向玩家的后果：简报字面写着“Defeat N hostile fleets”；每次击杀发出一个信号以推进计数。
证据：`destroy_fleet.dm:7-32`。`instance()` 设置 `target = rand(minimum_fleets=1, maximum_fleets=3)`，在主 overmap 上注册 `COMSIG_SHIP_KILLED_FLEET`（`destroy_fleet.dm:14-19`）；`register_kill` 执行 `tally++`、重置提醒、检查完成（`destroy_fleet.dm:21-24`）；在 `tally >= target` 时完成（`destroy_fleet.dm:26-32`）。
条件：`target_faction = "any"` 默认；简报文本使用“hostile”（`destroy_fleet.dm:10,18`）。`COMSIG_SHIP_KILLED_FLEET` 仅在*玩家*飞船位于该星系时发送（`ai-skynet.dm:328-335`）。
例外 / 覆写：`extension_supported = FALSE`（不能作为延长目标添加）。
置信度：HIGH（高）

### MISSION-008 (perform_jumps)

断言：船员必须进行 `rand(6,10)` 次 FTL 跃迁；该目标是**每个**游戏模式的基线。
面向玩家的后果：简报中总有一个跃迁计数任务，它统计自该目标实例化以来的跃迁（tally 从 -1 开始，以不计入回合开始时的调动跃迁）。
证据：`perform_jumps.dm:1-30`；`tally = -1` 字段默认（`:5`）；`target = rand(6,10)`（`:13`）；注册 `COMSIG_SHIP_ARRIVED`（`:16`）；`register_jump` 的 `tally++`（`:18-21`）；在 `tally >= target` 时完成/注销（`:26-30`）。`COMSIG_SHIP_ARRIVED` 在 FTL 跃迁结束时发送（`nsv13/code/modules/overmap/FTL/ftl_jump.dm:335`）。基础游戏模式 `New()` 播种 `objectives = list(/datum/overmap_objective/perform_jumps)`（`overmap_mode.dm:416-419`）。
条件：统计成功的跃迁抵达，因此失败/误跳的跃迁可能不计入。
例外 / 覆写：`extension_supported` 未设置 -> 默认 FALSE，因此它*不*可作为随机抽取项；其唯一来源是基础游戏模式播种和 shakedown 随机池（`shakedown.dm:22-27`）。
置信度：HIGH（高）

### MISSION-009 (scan)

断言：船员必须用天体测量控制台扫描 `rand(1,5)` 个特定 overmap 异常类型。
面向玩家的后果：简报会指出异常类型；扫描它（同时产出研究点）计入目标，直到达到目标数量。
证据：`scan.dm:1-45`。`anomaly_whitelist`：安全太阳、红巨星太阳、虫洞、奇点（`scan.dm:8-12`）。`New()` 挑选一个已存在足够实例的类型并设置 `target = rand(1,5)`（`scan.dm:16-29`）；`instance()` 注册 `COMSIG_ANOMALY_SCANNED`（`scan.dm:31-35`）；`register_scan` 设置 `tally = count_by_type(main_overmap.scanned, anomaly_type)`（`scan.dm:37-40`）；在 `tally >= target` 时完成（`scan.dm:42-45`）。扫描信号从 `astrometrics.dm:137-142` 发送，后者还授予 `TECHWEB_POINT_TYPE_DISCOVERY` 点（`astrometrics.dm:143-149`）。
条件：在 `New()` 时需要有足够数量的该异常类型存在，否则 `status=1`（自动完成）+ 管理日志（`scan.dm:27-29`）。
例外 / 覆写：未发现。
置信度：HIGH（高）

### MISSION-010 (apnw_efficiency / "Test APNW")

断言：船员必须将飞船的装甲镀层纳米修复井（Armour Plating Nanorepair Well）的 `repair_efficiency` 提升到随机选定的 60-90%。
面向玩家的后果：船员为 APNW 机器分配电力，当修复效率越过阈值时目标完成；它通过 `process()` 持续重新检查。
证据：`apnw_efficiency.dm:1-34`。`New()` 在 `GLOB.machines` 中定位一个 `/obj/machinery/armour_plating_nanorepair_well`，设置 `target_efficiency = rand(60,90)/100`、STARTS_PROCESSING（`:8-16`）；当 `APNW.repair_efficiency >= target_efficiency` 时 `check_completion()` 完成（`:26-34`）。效率公式由分配的电力与材料修正驱动（`nsv13/code/modules/overmap/armour/nano_well.dm:125-128`）。
条件：不存在 APNW -> 自动完成 + 管理日志（`:11-13`）。
例外 / 覆写：`extension_supported` 默认 FALSE（仅在 shakedown 池中）。
置信度：HIGH（高）

### MISSION-011 (board_ship / "Capture syndicate vessel")

断言：船员必须找到一艘特定生成的辛迪加飞船，登船、击败船员，并将其应答器（IFF）改为 NT；当被登上飞船的阵营等于玩家的起始阵营时目标完成。
面向玩家的后果：简报会指出目标飞船与星系；该飞船无法被轻易摧毁（essential、block_deletion），且船员在完成前无法解除登船。
证据：`board_ship.dm:1-72`。`instance()` 从 `GLOB.boardable_ship_types`（`_globalvars/ships.dm:3`）生成一艘随机类型的飞船，标记其为 `block_deletion/essential`，注册 `COMSIG_SHIP_BOARDED` 和 `COMSIG_SHIP_RELEASE_BOARDING`，将其放置在一个无隐藏的第 2 星区非起始星系中且 alignment 匹配，生成一支辛迪加防御舰队，并依据旅行时间计算提醒间隔（`board_ship.dm:8-58`）。当 `target_ship.faction == starting_faction` 时 `check_completion()` 设 `status=1`，然后清除 flags/注销（`board_ship.dm:60-66`）。`release_boarding()` 除非完成/被覆写，否则返回 `COMSIG_SHIP_BLOCKS_RELEASE_BOARDING`（`board_ship.dm:68-72`）。
条件：IFF 更改在 `/obj/machinery/computer/iff_console` 处通过其 `hack()` proc 完成，该 proc 发送 `COMSIG_SHIP_BOARDED` 并翻转 `OM.faction` 辛迪加<->nanotrasen（`nsv13/code/game/machinery/iff_console.dm:124-160`）。若船员改为 IFF 骇入自己的主飞船，一支 Solgov 拦截舰队会被派去追杀他们（`iff_console.dm:138-156`）。
例外 / 覆写：玩家的 `starting_faction` 在登船模式中为 `nanotrasen`（`boarding_gamemode.dm:8-14`；唯一的 `fixed_objective` 是 board_ship）。
置信度：HIGH（高）

### MISSION-012 (system_defence_armada / Armada 模式)

断言：Armada 模式有一个固定目标：当辛迪加 armada 抵达时防守选定的 NT 星系。在 datum 中完成并非“击败 armada”；失败是丢失目标星系。
面向玩家的后果：倒计时提醒会为袭击倒数；在第 5 次提醒时 earthbuster armada 生成且 `armada_arrived` 翻转；若目标的 alignment 不再是 `nanotrasen`，则回合失败。
证据：`system_defence_armada.dm:1-26`；`instance()` 从 armada 模式复制 `selected_system`（`:8-16`）；`check_completion()` 在 `armada_arrived` 之前提前返回，然后若 `target.alignment != "nanotrasen"` 则调用 `defeat()`（`:18-26`）。Armada 模式（`armada.dm`）：挑选一个随机 NT 星系（排除返航星系），设置 3 分钟提醒间隔与倒计时字符串（`armada.dm:1-42`）；`consequence_four` 统计已完成的支线目标以排队 NT 增援舰队并生成一支先锋（`armada.dm:53-92`）；`consequence_five` 在目标上设 `armada_arrived=TRUE` 并生成 `/datum/fleet/earthbuster`（`armada.dm:66-74`）。
条件：此模式中不自动生成支线目标；`fixed_objectives` 只有防守目标（`armada.dm:23`），且“支线目标”仅在外部添加时才存在。
例外 / 覆写：`selection_weight = 0` 和 `required_players = 15` 通常会阻止随机抽取（`armada.dm:21-22`）。
置信度：HIGH（高）（结构），MEDIUM（中）（增援计数是否意在针对当前并未创建的自动生成支线目标）

### MISSION-013 (clear_system / "rubicon")

断言：清空一个指定的 overmap 星系中的所有敌人；`rubicon` 和 `dolos` 变体是 hardmode 固定目标，`rubicon` 也是首选的延长目标。
面向玩家的后果：hardmode 船员必须一路打到 Rubicon，再打到 Dolos；目标星系会被解除隐藏，以便船员能导航过去。
证据：`rubicon.dm:1-51`（该文件是 `/datum/overmap_objective/clear_system`）。`New()` 使用传入的星系名称或第一个有敌人的中立区星系（`:11-22`）；`instance()` 解除目标的隐藏并注册 `COMSIG_SHIP_KILLED_FLEET`（`:24-30`）；当 `target_system.enemies_in_system` 为空时 `check_completion()` 完成（`:32-37`）。变体：`clear_system/rubicon`（`system_name="Rubicon"`、`required_players=10`、`extension_supported=TRUE`）和 `clear_system/dolos`（`system_name="Dolos Remnants"`，在实例化时生成一支 remnant 舰队）（`:39-51`）。Hardmode 将两者用作 `fixed_objectives`（`hardmode.dm:14`）。
条件：`enemies_in_system` 必须真正被消耗——生成进该星系的舰队会跟踪它。
例外 / 覆写：当 `get_active_player_count > 10 && length(rubicon.enemies_in_system)` 时，`request_additional_objectives()` 添加 `clear_system/rubicon`，否则添加一个 `tickets` 目标以及升级的舰队（`overmap_mode.dm:344-352`）。
置信度：HIGH（高）

### MISSION-014 (cargo 目标基类 + 货运投递流程)

断言：货物目标（`/datum/overmap_objective/cargo`）要求船员用**精确内容**装载一枚**货运鱼雷**（`/obj/item/ship_weapon/ammunition/torpedo/freight`）并将其射向一个特定的 NT 空间站，空间站会校验内容并批准或退回。
面向玩家的后果：投递正确的物品且别无他物；多余的“垃圾”会导致拒收，货运会被寄回。完成一次货运会重置目标提醒。
证据：`cargo/_cargo.dm:1-191`。`instance()` 运行 `get_target()`、`pick_station()`、可选的 `roundstart_deliver_package()`、`update_brief()`、`update_freight_type_group()`（`:42-51`）。`pick_station()` 选择一个 NT 阵营的空间站（若 `pick_same_destination` 则优先已 `expecting_cargo` 的）并调用 `S.add_objective(src)`（`:61-84`）。投递校验：`check_cargo(shipment)` 将所有非黑名单内容收集进一个 `freight_type_check`，运行 `freight_type_group.check_contents()`，仅当 `group_status` 为 TRUE 且**没有未跟踪的余留物**时才完成（`tally=target; status=1`）（`:165-191`）。空间站侧：`receive_cargo` 构建一份 `freight_delivery_receipt` 并在延迟后调用 `check_objectives`（`ai-skynet.dm:455-484, 591-621`）；成功时 `approve_shipment` + `return_approved_form`（`ai-skynet.dm:575-580, 534-541`）；失败时 `reject_incomplete_shipment` / `reject_unexpected_shipment` 退回鱼雷（`ai-skynet.dm:543-573`）。`blacklisted_paperwork_itemtypes` 忽略货运鱼雷本身以及衣柜（`freight_type/single/_single.dm:1-7`）。
条件：在 `expecting_cargo` 期间空间站被标记为 `essential`/`nodamage`（`ai-skynet.dm:429-433`）。代码中注明硬性限制：一枚货运鱼雷约能容纳 4 个槽位，因此目标不应要求超过 4 种物品类型（`freight_type/group/_group.dm:8-9`）。
例外 / 覆写：`add_objective`（接收方）不向飞船投递任何东西；只有空间站使用它。注意代码注释 `_cargo.dm:26-27` 警告：在一个模式的 `random_objectives` 中混用 `pick_same_destination` TRUE/FALSE 会产生不一致的目的地。
置信度：HIGH（高）

### MISSION-015 (捐赠目标)

断言：捐赠变体要求船员自行*生产/获取*物品（没有预包装——`send_prepackaged_item` 默认 FALSE，因此 `allow_replacements` 默认 TRUE）并将其投递到 NT 空间站。
面向玩家的后果：这些是生产类工作（烹饪、化学、采矿、货物）而非快递类工作；任何有效实例都被接受。
证据 / 细节：
- `blood` —— 一个 `/datum/freight_type/single/reagent/blood`，血型从 `O-, O+, B-, B+, A-, A+, L` 中随机（ethereal/oozling 被注释掉），目标 = 200 单位（`donation/blood.dm:1-21`；试剂子类型 `freight_type/single/reagent/blood.dm` 目标 200，容器限制为血袋）。
- `chems` —— 挑选 1-3 个随机的 `medicine` 子类型试剂；构建一个“require all of {require any of [bottle, pill/patch]}”的组，每种药 `target = 90/len(chemicals)`（`donation/chems.dm:1-44`）。
- `food` —— 从一份精选列表中随机一种食物，目标 `rand(3,5)`（`donation/food.dm:1-53`）。
- `minerals` —— 随机一种矿物堆，目标 50 张；覆写 `pick_station()` 以避开矿物商人空间站（“Shallowstone”）（`donation/minerals.dm:1-45`；`object/mineral.dm` 目标 50）。
- `munitions` —— 随机一种弹药类型，目标 `rand(6,12)`（`donation/munitions.dm:1-17`）。
- `social_supplies` —— 一个蛋糕 + 200 单位随机乙醇 + 3 件物品，或包装成小投递包裹，或 3 件物品装在一个包装好的大板条箱中（`require any`）（`donation/social_supplies.dm:1-53`）。
条件：全部使用基础的 `check_cargo`/`check_contents` 校验。为打印的征用表单按类型设置 `crate_name`。
例外 / 覆写：`courier` 模式的 `random_objectives` = 所有捐赠 + 所有转移子类型（`courier.dm:23-24`）。
置信度：HIGH（高）

### MISSION-016 (转移目标 + 篡改/失败)

断言：转移变体预包装物品，并在回合开始时通过货物补给舱交给船员（或通过 `send_to_station_pickup_point` 放到空间站取货点）；船员将其运送到目的空间站。某些转移物品不可替换，若其包装被摧毁则标记为失败。
面向玩家的后果：船员收到一个密封板条箱；用撬棍打开它会弹出警告（并可能触发一次幽灵轮询的“sentient specimen”事件），但**本身不会**使目标失败——摧毁包装才会使其失败。对于不可替换的转移，无法替换该物品。
证据：转移的 `New()` proc 设置 `send_prepackaged_item=TRUE`，并且（对于 credits/data/documents/specimen/fighter_parts）设置 `allow_replacements=FALSE` 和 `C.overmap_objective = src`；其中若干还设置 `send_to_station_pickup_point=TRUE`：
- credits（`transfer/credits.dm:1-13`）：价值 `rand(3,15)*1000` 的全息芯片，取货点。
- data（`transfer/data.dm`）：一个 `/obj/item/disk/tech_disk`，取货点。
- documents（`transfer/documents.dm`）：`/obj/item/documents`，取货点。
- emergency_supplies（`transfer/emergency_supplies.dm`）：氧气罐、EVA 服、EVA 头盔、呼吸面罩各 5；预包装，**无**取货点（回合开始补给舱）。
- fighter_parts（`transfer/fighter_parts.dm`）：2-4 个随机战机组件；`allow_replacements` 本身是 `pick(TRUE, FALSE)` 并按代码应用；取货点。
- specimen（`transfer/specimen.dm`）：一个随机简单动物（危险或无害）；预包装，`allow_replacements=FALSE`，无取货点（回合开始补给舱）。
失败路径：当 `freight_type.allow_replacements == FALSE` 时，`/obj/structure/closet/crate/large/freight_objective/Destroy()` 设置 `overmap_objective.status = 2`（FAILED）（`nsv13/code/modules/cargo/objective_cargo.dm:32-40`）。用撬棍 `attackby` 只发送 `COMSIG_FREIGHT_TAMPERED`（警告提示 + 可能的幽灵轮询），而非失败（`objective_cargo.dm:20-31, 42-53`）。
条件：`deliver_package()` 创建 freight_objective 板条箱并 `MO.send_supplypod` 它；对于取货点目标，`pick_station_pickup_point()` 改为将板条箱注册为空间站上的 `holding_cargo`，船员通过商人菜单的 `receive_cargo` 动作取回它（`cargo/_cargo.dm:86-119`；`ai-skynet.dm:441-453`；`traders.dm:381-383`）。取走目标货物会调用 `update_reminder(objective=TRUE)`（`_cargo.dm:100`）。
例外 / 覆写：撬棍警告文本说打开“可能会将目标标记为失败”（`objective_cargo.dm:22`）——代码只在板条箱**被摧毁**时失败，因此该警告夸大了。若某个 freight_type 有 `send_prepackaged_item=TRUE` 但未设置 `overmap_objective`，Destroy() 的失败会被静默跳过（只有存在 `overmap_objective` 时才会失败）。
置信度：HIGH（高）（流程），MEDIUM（中）（“运送一个已摧毁板条箱的目标无处可逃”——仅在包装被摧毁时才有保证，而非仅当物品被取走时）

### MISSION-017 (任务奖励 / 货物 —— 死代码)

断言：`mission_rewards.dm`、`mission_cargos.dm`、`nsv_mission` 及其组件已弃用，在正常回合中不运行；它们仅为管理员/遗留而保留。
面向玩家的后果：在正常回合中无。活跃的目标系统不会发放“combat supply crate”和任务货箱。
证据：`nsv13/code/modules/cargo/mission_rewards.dm:1-11` 定义 `/obj/structure/closet/crate/nsv_mission_rewards`（每箱 2 个 PDC 弹匣 + 2 个轨道炮弹药 + 6 个 gauss）；`nsv13/code/modules/cargo/mission_cargos.dm:1-99` 定义 `/obj/structure/closet/crate/large/cargo` 变体；两者均在头部标注为已弃用（`missions.dm:1`、`components/missions.dm:1`、`mission_cargos.dm:1`）。唯一的活跃消费者（`nsv_mission/... payout`）不可达，因为商人 `mission` UI 分支被注释掉了（`traders.dm:385-419`）。
条件：`nsv_mission/payout()`（若曾被调用）会授予一个信用点全息芯片 + `ticket_bounty` 影响力 + 通过补给舱发放的奖励物品（`missions.dm:71-92`）。
例外 / 覆写：无——就游戏玩法而言视为不生效。
置信度：HIGH（高）

### MISSION-018 (呼叫计算机 / 任务提议流程)

断言：**不存在**基于呼叫的活跃任务提议流程。呼叫日志可在 NTOS 程序中查看，但它的任务列表只是一个桩。
面向玩家的后果：船员无法通过呼叫接受 overmap/空间站任务；所有活跃目标都来自游戏模式。呼叫飞船/空间站仅用于氛围/通讯。
证据：`nsv13/code/modules/ship_missions/hail_computer.dm:1-41`。`ship_hail_logger` 程序的 `prep_missions()` 在一个完全被注释掉的循环中遍历 `ship.missions` 并返回空列表（`hail_computer.dm:22-29`）；`ui_data()` 只报告 `ship_name` 和空的 `missions`（`:32-36`）。`ai-skynet.dm` 有一个通用的 `hail(text, ship_name, ...)` proc，用于闲聊（`ai-skynet.dm:623-635`）。
条件：无。
例外 / 覆写：对空间站货物发射器的 `try_hail`/hail 是投递通讯路径，而非任务提议（`ai-skynet.dm:395-397`）。
置信度：HIGH（高）

## 目标目录 (Objective Catalogue)

| 目标 | 船员做什么 | 完成检查 | 奖励 | 失败 |
|---|---|---|---|---|
| `tickets/tickets/nt` | 通过击毁敌对舰队 / 完成 NT 任务来赚取阵营（“victory”）票据 | `F.tickets >= F.tickets_start + 700`（`tickets.dm:9-23`） | 回合胜利；无信用点 | 无 |
| `destroy_fleets` | 摧毁 `rand(1,3)` 支敌对舰队 | 在 `COMSIG_SHIP_KILLED_FLEET` 时 `tally>=target`（`destroy_fleet.dm:21-32`） | 威胁 -50，进度 | 无 |
| `perform_jumps` | 进行 `rand(6,10)` 次 FTL 跃迁 | 在 `COMSIG_SHIP_ARRIVED` 时 `tally>=target`（`perform_jumps.dm:18-30`） | 进度 | 无 |
| `scan` | 用天体测量扫描 `rand(1,5)` 个指定异常类型 | 扫描信号时 `count(scanned)>=target`（`scan.dm:37-45`） | 进度 + 科技点（扫描本身） | 异常太少时自动完成（`scan.dm:27-29`） |
| `apnw_efficiency` | 将 APNW 的 `repair_efficiency` 提升到 `rand(60,90)%` | `process()` 直到 `efficiency>=target`（`apnw_efficiency.dm:26-34`） | 进度 | 无 APNW 时自动完成（`:11-13`） |
| `board_ship` | 登上指定的辛迪加飞船，击杀船员，将其 IFF 骇为 NT | `target_ship.faction == starting_faction`（`board_ship.dm:60-66`） | 进度 | 完成前无法解除登船；datum 层无失败 |
| `clear_system/rubicon` | 清空 Rubicon 中的所有敌人 | `enemies_in_system` 为空（`rubicon.dm:32-37`） | 进度 | 无 |
| `clear_system/dolos` | 清空 Dolos Remnants 中的所有敌人（生成 remnant 舰队） | 同上（`rubicon.dm:44-51`） | 进度 | 无 |
| `system_defence_armada` | 防守选定的 NT 星系对抗辛迪加 armada | 在 `armada_arrived` 之前返回，然后检查 alignment（`system_defence_armada.dm:18-26`） | 进度 / NT 增援 | 目标不再是 NT 则 **defeat** |
| `cargo/donation/blood` | 投递一包指定血型（200u） | `check_cargo` 精确内容，无垃圾（`blood.dm`；`_cargo.dm:165-191`） | 威胁 -50，进度 | 拒收（退回），并非失败 |
| `cargo/donation/chems` | 投递 1-3 种指定药物（合计约 90u 分摊）装入瓶子/药片 | 同上（`chems.dm`） | 进度 | 拒收 |
| `cargo/donation/food` | 投递 `rand(3,5)` 份指定食物 | 同上（`food.dm`） | 进度 | 拒收 |
| `cargo/donation/minerals` | 投递 50 张指定矿物 | 同上（`minerals.dm`） | 进度 | 拒收 |
| `cargo/donation/munitions` | 投递 `rand(6,12)` 件指定弹药 | 同上（`munitions.dm`） | 进度 | 拒收 |
| `cargo/donation/social_supplies` | 投递一个蛋糕 + 200u 乙醇 + 3 件包装好的包裹/板条箱 | 同上（`social_supplies.dm`） | 进度 | 拒收 |
| `cargo/transfer/credits` | 在空间站取走全息芯片，投递到另一个空间站 | 预包装匹配；`allow_replacements=FALSE`（`credits.dm`） | 进度 | 板条箱被摧毁 -> status FAILED（`objective_cargo.dm:32-40`） |
| `cargo/transfer/data` | 投递一个科技磁盘（预包装） | 同上（`data.dm`） | 进度 | 板条箱被摧毁 -> FAILED |
| `cargo/transfer/documents` | 投递机密文件（预包装） | 同上（`documents.dm`） | 进度 | 板条箱被摧毁 -> FAILED |
| `cargo/transfer/emergency_supplies` | 投递 5 份氧气罐/EVA 服/头盔/面罩 | 内容匹配（`emergency_supplies.dm`） | 进度 | 拒收 |
| `cargo/transfer/fighter_parts` | 投递 2-4 个战机组件 | 内容匹配；是否可替换随机（`fighter_parts.dm`） | 进度 | 仅当 `allow_replacements` 掷为 FALSE 且板条箱被摧毁时 FAILED |
| `cargo/transfer/specimen` | 投递一个活体样本（危险或无害） | 内容匹配（`specimen.dm`） | 进度 | 板条箱被摧毁（释放/杀死）则 FAILED |

奖励说明：**没有任何 overmap 目标直接支付信用点。** 任何目标的奖励是（i）向 `victory()` 推进的进度，以及（ii）每新完成一个目标一次性降低 50 点威胁（`overmap_mode.dm:476-478`）。已弃用的 `nsv_mission` 层是唯一曾支付信用点的东西（`missions.dm:71-92`）。

## 跨系统依赖 (Cross-System Dependencies)

- **游戏模式（`nsv13/code/game/gamemodes/overmap/*`）**：定义 `fixed_objectives`/`random_objectives`。patrol=tickets/nt；shakedown={perform_jumps, destroy_fleets, apnw_efficiency, scan} 中的 3 个随机；courier=所有捐赠+转移货物中的 3 个随机；boarding=board_ship；armada=system_defence_armada；hardmode=rubicon+dolos clear_system；galactic_conquest=tickets/nt（PvP）。每个模式还继承基础 `perform_jumps` 播种（`overmap_mode.dm:416-419`）。
- **`SSovermap_mode`（`controllers/subsystem/overmap_mode.dm`）**：拥有生命周期、提醒、威胁、胜利/失败、管理面板。“完成追踪”、“完成时降低威胁”和“提醒重置”实际就驻留于此。
- **阵营（`modules/overmap/factions.dm`）**：`tickets`、`gain_influence`/`lose_influence`；`tickets` 目标与提醒后果读写这些。舰队击杀会消耗被击败阵营的票据（`ai-skynet.dm:340-342`）。
- **舰队 / skynet（`modules/overmap/ai-skynet.dm`）**：发送 `COMSIG_SHIP_KILLED_FLEET`、击杀时的威胁，并承载整个货物投递校验（`check_objectives`、`receive_cargo`、approve/reject）以及 `expecting_cargo`/`holding_cargo` 空间站列表。
- **货运类型 datum（`nsv13/code/datums/freight_type/*`）**：编码逐物品校验（object / mineral / reagent / blood / chemistry / pill_patch / drinks / credits / specimen）以及 require-ALL/ANY/ONE 组逻辑（`group/_group.dm`）。
- **IFF 控制台（`game/machinery/iff_console.dm`）**：实现 board_ship 的“modify IFF codes”。
- **天体测量（`modules/research/astrometrics.dm`）**：实现 scan 目标的信号 + 科技点。
- **APNW（`modules/overmap/armour/nano_well.dm`）**：apnw 目标所测试的机器。
- **定义（`__DEFINES/missions.dm`）**：`MISSION_*`/`CARGO_*` 以及 `COMSIG_SHIP_*`、`COMSIG_ANOMALY_SCANNED` 信号。
- **通讯控制台（`game/machinery/computer/communications.dm:91,284,355`）**：为船员提供按需的 `check_completion()` 触发器。

## 开放问题 (Open Questions)

- 船员是否有任何 UI 能在回合中查看实时票据总数 / 目标完成情况，还是只有打印的简报？（未发现专用的目标 HUD；通讯控制台会触发检查，但未追踪其显示。）
- `system_defence_armada` 统计已完成的“支线目标”以触发 NT 增援，但 armada 模式默认不生成任何支线目标——是否存在一个支线目标来源，还是 `reinforcements >= 1` 实际上永远达不到？
- `social_supplies` 的调制饮品路径依赖在 New() 时实例化 `subtypesof(/datum/chemical_reaction)`；确认这不会在缺少 `id` 的配方上运行时出错。
- 当舰队撤退/占据而非被摧毁时，`clear_system` 是否真能保持 `enemies_in_system` 准确（即，若一支敌方舰队离开该星系，目标是否会软锁）？
- `armada` 的 `selection_weight = 0` 加上 `required_players` 门槛限制了随机选择，但它能通过配置 `omode_probability` 到达吗？（配置文件不在仓库中。）
- 对于 `allow_replacements=FALSE` 的转移目标，如果板条箱只是被打开（未被摧毁）但物品仍在，是否存在任何回合内的*修复*路径——即，船员能否重新装箱，还是唯一途径是原来的板条箱？
