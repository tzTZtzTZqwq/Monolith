> 本文为 research/evidence/ship_combat.md 的中文翻译。

# 舰船战斗 —— 武器、瞄准与开火

范围：舰船武器的开火/瞄准*规则*（武器 datum、开火流程、前置条件、选择、自主性、对 overmap 的伤害模型、瞄准/提前量）。实体武器机械的内部与弹药供给属于弹药（系统 10）—— 交叉引用，不重复。

源根目录：`nsv13/code/modules/overmap/weapons/`
（`delta_overmap_ship_weapons/` = "新武器 datum"系统），
`nsv13/code/modules/munitions/ship_weapons/`（实体机械），
`nsv13/code/modules/overmap/`（overmap 核心：physics、overmap.dm、ai-skynet.dm、aim_helper.dm）。

## 系统概述
舰船战斗围绕一个 **武器 datum**（`/datum/overmap_ship_weapon`，在代码/`OSW_` 标志中缩写为 OSW）组织。每个 OSW 是一个针对单件武器的*规则 + 数值*对象，且恰好链接到一艘 overmap 舰船。它决定：何种抛射物、点射规模、冷却、弹药类别、射击弧、谁可控制它、AI 行为以及音效。
实体火炮（`/obj/machinery/ship_weapon`）要么馈入一个 OSW（`linked_overmap_ship_weapon`），要么当 OSW 从舰船弹药池"非实体地"开火时被完全绕过（AI 舰船、无链接内部的舰船）。

玩家炮手通过战术控制台（`/obj/machinery/computer/ship/tactical`）开火，该控制台授予 `OVERMAP_USER_ROLE_GUNNER`；在 overmap 上的点击/拖拽路由至 `overmap.fire()` / `overmap.fire_weapon()`。AI 舰船通过 `ai_fire()` / `ai_elite_fire()` 以及自主武器处理来开火。

## 核心游戏循环
1. 舰船被建造/载入，`apply_weapons()` 创建/链接 OSW datum；实体武器在初始化后 15 秒链接自身（`_ship_weapon.dm get_ship`）。
2. 玩家：坐上战术控制台（→ 炮手角色），循环开火模式（Space / 滚轮 / 动词），可选地 Ctrl-点击来**标记/锁定**目标，然后 M1（或对光束瞄准火炮为拖拽直线 + 释放）开火。
3. `fire_proc_chain` 通过 `can_fire()` 进行校验（冷却、弹药、IFF、弧、AI 分析、实体/能量就绪）→ `fire()` 发射一次点射 → 设定冷却、短暂关闭隐身、震动屏幕。
4. 抛射物在舰船中心生成，沿某航向（目标方位、舰船朝向或舷侧）飞行，经由 physics2d 行进，并在重叠时调用 `overmap.bullet_act()` → 护盾 → 装甲象限 → 结构伤害；一份中继副本被发射到内部 z 层级。
5. AI/自主性：被标记自主的武器自行选取目标（AMS 模式 / flak / PDC），或 AI 发射射程惩罚最低的武器。

## 机制

### COMBAT-001 —— 武器 datum（`/datum/overmap_ship_weapon`）是开火规则的原子单元
断言：每件武器是一个 datum，携带抛射物类型、节奏、弧、控制标志、弹药类别与 AI 参数；它恰好链接到一艘 overmap，并以 `sort_priority`（高者在前）排序存储在 `overmap_weapon_datums` 中。
面向玩家的后果：炮手的武器列表顺序以及他们能看见/使用哪些武器，均源自这些 datum，而非实体火炮。
证据：
- `delta_overmap_ship_weapons/_overmap_ship_weapon.dm:12-128`（所有变量）、`:131-141` `New()`（初始化 `weapons["loaded"]/["all"]`，若给定舰船则链接）、`:176-197` `insert_into_overmap_weapons()` 按 `sort_priority` 降序排序。
- `:346-364` `recalc_role_weapon_lists()` 构建 `pilot_weapon_datums`、`gunner_weapon_datums`、`autonomous_weapon_datums`，以及 `pilotgunner_weapon_datums = gunner + pilot`。
- `:162-167` `link_weapon()` 若已链接或无目标则 `CRASH()`。
关键变量及其默认值：`standard_projectile_type`、`burst_size=1`、`fire_delay=0`、`burst_fire_delay=1`、`used_nonphysical_ammo=OSW_AMMO_HEAVY`、`min_ammo_per_physical_gun=1`、`weapon_control_flags=OSW_CONTROL_GUNNER|AI`、`weapon_facing_flags=OSW_FACING_OMNI`、`firing_arc=0`、`sort_priority=1`、`miss_chance=5`、`max_miss_distance=4`、`requires_physical_guns=TRUE`、`permitted_ams_modes=list("Anti-ship"=1,"Anti-missile countermeasures"=1)`。
条件：除非子类型覆写，否则默认值生效（`weapon_datum_types.dm`）。
置信度：HIGH（高）。

