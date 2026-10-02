> 本文为 research/evidence/point_defence.md 的中文翻译。

# 近防、对抗措施与电子战

NSV13 系统 #11 的研究笔记。事实来源：当前 `master` 的 .dm 代码。
所有行号引用均针对 `D:\code\NSV13` 下的文件。来源标签：(a) 已证实的机制，
(b) 解读，(c) 策略。

## 系统概述

NSV13 中的"近防"不是一个单一系统，而是一族由 overmap 舰船武器 datum 框架
（`nsv13/code/modules/overmap/weapons/delta_overmap_ship_weapons/`）驱动的相互重叠的行为：

- **实体弹道近防**：PDC（`pdc.dm`）与 Flak（`flak.dm`）—— 实体弹匣供弹的炮位。
- **能量近防**：Laser AMS（`laser_ams.dm`）与 Laser PD（`laser_pd.dm`）—— 电力供能，无弹匣。
- **对抗措施**：战机 chaff 撒布器（`countermeasure_ammo.dm`）—— 反制制导弹药。
- **水雷**：`overmap/weapons/mines.dm` —— AI 布设的区域拒止。
- **电子战（EWAR）**：打捞控制台的 "telemetry scrambler"（`game/machinery/computer/salvage.dm`）——
  用于禁用敌方自主近防、以便登舰小队飞入的工具。

核心分派是 `handle_autonomous_targeting()`（`autonomy.dm:204`），由 `ai_process()`（`physics.dm:135` -> `ai-skynet.dm:1490`）在每个 `slowprocess()` tick（约每 ~1s 一次）调用。
这对**所有** overmap 舰船（玩家与 AI）都运行；`ai_process()` 仅在目标选取调用*之后*、针对非 AI 的目标逻辑才提前返回。`slowprocess` -> `ai_process` -> autonomy 是理解过程中最重要的单一路径：正是它让"自动"近防得以工作。

存在两条独立的自动开火通道（均在 `handle_autonomous_targeting` 内部）：
1. **自处理武器**（`autonomous_handling()`）：标有 `OSW_CONTROL_FULL_AUTONOMY` 的武器，或**若舰船为 AI 控制**则标有 `OSW_CONTROL_AI_FULL_AUTONOMY` 的武器。第 213 行。
2. **AMS 模式**：AMS 控制台上玩家可配置的模式，它们会发射任何*不*具备 FULL/AI-FULL 自主性、且允许该模式的 `OSW_CONTROL_AUTONOMOUS` 武器。第 219-243 行。

## 核心游戏循环

敌方发射导弹／鱼雷（制导弹药）。防守方的近防试图将其击落：
PDC 弹药与 flak 云对来袭的制导弹药造成实体伤害（`projectiles_fx.dm:410-451`），
或 AMS 的 "countermeasures" 模式以舰船自身的导弹／激光射向它们。锁定某舰船的导弹会被登记在该舰船的 `torpedoes_to_target` 中（`physics.dm:701`、`autonomy.dm:247-250`），这正是近防取用的目标列表。若要登舰而非摧毁敌人，攻击者先将其削弱至 ≤50% 完整度，然后使用电子战打捞控制台，它会加载一个登舰内部并翻转 `ai_controlled = FALSE` —— 从而静默地关闭敌方的 PDC 自动拦截（见 PD-010）。

## 机制

### PD-001
断言：PDC 是一种实体、弹匣供弹的弹道炮位；在玩家舰船上它是**手动**武器，由飞行员或炮手发射，而非自动武器。
面向玩家的后果：船员必须装载一个 PDC 弹药箱并将 PDC 选为其武器，才能击落来袭导弹。在玩家舰船上它不会"自动发生"。
证据：`weapon_control_flags = OSW_CONTROL_PILOT|OSW_CONTROL_GUNNER|OSW_CONTROL_AI|OSW_CONTROL_AUTONOMOUS|OSW_CONTROL_AI_FULL_AUTONOMY`
（`weapon_datum_types.dm:370`）。PDC 唯一的自主行为（`pdc_mount/autonomous_handling`，
`autonomy.dm:55`）仅在 `ai_controlled && (flags & OSW_CONTROL_AI_FULL_AUTONOMY)`
（`autonomy.dm:213`）时才被触及。玩家舰船默认 `ai_controlled = FALSE`（`ai-skynet.dm:1299`；仅敌方
类型将其设为 TRUE，例如 `syndicate.dm:75`）。
条件：一艘以 `ai_controlled = TRUE` 起始的舰船可以在舵位切换自动驾驶
（`helm.dm:28-31`），这也会开启 PDC 自动拦截；普通玩家舰船起始为 FALSE。
例外 / 覆写：在 AI 控制的舰船（或被 AI 自动驾驶操控的舰船）上，PDC *确实*
自动拦截（PD-003）。
置信度：HIGH（高）

