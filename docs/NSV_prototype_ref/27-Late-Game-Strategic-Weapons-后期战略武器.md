> 本文为 research/evidence/late_game_weapons.md 的中文翻译。

# 后期 / 特殊战略武器

NSV13 研究笔记（唯一事实来源：D:\code\NSV13 处的当前代码）。
范围：高阶层级 / 依赖地图或配置的战略资产。每条断言都追溯到代码；
可获取性依据 `_maps/*.dmm` 与 `config/maps.txt` 核查。

## 系统概述

不存在单一的“特殊武器”子系统。本范围内包含的资产来自四个
不同的层次，而在编写手册时最大的风险就是把它们混淆：

1. **舰船机器**（`nsv13/code/modules/munitions/ship_weapons/*`）——位于某一舰船 z 层上的实体 `obj/machinery`
   火炮，负责装载/管理弹药，并交接给一个星图弹丸。
   例如：MAC、混合轨道炮、等离子投射器、BSA。
2. **星图武器 datum**（`nsv13/code/modules/overmap/weapons/delta_overmap_ship_weapons/new_weapon_types/weapon_datum_types.dm`）
   ——即开火档案（弹丸、开火延迟、射界、控制标志），由玩家舰船或 AI 舰船
   在 `apply_weapons()` 中实例化。只有当 *舰船类型* 添加该 datum 时，一门炮才可用。
3. **战略地图映射系统** —— BSA、PDSR 和 Railgun Forge 作为机器存在于
   特定舰船地图上，而非可研究/可制作的装备。它们的电路板存在，
   但 **没有** 研究设计（已验证：在 `nsv13/code/modules/research/` 下任何地方都没有
   `bsa`/`plasma_caster`/`defence_screen`/`railgun_forge`
   条目）。
4. **游戏模式/目标内容** —— hardmode（Dolos Assault）、`clear_system/rubicon`、boss 舰队。

可获取性主要由 **你掷到哪张地图** 决定（`config/maps.txt`）：
`galactica`（minplayers 27）携带 BSA + PDSR；`shrike`（maxplayers 24）携带混合
轨道炮 + Railgun Forge；`serendipity`（maxplayers 20）携带等离子投射器。其他一切
都是常见的（MAC）或仅限 AI/solgov 的（interdiction、boss 舰队）。

---

## 机制

### LATE-001 — BSA 是地图锁定的 Galactica 脊炮，不可制作
断言：超光速蓝空间炮（Superliminal Bluespace Artillery，`/obj/machinery/ship_weapon/energy/beam/bsa`，
`.../energy_weapons/bsa.dm:1`）**只能** 通过驾驶 Galactica 获得。它不可
研究、不可建造，也不能被拆解。
面向玩家的后果：只有 Galactica 回合中的玩家才能使用 BSA。该炮是一门前向、
由飞行员/AI 控制的脊炮光束（datum `/datum/overmap_ship_weapon/bsa`，
`weapon_datum_types.dm:185`：`firing_arc = 45`、`OSW_FACING_FRONT`、`OSW_ALWAYS_FIRES_FORWARD`、
`OSW_CONTROL_PILOT|OSW_CONTROL_AI`、`used_nonphysical_ammo = OSW_AMMO_FREE`，20 秒开火延迟）。
飞行员从军械控制台瞄准并开火；一名船员从
`/obj/machinery/computer/sts_bsa_control` 控制台（在 Galactica 上映射 1 个）控制充能/电力。
证据：
- 机器 + datum：`bsa.dm:1-142`；`weapon_datum_types.dm:185-205`。
- Galactica 舰船携带它：`types/nanotrasen.dm:284`（`new /datum/overmap_ship_weapon/bsa`）。
- 地图：`grep 'energy/beam/bsa\|sts_bsa_control' _maps` → 仅 `Galactica2.dmm`（1 门炮，1 个控制台）
  以及 `Testship/testship.dmm`（已禁用的调试地图）。
- 无研究：在 `nsv13/code/modules/research/` 中无 `bsa` design/node。
条件：
- 充能：`charge_rate = 1000000` /tick，`charge_per_shot = max_charge = 7.5e+7`（75 MW）；
  `power_modifier_cap = 3` 允许高达 225 MW 以进行更强的一击。由电网供电。
