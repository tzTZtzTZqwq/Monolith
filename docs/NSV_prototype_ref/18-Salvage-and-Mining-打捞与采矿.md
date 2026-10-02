> 本文为 research/evidence/salvage_mining.md 的中文翻译。

# 打捞、采矿与小行星作业

系统 #18。范围：获取原材料与夺取舰船。打捞计算机的 EWAR/点防御用途归系统 11/17
所有（`point_defence.md`、`overmap.md`）；本文覆盖材料获取与夺舰这一侧。

所有事实均追溯自源代码；引用了 文件:行号。代码优先于记忆。

## 系统概述

两项基本相互独立的活动共享该部门：

1. **材料获取（采矿）。** 星图在恒星系中生成 `asteroid` 对象。**采矿舰**（一艘
   独立的、配齐全员的玩家舰船）使用 **矿物磁铁** 或 **对接小型飞行器** 将一颗
   小行星变成为一个可开采的岩石内部空间，随后普通的镐/钻采矿产出矿石，矿石在
   **主舰** 上被加工成板材/合金（采矿船体自身没有矿石加工能力）。
2. **夺舰（打捞）。** **打捞计算机**（“EWAR telemetry scrambler”）不会将舰船
   拆解为材料。它 *禁用* 一艘受损足够严重的敌舰的 *点防御与 AI*，以便突击队可以
   登上它、杀死船员，并通过 **IFF 控制台** 翻转其 IFF 来夺取它。

NSV13 的采矿基于星图。上游的 Lavaland/太空废墟机械（`code/modules/mining/*`、
`_maps/RandomRuins`）仍然存在，但在正常回合中 **不会被加载**（见 SITES-001）。

## 核心玩法循环

**采矿循环**（已证实）：恒星系初始化生成 4–7 颗小行星
（`starsystem.dm:960`）→ 采矿 DRADIS 控制台渲染 `required_tier` ≤ 控制台传感器
等级的小行星，带颜色编码（`radar.dm:385-397`）→ 采矿舰接近，**矿物磁铁** 将一颗
合格的小行星拉入距舰 5 格以内至舰船的 小行星笼 地标（`asteroid.dm:287-308`），
或者一艘小型飞行器 **对接** 进入小行星并加载其内部空间
（`fighters_launcher.dm:347-355`）→ 船员用带有 `TOOL_MINING` 的物品开采
`turf/closed/mineral` 岩石，每块岩石掉落 3 个矿石（`minerals.dm:82-93`）→ 矿石
被运送到 **主舰** 的 ORM/熔炉（`ore_redemption` / `processing_unit`）→ 板材与
合金（durasteel、duranium、nanocarbon glass）→ 原型机 / 弹药 / 工程
（`smelting_designs.dm`、`*_construction.dm`、`stormdrive.dm`）。

**夺取循环**（已证实）：将一艘敌方星图舰船打至低于其初始完整度的 50% → 打捞
控制台“Establish Hammerlock”（`salvage.dm:75-108`）加载目标的登舰内部空间并
禁用其 AI + 点防御 → 突击队乘运输机登舰、击杀防守者 → **IFF 控制台** 破解翻转
`OM.faction` 并触发 `COMSIG_SHIP_BOARDED`（`iff_console.dm:124-140`）→ 当 faction
== 船员的起始阵营时 `board_ship` 目标完成（`board_ship.dm:60-66`）→ 控制台
“Release Hammerlock”删除登舰层级（`salvage.dm:110-132`）。

## 机制

### SALVAGE-001 — 打捞控制台的身份与文件
断言：打捞计算机是 `Seegson model EWAR telemetry scrambler`
（`/obj/machinery/computer/ship/salvage`），由
`/obj/item/circuitboard/computer/salvage` 建造。它是代码库中 *唯一* 的打捞机器；
不存在舰船回收机器。
面向玩家的影响：“打捞”一艘舰船意味着登上它，而不是为了材料拆解它。
证据：`nsv13/code/game/machinery/computer/salvage.dm:1-27`；`git grep -i salvage`
未显示其他材料打捞机器。
条件：电路板没有原型机/压印机设计（仅限地图放置）；存在于采矿船体上
（`_maps/map_files/Mining/nsv13/Rocinante.dmm`）。
例外 / 覆写：物种/变体 `/console` 仅图标子类型在 `custom_computers.dm:10-15`。
置信度：HIGH（高）

