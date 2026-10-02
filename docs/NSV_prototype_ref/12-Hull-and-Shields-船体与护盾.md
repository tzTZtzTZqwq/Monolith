> 本文为 research/evidence/hull_shields.md 的中文翻译。

# 船体、装甲、护盾与防御系统

事实来源：当前 NSV13 `/dm` 代码，直接追踪。所有行号引用相对于 `D:\code\NSV13`。

## 系统概述

overmap 上的一艘舰船是一个 `/obj/structure/overmap`，带有一个标量 `obj_integrity`/`max_integrity`
（"上层结构／结构 HP"）。生存是分层的，按来袭抛射物被处理的顺序：

1. **护盾**（`shields` 变量，或是 `/obj/machinery/shield_generator`、PDSR 反应堆，或一个
   `/datum/component/overmap_shields`）—— 一个全有或全无的能量缓冲，在 `bullet_act` 中最先检查。
2. **装甲象限**（方向性的 `armour_quadrants` 列表）—— 四个独立的池，按象限吸收命中，溢出部分进入结构。
3. **船体装甲板**（位于舰船 Z 层级内的 `/obj/structure/hull_plate` 对象）—— 实体板，其*完好板的数量* 缩放舰船的被动修理速度。
4. **上层结构**（`obj_integrity`）—— 当它相对命中达到 0 时，舰船进入"结构临界"
   并开始一段持续数分钟的引爆倒计时（玩家／主舰）。
5. **纳米修理套件**（APNW + APNP 泵）—— 有动力的机械，从材料供给的资源池主动治疗象限和结构。

还存在两个正交的逐舰防御系统：**PDSR**（一个投射偏转屏的 nucleium 反应堆）和可研究的 **护盾发生器**。`armor`（BYOND 的 `armor` 列表，以 `"overmap_light"/"overmap_medium"/"overmap_heavy"` 为键）是一个独立的*百分比伤害减免*，由 `run_obj_armor` 在象限吸收之前施加。

基础 overmap 上 `integrity_failure = 0`（`overmap.dm:51`），因此 `obj_break()` **不**用于普通舰船死亡；走的是倒计时／`obj_destruction` 路径。

## 核心游戏循环

- 交战方互相射击；命中先被 `armor` 减免，然后要么被护盾吸收（若护盾升起且对该次命中足够强），要么被路由到朝向的象限，溢出到结构。
- 若命中持续落在同一象限，整个一侧可能被击穿；该象限在低于 10% 时显示"屈曲"overlay，然后停止吸收。
- 当结构相对来袭伤害达到 0 时，玩家舰船不会立即爆炸 —— 它们进入上层结构临界，并有一个 15 分钟的全局倒计时，给船员时间焊接装甲板、运行纳米修理套件，或一个与修理相关的控制台。
- 战斗之间，工程通过以下方式恢复完整度：焊接船体装甲板（亲自）、APNW/APNP 自修理系统（有动力、有材料供给），以及与完好板数量挂钩的缓慢被动回复。

## 机制

### HULL-001
断言：一个 `/obj/structure/hull_plate` 拥有自己的 `obj_integrity`（默认 200），独立于舰船。它向一个父 overmap 注册，且在就位时递增 `parent.armour_plates` 和 `parent.max_armour_plates`；被摧毁时递减 `armour_plates`。
面向玩家的后果：完好的板是决定舰船自愈速度的资源；被击毁的板必须焊回来。
证据：`hull_plating.dm:13-14`（obj/max_integrity 200）、`:58-59`（链接时递增）、`:63`（Destroy 时递减）、`:132`（破碎时 `parent?.armour_plates--`）。
条件：板链接到其 Z 层级的 `get_overmap()`；每 10s 重试，最多 `tries=2`，然后 `message_admins` + `qdel`（`:48-57`）。
例外 / 覆写：`/obj/structure/hull_plate/end` 仅改变图标（`:20-21`）。
置信度：HIGH（高）

### HULL-002
断言：`armour_scale_modifier`（=4）在船体装甲板上被声明，但在整个代码库中**从未被读取**。它没有游戏性效果。
面向玩家的后果：无。文档／手册不得将其视为减伤属性。
证据：`hull_plating.dm:16` 声明了它；对 `armour_scale_modifier` 的全仓库 grep 只返回那一行。
置信度：HIGH（高）