### PD-002
断言：PDC 每 0.25 秒发射 3 发点射；每发是一枚 15 伤害的轻型抛射物；弹药架可容纳 300 发（30.12x82mm）。
面向玩家的后果：一门 PDC 在 25 次点射内打空 300 发的弹匣（100 发=约 8s 连续射击）。单发很弱；持续射击才是杀伤所在。
证据：`/datum/overmap_ship_weapon/pdc_mount` burst_size 3、fire_delay 0.25 SECONDS、
optimal_range 10、miss_chance 33、max_miss_distance 6、ai_fire_delay 0.5 s
（`weapon_datum_types.dm:358-373`）。抛射物 `/obj/item/projectile/bullet/pdc_round` 伤害 15、
散布 5（`projectiles_fx.dm:626-632`）。弹药架 `max_ammo = 300`
（`pdc.dm:19`）；弹匣 `/obj/item/ammo_box/magazine/nsv/pdc` max_ammo 300（`magazines.dm:55-56`）。
弹药架具有 `feed_delay = 0`、`chamber_delay = 0`、`chamber_delay_rapid = 0`、`semi_auto = TRUE`、
`auto_load = TRUE`、`bang = FALSE`、`maintainable = FALSE`（`pdc.dm:16-35`）。
条件：`burst_fire_delay` 在 PDC datum 上**未**被覆写，因此点射内的延迟为
datum 默认值 `1` 分秒（`_overmap_ship_weapon.dm:61`）。每次触发的实体点射规模
为 `min(burst_size, ammo_in_rack)`（`firing.dm:45`）。
例外 / 覆写：PDC 无法从电路构建 flak 变体（电路被注释掉，`flak.dm:13`）；普通 PDC *是*可建造的。
置信度：HIGH（高）

### PD-003
断言：在 AI 控制的舰船上，PDC 仅在来袭制导弹药位于 15 格以内时才自动迎击；它发射瞄准射击（`ai_aim = TRUE`）。
面向玩家的后果：一艘带 PDC 的 AI 舰船会在你的导弹即将命中前将其从天空中拍落。从最大鱼雷射程攻击并不保证命中。
证据：`pdc_mount/autonomous_handling()`（`autonomy.dm:55-69`）：遍历
`linked_overmap.torpedoes_to_target`，跳过 `z` 不匹配者，并 `if(target_range > 15) continue;`
然后 `fire_proc_chain(incoming_missile, ai_aim = TRUE)`。若
`linked_overmap.disruption` 非零也会提前返回。
条件：仅在 `ai_controlled`（PD-001）时运行。`torpedoes_to_target` 仅由锁定本舰的制导弹药填充（`physics.dm:701`）。
例外 / 覆写：已发射但未锁定的导弹（或非制导火箭）不在列表中，
因此 PDC 忽略它们。
置信度：HIGH（高）

### PD-004
断言：PDC 被刻意**排除**在面向玩家的 AMS 模式之外，因为其 `OSW_CONTROL_AI_FULL_AUTONOMY` 标志使 AMS 扫描跳过它。
面向玩家的后果：在 AMS 控制台上启用 "Anti-missile countermeasures" **不会**让本舰的 PDC 自动开火。只有 VLS 导弹发射器和 Laser AMS 响应 AMS 模式。
证据：AMS 循环跳过带有 `OSW_CONTROL_FULL_AUTONOMY` 或 `OSW_CONTROL_AI_FULL_AUTONOMY`
（`autonomy.dm:231-234`）的武器；PDC 具有 `OSW_CONTROL_AI_FULL_AUTONOMY`（`weapon_datum_types.dm:370`）。
置信度：MEDIUM（中）—— 代码路径毫不含糊，但这是一个令人意外的设计意图；请在游戏中验证玩家 PDC 是否真的从不在 AMS 控制台触发。