### SALVAGE-002 — 50% 伤害门限（`required_damage_percentage`）
断言：只有当 `obj_integrity * 100 / initial(obj_integrity) <= 50` 时，目标才能被
hammerlock。
面向玩家的影响：你必须先将敌方船体打至其起始 HP 的一半；一艘完好的舰船不能被打捞。
证据：`salvage.dm:8`（`required_damage_percentage = 50`）、`salvage.dm:93`
（若完整度% `> 50` 则拒绝）。
条件：分母是 `initial(OM.obj_integrity)`（类型默认值），而非实时的
`max_integrity`。该值是每个控制台的变量（非 config）。
例外 / 覆写：未找到任何；代码树中没有任何地方覆写
`required_damage_percentage`。
置信度：HIGH（高）

### SALVAGE-003 — 冷却为全局，而非每控制台
断言：`can_salvage` 是一个 `static` 变量，因此启动/关闭冷却由服务器上每台打捞
控制台共享。成功锁定 = 5 分钟；锁定失败或释放 = 2.5 分钟。
面向玩家的影响：若任何一台控制台处于冷却中，回合中的其他每台控制台都会报告
“equipment is starting up or shutting down”。
证据：`salvage.dm:10`（`var/static/can_salvage = TRUE`）、`:11`
（`salvage_cooldown = 5 MINUTES`）、`:101/108/132` 定时器重置
（`salvage_cooldown` 或 `/2`）。
条件：`addtimer(VARSET_CALLBACK(src, can_salvage, TRUE), ...)` 操作的是共享的
类变量。
例外 / 覆写：无。
置信度：HIGH（高）

### SALVAGE-004 — `max_salvage_range` 是死代码
断言：`max_salvage_range = 20` 已声明但从未被读取。
面向玩家的影响：不存在注释所暗示的 20 格范围限制；真正的约束是 同 Z +
传感器可见性（见 SALVAGE-009）。
证据：`salvage.dm:7` 声明；`git grep max_salvage_range` 仅返回该声明。
条件：不适用。
例外 / 覆写：不适用。
置信度：HIGH（高）

### SALVAGE-005 — 采矿变体仅改变无线电频道
断言：`/obj/machinery/computer/ship/salvage/mining` 覆写 `radio_key`/`radio_channel`
为 cargo/supply 及其自己的电路板；所有逻辑均原样继承。
面向玩家的影响：采矿船体获得一台打捞控制台，其警报发往 Supply 频道而非 Security
频道。它没有额外的采矿功能。
证据：`salvage.dm:16-19`；存在于 `_maps/map_files/Mining/nsv13/Rocinante.dmm`。
条件：不适用。
例外 / 覆写：不适用。
置信度：HIGH（高）

### SALVAGE-006 — “Establish Hammerlock” 对目标实际做了什么
断言：对有效目标，控制台：(a) 清除 `DAMAGE_DELETES_UNOCCUPIED` 以让 simplemob
不会自动删除船体；(b) 调用 `OM.ai_load_interior(linked)`，它将目标的登舰地图加载
到 *登舰者* 的保留 Z 层级上并设置 `OM.hammerlocked = TRUE`；(c) 设置
`OM.ai_controlled = FALSE`（AI 停止飞行/作战）；(d) 记录
`linked.active_boarding_target = OM`。
面向玩家的影响：目标的点防御关闭，其 AI 在锁定期间冻结；登舰内部空间被实例化，
以便陆战队可以与船员战斗。
证据：`salvage.dm:98-103`；`interiors.dm:74-116`（`ai_load_interior`、
`hammerlocked = TRUE`、`SL.linked_overmap = src`）；`overmap.dm:255,257`。
条件：AI 舰船需要能够 `ai_load_interior` 的 `interior_mode`
（`interior_mode` 必须不是 `NO_INTERIOR`/`INTERIOR_DYNAMIC`）；若预检查失败，
`ai_load_interior` 返回 FALSE 且控制台中止。
例外 / 覆写：`ghost_controlled` 舰船始终被拒绝
（`salvage.dm:83-85`）——“unable to achieve positive lock”。
置信度：HIGH（高）