### HULL-003
断言：船体装甲板只通过来自父舰船的概率性中继受到伤害。父级的 `take_damage` 发送 `COMSIG_ATOM_DAMAGE_ACT`；每个链接的板的 `relay_damage` 以概率 `amount/5` 调用 `take_damage(amount)`（因此更大的命中更可能使装甲板凹陷）。
面向玩家的后果：重型弹药使板凹陷／破碎的速度远快于轻型火力；小命中很少损伤装甲板。
证据：`hull_plating.dm:120-124`（`if(prob(amount/5)) take_damage(amount)`），注册于 `:60`；
信号在 `obj_defense.dm:15` 和 `weapons/damage.dm:111` 中发送。
条件：`amount` 是舰船承受的装甲后伤害值。
例外 / 覆写：板覆写了 `take_damage`（`:126-137`）：若一次命中 ≥ 当前完整度，
完整度被钳制为 1，`parent.armour_plates--`，`armour_broken=TRUE`，并直接返回而不
调用父级；一块破碎的板不能被递减两次。
置信度：HIGH（高）

### HULL-004
断言：对船体装甲板的任何非零修理都会将其完全修复。`try_repair` 添加 `amount`，然后由于该值总是被钳制 ≤ max，它无条件将完整度设为 max，并且若板曾是破碎的，则重新递增 `parent.armour_plates`。
面向玩家的后果：修理动作之后不存在"部分"板状态 —— 一次修理就把它焊回满值，并把它重新加入舰船的板计数。
证据：`hull_plating.dm:139-155`（`if(obj_integrity <= max_integrity)` 在钳制后总是为真，强制完全修理以及 `armour_plates++`）。
条件：由焊接器以 `amount=100` 调用，或由修理泡沫以 `max_integrity` 调用。
置信度：HIGH（高）

### HULL-005
断言：焊接器在一次 4 秒动作中修理目标板**以及 3×3 区域（`orange(1)`）内所有受损的板**；燃料成本等于实际修理的板数。
面向玩家的后果：聚集的损伤让工程师每次焊接可修理多块板；燃料按板消耗。
证据：`hull_plating.dm:185-208`。
条件：只有 `obj_integrity < max_integrity` 的板会被加入修理列表。
置信度：HIGH（高）

### HULL-006
断言：`max_armour_plates` 由 `check_armour()` 从实时的 `armour_plates` 计数刷新，舰船在初始化后 20 秒调度它，并在 `armour_plates <= 0` 时每 20 秒重新调度。
面向玩家的后果：板计数在回合最初约 20 秒内可能不是最终值；这只影响回复数学，不影响生存。
证据：`overmap.dm:473` 调度 `check_armour`；`hull_plating.dm:164-170`。
置信度：MEDIUM（中）（proc 在无 `occupying_levels` 时提前返回；依赖时序）。

### HULL-007
断言：注释 "You lose max integrity when you lose armour plates" **未被实现**。没有任何代码因板损失而降低 `max_integrity`（或 `obj_integrity`）。板损失仅降低 `get_repair_efficiency`。
面向玩家的后果：破碎的板减慢被动自修理；它们不会降低舰船的 HP 上限或其伤害抗性。
证据：`overmap.dm:52` 注释 vs. 仓库 grep：`armour_plates`/`max_armour_plates` 仅在
`hull_plating.dm` 的回复数学中使用（`:211-214`）；没有与板绑定的 `max_integrity` 写入。
置信度：HIGH（高）

### HULL-008（被动修理）
断言：被动舰船自修理（位于 `slowprocess`）按 `get_repair_efficiency()` =
`100*(armour_plates/max_armour_plates)` 缩放（若无板记录则为 10）。带装甲象限且
`role == MAIN_OVERMAP` 的舰船调用 `repair_all_quadrants(eff/2000)`；无象限的舰船调用
`try_repair(eff/25)`。战机（`mass == MASS_TINY`）和小行星从不被动修理。
面向玩家的后果：完好的板越多，舰船恢复得越快；被剥光的舰船恢复得非常慢；战机无法自愈。
证据：`hull_plating.dm:172-183`、`:211-214`；`damage.dm:263-277`（overmap `try_repair`）。
条件：主舰象限路径使用 `repair_all_quadrants`，其 `input` 是一个**百分比**
（`input/100`），因此每 slowprocess tick 为 `eff/2000`。
置信度：HIGH（高）