### PD-005
断言：AMS 控制台让船员可以启用逐模式自动开火；"Anti-ship" 默认启用，"Anti-missile countermeasures" 默认**关闭**；目标来源默认为 "Locked Targets"，可切换为 "Painted Targets"；一个射击上限限制反舰开火。
面向玩家的后果：一艘全新舰船的 AMS（若有符合条件的武器）会自动向其当前锁定的目标开火，但在船员启用对抗措施模式之前*不会*拦截来袭导弹。弹药／能量允许时，每艘船约每 ~1.5 s 发出一次 AMS 射击。
证据：`/obj/machinery/computer/ams`（`autonomy.dm:137-196`）；`ui_act` 在 `AMS_LOCKED_TARGETS`/`AMS_PAINTED_TARGETS` 之间切换 `data_source` 以及模式；`set_shot_limit` 将数值限制在 1-100，默认 5。默认值：`ams_data_source = AMS_LOCKED_TARGETS`、`ams_targeting_cooldown = 1.5 SECONDS`、
`ams_shot_limit = 5`、`ams_shots_fired = 0`（`overmap.dm:190-195`）。`ams_mode/sts` 默认启用 TRUE，max_range 85（`autonomy.dm:108-124`）；`ams_mode/countermeasures` 默认启用 FALSE，max_range
10，目标为 `torpedoes_to_target`（`autonomy.dm:126-133`）。全局速率限制
`next_ams_shot`/`ams_targeting_cooldown` 位于 `autonomous_fire()`（`autonomy.dm:6-17`）。
条件：当数据来源为 LOCKED_TARGETS 时，`sts/acquire_targets` 使用 `OM.target_lock`，否则使用
`target_painted`。友方／关键／异 z 目标被跳过（`acquire_targets`，`autonomy.dm:83-98`）。
例外 / 覆写：AMS 选择是逐舰状态，在切换模式时重置
（`ams_shots_fired = 0`，`autonomy.dm:168`）。
置信度：HIGH（高）

### PD-006
断言：Laser AMS 是一种能量武器，发射 hitscan 反导弹光束，但仅在船员 (1) 分配电力、(2) 启用机械、(3) 启用 "Anti-missile countermeasures" 时才工作。
面向玩家的后果：全新／未配置的 laser AMS 是惰性的。游戏内标牌正是这么写的。
证据：`/obj/machinery/ship_weapon/energy/ams`（`laser_ams.dm:1-26`）：`active = FALSE`、
`charge = 0`、`power_modifier = 0`、`power_modifier_cap = 2`，charge_rate 1,000,000、
charge_per_shot 3,000,000、max_charge 3,000,000（约 1 发）。`process()`（`phaser.dm:118-134`）仅在
`active` 时充能，并将 rate/shot/max 乘以 `power_modifier`；功率为 0 时 charge_rate = 0。
Datum `laser_ams`：burst 1、fire_delay 0.35 s、optimal_range 30、`permitted_ams_modes = list(
"Anti-missile countermeasures" = 1)` 仅此一项，控制标志仅 `OSW_CONTROL_AUTONOMOUS`
（`weapon_datum_types.dm:467-479`）。抛射物 `/obj/item/projectile/beam/laser/point_defense`
伤害 30，hitscan（`projectiles_fx.dm:671-682`）。地图标牌确认："laser anti missile systems
provided must have power allocated, must be enabled, and must have Anti-missile countermeasures
enabled in the AMS control console"（`Aetherwhisp2.dmm:4564`）。
条件：laser_ams 的 `static_charge` 默认 FALSE，因此抛射物伤害随
`power_modifier` 缩放（`phaser.dm:113-116`）。充能取自舰船电网
（`try_use_power`，`phaser.dm:137-151`）；`idle_power_usage` 空闲时 2500，充能时 1000。
例外 / 覆写：`laser_ams` 在玩家舰船上实体开火（需要在机械中链接）,
在 AI 舰船上非实体开火，使用舰船 `light_shots_left` 池。
置信度：HIGH（高）