### SALVAGE-007 — 释放 hammerlock 会杀死仍在上面的所有人
断言：“Release Hammerlock” 调用
`active_boarding_target.kill_boarding_level(linked)` 并恢复删除特性。
`kill_boarding_level` 清空登舰内部空间的每个地格并释放保留 Z。
面向玩家的影响：在登舰者仍在目标内部时释放会在他们脚下删除该层级（TGUI 提示：
“any forces boarding it will be killed”）。
证据：`salvage.dm:110-132`；`interiors.dm:8-53`（登舰内部空间上的 `T.empty()`
循环）。
条件：若有东西（例如 board_ship 目标）将目标标记为任务关键，则会被
`COMSIG_SHIP_RELEASE_BOARDING` 阻止（`salvage.dm:120-122`、
`board_ship.dm:68-72`）。
例外 / 覆写：一个确认弹窗（“ALL BOARDERS WILL BE KILLED”）对释放加以门限
（`salvage.dm:123-124`）。
置信度：HIGH（高）

### SALVAGE-008 — 夺取舰船 = 翻转其 IFF，而非吸收它
断言：一艘被登舰的舰船通过用多功能工具破解其 IFF 控制台（控制台必须为 `EMAGGED`）
来夺取；`hack()` 发出 `COMSIG_SHIP_BOARDED` 并翻转阵营 syndicate↔nanotrasen
（pirate→nanotrasen）。当 `target_ship.faction == mode.starting_faction` 时
`board_ship` 目标得到满足。
面向玩家的影响：“夺取” = 将船体变为友方；它不会被加入玩家舰队，也不给予直接的
板材/点数奖励。战利品就是舰上船员/物件所提供的任何东西。
证据：`iff_console.dm:79-95,124-163`；`board_ship.dm:60-66`；
`ai-skynet.dm:807`（舰队在登舰时放弃该舰）。
条件：登舰内部空间在一个随机 `iffloc` 生成一个 IFF 控制台
（`interiors.dm:207-220`）；在 AI 目标舰船上它是预先 emag 的
`/obj/machinery/computer/iff_console/boarding` 子类型。
例外 / 覆写：将一艘 `MAIN_OVERMAP` 蓝色舰船破解为 syndicate 会生成一支 SolGov
拦截舰队（`iff_console.dm:138-156`）——这是一个陷阱，而非夺取。
置信度：HIGH（高）

### SALVAGE-009 — 目标资格过滤器
断言：控制台只列出满足以下条件的星图对象：与控制台所在舰船同一 Z、
`interior_mode == INTERIOR_EXCLUSIVE`、被传感器以高于 `SENSOR_VISIBILITY_FAINT`
的级别可见、且尚未是活跃目标。
面向玩家的影响：小行星、友方/幽灵接触物，以及没有登舰内部空间的舰船永远不会
作为打捞目标出现。你必须与目标处于同一星图“平面”（Z）。
证据：`salvage.dm:59-63`。
条件：`linked` 通过 `get_overmap()` 解析；控制台必须位于 `ZTRAIT_OVERMAP` 层级
（`salvage.dm:72`）。
例外 / 覆写：无。
置信度：HIGH（高）

### MINING-001 — 采矿舰是一艘独立的、配齐全员的玩家舰船
断言：“采矿舰”是一个星图船体（`role = MAIN_MINING_SHIP`），在回合开始时由
`instance_overmap` 加载其自己的内部地图文件，赋予它真实的 `occupying_levels`
及 `is_player_ship() == TRUE`。它按地图通过 `mining_ship_type` + `mine_file`
选择。
面向玩家的影响：采矿部门生活在自己的舰船上（舰桥、机库、采矿码头、医疗舱、科学），
而非主船体上。它与主舰一起被移入模式的起始星系。
证据：`miningships.dm:1-49`（Rocinante/Nostromo/Hephaestus/Rig 变体）；
`mapping.dm:314-315`；`overmap.dm:263-310`；`overmap_mode.dm:197-204`；
地图配置 `_maps/*.json`（`mining_ship_type`、`mine_file`、`mine_path`）。
条件：`config.mine_disable` 可完全阻止加载（`map_config.dm:23`）；`vago.json`
使用小型的 `nostromo/fob`（FOB_Shuttle.dmm）。
例外 / 覆写：`main_overmap` 角色是独立的（`MAIN_OVERMAP`）。
置信度：HIGH（高）

