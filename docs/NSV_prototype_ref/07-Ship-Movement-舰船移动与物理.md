> 本文为 research/evidence/ship_movement.md 的中文翻译。

# 舰船移动、驾驶与物理

NSV13 系统 #7 的研究笔记。事实来源：磁盘上当前的 `.dm`。已考虑继承关系。
所有行号引用均指 Evidence 行中所指名文件。

## 系统概述

NSV13 中的实时空间舰船移动是一套 **自定义牛顿矢量引擎**，由 Monster860（yogstation）编写，在 `physics.dm` 与 `overmap.dm` 的文件头中均有致谢。舰船**不**使用 BYOND 的格子移动器：`/obj/structure/overmap` 设置 `animate_movement = NO_STEPS` 并覆写 `Process_Spacemove()` 使其返回 `1`（`physics.dm:70-72`），从而让舰船保有自己的速度而非使用 BYOND 的漂移模型。位置以浮点 `offset`（以格为单位）加上 `velocity` 矢量（格/秒）进行追踪，整个 offset 每 tick 逐格结算为真实的 `/turf` 移动，而小数余量则通过 `pixel_x/pixel_y` 进行动画呈现。

三层协同工作：
1. `/obj/structure/overmap/process()`（`physics.dm:167`）—— 每舰运动积分器（旋转、阻力、推力、IAS、位置结算、动画）。运行于 `SSphysics_processing` 之下（`wait = 1.5` 分秒 = 0.15 秒；`physics2d.dm:11-14`）。
2. `/datum/component/physics2d` + 四叉树（`physics2d.dm`）—— 宽阶段碰撞剔除；舰船与抛射物注册 `collider2d` 形状。精细碰撞数学位于 `collision/shape.dm`（SAT，分离轴）。
3. `SSstar_system` / overmap z 层级 —— 舰船"占据"一个保留的 **treadmill**（跑步机）z 层级（`ZTRAIT_OVERMAP`）；overmap 正是无物理漂移发生之处。

驾驶：驾驶员使用舵机控制台成为舰船的 `pilot`，获得一个 `overmap_observer` 相机眼，并通过键盘（`relaymove`）、鼠标（`onMouseMove`）或 AI 控制来操控。控制动词为刹车、IAS 与鼠标移动模式的开关。

## 核心游戏循环

驾驶员坐上舵机 -> 获得远程相机 + `overmap_ship` -> 按住移动键施加推力，Q/E 或鼠标转向 -> 舰船累积 `velocity` 并朝 `desired_angle` 转动 -> 要停下，你或者开启**刹车**（主动将速度归零）或者切换 **IAS**（仅消除侧向漂移），因为**overmap 上没有任何阻力** -> 飞入舰船/小行星会发生弹性反弹并附带象限伤害，冷却为 1 秒。在 overmap z 层级上，一次推力之后你会永远漂下去；离开 overmap（位于机库/内部）时，空气阻力与地板摩擦会消耗速度。

## 机制

### MOVE-001
断言：舰船的真实世界位移源于内部的 `offset`/`velocity` 矢量，而非来自格子步进。每个 tick，`offset` 的整格部分被转换为 `Move(get_step(...))` 调用（`offset.a` 对应 EAST/WEST，`offset.e` 对应 NORTH/SOUTH），小数余量则通过 `animate(... pixel_x, pixel_y, time = time*10 ...)` 呈现。
面向玩家的后果：运动看起来平滑/亚格，且取决于引擎节拍，而非服务器的格子 tick。
证据：`physics.dm:301`（`offset._set`）、`physics.dm:308-384`（结算循环）、`physics.dm:405-408`（动画）；`overmap.dm:78-79`、`overmap.dm:18`（`animate_movement = NO_STEPS`）、`physics.dm:70-72`。
条件：`time = min(world.time - last_process, 10)/10` 秒，上限为 1 秒（`physics.dm:169-170`）。
置信度：HIGH（高）。