- `built` 变量（`bsa.dm:23`，`"Station goal fuckery"`）在 `/built` 子类型上设置，但 **任何地方
  都从未被读取**——空间站目标建造路径是死代码/未实现。
例外 / 覆写：
- `wrench_act` 返回 FALSE，且 `attack_hand/ai/robot` 返回 FALSE——你无法直接与
  炮体交互；只有链接的控制台可用。
- 目标大小门控：无法瞄准战斗机（`is_target_size_valid`，`weapon_datum_types.dm:202`）。
置信度：HIGH（高）（可获取性 + 控制路径）；对 `built` 变量完全为死代码的判断为 MEDIUM（中）（仅存在两处
引用，都在 bsa.dm 中）。

### LATE-002 — BSA 有一条实体的 z 层光束，会 gib 船员并可能生成一个奇点
断言：发射 BSA 会运行 `animate_projectile`（`bsa.dm:152`），它在星图弹丸结算之前
**在舰船 z 层上** 从炮口扫射一条 hitscan 光束到地图边缘。
面向玩家的后果：
- 光束线上的任何 `mob/living` 会被直接 `gib()`（`bsa.dm:173-176`）。
- 光束会在第一个致密地块/原子或一个 `COMSIG_ATOM_BSA_BEAM` 阻挡物处停下。若它在舰船上击中一个
  阻挡物，则在该地块发生 MAX 强度爆炸并记入管理员日志；星图
  射击 **不会** 发射。
- 若阻挡物是一个 `/obj/machinery/the_singularitygen`，它会在舰船上生成一个奇点（2500）
  以及一个 `overmap_anomaly/singularity`——一条刻意的破坏/恶作剧路径。
- 若无物阻挡，它调用 `..()` 并发射星图弹丸
  （`/obj/item/projectile/beam/laser/heavylaser/bsa`，`damage = 1000`，hitscan，`flag = "overmap_heavy"`，
  `projectile_piercing = ALL`；接力变体会将活物化为灰烬并产生 6/8/9/12 的爆炸）。
证据：`bsa.dm:128-211`、`213-248`。该地图（Galactica）以需要一条
通往太空的清晰射击通道而闻名——描述明确警告 “Ensure it has a clear firing path to space!”。
条件：必须只朝东或西瞄准；若 `dir` 得不到有效地块则管理员告警并中止。
例外 / 覆写：星图光束无法瞄准战斗机（`MASS_TINY`），但 *z 层* 光束
仍会 gib 通道中的任何船员，且没有大小检查。
置信度：HIGH（高）。

### LATE-003 — 混合轨道炮是 Shrike 锁定的双模式炮，由电网馈电的电容器驱动
断言：`/obj/machinery/ship_weapon/hybrid_rail`（“NT-ST049 'Sturm' coaxial railgun”）仅由
Shrike 玩家舰船携带。Datum `/datum/overmap_ship_weapon/hybrid_railgun`
（`weapon_datum_types.dm:54`：1 秒开火延迟，最佳射程 50，`OSW_AIMING_BEAM`）。
面向玩家的后果：
- 两种配置，从 TGUI 切换（“Cycling ordnance chamber configuration”，10 秒，仅在
  未装填时）：**400mm 弹丸**（max_ammo 5，电容器 400 kW）或 **800mm 罐体**（max_ammo 1，
  电容器 1 MW）。
- 它直接从电网取电（`try_use_power` 读取 `powernet.avail`），而非从 APC；
  操作员在 UI 中将充能率设置至最高 `capacitor_max_charge_rate = 200000`（200 kW）。
- 一个会退化的 `alignment` 属性会增加散布并导致失火（低于 25 时 25% 失火）。每次射击消耗
  alignment（−0..2 弹丸，−0..8 罐体）；通过打开维护面板（`maint_state >= 2`）
  并持有一把 multitool 来重新校准（`multitool_act`，`hybrid_railgun.dm:327`）。失火会倾泻电容器、
  损坏火炮并电击/点燃附近的碳基生物（`misfire`，第 281 行）。
- 带有 BLUESPACE 标志的锻造弹药每次射击有 33% 的几率“蓝空间”掉这一击而非
  发射（弹药被删除、被传送至舰船上，或舰船受到后座；`bluespace`，第 303 行）。
