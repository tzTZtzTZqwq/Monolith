> 本文为 research/evidence/fighters.md 的中文翻译。

# 战斗机、小型飞行器及其操作

范围：玩家驾驶的小型飞行器（`/obj/structure/overmap/small_craft` 及其子类型），
它们的起飞/返回方式、燃料、模块、建造、座舱，以及它们与武器 / 点防御 / 登舰
的接口。GUI/TGUI 不在讨论范围内。

源文件位置：
- `nsv13/code/modules/overmap/fighters/`（`_fighters.dm`、`construction.dm`、
  `control_console.dm`、`fighters_launcher.dm`、`fuel_tank.dm`、`tug.dm`、
  `vv_docking.dm`、`modules/*`）。
- `nsv13/code/game/general_quarters/dropship_types.dm` + `dropship.dm`（运输机/Sabre）。
- `nsv13/code/modules/overmap/traders_items.dm`（仅商人出售的战斗机）、
  `nsv13/code/modules/research/designs/fighter_designs.dm`（原型机）、
  `nsv13/code/modules/cargo/packs.dm`（货物初始套件）、
  `nsv13/code/modules/overmap/verbs.dm`（舰船动词）、
  `nsv13/code/modules/overmap/weapons/delta_overmap_ship_weapons/new_weapon_types/weapon_datum_types.dm`（战斗机 OSW）。

关于命名的说明：基础类型设置 `name = "Space Fighter"`；战斗子类型覆写为
Rapier/Scimitar/Peregrine。战斗机的 `random_name` 为 TRUE，因此被生成的战斗机
运行时名称是随机的（`generate_fighter_name()`）。

## 系统概述

所有玩家驾驶的小型飞行器都派生自 `/obj/structure/overmap/small_craft`
（`_fighters.dm`）。它们是 `anchored`、`mass = MASS_TINY`，并作为星图对象驱动，
而不是作为普通载具被四处走动操作。每架飞行器不像硬编码固定装备那样，而是携带
一个 **装备配置组件**（`/datum/component/ship_loadout`），其中持有一组 **固定的
挂载点槽位**；每个槽位接受一个 `/obj/item/fighter_component`。组件承载着飞行器
的引擎、油箱、装甲、座舱罩、传感器、武器等。战斗机在两个 z 层级上飞行：停放在
母舰内部的 *舰侧*（一个普通的 map z），以及 *在星图上* 作为缩放过的精灵，跨越
地图边界或通过对接计算机时在两者之间转移。

已收录、可接触的小型飞行器有：基础 small_craft（自制的“轻型”框架）、Su-818
Rapier（`combat/light`）、Su-410 Scimitar（`combat/heavy`）、Su-437 Sabre 通用
舰（`transport/sabre` 及其 `/mining` 变体）、Su-624 Trafalgar 运输机（`transport`）、
NSV Sephora（`transport/starter`），以及逃生舱。Peregrine（`combat/solgov`）、
辛迪加变体、炮艇以及商人的“judgement”/“prototype”均为阵营/事件/管理员/商人专属。

## 核心玩法循环

1. 一架战斗机存在于舰侧某个已映射的停靠点或发射台上，从商人处购买，作为初始套件
   （框架 + 部件）通过货物订购，或是在原型机中制造 / 手工组装（`fighter_frame`）。
   `Initialize()` 将 `components` 列表安装进装备配置，设置 `obj_integrity =
   max_integrity` 及 `set_fuel(rand(500,1000))`。
2. 一名驾驶员（战斗机需要 `ACCESS_COMBAT_PILOT`，运输机需要 `ACCESS_TRANSPORT_PILOT`）
   点击飞行器 / 将自己拖拽到其上以 `enter()` 并 `start_piloting()`。引擎必须通过
   APU（燃料管线）启动并点火；否则 `can_move()` 为 FALSE，飞行器不会加速。
3. 起飞：磁力弹射器（`fighter_launcher`）或拖车抓住飞行器（`mag_lock`），操作员
   将其发射，飞行器以 `velocity.e/a = ±20` 被抛出，并带有临时的 20 速度上限。
   或者驾驶员只需飞到舰船 z 的边缘，自动转移到星图上。
4. 在星图上，飞行器作战（主/副挂载点），由通用飞行器或燃料泵补给燃料，部署
   干扰弹，然后要么死亡（乘员自动进入逃生舱），要么返回。
5. 返回：启用对接计算机的对接模式，飞入一个拥有 `docking_points` 的友方、更重的
   星图对象；`transfer_from_overmap()` 将飞行器传送回该舰的某个对接点，并武装
   保险。

## 机制

### FIGHTER-001 — 战斗机是一个带槽位装备配置的星图对象，而非载具
断言：每架战斗机的各项能力（引擎等级、燃料、装甲、座舱罩、武器、传感器、对接）
都存在于放入 `ship_loadout` 组件固定挂载点槽位的 `/obj/item/fighter_component`
物品中；不存在独立的储物柜式舱室。
面向玩家的影响：战斗机的能力恰好就是已安装组件之总和；拔出组件（5 秒 `do_after`）
会实时改变属性；没有引擎的飞行器完全无法移动。
证据：
- `_fighters.dm:7-59` 基础类型变量（`loadout_type`、`components`、
  `max_integrity`、推力、`speed_limit`、access）。
