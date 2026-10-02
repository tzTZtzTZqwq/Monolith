> 本文为 research/evidence/ftl.md 的中文翻译。

# FTL、跃迁与拦截 (FTL, Jump Travel & Interdiction)

## 系统概览 (System Overview)
FTL 在星图上的 `datum/star_system` 节点之间移动一艘 overmap 飞船（`/obj/structure/overmap`）。两套驱动器实现共存，并共用同一套调用接口（`ftl_drive.jump()`、`begin_jump()`、`get_jump_speed()`、`.progress`、`.ftl_startup_time`）：

- **现代 / 模块化“瑟林驱动器（Thirring Drive）”** —— `/obj/machinery/computer/ship/ftl_core`（文件 `components/drive.dm`）+ `/obj/machinery/atmospherics/components/binary/drive_pylon`（`components/drive_pylon.dm`）。燃料（Nucleium 气体）+ 电力双重门控，多部件。由较新的玩家船体使用（Aetherwhisp、Galactica2、Gladius2 —— 已在 `_maps` 中确认）。这是官方意图的系统。
- **旧版“Seegson”计算机** —— `/obj/machinery/computer/ship/ftl_computer`（`ftl_legacy.dm`）。自成一体，基于计时器蓄能。**仍是活跃代码**：被 28 个 `.dmm` 文件引用（采矿船、辛迪加登船模板、较旧的船体如 Galactica2_old、Eclipse、Atlas）。并非死代码——注释写道“保留在游戏中是为了兼容性以及更小的飞船”。

两者都调用 `ftl_jump.dm` 中的共享 proc（`begin_jump`、`jump_start`、`jump_end`）。还有一种用于战机的小型驱动器（`fighters/modules/ftl_drive.dm`、`/obj/item/fighter_component/ftl`），接口相同，但射程较短。

面向玩家的入口是**星图 / FTL 导航控制台**（`/obj/machinery/computer/ship/navigation`、`starmap.dm`）。驱动器机械本体（`ftl_core`）有自己的控制 TGUI，但 GUI 细节不在本文范围内。

## 核心玩法循环 (Core Gameplay Loop)
1. 工程师为驱动器供电/蓄能（旧版：在计算机上打开电源；模块化：激活 `ftl_core` 并启用 >=1 个 `drive_pylon`，供应 Nucleium + 电网电力）。
2. 驱动器充能至“READY”（`ftl_state == FTL_STATE_READY`）。
3. 导航员打开星图控制台，选择一个**相邻**星系，按下跃迁。
4. `ftl_drive.jump()` -> `OM.begin_jump()` 播放启动音效，等待 `ftl_startup_time`，然后 `jump_start()`。
5. `jump_start()` 检查拦截；若畅通，则将飞船移出该星系，设置一个旅行计时器（分钟），并将视差设为“transit”。
6. `jump_end()` 将飞船添加到目标星系的、依阵营而定的抵达点；若该星系带有 `STARSYSTEM_END_ON_ENTER`，则本轮可能结束。

## 机制 (Mechanics)

### FTL-001
断言：跃迁**并非瞬时**。有两个连续的时间开销：传送前的固定“启动”延迟，然后是依距离决定的在途（离图）时段。
面向玩家的后果：指挥官/导航会播报预计抵达时间；船员会在“跃迁空间”中度过数分钟，期间飞船从星系地图上消失。
证据：`begin_jump()` 播报 `ftl_drive.ftl_start`，然后 `addtimer(jump_start, ftl_drive.ftl_startup_time)`（`ftl_jump.dm:167-170`）。`ftl_core.ftl_startup_time = 32.3 SECONDS`（`drive.dm:40`）；旧版 tier1 `30 SECONDS`、tier2（slipstream）`6 SECONDS`、tier3（warp）`5 SECONDS`（`ftl_legacy.dm:34,73,83`）。`jump_start()` 计算 `speed = curr.dist(target) / (drive_speed * 10)` 分钟，设置 `to_time`，并 `addtimer(jump_end, speed MINUTES)`（`ftl_jump.dm:246-265`）。
条件：旅行分钟数随 LY 距离除以驱动速度而缩放。模块化的 `get_jump_speed()` `= jump_speed_factor * (jump_speed_pylon * active_pylons)` = `1.5 * active_pylons`（`drive.dm:411-412`）；旧版 `= jump_speed_factor`（基础 3.5，tier2 5，tier3 10）（`ftl_legacy.dm:18,75,86`）。战机驱动器系数 5.5（`fighters/modules/ftl_drive.dm:18`）。
例外 / 覆写：误跳（misjump）旅行时间有 `speed = max(speed, 3)` 分钟的下限（`ftl_jump.dm:250-251`）。`force_jump()` 路径（回合结束召回 / AI 管理）跳过充能要求，但仍使用正常的旅行计时器。
置信度：HIGH（高）