- 该炮默认不可摧毁（`deletion_protection = TRUE`；Destroy 时它变成一个破损的
  `OBC_destroyed` 船体，必须焊接回去——`welder_act`），即不可替换。
证据：`hybrid_weapons/hybrid_railgun.dm` 整个文件；搭载舰船 `nanotrasen.dm:193-196`
（`frigate/starter/shrike`）；地图 `grep hybrid_rail _maps` → `Shrike/Shrike2.dmm`（1）。
条件：`max_integrity = 400`、`maintainable = TRUE`；低 alignment 导致失火；`functional_crit`
阻止装填和开火。
例外 / 覆写：该 datum 的第二份、仅 AI 的副本存在于 **实例化** 的太空海盗
舰船 HomeOne 上（`types/spacepirates.dm:20`，角色 `INSTANCED_MIDROUND_SHIP`，地图
`_maps/map_files/Instanced/HomeOne.dmm`）——那艘舰船没有锻造炉，且不是一张被投票的地图。
置信度：HIGH（高）（机制/可获取性）；对玩家舰船 vs 海盗的细微差别为 MEDIUM（中）。

### LATE-004 — “锻造炉” 造的是弹药，不是火炮；它是 Shrike 军械工厂
断言：`/obj/machinery/railgun_forge`（`hybrid_weapons/railgun_forge.dm:1`）是一个用于混合轨道炮弹药的
*军械打印机*，而不是武器组装器。可用仅因它映射在 Shrike 上。
面向玩家的后果：
- 必须连接两个槽罐（经由 multitool 缓冲或 `forge_ID`）：一个 **Coating Tank**（T1）和一个
  **Core Tank**（T2）。每个槽罐都装有从矿石筒仓（`remote_materials` 组件）取用的原材料
  （Iron/Silver/Gold/Diamond/Uranium/
  Plasma/Bluespace/Bananium/Titanium/Copper）。
- “core”（T2）与 “coating”（T1）材料按固定权重混合进三个属性：
  导电率、密度、硬度（`getConductivity/getDensity/getHardness`）。这些决定
  发射弹丸的速度、伤害与穿甲（`animate_projectile`，`hybrid_railgun.dm:154`）。
- 产出要么是一个 400 mm 弹丸（消耗槽罐容积的 4% T1 + 20% T2），要么是一个 800 mm 罐体
  （20% T1 + 30% T2）。打印要求两个槽罐都材料锁定且有足够容积。
- 材料标志很重要：**Bananium** → `RAIL_BANANA`（滑溜，降级为 `overmap_light`）、
  **Bluespace** → `RAIL_BLUESPACE`（射击可能蓝空间跳走）、**Plasma** → `RAIL_BURN`（+20 燃烧，
  物品易挥发）。`component_multiplier` 随机器部件等级放大成本（`RefreshParts`）。
- 罐体额外需要一个 **Railgun Canister Filler**（一个大气二元设备，用于
  注入气体并密封罐体）和一个 **Railgun Canister Charger**（充入 `material_charge`；
  罐体的 *气体混合物* 随后添加 EMP/燃烧/伤害，`hybrid_railgun.dm:211-244`）。
证据：`railgun_forge.dm` 整个文件（`forge_slug:353`、`forge_canister:387`，filler:733，charger:916）；
地图 `grep railgun_forge _maps` → `Shrike/Shrike2.dmm`（3 个对象：forge + 2 个槽罐）。
条件：锻造炉需要电力（`auto_use_power`）且 T1/T2 已连接；材料来自矿石筒仓。
例外 / 覆写：锻造机器没有任何研究设计（已验证）。若 Shrike 不是
该地图，整个混合弹药制作循环不可用。弹丸/铀设计作为
研究/设计盘条目存在（`designs/ship_weapon_designs.dm:210-230`、
`disk/design_disk/hybrid_rail_slugs` 在 `hybrid_railgun.dm:518`），但那些是遗留弹丸，不是
锻造系统。
置信度：HIGH（高）（机制/映射）；MEDIUM（中）（确切的矿石筒仓路由边界情况）。

