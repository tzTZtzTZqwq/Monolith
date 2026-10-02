> 本文为 research/evidence/damage_control.md 的中文翻译。

# 损管、火灾与破口

事实来源：`D:\code\NSV13` 处的当前 NSV13 检出。所有行号引用均直接追踪。
范围：当星图战斗落地到玩家舰船*内部*时会发生什么，以及船员如何
应对（焊接装甲板、运行纳米修复套件、封堵破口、取消上层结构临界、
在 hullburn/内部火灾中求生）。重叠机制仅交叉引用，不重新推导：
- `research/evidence/overmap.md` — 删除特性、结构临界时间线、hullburn（OMAP-0xx）。
- `research/evidence/hull_shields.md` — 护盾 → 象限 → 舰体装甲板 → 结构分层，
  APNW/APNP 机械内部、装甲象限吸收。
- `research/evidence/ship_combat.md` — `bullet_act` / `relay_damage` 入口路径（COMBAT-024）。

## 系统概述

星图舰船（`/obj/structure/overmap`）是单个物理对象，其*内部*是它所"占据"的
一个或多个真实 ss13 z 层（`occupying_levels`、`linked_areas`、`overmap.dm:227,44`）。
战斗结算发生在星图对象上，但*船员所感受到的后果*被注入到那些内部
z 层中。存在三条注入通道：

1. **声音/消息中继** — `relay()`/`stop_relay()` 向每个 `mobs_in_ship` 推送声音 + 文本，
   并递归进入嵌套舰船（`overmap.dm:848-866`）。这就是"你感到舰船震动 /
   听到被击中"这一反馈循环的全部。
2. **弹丸中继** — `relay_damage()` 在内部 z 层生成一个*真实*弹丸，它
   从命中侧的地图边缘飞向舰船中心，并在那里引爆（`weapons/damage.dm:67-96`）。
3. **爆炸预警** — 上层结构临界会在链接区域内生成有预警的爆炸
   （`weapons/damage.dm:146-164,280-305`）。

船员的损管（DC）工具包对抗这些：焊接舰体装甲板（被动修复速度）、
通电的 APNW/APNP 纳米修复套件（主动结构/象限治疗）、舰体修复液泡沫、
破口封堵（智能金属泡沫、充气墙、密封剂），以及——对于舰船级火灾——什么都做不了，
因为 `hullburn` 是一种抽象 DoT，船员无法直接对抗（只有防火装甲能抵抗它）。

## 核心玩法循环

1. 敌方射击在星图上结算：护盾 → 装甲象限 → 结构（交叉引用 HULL-*）。
2. `bullet_act` 还会向内部发射一枚**中继弹丸**（`damage.dm:48-51`）；一个伤害
   音效 + 画面震动被中继给船员（`damage.dm:101-108`，受惯性阻尼器削减）。
3. 中继弹丸穿过内部并引爆：爆炸（破口 + 局部火灾）、
   等离子/燃烧空气、放射性废物，或视武器而定生成敌对生物。
4. 工程师响应：扑灭内部火灾 / 撤离预警地块、封堵破口、将
   受损舰体段焊回，并监视 APNW/APNP 修复套件。医疗人员治疗伤者。
5. 若结构在与来袭伤害的对抗中降至 0，舰船进入**上层结构临界**——一个 15 分钟的
   引爆倒计时（由 `handle_critical_failure_part_1/2` 处理）。船员必须将
   结构修复回 ≥20% 最大值，否则舰船（且对主舰而言，本回合）终结。

## 机制

