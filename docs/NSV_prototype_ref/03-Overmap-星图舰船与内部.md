> 本文为 research/evidence/overmap.md 的中文翻译。

# 星图：飞船、Z 层与内部空间 (Overmap: Ships, Z-levels & Interiors)

## 系统概览 (System Overview)
**星图（overmap）** 是一个移动/战斗层。一个 `/obj/structure/overmap`（`nsv13/code/modules/overmap/overmap.dm:10`）是放置在专用 z 层上的单个 atom，它*代表一整艘飞船*——其船员、房间、机械与大气存在于一个或多个独立的“内部（interior）”z 层上。玩家从不在星图上行走；他们通过控制台在那里驾驶飞船，而他们的身体仍留在内部 z 上。飞船之间的战斗（弹丸、碰撞、装甲）发生在星图上；伤害会以爆炸的形式被*中继*到内部。

不存在特殊的“星图层”。星图 z 层是标有 `ZTRAIT_OVERMAP` 的普通 z 层（见下）。飞船与弹丸共存于同一 z 上，并通过 physics2d 组件（colliders/bounds）而非平面来交互。

本文所用术语：
- **星图飞船（Overmap ship）** = `/obj/structure/overmap`（飞行的船体）。
- **内部（Interior）** = 船员占据的 z 层，与飞船相连。
- **星系（System）** = `datum/star_system`（`SSstar_system`），星图上的一个节点，拥有一个飞船实际所处的 `occupying_z`。

存在两种内部模型（`nsv13/code/__DEFINES/overmap.dm`，`interior_mode`）：
- `NO_INTERIOR` —— 无内部（普通 AI/装饰性飞船）。
- `INTERIOR_EXCLUSIVE` —— 飞船拥有整个预留 z 层，从地图模板加载。
- `INTERIOR_DYNAMIC` —— 内部被加载进从既有 z 层中划出的*turf 预留区*。

## 核心玩法循环 (Core Gameplay Loop)
1. 玩家飞船在回合开始时由 `loadWorld` 调用 `instance_overmap(config.ship_type)`（`code/controllers/subsystem/mapping.dm`）创建。对于**玩家主船**，其内部*不是*加载的模板：其 `occupying_levels`/`linked_areas` 变为所有 `ZTRAIT_STATION` z 层（即空间站地图本身）。
2. `SSstar_system` 将飞船放入其 `starting_system`（玩家船体为 “Staging”），并从一艘携带 z 层的飞船设置该星系的 `occupying_z`（`ftl_jump.dm` 的 `add_ship`）。
3. 在船上，船员使用 `/obj/machinery/computer/ship` 控制台（舵轮、战术、dradis/雷达等）。每个控制台的 `LateInitialize` 调用 `get_overmap()` 将自身绑定到正确的飞船。
4. 驾驶员接管舵轮（`start_piloting`），这会将其 client 放入一个观察者/摄像机眼并拦截点击；他们用移动动词加速船体。
5. 飞船飞行、射击、被击中。弹丸击中星图 atom；装甲象限吸收；溢出调用 `take_damage`；某些命中会以弹丸/爆炸形式中继到内部 z。主船被毁则结束回合。
6. 导航/FTL 在星系之间移动整艘飞船（因而移动其内部）（见 FTL 注记）。

## 机制 (Mechanics)

### OVERMAP-001
断言：星图飞船是专用 z 层上的一个 atom，代表一整艘飞船；船员及其环境物理上生活在独立的“内部”z 层上，星图上的射击/伤害会向下中继到该内部。
玩家可感知的后果：在战术屏幕上击中一艘飞船会在玩家所站立的飞船*内部*造成真实的爆炸、火灾与船体破损。
证据：`/obj/structure/overmap` 继承 `/obj`（`overmap.dm:10`）；`relay_damage()` 选取一个 `occupying_levels` z，在随机空间边缘生成一枚指向地图中心的新弹丸，`proj.fire()`（`weapons/damage.dm:67-96`）。`overmap_explode(linked_areas)`（在 `Destroy` 中调用，`overmap.dm:540-541`）调用 `decimate_area()`（`ai_interiors.dm`）。无内部飞船覆写：`small_craft/relay_damage` 是空操作（`damage.dm:98-99`）。
条件 / 例外：没有 `occupying_levels` 的飞船不中继任何内容（`damage.dm:69-70`）。
置信度：HIGH（高）