### PD-007
断言："Laser PD"（`laser_pd`）是一门*手动*近防激光炮塔，由专用炮手控制台（或飞行员链接）瞄准，而非 AMS／自主武器，且不需要弹药 —— 只需电力。
面向玩家的后果：必须有人坐在近防激光控制台前手动追踪目标；它永远不会自行开火。
证据：Datum `/datum/overmap_ship_weapon/phaser_pd` 具有 `weapon_control_flags = OSW_CONTROL_MANUAL|OSW_CONTROL_AI`
（无 AUTONOMOUS），burst 4、burst_fire_delay 0.25 s、fire_delay 1.5 s、optimal_range 16、miss_chance
20（`weapon_datum_types.dm:169-183`）。机械 `/obj/machinery/ship_weapon/energy/laser_pd`
使用带 `fire_delay = 1.5 SECONDS` 的 `overmap_gunning` 组件（`laser_pd.dm:46-47`）；
`start_gunning()` 添加该组件（`laser_pd.dm:78-82`）。控制台通过 `gun_id` 或 multitool 链接
（`laser_pd.dm:10-43`）。充能：rate 500,000、per_shot 1,000,000、max_charge 4,000,000、
`static_charge = TRUE`、`power_modifier_cap = 1`（`laser_pd.dm:63-69`）。抛射物
`/obj/item/projectile/beam/laser/phaser/pd` 伤害 60（`projectiles_fx.dm:656-661`）。
条件：`RefreshParts` 可用电容器提高 `power_modifier_cap` 和 `max_charge`
（`laser_pd.dm:87-93`）。
例外 / 覆写：未发现关于自主性的例外。
置信度：HIGH（高）

### PD-008
断言：Flak 炮组在**玩家和 AI 舰船两者**上都是完全自主的：它们在无任何控制台输入的情况下，自动向 30 格内最近的合法敌方 overmap 开火。
面向玩家的后果：安装一个已装填的 flak 弹药架会把它变成一道常开的 flak 屏幕，自行射击附近的敌方舰船；"manual" 选择警报只是风味警告。
证据：Datum `flak` 为 `OSW_CONTROL_PILOT|OSW_CONTROL_AI|OSW_CONTROL_AUTONOMOUS|OSW_CONTROL_FULL_AUTONOMY`
（`weapon_datum_types.dm:397`）。第一条自主循环仅要求 FULL_AUTONOMY（而非 `ai_controlled`）
（`autonomy.dm:213`）。`flak/autonomous_handling()`（`autonomy.dm:26-52`）扫描
`current_system.system_contents` 以寻找 `max_ai_range = 30` 内的合法敌人
（`weapon_datum_types.dm:401`），跳过自身舰船、自身 last_target／自身弹药、同阵营、
`warcrime_blacklist`、`essential` 以及不可被瞄准的目标，然后 `fire_proc_chain(ship)` 并中断。
选择警报文本称 flak 屏幕在被手动控制前为 "OFFLINE"，但没有代码强制执行这一点（`weapon_datum_types.dm:393`）。
条件：Flak datum 的 burst_size 被设为创建时传入的炮组数量
（`weapon_datum_types.dm:404-406`）；AI 舰船以数量 2-3 创建它（例如 `syndicate.dm:240,514,553`）。
射击延迟 0.5 s，optimal_range 8（`weapon_datum_types.dm:389-390`）。
例外 / 覆写：Flak 自动开火即使在被登舰／`ai_controlled = FALSE` 的舰船上也持续运行
（该循环不以 `ai_controlled` 为门槛），因此**电子战不能阻止敌方 flak**（见 PD-010）。
置信度：HIGH（高）

### PD-009
断言：Flak 是一种区域拒止的空爆武器：炮弹飞行约 距离/1.5 格，然后炸裂成 2-5 朵短命的云，对穿过的敌方导弹和舰船造成伤害。
面向玩家的后果：Flak 对穿越云的战机和导弹造成伤害；炮弹直接命中几乎无害（2 伤害），所以所有的咬合力都在 AOE。密集的 flak 是一堵墙，而非狙击手。
证据：`flak/animate_projectile` 设置 `steps_left = get_overmap().get_flak_range(target)`，其中
`get_flak_range = max(dist/1.5, safe_distance)`（`flak.dm:40-51`）。炮弹
`/obj/item/projectile/bullet/flak` 伤害 2，在命中或超出射程时炸裂为 `rand(2,5)` 朵云
（`flak.dm:93-132`）。云 `/obj/effect/temp_visual/flak` 持续 2.5 s；`on_entered` 对制导弹药造成
`severity*30`、对**不同阵营**的 overmap 造成 `severity*20`，其中
`severity = 1/dist`（`flak.dm:53-88`）。
条件：友军伤害通过阵营比较（`P.faction != faction`）防止。
例外 / 覆写：`max_miss_distance = 8`、`miss_chance = 33`（`weapon_datum_types.dm:394-395`）。
置信度：HIGH（高）