### COMBAT-002 —— 武器目录与子类型数值覆写
断言：`weapon_datum_types.dm` 定义具体武器；伤害/冷却来自*抛射物类型*（`projectiles_fx.dm`）加上 datum 节奏。有代表性（与玩家相关）的数值：
- `mac` "Naval Artillery"：抛射物 `mac_round`（400 伤害），点射 1，fire_delay 3.5 秒，最优 50，瞄准光束，AI 尺寸有效性仅 `mass > MASS_TINY`，`sort_priority` 12。`mac/dirty` 将抛射物换成 `mac_round/dirty`（150 伤害 + 淤泥/EMP 中继载荷）。
- `railgun`：`railgun_slug`（150），延迟 1.5 秒，最优 20，**前向弧 45°**，`OSW_ALWAYS_FIRES_FORWARD`，控制 PILOT|AI。不适用于 `MASS_TINY`。
- `torpedo_launcher`：制导 `torpedo`，延迟 0.5 秒，`OSW_AMMO_TORPEDO`，仅前向，`has_special_action`（循环弹药过滤器），尺寸有效性 `mass >= MASS_SMALL`。
- `aa_guns`：`aa_round`（40），点射 4，延迟 0.6 秒，最优 15，`miss_chance 33`。
- `burst_phaser`：`laser/phaser`（30），延迟 0.5 秒，轻型弹药，仅前向，PILOT|AI。
- `phaser`：`heavylaser/phaser`（200，**hitscan**），延迟 1.5 秒，最优 60，瞄准光束。
- `phaser_pd`：`phaser/pd`（60），点射 4（burst_delay 0.25 秒），延迟 1.5 秒。
- `bsa`：`heavylaser/bsa`，延迟 20 秒，前向弧 45°，`OSW_AMMO_FREE`。
- `missile_launcher`：制导 `missile`，延迟 0.5 秒，`OSW_AMMO_MISSILE`，仅前向，尺寸有效性**仅 `mass <= MASS_SMALL`**。
- `light_cannon`/`heavy_cannon`：40/30 伤害，点射 2，延迟 0.25/0.5 秒，前向弧 20°。
- `gauss`：`gauss_slug`（80），点射 2，延迟 3 秒，`OSW_CONTROL_MANUAL|AI`，轻型。
- `pdc_mount`：`pdc_round`（15），点射 3，延迟 0.25 秒，最优 10，完整自主性集。
- `flak`：`flak`，延迟 0.5 秒，MAX 自主性标志，`max_ai_range 30`。
- `broadside`：`broadside`（125），点射 5，延迟 0.5 秒，`OSW_AMMO_HEAVY`，**侧向弧 30°**，`OSW_ALWAYS_FIRES_BROADSIDES`，`OSW_SIDE_AIMING_BEAM`。
- `vls`：制导导弹，延迟 0.35 秒，`OSW_AMMO_MISSILE`，AUTONOMOUS|GUNNER|AI。
- `laser_ams`：`laser/point_defense`（30，hitscan），延迟 0.35 秒，仅 AUTONOMOUS，`permitted_ams_modes = list("Anti-missile countermeasures"=1)`。
- `plasma_caster`：延迟 5 秒，`ai_fire_delay 180 秒`，`max_ai_range 25000`，GUNNER|AI。
- 仅 AI：`twinmac`、`quadgauss`、`hailstorm`（点射 80，延迟 20 秒）、`prototype_bsa`。
证据：`weapon_datum_types.dm:4-548`；抛射物数值在 `projectiles_fx.dm`。
条件：`used_nonphysical_ammo` 在未设定时默认为 `OSW_AMMO_HEAVY`（例如 `mac`、`phaser`），因此这些武器消耗重型弹药池。`burst_fire_delay` 除非覆写否则默认为 1 分秒。
置信度：HIGH（高）（机械数值）；其平衡意义属于解读。