### LATE-005 — MAC 是常见主炮，不是稀有战略资产
断言：`/obj/machinery/ship_weapon/mac`（“Radial MAC cannon”）与 datum
`/datum/overmap_ship_weapon/mac`（`weapon_datum_types.dm:4`）是几乎每艘玩家战舰都携带的
标准反主力舰火炮。
面向玩家的后果：MAC 每发发射一个 `mac_round`（`projectiles_fx.dm:38`：
`damage = 400`、`flag = "overmap_heavy"`，轻微制导 `0.2 s`，`projectile_piercing` 初始为
light，对战斗机切换为 `ALL`）；datum 属性：3.5 秒开火延迟，最佳射程 50，1 发点射。
它 **不能** 攻击战斗机（`is_target_size_valid` 拒绝 `MASS_TINY`）。
证据（搭载该 datum 的玩家舰船）：`types/nanotrasen.dm:186`（Eclipse 轻型巡洋舰）、
207（Jeppison/Atlas 护卫舰）、224（Snake 巡逻巡洋舰）、269（Tycoon/Gladius 战列巡洋舰）、285
（Galactica 战列舰）。一个 `mac/dirty` 变体（延迟的污泥/凝固汽油弹丸，`projectiles_fx.dm:104`）
被太空海盗使用。
条件：弹药是 `railgun_ammo` 系列的实体弹丸。
例外 / 覆写：“Dirty” MAC 仅限 AI/海盗；*玩家* 的 MAC 使用洁净弹药。
置信度：HIGH（高）。

### LATE-006 — MAC 可原地重新组装，但其框架不可研究
断言：`mac_construction.dm` 定义了一个 14 步组装（`/obj/structure/ship_weapon/mac_assembly`），
从未栓固 → 栓固 → 焊接 → 炮管 → 锡焊 → 纳米碳绝缘 → 栓固衬里 →
4 个电容器 → 接线 → 锡焊 → 开火电子器件 → 固定 → 塑钢外壳 → 装填托架。
面向玩家的后果：原则上 MAC 可由船员重建/修复（扳手/焊枪/
螺丝刀/撬棍/线缆步骤），消耗 4 个电容器、4 个纳米碳玻璃、4 根线缆、2 份塑钢，外加
`mac_barrel`、`firing_electronics`、`loading_tray` 部件。
证据：`mac_construction.dm` 整个文件；MAC 的 `spawn_frame`（mac.dm:76）在你拆解一门现有
火炮时生成该组装件；`apply_default_parts`（mac.dm:36）播种部件。
条件：该组装框架本身 **没有主动研究设计**——两个 `mac_assembly`
设计（`designs/ship_weapon_designs.dm:56-76`）位于一个 `/*Obsolete … */` 注释块内。
`grep mac_assembly _maps` 仅找到 `Testship`（已禁用）。`firing_electronics` **可** 研究
（`advanced_ballistics` 节点），但没有任何设计生产 `mac_assembly` 框架。
例外 / 覆写：实际含义——你可以 *拆解并重新组装一门现有 MAC*
（并修复它），但你不能经由研究从零制造一个全新的 MAC 框架。hardmode 科技
授予（`_techweb.dm:8-15`）添加高斯炮塔、甲板炮炮塔、VLS 发射管和开火
电子器件，但不含 MAC 框架。
置信度：代码路径为 HIGH（高）；是否有任何管理员/事件地图提供散落的组装件为 MEDIUM（中）。

### LATE-007 — 等离子投射器（MPAC）是 Serendipity 锁定的，带气体 + alignment 小游戏
断言：`/obj/machinery/ship_weapon/plasma_caster`（“Magnetic Phoron Acceleration Caster”）是
Serendipity 地图的招牌武器；datum `/datum/overmap_ship_weapon/plasma_caster`
（`weapon_datum_types.dm:481`：5 秒开火延迟，`ai_fire_delay = 180 SECONDS`，炮手/AI 控制，
总是向前发射，`max_ai_range = 25000`）。
面向玩家的后果——小游戏（交叉参考气体/大气系统 10）：
- **Phoron 燃料**：该炮需要 `plasma_mole_amount >= plasma_fire_moles`（500）才能开火。气体由
  相邻的 `/obj/machinery/atmospherics/components/unary/plasma_loader` 供给；装填器在开火瞬间
  必须为 OFF，否则 `can_fire` 中止（“back pressure surge”）。
- **alignment**（0-100）：每次射击下降 `rand(30,60)`。在维护面板打开时
  用 multitool 把它调回去（`multitool_act`，`plasma_gun.dm:289`）。低 alignment 会强制失火
  （低于 90/75/50/25 时为 10%/25%/50%/100%）。