### OVERMAP-002
断言：星图 z 层使用一种“跑步机（treadmill）”池。一个 z 层只被预留一次（`get_reserved_z`），并在飞船 `Destroy` 时归还到一个静态 `free_treadmills` 列表，供下一艘需要它的飞船复用。
玩家可感知的后果：z 层数量有限但会被回收；一个回合会随飞船的生成与死亡而创建/复用它们，而非每艘飞船泄漏一个新 z。
证据：`var/static/list/free_treadmills`（`overmap.dm:211`）；`get_reserved_z()` 选取 `pick_n_take(free_treadmills)` 或通过 `SSmapping.add_new_overmap_zlevel(...)` 分配，设置 `reserved_z`（`overmap.dm:1002-1015`）；`Destroy()` 执行 `free_treadmills += reserved_z`（`overmap.dm:570`）。
条件：只有带 `ftl_drive` 的飞船（以及主船/实例化飞船）会分配预留 z；环境/AI 飞船通常复用星系的 `occupying_z`。
置信度：HIGH（高）

### OVERMAP-003
断言：一个星系内的所有飞船物理上共存于同一个 z 层——该星系的 `occupying_z`——它由在场的第一艘携带 z 层的飞船设置。视差与“所有权”被施加到该 z 上。
玩家可感知的后果：同一星系中的两艘飞船实际上位于同一张地图上；雷达范围、碰撞与弹丸都是针对该共享 z 的普通距离检查。
证据：`star_system.occupying_z`；当飞船持有 `OVERMAP_FLAG_ZLEVEL_CARRIER` 时，`add_ship()` 从飞船的 `reserved_z`（或 `OM.z`）设置 `occupying_z`（`ftl_jump.dm`，`add_ship`/`remove_ship`）；`force_parallax_update()` 在 `reserved_z` + `occupying_levels` 上设置视差。
例外：没有携带 z 层飞船的星系没有 `occupying_z`；移动中的飞船使用 `move_existing_object`。
置信度：HIGH（高）

### OVERMAP-004
断言：控制台或 turf 通过 `get_overmap()` 找到“它的”飞船：沿 `loc` 向上走（返回任何 `isovermap` 的祖先），然后回退到该 z 层的 `linked_overmap`，再回退到 turf 预留区的飞船。因此飞船内部的一个 `computer/ship` 会自动绑定到该飞船。
玩家可感知的后果：把飞船控制台放进船体里“就能用”；正常情况下无需将每个控制台接到特定飞船。
证据：`/atom/proc/get_overmap(failsafe)`（`__HELPERS/overmap.dm`）；`space_level.linked_overmap`（`nsv13/code/modules/mapping/space_management/space_level.dm:2`）；`forceMove` 阻止将 OM 移动到没有 `ZTRAIT_OVERMAP` 的 z 上（`overmap.dm:575`）。
例外：`get_overmap` 在无 `linked_overmap` 的 z 层（例如未链接的 z）上返回 null，产生“无已登记飞船”控制台错误（见 OVERMAP-005）。
置信度：HIGH（高）

### OVERMAP-005
断言：若 `computer/ship` 无法解析出一艘飞船，其 UI 会拒绝打开并显示错误，而非静默失败。
玩家可感知的后果：未建造在飞船中的控制台会告知用户 “Unable to locate thrust parameters, no registered ship stored in microprocessor.”
证据：`LateInitialize` -> `has_overmap()` -> `get_overmap()` + `set_position`（`code/game/machinery/computer/_ship.dm`）；在 `ui_interact` 中，`if(!has_overmap)` 会显示该确切警告；`position` 选择控制台角色。
条件：若飞船 `ai_controlled` 且控制台角色为 PILOT/GUNNER，UI 反而显示 “Automated flight protocols are still active” 并拒绝手动控制。
置信度：HIGH（高）