- `_fighters.dm:909-976` `/datum/component/ship_loadout`（`equippable_slots`、
  `install_hardpoint()`、`remove_hardpoint()`、`process()` 逐刻处理组件）。
- `_fighters.dm:978-995` `/obj/item/fighter_component` 基础（`slot`、`weight`、
  `power_usage`）。
- 槽位名称：`code/__DEFINES/nsv13.dm:49-75`（`HARDPOINT_SLOT_*`、
  `ALL_HARDPOINT_SLOTS`、`LOADOUT_DEFAULT_FIGHTER`、`LOADOUT_UTILITY_ONLY`、
  `LOADOUT_ESCAPE_POD`、`ENGINE_RPM_SPUN 8000`）。
- 在 `attackby` / `MouseDrop_T` 上的安装：`_fighters.dm:612-621`、
  `:623-649`；UI `eject_hardpoint` 动作 `:237-248`。
条件：`install_hardpoint()` 会拒绝 `slot` 不在装备配置的 `equippable_slots` 中的
组件。
例外 / 覆写：通用飞行器（`loadout_type = LOADOUT_UTILITY_ONLY`）只接受
`HARDPOINT_SLOTS_UTILITY`（没有专用的主/副武器挂载点——只有
`UTILITY_PRIMARY`/`UTILITY_SECONDARY`，它们接受武器或通用模块）。逃生舱使用
`HARDPOINT_SLOTS_ESCAPE_POD`（无武器挂载点）。
置信度：HIGH（高）

### FIGHTER-002 — 引擎分级，直接设定速度/推力；重量会将其拖回
断言：已安装的引擎将其 `tier` 乘以机身 *初始* 的 `speed_limit`、前/后/侧推力
以及角加速度；随后每个 `weight` 非零的组件都会从这些数值中减去。
面向玩家的影响：装配 2 级/3 级引擎大约将机动性上限翻倍/增至三倍，而重型装甲、
火炮和大型油箱会降低它；最大速度就是你在飞行器上看到的数值，受装备配置修正。
证据：
- `_fighters.dm:1456-1475` `engine/on_install`（`*= tier`）及 `remove_from`
  （将所有数值设为 0——没有引擎，无法移动）。
- `_fighters.dm:1063-1094` `apply_drag()` / `remove_from()` 从
  `speed_limit`/推力中减去 `weight`，从侧向减去 `0.25*weight`，从角加速度减去
  `weight*10`。
- 重量，例如：装甲 tier1 为 1，tier2 为 2，tier3 为 1.25；重型火炮为 2；等离子
  切割器为 1；激光主武器为 3；油箱/引擎通过 `tier` 分级。
条件：引擎 `on_install` 遍历整个装备配置并重新应用每个组件的阻力，因此安装顺序
是先引擎后其他；`Initialize()` 最后安装引擎（`_fighters.dm:544-554`）。
置信度：HIGH（高）

### FIGHTER-003 — 装甲板是第二层 HP 池，并将机身完整度设为 初始值*tier
断言：`armour_plating/on_install` 设置 `target.max_integrity =
initial(target.max_integrity) * tier`；传入的 `take_damage` 首先被路由到装甲组件，
只有在装甲被摧毁后才会击中机身。存在一个护盾变体 `armour_plating/tier6`
（提供星图护盾）。
面向玩家的影响：总有效 HP 为装甲完整度 + 机身完整度；tier-2 装甲既使机身 HP 翻倍
又增加一块 450 点的板；SolGov tier6 用 125 点再生护盾替代装甲板。
证据：
- `_fighters.dm:1096-1273` 装甲分级；`:1263-1273` `on_install`/`remove_from`。
- `:813-837` `take_damage`（装甲吸收；装甲死亡时 `qdel` 并继续）。
- `:902-907` `try_repair` 先修复机身，再修复装甲。
- tier6 护盾：`:1149-1175`（AddComponent `/datum/component/overmap_shields`，
  125/125/15）。
条件：`remove_from(due_to_damage=TRUE)` 有意 *不* 重置机身生命值，因此失去装甲板
的飞行器会保留被提升后的最大值，直到安装新装甲。
置信度：HIGH（高）

### FIGHTER-004 — 引擎需要通过 APU 提供燃料流；燃料耗尽会触发“主警告”
断言：`can_move()` 返回 `engines_active()` = 引擎 `active()` 且 燃料 > 0。
引擎的 `active()` 只有在已开启、未受损、达到 `rpm >= ENGINE_RPM_SPUN (8000)`
且未淹缸时才为 TRUE。只有当 APU 的 `fuel_line` 处于 ON（在控制 UI / `fuel_pump`
动词中切换）时 rpm 才会攀升。没有燃料管线时，引擎会以 1000 rpm/每刻 *降速* 直到
熄火。
面向玩家的影响：你必须先打开电池、APU + 燃料管线，然后点火（或让 APU 将引擎带起
转速），飞行器才会移动；燃料耗尽会使飞行器变成死机并点亮主警告警报。
证据：
- `_fighters.dm:1292-1294` `engines_active`；`:1319-1326` `can_move`；逃生舱
  将两者覆写为始终 TRUE。