### FTL-002
断言：正常跃迁只能前往星图上**直接相邻**的星系（route graph），且驱动器必须完全蓄能到 READY。射程是次级门控，实际上几乎从不构成约束。
面向玩家的后果：你无法把跃迁规划到未连接的星系；地图只画出唯一有效的航线。
证据：`starmap.dm` ui_data：`can_jump = (dist < max_range && ftl_state == FTL_STATE_READY && LAZYFIND(current_system.adjacency_list, selected_system.name))`（`starmap.dm:222`）。`is_in_range()` 是纯相邻判定（`starmap.dm:233-234`）。默认 `max_range = 30000`（`drive.dm:37`、`ftl_legacy.dm:29`）；相邻线从 `adjacency_list` 绘制（`starmap.dm:170-201`）。
条件：`ftl_state` 必须为 `FTL_STATE_READY`（仅由 `ready_ftl()` 在 `progress >= req_charge` 时设置）（`drive.dm:161-162,370-374`）。
例外 / 覆写：残局返航跃迁绕过相邻判定——`return_jump_check()` 在 `SSovermap_mode.already_ended || round_extended` 时对 `/datum/star_system/outpost` 返回 TRUE（`starmap.dm:239-240`、`223-224`）。公共星图控制台（`navigation/public`、`can_control_ship = FALSE`）强制 `can_jump = FALSE`（`starmap.dm:32-34,225-227`）。`lockout`（返航跃迁）完全禁止跃迁（`starmap.dm:74-75`）。
置信度：HIGH（高）

### FTL-003
断言（现代驱动器）：蓄能需要**至少一个可运行、完全预热的驱动塔（pylon）**（默认 `min_pylons = 1`，最多 4 个链接的塔）。每个*活跃*塔都会增加充能；每 2 秒的机械 tick，每个活跃塔累积充能。塔必须由电缆供电并供给 Nucleium 气体。
面向玩家的后果：你可以只用一个塔蓄能，但很慢；更多活跃塔充能更快、跃迁更快，但会消耗多得多的燃料和电力。
证据：`ftl_core.spoolup()` 要求 `check_pylons()`，且仅在 `active_pylons >= min_pylons` 时才开始（`drive.dm:111-141`）。`process()` 对每个 `PYLON_STATE_ACTIVE` 塔向 `progress` 添加 `round(charge_rate * delta_time, 0.1)`，上限为 `req_charge`（`drive.dm:144-162`）。`charge_rate = 1`，`req_charge = 100`（`drive.dm:31,33`）。SSmachines 每 `2 SECONDS` 触发一次（`code/controllers/subsystem/machines.dm:5`），因此每塔每 tick 约 2 点充能 => 1 个塔约 100 秒，4 个塔约 25 秒。`get_pylons()` 链接 `MAX_PYLON_DISTANCE = 10` 格内、`link_id` 匹配的最多 4 个塔（`drive.dm:83-96,6`）。跃迁速度也随活跃塔数量缩放（`get_jump_speed`，见 FTL-001）。
条件：塔仅在 `capacitor >= req_capacitor`（5）之后才变为 ACTIVE（`drive_pylon.dm:69-70,35`），而这需要 Nucleium。
例外 / 覆写：`auto_spool_capable`（对 `ftl_core` 恒为 TRUE；开关 `auto_spool_enabled` 默认为 FALSE）使驱动器能在冷却后自行重新蓄能（`drive.dm:45-46,405-409`）。Tier-3“warp”芯片设置 `auto_spool_capable = TRUE`（本就是 true）和 `jump_speed_factor = 5`（`drive.dm:202-206`）。
置信度：HIGH（高）