### HULL-009（修理泡沫 / juice）
断言："Hull repair juice" 是一种试剂（配方：1 稳定剂 + 1 铁 + 1 碳 → 10 单位）。
其泡沫将它落到的 turf 中的每一块船体装甲板修理至满值。从 1500 单位罐和一个高级灭火器变体中分配。
面向玩家的后果：一种在战斗中／战斗后无需焊接器即可修理装甲板的快速、区域拒止方式。
证据：`hull_plating.dm:66-118`。
条件：`reaction_turf` 遍历 `T.contents` 寻找船体装甲板，并调用 `try_repair(max_integrity)`。
置信度：HIGH（高）

### QUAD-001
断言：`use_armour_quadrants` 默认 FALSE，并在 `LateInitialize` 中为任何 `mass > MASS_TINY`、`role != MAIN_MINING_SHIP` 且未预置它的舰船自动启用。启用时，对于玩家／普通舰船，全部四个象限获得 `max_armour = current_armour = obj_integrity/4`；对于 AI 舰船（`role > NORMAL_OVERMAP`）为 `obj_integrity/2`。
面向玩家的后果：象限的坚固程度源自 HP，而非一个独立的板属性；AI 舰船的象限按比例更强。
证据：`overmap.dm:395-402`；`overmap.dm:64`（默认 FALSE）。
条件：采矿船明确禁用象限（`types/miningships.dm:24,32,40,48`），而是
依靠焊接。
置信度：HIGH（高）

### QUAD-002
断言：来袭命中按朝向被路由到四个象限之一。抛射物使用抛射物的
`Angle`（`projectile_quadrant_impact`）；碰撞和水雷使用攻击者相对
舰船的位置（`quadrant_impact`）。这两种映射**并不完全相同**：`projectile_quadrant_impact`
改变了哪个弧映射到哪个象限。
面向玩家的后果：哪一侧承受命中取决于射击来自何处／炮弹行进的方向，因此站位（舷侧对舰首朝向）会改变哪个池被耗尽。
证据：`armour_quadrant.dm:24-51`（`quadrant_impact`、`projectile_quadrant_impact`）、`:6-21`
（`check_quadrant`）；调用者 `weapons/damage.dm:59`、`physics.dm:536-538`、`weapons/mines.dm:107`。
条件：`projectile_quadrant_impact` 的弧→象限映射：0-89 aft_port、90-179 forward_port、
180-269 forward_starboard，其余 aft_starboard。
例外 / 覆写：`check_quadrant`（physics.dm:555）被调用但其返回值被丢弃 ——
它对碰撞伤害没有影响；实际路由使用 `quadrant_impact`。
置信度：HIGH（高）于映射；MEDIUM（中）于 `check_quadrant` 是有意为之的死代码（解读）。

### QUAD-003
断言：`take_quadrant_hit(damage, quadrant)` 设置 `current_armour = max(current - damage, 0)`，并且若
命中超出该象限的池（`delta = damage - current_armour > 0`），则将溢出
`delta` 传递给 `take_damage`（结构）。若被完全吸收，它播放一声"叮"，并在 ≥15 的命中时震动船员。
面向玩家的后果：象限起到方向性装甲板的作用；一旦某一侧被剥光，
对该侧的进一步命中会直接进入上层结构。
证据：`armour_quadrant.dm:66-89`。
条件：若 `use_armour_quadrants` 为 FALSE，伤害直接进入 `take_damage`。若 `nodamage`
为 TRUE（任务关键空间站、courier MO），命中被完全忽略。
置信度：HIGH（高）

### QUAD-004
断言：一旦 `current_armour <= max_armour/10`（低于 10%），象限就会显示警告 overlay。
面向玩家的后果：船员可以从舰船精灵上读出"这一侧即将屈曲"。
证据：`armour_quadrant.dm:96-104`（`update_quadrants`）。
置信度：HIGH（高）