### MOVE-002
断言：推力沿舰船的朝向矢量施加。`angle` 为自 NORTH 起顺时针的度数；前向矢量 `fx = cos(90-angle)`、`fy = sin(90-angle)`，侧向矢量 `sx = fy`、`sy = -fx`。按住 NORTH/SOUTH 加上 `forward_maxthrust`/`backward_maxthrust`；按住 EAST/WEST 加上 `side_maxthrust`。加速度为 `thrust * time`，累加到 `velocity`。
面向玩家的后果：W 相对船头所指方向向前推进；S 是较弱的倒退；A/D 是较弱的横向平移。方向不改变量级，只改变瞄准。
证据：`physics.dm:253-293`；默认值 `overmap.dm:97-99`。
条件：仅当 `can_move()` 为真且 `brakes` 关闭时。`can_move()` 对所有非战斗机舰船返回 `TRUE`（`physics.dm:117-118`），对战斗机则为 `engines_active()`（燃料 + 已完成 spool 的引擎）。
置信度：HIGH（高）。

### MOVE-003
断言：旋转是一个朝 `desired_angle` 的 bang-bang 控制器，受 `max_angular_acceleration`（度/秒^2）限制，并转换为 `angular_velocity`（度/秒）。若剩余角度在一个 tick 的加速度范围内，控制器直接贴合；否则使用 `2*sqrt(...)` 曲线；每 tick 的速度变化为 `CLAMP(desired-angvel, -accel*time, accel*time)`。
面向玩家的后果：舰船不会瞬间转向；它们会逐渐加速并缓慢消耗角速度，重型舰船（低加速度）手感迟钝。
证据：`physics.dm:176-202`；变量声明于 `overmap.dm:85-86`。
条件：`desired_angle` 必须为数值。键盘模式下目标角度每 tick 依据按住的按键重新计算；鼠标模式则依据光标设定。
置信度：HIGH（高）。

### MOVE-004
断言：两套控制方案设定 `desired_angle`：
- **键盘：** Q = `keyboard_delta_angle_left = -15`，E = `keyboard_delta_angle_right = +15`（按住时恒定）；每 tick `desired_angle = (angle + left + right + movekey_delta_angle + 360) % 360`（`physics.dm:177-179`）。A/D 仅在 IAS 开启时转向，通过 `movekey_delta_angle = ±15`（`overmap.dm:780-786`）。
- **鼠标（`move_by_mouse`）：** `onMouseMove` 设定 `desired_angle = getMouseAngle(...)` 以指向光标（`overmap.dm:735-755`）。在距中心约 1 格的死区内被忽略。
仅当 `!move_by_mouse && !ai_controlled`（键盘）时才读取转向输入 —— 鼠标覆写键盘。
面向玩家的后果：两种截然不同的驾驶手感；战斗机默认鼠标（`overmap.dm:419`），主力舰默认键盘。
证据：`keybinding/overmap.dm:7-75`；`overmap.dm:177-179`、`735-786`。
条件：按键绑定仅在 `M == OM.pilot` 且非 `move_by_mouse` 时生效（`keybinding/overmap.dm:20-25` 等）。
置信度：HIGH（高）。

### MOVE-005
断言：玩家可用的转向输入动词与开关：Q/E 旋转，W/A/S/D（或方向键）推进，Shift = **加速/加力燃烧**（对玩家而言始终为 `boost(NORTH)`），Alt = 切换刹车，X = 切换 IAS（"惯性辅助"），C = 切换鼠标移动模式。5 = 反制措施，Capslock = 武器保险（战斗机）。
面向玩家的后果：一只手在 WASD，另一只手在 Q/E；刹车与 IAS 是两种停下的方式。
证据：`keybinding/overmap.dm`（加速 78-108、刹车 110-143、惯性 146-167、移动模式 169-190）。
移动键本身即为默认客户端移动路径（无按键绑定条目），抵达 `overmap_observer/relaymove` -> `overmap.relaymove`（`camera.dm:167-169`、`overmap.dm:775`）。
置信度：HIGH（高）。

### MOVE-006
断言：**惯性阻尼器（IAS）**是每舰独立的驾驶开关（默认 `TRUE`）。开启时，舰船每 tick 计算其速度沿侧向矢量的分量并将其消除，消除量至多为 `(mass/10 + side_maxthrust) * time`；它**不**消除前向速度。
面向玩家的后果：IAS 让舰船"电传操纵"—— 它只朝它指向的方向前进，因此行进中转向会使你的路径弯曲。它**不是**自动刹车；你仍需刹车/反推才能停下。
证据：`physics.dm:294-299`；变量 `overmap.dm:92`；开关 `overmap.dm:757-773`、`verbs.dm:22-31`。
条件：战斗机行为不同 —— 见 MOVE-013；黑洞特性 `TRAIT_NODAMPENERS` 会阻止重新启用（`overmap.dm:758-761`，在 `starsystem.dm:718` 中施加）。
置信度：HIGH（高）。