### OVERMAP-006
断言：接管舵轮会将玩家绑定到飞船：`start_piloting(user, position)` 驱逐该角色的当前持有者，将他们记录为 `pilot`/`gunner`，设置 `user.overmap_ship = src`，将他们加入 `operators`，并给予他们摄像机眼 + 动词。`stop_piloting` 逆转此过程并触发 `COMSIG_STOPPED_PILOTING`。
玩家可感知的后果：每艘飞船一名驾驶员 / 一名炮手；第二名玩家接管控制台会踢掉第一位；离开控制台会把摄像机快照回身体。
证据：`start_piloting`/`observe_ship`/`stop_piloting`（`nsv13/code/modules/overmap/camera.dm`）；`/mob var/obj/structure/overmap/overmap_ship`（`overmap.dm`）；角色标志 `OVERMAP_USER_ROLE_PILOT/GUNNER/...`（`__DEFINES/nsv13.dm`）；`update_overmap` 维护 `mobs_in_ship`（`__HELPERS/overmap.dm`）。
条件：大型飞船在驾驶时会拉远视角。
置信度：HIGH（高）

### OVERMAP-007
断言：玩家/目标飞船不会在完整度（integrity）为 0 时死亡——它们会进入**上层结构危急（superstructure crit）**：完整度被钳制到 10，启动 15 分钟的失效倒计时，若未修复到超过最大完整度 20% 则飞船引爆（对主船而言，则结束回合）。
玩家可感知的后果：一艘残废的玩家飞船是一场限时工程紧急事件——爆炸前有 15 分钟的警告（“T-15/T-10/T-5/T-1”）；修复约 20% 最大船体即可取消倒计时。
证据：`take_damage` 的 crit 分支：若 `overmap_deletion_traits` 含 `DAMAGE_STARTS_COUNTDOWN` **且**飞船被占据，则设置 `obj_integrity = 10`、`handle_crit()`（`weapons/damage.dm:109-114`）；`handle_critical_failure_part_1` 时间线（`damage.dm:167-218`）；`part_2` 引爆，且对 `MAIN_OVERMAP` 设置 `SSticker.force_ending`（`damage.dm:221-257`）；`try_repair` 在 `obj_integrity >= max_integrity*0.2` 时清除 crit，除非 `structure_crit_no_return`（`damage.dm:263-277`）。`handle_crit` 还会在一个链接区域生成 `explosion_telegraph` 标记（`damage.dm:146-164`）。
条件：`!(DAMAGE_DELETES_UNOCCUPIED && !has_occupants())` 子句意味着，一艘**未被占据**的、带默认特性的飞船会被立即删除，而非进入 crit。
置信度：HIGH（高）

### OVERMAP-008
断言：删除由 `overmap_deletion_traits` 位域加上 `block_deletion`/`deletion_teleports_occupants`/`destruction_effects` 管理。默认是 `DAMAGE_DELETES_UNOCCUPIED | DAMAGE_STARTS_COUNTDOWN`。
玩家可感知的后果：被占据的飞船进入 crit；空飞船直接死亡。目标/登舰目标可被设为不可摧毁。某些飞船在被摧毁时将船员传送出去（附带 200 伤害）；某些会引爆内部；战机没有可中继的内部。
证据：默认值（`overmap.dm:27-32`）；标志 `DAMAGE_ALWAYS_DELETES / DAMAGE_STARTS_COUNTDOWN / DAMAGE_DELETES_UNOCCUPIED / NEVER_DELETE_OCCUPIED / DELETE_UNOCCUPIED_ON_DEPARTURE / FIGHTERS_ARE_OCCUPANTS`（`__DEFINES/nsv13.dm`）；当 `block_deletion` 或 `NEVER_DELETE_OCCUPIED && has_occupants()` 时 `Destroy()` 返回 `QDEL_HINT_LETMELIVE`（`overmap.dm:501-502`）；`destruction_effects` 播放音效 / `overmap_explode(linked_areas)`（`overmap.dm:531,540-541`）；`deletion_teleports_occupants` 将 mob 强制移动到某个 turf + 200 伤害（`overmap.dm:560`）。登舰目标设置 `target_ship.block_deletion = TRUE`，并在成功时清除它（`code/game/gamemodes/overmap/objectives/board_ship.dm`）。战机：`DAMAGE_ALWAYS_DELETES`、`deletion_teleports_occupants = TRUE`（`fighters/_fighters.dm`）。
条件：`has_occupants()` 统计非动物 `/mob/living` 以及（若标志置位）所载星图飞船的占据者（`damage.dm:123-134`）。
置信度：HIGH（高）