### COMBAT-003 —— 开火的入口点：玩家点击、瞄准释放、AI
断言：玩家炮手通过 M1 点击目标（非瞄准武器）或在 overmap 上按住-释放（瞄准光束武器）来开火。驾驶员也可开火。AI 调用不同的路径。
证据：
- `overmap.dm:586-623` `InterceptClickOn()`：若 `user.incapacitated()` 则拒绝；若 `weapon_safety && !can_friendly_fire()` 则提前返回（默认 `can_friendly_fire` = FALSE → 保险开启会阻断所有点击，`overmap.dm:626-627`）。Ctrl-点击 → `start_lockon`。否则 `fire(target, user)`。
- `aim_helper.dm:42-89` `onMouseDown/onMouseUp`：对具有 `OSW_AIMING_BEAM`/`OSW_SIDE_AIMING_BEAM` 的武器，绘制一条激光，并在释放时调用 `fire_weapon(object, M, aimed_weapon)`；其他武器设定 `autofire_target`（移动时自动开火，`physics.dm:429-435`）。
- `weapons.dm:11-44` `overmap/fire()`：保险联锁检查，从 `controlled_weapon_datum[user]` 解析开火武器，AI 分支 → `ai_fire`/`ai_elite_fire`。
- `weapons.dm:46-69` `fire_weapon()`：再次保险检查，构建每发的 `report_list` 以便将失败显示给炮手（通过 `next_error_report` 限速 0.5 秒），然后调用 `firing_weapon.fire_proc_chain(...)`。
条件：点击要求该生物为 `gunner`（或 `pilot`）；`user.GetAllContents()` 以及 alt/shift 点击被忽略。Gauss 炮手优先（`overmap.dm:596-610`）。
置信度：HIGH（高）。

### COMBAT-004 —— 核心开火链：校验 → 点射 → 冷却
断言：`fire_proc_chain` 是单一的瓶颈：它重新检查 `can_fire()`，发射一次点射，然后设定 `next_firetime = world.time + fire_delay`，闪烁隐身，震动屏幕，并在 AI 瞄准时加上 `ai_fire_delay`。
证据：`firing.dm:8-24`。
- `SHOULD_NOT_SLEEP(TRUE)` —— 它不得 sleep；异步工作被推迟。
- 返回实际发射的射击次数；`fire()` 分流为实体与非实体。
- `linked_overmap.handle_cloak(CLOAK_TEMPORARY_LOSS)`（`radar.dm:356-370`）：解除舰船隐身 15 秒以便其开火。
- `screen_shake` 震动内部生物；`shake_everyone`（`damage.dm:15-18`）。
面向玩家的后果：发射一件 3.5 秒冷却的 MAC 会阻断其接下来所有射击达 3.5 秒；AI 武器在此之上再叠加额外的 `ai_fire_delay`（例如 MAC AI 2 秒 → 5.5 秒）。
条件：`fire_delay` 与 `ai_fire_delay` 是逐 datum 的，且逐武器覆写。
置信度：HIGH（高）。

### COMBAT-005 —— 实体与非实体开火：`needs_real_weapons()`
断言：一件武器仅当它 `requires_physical_guns`、舰船非 AI 控制、且舰船有链接的内部区域时才使用真机械。否则它从抽象弹药池"非实体地"开火。
证据：`firing_checks.dm:7-8`
`needs_real_weapons() = requires_physical_guns && !linked_overmap.ai_controlled && length(linked_overmap.linked_areas)`。
后果：AI 舰船与无内部的道具（小行星、幽灵船）始终从抽象池开火；一艘有实体火炮且配员的舰船使用进弹/装膛循环。战斗机覆写 `requires_physical_guns=FALSE` 并改为经由挂载点路由（`weapon_datum_types.dm:281-303`、`_fighters.dm:1637-1660`）。
置信度：HIGH（高）。

### COMBAT-006 —— 实体开火按每门炮消耗已装膛的弹药，至多一次点射
断言：`fire_physical` 遍历 `weapons["loaded"]` 列表，跳过无法开火的火炮，并对每门炮发射 `min(burst_size, gun_ammo)`；第一发立即发射，之后的射击通过计时器按 `burst_fire_delay` 铺开，以呈现"一致点射"的观感。
证据：`firing.dm:38-53`。`minimum_ammo_per_physical_gun`（默认 1，broadside 5）是一门炮参与所需的弹药阈值（`can_fire(target, minimum...)`）。弹药过滤器（`ammo_filter`）跳过缺少该确切弹药类型的火炮（`firing_checks.dm:166-172`）。
条件：`burst_size` 总数在该 datum 的所有已装填火炮间共享；返回的总射击数 = `burst_size - remaining`。
置信度：HIGH（高）。