### MINING-002 — 采矿船体没有矿石加工
断言：没有任何采矿舰地图（`Rocinante`、`nostromo`、`rig`、`FOB_Shuttle`）包含
ORM、熔炉（processing_unit）、矿石筒仓或原型机。这些机器只存在于主玩家舰船上。
面向玩家的影响：矿石必须被运送到主舰的 ORM/熔炉；采矿船体是一个采矿/采掘平台，
而非精炼厂。
证据：`ore_redemption` 出现在 17 张地图中，没有一张位于
`_maps/map_files/Mining/nsv13/` 下（Grep）；对 `Rocinante.dmm` 的机械普查仅显示
`mineral_magnet`、`dradis/mining`、`salvage/mining`、镐、矿石箱、采矿售货机、
EVA 服、2× `sabre/mining` 飞行器。
条件：舰↔舰转移通过穿梭机码头（MINING-00x/待解问题）。
例外 / 覆写：带 ORM 的主舰包括 Gladius、Galactica、Atlas、Eclipse、Aetherwhisp、
Hammerhead、Vago、Tycoon、Snake、Serendipity、vonneumann 以及若干实例化船体。
置信度：HIGH（高）

### MINING-003 — 采矿 DRADIS 控制台 + 小行星传感器升级
断言：`/obj/machinery/computer/ship/dradis/mining` 设置 `show_asteroids = TRUE`。
只有当 `asteroid.required_tier <= mining_sensor_tier`（默认 tier 1）时，它才会
将小行星渲染为光点，颜色为 棕色（t1）/ `#ffcc00`（t2）/ `#cc66ff`（t3）。
`/obj/item/mining_sensor_upgrade`（tier 2）和 `/max`（tier 3）在插入时提升等级。
面向玩家的影响：默认情况下矿工只在地图上看到/打捞 1 级（含铁）小行星；科学部必须
先建造传感器升级，2/3 级小行星才会出现。
证据：`radar.dm:19,179-184,316-328,385-397`。
条件：传感器升级是研究设计 `asteroidscanner` / `asteroidscanner2`
（`asteroid.dm:43-61`），门禁于科技树节点 `mineral_nonferrous`（7500 点）和
`mineral_exotic`（12500 点）。
例外 / 覆写：任何 dradis 都可升级；`dradis/mining` 只是初始就显示小行星。
置信度：HIGH（高）

### MINING-004 — 矿物磁铁将小行星拉上舰船
断言：`/obj/machinery/computer/ship/mineral_magnet`（权限 `ACCESS_MINING`）
通过与舰船共享同一星图对象的 `asteroid_spawn` 地标找到其笼子（`LateInitialize`）。
“Pull in asteroid” 列出 `orange(5, linked)` 中 `required_tier <= tier` 的小行星，
然后在 10 秒延迟后，将一张随机小行星地图模板连同所选岩石的 `core_composition`
加载进笼子。
面向玩家的影响：磁铁是主循环——接近一颗小行星直至其在 5 格内，将其拉上舰，然后
在舰船笼子中开采生成出的岩石。
证据：`asteroid.dm:196-311`；地标 `asteroid.dm:161-170`；舰船地图中的笼子地标
（`Rocinante.dmm:1558`）。
条件：磁铁 `tier` 默认为 1；`cooldown` 阻止重复使用 1 分钟
（`asteroid.dm:299-300`）；有 20% 概率加载 `ruins/` 模板，80% 概率加载普通
`asteroids/` 模板（`:301-306`）；星图小行星对象在拉取时被 `qdel`（`:308`）。
例外 / 覆写：`.../stupidfuckingbabyaetherwhispmagnetvariant...` 使用
`/turf/open/floor/plating/airless` 作为 Aetherwhisp 验证甲板的重置地格
（`asteroid.dm:209-210`）。
置信度：HIGH（高）