- `_fighters.dm:1393-1443` 引擎 `active()`/`process()`/`try_start()`/`apu_spin`。
- `_fighters.dm:1565-1581` APU `process()`（每 4 秒 `use_fuel(2,TRUE)`，转速
  ±500*tier）；`:1296-1303` `set_master_caution`。
- UI 动词：`apu`、`fuel_pump`、`battery`、`ignition`（`_fighters.dm:272-306`）。
条件：当 rpm 远低于 8000 时，`try_start()` 有 20% 概率淹缸并熄火。可以将
“淹缸”的引擎用螺丝刀清除（10 秒，`_fighters.dm:1383-1391`）。
置信度：HIGH（高）

### FIGHTER-005 — 燃料：油箱容量、消耗速率，以及两条补给路径
断言：燃料是 `fuel_tank` 组件内的试剂 `cryogenic_fuel`。油箱容量为 1000（t1）/
2500（t2）/ 4000（t3）/ 3000（运输机 t2）。怠速消耗为每刻 `0.5*tier`，推进时为
`0.5*tier + 0.25`；APU 在其燃料管线开启时附加固定的 2/刻（每 4 秒一次）。补给方式
为舰侧燃料泵（50 单位/秒，范围 2）或空对空加油套件（50/每次投送块，范围 10）。
面向玩家的影响：一架崭新的战斗机初始有 500-1000 燃料；按油门不同，预计在 1 级油箱
上可飞行约 10-20 分钟便须补给；HUD 在低于 40% 时显示低燃料警告。
证据：
- `_fighters.dm:1276-1290` `get/set_fuel`；`:1305-1317` `use_fuel`
  （`fuel_consumption = 0.5*engine.tier`，若 `user_thrust_dir` 则 `+0.25`）。
- `_fighters.dm:1338-1362` 油箱分级；`dropship.dm:78-84` 运输机油箱。
- 试剂：`nsv13/code/modules/reagents/chemistry/reagents/pyrotechnic_reagents.dm:1-8`。
- `fuel_tank.dm:164-192` 泵 `process()`（`units_per_second = 50`、
  `max_range = 2`）；`:7-15` 泵容量 3500；喷嘴攻击 `:207-227`
  （引擎运转时拒绝）。
- 加油套件：`modules/refueling_kit.dm`（`fuel_transfer_rate = 50`、
  `minimum_fuel_to_keep = 250`、`refuel_range = 10`、`battery_recharge_amount
  = 500`）。
- 低温泵的货物包：`packages/cargo/packs.dm:599`。
条件：燃料消耗按每次 `slowprocess()` 调用施加，它以约 0.7 秒的节奏运行
（`physics.dm:172-174`），而非每个物理刻——上述速率数字是每次调用。`use_fuel`
不随 delta_time 缩放。
例外 / 覆写：逃生舱完全忽略燃料（`engines_active` 强制为 TRUE）。
置信度：容量/消耗公式为 HIGH（高）；“可飞行分钟数”为 MEDIUM（中）
（由约 0.7 秒的节奏推导）。

### FIGHTER-006 — 起飞：弹射器抓住、蓄能 10 秒，然后以速度 20 抛出
断言：`fighter_launcher` 在 `ready` 时抓住任何进入其格的 small_craft，将其磁力
锁定，将速度归零，并重新定向。起飞需要 10 秒蓄能，然后按起飞方向设置飞行器的
`velocity.e/a = ±20`，并给予 5 秒的窗口，期间 `speed_limit = 20`（高于正常值），
之后恢复。弹射器在 10 秒内重新充能。
面向玩家的影响：航母/起飞甲板能以高初速将战斗机抛出；驾驶员必须面向起飞方向
（机头朝前），否则会撞墙。
证据：
- `fighters_launcher.dm:67-105` 弹射器类型 + `linkup`。
- `:122-158` `on_entered`（磁力锁定、`brakes=TRUE`、`velocity=0`、按 dir 的
  `desired_angle`）。`:115-120` 磁力锁定时 `can_brake()` 为 FALSE。
- `:173-209` `start_launch`/`abort_launch`/`finish_launch`（10 秒，±20）。
- `:265-268` `prime_launch`（`speed_limit = 20` 持续 5 秒；也会
  `release_maglock`）。
- 控制台：`:1-65` `fighter_launcher` 计算机（`launch`/`release`/`message`）。
条件：弹射器有 `ready` 门限；`galactica` 子类型仅作拦阻（catch，相关
launched 偏移被调整）；`launch_only` 仍会 catch，但不放置降落航点
（`fighters_launcher.dm:82-102`）。
置信度：HIGH（高）