- **field_integrity**（0-100）：在安全装置 OFF 时（火炮的 `process` 运行）流失，
  在安全装置 ON 时再生。降到 0 会触发 `misfire`，自动重新启用安全装置并重置为 100。
- `misfire`（`plasma_gun.dm:232`）：将 phoron 作为等离子气云排出、火花、tesla 电击、弹出
  核心，并有（5% 几率）生成一只狂暴的暗紫色史莱姆。
- 一个 `cooldown` 为 100（约 1.5 分钟，“splines unreticulate”）门控开火后的射击。
- 弹丸（`/obj/item/projectile/bullet/plasma_caster`）无情制导（射程 25000，
  `homing_turn_speed = 180`、`damage = 150`、`overmap_medium`、`projectile_piercing = ALL`），当其目标死亡时
  重新锁定最近的非微小敌人，并在被摧毁时爆裂为一团 phoron。
  它能承受 200 次命中才失去凝聚力。
- 弹药是一个 **Condensed Phoron Core**（`plasma_core`），它可研究（`advanced_ballistics`
  节点设计 id `plasma_core`）。
证据：`overmap/weapons/plasma_gun.dm` 整个文件；舰船 `types/nanotrasen.dm:167-171`
（`serendipity`），玩家变体 `:290`；地图 `grep plasma_caster _maps` → `Serendipity/Serendipity1.dmm`
（1 门炮，1 个装填器，6 个核心）。
条件：需要电力（否则卸弹）、phoron 气体供应、校准的 alignment、安全装置关闭才能开火。
例外 / 覆写：用撬棍拆解它会刻意烧伤/点燃使用者。
置信度：HIGH（高）。

### LATE-008 — Lance 是仅限 AI 的协同；无玩家接口
断言：`/datum/lance`（`overmap/lance.dm`）是一个把 AI 舰船编成群的数据结构。
没有面向玩家的控制。
面向玩家的后果：Lance 会指派一名首领、共享一个目标，并封顶于 `maximum_members = 5`。
它们在 AI 代码内部被创建和加入（`ai-skynet.dm:912-990`：`if(!OM.current_lance) … new /datum/lance(OM, OM.fleet)`），
依该文件自身的头部注释：“Currently only used by Fighters, which assign themselves to a lance when
in the 'swarm' behavior.”
证据：`lance.dm:1-49`；所有 `current_lance` 引用都在 `ai-skynet.dm` 中。
条件：需要一个 AI 舰队（`homefleet`）；成员在删除时自动移除。
例外 / 覆写：无。
置信度：HIGH（高）。

### LATE-009 — PDSR 是 Galactica 专属的 nucleium 反应堆护盾，带熔毁
断言：`/obj/machinery/atmospherics/components/trinary/defence_screen_reactor`
（“mk II Prototype Defence Screen Reactor”，`overmap/pdsr.dm:15`）成为舰船的护盾
（`OM.shields = src`）并偏转弹丸命中。它仅映射在 Galactica 上。
面向玩家的后果：
- 它是一个 nucleium 驱动的反应堆，而非被动护盾发生器：CE/工程师必须注入
  **nucleium** 气体（来自 `airs[2]`）并管理 **coolant**（`airs[1]`）、极性反转、电力
  分配、温度与安全壳，从两个主机控制台操作
  （`defence_screen_mainframe_reactor` 用于点火/冷却/关机，`..._shield` 用于再生/硬化/
  密度/电力）。电力来自电网（`min_power_input` ≥ 1 MW，`max_power_input` =
  10 MW + 每个已连接继电器的 2 MW）。
- **熔毁风险**：若 `reaction_containment` 达到 0，它进入 `REACTOR_STATE_EMISSION`，使
  继电器过载，爆炸波及舰船内每个生物（闪光 + 击倒 + 恶心），并对所有四个船体象限造成
  `(emission_energy^2)/2` 伤害（`handle_emission_release`，第 388 行）。过热 / 极性漂移 /
  电力不足都会侵蚀安全壳（`handle_containment:310`、`handle_temperature:414`、
  `handle_polarity:359`）。
- 护盾主动偏转（`absorb_hit`，第 471 行）：若完整度 ≥ 来袭伤害，则 `SHIELD_FORCE_DEFLECT`。
  `DENSITY_LOW` 只阻挡 heavy/BURN 命中。