### DAMCTRL-001
断言：星图命中会向舰船内部注入一枚实弹。`relay_damage(proj_type, angle)`
选取舰船的 `occupying_levels` 之一，由 `(720 + proj_angle - ship_angle) % 360`
计算命中侧（45-135=北/左舷，135-225=东/舰首，225-315=南/右舷，否则为西/舰尾），
在 `spaceDebrisStartLoc(side, z)`（该侧的地图边缘）生成弹丸，并以 ±20° 散布朝地图中心发射。
面向玩家的后果：来袭火力从视觉上正确地来自内部对应的罗盘方向，
并横穿舰船飞向中央；它不是随机的内部生成。
证据：`weapons/damage.dm:67-96`；`spaceDebrisStartLoc` = 边缘坐标（`code/game/gamemodes/meteor/meteors.dm:43-59`）。
条件：`proj_type` = 若设置则为 `P.relay_projectile_type`，否则为弹丸自身类型（`damage.dm:49-51`）；
仅对具有 `length(occupying_levels)` 的舰船生效。弹丸为 `firer=null`、`def_zone="chest"`、速度覆写 4。
例外 / 覆写：`/obj/structure/overmap/small_craft/relay_damage` 是**空操作**（`damage.dm:98-99`）——
战斗机承受星图伤害，但从不将其注入座舱内部。
置信度：HIGH（高）

### DAMCTRL-002
断言：中继弹丸的*载荷*决定内部实际发生什么，且它不是普通子弹：
它是一个 `delayed_prime` 弹丸，穿透若干墙壁然后释放其效果。
具体载荷（全部朝中心飞行，因此径向击中舰船）：
- `relayed_incendiary_torpedo`（地狱火）：`explosion(turf, 0,0,4,7, flame_range=4)` + `atmos_spawn_air("o2=75;plasma=425;TEMP=1000")` → 局部火灾 + 等离子火灾（`projectiles_fx.dm:204-231`）。
- `relayed_viscerator_torpedo`：**生成 13 只 `/mob/living/simple_animal/hostile/viscerator`**（manhacks）+ 闪光（`:233-256`）。
- `relayed_plushtorp`：无伤害的恶作剧，投掷玩偶（`:258-287`）。
- `dirty_shell_stage_two`（来自 `mac_round/dirty`）：`explosion(...,0,0,5,8, flame_range=3)`，散布 `nuclear_waste`，发射辐射弹丸（`:143-202`）。
- Phaser/PD-phaser 中继光束：`on_hit` 小型爆炸，带 `flame_range`（`:647-669`）。
面向玩家的后果：单枚鱼雷命中就可能使舰体破口（爆炸）、引发内部火灾，
并用等离子或辐射充斥舱室——因此 DC 是一个分诊问题（火灾 vs 破口 vs 辐射），
而非单一任务。
证据：按类型所列；父级 priming 逻辑 `:111-141`。
条件：穿甲引信（penetration_fuze，2-4）必须在*封闭*地块上耗尽，载荷才会在开阔地块上释放。
例外 / 覆写：某些载荷（`viscerator`、`plushtorp`）是登舰/混乱类，而非结构类。
置信度：HIGH（高）

### DAMCTRL-003
断言：在上层结构临界期间，`handle_crit(damage_amount)` 会周期性地在随机内部地块上
生成一个 `/obj/effect/temp_visual/explosion_telegraph`。它存在 6 秒，警告附近的活体
生物（"You hear a loud creak coming from above you. Take cover!" + 嘎吱音效），然后在 `Destroy` 时调用
`explosion()`，大小由原始伤害决定。这就是滴答作响的"舰船正在解体"危害。
面向玩家的后果：临界期间内部会随机标记地块以供即将到来的爆炸；船员
可以通过不站在预警上来求生，但反复的预警会把内部一点点打碎。
证据：`weapons/damage.dm:146-164`（生成），`:280-305`（警告 + 毁灭时爆炸）。
条件：目标区域 = 对 `MAIN_OVERMAP` 而言是随机 `GLOB.teleportlocs` 区域，否则是随机 `linked_areas`
条目；由 `explosion_cooldown` 限速（每 5 秒一次）（`:151-154`）。
例外 / 覆写：爆炸大小来自 `damage_level` 1-4（`≤20→1, ≤75→2, ≤150→3, else 4`）：level 4 =
`explosion(T, devastation=2, heavy=7, light=9)`；level 1 = `(0,2,2)`（`:303-304`）。
置信度：HIGH（高）