### FIGHTER-007 — 离舰：边界自动转移；返舰：对接计算机
断言：飞到可登舰/保留 z 边缘的飞行器（`is_near_boundary()`）会自动转移到星图
（`check_overmap_elegibility`）——缩放精灵，将其放置在母星图对象附近，并禁用对接
模式。要返回，驾驶员启用对接计算机的对接模式，并撞击一个 `mass` 更大的友方星图
对象；`transfer_from_overmap()` 将飞行器移动到该舰的某个 `docking_points` 并
重新武装枪械保险。
面向玩家的影响：你通过飞出地图边缘（或先弹射再飞出边缘）来起飞，通过开启对接
计算机并撞击母舰来对接；附带性转移会对对接施加 5 秒冷却，刻意对接则为 20 秒冷却。
证据：
- `fighters_launcher.dm:234-254` `is_near_boundary`；`:284-334`
  `check_overmap_elegibility`（缩放、`flight_pixel_*`、对接冷却 5 秒、
  关闭 docking_mode、跑步机）。
- `:339-398` `docking_act` / `transfer_from_overmap`（需要对接计算机 +
  docking_mode + `mass < OM.mass` + `length(OM.docking_points)`；冷却 20 秒；
  保险 ON）。
- `:228-232` `is_docking_on_cooldown`；`:262-263` `release_maglock`。
- 对接模式 UI 切换：`_fighters.dm:313-321`。
- 管理员强制对接/取消对接：`vv_docking.dm`。
条件：小行星星图对象使用内部加载握手
（`fighters_launcher.dm:347-368`）。
例外 / 覆写：`get_overmap()` 必须非空；对非友方/更轻的飞行器对接受拒。
置信度：HIGH（高）

### FIGHTER-008 — 磁力拦阻器 / 弹射器也能从拖车（地面载具）发射
断言：M575 飞机拖车（`/obj/vehicle/sealed/car/realistic/fighter_tug`）将一架
战斗机装入自身，并可用与弹射器相同的 ±20 速度逻辑将其抛出。
面向玩家的影响：地勤人员可以在机库周围重新布置战斗机，如果弹射器不可用，也可以
从拖车发射它们；拖车也作为发射器出现在空中交通管制员的清单上。
证据：
- `tug.dm:1-27` 拖车类型；`:92-103` `load()`；`:109-120` `hitch()`（磁力锁定、
  停止飞行器处理）；`:158-202` `start_launch`/`finish_launch`（±20）；
  `:204-224` `abort_launch`（若存在弹射器则就近投放）。
- `fighters_launcher.dm:8-15` ATC 控制台 `get_launchers()` 包含
  `fighter_tug`（全部报告 `can_launch_fighters() = TRUE`）。
条件：拖车需要其 `fighter_tug` 钥匙；`emp_act` 可以中止一次发射。
置信度：HIGH（高）

### FIGHTER-009 — 座舱：通行权、座舱罩、大气、弹射
断言：登上战斗机需要匹配的通行权限，且在搭载乘客时需要 `canopy_open`；座舱罩
组件控制大气与弹射。没有座舱罩（或座舱罩破损）时，舱室会返回外部空气并泄漏；
座舱罩受击可能破口。飞行中弹射会被阻止，除非该 z 可登舰/保留或被强制。
面向玩家的影响：保持座舱罩关闭以维持舱内加压（需要制氧机）；打开它以让乘客进出；
座舱罩破口会使舱室失压并可能伤害乘员。
证据：
- `_fighters.dm:756-776` `attack_hand`（通行权限、无现有驾驶员、2 秒爬入、
  打印快捷键帮助）。`:623-649` `MouseDrop_T`（搭载乘客时座舱罩必须打开）。
- `:1959-2005` `slowprocess`/`return_air`/`remove_air` 及座舱罩泄漏 + 视觉效果。
- `:839-846` `canopy_breach`；`:826-837` 座舱罩伤害/乘员伤害。
- `:676-693` `eject`（座舱罩门限；除非强制否则有 z 特性门限）。
- 制氧机分级：`_fighters.dm:1478-1532`（`refill_amount` 1/3/10，等离体变体填充
  等离子）。
条件：`escapepod` 覆写 `stop_piloting`，除非 z 可登舰（除非强制）否则拒绝弹射。
置信度：HIGH（高）

### FIGHTER-010 — 被摧毁时自动将乘员弹射进逃生舱
断言：当战斗机在有人驾驶时被摧毁，它会生成一个 `escapepod`（以最后一名驾驶员
为驾驶员）并将所有人转移到其中；若未配置逃生舱类型，乘员改为受到 200 伤害。
面向玩家的影响：在战斗机中死亡是可能生还的，只要该飞行器装有逃生舱；逃生舱非常慢
（`speed_limit = 2`）但始终可移动且按命令供给燃料。
证据：
- `_fighters.dm:61-77` `Destroy()`；`:712-754` `create_escape_pod`。
- `:458-490` 逃生舱类型（`max_integrity = 500`、`speed_limit = 2`、
  `can_move()=TRUE`、`escape_pod_type = null`、空通行权限）。
- `:707-710` 逃生舱在最后一名乘员弹射后自删除。
条件：`deletion_teleports_occupants` 和 `overmap_deletion_traits`
（`DAMAGE_ALWAYS_DELETES`）支配受击即删除的行为。
置信度：HIGH（高）