### QUAD-005
断言：`repair_quadrant` / `repair_all_quadrants` 支持一个 `failure` 几率，它会触发"错误修理"：
要么是结构伤害，要么是对随机象限的伤害（偏差 50/50）。被动回复路径以
`failure=0` 调用 `repair_all_quadrants`，因此被动修理从不搞砸。`full_repair()`（仅
管理员／VV）恢复结构 + 所有象限，并即使在无归点之后也清除结构临界。
面向玩家的后果：任何传入非零 failure 的面向玩家象限修理都可能适得其反；
基线回复是安全的。
证据：`armour_quadrant.dm:131-208`，管理路径 `:113-124`、`:210-222`。
置信度：HIGH（高）（于这些 proc）；MEDIUM（中）于哪些运行时调用者传入非零 failure（grep 显示
APNP 做自己的修理数学，而非这些 proc；这些似乎由任务／管理员使用）。

### NANO-001（APNW - "井"）
断言：`/obj/machinery/armour_plating_nanorepair_well` 是枢纽。它存储一个材料容器
（铁／银／钛／等离子，上限 1,000,000），将选定的合金等级转换为 `repair_resources`
（上限 `RR_MAX = 5000`），并暴露一个 `power_allocation`（默认最大 3 MW），它通过 sigmoid 设置 `repair_efficiency`。
其 `active_power_usage = power_allocation`，取自本地电网盈余。
面向玩家的后果：自修理系统既需要一个有燃料的材料储备，也需要一个巨大的、
专用的电力预算；它是主舰主要的被动治疗手段。
证据：`nano_well.dm:46-79`、`:100-128`、`:167-177`、`:179-243`。
条件：只能链接一个井（`OM.linked_apnw`）；重复会警告（`:91-92`）。
例外 / 覆写：超频模块（电力 → 12 MW 上限，负载 → 应力阈值 200，冷却 →
2.5× 应力衰减）已定义（`:265-293`），但仓库 grep 找不到它们在别处的引用，即
不能通过任何设计图制作 —— 视为仅管理员／地图使用。
置信度：HIGH（高）

### NANO-002（APNW 应力）
断言：系统总负载 = 所有泵的 `armour_allocation + structure_allocation` 之和。高于
`system_stress_threshold`（默认 100）时，应力累积（将 turf 加热 +3，并最终随机
`stress_shutdown` 一个活动泵）；处于／低于阈值时按 `system_cooling` 衰减。
高于 3 MW 时井还会将该 tile 空气加热至最高 398 K。
面向玩家的后果：过度分配泵会导致过热，并使泵带着
"overload" 灯离线，需要扳手重启。
证据：`nano_well.dm:130-165`、`:167-177`。
置信度：HIGH（高）

### NANO-003（APNP - 泵）
断言：每个象限一个 `/obj/machinery/armour_plating_nanorepair_pump`；每个都有独立的
`armour_allocation` 和 `structure_allocation`（0-100，通过 UI 联合上限为 100）。装甲修理
增加 `delta_time * sigmoid(quadrant_%) * repair_efficiency * armour_alloc/100 * 3`；结构修理
增加 `delta_time * ((2+weight/10)*repair_efficiency*structure_alloc)/200`。两者都消耗
`repair_resources`（装甲 ×weight；结构 ×weight ×1.5）并消耗电力（amount×100）。
面向玩家的后果：工程师在一个有限的资源+电力预算中，逐侧地在修补装甲和
修补结构之间分配。
证据：`nano_pump.dm:61-117`。
条件：`weight_class = OM.mass`，对 MASS_TITAN+ 上限为 10（`:66-68`）。若 `structure_crit_no_return`，结构修理
停止（`:83`）。修理输出随 `apnw.repair_efficiency` 缩放。
例外 / 覆写：象限预设（`/forward_port` 等）在地图时设置象限（`:30-40`）。
置信度：HIGH（高）

### NANO-004（APNP 保养）
断言：焊接器打开／关闭维护面板（切换 `online`）；扳手重启一个
`stress_shutdown` 泵（仅在离线时）。若未链接任何井，交互会播放一个错误并
拒绝。
面向玩家的后果：破坏（打开面板、切断电力）是针对舰船自修理的反制玩法。
证据：`nano_pump.dm:139-165`、`:189-214`。
置信度：HIGH（高）