### FTL-004
断言（塔的时序 + 燃料/电力）：塔的运行序列为 `offline -> starting -> warmup -> spooling -> active`。它从其输入管道消耗 Nucleium 气体，必须通过输出管道排出废等离子/热量，并抽取不断升级的电网电力。Nucleium 或电网盈余耗尽会导致塔停机。
面向玩家的后果：工程师必须把 Nucleium 接进来、把废热/等离子排出去，并提供巨大的电力余量（预热 20 kW、蓄能 50 kW，每塔；活跃抽取呈指数上升）。内部废料罐过压的塔会**爆炸**。
证据：`drive_pylon/process()` 中的状态机（`drive_pylon.dm:56-129`）：STARTING power_draw 5000 -> WARMUP 20000 消耗 0.25 mol/tick -> SPOOLING 50000 通过 `consume_fuel()` 消耗 -> ACTIVE。`consume_fuel()` 将最多 `max_charge_rate(1) * mol_per_capacitor(10)` 的 Nucleium 转化为最多 1 电容/tick（`drive_pylon.dm:231-249`）。`PYLON_ACTIVE_EXPONENT 1.01` 使活跃抽取每 tick 增长（`drive_pylon.dm:9-10,79`）。`power_drain()` 要求 `cable.surplus()`；超过 `POWER_FAIL_TOLERANCE(3)` 次错过的周期 => 停机（`drive_pylon.dm:4,131-151`）。废料：若 `air_contents` 压力 >= `MAX_WASTE_STORAGE_PRESSURE (8000)` kPa => `explosion(...,0,1,3)` + qdel（`drive_pylon.dm:182-185,3`）。Nucleium 不足 => 消息“Insufficient FTL fuel, spooling down.” + SHUTDOWN（`drive_pylon.dm:81-84`）。加护盾的塔（`toggle_shield`）降低电力抽取，但热量增益翻倍且消耗更多燃料（`drive_pylon.dm:76-77,87-88,242-243`）。
条件：`min_pylons`/`charge_rate`/`max_range` 等是逐船体变量；此处显示为默认值。
例外 / 覆写：`/drive_pylon/hugbox`（调试）自动供给输入并清除废料（`drive_pylon.dm:343-366`）。
置信度：HIGH（高）

### FTL-005
断言：已蓄能/正在充能的驱动器可以被取消，并且如果停止充能（例如塔被摧毁）就会失去充能。在任意跃迁或断电之后，驱动器会进入短暂冷却，之后才能再次充能。
面向玩家的后果：充能中途失去工程师/塔会中止跃迁（“FTL translation cancelled”）。
证据：`process()`：若没有塔在主动充能且 `progress > 0`，则 `progress = min(progress - 1, 0)`，且若在 READY 状态下掉到 `req_charge` 以下，则调用 `cancel_ftl()`（`drive.dm:157-160`）。`depower()` 设置 `cooldown = TRUE` 并 `addtimer(post_cooldown, FTL_COOLDOWN)`，`FTL_COOLDOWN = 260` ds = 26 秒（`drive.dm:3,384-402`）。`jump_start()` 在发射后调用 `ftl_drive.depower(auto_spool_enabled)`（`ftl_jump.dm:266`）。
条件：若处于 `cooldown`、已激活，或塔不足，`spoolup()` 会拒绝（`drive.dm:115-124`）。
例外 / 覆写：启用 `auto_spool_enabled` 时，`depower()` 不会关闭塔，且 `post_cooldown` 会自动再次调用 `spoolup()`（`drive.dm:394-409`）。
置信度：HIGH（高）

### FTL-006
断言：星图的“jump”动作经由 `ftl_drive.jump()` 路由。若飞船的 FTL 安全锁被解除，跃迁按钮会改为请求一次**紧急（误）跃迁**，必须在 15 秒内于驱动器上手动确认。否则即为正常跃迁。
面向玩家的后果：正常跃迁一键完成；紧急跃迁需要双钥匙授权才能启用、一次屏幕上的请求，然后在 FTL 核心处进行物理确认，且具有危险。
证据：`starmap.dm` 的 `if("jump")`：若 `linked.ftl_safety_override` 且 `next_emergency_jump` 已清除 -> `ftl_drive.request_emergency_ftl_confirm()`；否则 `ftl_drive.jump(selected_system)`（`starmap.dm:73-84`）。`request_emergency_ftl_confirm()` 设置 `await_emergency_ftl_confirm = world.time + 15 SECONDS` 并提示需要手动确认（`drive.dm:209-216`）。驱动器 TGUI 的 `"emergency_jump"` 动作要求 `await_emergency_ftl_confirm` 仍然有效、`progress/req_charge >= 0.25`、冷却已清除，且 `linked.ftl_safety_override` 为真，然后调用 `linked.emergency_jump()`（`drive.dm:308-323`）。
条件：`ftl_safety_override` 由一个**钥匙卡认证设备**事件（`toggle_ftl_drive_safety`、`KEYCARD_FTL_SAFETY_OVERRIDE`）切换，需要刷两张不同的 ID 卡（`code/modules/security_levels/keycard_authentication.dm:166-167,196-204,116-119`）。默认为 FALSE；只能通过该设备或管理 VV 设置（`overmap.dm:220`）。
例外 / 覆写：旧版与模块化驱动器实现完全相同的流程（`ftl_legacy.dm:218-233`、`drive.dm:308-323`）。
置信度：HIGH（高）