### DAMCTRL-004
断言：`nuclear_impact()`（位于 `nsv13/code/game/general_quarters/damage.dm`）是一个*舰体内核爆* proc：
它隆隆作响，震动每名船员（冷冻柜 → 仅震动，印第安纳琼斯桥段），
播放核爆命中音效，然后对每个 `linked_area` 以 70% 几率在随机地块上生成
一个 `radiation_pulse(T,1000,10)` 与 1000K 等离子/氧气空气。**该文件恰好只有一个
proc；它是该文件的全部内容。**
面向玩家的后果：一次舰船级核爆命中会使分散的内部舱室受辐射并着火。
证据：`general_quarters/damage.dm:1-22`。
条件：**无活调用者。** 唯一的调用点（`projectiles_fx.dm:620`）位于一个 `/* … */` 块内
（"Sleep for now" 核鱼雷，`:615-624`），因此该 proc 目前是死代码，除非被重新启用/管理员调用。
例外 / 覆写：`simulate_nuke`（训练）也被注释掉了（`:24-39`）。
置信度：HIGH（高）（死代码）；MEDIUM（中）关于"为重构的核鱼雷而设"。

### DAMCTRL-005
断言：舰体装甲板**通过焊接**修复，且一次焊接修复整个 3×3 段。使用 `TOOL_WELDER` 的
`hull_plate/attackby`：它收集 `src` 以及 `orange(1,src)` 中所有低于最大完整度的
`hull_plate`，扣取 `fuel_required = 1 + (受损邻居数量)`，`do_after 4 SECONDS`，然后对
**每一个**收集到的装甲板调用 `try_repair(100, user)`（即每次焊接最多 9 块板 × 每块 100 HP）。
面向玩家的后果：工程师成簇地修补舰体段；站在受损坑区焊接一块板
也会修复邻居，但附近受损板越多，消耗的焊机燃料越多。
证据：`hull_plating.dm:185-209`。
条件：需要 4 秒不中断；若 `obj_integrity >= max_integrity` 则拒绝；燃料检查失败会打印
还需要多少单位。
例外 / 覆写：装甲板默认 `obj_integrity`/`max_integrity` = 200（`:13-14`），因此一次完整焊接
在该簇中约等于两次焊接量的治疗使一块板复原。
置信度：HIGH（高）

### DAMCTRL-006
断言：舰体装甲板有一个 `armour_broken` 状态。`hull_plate/take_damage` 将板夹取到 `obj_integrity = 1`
（板永不被摧毁），并且*第一次*时设置 `armour_broken = TRUE` 并递减
`parent.armour_plates`。将其修复回满（`try_repair` 达到 `>= max_integrity`）会递增
`parent.armour_plates` 并清除该标志。板对象本身从不被战斗伤害 `qdel`。
面向玩家的后果：每当一块板被打到 1 HP，舰船就失去*修复能力*（而非一个物理
洞）；必须将其完全重新焊接才能恢复该能力。
证据：`hull_plating.dm:126-155`。
条件：递减由 `!armour_broken` 守卫，因此每次破损只发生一次；递增由
`if(armour_broken)` 守卫，因此一块板不会被重复计数。
例外 / 覆写：`hull_plate/Destroy()` 对经非战斗方式（例如建造）移除的板也执行
`parent?.armour_plates--`（`:62-64`）。
置信度：HIGH（高）

### DAMCTRL-007
断言：`GLOB.plating_repairers` 是一个全局映射（ckey → 计数），**仅当特定玩家
亲手完全修复一块先前 `armour_broken` 的板**（焊接）时才递增。被动/纳米修复
从不写入它。
面向玩家的后果：它是"亲手复原的板数"的统计/成就计数器，而非
影响修复速度的资源。
证据：`hull_plating.dm:2`（全局声明），`:150-154`（递增，需要 `user.client`）。
条件：仅在 `try_repair` 的 `if(armour_broken)` 分支内，且经由用户焊接到达时递增。
例外 / 覆写：其他地方未发现。
置信度：HIGH（高）