### PD-010
断言：电子战遥测扰乱器的工作原理是加载一个登舰内部并将目标的 `ai_controlled = FALSE`，这正是禁用其自主 PDC 导弹防御（以及所有 AI 炮术）的机制。它以目标处于 ≤50% 完整度为门槛。
面向玩家的后果：要登舰，你必须先把敌人打到半血，然后电子战控制台对其"扰乱"；它的 PDC 停止拦截你的登舰船／舱，让突击队得以逼近。"致盲／禁用敌方近防"在机制上就是"关闭该舰的 AI"。
证据：若 `(OM.obj_integrity * 100 / initial(OM.obj_integrity)) > required_damage_percentage`
（=50，`salvage.dm:8`），`salvage.dm:93` 则中止。成功时：`ai_load_interior(linked)` 然后
`OM.ai_controlled = FALSE`（`salvage.dm:99-103`），并设置 `linked.active_boarding_target`。PDC 自动拦截要求
`ai_controlled`（PD-001/PD-003），因此该开关就是禁用机制。
条件：目标必须为 `interior_mode == INTERIOR_EXCLUSIVE`、同 z、传感器可见度高于
`SENSOR_VISIBILITY_FAINT`，且非 `ghost_controlled`（`salvage.dm:60,83`）。一次只能维持一艘舰船
（`active_boarding_target`）；第二次尝试会发出确认警告。
例外 / 覆写：声明的 `max_salvage_range = 20`（`salvage.dm:7`）**在任何地方都从未被读取** ——
代码中不存在 20 格的强制；实际范围仅受传感器可见度和同 z 限制。冷却是一个**静态**的
`can_salvage` 变量，成功时 5 分钟／失败时 2.5 分钟（`salvage.dm:10,101,108`），因此它实际上是在该类型的所有
控制台之间共享，而非逐控制台。对任务关键目标，取消锁定会被
`COMSIG_SHIP_RELEASE_BOARDING` 阻止（`salvage.dm:120-121`）。
置信度：HIGH（高）（机制／数值）—— MEDIUM（中）于"禁用近防"是其*唯一*预期用途；
`ai_controlled = FALSE` 还会使舰船停止移动和发射 AI 武器，因此这是一种彻底瘫痪，而不只是近防干扰。

### PD-011
断言：电子战控制台的采矿变体仅在其无线电频道（Supply/Cargo 而非 Security）和其电路板上有所不同；所有扰乱机制完全相同，且共享同一个静态冷却。
面向玩家的后果：货船／采矿船可以执行与安全部门相同的登舰电子战。
证据：`/obj/machinery/computer/ship/salvage/mining` 设置 `radio_key = /obj/item/encryptionkey/headset_cargo`
和 `radio_channel = RADIO_CHANNEL_SUPPLY`（`salvage.dm:16-19`）；单独的电路板
（`salvage.dm:25-27`）。未覆写 `required_damage_percentage`、`max_salvage_range` 或
`can_salvage`。
条件：`can_salvage` 继承自基础类型；在 BYOND 中，静态变量在类型树中共享，除非被重新声明，因此普通控制台和采矿控制台共享一个冷却。（解读。）
置信度：MEDIUM（中）（静态共享是 DM 语言推断，代码中未明言）。