### FTL-007
断言：**紧急跃迁是一次蓄意的误跳**，会跳到同星区内 120 LY 以内的随机星系；它总是有害的（结构损伤、陨石、空间扭曲、幽灵船员），随后被锁定 35 分钟。
面向玩家的后果：它是逃生/自杀按钮，而非便利手段；船员应做好防冲击准备，并预料到损伤与残留的异常。
证据：`/obj/structure/overmap/proc/emergency_jump()` 在 `EMERGENCY_FTL_RANGE = 120` 内挑选一个随机的非隐藏同星区星系，设置 `next_emergency_jump = world.time + EMERGENCY_FTL_COOLDOWN (35 MINUTES)`，调用 `ftl_drive.jump(target, misjump = TRUE)`（`jump_mishap_helpers.dm:1-16`；`code/__DEFINES/overmap.dm:95-96`）。
条件：需要事先 25% 的蓄能以及安全锁解除（FTL-006）。
例外 / 覆写：`overmap.dm:221` 的注释声称“每 25 分钟一次”，但实际定义是 35 分钟（注释过时）。
置信度：HIGH（高）

### FTL-008
断言：误跳开始时飞船受到轻度结构损伤并开始扰动“相位态”；抵达时受到更重的损伤，可能向内部发射陨石，可能强制让一颗小行星撞入内部，并产生长寿命的时空扭曲与船员的幽灵拷贝。
面向玩家的后果：一次糟糕的跃迁会产生船体破口、来袭陨石、飞船内部类似传送器的时空裂隙、数分钟后出现隐形的“回声”船员，并可能通过在船员身上实化小行星地板将其压碎/绞碎（gib）。
证据：`on_misjump_start()`：若有内部，`take_damage(rand(5,10)% max_integrity)`，启动 `misjump_noise_handler`，在 rand(35,180) 秒后安排 `fry_phasestate()`，以及 `disjoint_phasestate()`（`jump_mishap_helpers.dm:19-27`）。`on_misjump_end()`：`structure_stress = prob(25) ? 10 : rand(15,75)`，按其占完整度的百分比造成损伤；`fun_meteors = pick(0,0,0,0,1,2,2,3,4)` 颗陨石；小行星碰撞几率 `= CLAMP(25 + 5*asteroids_in_system, 25, 90)`；`fry_phasestate()`（`jump_mishap_helpers.dm:30-48`）。`handle_meteor_launches()` 从前缘向有船员占据的区域抛射陨石（`jump_mishap_helpers.dm:128-149`）。`handle_asteroid_phase_collision()` 将一张小行星地图模板加载到飞船的 z 层；处于封闭地板上的船员会“被某种实化的固体压碎”并被**绞碎（gibbed）**（`jump_mishap_helpers.dm:209-232`）。`disjoint_phasestate()` 生成成对的时空扭曲传送地板，并为每名存活船员生成 `phase_ghost` 回声（`jump_mishap_helpers.dm:235-280`；`phase_ghost.dm`）。`fry_phasestate()` 随机交换/`ChangeTurf` 内部区块，永久破坏其结构（`jump_mishap_helpers.dm:288-327`）。
条件：仅在 `misjump = TRUE` 时触发（紧急跃迁 / 管理）。正常跃迁从不调用这些（`ftl_jump.dm:268-269,337-338`）。
例外 / 覆写：没有内部的飞船（`!occupying_levels || !linked_areas`，例如战机/AI overmap）改为开始时承受固定 `0.4 * max_integrity`、结束时承受 `0.5 * max_integrity`（`jump_mishap_helpers.dm:20-22,31-33`）。
置信度：HIGH（高）

### FTL-009
断言：误跳期间，一个后台处理器每 8 秒播放令人不安的船体吱嘎声，并以 1% 概率生成一个现实扭曲异常（stormdrive/sheer/bluespace），以及一艘隐形的、不可摧毁的微弱“幽灵船”（`ref_0x000000`），它会跟着本船。
面向玩家的后果：氛围/不祥的运行；跃迁期间偶尔会有异常与一个传感器幽灵出现在你的飞船里。
证据：`misjump_noise_handler/process()` 每 `8 SECONDS` 播放一段加权的吱嘎声，并且 `if(prob(1))` 挑选一个 `/obj/effect/anomaly/...` 并生成 `/obj/structure/overmap/ref_0x000000`；若 `structure_crit` 且 `prob(20)`，则生成一个爆炸前兆（`jump_mishap_helpers.dm:95-125`）。`ref_0x000000` 具有 `alpha = 0`、`invisibility = MAXIMUM`、`INDESTRUCTIBLE`、`sensor_profile = 255`（极其微弱），在飞船抵达时或 2-4 分钟后自毁（`jump_mishap_helpers.dm:152-201`）。
条件：仅在误跳期间存在（`jump_length - 20s`）。
例外 / 覆写：未发现。
置信度：HIGH（高）