### DAMCTRL-008
断言：被动修复速度随完好装甲板的比例线性缩放：
`get_repair_efficiency() = 100 * armour_plates / max_armour_plates`（`hull_plating.dm:211-214`），且
`slowprocess` 每个处理 tick 调用 `try_repair(get_repair_efficiency() / 25)`（`:172-183`）。装甲板数为
零的舰船回退到 10 的固定效率（AI 的缓慢自动再生）（`:212-213`）。
面向玩家的后果：失去装甲板会拖慢*所有*被动结构治疗，因此一艘挨打更多的
舰船治疗更慢——修复装甲板是力量倍增器，而不仅仅是外观问题。
证据：`hull_plating.dm:172-183,211-214`；`max_armour_plates` 在所有板经 `check_armour` 初始化后
设置一次（`:164-170`）。
条件：对 `/obj/structure/overmap/asteroid` 跳过；仅当 `mass > MASS_TINY` 时运行（如果有装甲板，
战斗机也会再生）；使用 `use_armour_quadrants`（主舰）时它改为调用 `repair_all_quadrants(efficiency/2000)`。
例外 / 覆写：源码注释声称"80% 装甲板 ≈ 7.5 分钟，25% ≈ 30 分钟完全修复"（`:183`）——
所述意图，未对照确切的 tick 节奏验证。将*公式*视为已证实，墙钟时间视为近似。
置信度：MEDIUM（中）（公式 HIGH（高）；tick 间隔为 BYOND 对象处理节奏，约 2 秒）。

### DAMCTRL-009
断言："Hull Repair Juice"（舰体修复液）是一种批量修复舰体装甲板的试剂。`reagent/hull_repair_juice`
在地块上反应（`reaction_turf`），生成泡沫并对该地块上的每一块 `hull_plate` 调用
`try_repair(max_integrity)`。它由泡沫罐（`/obj/structure/reagent_dispensers/foamtank/hull_repair_juice`，
1500 单位）、一个高级灭火器以及一个机甲挂载分配器提供；配方 = 1 稳定剂 + 1 铁 + 1 碳 → 10 单位。
面向玩家的后果：工程师可以在地板上喷洒泡沫来快速批量治疗装甲板，而非
一块一块地焊接；它是一种合成/由货舱供应的消耗品，而非初始工具。
证据：`hull_plating.dm:66-118`；机甲分配器 `nsv13/code/game/mecha/work_tools.dm`；货舱包
`code/modules/cargo/packs.dm:697-701`。
条件：修复至 `max_integrity`（完全复原装甲板，包括 `armour_broken` 那些）。
例外 / 覆写：泡沫结构不同（`/obj/effect/particle_effect/foam/hull_repair_juice`，不滑）。
置信度：HIGH（高）

### DAMCTRL-010
断言：**首要**的救舰损管机械是 APNW/APNP 纳米修复套件：
一个 `/obj/machinery/armour_plating_nanorepair_well` 为最多四个 `.../nanorepair_pump` 供料，
每个泵分配一个象限。工程师为 well 的电力分配一个百分比，并设定每个泵的装甲-vs-结构
分配；泵消耗 `repair_resources`（在 well 中处理的材料）来治疗该象限的装甲和/或结构。
结构修复还会在 ≥20% 最大完整度时清除 `structure_crit`（除非 `structure_crit_no_return`），
并且一旦设置 `structure_crit_no_return` 就被完全阻断。
面向玩家的后果：这是工程师在战斗中和战斗后运行的机械，用于直接治疗结构
（焊接只治疗*装甲板*，而装甲板只喂养被动再生）；它需要电力、材料，且在受压时
可能过载/停机。
证据：`nsv13/code/modules/overmap/armour/nano_pump.dm`（`:61-97` 处理、象限修复、结构
修复 + 临界清除）；`nano_well.dm`（`:100-243` 电力/压力/资源处理，合金等级 1-5 → material_modifier）。
条件：well 必须被链接（`linked_apnw`），单一 well 规则（重复会警告）；每个泵需要一个象限；
过载可通过 `stress_shutdown` 使泵停机（用扳手重启，10 秒）。
例外 / 覆写：超频模块改变电力上限 / 压力阈值 / 冷却（`nano_well.dm:265-293`，`:540-566`）。
置信度：HIGH（高）