### MOVE-007
断言：**刹车**（手刹）是一个开关；开启期间，舰船不施加驾驶员推力，而是计算将前向与右向速度均归零所需的推力，并将其钳制在 `-backward_maxthrust..forward_maxthrust` / `±side_maxthrust` 范围内施加。刹车还会阻断 `Bumped()` 冲量，并在战斗机被捕获/对接时强制开启。
面向玩家的后果：刹车是一种主动停止，消耗舰船自身的推进器（笨重缓慢的舰船减速也慢）。它不是瞬时且非免费的。
证据：`physics.dm:259-268`；`physics.dm:592`（`Bumped` 提前返回）；在发射器中捕获 `fighters_launcher.dm:129`。
条件：`can_brake()` —— 战斗机在磁锁于弹射器期间返回 `FALSE`（`fighters_launcher.dm:115-120`）；主力舰 `can_brake()` = TRUE（`overmap.dm:995-996`）。
置信度：HIGH（高）。

### MOVE-008
断言：**overmap 上完全没有阻力。** 整个阻力/空气阻力代码块都以 `!SSmapping.level_trait(src.z, ZTRAIT_OVERMAP)` 为门控。在正常的舰船 treadmill z（overmap）上，速度永不被环境消耗，因此舰船会以其最后速度永远滑行。离开 overmap（机库/内部）时，空气压力会施加阻力（每格 `velocity_mag * pressure * 0.001`），而低位/贴地舰船会增加最多 `0.5` 的阻力外加刮地板伤害。
面向玩家的后果：牛顿飞行 —— 你必须主动刹车或转向再点火；不能依赖摩擦来停下。这正是 IAS/刹车存在的原因。
证据：`physics.dm:219-248`（阻力包含 `velocity_mag/pressure` 与 `has_gravity` 刮擦）、`physics.dm:240-241`（针对极高速的硬上限）。
置信度：HIGH（高）。

### MOVE-009
断言：一个硬性**速度上限钳制**在每 tick **推力加入之前**将 `|velocity.a|` 与 `|velocity.e|` 钳制到 `speed_limit`；无推力时该钳制不生效（它只在推力代码段内运行）。overmap 类型的基础 `speed_limit = 3.5`（格/秒）；战斗机有覆写。
面向玩家的后果：动力飞行的上限为 `speed_limit`，但发射/加速会暂时提高它。
证据：`physics.dm:289-290`；变量 `overmap.dm:87`。
条件：战斗机 `small_craft` 设定 `speed_limit = 7`（`fighters/_fighters.dm:29`）；轻型战斗机 10、重型 8、逃生舱 2（见 MOVE-013）。引擎等级与部件重量会对其重新缩放（MOVE-014）。
置信度：HIGH（高）。

### MOVE-010
断言：**加速 / 加力燃烧**（玩家按 Shift）。激活时（15 秒冷却 `next_maneuvre`）：`speed_limit += 5`，所有推力 `*= 5`，且对 NORTH/SOUTH 而言 `max_angular_acceleration *= 0.5`；对 EAST/WEST 则相反 `max_angular_acceleration *= 5`（钳制 0-360）且仅 `side_maxthrust *= 5`；它会施加 `add_overlay("thrust")`，并有一个 6 秒计时器恢复加速前的数值（包括 speed_limit）。
面向玩家的后果：6 秒的速度爆发，代价是（通常）转向能力与 15 秒的锁定；还会震动所有人，并可能撞倒/甩飞未系缚的船员（G 力抛掷）。
证据：`overmap.dm:881-920`；重置 `overmap.dm:914-920`；`boost()` 上的冷却参数。
条件：非 NORTH 方向由 AI 使用（`ai-skynet.dm:1646-1655`）；`check_throwaround`（`overmap.dm:936-989`）抛飞未系安全带的船员，并可在 `|angular_velocity| >= 20` 时使战斗机飞行员昏厥（耐力）。
置信度：HIGH（高）。