### OVERMAP-009
断言：飞船具有方向性装甲。大于战机尺寸的飞船获得四个**装甲象限**（前/后 × 左舷/右舷），每个都有 `current_armour`/`max_armour`。一次命中首先被朝向入射角的象限吸收；只有溢出才会到达 `take_damage`/完整度。`use_armour_quadrants` 飞船会跳过基础子弹处理。
玩家可感知的后果：被击中何处很重要——调整船体角度或从薄弱一侧进攻会改变有效耐久；必须先打穿象限，船体的完整度才会下降。
证据：`weapons/damage.dm:52-60`（基于 `use_armour_quadrants` 的象限分支，`take_quadrant_hit(...)`）；`nsv13/code/modules/overmap/armour/armour_quadrant.dm`（`take_quadrant_hit`、`projectile_quadrant_impact`、`update_quadrants`、守卫 `nodamage`）；象限键 `ARMOUR_*`（`__DEFINES/overmap.dm`）；在 `LateInitialize` 中对 `mass > MASS_TINY` 自动设置（自定义列表记录于 `types/nanotrasen.dm` 顶部）。
条件：基础 `armor` 列表（`overmap_light`/`medium`/`heavy`）仍适用于非象限飞船，以及经 `run_obj_armor` 的溢出。
置信度：HIGH（高）

### OVERMAP-010
断言：船体装甲板（`/obj/.../hull_plate`）是装甲的物理修复/资源层：安装装甲板会提升 `armour_plates`/`max_armour_plates`；破损的装甲板会降低它们；修复效率为 `100 * armour_plates / max_armour_plates`。
玩家可感知的后果：修复一艘飞船意味着给它重新铺板；完好的装甲板越多 = 修复越快/越有效且最大船体越高。
证据：`nsv13/code/modules/overmap/hull_plating.dm`（`TryFindParent` -> `armour_plates++`/`max_armour_plates++`；破损则递减；`get_repair_efficiency = 100*(armour_plates/max_armour_plates)`）；`check_armour` 将 `max_armour_plates = armour_plates` 初始化；飞船 `slowprocess` 重新生成象限（主船）或 `try_repair`。
置信度：HIGH（高）

### OVERMAP-011
断言：飞船具有传感器/隐形模型。`sensor_profile` 增加一个探测惩罚（一次传感器 ping 会临时将 `max_tracking_range` 翻倍 + 增加惩罚）；`is_sensor_visible` 以飞船的 `alpha` 为阈值；`handle_cloak(state)` 将 alpha 动画过渡到 `cloak_factor`（0-255，默认 255）。
玩家可感知的后果：一艘飞船可能在雷达上难以或无法被看见；隐形会淡化船体 alpha；ping 会骤增所有人的可见度。
证据：`overmap.dm:52-54`；`nsv13/code/modules/overmap/radar.dm`（`send_radar_pulse` 将 `max_tracking_range` 翻倍 + `add_sensor_profile_penalty`；`is_sensor_visible` 按 alpha 0-50/51-100/101-250/251-255 分桶；`handle_cloak`）；`SENSOR_VISIBILITY_*` + `SENSOR_RANGE_DEFAULT 40` + `CLOAK_TEMPORARY_LOSS 2`（`__DEFINES/overmap.dm`）；惩罚由武器类型施加（`weapons/projectiles_fx.dm`）。
条件：雷达/DRADIS 控制台必须绑定到飞船（`set_position` 设置 `OM.dradis`）。
置信度：HIGH（高）

### OVERMAP-012
断言：飞船外观/尺寸随 `mass` 缩放。精灵由 `sprite_size`/`resize` 缩放；`resize` 以循环中重复的 `mat.Scale(0.5, 0.5)` 施加（`resize` 为 N 则把精灵减半 N 次），碰撞边界也相应设置。战机在发射时设置 `resize = resize_factor`（例如 2 -> 0.25x），在停靠时设置 `resize = 0`。
玩家可感知的后果：大飞船在战术地图上物理上更大并占据更大的碰撞边界；停靠战机时它会缩小以放入机库。
证据：`overmap.dm` 的 `LateInitialize`（质量 -> 推力/碰撞缩放）+ `physics.dm`（`resize` 循环、`mat.Scale`）；`sprite_size` 默认 64（`overmap.dm`）；战机停靠/发射的 `resize`（`fighters/fighters_launcher.dm` 的 docking_act/`transfer_from_overmap`）；`MASS_TINY/SMALL/MEDIUM/LARGE/TITAN/IMMOBILE`（`__DEFINES/overmap.dm`）。`dent_decals`（overmap.dm:56）被声明但无处引用——死代码/未使用。
置信度：HIGH（高）（精灵/resize/碰撞），MEDIUM（中）（游戏中每个 `resize` 值是否都可实际触及）