- 一个 CE 锁定的 `/obj/machinery/syndicatebomb/self_destruct/pdsr`（“scorched earth”）终端可被
  用于销毁证据，并引爆 10 格内的任何 PDSR（`try_detonate`，第 1032 行）。
证据：`overmap/pdsr.dm` 整个文件；地图 `grep defence_screen _maps` → `Galactica2.dmm`
（1 个反应堆，5 个继电器，2 个主机控制台）。
条件：继电器必须通电且未过载（每个都贡献最大护盾完整度与
功率上限）。过载的继电器需要线缆 + 焊接；严重损坏的需要塑钢。
例外 / 覆写：反应堆/继电器/控制台的电路板存在，但 **没有研究
设计**。反应堆在被自毁终端或 ex_act 路径刻意驱散之前是 `INDESTRUCTIBLE`/`LAVA_PROOF`/等。
置信度：HIGH（高）。

### LATE-010 — 拦截场是 AI/solgov 资产；玩家从不部署它
断言：`/datum/component/interdiction`（`overmap/interdiction.dm`）被添加到特定星图舰船
并阻止 **其他阵营** 的舰船跃出该星系。玩家无法部署它。
面向玩家的后果：
- 在 `COMSIG_GLOB_CHECK_INTERDICT`（在一次 FTL 跃迁期间发出，`FTL/ftl_jump.dm:228`）时，
  同一星系中任何带有该组件且阵营不同的舰船返回 `WEAK_INTERDICT`；一次非强制
  跃迁随后失败，除非是误跳。`owner.on_interdict()` 触发（Syndicate Kadesh 添加一个
  传感器轮廓惩罚，`syndicate.dm:518`）。
- 谁拥有它：Syndicate **Kadesh** 战列舰（`syndicate.dm:508`）以及 Solgov **Capiens 级
  拦截舰**（`solgov.dm:105-107`）。两者都是舰队。
- 玩家如何遭遇它：(a) 当玩家 *在 MAIN_OVERMAP 上把 IFF 控制台黑入 Syndicate* 时，
  会生成 **Solgov 猎杀舰队**（`/datum/fleet/solgov/interdiction`）
  （`iff_console.dm:138-155`）——随后拦截舰跨星系追猎该舰；
  (b) **轻型 Syndicate 拦截舰队** 可随机生成（它是 Syndicate 阵营的一个
  `randomspawn_only_fleet_types` 条目，权重 1，`factions.dm:156`），并在 hardmode 中被
  强制生成（`hardmode.dm:27`）。主 `/datum/fleet/interdiction` 是管理员生成。
证据：`interdiction.dm`、`ftl_jump.dm:228-229`、`ai-skynet.dm:236`、`fleet_types.dm:127-149`、
`factions.dm:92-98,156`、`iff_console.dm:138-155`。
条件：只影响 *同一星系* 中 *不同阵营* 的舰船。
例外 / 覆写：不安全（“fly unsafe”）跃迁会绕过非场干扰性（WEAK）拦截。
不存在可部署的拦截物品/机器（grep 未找到任何）。
置信度：HIGH（高）。

### LATE-011 — Rubicon/Dolos hardmode 是白名单门控的终局内容
断言：`/datum/overmap_gamemode/hardmode`（“Dolos Assault”，`gamemodes/overmap/hardmode.dm`）是
终局海战战役。目标为两个 `clear_system` 目标：**Rubicon** 和 **Dolos Remnants**。
面向玩家的后果：
- Hardmode 是 `whitelist_only = TRUE`、`required_players = 25`、`selection_weight = 5`——它必须在
  地图配置中启用（或由管理员强制切换，`overmap_mode.dm:691` 的 `toggle_hardmode`）。
- 启用它 (a) 开启 **hardmode 研究**（授予玩家高斯炮塔、甲板炮炮塔/三管、
  VLS 发射管和开火电子器件——`_techweb.dm:8-25`），(b) 立即在 Rubicon 生成一个
  `/datum/fleet/interdiction/light`，且 (c) 若尚无回合开始，则强制
  `secret_extended`（无反派——“you've got enough on your plate”）。