### FTL-010
断言：`phase_ghost` 回声在创建时隐形，3-6 分钟后变成船员的半透明蓝色拷贝，再经过 13-20 分钟后删除。
面向玩家的后果：你自己的、延迟出现的诡异“幽灵”站在误跳开始时你所处的位置。
证据：`set_up()` 复制 icon/overlays 并在 rand(3,6) MINUTES 后安排 `appear()`；`appear()` 设置 `invisibility = 0`、`alpha = 100`、颜色 `#77abff`、`QDEL_IN rand(13,20) MINUTES`（`phase_ghost.dm:8-19`）。
置信度：HIGH（高）

### FTL-011
断言：拦截是 overmap 飞船上的一种组件，它**阻止不同阵营的飞船从同一星系跃迁离开**。唯一携带它的飞船是辛迪加的 `kadesh` 巡洋舰和 SolGov 的 `interdictor` 巡洋舰。
面向玩家的后果：你所在星系中的敌方拦截舰会封锁你的正常 FTL 跃迁；你会收到一条无线电警告，驱动器会重启。
证据：`/datum/component/interdiction` 在 `owner.current_system == interdicted.current_system` 且 `owner.faction != interdicted.faction` 时返回 `WEAK_INTERDICT`（`interdiction.dm:15-22`）。仅在 `syndicate.dm:508`（`/obj/structure/overmap/syndicate/ai/kadesh`）和 `solgov.dm:107`（`.../nanotrasen/solgov/ai/interdictor`）中添加。`jump_start()` 调用 `SEND_GLOBAL_SIGNAL(COMSIG_GLOB_CHECK_INTERDICT, src)`，在遭拦截时中止（无线电播报“Warning. Local energy anomaly detected... Performing emergency reboot.” + `ftl_drive.depower()`）（`ftl_jump.dm:228-233`）。
条件：拦截仅在 `jump_start` 触发，即玩家按下跃迁**之后**的 `ftl_startup_time`（约 32 秒）——跃迁按钮被启用，但传送失败。射程无关紧要；只取决于同星系 + 不同阵营。
例外 / 覆写：`kadesh.on_interdict()` 增加一个传感器轮廓惩罚（更难被探测）（`syndicate.dm:518-519`）。AI 舰队在遭拦截时也拒绝移动（跃迁）（`ai-skynet.dm:236-238`）。
置信度：HIGH（高）

### FTL-012
断言：拦截分为 WEAK 与 STRONG 两类；**实际只会产生 WEAK**。非紧急跃迁会被 WEAK 阻断，但紧急（误）跃迁**完全绕过 WEAK 拦截**。STRONG 会连紧急跃迁一并阻断，但没有任何代码路径返回它。
面向玩家的后果：若遭拦截且无力作战，解除 FTL 安全锁并紧急跃迁是一条可行（尽管有破坏性）的逃生路径——拦截舰无法扣住你。
证据：`if(!force && ((interdict_return & STRONG_INTERDICT) || ((interdict_return & WEAK_INTERDICT) && !misjump)))`（`ftl_jump.dm:229`）。定义：`WEAK_INTERDICT (1<<0)`“依赖加扰计算”，`STRONG_INTERDICT (1<<1)`“扰乱驱动场……任何平移都不可能”（`code/__DEFINES/nsv13.dm:79-80`）。Grep 显示该组件是唯一的返回者且总是返回 WEAK；没有任何产生者返回 STRONG。
条件：`force`/`misjump` 必须为 TRUE 才能绕过；`force` 在回合结束的管理/召回路径上被设置。
例外 / 覆写：未发现针对 STRONG 的。
置信度：HIGH（高）（仅 WEAK），MEDIUM（中）（STRONG 完全不可达——未发现产生者，但无法排除外部/管理 VV）

### FTL-013
断言：抵达位置取决于阵营：Nanotrasen/SolGov 同盟的飞船抵达目标星系的**左**半边，辛迪加/其他抵达**右**半边。若飞船没有阵营，则抵达任意位置。
面向玩家的后果：NT 玩家飞船总是落入新星系的西/左侧。
证据：`add_ship()`：`"nanotrasen" || "solgov"` -> `locate(rand(40, round(world.maxx/2)-10), ...)`（左）；否则 `locate(rand(round(world.maxx/2)+10, world.maxx-39), ...)`（右）；无阵营 -> 完全随机（`ftl_jump.dm:21-33`）。抵达在一个 `pick(orange(15, destination))` 地板上。
条件：仅在飞船没有显式 `target_turf` 且目标星系没有存储位置时适用。
例外 / 覆写：存储在星系 `contents_positions` 中的飞船会改为恢复到其缓存的 x/y（`restore_contents()`，`ftl_jump.dm:68-86`）。
置信度：HIGH（高）