### OVERMAP-013
断言：小型飞行器（战机/运输艇）携带自己密封的座舱大气。一艘 `MASS_TINY` 飞船获得一个 `cabin_air`（20 C，约 200 L，约 101 kPa O2/N2）与一个 `internal_tank`；当座舱盖关闭/完好时 `return_air()` 产出 `cabin_air`，否则产出周围 turf 的空气。破损的座舱盖会在每次 `slowprocess` 泄漏座舱空气，均衡到 `internal_tank`。制氧机（oxygenator）战机组件会补充 `cabin_air`。
玩家可感知的后果：战机驾驶员呼吸的是驾驶舱自身的空气；一个破裂并泄漏到太空的座舱盖会杀死他；运输艇/战机需要生命维持才能在长时间暴露下生存。
证据：`overmap.dm` 的 `LateInitialize`（MASS_TINY -> cabin_air，20 C / 200 体积 / 约 101 kPa O2+N2）；`physics.dm` 的 `slowprocess`（cabin_air 温度漂移；`internal_tank` 均衡到 `RELEASE_PRESSURE = ONE_ATMOSPHERE`；座舱盖破损时泄漏）；`fighters/_fighters.dm` 的 `return_air`；`/obj/item/fighter_component/oxygenator`（+ `/plasmaman`）补充 cabin_air。
条件：只有质量 TINY 的飞船会获得座舱；更大的玩家飞船使用其真实内部大气（空间站/船体 z）。
置信度：HIGH（高）

### OVERMAP-014
断言：星图上存在持续的持续伤害（DoT），通过 `hullburn`/`hullburn_power`（燃烧）与 `disruption`（在 `slowprocess` 中随时间衰减），由某些武器/弹药施加。
玩家可感知的后果：被燃烧弹/船体燃烧武器命中后，会在命中之后持续对飞船造成伤害；disruption 会暂时削弱飞船。
证据：`overmap.dm:59-61`；`physics.dm` 的 `process`/`slowprocess`（`disruption` 衰减；`hullburn` 时 `take_damage(hullburn_power, BURN, "fire", FALSE)`）；由武器 fx 施加（`weapons/projectiles_fx.dm`）。
置信度：MEDIUM（中）

### OVERMAP-015
断言：不存在专用的星图渲染平面；飞船、弹丸与效果共享星图 z 层，并通过 physics2d collider 组件外加常规 `Bump`/碰撞处理进行交互。
玩家可感知的后果：飞船/飞船撞击与弹丸碰撞是物理碰撞体事件；“星图层”是视觉分层（planes/layers），而非独立的模拟平面。
证据：`overmap.dm` 的 `Initialize`（创建 `physics2d`）；`physics.dm` 的 `collide()`/`Bump()`/`spec_collision_handling`（例如 Hammerhead 偏转正面撞击，`types/nanotrasen.dm:239-245`）；星图在 `SSphysics_processing` 中被移动。
条件：`small_craft` 覆写碰撞签名。
置信度：HIGH（高）

### OVERMAP-016
断言：由 `possible_interior_maps` 选择两条内部加载路径：`INTERIOR_EXCLUSIVE`（拥有自己的预留 z，从飞船的某个地图加载）与 `INTERIOR_DYNAMIC`（从宿主 z 划出的 turf 预留区）。加载通过 `SSstar_system.overmap_interior_queue` / `handle_interior_inits()` 延迟，后者调用飞船的 `instance_interior()`，然后触发 `COMSIG_INTERIOR_DONE_LOADING`。
玩家可感知的后果：登舰行动随内部按需实例化而出现/消失；一艘飞船的房间可能在登舰的那一刻才被新建出来。
证据：`interior_mode` 处理（`overmap.dm:479-481`，`after_init_load_interior`）；`ai_load_interior`/`choose_interior`/`instance_interior`/`load_interior`/`post_load_interior`（`nsv13/code/modules/overmap/boarding/interiors.dm`）；`overmap_interior_queue` + `handle_interior_inits()` + `queue_for_interior_load()`（`nsv13/code/controllers/subsystem/starsystem.dm`）；`COMSIG_INTERIOR_DONE_LOADING`（`__DEFINES/overmap.dm`）。
条件：`ai_load_interior` 仅对 `INTERIOR_EXCLUSIVE` 飞船有效。
置信度：HIGH（高）