### MOVE-011
断言：按**质量**划分的移动档案。overmap 的 `Initialize` 依据 `mass` 分支并覆写推力/角速度/反弹数值。实装表（质量值 -> 前向/后向/侧向推力 & max_angular_accel & 反弹/侧向反弹）：
- `MASS_TINY`（1）：2 / 2 / 2，加速度 100，反弹 1.0，move_by_mouse TRUE，IAS FALSE。
- `MASS_SMALL`（2）：0.75 / 0.75 / 0.75，加速度 12，反弹 0.75。
- `MASS_MEDIUM`（3）：0.75 / 0.5 / 0.45，加速度 10，反弹 0.55。
- `MASS_MEDIUM_LARGE`（5）：0.65 / 0.45 / 0.35，加速度 8，反弹 0.40。
- `MASS_LARGE`（7）：0.5 / 0.35 / 0.25，加速度 6，反弹 0.20。
- `MASS_TITAN`（150）：0.35 / 0.10 / 0.10，加速度 2.75，反弹 0.10。
面向玩家的后果：更大的舰船加速更慢、转向更慢、反弹更少（它们"仗着体量横冲直撞"）。战斗机（TINY）推力相等且由鼠标驱动。
证据：`overmap.dm:403-462`；质量常量 `__DEFINES/overmap.dm:73-80`。
条件 / 陷阱：`speed_limit` **未**在此 switch 中设定，因此主力舰除非被覆写，否则保留类默认值 3.5。`MASS_MEDIUM_SMALL`（2.5，被 solgov 的 `types/solgov.dm:9` 使用）与 `MASS_IMMOBILE`（200，空间站）**不是**此 switch 的分支，因此这些舰船保留类默认值（前向 6 / 后向 3 / 侧向 1，加速度 180）—— 一个真实的怪癖。（解读在下方标记。）
置信度：HIGH（高）（针对表格）；对 MASS_MEDIUM_SMALL/IMMOBILE 的后果为 MEDIUM（中）。

### MOVE-012
断言：**玩家舰船**是 `/obj/structure/overmap/nanotrasen/heavy_cruiser/starter`，`role = MAIN_OVERMAP`，`mass = MASS_LARGE`（7）。因此它使用 LARGE 行：推力 0.5/0.35/0.25，加速度 6，speed_limit 3.5，反弹 0.20，IAS 开启，键盘转向。它还有一个 **hammerhead 撞击**覆写（MOVE-017）。存在更小/其他的玩家选项（`light_cruiser/starter`、`frigate/starter`、`patrol_cruiser/starter`、solgov 的 `aetherwhisp/starter`、`vnc/starter`），各自有自己的质量。
证据：`types/nanotrasen.dm:227-246`；`heavy_cruiser` 的质量在 `types/nanotrasen.dm:71`；`instance_overmap` 默认路径 `overmap.dm:268`。
置信度：HIGH（高）。

### MOVE-013
断言：**战斗机**（`/obj/structure/overmap/small_craft`）覆写了质量档案，因为其类级别的推力/角速度值被直接设定，且（对推力而言）由引擎部件重新施加：基类前向/后向/侧向 3.5/3.5/4，加速度 180，speed_limit 7，鼠标移动，IAS 来自质量（TINY）。轻型战斗战斗机设定加速度 200、speed_limit 10；重型战斗机设定推力 8/8/7.75、加速度 80、speed_limit 8；逃生舱设定 speed_limit 2 且 `can_move()`/`engines_active()` 恒为 TRUE。关键在于，**引擎部件的 `on_install` 使用 `initial(target.*)` x `tier`**，覆写了质量 switch 的数值，而其他部件从推力/速度/加速度中扣除 `weight`。
面向玩家的后果：战斗机的真实性能 = 基类数值 x 引擎等级减去每一个已装配模块的重量；一架拆空轻型战斗机远比装配满的敏捷。
证据：`fighters/_fighters.dm:16,23-29`（类）、`404-520`（子类型）、`1456-1474`（引擎 on_install）、`1063-1094`（`apply_drag`/`remove_from`）、`544-554`（引擎最后安装）。
条件：无引擎 -> 所有移动变量归零（`_fighters.dm:1467-1474`）。`can_move()` 要求 `engines_active()` = 引擎完成 spool + 燃料 > 0（`_fighters.dm:1292-1323`；逃生舱除外）。
置信度：HIGH（高）。

### MOVE-014
断言：**燃料与 spool 启动门控战斗机移动。** `small_craft/can_move()` 返回 `engines_active()`；`use_fuel()` 燃烧低温燃料（怠速 0.5 x 引擎等级，推进时 +0.25），并在耗尽时设置主警告。引擎在 `active()` 之前需要 APU 燃料管线且 RPM 完成 spool（`try_start`/`apu_spin`）。
面向玩家的后果：战斗机可因燃料耗尽、APU 燃料管线被切断、引擎淹缸或过冷无法启动而被瘫痪 —— 这些全都是破坏/战斗的杠杆。
证据：`_fighters.dm:1305-1323`、`1393-1442`。
置信度：HIGH（高）。