### FTL-014
断言：事件“Now entering <system>”以及任何星系描述会在抵达时播报给全体船员，进入某些星系可以结束本轮。指定的**返航星系**是 `Outpost 45`，它带有 `STARSYSTEM_END_ON_ENTER`。
面向玩家的后果：抵达母港前哨站会以胜利结束本轮；其他星系只是打印其描述。
证据：`after_enter()` 发送 `COMSIG_STAR_SYSTEM_AFTER_ENTER`，播报名称/描述，且若 `system_traits & STARSYSTEM_END_ON_ENTER` 且 `role == MAIN_OVERMAP`，则宣告一条成功消息，设置 `GLOB.crew_transfer_risa = TRUE`、`SSticker.mode.check_finished()`、`news_report = SHIP_VICTORY`（`ftl_jump.dm:47-66`）。`/datum/star_system/outpost`（Outpost 45）带有 `STARSYSTEM_END_ON_ENTER`（`starsystem.dm:1093-1101`）。`return_system` 从地图配置 `SSmapping.config.return_system` 设置（`starsystem.dm:19,66`）。
条件：只有 `MAIN_OVERMAP`（玩家飞船）会触发结束。
例外 / 覆写：`hidden` 星系（如最初的 Outpost 45）在被揭示前不显示（`starmap.dm:140,154`）。
置信度：HIGH（高）

### FTL-015
断言：存在一个回合结束时的强制“返航跃迁”特例：游戏可以强制玩家飞船回家（直接、绕过相邻判定），并锁定后续跃迁。
面向玩家的后果：任务结束时你可能会被自动召回 Outpost 45；返航期间你不能跃迁到任何其他地方。
证据：`force_return_jump()`：设置 `SSovermap_mode.already_ended = TRUE`，设置 `ftl_drive.lockout = TRUE`，揭示返航星系，并调用 `ftl_drive.force_jump(target_system)`（通过 `force = TRUE` 绕过燃料/电力），然后在 `to_time + 35s` 后 `check_return_jump()`（`ftl_jump.dm:172-201`）。驱动器上的 `force_jump()` 设置 READY/use_power 0 并调用 `jump(..., force = TRUE)`（`drive.dm:363-368`、`ftl_legacy.dm:277-281`）。由 `overmap_mode.victory()` 在本轮被延长后调用（`overmap_mode.dm:517-526`），以及由自动召回提醒调用（`overmap_mode.dm:256-271`）。`lockout` 封锁星图的跃迁/取消按钮（“Invalid authkey...”）（`starmap.dm:74-75,86-88`）。
条件：若已在跃迁中，返航会推迟到 `COMSIG_SHIP_ARRIVED`（`ftl_jump.dm:178-182`）。
例外 / 覆写：未延长的回合会先进行“Press On Or Return Home?”投票（`overmap_mode.dm:519-521`）。
置信度：HIGH（高）

### FTL-016
断言：跃迁时，跃迁的飞船从其星系内容中移除，并移动到它自己保留的 Z 层（“treadmill”）；该星系剩余的 NPC 被暂存到零空间，从而中止那里的 overmap 战斗。船员仍留在飞船内部 Z 层中，视差设为“transit”。
面向玩家的后果：在途期间飞船脱离 overmap，既不能攻击也不能被攻击；船员只感到飞船猛然一顿（且晕船船员会感到恶心）。
证据：`jump_start()` 调用 `curr.remove_ship(src)`（`ftl_jump.dm:245`）。`remove_ship()` 将跃迁者移动到 `reserved_z`，且如果它是最后一艘玩家飞船，则缓存位置并对其余内容 `moveToNullspace()` 并 `STOP_PROCESSING` 其物理；设置 `occupying_z = 0`（`ftl_jump.dm:88-142`）。`force_parallax_update(TRUE)` 在 `reserved_z` + `occupying_levels` 上设置“transit”视差（`ftl_jump.dm:204-218,236-240`）。`jump_end()` 将跃迁者以及任何同 reserved_z 的飞船重新添加进目标星系（`ftl_jump.dm:298-339`）。
条件：`ftl_pull_small_craft()` 会把同阵营的、有玩家驾驶的小型飞船拖入“蓝空间尾迹”（`ftl_jump.dm:144-164`）。
例外 / 覆写：船员的震动使用 `jump_handle_shake()`；`TRAIT_SEASICK` 的碳基生物会根据最近的活动 `/obj/machinery/inertial_dampener` 调整其恶心度（`ftl_jump.dm:272-295`）。
置信度：HIGH（高）

