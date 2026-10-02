> 本文为 research/evidence/holomap.md 的中文翻译。

# 全息地图与舰船测绘

## 系统概述
每个空间站/舰船 z 层的 **静态、回合开始快照**，被绘制进一张
480x480 的图标，随后作为全屏 HUD 覆盖层（全息投影地图）呈现给玩家，
并带有一个动画的“你在这里”光标。

- **子系统** `SSholomaps`（`/datum/controller/subsystem/holomaps`，
  `nsv13/code/modules/holomap/holomap_subsystem.dm`）。`flags = SS_NO_FIRE`
  （`:16-19`）——它只运行 **一次**，在 `Initialize` → `generate_holomaps()`
  （`:32-34`）。它遍历每一个带有 `ZTRAIT_STATION` 或
  `ZTRAIT_LAVA_RUINS` 特性的 z 层（`:45`）。`code/datums/map_config.dm:139-140`
  强制为任何缺少特性的层级赋予 `ZTRAITS_STATION`，因此 **所有舰船 z 层都被
  测绘**。NSV 舰船是多层的（例如 `_maps/aetherwhisp.json` = 2 层，
  上/下相连）。
  持有的数据：`holomaps[z]`（基础画布）、`extra_holomaps`（每 z 的
  `stationmapformatted`、`stationareas`、`stationmapsmall`）、
  `holomap_z_transitions[z]`、`holomap_pod_locations[z]`、
  `holomap_position_to_name[z]`、`valid_map_indexes`、
  `station_holomaps`（`:21-27`）。
- **每设备 datum** `/datum/station_holomap`（`holomap_datum.dm`）持有
  `base_map` 图像、`cursor` 图像，以及覆盖层/图例数据。每一台能显示该地图的机器
  或组件都有自己的一份。
- **显示载体**：`/obj/machinery/station_map` 壁挂机器
  （`station_holomap.dm`）以及 `/datum/component/holomap`（`code/datums/components/holomap.dm`），
  后者附于 PDA、cyborg 和 AI。
- **HUD** `/atom/movable/screen/holomap`（`holomap_hud.dm`），位于 `ui_holomap`
  （`CENTER-7,CENTER-7`）。

## 核心玩法循环
1. 玩家打开地图（点击壁挂全息地图机器，或使用来自其 PDA / borg / AI 的
   “Toggle Holomap” 动作）。
2. 静态舰船图像被放置到其 HUD 上，光标位于其位置。
3. 他们悬停以读取 **区域名称**，并可 **点击图例条目** 来切换
   覆盖层分组（楼梯/梯子/电梯、逃生舱，以及在工程地图上，火灾/大气警报）。
4. 壁挂机器的用户必须保持相邻；当他们走开、断电或右键点击时地图关闭。
   PDA/borg/AI 用户没有此类门控。
5. 这仅用于导航/诊断——没有船员追踪，没有实时损伤信息（见限制）。

## 机制

### HOLOMAP-001
断言：全息地图是一个 **在回合开始时生成一次的冻结快照**；它
永远不会被重绘。
面向玩家的后果：被摧毁的墙、船体破口、烧毁一个房间的大火，以及玩家建造的房间
**不会** 改变地图所绘制的内容。它始终显示回合开始时的几何结构。
证据：`holomap_subsystem.dm:16-19`（`SS_NO_FIRE`）、`:32-34`（只有 `Initialize`
调用 `generate_holomaps`）、`:39-52`。在整个仓库中，
`generate_holomaps()` 的唯一调用者是子系统的 Initialize。
条件：不适用。
例外 / 覆写：逃生舱与火灾/大气警报覆盖层在每次打开/点击时实时计算，
因此 *那些标记* 可以更新；底层的几何/区域颜色
则不能。
置信度：HIGH（高）