### COMBAT-007 —— 非实体开火消耗抽象的舰船弹药池
断言：非实体武器从由 `used_nonphysical_ammo` 选择的舰船级计数器扣除：LIGHT→`light_shots_left`，HEAVY→`shots_left`，MISSILE→`missiles`，TORPEDO→`torpedoes`，FREE→无限。
证据：`firing.dm:58-72`（点射 = `min(burst_size, get_ammo())`，若 `nonphysical_fire_single_ammo_use` 则为完整 `burst_size` 且使用 1 发弹药）、`firing.dm:110-124` `use_nonphysical_ammo()`、`firing_checks.dm:13-45` `get_ai_ammo_by_current_weapon_class`。
后果：broadside 每次完整点射仅用一发弹药（`nonphysical_fire_single_ammo_use=TRUE`）；否则一次正常的 5 发 broadside 会消耗 5 发。
置信度：HIGH（高）。

### COMBAT-008 —— `can_fire()` 前置条件阶梯（门控）
断言：一件武器仅当以下所有条件均满足时才能开火：
1. `next_firetime <= world.time`（不在冷却 —— 无消息，静默）。
2. 目标未 `QDELETED`。
3. `get_ammo() > 0`（实体弹药 / 能量充能 / 抽象池）。
4. 若目标是 overmap，其 `faction != linked_overmap.faction`（IFF；友军被阻断，消息 "Target IFF friendly"）。
5. `check_valid_fire_angle(target)` 通过（弧）。
6. 若舰船为 AI 控制且目标是 overmap，`is_valid_ai_target(target)` 通过（"did not pass analysis"）。
7. 实体：`can_fire_physical`（`weapons["loaded"]` 中 ≥1 门炮通过）。非实体：始终 TRUE。
证据：`firing_checks.dm:101-132` `can_fire`；`:183-184` `is_valid_ai_target` = `!QDELETED && is_target_size_valid && !warcrime_blacklist[type] && !target.essential && overmap_dist <= max_ai_range`。
后果：炮手仅对条件 3-7 看到具体的失败字符串（冷却被刻意设为静默）。
置信度：HIGH（高）。

### COMBAT-009 —— 射击弧是针对舰船朝向的角度测试
断言：受限于 FRONT/SIDES/BACK 的武器仅在距相关轴 `firing_arc` 度内开火（弧被翻倍 —— 对称施加）。`OSW_FACING_OMNI`（武器默认）从不限制。
证据：`facing_checks.dm:7-30`：规范化 `our_angle`，`angle_diff = (target_angle - our_angle) mod 360`。
- FRONT 若 `diff >= 360-firing_arc || diff <= firing_arc` 则通过。
- SIDES 若 `|270-diff| <= arc || |90-diff| <= arc` 则通过。
- BACK 若 `|180-diff| <= arc` 则通过。
`check_valid_fire_angle`（`firing_checks.dm:138-143`）在 OMNI 时短路返回 TRUE，否则要求匹配的标志测试。`overmap_angle` 使用舰船*中心*。
后果：一门 railgun（前向 45°）实际拥有 90° 前向锥；broadside（侧向 30°）每舷 60° 锥。任一匹配的标志即放行射击。
置信度：HIGH（高）。

### COMBAT-010 —— 实体火炮就绪规则（交叉引用弹药）
断言：一门实体火炮仅当：状态 ≥ `STATE_CHAMBERED` 且有 `chambered` 弹、`maint_state <= MSTATE_UNSCREWED`、不在 `STATE_FIRING`、未 `malfunction`、本地 `safety` 关闭、且弹药 ≥ 请求射击数时才能开火。侧向火炮还要求火炮的地图 `dir` 与相对方位匹配。
证据：`_ship_weapon.dm:468-484` `can_fire`。侧向检查：`dir == angle2dir_ship(overmap_angle(linked, target) - linked.angle)`。
支撑状态机与弹药供给（系统 10，仅交叉引用）：`_ship_weapon.dm` `load/feed/chamber/fire`，定义在 `__DEFINES/weapons.dm`（`STATE_*`）。开火消耗 `chambered`，减少弹药，若 `semi_auto` 可能重新装膛，并倒计时 `maint_req` → 低于 0 时 `weapon_malfunction()`（`_ship_weapon.dm:495-587`）。
置信度：HIGH（高）（规则）；实体*操作*细节属于系统 10。