### OVERMAP-017
断言：登舰/夺取是一种脚本化的状态改变，而非仅靠伤害：打捞控制台的 EWAR 扰频器要求目标被削弱到 `required_damage_percentage`（50%）以下，不能击中 `DAMAGE_DELETES_UNOCCUPIED` 飞船，然后对目标执行 `ai_load_interior`，禁用其 AI（`ai_controlled = FALSE`，“hammerlock”）并将其交给登舰者。
玩家可感知的后果：你把一艘飞船削弱到约一半，然后黑入它以触发登舰内部并使其停止反击。
证据：`nsv13/code/game/machinery/computer/salvage.dm`（检查目标损伤 % <= `required_damage_percentage = 50`；`DISABLE_BITFIELD(overmap_deletion_traits, DAMAGE_DELETES_UNOCCUPIED)`；`ai_load_interior`；`ai_controlled = FALSE`）；内部加载路径（`boarding/interiors.dm`）。
置信度：MEDIUM（中）

### OVERMAP-018
断言：玩家主船的内部就是空间站本身，而非加载的模板。
玩家可感知的后果：玩家船体的“内部”就是服务器正在运行的那张地图；中继到内部的伤害发生在空间站的实际房间里。
证据：`loadWorld` 调用 `instance_overmap(config.ship_type)`，**不带** `folder`/`interior_map_files`（`code/controllers/subsystem/mapping.dm`）；`MAIN_OVERMAP` 分支通过 `linked_overmap` + `occupying_levels` 链接所有 `ZTRAIT_STATION` z 层（`overmap.dm`，`instance_overmap`）；`find_area` 将 `MAIN_OVERMAP` 链接到所有 `GLOB.teleportlocs`（`overmap.dm:580-584`）。
例外：实例化飞船（`INSTANCED_MIDROUND_SHIP`，例如 `nanotrasen/gunstar`）与采矿船会传入 folder/地图参数，确实会加载真实内部。
置信度：HIGH（高）

## 跨系统依赖 (Cross-System Dependencies)
- **FTL / 星图**（`ftl_jump.dm`、`SSstar_system`）：在星系间移动飞船；`occupying_z`、跳跃计时器。见 FTL 注记。
- **物理子系统**（`SSphysics_processing`）：驱动飞船的 `process`/`slowprocess`（移动、DoT、大气、resize、修复）。未在处理的飞船是惰性的。
- **Z 层特性 / 映射**（`ZTRAIT_OVERMAP`、`ZTRAITS_OVERMAP`、`ZTRAITS_BOARDABLE_SHIP`、`space_level.linked_overmap`、`add_new_overmap_zlevel`、`initialize_reserved_level`）：整个系统运行的基底。
- **控制台**（`computer/ship/*`：helm、tactical、dradis、navigation/starmap、salvage、ftl）：船员的唯一界面；通过 `get_overmap` 绑定。
- **目标 / 回合流程**：`MAIN_OVERMAP` 被毁会结束回合；`board_ship` 设置 `block_deletion`。
- **小型飞行器的大气**（`cabin_air`/`internal_tank`/oxygenator）与大型飞船的内部 turf。

## 待解问题 (Open Questions)
- `handle_critical_failure_part_1` 中公告计时器的确切插值（使用 `world.time - structure_crit_init`，因此各段仅在飞船被轮询时才触发；它是否被可靠地轮询？）。核实谁每 tick 调用 `handle_critical_failure_part_1`。
- `dent_decals`（已声明、未引用）是死代码，还是为未来的损伤叠加渲染所预留。
- 随附船体中 `resize` 值 >1 的确切可达性（战机使用 2 -> 0.25x）；典型飞船请求多少次迭代。
- `INTERIOR_DYNAMIC` turf 预留区是否曾用于玩家标准船体，还是仅用于运输艇/采矿。
- 实际设置 `possible_interior_maps` 的飞船完整列表，相对于依赖空间站内部者。