### HOLOMAP-002
断言：基础地图绘制物理结构加上部门颜色——墙、
地板、岩石、软障碍，以及每个区域的 `holomap_color`。
面向玩家的后果：玩家可以读懂舰船布局（开放地板 vs 墙）
并依据颜色图例辨认部门（Command、Security、Medical、Science、
Engineering、Cargo、Hallways、Maintenance、Arrivals、Departures、Recreation、
Service、Munition、Hangar——`_globalvars/lists/holomap.dm:7-22`）。
证据：宏 `holomap_subsystem.dm:1-8`（`IS_ROCK/IS_OBSTACLE/IS_SOFT_OBSTACLE/
IS_PATH`）；绘制循环 `:83-107`（`DrawBox` 配合 `HOLOMAP_ROCK/OBSTACLE/
SOFT_OBSTACLE/PATH` 与 `tile_area.holomap_color`）；颜色 `__DEFINES/holomap.dm:10-29`；
区域变量/覆写 `holomap_area.dm:5-256`。
条件：仅绘制其区域 `holomap_should_draw = TRUE` 的地块（`:90`）。
例外 / 覆写：`/area/shuttle`（逃生舱/电梯除外）、`/area/ruin`、
`/area/nsv/boarding_pod` 的 `holomap_should_draw = FALSE`（`holomap_area.dm:11-33`）——
这些在地图上缺失。墙是任何 `/turf/closed` 或包含一个
fulltile `/obj/structure/window` 的东西。
置信度：HIGH（高）

### HOLOMAP-003
断言：悬停地图会显示光标下的 **区域名称**。
面向玩家的后果：可用于按名称查找特定部门/房间
（例如“地图说大气储存在这里”）。
证据：`holomap_subsystem.dm:95` 按地块构建 `position_to_name`（仅在
设置了 `holomap_color` 的地方）；`holomap_hud.dm:53-70` 的 `MouseMove` 将屏幕
`maptext` 设为 `position_to_name["x:y"]`。
条件：只有具有 `holomap_color` 的区域的地块才会获得名称——未着色
（默认）区域在悬停时不显示任何内容。
例外 / 覆写：**任何** 维护色区域都会被重新标记为
`"Maintenance"`，而非其真实名称（`holomap_subsystem.dm:95`）。
置信度：HIGH（高）

### HOLOMAP-004
断言：地图 **只通过光标显示你自己的位置**；**没有船员
或物体追踪**。
面向玩家的后果：你可以相对于各部门定位自己，但
你无法在地图上看到其他玩家、blob、敌对登舰者或物品。（系统 26 的范围说明
“shows crew” 是 **不正确的**——代码不绘制任何生物标记。）
证据：光标放置 `holomap_datum.dm:63-70` 与 `initialize_holomap` 参数
由设备所在地块供给（`station_holomap.dm:61`、`holomap.dm:121`）；AI 使用其
相机视野地块（`holomap_datum.dm:26-32`）。任何地方都不存在生物/人类标记生成；
唯一的覆盖层来源是图例/z-过渡/逃生舱/警报
（`station_holomap.dm:256-312`）。
条件：仅当 `map_z` 具有 `ZTRAIT_STATION` 时才绘制光标（`holomap_datum.dm:66`）。
例外 / 覆写：对于 AI/borg 版本，光标跟随 AI 视野
（`user.client.eye`），即 AI 正在看的地方。
置信度：HIGH（高）

### HOLOMAP-005
断言：地图覆盖 **z-过渡**（梯子、上/下楼梯、电梯）以及
**逃生舱位置**（逃生舱仅在撤离激活时）。
面向玩家的后果：这是主要的实用价值——在一艘多层舰船上，它
显示你可以在哪里换层（梯子/楼梯/电梯），而在撤离期间它
显示逃生舱在哪里。
证据：生成 `holomap_subsystem.dm:109-169`（`HAS_Z_TRANSITION` /
`HAS_ESCAPE_POD_CONTROLLER` 宏位于 `:9-12`）；图例组装
`station_holomap.dm:256-271`（以及完全相同的 `holomap.dm:95-109`）；逃生舱图例仅在
`SSshuttle.emergency.mode in (SHUTTLE_CALL, SHUTTLE_DOCKED,
SHUTTLE_IGNITING)` 时添加（`:265`）。
条件：楼梯在此处产生一个 “Up” 标记，并在上方 z 层产生一个 “Down” 标记
（`:113-138`）；梯子/电梯是单一标记。
例外 / 覆写：逃生舱标记仅存在于初始时存在的逃生舱（静态列表）。
置信度：HIGH（高）