### MINING-005 — 磁铁等级门限 + 深层核心升级
断言：小行星等级：基础 `asteroid` = tier 1（核心：铁、钛）；`asteroid/medium` =
tier 2（铜、银、金、等离子）；`asteroid/large` = tier 3（钻石、铀、bscrystal）。
磁铁只拉取 `required_tier <= tier` 的小行星。`/obj/item/deepcore_upgrade`
（tier 2）和 `/max`（tier 3）在插入时提升磁铁等级。
面向玩家的影响：默认磁铁只能拉取含铁小行星；非铁与异域小行星需要科学部建造的
深层核心模块。
证据：`asteroid.dm:63-100,172-234`；设计 `deepcore1`/`deepcore2`
（`asteroid.dm:23-41`）。
条件：与 MINING-003 相同的科技树节点（`mineral_nonferrous` /
`mineral_exotic`）。
例外 / 覆写：插入更低等级的模块会被拒绝（“already upgraded to a higher tier”）。
置信度：HIGH（高）

### MINING-006 — 将小行星推出会重置笼子
断言：“Push away asteroid” 运行 30 秒倒计时（1 分钟磁铁冷却），然后删除该小行星
受影响地格中每个非 mob 原子，并将它们重置为磁铁的 `turf_type`（默认为太空）。
面向玩家的影响：在拉取新小行星前清除残留的岩石/战利品；仍留在笼子内的任何人都会
失去那些地格上的东西。
证据：`asteroid.dm:313-332`。
条件：确认弹窗；通过 Topic 进行 `Adjacent(usr)` 检查。
例外 / 覆写：mob 和 `asteroid_spawn` 地标被保留。
置信度：HIGH（高）

### MINING-007 — 小行星作为星图对象
断言：星图小行星有 100 完整度、装甲 100/100/25，以及
`overmap_deletion_traits = DELETE_UNOCCUPIED_ON_DEPARTURE | DAMAGE_DELETES_UNOCCUPIED
| DAMAGE_STARTS_COUNTDOWN | FIGHTERS_ARE_OCCUPANTS`、`deletion_teleports_occupants
= TRUE`。`weapon_addition_allowed = FALSE` 且 `apply_weapons()` 为空操作；尝试
驾驶一颗会立即停止驾驶。
面向玩家的影响：小行星是可摧毁的景物，被遗弃时会自删除，且不能被武装或驾驶。
证据：`asteroid.dm:63-82`；`overmap.dm:28-29`。
条件：初始化时每个星系放置 4–7 颗小行星（`starsystem.dm:960`）；星系可被标记
`STARSYSTEM_NO_ASTEROIDS`（例如 Staging）。
例外 / 覆写：阵营变化会刷新星系的小行星（`factions.dm:91`）。
置信度：HIGH（高）

### MINING-008 — 小行星内部有什么（磁铁与对接不同）
断言：两条路径，来源列表相同但概率不同。
- **磁铁拉取**：20% `_maps/map_files/Mining/nsv13/ruins/`，80% `.../asteroids/`
  （`asteroid.dm:301-306`）。
- **小型飞行器对接 / `choose_interior`**：33% `ruins/`，67% `asteroids/`
  （`asteroid.dm:111-116`）。
两者都叠加一个矿物 **核心**：距中心 `rand(4,6)` 内的每个地格有 80% 概率被设为
来自该小行星 `core_composition` 的随机地格。
证据：`asteroid.dm:108-159`、`map_template/asteroid/load`。
条件：`asteroids/` 地图是纯岩石（`turf/closed/mineral/random` + 无气镀层）；
`ruins/` 地图是结构/前哨（机械、战利品，有时有敌对 mob——例如 `mining5` 有
香蕉岩堆与一座残破设施，`mining14` 有一个带镐/GPS 的生存舱，`mining20` 有丛林
植物群与一只鹦鹉，其他包含 `hostile/carp`、`hostile/bear`、
`hostile/skeleton/eskimo` 等）。
例外 / 覆写：磁铁变体传入 `magnet_load = TRUE`，跳过不可摧毁的
`boarding_cordon` 边界地格（`asteroid.dm:147`）；对接的小行星会获得封锁线。
置信度：HIGH（高）