### MOVE-015
断言：**战斗机弹射发射。** `fighter_launcher` 磁锁住抵达的战斗机（设定刹车、将速度归零），并在发射时调用 `prime_launch()`（设定 `speed_limit = 20`，5 秒后恢复），然后沿轨道方向将 `velocity` 设定为 `±20` 格/秒。
面向玩家的后果：发射是一次爆发性的、不受控的 20 格/秒抛射，暂时超出正常速度上限；发射前瞄准船头，否则你会撞上墙壁。
证据：`fighters_launcher.dm:122-209`、`265-268`（prime_launch）。
置信度：HIGH（高）。

### MOVE-016
断言：**惯性阻尼器机器**（`/obj/machinery/inertial_dampener`）**不**影响舰船动量。它减少由 `shake_with_inertia` 施加的**相机震动**（用于碰撞与战斗机捕获），并减少 FTL 跳跃的眩晕感，按距离缩放（`reduceStrength`），且仅对有 ckey 的玩家生效。其强度/范围来自 manipulator/scanner 库存部件；对其 emag 会使其反转（增加震动）。
面向玩家的后果：它是一台舒适性/船员 QoL 机器，与驾驶员的 IAS 开关不同。
证据：`inertial_dampener.dm`（整个文件，尤其 `reduceStrength` 200-216、`ui_data` 90-100）；消费者 `mob_helpers.dm:2-23`；`FTL/ftl_jump.dm:278-288`。
置信度：HIGH（高）。

### MOVE-017
断言：overmap 对象之间的碰撞由四叉树宽阶段（`SSphysics_processing`，每 10 tick 重建）加上 SAT 窄阶段（`shape.collides`）发现，然后在 `overmap/collide()` 中结算。结果是**弹性碰撞**（基于质量），更新两舰的速度矢量，外加一个按 `0.25/32` 与 `bounce_factor` 缩放的分离位移以消除重叠。
面向玩家的后果：舰船会互相弹开；较轻的舰船被甩飞，较重的舰船几乎不动；高速撞船就像打台球。
证据：`physics2d.dm:39-76`（宽阶段）、`physics.dm:465-589`（`collide()`）、`physics.dm:587-589`（重叠分离）；`physics.dm:309-355`（逐轴反弹，使用 `bounce_factor`/`lateral_bounce_factor`）。
条件：无 `collision_response` 的 `collide()` 路径（常见/"哑"情况）仅施加伤害 —— 速度数学无论如何都会运行。
置信度：HIGH（高）。

### MOVE-018
断言：碰撞**伤害**要求合计冲撞力 `total_force >= 2`，并遵守 **1 秒冷却**（`next_collision`）。`total_force = self_ramvec + other_ramvec`（各自 = 沿碰撞轴的相对速度），然后 `*= OVERMAP_COLLISION_MAGNIFIER (4)`。每一侧的伤害为 `CLAMP(other.mass/mass, 0.2, 10) * (total_force * 0.5)`，且冲撞矢量更大的舰船自身伤害**翻倍**。伤害经由 `spec_collision_handling` 再施加到一个**象限**（`take_quadrant_hit`），溢出则作用于船体。
面向玩家的后果：撞击对两舰都造成伤害，大致与对方的质量成正比；在错误朝向上的一次快速正面撞击会造成严重象限伤害并可能击穿船体装甲。
证据：`physics.dm:511-540`；常量 `__DEFINES/overmap.dm:83-85`；象限路由 `armour/armour_quadrant.dm:6-89`；`quadrant_impact` `armour/armour_quadrant.dm:24-34`。
条件：无象限的舰船（`use_armour_quadrants = FALSE`）承受原始船体伤害。
置信度：HIGH（高）。

### MOVE-019
断言：玩家起始舰船与辛迪加驱逐舰上存在 **hammerhead 撞击加成**：若撞击角度在船头的 `HAMMERHEAD_COLLISION_GUARD_ANGLE = 55°` 以内，自身伤害为 `x0.5`，对另一艘舰船的伤害为 `x2.5`。
面向玩家的后果：起始巡洋舰是专为正面撞击并安然承受而打造的；从正面撞击驱逐舰是个坏主意。
证据：`types/nanotrasen.dm:239-245`；`types/syndicate.dm:202-208`；常量 `__DEFINES/overmap.dm:85`；施加于 `physics.dm:530-534`。
置信度：HIGH（高）。