### DAMCTRL-011
断言：上层结构临界是一个 15 分钟的死亡螺旋，船员通过将结构修复到 ≥20% 最大值来取消。
触发时，完整度被夹取到 10，且 `handle_crit` 启动倒计时（`weapons/damage.dm:109-116,146-164`）。
`handle_critical_failure_part_1` 宣告 T-15/T-10/T-1，并在 10:00 时设置 `structure_crit_no_return = TRUE`
（"修复窗口已过期"），然后在 15:00 时引爆（`:167-257`）。清除需要经 `try_repair` 达到
`obj_integrity >= max_integrity*0.2`（`:271-277`）、APNP 结构修复（`nano_pump.dm:93-97`）或管理员 `full_repair`
（`armour_quadrant.dm:113-124`）。
面向玩家的后果：一艘瘫痪的玩家舰船不会立即死亡——船员有 10 分钟可修复的
宽限期，然后是 5 分钟不可避免的毁灭；计数器是*结构完整度*，因此 APNP 结构分配
是直接杠杆，而焊接装甲板只是间接的（经由喂养 `try_repair` 的被动再生）。
证据：如上；交叉引用 `research/evidence/overmap.md`（结构临界 OMAP 断言）与
`hull_shields.md`（HULL 结构临界条目）。
条件：仅对具有 `DAMAGE_STARTS_COUNTDOWN` 且有乘员的舰船；无乘员的 `DAMAGE_DELETES_UNOCCUPIED` 舰船
则直接死亡（`damage.dm:109`）。
例外 / 覆写：`try_repair` 与 `repair_quadrant`/`repair_structure` 若给定 `failure`
几率会*搞砸*——搞砸的修复会在舰船内部生成一个 `explosion_telegraph`（`armour_quadrant.dm:134-139,161-174`）；
面向船员的 APNP 路径无失败，控制台/datum 修复路径则可能不是。
置信度：HIGH（高）

### DAMCTRL-012
断言："舰船火灾"是两件不同的事。(a) 星图舰船对象本身是 **`FIRE_PROOF`**，永远
无法被设为 `ON_FIRE`（`overmap.dm:23`）——因此没有视觉上的舰体火灾；取而代之的是抽象 DoT
`hullburn`/`hullburn_power` 在 `slowprocess` 中递减，每秒造成 `hullburn_power` 点 BURN"火灾"伤害，
由舰船的防火装甲抵抗（`physics.dm:130-134`；由地狱火鱼雷 `hullburn += 60`、
电磁炮锻造弹 `hullburn += burn`、`projectiles_fx.dm:323-329,532-537` 设置）。(b) **内部 z 层**
是普通 ss13 地块，完全像空间站一样燃烧：爆炸 `flame_range` 放置 LINDA 热点
（`code/controllers/subsystem/explosion.dm:386`），可燃对象获得 `ON_FIRE` 并在
`SSfire_burning` 中燃烧（`code/game/objects/obj_defense.dm:214-237`，`code/controllers/subsystem/fire_burning.dm:19-45`）。
面向玩家的后果：船员火灾是用灭火器/泡沫扑灭的普通空间站火灾；*舰船的*
火灾是一种无人能扑灭的状态效果（只有防火装甲和时间能对抗），且它只施加于
AI 舰船 / 战斗机 / 无内部舰船（具有 `occupying_levels` 的舰船改为在内部"让它们烧"，
`projectiles_fx.dm:327,534-535`）。
证据：如上所列；交叉引用 `research/evidence/hull_shields.md`（hullburn OMAP 断言）与
`ship_combat.md`（spec_overmap_hit hullburn）。
条件：内部火灾仅在载荷的 `flame_range` 触及开阔、非太空地块处发生。
例外 / 覆写：PDSR/`pdsr.dm` 运行自己的 `slowprocess`，与火灾无关。
置信度：HIGH（高）

### DAMCTRL-013
断言：**喷淋淋浴器**（blast shower）**不是消防设备**；它是一台高压辐射/去污
淋浴器（RS-455）。`wash_mob` 移除最多 15 点辐射、洗掉乳霜/颜色、损伤穿着的衣物（8 BRUTE）、
施加约 0.5 点钝击（"peels your outer layers of skin away"）、给予 −4 心情事件，且仅对碳基
生物洗涤手持/穿着物品。对其 emag 会将自伤提高至 5 点钝击。
面向玩家的后果：它是抗辐射工具（在 dirty-shell/核内部事件之后相关），
作为船员穿行的淋浴器设置；它对火灾、热点或舰体装甲板毫无作用。
证据：`nsv13/code/game/machinery/blast_shower.dm:32-112`；建造/拆解 = 钛外壳/框架（`:2-30,114-124`）。
条件：`wash_mob(iscarbon)` 路径洗涤装备；`use_power`，`active_power_usage = 50`。
例外 / 覆写：基础 `/obj/machinery/shower` 处理此覆写所替换的通用去污/淋浴行为。
置信度：HIGH（高）