### MINING-009 — 将小型飞行器对接到小行星
断言：处于对接模式的小型飞行器（战斗机/运输机/穿梭机）与一颗小行星相撞时，会触发
`AS.interior_mode = INTERIOR_DYNAMIC`，排队内部加载，并在完成时将飞行器的乘员
转移到小行星内部空间。
面向玩家的影响：`sabre/mining`（或任何小型飞行器）可以直接飞到一颗小行星并进入
其中采矿，而不使用母舰磁铁。
证据：`fighters_launcher.dm:339-358`。
条件：要求飞行器有 FTL/对接计算机且对接模式开启。
例外 / 覆写：若小行星内部空间已为 `INTERIOR_READY`，乘员立即转移。
置信度：HIGH（高）

### MINING-010 — 每块岩石的矿石产出
断言：用带有 `TOOL_MINING` 的物品开采 `turf/closed/mineral` 会生成
`new mineralType(src, mineralAmt)` 矿石物品并 `ScrapeAway` 该岩石。
- `turf/closed/mineral/random`（通用岩石）：有 13% 概率（`mineralChance`）出矿；
  若选中一种矿石路径则 `mineralAmt = rand(1,5)`；`gibtonite` 改为变成一个特殊的
  地格。
- 手工放置的单一矿物地格（核心）：`mineralAmt` 默认为 **3**。
面向玩家的影响：特定矿物的核心岩石每格产出 3 个矿石；通用岩石大多数时候产出 0，
出矿时产出 1–5。
证据：`minerals.dm:18-19,82-93,139-171,235-345`；矿石价值在 ORM
`machine_redemption.dm:21`。
条件：开采每块岩石耗时 `40 * toolspeed`（标准镐为 40 ds）
（`minerals.dm:69-74`）。
例外 / 覆写：`gibtonite` 的 `mineralAmt = 1` 但会引爆（MINING-012）。
置信度：HIGH（高）

### MINING-011 — Gibtonite 危险
断言：`turf/closed/mineral/gibtonite`（在通用岩石的加权表中的生成权重为 4）若在
未敲击状态下被开采会引爆：`rand(8,10)` 刻 × 5 ds 的倒计时（约 4–5 秒），以
`explosion(turf, 1, 3, 5)` 结束。可以用采矿扫描仪/高级扫描仪拆除。
面向玩家的影响：清理通用岩石时偶尔会遇到爆炸性岩石；拆掉它，否则它会炸出一个洞
（并可能通过附近的爆炸触发连锁）。
证据：`minerals.dm:415-484`；生成权重在 `minerals.dm:140-143`。
条件：仅出现在通用岩石/低概率表中；不属于任何 `core_composition`，因此小行星
核心是安全的。
例外 / 覆写：仅当 z==5 时抑制管理员通知。
置信度：HIGH（高）

### MINING-012 — 小行星伤害/删除自身（在战斗中采矿的危险）
断言：对一颗带有 `DAMAGE_DELETES_UNOCCUPIED` 且无乘员的小行星施加 `take_damage`
会删除它；否则它会进入上层结构临界状态。`has_occupants()` 因
`FIGHTERS_ARE_OCCUPANTS` 而将战斗机计入。离开最后一名乘员会通过
`DELETE_UNOCCUPIED_ON_DEPARTURE` 触发删除。
面向玩家的影响：你不能通过将一架战斗机长时间停在其中来“保住”一颗小行星——船体
可能在你周围被摧毁，且被采空的空小行星会消失。
证据：`weapons/damage.dm:101-134`；`fighters_launcher.dm:326-333`。
条件：`DAMAGE_DELETES_UNOCCUPIED` 被打捞控制台在 *舰船* 上临时清除，但不在
小行星上。
例外 / 覆写：`deletion_teleports_occupants = TRUE` 会在删除时重新安置其中的
任何人而非杀死他们。
置信度：HIGH（高）

### MINING-013 — FTL 事故小行星碰撞
断言：一次失败的 FTL 跳跃可能在舰船内部的一个随机地格实体化一颗小行星（核心：
铁、钛、等离子、bscrystal），并压死封闭地格内的任何 mob。碰撞概率 =
clamp(25 + 5×asteroids_in_system, 25, 90) %。
面向玩家的影响：跳跃穿过富含小行星的星系有风险，会带来一颗舰内小行星压死船员
——但也会在舰内掉落免费矿石。
证据：`ftl_jump_mishaps/jump_mishap_helpers.dm:40-46,209-232`。
条件：仅作为 FTL 驱动器操作失误的一部分触发。
例外 / 覆写：无。
置信度：HIGH（高）