- 当目标星系再无敌人时 `clear_system`（`objectives/rubicon.dm`）完成；
  **Dolos** 变体生成一个 `/datum/fleet/remnant`（“The Remnant”，`FLEET_DIFFICULTY_WHAT_ARE_YOU_DOING`）。
- 当存活玩家 > 10 且 Rubicon 有敌人时，`clear_system/rubicon` 也会作为“扩展”目标在回合中途
  由 `overmap_mode.request_additional_objectives`（`overmap_mode.dm:344-348`）添加。
证据：`hardmode.dm`、`objectives/rubicon.dm`、`system_defence_armada.dm`、
`fleet_types.dm:151-171`、`overmap_mode.dm:321-352`、`starsystem.dm:1745-1757`（`Oasis Fidei` 持有
Remnant 舰队）。
条件：目标门控经由 `required_players`/`extension_supported`（通用扩展池
循环当前被注释掉，替换为硬编码的 Rubicon 检查）。
例外 / 覆写：`system_defence_armada`（防御选定星系对抗 Syndicate 舰队）是一个
由 `/datum/overmap_gamemode/armada` 模式驱动的独立目标。
置信度：HIGH（高）。

### LATE-012 — Boss 舰船（Fist of Sol、Alicorn）已定义但无代码生成
断言：Syndicate 战列舰 **Fist of Sol**（`/obj/structure/overmap/syndicate/ai/fistofsol`，
`types/syndicate.dm:521`）与敌对的 **Alicorn**（`/obj/structure/overmap/hostile/ai/alicorn`，
`syndicate.dm:568`）是最重的 AI 主力舰，带有专属 boss 舰队
（`fleet/syndicate/fistofsol_boss`、`fleet/hostile/alicorn_boss`、`fleet_types.dm:182-202`）。
面向玩家的后果：
- Fist of Sol：`obj_integrity = 5000`，护甲 `light 99 / medium 65 / heavy 40`，精英战列舰 AI，
  发射战斗机/轰炸机，武器 = `twinmac`（2 发 MAC 点射）、`hailstorm`、`quadgauss`、
  `pdc_mount`、`flak ×3`、`missile_launcher`。在有一名玩家在其上时击杀它会授予
  `fist_breaker` 成就（`syndicate.dm:556-566`）。
- Fist of Sol **被列入随机敌人生成的
  黑名单**（`starsystem.dm:12`）。
- boss 舰队 **未被任何生成代码引用**（grep 只找到它们的定义）——
  实际上是管理员/事件生成，或保留给未来内容。
证据：`types/syndicate.dm:521-566`；`fleet_types.dm:182-202`；`starsystem.dm:12`。
条件：任何直接生成都由管理员或事件完成，而非标准星图模式循环。
例外 / 覆写：未发现。
置信度：对“仅限管理员”结论为 MEDIUM（中）（缺乏代码引用是有力但非
绝对的——任务/星图数据路径仍可能引用它；在 `config/starmap` 中未找到任何）。

---

## 可获取性表