### DAMCTRL-014
断言：内部**破口用便携式屏障封堵，而非舰体装甲板**。DC 工具：
- `/obj/item/inflatable` → `/obj/structure/inflatable` 墙（25 完整度、`density`、`ATMOS_PASS_DENSITY`）
  阻挡空气/压力；将其刺穿（尖锐/带尖物品）会使其泄气成一个*破损*的充气物，必须
  用 `/obj/item/sealant`（5 秒）修补后才能再次使用（`squads/squad_items.dm:371-478`）。
- **智能金属泡沫手榴弹**（`/obj/item/grenade/chem_grenade/smart_metal_foam`）释放智能泡沫，其
  在触发时于暴露的太空/openspace 上放置 `/turf/open/floor/plating/foam`，并在区域边界掉落
  `foamedmetal` 墙——即它密封舰体开口并隔断舱室（`code/game/objects/effects/effect_system/effects_foam.dm:122-134,213-218`；反应 `others.dm:455-467`）。
- `sealant` 也会修复受损/破损的充气物（`squad_items.dm:387-397,448-456`）。
面向玩家的后果：失压用可投掷泡沫与气密充气墙加以遏制，并以密封剂
修补/补充；标准损管套件正是这些工具。
证据：`nsv13/code/modules/squads/squad_vendor.dm:1-21`（套件 = 3× 智能金属泡沫、5× 充气物、密封剂、撬棍）。
条件：泡沫/充气物是临时的、脆弱的（充气物 25 HP），且可被刺穿。
例外 / 覆写：舰体装甲板焊接（DAMCTRL-005）针对舰船上层结构能力，而非大气密封。
置信度：HIGH（高）

### DAMCTRL-015
断言：**撞击/碰撞会损伤舰船但不会注入内部弹丸。** `collide()` 计算冲击力，施加
`OVERMAP_COLLISION_MAGNIFIER`（×4），经由 `spec_collision_handling`（随后 `take_quadrant_hit`）路由伤害；
`Bump`一个非舰船对象造成 `strength*10` 自身 / `strength*5` 给对象，并可能撞倒被撞的生物。
具有舰首（"hammerhead"）的舰船覆写 `spec_collision_handling`，当冲击在其航向 55° 以内时
承受 ×0.5、造成 ×2.5。
面向玩家的后果：撞击是*舰船*完整度/装甲板的真实伤害来源，但它不会
生成内部爆炸（不同于炮火，DAMCTRL-001）——内部后果仅经由
`take_damage` → `COMSIG_ATOM_DAMAGE_ACT` → 装甲板 `relay_damage`。
证据：`physics.dm:465-541`（碰撞力 + 放大倍数 + 象限路由），`:605-650`（Bump）；
`OVERMAP_COLLISION_MAGNIFIER`/`HAMMERHEAD_COLLISION_GUARD_ANGLE`（`nsv13/code/__DEFINES/overmap.dm:83-85`）；
舰首覆写 `types/nanotrasen.dm:239-245`，`types/syndicate.dm:202-208`。
条件：需要 `total_force >= 2` 且 `world.time >= next_collision`（1 秒冷却）；由护盾/`next_collision`
门控；`Bump` 快速冲击阈值 `bump_velocity >= 3`。
例外 / 覆写：small_craft 的 `collide` 交由 `docking_act`（战斗机对接而非撞击）。
置信度：HIGH（高）