### FIGHTER-011 — 武器是默认开启保险的主/副挂载点模块
断言：战斗机获得两个 OSW 挂载点（`fighter/primary`、`fighter/secondary`），其实际
枪械/弹药存于已安装的 `primary`/`secondary` 组件（或
`UTILITY_PRIMARY`/`UTILITY_SECONDARY` 槽位）中。当 `weapon_safety` 为 TRUE
（默认）时 `fire_weapon` 被阻止，驾驶员和炮手都可发射主武器。
面向玩家的影响：新驾驶员必须先关闭枪械保险（舰船动词 / 控制台 / 战斗机控制台）
才能开火；主武器使用弹匣式弹夹，副武器使用导弹/鱼雷。
证据：
- `_fighters.dm:85-90` 保险门限；`:30` `weapon_safety = TRUE`。
- `:1633-1699` `apply_weapons`、`hardpoint_fire`、`hardpoint_get_ammo(_max)`。
- `:1605-1925` 主/副组件类及分级。
- OSW datum：`weapon_datum_types.dm:281-330`（`fighter/primary` 向前开火，
  驾驶员+炮手；`fighter/secondary`）。
- 火炮/激光/轨道炮/鱼雷的差异如编码所示（`_fighters.dm:1743-1925`）。
条件：`primary` 接受 `/obj/item/ammo_box/magazine`（轻型/重型火炮口径）；激光
主武器消耗电池电量（`charge_to_fire = 2000`）；副武器接受导弹/鱼雷；
`bypass_safety` 让某些通用模块无需关闭保险即可开火（加油/维修）。
置信度：HIGH（高）

### FIGHTER-012 — 枪械保险 / 对接 / 弹射可被远程控制
断言：`fighter_controller` 控制台（`req_access = ACCESS_MAA`）按 *初始* 类名列出
战斗机，并可以远程切换保险、切换对接模式和强制弹射——可单独操作，也可对舰侧所有
战斗机全局操作。
面向玩家的影响：军械长可以从舰桥解除武装/冻结或弹射一名失控驾驶员；该控制台按
四个原型类名过滤，因此自制的基类型飞行器（初始名称为“Space Fighter”）不会出现在
其列表中。
证据：
- `control_console.dm:1-9` 类型、访问权限、`valid_filters`（“Only Occupied Ship”、
  “Su-818 Rapier”、“Su-410 Scimitar”、“Su-437 Sabre”）。
- `:21-43` `ui_data`（将 `initial(OM.name)` 与过滤器匹配）。
- `:45-104` `ui_act`（全局/单独切换 + force_eject）。
条件：emag 覆写访问检查（`control_console.dm:15-19,46`）。
置信度：HIGH（高）（“不在列表中”这一后果是对 `initial(OM.name)` 过滤测试的
直接解读）。

### FIGHTER-013 — 战斗机构建：框架 → 有序的 螺栓/接线/焊接/装配 序列 → 完成
断言：`fighter_frame` 是由工具驱动的多阶段组装（扳手 = 上/卸螺栓，焊枪 =
焊/切，螺丝刀 = 上/卸螺丝，钢丝钳 = 拆线，电缆线圈 = 接线），并通过插入组件推进。
进度是一个线性的 `build_state` 计数器（LBS_CHASSIS … LBS_PAINT_DETAILING）。
在已喷漆的框架上用空手完成会生成带有已收集组件类型的输出飞行器。
面向玩家的影响：建造战斗机是一项漫长、严格有序的任务；在某一步用错工具/部件不会
有任何效果；框架会消耗组件（它们被移入框架）并产出一架新飞行器。
证据：
- `construction.dm:22-48` build_states；`:49-78` 框架与 `output_path`。
- `:139-304` `attackby`（按状态插入部件）；`:306-506`
  wrench/welder/screwdriver/wirecutter 动作；`:508-521` `attack_hand` 完成。
- 部件由原型机 `fighter_designs.dm`（框架 + 各等级组件）和货物初始套件
  `packages/cargo/packs.dm:289-347` 提供。
条件：框架不能位于另一架 small_craft 内部（“No Sabreception.”，
`construction.dm:512-514`）。
例外 / 覆写：基础“Light Fighter Chassis”框架的 `output_path` 是 **基础**
`/obj/structure/overmap/small_craft`（“Space Fighter”），而 *不是*
`combat/light`。因此建造出的轻型战斗机使用基础属性（见 FIGHTER-014）。
置信度：序列为 HIGH（高）；基础-vs-Rapier 这点是直接的代码解读。

### FIGHTER-014 — 自制的“轻型战斗机”是基础类型，而非 Rapier
断言：`/obj/structure/overmap/small_craft`（轻型战斗机框架的默认输出）与
`combat/light` 的属性不同：完整度 250、速度上限 7、推力 3.5/3.5/4、角加速度 180、
`damage_states = TRUE`，以及一个空的默认 `components` 列表（因此它只与你安装的
部件一样好）。
面向玩家的影响：原型机/货物建造的轻型战斗机是一个通用机身，装有你能塞进去的
任何东西，而映射/商人出售的 Rapier 是一架调校过的飞行器（完整度 200、速度 10、
角加速度 200），预装全套战斗装备配置。
证据：
- `_fighters.dm:7-59` 基础属性；`:404-428` Rapier 属性 + 组件列表。
- `construction.dm:49-60` 基础框架 `output_path = /obj/structure/overmap/small_craft`。
- `cargo/packs.dm:289-307` “Light Fighter Starter Kit”附带一个基础
  `/obj/structure/fighter_frame`，带有绑装式 1 级部件。