### COMBAT-011 —— 能量武器以储存电荷为门控，按功率设定缩放
断言：能量武器没有实体弹药；`get_ammo() = round(charge / charge_per_shot)`，且当 `charge/charge_per_shot >= shots` 时可开火。电荷仅在武器 `active`、通电且未满时通过 `process()` 从线缆电网拉取 `charge_rate` 而积累。
证据：`energy_weapons/phaser.dm:99-151`。
- `charge_rate = initial(charge_rate) * power_modifier`；`max_charge = initial * power_modifier`；`charge_per_shot = max(initial * power_modifier, 10)`（不能通过把功率设为 0 来白嫖射击）。
- 功率消耗：`try_use_power(charge_rate)` 要求电网有可用余量。
- 伤害随 `power_modifier` 缩放：`animate_projectile` 执行 `P.damage *= power_modifier`，除非 `static_charge`（`phaser.dm:113-116`）。
- Phasers：`power_modifier_cap` 3（burst phaser）/ 5（phase cannon）。Burst phaser 储存 5 发；phase cannon 2 发。
后果：超频一门 phaser 会同时放大伤害与每发能量消耗；一门未通电/离线的火炮根本不会充能（弹药耗尽）。
置信度：HIGH（高）。

### COMBAT-012 —— 武器选择与控制需要角色；选择会抑制自主性
断言：一个生物可循环哪些武器取决于其角色：炮手看见炮手列表，驾驶员看见驾驶员列表，同时持有两者的生物看见并集（`pilotgunner`）。选择武器使其 `controller_count` 递增；取消选择则递减。任何 `controller_count > 0` 使自主性跳过该武器。
证据：`selection.dm:6-16` `mob_weapon_datum_list`；`:92-112` `swap_to` / `on_swap_to`/`on_swap_from`（`controller_count++/--`）；`autonomy.dm:210-213`（`if(osw.controller_count > 0) continue`）。
`select_weapon(n)` 索引角色列表；`increment_selected_weapon` 循环（`selection.dm:38-86`）。`adjust_priority`（Tactical）重新排序并清除所有选择。
后果：炮手主动持有一件 PDC/flak 武器会禁用该武器的自动开火；放开它（停止操控）则重新启用。
置信度：HIGH（高）。

### COMBAT-013 —— 战术控制台 = 炮手入口点（规则，非 UI）
断言：战术控制台分配/使用 `OVERMAP_USER_ROLE_GUNNER` 位置，是炮手的控制界面。其与规则相关的动作：切换炮相机（要求 `target_lock` 在扫描范围内）、锁定/抛弃被标记的目标，以及修改逐武器的 `sort_priority`（钳制 1..900）。
证据：`tactical.dm:18-48`（角色分配；当 `ai_controlled` 时拒绝 "Automated flight protocols are still active"；告知炮手 M1 开火、Space 循环、F = 特殊动作）、`:64-105`（toggle_gun_camera 由 `dradis.visual_range` 或 `SENSOR_RANGE_DEFAULT` 范围门控；`change_weapon_priority` 钳制 1..900 → `adjust_priority`）。
条件：除非用户在 `linked.operators` 中，否则控制台 `ui_status` 关闭。
置信度：HIGH（高）（行为）；UI 呈现超出范围。

### COMBAT-014 —— 目标标记/锁定：要求与存续
断言：炮手 Ctrl-点击在一个锁定延迟后标记目标；标记要求一个 IFF 敌人且其传感器可见性足够；锁定受 `max_paints` 限制，并在目标离开传感器范围时过期。被锁定的目标是制导武器/AMS 追踪的对象。
证据：`overmap.dm:629-663` `start_lockon`（拒绝同阵营除非 `can_friendly_fire`；计时器 `lockon_time` → `finish_lockon`），`finish_lockon` 强制执行 `is_sensor_visible(src) >= SENSOR_VISIBILITY_TARGETABLE`（=0.70），除非有数据链，且在 `max_paints` 时丢弃最旧的标记。
`overmap.dm:665-676` `select_target` 设定 `target_lock`；在 qdel/FTL 时 `dump_lock`。
`physics.dm:437-447`：当 `target_last_tracked + target_loss_time < world.time` 时失配的标记被抛弃。数据链可传输友军的锁定（`overmap.dm:695-707`）。
后果：隐身/低信号舰船（`is_sensor_visible < 0.70`）即使能在 DRADIS 上看见也无法被炮手标记。
置信度：HIGH（高）。