### FTL-017
断言：跃迁时已处于严重结构状态（`structure_crit`）的飞船会在传送过程中被撕裂：爆炸前兆会在内部各处生成，其规模随处于严重状态的时间而放大。
面向玩家的后果：在瘫痪状态下勉强逃离，有在途中爆炸的风险。
证据：`jump_start()`：`if(structure_crit && !istype(.../small_craft))` 会在随机传送区域生成 `rand(4,8)` 个 `/obj/effect/temp_visual/explosion_telegraph`，`damage_amount = (world.time - structure_crit_init)/30`（`ftl_jump.dm:256-261`）。
置信度：HIGH（高）

### FTL-018
断言：一块超空间/虫洞 slipstream 芯片（`/obj/item/ftl_slipstream_chip`）可以插入任一驱动器以提升其 tier：tier 2 使最大射程翻倍并加快跃迁；还存在一个 tier-3“warp”/slipstream 变体。
面向玩家的后果：打捞/科研升级可以实质性地缩短蓄能与旅行时间；tier-3 warp 芯片是管理/稀有物品（设计上的 `ftl_slipstream_chip` 可由货物/科研制作）。
证据：若 `FI.tier > tier`，`attackby()` 会插入芯片，然后 `upgrade()`（`drive.dm:177-206`、`ftl_legacy.dm:41-88`）。`ftl_core` tier2：`max_range *= 2`、`jump_speed_factor = 2`；tier3：`jump_speed_factor = 5`、`max_range *= 3`（`drive.dm:195-206`）。旧版 tier2 增加 slipstream 音效 + `ftl_startup_time = 6s`，tier3 warp `auto_spool_enabled = TRUE`、`jump_speed_factor = 10`（`ftl_legacy.dm:64-88`）。`/obj/item/ftl_slipstream_chip/warp` 是 tier 3（`ftl_jump.dm:348-352`）；设计/科技树在 `ftl_jump.dm:354-371`。
条件：芯片仅在其 tier 严格高于驱动器当前 tier 时生效。
例外 / 覆写：无。
置信度：HIGH（高）

### FTL-019
断言：AI/辛迪加/SolGov 舰队通过一套独立的寻路 + 定时跃迁机制在星系间移动（在 `adjacency_list` 上做 Dijkstra 的 `find_route`，尊重虫洞、alignment 黑名单/白名单），而非玩家驱动器。
面向玩家的后果：敌方舰队会按计时器重新出现在相邻/友方星系；一支拦截舰队会专门向被猎杀的玩家飞船重新规划路线。
证据：`find_route()` 位于 `fleet_ftl_pathfinding.dm:13-90`。舰队按计时器移动并在每次移动后重新规划（`ai-skynet.dm:200-277`）。`/datum/fleet/interdiction/move()`/`New()` 设置 `hunted_ship = find_main_overmap()` 并重新计算朝向它的航线（`ai-skynet.dm:279-288`、`fleet_types.dm:127-149`）。`/datum/fleet/solgov/interdiction` 猎杀主 overmap（`fleet_types.dm:301-315`），在玩家的 IFF 被改为辛迪加时生成（`iff_console.dm:141-155`）。
条件：拦截舰队大多由管理/游戏模式生成；轻量变体是一个稀有的随机辛迪加舰队生成（`factions.dm:156`）。
例外 / 覆写：AI 的 `force_jump(where)` 绕过一切，通过 `jump_end()` 即时传送一支舰队飞船（`ai-skynet.dm:353-361`）。
置信度：HIGH（高）

### FTL-020
断言：旧版 `/obj/machinery/computer/ship/ftl_computer` 可用但被简化：它按固定计时器充能（`spoolup_time 45s`）消耗电力，然后是一个标准的 `ftl_startup_time`（30s）延迟；没有燃料气体，没有塔。
面向玩家的后果：较旧/较小的飞船按纯粹的时间/电力模型跃迁——不涉及工程燃料物流。
证据：`process()` 递增 `progress += progress_rate (1/s)` —— 实际是每个 delta 加 1 —— 直到 `spoolup_time`，然后 `ready_ftl()`；充能时 `use_power = 500`（`ftl_legacy.dm:154-167,24-26`）。`jump()` -> `begin_jump()` 使用 `ftl_startup_time`（`ftl_legacy.dm:265-274`）。`upgrade()` 的各 tier 缩短时间（见 FTL-018）。
条件：要求计算机可运行；失去电力时 `depower()`。
例外 / 覆写：`auto_spool_enabled`（仅 tier 3）在断电后自动重新开始充能（`ftl_legacy.dm:330-334`）。
置信度：HIGH（高）