### OMAP-001
断言：在基础 overmap 上，`max_integrity = 300` 是默认值；实际值按子类型设定
（例如 light_cruiser/starter 1400、frigate/starter 800；AI 舰船范围约 300-5000）。
面向玩家的后果：舰船 HP 是一个子类型属性；玩家舰船属于较肉的船体之列。
证据：`overmap.dm:50`；`types/nanotrasen.dm:179-180,200-203`；`types/syndicate.dm`（多处）。
置信度：HIGH（高）

### OMAP-002
断言：overmap 舰船的 `armor` 以 overmap 抛射物标志（`overmap_light/medium/heavy`）为键，
并按子类型设定；`LateInitialize` 还在其上设置标准的 `OM_ARMOR`（melee/bullet/laser/etc = 100，
bomb = 80）。`run_obj_armor` 在象限吸收之前按匹配标志的百分比减少来袭伤害。
面向玩家的后果：不同武器对不同船体受到的抗性不同；例如起始 light cruiser 抗轻型 95%，但抗重型只有 10%。
证据：`overmap.dm:377`、`__DEFINES/overmap.dm:3`（`OM_ARMOR`）、`types/nanotrasen.dm:182`；
`obj_defense.dm:27-38`（`run_obj_armor`）。
条件：`run_obj_armor` 只减少 BRUTE/BURN；其他伤害类型返回 0。
置信度：HIGH（高）

### OMAP-003
断言：`hullburn` 是一种舰船级的持续伤害。`hullburn` 计数 tick（秒）；每个
`slowprocess` tick 递减它，并以 BURN "fire" 伤害施加 `hullburn_power`。燃烧弹头设置它：
hellfire 鱼雷 `hullburn += 60`、`hullburn_power >= 6`；轨道炮锻造弹设置
`hullburn += burn`。
面向玩家的后果：火焰武器在命中后持续伤害舰船；较弱的燃烧可以延长强燃烧的
持续时间（power 以 `max()` 合并）。
证据：`overmap.dm:58-61`；`physics.dm:130-134`；`weapons/projectiles_fx.dm:326-329, 532-537`。
条件：仅施加于没有 `occupying_levels` 的目标（有内部的舰船改为在内部燃烧）
且通常是 AI 舰船／战机。
置信度：HIGH（高）

### OMAP-004
断言：`damage_states` + `apply_damage_states` 驱动一个基于精灵的伤害指示器：icon_state 按完整度百分比（四舍五入到 25）附加后缀 `-0/-25/-50/-75/-100`。`dent_decals` 列表被声明但从未在任何地方使用。
面向玩家的后果：一些舰船在受损时可见地变黑；`dent_decals` 目前不做任何事。
证据：`overmap.dm:56-57`、`:839-846`；grep 显示 `dent_decals` 仅出现在其声明处。
置信度：HIGH（高）

### OMAP-005（结构临界 / 死亡）
断言：对于具有 `DAMAGE_STARTS_COUNTDOWN` 的舰船，`take_damage` 在 `obj_integrity <= damage_amount`（或已经临界）时拦截命中：
它发送板信号，设置 `obj_integrity = 10`，并调用
`handle_crit` 而非 `..()`。`handle_crit` 启动倒计时并生成内部爆炸
预告。`handle_critical_failure_part_1` 运行一条时间线：15 分钟警告 → 10 分钟"无归"
（`structure_crit_no_return = TRUE`）→ 15 分钟时通过 `handle_critical_failure_part_2`
引爆（若为 MAIN_OVERMAP 则 qdel + 结束回合）。
面向玩家的后果：主舰很少因单发而立即死亡 —— 它进入一个
船员仍可挽救的紧急状态，直到 10 分钟无归标记。
证据：`damage.dm:101-116`、`:146-164`、`:167-257`；`physics.dm:125-128` 驱动该时间线。
条件：要求 `overmap_deletion_traits` 中有 `DAMAGE_STARTS_COUNTDOWN`（许多舰船默认设置）；
修理到完整度的 20% 以上会取消临界，除非已无归。
置信度：HIGH（高）

### OMAP-006
断言：`obj_break()` 对普通舰船实际上未被使用，因为 `integrity_failure = 0` 禁用了
`/obj/take_damage` 中的 `obj_break` 触发。只有特殊情况覆写它：一艘 syndicate elite
潜艇将 `obj_break` 复用为"阶段 2"鱼雷更换（`integrity_failure = 200`）。
面向玩家的后果：普通舰船没有低 HP 的"故障"破碎阶段；破碎行为按子类型定制。
证据：`overmap.dm:51`；`obj_defense.dm:19-20`；`types/syndicate.dm:462-483`。
置信度：HIGH（高）