### COMBAT-015 —— 瞄准/精度：玩家射击是确定性的，AI 使用二次提前量
断言：玩家发射的射击不是命中几率 —— 它们飞向瞄准方位（或对仅前向武器飞向舰船朝向）。AI 发射的、非制导、非 hitscan 的射击通过求解目标位置/速度的拦截二次方程来修正提前量。逐武器的 `miss_chance`/`max_miss_distance` 被贯穿传入但**未被消费** —— 目前对 AI 瞄准而言是死的（未施加随机失准）。
证据：
- `physics.dm:668-669` 仅 AI 瞄准的射击调用 intercept：`if(ai_aim && !proj.can_home && !proj.hitscan) target = calculate_intercept(target, proj, miss_chance=..., max_miss_distance=...)`。
- `ai-skynet.dm:1445-1481` `calculate_intercept`：求解 `a*t^2+b*t+c=0`，其中 `projectilespeed = 32 / P.speed`，返回预测的 turf。**`miss_chance` 与 `max_miss_distance` 在函数体内未使用**（已验证：唯一的其他引用是定义与转发调用）。
- 玩家方向：`physics.dm:715-742` `preparePixelProjectileOvermap` 依据开火标志（前向/舷侧）或 `overmap_angle(curloc, targloc)` 设定角度。
- 残余不精确来自逐抛射物的 `spread`（例如 broadside 15、pdc 5、aa 5），由抛射物引擎施加，而非 `miss_chance`。
后果：熟练的炮手手动打提前量；AI 实际上始终正确打提前量（目前没有随机性，尽管变量名/注释如此暗示）。
置信度：调用/使用为 HIGH（高）；`miss_chance` 是否*有意*为死代码为 MEDIUM-LOW（中低）（可能是潜在 bug）—— 标记以供审查。

### COMBAT-016 —— 抛射物生成、航向与命中检测
断言：发射的抛射物在射击者中心生成，被赋予一个角度，在 physics2d 四叉树下行进，并在碰撞体重叠时调用 `Bump → can_hit_target → Impact → target.bullet_act`。制导抛射物获得一个 `homing_target`。
证据：`physics.dm:659-711` `fire_projectile`（在 `get_center()` 生成 proj，设定射击者、阵营、`original = target`；若 `can_home` → 从 target 或 `target_painted`/`target_lock` 调用 `set_homing_target`，然后 `target.on_missile_lock(src, proj)`）。
碰撞：`physics2d.dm:39-57` 四叉树开火循环在碰撞体重叠时调用 `holder.Bump(neighbour)`；`projectiles/projectile.dm:327-331` `Bump` → `can_hit_target` → `Impact` → `process_hit` → `target.bullet_act(src, ...)`（`projectile.dm:437`）。
`physics.dm:715-742` 设定开火角度；穿透行为来自 `projectile_piercing`/`spec_overmap_hit`。
后果："未命中"的抛射物只是撞上别的东西或按射程过期；命中时没有精度掷骰。
置信度：HIGH（高）。

### COMBAT-017 —— Overmap 伤害模型：护盾 → 象限 → 结构
断言：对一次 overmap 命中，顺序为：(a) 瞄准光束被忽略；(b) 若舰船有护盾，运行 `shields.absorb_hit` —— 完全吸收会消耗护盾完整度并停止该次命中，偏转/反射会重新瞄准抛射物并让其穿透；(c) 否则运行 `spec_overmap_hit`（特殊效果：hullburn/EMP/disruption）；(d) 该次命中被中继到内部 z 层级；(e) 若有 `use_armour_quadrants`，伤害命中单个象限（溢出 → 结构），否则直接结构伤害。
证据：`damage.dm:20-60` `bullet_act`；`:67-96` `relay_damage`；象限路由 `armour_quadrant.dm:41-51`（`projectile_quadrant_impact` 按角度）与 `:66-89` `take_quadrant_hit`（`delta = damage - current_armour`；≤0 = 完全吸收 + "ding"；>0 = `take_damage(delta)` 上层结构）。`use_armour_quadrants` 对 `mass > MASS_TINY` 的非采矿舰船自动启用（`overmap.dm:395-399`），初始象限装甲 = `armour_efficiency`。
伤害类型：抛射物携带 `flag = "overmap_light"/"medium"/"heavy"`，舰船携带 `armor = list("overmap_light"=..,"overmap_medium"=..,"overmap_heavy"=..)`；`run_obj_armor(P.damage, P.damage_type, P.flag, ...)` 施加它（`damage.dm:59`）。
条件：护盾仅在 `shield["integrity"] >= damage` 时完全吸收，否则该次命中整体穿透（`shieldgen.dm:436-443`）。战斗机跳过内部中继（`damage.dm:98-99`）。
置信度：HIGH（高）（路由）；抗性量级因舰船子类型而异（见各类型文件）。

### COMBAT-018 —— 结构临界：玩家舰船无法直接死亡
断言：主力/有人舰船进入致命伤害时进入 `structure_crit` 而非死亡；约 15 分钟倒计时会触发公告，然后是电影式引爆与回合结束。修复至 ≥20% 最大完整度可清除临界。
证据：`damage.dm:101-116` `take_damage`（当 `DAMAGE_STARTS_COUNTDOWN` 特性匹配时的临界钩子）、`:146-217` `handle_crit` / `handle_critical_failure_part_1`（时间线 0 → 15 分钟）、`:221-257` `handle_critical_failure_part_2`（爆炸序列、`SSticker.force_ending`）。`try_repair`（`:263-277`）在 20% 时清除临界（超过 10 分钟后不可逆）。
置信度：HIGH（高）。