### HOLOMAP-006
断言：**工程** 全息地图额外标记具有 **活动火灾
警报** 和 **活动大气警报** 的区域；普通全息地图则不会。
面向玩家的后果：位于工程全息地图处的损管/工程玩家可以看到哪些区域当前触发了
火灾警报或大气警报并前往那里——在火灾/破口期间是真正的诊断辅助。
证据：`/obj/machinery/station_map/engineering/handle_overlays()` 在
`alarm.myarea.fire` 处添加 `Fire Alarms` 标记（`station_holomap.dm:284-298`），并在
`get_area(air_alarm).atmosalm` 处添加 `Air Alarms` 标记（`:300-310`）；`area.fire`
由火灾警报触发时设置（`code/game/machinery/firealarm.dm:148`、
`code/game/area/areas.dm:493`；清除于 `:507`），`area.atmosalm` 设置于
`areas.dm:347`。电路描述明确说 “Also shows any active fire and atmos
alarms”（`station_holomap.dm:321`）。
条件：仅在 `station_map/engineering` 子类型上；标记位于
**火灾警报 / 空气警报设备的位置**，而非火源本身。空气警报经由
`GLOB.machines`（所有机器，按 z 过滤）扫描。
例外 / 覆写：火灾警报由 `holomap_automapper.dm:1-8` 预按 z 层索引进
`GLOB.station_fire_alarms`（该文件，尽管其名称如此，
是它 *唯一* 做的事——见下文 HOLOMAP-010）。如果该机器是
`bogus`（该 z 层无地图数据）则仅返回基础覆盖层（`:286-287`）。
置信度：HIGH（高）

### HOLOMAP-007
断言：图例条目是 **可点击的开关**；右键点击关闭地图。
面向玩家的后果：玩家可以隐藏/显示标记分组（例如关闭 Fire
Alarms 杂项，或切换部门图例）而无需离开地图。
证据：`holomap_hud.dm:18-47`（右键 → `close_map`；在图例
框内左键将该条目从 `disabled_overlays` 中添加/移除并重新渲染）；
禁用的条目获得一个 `legend_cross` 覆盖层（`holomap_datum.dm:50-54`）。被禁用的
覆盖层在绘制标记时也会被跳过（`holomap_datum.dm:72-77`）。
条件：点击必须落于图例列内
（`HOLOMAP_LEGEND_X/Y/WIDTH`，`__DEFINES/holomap.dm:31-34`）。
置信度：HIGH（高）

### HOLOMAP-008
断言：配发/权限——**每个 PDA 都携带一个可用的全息地图**；cyborg 和
AI 也各获得一个。壁挂机器受相邻 + 电力门控。
面向玩家的后果：普通船员始终在其 PDA 中拥有舰船地图，
无论空间站电力如何；壁挂机器会在你走开、断电/失去锚定、
或被 EMP 时关闭。
证据：PDA 在 `LateInitialize` 中获得该组件（`code/modules/modular_computers/
computers/item/tablet.dm:414-423`）；borg `code/modules/mob/living/silicon/robot/
robot.dm:119`；AI `.../silicon/ai/ai.dm:171`。壁挂机器打开检查
`!anchored || NOPOWER||BROKEN || panel_open || already-open`（`station_holomap.dm:77`）；
移动时经由 `Adjacent` 自动关闭（`:119-130`）；`emp_act` 破坏它（`:249-254`）。
组件版本（`holomap.dm`）在
`activate_holomap` 中 **没有** 相邻/电力检查（`:111-136`）。
条件：组件按钮要求 `user.mind && stat == CONSCIOUS`
（`holomap.dm:12-13`）。
例外 / 覆写：`has_use_permission` 始终返回 TRUE（`holomap.dm:77-78`）
——PDA 地图没有职务/锁定限制。
置信度：HIGH（高）