### PROCESS-001 — 矿石兑换机（仅在主舰上）
断言：`/obj/machinery/mineral/ore_redemption` 将矿石熔炼成板材
（`sheet_per_ore` 默认 1，可通过材料仓升级）并累积 `points`
（每矿石价值，例如 铜 5 … 钻石 50，×`point_upgrade`）。
面向玩家的影响：送达 ORM 的矿石变为可用的板材以及可在采矿售货机兑换的采矿点数。
证据：`machine_redemption.dm:4-79`。
条件：需要 `ACCESS_MINERAL_STOREROOM`；使用一个远程材料组件（通常链接到一个
矿石筒仓）。
例外 / 覆写：无。
置信度：HIGH（高）

### PROCESS-002 — 熔炉（“furnace”）合金
断言：`/obj/machinery/mineral/processing_unit`（+ 控制台）持有一个材料容器，并
通过控制台的 “Alloy” 动作将精炼材料转换为合金设计（`build_type & SMELTER`）。
面向玩家的影响：工程/弹药部门可以用精炼材料自行合金化他们的 durasteel 等，
无需原型机。
证据：`machine_processing.dm:141-152,194-240`；ORM 也暴露 SMELTER 设计
（`machine_redemption.dm:259`）。
条件：速率受 `smelt_amount` 限制（基础 5/秒，可由材料仓升级）。
例外 / 覆写：`laborcamp` 子类型禁用点数兑换。
置信度：HIGH（高）

### PROCESS-003 — 熔炼设计（合金）及其消费者
断言：三种舰船级合金为 `build_type = SMELTER | PROTOLATHE`：
- **Nanocarbon glass** = 铁 + 玻璃。
- **Durasteel** = 铁 0.2 + 银 0.15 + 钛 0.65。
- **Duranium** = 铁 0.175 + 等离子 0.05 + 银 0.15 + 钛 0.625。
它们在基础科技树节点中自动解锁。
面向玩家的影响：开采的铁/银/钛/等离子直接门控军舰建造与维修。
证据：`smelting_designs.dm:1-29`；基础节点 `techweb/all_nodes.dm:16`。
条件：每单位 `MINERAL_MATERIAL_AMOUNT`；最大堆叠 50。
例外 / 覆写：消费者：MAC/轨道炮/鱼雷发射器建造（nanocarbon 绝缘）、自定义舰船
地格/材料，以及 stormdrive（工程）——`munitions/.../*_construction.dm`、
`custom_turfs.dm`、`power/stormdrive.dm`。
置信度：HIGH（高）

### PROCESS-004 — “Replicator” 是食品机器，而非矿物加工
断言：`/obj/machinery/replicator` 是一个《星际迷航》风格的 **食品** 合成器，由
**biomatter**（食物/生物质）充能，通过语音命令（“computer, make X hot”）产出
`/datum/design/replicator` 配方。它不消耗矿物或矿石。
面向玩家的影响：它是厨房/给养便利设施，与采矿无关；将其列在矿物加工下是误称。
证据：`replicatorDS.dm:1-359`；设计 `research/designs/replicator_designs.dm`；
仅放置在 `vonneumann/vnmk3.dmm` 和 `Serendipity/Serendipity1.dmm`。
条件：需要 biomatter（来自链接的生物发生器或内部储存）；可通过标准部件升级；
emag 改变行为（毒/“惊喜”生成物）。
例外 / 覆写：图案光盘 tier2/3/4 添加配方（`replicatorDS.dm:505-521`）。
置信度：HIGH（高）