### COMBAT-019 —— 自主性：AMS 模式 + 完全自主武器
断言：被标记为 `OSW_CONTROL_AUTONOMOUS` 的武器由 `handle_autonomous_targeting` 驱动：完全自主的武器运行自己的处理器；其他武器由 AMS 模式发射。
证据：`autonomy.dm:204-243`。
- 循环 1：具有 `FULL_AUTONOMY`（或 AI + `AI_FULL_AUTONOMY`）且 `controller_count == 0` 的武器调用 `autonomous_handling()`。
- 循环 2：对每个已启用的 `ams_mode`，若 `able_to_operate`，则获取目标、选取一个，然后发射 `sort_priority` 最高且 `permitted_ams_modes` 包含该模式名称的自主（非完全）武器；发射一次后 `break`。
- `autonomous_fire`（`autonomy.dm:5-17`）：绘制一条光束，遵守 `next_ams_shot` 冷却（`ams_targeting_cooldown`），`fire_proc_chain`，加上 `ams_shots_fired`。
模式：`ams_mode/sts`（反舰，`max_range 85`，默认启用；当 `ams_data_source == AMS_LOCKED_TARGETS` 时使用被标记目标或 `target_lock`；受 `ams_shot_limit` 限制）、`ams_mode/countermeasures`（`max_range 10`，目标为 `torpedoes_to_target`）。AMS 控制台设定数据源 / 射击上限（1..100，默认 5）/ 模式启用（`autonomy.dm:108-169`）。
`flak` 自定义处理器接战 `max_ai_range` 内的附近敌人（友军、`last_target`、黑名单、essential 与不可瞄准者均跳过）；`pdc_mount` 接战 15 overmap 距离内的传入制导弹药，除非被 `disruption`（`autonomy.dm:26-69`）。
置信度：HIGH（高）。

### COMBAT-020 —— AI 武器选择：射程惩罚最低；精英发射一切
断言：标准 AI 发射单件 `get_ai_range_penalty` 最低（= `max(0, distance - optimal_range)`）的 AI 可控武器；精英 AI 发射所有就绪的 AI 武器。两者均为瞄准辅助（`ai_aim=TRUE`）。
证据：`ai-skynet.dm:1366-1397` `ai_fire`、`:1404-1426` `ai_elite_fire`；`firing_checks.dm:191-199` `get_ai_range_penalty`。AI 首先为空的武器调用 `try_initiating_resupply()`（`_overmap_ship_weapon.dm:322-336`；舰船上的重新武装计时器），并遵守 `max_weapon_range`（`ai-skynet.dm:1373`）。交叉引用：目标获取/目标选择是 `choose_goal`/`ai_process`（`ai-skynet.dm:1276-1294`、`:1490+`）—— AI/skynet 系统。
置信度：HIGH（高）。

### COMBAT-021 —— 武器保险联锁
断言：`weapon_safety`（默认 FALSE）在 TRUE 时阻断所有开火，并彻底阻断点击开火（除非舰船覆写 `can_friendly_fire`，例如通用模块）。仅驾驶员可通过舰船动词切换它，且仅可在 overmap/保留 z 空间中切换；战斗机还可由具有 MAA 权限的战斗机控制台远程切换。
证据：`overmap.dm:139`（`weapon_safety` 默认 FALSE）、`weapons.dm:12-15`（保险阻断 `fire`）、`weapons.dm:54-55`（保险阻断 `fire_weapon`）、`overmap.dm:586-590`（当 `!can_friendly_fire` 时保险阻断 `InterceptClickOn`）、`verbs.dm:87-95` `toggle_safety`（要求 `verb_check()` = 使用者为驾驶员）、`overmap.dm:991-993` `can_change_safeties`（仅 overmap 或保留 z 特性）、`fighters/control_console.dm:75-97`（远程全局/逐战斗机保险覆写）。
后果：一艘配员舰船在驾驶员关闭保险前无法开火；保险对普通舰船默认关闭（武器处于可开火状态），但开关存在。
置信度：HIGH（高）。