### MOVE-020
断言：与**非 overmap 原子**（墙、门、物体、生物）的碰撞经由 `Bump()`。`bump_velocity` 由舰船速度推导；物体撞击在 `>= 3` 时对舰船造成 `min(bump_velocity^2, 5)*10` 伤害、对物体造成 `*5` 伤害；活体目标在 `> 2` 时承受 `bump_velocity*2` 伤害并被击倒。`Bumped()`（舰船被某物推动）沿推动者方向加上 `bump_impulse`（0.6），除非刹车开启。
面向玩家的后果：飞入空间站墙壁/结构会损坏舰船并可能把船员撞烂，抛射物被忽略（无巨额伤害），且舰船可被移动冲量轻轻推动。
证据：`physics.dm:591-650`；`bump_impulse` `overmap.dm:102`。
条件：层级可使舰船从某些物体下方穿过（`layer < A.layer` 提前返回，`physics.dm:619-620`）。
置信度：HIGH（高）。

### MOVE-021
断言：**小行星**是 overmap 对象（`/obj/structure/overmap/asteroid`），经由相同的 `collide()` 路径碰撞。它们没有驾驶员/AI，因此从不自行施加推力（速度保持 0）—— 它们是静态障碍物。小行星质量：小型 = 默认 `MASS_SMALL`；`medium` = `MASS_MEDIUM`；`large` = `MASS_MEDIUM_LARGE`。
面向玩家的后果：撞上小行星是一次质量失衡的碰撞 —— 舰船弹开并承受伤害，而小行星几乎不动。
证据：`mining/asteroid.dm:63-106`；`_DEFINES/overmap.dm:73-80`。
置信度：MEDIUM（中）（*基础*小行星的质量是继承的默认值，并未显式设定）。

### MOVE-022
断言：**战斗机对接**是一个 `docking_computer` 部件模式。启用对接模式后，与一个**更大**的 overmap（`mass < OM.mass`）碰撞且该 overmap 有 `docking_points` 时，会调用 `transfer_from_overmap`，它会 `forceMove` 战斗机到一个对接 turf、将其登记进 `OM.overmaps_in_ship`、重置缩放、开启武器保险，并使对接计算机进入 20 秒冷却。小行星会先加载其内部。
面向玩家的后果："开启对接模式飞入友方舰船"是归航的预期方式；战斗机无法对接至比自身更轻的物体。
证据：`fighters_launcher.dm:228-254`、`339-398`；UI 开关 `_fighters.dm:313-320`；`collide()`/`small_craft/collide` 入口 `physics.dm:449-472`。
条件：要求两者中至少一个有 FTL 引擎（保留 z）。对接冷却为每次对接后真实的 20 秒锁定。
置信度：HIGH（高）。

### MOVE-023
断言：**AI 自动驾驶**（`ai_controlled`）通过设定玩家会设定的相同 `desired_angle`/`user_thrust_dir` 来驱动舰船。AI 舰船创建一个 `dummy_pilot` 生物，每 `slowprocess`（约 0.7 秒）运行 `ai_process()`，用 `move_toward` 朝 `last_target` 寻路（会设定 `brakes = FALSE`、开启 IAS，并在距离远时 `boost(NORTH/EAST/WEST)`），并向前射线检测最多 8 格以躲避阻挡物。
面向玩家的后果：AI 舰船使用完全相同的物理与加速/刹车系统，因此其行为（以及可被利用的加速冷却）与玩家对称。登舰舰船的自动驾驶可用多用途工具在其舵机上切换（仅对 `role <= NORMAL_OVERMAP` 且有 `initial(ai_controlled)` 的舰船）。
证据：`ai-skynet.dm:1490-1535`（`ai_process`、`user_thrust_dir = move_mode`）、`ai-skynet.dm:1632-1687`（`move_toward`）；舵机开关 `computer/helm.dm:25-33`。
置信度：HIGH（高）。