### SHLD-001（护盾发生器）
断言：`/obj/machinery/shield_generator` 需要有盈余的电缆电力；`power_input`（MW，≤ `max_power_input`
= 15 MW）由用户设置。每次 process：`flux_rate = round(MW * 2.5)`；`max_integrity =
(maxHealthPriority% * flux_rate) * 100`；剩余的 flux 以 `regenPriority%` 添加到 `integrity`。
`maxHealthPriority` 和 `regenPriority` 总是合计为 100。
面向玩家的后果：更多电力 = 更大的护盾上限；两个滑块在更高容量和更快回复之间权衡。
证据：`shieldgen.dm:186-311`。
条件：`absorb_hit` 仅在 `active` 且 `integrity >= 完整命中伤害` 时才成功；否则
NOEFFECT（整个命中绕过护盾 —— 见 SHLD-003）。
置信度：HIGH（高）

### SHLD-002
断言：断电会消耗护盾：在无电力／`power_input<=0`／盈余不足时，完整度
每 tick 下降 2 且 `active` 被强制 FALSE；在 ≤0 时护盾完全断电（integrity 和
max 设为 0），必须重新升起。
面向玩家的后果：切断护盾发生器的电力（或耗尽电网）会使护盾崩溃；护盾是衰减而非瞬间消失。
证据：`shieldgen.dm:286-311`、`:271-274`。
置信度：HIGH（高）

### SHLD-003
断言：护盾吸收对每个抛射物是全有或全无：若 `shield["integrity"] >= damage`，命中
被完全吸收（`SHIELD_ABSORB`）；若护盾的完整度*低于*命中，命中原样穿过
（`SHIELD_NOEFFECT`）且不损失护盾完整度。
面向玩家的后果：单发大威力弹会直接穿透低护盾而不消耗
它 —— 护盾对爆发很弱，而"微操"护盾降下来躲避廉价命中的做法是可能的
（正如代码注释所述）。
证据：`shieldgen.dm:210-219`；`weapons/damage.dm:20-47`。
置信度：HIGH（高）

### SHLD-004（建造 + 解锁）
断言：发生器由 `shieldgen_frame` 手工建造，需要 2 个冷却风扇、4 个 flux
电容器、4 个调制器和 1 个蓝空间晶体接口（焊接器／扳手／螺丝刀步骤）。
所有部件都是 PROTOLATHE "Experimental Technology" 设计图，由**隐藏**科技树节点
`ship_shield_tech`（tier 5，1000 点）限定，该节点通过 **SolGov Experimental Shielding
Technology Disk** 解锁 —— 一件花费 100,000 信用点（库存 1）的商人物品。
面向玩家的后果：玩家护盾是一项后期、昂贵的获得物，需要货物／商人
交互（或幸运发现），而非起始能力。
证据：`shieldgen.dm:9-92`、`:44-151`；`shield_designs.dm:1-60`；
`research/techweb/all_nsv_nodes.dm:2-10`；`traders_items.dm:362-367`。
置信度：HIGH（高）

### SHLD-005（AI / 组件护盾）
断言：AI 和小型舰船使用 `/datum/component/overmap_shields` 而非该机械。它设置
`OM.shields = src`（若护盾已存在则拒绝），带有 `max_integrity` 和 `recharge_rate`，
每 process tick 回复 `recharge_rate`。SolGov 舰船将其实例化为
`(mass*600, mass*600, mass*15)`；战机为 `(125,125,15)` 或通过一个升级。
面向玩家的后果：NPC 护盾是被动回充的 HP 池，并可由升级组件调校。
证据：`shieldgen.dm:399-443`；`types/solgov.dm:160,174,188`；`fighters/_fighters.dm:1156-1171`。
置信度：HIGH（高）

### PDSR-001（它是什么）
断言：PDSR 是 `/obj/machinery/atmospherics/components/trinary/defence_screen_reactor`，一个
nucleium 气体反应堆（而非普通机械发生器），它链接到 `OM.shields` 并投射一个
偏转性的"防御屏"。它默认不可摧毁，且仅可通过定制的
自毁或其自身的排放失效路径移除（`ex_act` 覆写引爆 + qdel）。
面向玩家的后果：它无法被射掉；禁用它需要工程行动
（自毁终端或让它熔毁）或切断其气体／电力。
证据：`pdsr.dm:15-29`、`:546-557`。
置信度：HIGH（高）