### FTL-021
断言：战机小型飞船有一个独立的短程 FTL（`/obj/item/fighter_component/ftl`，“torch drive”，`max_range 50`），它也使用共享的 `begin_jump`，需要 `ftl_startup_time 6s`，自动蓄能，且在另一艘 overmap 与其位置重叠时无法跃迁。
面向玩家的后果：战机可以进行短途跳跃，但不能进行长途战略跃迁。
证据：`fighters/modules/ftl_drive.dm`：`max_range = 50`、`spoolup_time = 120`、`req_charge = 120`、`jump_speed_factor = 5.5`、`auto_spool_enabled = TRUE`（`:10-22`）；若 `linked.get_overmap()` 返回一个地板（“area not clear”），`jump()` 会拒绝（`:59-61`）。tier2“class III torch drive” `max_range = 200`（`:90-93`）。
条件：战机必须位于 overmap 的 z 层上并远离轨道天体。
例外 / 覆写：未发现。
置信度：HIGH（高）

## 跨系统依赖 (Cross-System Dependencies)
- **星图控制台**（`starmap.dm`）：唯一的玩家跃迁 UI；也显示在途 ETA（`time_left`）、`ftl_progress`/`ftl_goal`（通过一个权宜的类型转换同时处理现代 `req_charge` 和旧版 `spoolup_time`，`starmap.dm:105-112`）。
- **`SSstar_system`**（`starsystem.dm`）：拥有 `systems`、`ships[]` 状态字典（`current_system`/`target_system`/`last_system`/`to_time`/`from_time`）、`return_system`，以及 `add_ship`/`remove_ship`。
- **`SSovermap_mode`**（`overmap_mode.dm`）：`victory()`/`consequence_five()`/自动召回驱动 `force_return_jump()`；生成 `/datum/fleet/interdiction`。
- **任务（Missions）**（`code/datums/components/missions.dm`、`perform_jumps.dm` 目标）：订阅 `COMSIG_SHIP_ARRIVED`/`COMSIG_SHIP_DEPARTED`；一个“perform jumps”目标会统计抵达次数。
- **雷达/Dradis**（`radar.dm`）、**锁定（lock-on）**（`overmap.dm:485,660`）、**征服信标**（`pvp/items.dm:147`）、**PVP 自动蓄能模块**（`pvp/items.dm:270-280` 设置 `ftl_drive.auto_spool_enabled = TRUE`）都会对 `COMSIG_FTL_STATE_CHANGE` 作出反应。
- **电力/气体**：驱动塔与 `obj/structure/cable` 盈余以及大气 `airs[1]`（Nucleium 进）/ `airs[2]`（废料出）集成；GAS_NUCLEIUM。
- **晕船**：`TRAIT_SEASICK` + `/obj/machinery/inertial_dampener` 与跃迁震动交互。
- **回合结束**：`STARSYSTEM_END_ON_ENTER` + `GLOB.crew_transfer_risa` + `SSovermap_mode.already_ended`。

## 开放问题 (Open Questions)
1. `STRONG_INTERDICT` 真的无法通过任何已映射/GLOB 路径到达吗，还是某个组件/管理动词可以返回它？仓库内未发现产生者（搜索了所有 `.dm`）。未来的场域干扰器可能会加入它。
2. `charge_rate` 的确切时序：`process()` 使用 `delta_time`；确认 SSmachines 等待 = 2 秒，因此每塔每 tick 约 2 点充能，但未逐一枚举地图中逐船体的 `charge_rate`/`req_charge` 覆写。
3. 充能期间 `ftl_core` 的 `ftl_state` 值似乎停留在 `FTL_STATE_IDLE`（spoolup 不会设置 `FTL_STATE_SPOOLING`），而 `cancel_ftl` 会设置 SPOOLING——可能不一致，但对跃迁至关重要的门控（`READY`）是明确无歧义的。值得确认其意图。
4. `overmap.dm:221` 说紧急跃迁“每 25 分钟一次”，但 `EMERGENCY_FTL_COOLDOWN` = 35 分钟；确认预期值。
5. 在途期间飞船的 overmap 对象相对于其内部 Z 层（reserved_z 与 occupying_levels）究竟位于何处，以及是否有任何战斗子系统忽略 `system_contents`——追踪得足够说明在途是离图的，但边缘情况（登上在途中的飞船）未调查。
6. 在正常（未结束的）游戏中，`return_jump_check` 是否允许在隐藏前哨站被揭示之前跃迁到它——代码要求 `already_ended || round_extended`。