置信度：HIGH（高）

### FIGHTER-015 — 可用/生成的子类型（你实际会遇到的）
断言：地图文件放置了：`combat/light`、`combat/heavy`、`transport/sabre`、
`transport/sabre/mining`、`transport/starter`（Sephora）、`transport/syndicate`、
`transport/sabre/syndicate`、`combat/light/syndicate` 以及 `fighter_frame/*`。
`combat/solgov`（Peregrine）和 `transport/gunship`（Halberd）未在任何地图上放置
（管理员/事件）。
面向玩家的影响：标准舰船配备 Rapier、Scimitar、Sabre（通用 + 采矿）以及一架
Sephora 运输机；采矿船配备 Sabre/mining。
证据：
- 放置示例：`_maps/map_files/Aetherwhisp/Aetherwhisp2.dmm`（light、heavy、sabre、
  sabre/mining）、`Gladius/Gladius1.dmm`、`Tycoon/Tycoon1.dmm`、
  `Hammerhead/Hammerhead.dmm`、`Mining/.../nostromo.dmm`、
  `Galactica/Galactica2.dmm`（`transport/starter`）、辛迪加模板
  （`mako`、`carrier`、`destroyer`、`marine_frigate`）。
- 建造器：`vonneumann/vnmk3.dmm:3017` 战利品表包含 light/sabre/heavy +
  框架。
- 仅商人：`traders_items.dm:264-346`（light 11000、utility 9000、heavy
  15000、judgement 20000、prototype 50000、syndicate 7000）。
- 未映射：`combat/solgov`（`_fighters.dm:430`，描述“adminspawn anyway”）；
  `transport/gunship`（`dropship_types.dm:108`）。
条件：`transport/sabre/syndicate`、`combat/light/syndicate` 限制为
`ACCESS_SYNDICATE`；`start_emagged = TRUE`。
置信度：HIGH（高）

### FIGHTER-016 — 干扰弹：3 次充能、3 团箔条云、对制导弹药 50/50
断言：部署干扰弹消耗一次充能并生成三团箔条云；进入云团的制导弹药（鱼雷/导弹）
有 50% 概率提前引爆，否则失去制导。
面向玩家的影响：战斗机默认携带 3 次干扰弹使用次数（最多可化解约 9 次导弹交互）；
通过插入 `countermeasure_charge`（3）来补充——5 发变体存在，但没有任何设计制造它。
证据：
- `_fighters.dm:1584-1590` 发射器（`max_charges = 3`、`charges = 3`）。
- `countermeasure_ammo.dm:41-57` `fire_countermeasure`（3 团云，相隔 5 ds）。
- `countermeasure_ammo.dm:18-39` 云 `on_entered`（50% 爆炸 / 否则
  `homing = FALSE`）。
- 装填：`_fighters.dm:593-610`；补充物品 `countermeasure_ammo.dm:1-13`；
  设计 `fighter_designs.dm:2-10`；货物 `packs.dm:280-287`。
- 动词：`verbs.dm:97-106` `countermeasure`；HUD 计数 `_fighters.dm:143-145`。
置信度：HIGH（高）

### FIGHTER-017 — 战斗机 FTL 是一种需要牵引的短程火炬驱动器
断言：`ftl` 组件（`class II torch drive`，槽位 `HARDPOINT_SLOT_FTL`）在
`spoolup_time = 120` 秒内蓄能，消耗能量，并且只有当飞行器 *不* 已在另一星图对象
内部时才能跳跃；目的地必须是驱动器“锚定（anchored）”到的恒星系（其
`anchored_to`，在安装时或接近信标时设置）。Class III（tier2）将 `max_range`
从 50 扩展到 200。
面向玩家的影响：通用/运输飞行器获得一个需 2 分钟蓄能的微型跳跃驱动器，必须牵引
到有效目标；它不是免费的“回家”——返回跳跃 UI 动作受限于状态 READY + 活跃牵引
+ 非空的目的地星系。
证据：
- `modules/ftl_drive.dm:1-93` 整个文件（`spoolup_time`、`max_range`、
  `jump()`、`process()`、`on_install` 设置 `OM.ftl_drive`）。
- `_fighters.dm:349-384` `toggle_ftl` / `anchor_ftl` / `return_jump` UI 动作。
条件：若 `linked.get_overmap()`（你在舰船内部）或 z 非星图/保留，`jump()` 拒绝。
置信度：HIGH（高）