### PDSR-002（nucleium + 电力）
断言：它消耗来自 `airs[2]` 的 nucleium 和来自 `airs[1]`/`[3]` 的冷却剂。`min_power_input =
max(1e6, reaction_temperature * reaction_rate^2 * 225)`；`max_power_input = 10e6 + 2e6 * connected_relays`。
它每 tick 从电缆盈余取电。持续运行需要 ≥2.5 mol 注入速率和
充足的 nucleium，否则约束／能量输出受损，供给不足时有惩罚。
面向玩家的后果：PDSR 是一个重型电力+nucleium 消耗源，其稳定性取决于
持续反应，而非简单的开／关。
证据：`pdsr.dm:133-271`、`:339-343`。
条件：`connected_relays` = 已供电、未过载的 `defence_screen_relay` 机械数量
（`:532-538`）。
置信度：HIGH（高）

### PDSR-003（屏幕机制）
断言：屏幕具有 `integrity`、`max_integrity`、`stability` 和 `density`。受击时它扣除
integrity 并降低 stability（快速的连续命中降低 stability 更多）。`max_integrity =
(hardening% * reaction_energy_output) * (relays * 10)`；`integrity += regen% * reaction_energy_output`。
`DENSITY_LOW` 忽略低冲击抛射物和能量（BURN）武器，仅偏转 `overmap_heavy`
非燃烧弹；`DENSITY_HIGH` 偏转一切。
面向玩家的后果：屏幕与方向无关，但通过调节回复 vs.
硬化以及继电器数量来调校；速射会使它不稳定。
证据：`pdsr.dm:471-538`。
置信度：HIGH（高）

### PDSR-004（偏转 vs 吸收、稳定性崩溃）
断言：与护盾发生器不同，PDSR 的 `absorb_hit` 返回 `SHIELD_FORCE_DEFLECT` —— 抛射物被
重定向（角度改变）并继续，而非被停下。若 stability 达到 0 屏幕崩溃（`active = FALSE`），
播放 "shields down" 音效，且一个继电器被过载。屏幕在非活动时
回复 +10 stability/s，并在 100 时重新升起。
面向玩家的后果：PDSR 在维持时将来袭火力偏转开，并在被压垮时可见地／事实上地
落下；若不管它则自恢复。
证据：`pdsr.dm:471-508`；`weapons/damage.dm:34-45` 中的处理器。
条件：崩溃时的 `check_stability` 也会过载一个已供电的继电器（`pdsr.dm:502-508`）。
置信度：HIGH（高）

### PDSR-005（继电器 - 真正的弱点）
断言：`/obj/machinery/defence_screen_relay` 单元每个为 PDSR 的最大电力增加 +2 MW，为
屏幕的最大完整度增加 ×10。继电器可以 `overloaded`（用 5 电缆修理）或严重损坏
（先用 10 plasteel 修理，然后焊接器）；它们在低压
大气（<50 mol）中自我过载，每次检查 5% 几率。
面向玩家的后果：屏幕的强度由安装了多少健康继电器决定；
排气／损坏继电器室会使 PDSR 残废，而修理是电缆／plasteel／焊接器的工作。
证据：`pdsr.dm:873-1003`。
置信度：HIGH（高）

### PDSR-006（失效 / 熔毁）
断言：若约束降至 0，反应堆进入 `REACTOR_STATE_EMISSION`：粒子爆发，然后一次
大型辐射脉冲，船员闪光／击倒／厌恶，`depower_shield()`，以及对全部
四侧的象限伤害（`take_quadrant_hit(emission_energy^2/2, ...)`），然后停机。过早终止
（在 `min_power_input > 1e6` 时反应温度达到 0）会过载三个继电器并对每个象限造成
`rand(100,200)`。控制台仅在温度 < 200 时可触发受控停机。
面向玩家的后果：误操作 PDSR 会损坏它所保护的舰船；受控停机
比熔毁更安全。
证据：`pdsr.dm:226-247`、`:310-338`、`:388-412`、`:678-685`。
条件：排放幅度随 `reaction_energy_output` 和运行时间缩放。
置信度：HIGH（高）