### MOVE-024
断言：**载具**（`/obj/vehicle/sealed/car/realistic`，货运拖车/救护车）是该引擎的一个**独立副本**，具有地形（非太空）物理：它们在 `SSfastprocess` 下运行，拥有 `max_acceleration`、`max_turnspeed`、`static_traction`/`kinetic_traction`（打滑/漂移），以及一个会增加 `braking_efficiency = 2` 阻力的 `brakes` 标志。数值来自**挂载点**：引擎设定 `speed_limit`+`max_acceleration`（可怜巴巴的 3/1、V2 7/2、V4 10/4、V8 70/10），轮胎设定转向速度 + 抓地力。转向是 `desired_angular_velocity *= pre_forward_movement` —— 只有向前行驶时才能转向。需要插入钥匙。
面向玩家的后果：拖车开起来像（街机式）汽车，而非太空船；升级是引擎与轮胎挂载点；打滑会留下轮胎痕迹并使刹车/加速减半。
证据：`vehicles/_vehicle.dm:7-345`、`vehicles/hardpoint.dm:244-318`、`vehicles/presets.dm`。
条件：`canmove` 门控所有移动；`driver_move` 在没有正确钥匙时阻断。
置信度：HIGH（高）。

### MOVE-025
断言：**相机 / 视野跟随。** 每个就座的驾驶员/操作员获得一个 `overmap_observer` 眼，它将舰船的 `pixel_x/pixel_y` 复制到客户端，并在舰船每次 `MOVED` 时设定 `loc = ship.get_center()`（`camera.dm:181-192`）。每个操作员的客户端还会获得与舰船同步的动画 `pixel_x/pixel_y`（`physics.dm:420-426`）。键盘驾驶时会显示一个矢量叠加层指示预期航向（`physics.dm:397-402`）。驾驶 `>= MASS_MEDIUM` 的舰船会自动将客户端拉远。
面向玩家的后果：视野平滑地随舰船移动；船员能感受到舰船的运动。
证据：`camera.dm:1-205`；`physics.dm:385-426`；缩放 `camera.dm:38-41`。
置信度：HIGH（高）。

## 跨系统依赖

- **Overmap / 恒星系统：** 舰船生活在保留的 `ZTRAIT_OVERMAP` treadmill z 层级上；`reserved_z` 管理 FTL 与战斗机转换（`overmap.dm:226`、`instance_overmap` 263-310）。`forceMove` 拒绝离开 overmap z（`overmap.dm:575-578`）。
- **碰撞模块**（`collision/shape.dm`、`matrixvector.dm`）：SAT 几何支撑着舰船对舰船与舰船对小行星的结算。注意 `physics2d.dm` 文件头中关于碰撞检测依赖已弃用 C++ 钩子的警告。
- **装甲系统**（`armour/armour_quadrant.dm`）：碰撞伤害与武器伤害共用象限模型；`take_quadrant_hit` 是共同的汇点。
- **武器：** 当武器开火时，`collide()` 与象限撞击还会馈送 `handle_cloak(CLOAK_TEMPORARY_LOSS)`；速度/加速与 AI 射程相互作用。
- **电力/大气：** 战斗机移动由引擎 spool/燃料（APU、电池）门控；实体惯性阻尼器消耗线缆电力，仅用于船员舒适效果。
- **按键绑定：** `datums/keybinding/overmap.dm` 映射驾驶开关；未绑定的 WASD/方向键抵达 `relaymove`。

## 未解问题

1. `MASS_MEDIUM_SMALL`（solgov）与 `MASS_IMMOBILE` 在 `Initialize` 质量 switch 中缺失；这些舰船是否真的保留类默认值（推力 6/3/1，加速度 180）？需要一次游戏内管理员检查或测试工具。（在 MOVE-011 中标记为 MEDIUM（中））
2. `speed_limit` 仅在推力代码块内被钳制；舰船能否被外部冲量（碰撞加速/弹射器）推过 `speed_limit`，然后在超过上限的情况下滑行？代码表明可以（冲量直接设定 `velocity`），但结算循环可能有相互作用。
3. `rcs_mode`（`overmap.dm:107`）已声明但似乎在移动路径中未被使用 —— 死变量还是遗留？
4. 当驾驶员按住 W+A/W+D 时 IAS 与 `movekey_delta_angle` 的相互作用：转向与推进被合并，但侧向推力从方向位标志中减去；验证预期手感与行为。
5. `resize` 缩放对碰撞盒与伤害象限的影响，对于在 overmap <-> 内部之间转换的缩小战斗机的确切数值，本文未完全追踪。