| 项目 | 是否实现？ | 是否映射？ | 建造 / 研究 / 管理员？ | 实际可获取？ | 玩法角色 |
|---|---|---|---|---|---|
| BSA | 是 | 仅 Galactica2.dmm（调试地图不计） | 无设计；`built` 变量为死代码 | 仅作为 Galactica 的脊炮 | 飞行员发射的前向一次性光束；z 层扫射会 gib 船员，可能生成奇点 |
| Hybrid railgun | 是 | 仅 Shrike2.dmm | 无设计；舰船锁定 | 仅在 Shrike 上（+ 实例化海盗 HomeOne） | 双模式弹丸/罐体准脊炮，电网馈电，不可替换 |
| Railgun Forge（+ 槽罐/装填器/充能器） | 是 | Shrike2.dmm（forge+2 槽罐） | 无设计；舰船锁定 | 仅在 Shrike 上 | 从矿石筒仓材料制作锻造混合弹药；气体/充电罐体 |
| MAC | 是 | 许多舰船 | 部件可研究；框架不可研究（过时设计） | 常见初始火炮；经由拆解重新组装 | 标准反主力舰 400 伤害弹丸 |
| Plasma caster (MPAC) | 是 | 仅 Serendipity1.dmm | 无火炮设计；`plasma_core` 弹药可研究 | 仅在 Serendipity 上 | 气体+alignment 小游戏；无情制导的 phoron 弹丸 |
| Lances | 是 | 不适用（AI 数据） | 不适用 | 玩家不可用 | AI 战斗机群协同 |
| PDSR reactor + relays + consoles | 是 | 仅 Galactica2.dmm | 无设计；舰船锁定 | 仅在 Galactica 上 | Nucleium 护盾反应堆；偏转命中，熔毁会损伤己方舰船 |
| Interdiction component | 是 | 不适用（在 Kadesh / solgov 拦截舰上） | 仅 AI/solgov | 玩家无法部署 | 阻止敌方 FTL 跃迁；IFF 黑客触发 solgov 猎杀舰队 |
| Hardmode (Dolos Assault) | 是 | 不适用（游戏模式） | 白名单/配置或管理员 | 仅配置/管理员 | Rubicon+Dolos 清剿星系战役；授予额外研究 |
| clear_system/rubicon objective | 是 | 不适用（游戏模式） | >10 玩家时为扩展，或 hardmode | 游戏内目标 | “清剿 Rubicon 中的所有敌人” |
| Fist of Sol / Alicorn boss fleets | 是 | 不适用 | 未找到代码生成 | 仅管理员/事件 | 带 `fist_breaker` 奖励的 boss 主力舰 |
| HomeOne pirate hybrid-railgun ship | 是 | Instanced/HomeOne.dmm | 实例化回合中途 | 随机实例化遭遇 | 携带混合轨道炮的海盗袭击者（无锻造炉） |

## 跨系统依赖

- **地图配置是总门控。** `config/maps.txt` 决定 BSA/PDSR（galactica，27+）、
  混合轨道炮/锻造炉（shrike，≤24）或等离子投射器（serendipity，≤20）是否
  存在。据此填充手册——这三者都无法通过研究或建造获得。
- **舰船类型的 `apply_weapons()`（`types/nanotrasen.dm`）必须添加匹配的星图 datum**，
  否则一门已映射的火炮实际上无法经由星图火控开火。BSA（bsa datum）、混合轨道炮
  （hybrid_railgun datum）、等离子投射器（plasma_caster datum）都遵循此模式。
- **电网电力** 是混合轨道炮、PDSR 和 BSA 的共享基础设施——全都绕过 APC 并取用
  `powernet.avail`。工程/大气负载决定它们能否开火。
- **气体/大气** 把等离子投射器（phoron）和 PDSR（nucleium + coolant）链接到大气系统
  （系统 10）；轨道炮弹体使用相同的气体类型（nucleium/plasma/tritium）以获得 EMP/燃烧奖励。
- **研究** 只完全门控 MAC 部件（开火电子器件）和遗留轨道炮弹丸；战略
  武器（BSA/PDSR/forge/等离子投射器）刻意绕过研究。
- **游戏模式**（hardmode）添加一项研究授予并强制拦截舰队；`clear_system/rubicon`
  也作为普通模式的扩展目标出现。
- **AI/舰队代码**（`ai-skynet.dm`）驱动 lance、拦截舰队和 boss 行为——不是
  玩家可触及的系统。

## 未解问题

- `built` BSA 子类型（`bsa.dm:31`）在数据/配置文件中（空间站目标）是否有任何消费者，
  抑或“空间站目标”建造路径已完全死掉？对 `built` 只有两处引用，都在
  本地；假定已死但未在 `nsv13/code` 之外穷尽检查。
- 是否有任何管理员/事件地图（不在 `_maps/` 轮换中）放置了一个散落的 `mac_assembly` 框架或一个
  供玩家建造的 BSA/等离子投射器电路板？对于
  `mac_assembly` 仅找到 `Testship`（已禁用）。
- `fistofsol_boss` / `alicorn_boss` / `fleet/unknown_ship` / `fleet/dolos` 是否被
  `nsv13/code` 之外的任何任务或星图数据生成？未找到代码引用；在纯管理员专属的结论下，
  请核实任何运行时任务 JSON。
- `overmap_mode.dm:325-342` 中的通用扩展目标池被注释掉了（TODO）；确认
  这是否影响 `clear_system/dolos` 在 hardmode 之外是否可选。
- 当 BSA 星图射弹被接力到一艘接收舰船时的确切行为（灰化/爆炸值）——
  代码存在但此处未经玩家验证。