### FIGHTER-018 — 运输机带内部空间；战斗机没有
断言：`transport/*` 小型飞行器使用 `interior_mode = INTERIOR_DYNAMIC` 以及一个
`possible_interior_maps` 模板；登上它们会加载整个内部区域（`/area/dropship`），
带有自己的虚拟 z，且 `enter()` 将玩家移动到内部入口点而非座舱格。普通战斗机没有
内部空间，直接将驾驶员放入对象内部。
面向玩家的影响：Sabre/Trafalgar/Sephora 是可走入的飞行器，带有乘员空间；
Rapier/Scimitar 没有内部空间（max_passengers 0 = 一名乘员）。
证据：
- `dropship_types.dm:4-77` 运输类型 + `post_load_interior` + 虚拟 z；
  `:86-208` enter/exit/attack_hand/parallax。
- `dropship.dm` 内部地格、`/area/dropship`、加速椅；入口地格 `Bumped` 触发
  `exit()`。
- 基础战斗机 `max_passengers = 0`，`enter()` = `user.forceMove(src)`
  （`_fighters.dm:657-666`）。
条件：运输机准入同样遵循装备配置（在 `MouseDrop_T` 中首先调用相同的组件加载流程）。
置信度：HIGH（高）

### FIGHTER-019 — ATC 控制台和机库发射台是舰船的飞行甲板接口
断言：`fighter_launcher` 计算机（Mag-cat 控制台）扫描自身区域内的发射台 / 拖车，
列出它们磁力锁定的战斗机 + 驾驶员，并可以发射 / 释放 / 向驾驶员发送直接消息。
发射台被放置为朝向 北/南/东/西，并相应地设置发射向量；它们也充当拦阻器。
面向玩家的影响：一名发射官（通常是军械长 / 军需）从控制台发射战斗机；发射台既能
捕获返回的飞行器，也能抛出离开的飞行器。
证据：
- `fighters_launcher.dm:1-65` 控制台（`get_launchers` 扫描区域）。
- `on_entered`/`finish_launch` 中的方向处理（`:134-207`）。
- `linkup` 中的发射台方向/航点（`:213-226`）。
置信度：HIGH（高）

## 飞行器与模块目录

### 飞行器（small_craft 子类型）
| 飞行器 / 路径 | 定位 | 关键属性 | 获取方式 |
|---|---|---|---|
| `small_craft`（基础） | 通用自制机身 | hp 250，spd 7，thr 3.5/3.5/4，ang 180，空部件 | Light Fighter 框架（原型机 `light_frame`，货物“Light Fighter Starter Kit”） |
| `combat/light` “Su-818 Rapier” | 太空制空战斗机 | hp 200（×装甲等级），spd 10，ang 200，全套战斗装备 | 已映射（多数舰船），商人 11000 |
| `combat/heavy` “Su-410 Scimitar” | 反主力舰重型 | hp 300，spd 8，thr 8/8/7.75，ang 80，鱼雷+重型火炮 | 已映射，商人 15000 |
| `combat/solgov` “Peregrine” | 护盾战斗机 | hp 125，spd 10，ang 200，tier6 护盾，激光 | 仅管理员/事件 |
| `combat/light/syndicate` | 辛迪加 Rapier | 同 Rapier，`ACCESS_SYNDICATE`，已 emag | 已映射（辛迪加地图），商人 7000 |
| `combat/light/judgement` “AX-49” | 商人高级轻型 | tier2 引擎/装甲/电池，tier2 座舱罩 | 商人 20000 |
| `combat/light/prototype` “SU-148” | 轨道炮制空 | tier3 油箱/装甲/引擎，轨道炮 | 商人 50000 |
| `transport` “Su-624 Trafalgar” | 部队运输机 | hp 1000，spd 4，内部空间 | 已映射（Galactica） |
| `transport/starter` “NSV Sephora” | 开局运输机 | 同 transport，固定名称 | 已映射（Galactica/Tycoon/Gladius） |
| `transport/sabre` “Su-437 Sabre” | 通用/补给 | hp 250，spd 6，thr 5，通用槽位，FTL+加油 | 已映射，商人 9000 |
| `transport/sabre/mining` | 采矿通用 | 同 Sabre，采矿权限 | 已映射（采矿/若干舰船） |
| `transport/sabre/syndicate` | 辛迪加登舰飞行器 | 同 Sabre，辛迪加 | 已映射（辛迪加地图） |
| `transport/syndicate` | 辛迪加运输机 | 同 transport，辛迪加 | 已映射（辛迪加地图） |
| `transport/gunship` “SC-130 Halberd” | 炮艇 | 运输机身 + 主武器/鱼雷/VLS/高斯/PDC | 仅管理员/事件 |
| `escapepod` | 救生艇 | hp 500，spd 2，始终可移动 | 战斗机被摧毁时自动生成 |