### PD-012
断言：对抗措施是战机专属、充能受限的 chaff 云，它要么摧毁、要么"迷惑"（关闭制导）来袭的导弹与鱼雷。
面向玩家的后果：战机飞行员点击 "Deploy Countermeasures" 抛撒 chaff；它要么直接击杀一枚制导导弹，要么使其停止追踪。每次使用消耗一次充能（落下三朵云）；标准撒布器有 3 次充能，可升级到 5 次。
证据：`/obj/structure/overmap/small_craft/proc/fire_countermeasure()`（`countermeasure_ammo.dm:41-57`）：
需要一个 `countermeasure_dispenser` 挂点，要求 `CD.charges`，递减一次充能，生成
3 个 `/obj/effect/temp_visual/countermeasure_cloud`（相隔 5 ds）。云 `on_entered`（`countermeasure_ammo.dm:32-39`）：
对于 `/torpedo` 或 `/missile` 抛射物，`if(prob(50)) B.explode(); else B.homing = FALSE`。云
持续 10 s。撒布器 `max_charges = 3, charges = 3`（`_fighters.dm:1584-1590`）。装填物品
`/obj/item/ship_weapon/ammunition/countermeasure_charge`（`restock_amount = 3`，或 `.../five` = 5）
手工装入（`_fighters.dm:593-607`）。UI 数据暴露 charges/max（`_fighters.dm:143-145`）。
动词接线于 `verbs.dm:106`。
条件：Chaff 仅影响 `guided_munition` 导弹／鱼雷，不影响子弹、光束或 flak。
面对 PD-003 的 PDC 自动开火，chaff 毫无作用（它直接以抛射物为目标）。
例外 / 覆写：`explode()` 之所以有效，仅因为制导弹药具有抛射物 `explode`
proc（`flak.dm:163-176`）。
置信度：HIGH（高）

### PD-013
断言：太空水雷**仅能由 AI／NPC 舰船**（以及随机星系生成）布设；没有玩家布雷动词。水雷具有阵营归属，并在敌方舰船上引爆。
面向玩家的后果：玩家无法布雷；敌方巡逻／布雷舰船在身后投下水雷作为区域拒止，而漂流的星系可能已经含有漂移的水雷。
证据：`deploy_mine()`（`ai-skynet.dm:1749-1756`）递减 `mines_left`，强制执行
6 s 的 `mine_cooldown`，并在舰船自身中心生成 `/obj/structure/space_mine`；它仅
从 AI 目标调用（`patrol` `ai-skynet.dm:1227-1228`；`move_away_from` `ai-skynet.dm:1691-1692`）。
随机水雷在 `starsystem.dm:986-989` 于星系中生成。水雷 `on_entered` 仅对
`OM.faction != faction` 引爆（`mines.dm:75-78`）。`mines_left` 按敌对类型设定，例如 5/10/15
（`syndicate.dm:131,295,438`）。
条件：水雷伤害 100、`overmap_heavy`、`max_integrity` 300（`mines.dm:11-17`）。撞击
水雷施加 20 装甲穿透（"专为此设计"，`mines.dm:104-109`）。若存在则使用象限装甲。
例外 / 覆写：若水雷是因伤害而非撞击而被*摧毁*（`obj_destruction`），
`mine_explode()` 会在无目标的情况下运行，并对 `orange(2)` 中的**每一个** overmap **不论阵营**造成伤害
（`mines.dm:115-120`）。此外，破碎的水雷有 20% 的概率变为"未归属"
（`mines.dm:91-94`）。
置信度：HIGH（高）（无玩家布设）；MEDIUM（中）于"布雷舰"NPC 称谓是否映射到除高 `mines_left` 之外的
独特行为。

### PD-014
断言：所有近防开火都按阵营进行 IFF 检查：近防不会伤害同阵营的 overmap，且核心开火会直接拒绝友方 overmap 目标。
面向玩家的后果：你无法用近防射击自己的舰船或友方舰船；flak 云和对抗措施不会伤害友军。
证据：若 `overmap_target.faction == linked_overmap.faction`，`can_fire()` 返回 FALSE 并提示 "Target IFF friendly"
（`firing_checks.dm:112-117`）。Flak 云跳过同阵营伤害（`flak.dm:84`）。Flak
`autonomous_handling` 跳过自身舰船／自身阵营／自身 `last_target`（`autonomy.dm:35-37`）。
AMS `acquire_targets` 跳过同阵营、`essential` 和异 z（`autonomy.dm:88-90`）。
条件：`can_friendly_fire()` 是一个返回 FALSE 的占位符（`overmap.dm:626-627`）。
例外 / 覆写：被摧毁水雷的二次爆炸忽略阵营（PD-013）。
置信度：HIGH（高）