### COMBAT-022 —— 自主武器被手动控制抑制；补给基于计时器
断言：当任何控制者选择了一件武器时，其自主性被抑制（`controller_count>0`），且弹尽的 AI 武器会安排一个自我补给计时器而非开火。
证据：`autonomy.dm:210-231`（`controller_count > 0 → continue`）、`_overmap_ship_weapon.dm:322-336` `try_initiating_resupply`（设定 `SHIP_RESUPPLYING_LIGHT/HEAVY`，安排 `ai_self_resupply[_light]`）、`ai-skynet.dm:1429-1438`（重新武装每周期增加最大值的 ¼；轻型完全补满）。
后果：让一件 PDC/flak 保持未被选择会使其持续自动开火；持有它会禁用该自动化。
置信度：HIGH（高）。

### COMBAT-023 —— 战斗机/挂载点开火模型（交叉引用战斗机）
断言：战斗机使用两个 OSW datum（主/副），具有 `requires_physical_guns=FALSE`；`fire_nonphysical` 被覆写为调用 `hardpoint_fire`，它发射已安装部件自身的弹药。
证据：`weapon_datum_types.dm:281-340`（战斗机 datum 锁定到 `sort_priority` 999/998，`can_modify_priority=FALSE`，控制 PILOT|GUNNER）、`_fighters.dm:1637-1660` `hardpoint_fire`（将 `OSW_FIGHTER_MAIN_WEAPON`/`SECONDARY` 映射到配装槽）、`_fighters.dm:1716-1726` 部件 `fire()`（若无弹药则空放音；消耗一个弹壳）。部件 `on_install` 将其点射/延迟/音效复制到 OSW 上（`_fighters.dm:1728-1736`）。
置信度：HIGH（高）。

### COMBAT-024 —— 屏幕震动、隐身闪烁与内部中继后果
断言：开火有三种全舰副作用：对开火舰船内部的屏幕震动、15 秒的临时隐身丧失，以及一枚中继抛射物/爆炸到目标内部 z 层级（带伤害音效）。
证据：`firing.dm:16-21`；`radar.dm:356-370`；`damage.dm:67-96` `relay_damage`（从依撞击角度选定的侧面朝内部中心发射一枚抛射物，±20° 散布）、`small_craft/relay_damage` 为空操作（`damage.dm:98-99`）。
置信度：HIGH（高）。

## 跨系统依赖
- **系统 10 弹药**：实体火炮状态机（`load/feed/chamber/fire`）、弹药/弹匣类型、维护/故障、`weapon_malfunction()`。OSW 调用 `firing_weapon.can_fire()`/`.fire()` 并读取 `get_ammo_list()`。（文件：`nsv13/code/modules/munitions/ship_weapons/*`。）
- **护盾（`shieldgen.dm` / `pdsr.dm`）**：`shields.absorb_hit` 是 overmap `bullet_act` 中的第一道门；`SHIELD_ABSORB` 对 `SHIELD_FORCE_DEFLECT/REFLECT`。
- **AI/Skynet（`ai-skynet.dm`）**：目标获取（`choose_goal`、`ai_process`、`max_weapon_range`、`max_tracking_range`、`warcrime_blacklist`、`essential`）、`ai_fire`/`ai_elite_fire`、`calculate_intercept`。
- **传感器/雷达（`radar.dm`）**：`handle_cloak`、`is_sensor_visible`、`SENSOR_VISIBILITY_TARGETABLE`，DRADIS 扫描/视觉范围门控炮相机与标记。
- **战斗机（`fighters/*.dm`）**：挂载点开火、战斗机控制保险覆写。
- **装甲（`armour/armour_quadrant.dm`）**：象限定义与伤害路由。
- **物理（`physics.dm`、`physics2d.dm`）**：抛射物行进与碰撞；设定抛射物方位的开火标志。

## 未解问题
1. `miss_chance` / `max_miss_distance` 被逐武器定义并传入 `calculate_intercept`，但该 proc 从不使用它们。AI 瞄准是有意始终完美的（变量为残留），还是一个在重构中丢失了预期散布的 bug？（`ai-skynet.dm:1445-1481`。）
2. 非实体弹药池（`shots_left`、`light_shots_left`、`missiles`、`torpedoes`）—— 它们的最大值以及船员如何补充它们位于本系统之外；在记录经济之前确认归属（弹药/overmap 核心）。
3. `requires_physical_guns` + `linked_areas`：一艘无内部但*有玩家占据*的舰船会静默地非实体开火。是否有任何预期舰船命中此情况尚未验证。
4. `hybrid_railgun` 使用 `standard_projectile_type = /obj/item/projectile/bullet`（基础类型）—— 是否在其他地方替换为真实抛射物尚未确认。
5. 平衡数值（伤害对装甲）在此是机械事实，但其*预期*有效性属于策略，非代码所能证明。