### SITES-001 — 随机废墟 / 随机 Z 层级实际上被禁用
断言：所有 NSV 地图配置都设置 `space_ruin_levels = -1`，因此太空废墟 Z 层级
循环从不运行；没有 `_maps/RandomRuins/SpaceRuins` 或 `LavaRuins` 模板被播种。
`_maps/RandomZLevels/*` 仅作为 **外派任务** 加载，且 `ROUNDSTART_AWAY` 被注释掉
且 `VIRTUAL_REALITY` 在 `game_options.txt` 中未设置。
面向玩家的影响：SpaceRuins/LavaRuins 目录（废弃舰、小行星废墟、Mac Space、
Power Puzzle 等）以及 RandomZLevels 外派任务（洞穴、moonoutpost19、research、
SnowCabin、snowdin、spacebattle、TheBeach、undergroundoutpost45）在正常回合中
**不会** 出现；它们需要管理员干预或配置更改。
证据：`mapping.dm:83-92`；`_maps/*.json`（`"space_ruin_levels": -1`）；
`config/awaymissionconfig.txt`；`config/game_options.txt:365`（`#ROUNDSTART_AWAY`）；
`awaymissions/zlevel.dm:1-12`。
条件：用于熔岩的 `seedRuins` 还要求一个带 `ZTRAIT_LAVA_RUINS` 的层级，NSV 星图
地图不提供。
例外 / 覆写：管理员 `createRandomZlevel()` / 传送门仍可加载一个。
置信度：HIGH（高）

## 跨系统依赖

- **19 工程 / 13 弹药**：开采 → 熔炼的合金（durasteel、duranium、nanocarbon
  glass）是 MAC/轨道炮/鱼雷发射器建造与 stormdrive 的输入
  （`smelting_designs.dm`；`munitions/.../*_construction.dm`；
  `power/stormdrive.dm`）。
- **20 研究**：小行星采矿升级链由科学部设计（`mineral_nonferrous` /
  `mineral_exotic` 节点、深层核心 + 传感器模块）并在原型机中建造；加工材料也
  供给原型制造（`asteroid.dm:5-61`；`radar.dm:316-328`）。replicator 共享科技树
  自动解锁路径（`replicatorDS.dm`）。
- **11/17 点防御与 EWAR**：打捞控制台的 hammerlock 显式禁用目标的点防御，以便
  运输机可以接近（`salvage.dm:3-4,96-103`）；见 `point_defence.md:219` 和
  `overmap.md:138-147`。
- **货物/后勤**：矿石向主舰方向移动以供 ORM 加工；采矿船体有一台货物鱼雷发射器
  和 2× `sabre/mining` 小型飞行器用于运送（`Rocinante.dmm`）。
- **舰船战斗**：小行星是难以看见、可摧毁的接触物，打捞者与战斗舰船共享星图
  （`weapons/damage.dm`）。

## 待解问题

1. **船员/矿石如何实际上在主舰与采矿舰之间移动？** 主舰携带 `ferry_home`
   （有时还有 `mining_home`）固定码头；采矿船体携带一个 `ferry` 码头和一个
   `mining_away` 码头（`Gladius1.dmm`、`Galactica1.dmm`、`Hammerhead.dmm`、
   `Rocinante.dmm:145,4514`）。没有地图 JSON 声明一个 `"mining"` 穿梭机，因此
   确切的实时路线（ferry 对 mining shuttle）以及它是否在每艘船体上运行尚未确认。
2. **竖井矿工实际在哪里生成？** `start/shaft_miner` 地标同时存在于主船体
   （Gladius2、Atlas、Aetherwhisp…）和采矿船体（Rocinante、nostromo）上。生成
   循环按 `GLOB.start_landmarks_list` 顺序挑选第一个空闲的匹配地标
   （`job.dm:496-504`），这遵循加载顺序（站点 → 主舰 → 采矿舰）。矿工是否可靠
   地生成在采矿舰上取决于地图，且未在游戏中核实。
3. **一艘被夺取的舰船曾经可被永久使用吗？** 夺取只翻转阵营与目标状态；释放
   hammerlock 会删除登舰内部空间（`SALVAGE-007`）。未找到任何将捕获的船体交给
   船员的代码。
4. **小行星的 `interior_mode` 默认值**——基础 `Initialize` 为小行星设置
   `NO_INTERIOR`（无 `possible_interior_maps`），但对接会在回合中途将其设为
   `INTERIOR_DYNAMIC`（`overmap.dm:476-479`、`fighters_launcher.dm:351`）。确认
   没有其他路径期望小行星有一个预设的内部模式。
5. **`can_salvage` 在采矿与安保控制台之间共享**——两者共享同一个静态定时器；
   确认这是否是有意为之。