### WRK-REPAIR-001（跨系统修理循环）
断言：修理是多通道的：(a) 亲自焊接船体装甲板（HULL-005）、(b) APNW/APNP 套件从材料+电力治疗
象限／结构（NANO-001/003）、(c) 与完好板数量挂钩的缓慢被动回复（HULL-008）、(d) 修理泡沫试剂（HULL-009）。象限 `repair_*` proc 支持 failure/bias，
但不是正常的回合内路径（APNP 直接写入象限）。
面向玩家的后果：工程有多种重叠的治疗工具，成本各不相同
（劳动／燃料 vs. 材料／电力 vs. 时间）。
证据：`hull_plating.dm:185-208`；`nano_well.dm`/`nano_pump.dm`；`hull_plating.dm:172-214`；
`armour_quadrant.dm:131-208`。
置信度：HIGH（高）

## 跨系统依赖

- **系统：武器 / 伤害路由**（`weapons/damage.dm`）：`bullet_act` 依次处理护盾 →
  `relay_damage`（内部抛射物）→ 装甲减免 → `take_quadrant_hit`。鱼雷／采矿
  `spec_overmap_hit` 设置 `hullburn`/`disruption`。所有来袭伤害都在此进入。
- **系统 15（火焰／破损）**：overmap 级的火焰即 `hullburn`（OMAP-003）；内部火焰／破损
  后果来自 `relay_damage` 在舰船 Z 层级生成真实抛射物，以及来自
  `handle_crit` 的内部爆炸预告（`damage.dm:280-305`）。大气／火焰方面
  交叉引用系统 15。
- **电力系统**：护盾发生器、PDSR 和 APNW/APNP 都要求电网*盈余*（而非仅仅
  `powered()`），因此让电网掉压会禁用所有主动防御。
- **材料 / 货物 / 科学**：APNW 需要铁／钛／银／等离子储备；护盾需要
  SolGov 设计磁盘（商人／货物）+ PROTOLATHE 部件。防御能力是一个经济消耗口。
- **移动 / 物理**（`physics.dm`）：撞击和碰撞伤害经由
  `take_quadrant_hit` 路由，因此站位和质量是防御变量。
- **回合流程**：主舰结构临界引爆结束回合（`damage.dm:221-246`）。

## 开放问题

1. 哪些运行时调用者实际上向 `repair_quadrant` /
   `repair_all_quadrants` 传入非零 `failure`/`bias`？可见的仅有 `repair_all_quadrants(eff/2000)`（failure=0）；
   其余的可能是仅任务／管理员使用。
2. `RegisterSignal(parent, COMSIG_ATOM_DAMAGE_ACT, relay_damage, override = TRUE)` 是否意味着只有
   最近链接的船体装甲板响应对给定伤害信号（覆写语义），还是所有
   板都响应？这会影响整个船体的装甲退化速度。需要一次信号语义检查。
3. 对于精灵缺少 `-0/-25/...` 后缀状态的舰船，`apply_damage_states` 的确切效果
   （会渲染一个缺失的 icon_state）—— 未针对每个子类型的精灵验证。
4. `dent_decals` 被声明但未使用；确认没有运行时代码路径（例如通过
   `vars`/管理员）向其添加，以及它是否为遗留物。
5. APNW 超频模块（电力／负载／冷却）在回合内的实际可用性 —— 未找到设计图／配方，
   因此推测为仅映射／管理员；请对照地图文件确认。
6. PDSR 的 `handle_polarity`／温度管理很复杂；确切的安全运行包络
   （注入速率 vs. 冷却 vs. 运行时间）是从代码读取但未进行数值验证。
7. `/obj/structure/overmap/slowprocess` 在**同一类型上被定义了两次** —— 一次在 `physics.dm:122`
   （hullburn + 临界时间线，无 `..()` 调用），一次在 `hull_plating.dm:172`（被动修理，带
   `. = ..()`）。DreamMaker 如何解析这个重复（合并 vs. 后者胜 vs. `..()` 链接到
   基础 `/obj/structure`）决定了被动修理和 hullburn 是否都运行。标记为
   代码级歧义；两种行为均按其各自的定义描述。