### DAMCTRL-016
断言：船员侧伤害反馈（震动、命中音效、"你感到舰船猛地一歪"）经由
`shake_everyone`/`relay` 中继，并被**惯性阻尼器衰减**：`shake_with_inertia` 找到最近的
*通电* `/obj/machinery/inertial_dampener`，并在 `shake_camera` 之前调用 `reduceStrength(distance, strength)`。
`impact_sound_cooldown` 节流中继的伤害音效（0.5-1 秒）。
面向玩家的后果：靠近工作阻尼器的船员感到的命中镜头震动更小；舰船冲击
反馈是有位置性的、依赖阻尼器的，而非均匀的。
证据：`weapons/damage.dm:15-18,23-33,102-108`；`nsv13/code/modules/mob/mob_helpers.dm:2-23`。
条件：仅具有 `ckey` 的生物才会触发阻尼器搜索。
例外 / 覆写：对 ≥15 伤害的命中，无论音效节流如何都会调用 `shake_everyone(5)`。
置信度：HIGH（高）

### DAMCTRL-017
断言：在*舰船*内部，适用标准大气失压/火灾（基础 LINDA）；NSV 专属的
新增是经中继的载荷与便携式破口工具。当舰船星图对象被摧毁时，
`overmap_explode(linked_areas)` 每 3 秒以一个区域为单位，用 `explosion(T,7,0,0, ignorecap=TRUE)` 引爆每个链接区域
（`ai_interiors.dm:35-42`），且 `deletion_teleports_occupants` 可强制移动 + 对剩余生物造成 200 伤害
（交叉引用 `overmap.md`）。
面向玩家的后果：失去星图对象是内部范围内的一连串巨大爆炸，而非干净的
消失；在此之前进行 DC 是唯一的缓解。
证据：`nsv13/code/modules/overmap/ai_interiors.dm:35-42`；`overmap.dm:531-566`。
条件：`destruction_effects` / `deletion_teleports_occupants` 标志。
例外 / 覆写：`decimate_area()`（内部清剿）存在，用于脚本化的覆灭（`ai_interiors.dm:44-67`）。
置信度：HIGH（高）

## 跨系统依赖

- **舰体/护盾/装甲（系统 12，`hull_shields.md`）** — 拥有护盾→象限→装甲板→结构吸收、
  APNW/APNP 机械、`armour_plates`/`get_repair_efficiency`、`hullburn`。本文档涵盖围绕这些
  对象的*船员工作流*；数值追踪位于那边。
- **星图（系统 3，`overmap.md`）** — 删除特性、结构临界时间线数字、`occupying_levels`、
  `linked_areas`、`relay`。交叉引用，不重复。
- **舰船战斗（系统 10-11，`ship_combat.md`）** — `bullet_act`/`relay_damage` 入口路径（COMBAT-024）；
  `spec_overmap_hit` hullburn/disruption 设置器。
- **大气与生命维持（系统 14）** — 内部火灾（LINDA 热点、`SSfire_burning`）、失压、
  座舱空气；此处的破口工具是叠加在这些系统之上的*动作*。
- **工程/电力（系统 13）** — APNW 电力抽取（标称最多 3 MW / 超频 12 MW）与压力热馈入
  舰船电力预算，并可能关闭修复套件。
- **登舰（系统 17）** — `relayed_viscerator_torpedo` 与 ODST/海盗空投舱将敌对生物注入
  内部；DC 与击退登舰者重叠。

## 开放问题

1. 确切的墙钟被动修复速率：`slowprocess` 除以 tick 节奏，而代码注释的
   "7.5 分钟 / 30 分钟"数字未与观测到的 tick 间隔对齐。需要运行时测量。
2. `nuclear_impact()` **无活调用者**（只有被注释掉的核鱼雷）。确认是否有重构的核爆
   路径意图调用它，还是它完全残留。
3. `repair_structure()`/`repair_quadrant()`（带 `failure` 几率）已定义，但只有 `repair_all_quadrants`
   有活调用者（被动再生 + 商人 `ship_repair` 服务）。确定是否有任何控制台/目标调用
   可搞砸的修复路径（搞砸的修复会生成内部 `explosion_telegraph`）。
4. 除 `bullet_act` 外还有别的东西调用 `relay_damage` 吗（例如导弹、水雷、BSA）？只有弹丸
   `bullet_act` 被追踪为内部中继触发器；确认不存在近战/碰撞中继。
5. 舰体装甲板上的 `armour_scale_modifier`（=4）已声明但似乎未被读取（在 `hull_shields.md` HULL-002 中标记）——
   再次确认没有 DC 路径读取它。