### PD-015
断言：登舰／电子战是针对近防密集舰船的预期反制，因为它是唯一能整体关闭自主近防的机制；普通导弹被近防硬克制（PDC 拦截、flak 云、对抗措施）。
面向玩家的后果：战略三角是：导弹克制无防御舰船；近防克制导弹；电子战（在 50% 伤害后）克制近防；登舰队俘获战利品。
证据：PD-003、PD-008、PD-012（近防 vs 导弹）；PD-010（电子战开关）。交叉引用：
`research/evidence/overmap.md:138-140` 已记载 salvage->boarding 流程。
置信度：MEDIUM（中）（策略／框架；每个组成部分均已证实，但"三角"是解读）。

## 跨系统依赖

- **制导弹药**（`projectiles_fx.dm:386-498`）：导弹／鱼雷具有 `obj_integrity`
  40-240；近防（PDC 弹药伤害 15、flak 云每击 30、laser AMS 光束 30）将其逐步削减。
  一枚失去制导的导弹（`countermeasure_ammo.dm:39`）变为非制导抛射物。
- **鱼雷／导弹锁定登记**（`physics.dm:689-701` -> `autonomy.dm:247-250`）：填充
  `torpedoes_to_target`，即 PDC 自动拦截和 AMS 对抗措施的目标池。
- **Overmap 武器 datum 框架**（`_overmap_ship_weapon.dm`、`firing.dm`、`firing_checks.dm`）：
  所有近防节奏／弹药／控制流程都经过它。
- **电网**（`phaser.dm:118-151`）：能量近防取自舰船电力（`try_use_power`），与
  从弹匣取用的弹道近防不同。
- **战机**（`_fighters.dm`）：战机是 `/obj/structure/overmap/small_craft`
  （`isovermap` = `istype(A, /obj/structure/overmap)`，`__DEFINES/overmap.dm:22`），因此它们是 flak AOE（每击 20 伤害）、PDC 弹药和水雷的合法
  目标，并且它们携带可击败制导导弹的对抗措施
  撒布器。
- **登舰／内部**（`boarding/interiors.dm`）：电子战（`ai_load_interior`）加载夺船地图；
  `hammerlocked`（`interiors.dm:102`）只是标记舰船以便 IFF 控制台保持被 emag
  （`iff_console.dm:36`）—— 它*不是*禁用近防的东西。`ai_controlled = FALSE` 才是。
- **扰乱武器**（`projectiles_fx.dm:555-580`）：扰乱鱼雷增加 `disruption`，
  它 (a) 短路 PDC 的 `autonomous_handling`（`autonomy.dm:56`），(b) 使 AI 舰船
  超时（`ai-skynet.dm:1501`）。这是第二种、基于导弹的压制近防方式。

## 开放问题

1. 玩家 PDC 在任何 AMS 模式下是否真的从不自动开火？代码称它会被跳过
   （PD-004），但这与直觉相悖；需要游戏内或单元测试确认。
2. `max_salvage_range = 20` 本应限定什么？它被声明却从未使用（PD-010）。是
   早期设计强制 20 格登舰范围，还是 20 是传感器检查之前遗留下来的？
3. salvage 的 `can_salvage` 静态冷却在安全控制台和采矿控制台之间、以及同一地图上多艘舰船的控制台之间，是否真的共享？（PD-011，MEDIUM。）
4. 玩家船员能否通过舵位在*被俘获*的敌舰上切换 `ai_controlled = TRUE`
   （`helm.dm:28`），从而重新启用其自主 PDC？需要 `initial(linked.ai_controlled)`，
   因此只有其*类型*起始即为 AI 控制的舰船才符合条件。
5. `OSW_CONTROL_MANUAL` 武器（laser_pd、gauss、phaser_pd）—— 是否存在至多一名炮手的锁定，
   以及多名炮手如何交互（`get_overmap` 中的组件处理）？此处未追踪。
6. PDC 确切的实体点射间隔：`burst_fire_delay` 默认为 `1`（分秒）—— 确认
   这是有意为之，还是 `weapon_datum_types.dm:361` 中缺少一个 `SECONDS` 乘数。