### HOLOMAP-009
断言：壁挂机器的目标 z 层可用 **multitool** 重新配置，其
朝向可用 **wrench** 重设；两者仅在维护面板打开时。
面向玩家的后果：单台壁挂全息地图可被重新指向另一个已测绘
z 层（以查看另一层甲板）。
证据：`multitool_act` 循环 `valid_map_indexes` 并设置 `current_z_level`
（`station_holomap.dm:202-218`）；`wrench_act` → `rotate_map`（`:223-247`）。
条件：`valid_map_indexes.len > 1`。
例外 / 覆写：无（z 变更后不会重新运行 `setup_holomap`——
该变更在下次 `update_icon`/重新打开时生效）。
置信度：MEDIUM（中）（multitool 路径设置该变量但本身不会重新加载
基础图像；在下次打开时观察到）

### HOLOMAP-010
断言：尽管其文件名如此，`holomap_automapper.dm` **不** 自动注册
区域进地图——它只向 `GLOB.station_fire_alarms` 添加火灾警报。
面向玩家的后果：无直接影响；它存在是为了供给 HOLOMAP-006 的火灾
标记。真正的区域自动测绘是子系统的暴力地块循环。
证据：整个文件共 8 行，`holomap_automapper.dm:1-8`
（`firealarm/Initialize` `LAZYADD` 到 `GLOB.station_fire_alarms["[z]"]`）。
区域测绘实际发生在 `holomap_subsystem.dm:83-107`，读取
`holomap_area.dm` 中设置的 `area.holomap_color` / `holomap_should_draw`。
置信度：HIGH（高）

## 跨系统依赖
- **SSmapping / 层级特性** —— `ZTRAIT_STATION`/`ZTRAIT_LAVA_RUINS` 决定哪些
  z 层获得地图；默认特性注入 `code/datums/map_config.dm:139-140`。
- **穿梭机子系统** —— `SSshuttle.emergency.mode` 门控逃生舱标记
  （`station_holomap.dm:265`）。
- **火灾/空气警报** —— 供给工程覆盖层；`area.fire` / `area.atmosalm`。
- **PDA / 硅基** —— 携带组件形态；主要的游戏内配发途径。
- **星图 / 导航（系统 25/…）—— 无直接耦合。** 全息地图
  严格限于船体内；它不显示星图、其他舰船或接触目标。
- **图标** `nsv13/icons/holomap/*.dmi`（480x480 画布，8x8 标记，stationmap）。

## 未解问题
- **壁挂 `station_map` 在正常游玩中究竟能否获得？** 仓库中没有任何 `.dmm` 放置了
  任何 `station_map`，也没有任何 `datum/design`/科技网条目引用
  `/obj/item/circuitboard/machine/station_map`（全仓库 grep 仅在
  `station_holomap.dm` 中找到它）。有力证据表明壁挂机器 **仅限
  管理员/建造模式**；真正的玩家地图是 PDA 组件。需要确认
  压印机是否自动添加机器电路板。
- **回合开始快照在大型舰船上会劣化吗？** 生成是每个 z 层 O(maxx×maxy)，
  每行一个 `CHECK_TICK`（`holomap_subsystem.dm:172-173`）；`world.maxx/maxy`
  必须能容纳进 480px，否则会堆栈追踪（`:78-81`）。未追踪到
  任何运行时失败路径。
- `HOLOMAP_CENTER_X/Y` 是编译期由 `world.maxx/maxy` 推导的
  （`__DEFINES/holomap.dm:36-37`）；若后续地图超过 480 的行为未测试。
- 范围说明的 “shows crew” 论断被推翻——已标记给矛盾
  日志。