### 模块（`/obj/item/fighter_component`）——槽位与效果
| 模块 | 槽位 | 效果（分级者按等级列出） |
|---|---|---|
| `engine` | Engine | 设置 速度/推力/角加速度 ×tier；t1/t2/t3；移动所必需 |
| `fuel_tank` | Fuel Tank | 容量 1000/2500/4000（+运输机 3000） |
| `apu` | APU | 每 4 秒将引擎转速提升 +500×tier；控制燃料管线；t1/t2/t3 |
| `battery` | Battery | 10k/20k/40k 电量；为模块供电；激光弹药 |
| `armour_plating` | Armour | 第二层 HP 池 + 设置机身最大值 = 初始值×tier；t1/t2/t3；t6 = 护盾 |
| `canopy` | Canopy | 保持舱室加压；完整度 100/200/350/450；重量带来阻力 |
| `oxygenator` | Atmospheric Regulator | 补充舱内 O2（每刻 1/3/10）；等离子变体 |
| `targeting_sensor` | （建造/装饰） | 必需部件；无槽位动作 |
| `avionics` | （建造/装饰） | 必需部件；无槽位动作 |
| `docking_computer` | Docking Module | 切换对接模式；冷却状态 |
| `countermeasure_dispenser` | Countermeasure | 3 次充能；部署 3 团箔条云 |
| `ftl` | FTL | class II（range 50）/ class III（range 200）；120 秒蓄能 |
| `primary/cannon`（20mm Vulcan） | Primary / Utility Primary | 点射 2，0.25 秒；弹夹弹匣 |
| `primary/cannon/heavy`（30mm） | Primary / Utility Primary | 点射 3，0.5 秒，重量 2 |
| `primary/laser` “Stinger” | Primary | 点射 3，10 秒 cd，2000 电池/发，重量 3 |
| `primary/plasmacutter`（217-A） | Utility Primary | 点射 1，0.5 秒，2500 电池/发；采矿射弹 |
| `primary/utility/refuel` | Utility Primary | 空对空加油 + 电池跨接启动；范围 10，速率 50 |
| `primary/utility/repairer` | Utility Primary | 使用机身修复液进行空对空维修；分级 |
| `secondary/ordnance_launcher`（导弹架） | Secondary / U-Secondary | 5/10/15 枚导弹；t3 点射 2 |
| `secondary/ordnance_launcher/torpedo` | Secondary | 2/4/10 枚鱼雷；t3 点射 2，重量 2 |
| `secondary/ordnance_launcher/railgun` | Secondary | 10 发弹丸，重量 1 |
| `secondary/utility/hold` | Secondary Utility | 货舱，max_freight 5/10/20 |

## 跨系统依赖

- 系统 9/10（瞄准/武器与弹药）：战斗机通过 OSW datum
  `fighter/primary`/`fighter/secondary`（`weapon_datum_types.dm:281-330`）开火，
  它们委托给 `hardpoint_fire` → 组件的 `fire()`。弹药类型是共享的舰船弹药
  （轻型/重型火炮弹匣、导弹、鱼雷、轨道炮弹丸）。
- 系统 11（点防御）：PDC / AMS 逻辑通用地瞄准星图对象；战斗机是有效的星图对象，
  会被其碰撞处理捕获（`physics.dm:449`），但针对战斗机的 PDC 专属瞄准并未在
  fighters 文件夹中实现——请在系统 11 中核实。
- 系统 14（大气）：战斗机暴露 `return_air`/`remove_air`/`assume_air` 及自己的
  `cabin_air`；制氧机和座舱罩与舱内压力交互（FIGHTER-009）。运输机内部空间是
  带有自身大气的完整区域。
- 系统 17（登舰）：`transport/*` 携带内部空间（`INTERIOR_DYNAMIC`），用于前沿
  作战基地/部队投送；`enter()`/`exit()` 以及运输机入口地格是接口。辛迪加
  Sabre/Trafalgar 是进攻方的飞行器。
- 系统 25（职业）：`/datum/job/pilot`（`pilot.dm`，4 个名额）持有
  `ACCESS_COMBAT_PILOT`、`ACCESS_TRANSPORT_PILOT`、`ACCESS_HANGAR`，由军械长
  监督；`fighter_controller` 需要 `ACCESS_MAA`。代码中没有独立的“战斗机技师”
  职业类型——军需/货物填补该角色。（仅为注记。）
- 系统（机身与护盾）：战斗机 OSW 伤害路径经由星图伤害模型；tier6 装甲添加一个
  `/datum/component/overmap_shields`。

## 待解问题

1. 舰船到星图及返回使用 `last_overmap`/`overmap_deletion_traits`，带有大量
   “若此处出错就查这里”的注释（`fighters_launcher.dm:275-334`）；
   围绕 `DELETE_UNOCCUPIED_ON_DEPARTURE` 的确切边界情况未被完全追踪。
2. 是否有任何东西建造或放置 5 发的 `countermeasure_charge/five`？未找到设计；
   除非管理员生成，否则很可能是无用内容。
3. `throw_pilot`（`_fighters.dm:806-811`）被标记为未使用；未找到调用点
   ——请更广泛地搜索以确认没有存活的调用者。
4. 燃料“可飞行分钟数”由约 0.7 秒的 `slowprocess` 节奏推导；真实节奏取决于基础
   `process()` 的调度，未在 SSticker/SSobj 时序中重新核实。
5. 运输机内部模板（`_maps/templates/boarding/*.dmm`）未打开；内部尺寸/布局超出
   本文范围。
6. 点防御和登舰的 *具体* 交互未被彻底追踪到（推迟到系统 11 和 17）。
