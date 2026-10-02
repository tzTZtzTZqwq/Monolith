> 本文为 research/evidence/navigation_sensors.md 的中文翻译。

# 导航、传感器、雷达与 IFF

## 系统概述
overmap 舰船运作的"信息层"。每艘舰船都是一个 `/obj/structure/overmap`，并在该 datum 上携带两个相关字段：
- `sensor_profile`（`overmap.dm:53`，默认 `0`）—— 加到*其他*舰船能探测到本舰的距离上。被主动 ping、拦截以及某些武器命中时提高。
- `cloak_factor`（`overmap.dm:54`，默认 `255`）—— 舰船隐身时动画降低到的 `alpha` 下限。

面向玩家的界面有：
- **DRADIS 雷达控制台** `/obj/machinery/computer/ship/dradis`（`radar.dm`）—— 接触目标地图，外加被动/主动雷达与目标标记。
- **战术控制台** `/obj/machinery/computer/ship/tactical`（`tactical.dm`）—— 炮手的目标列表 + 外部炮相机。
- **炮相机 / 观察者之眼** `/mob/camera/ai_eye/remote/overmap_observer`（`camera.dm`）。
- **瞄准辅助**（`aim_helper.dm`）—— 炮手的鼠标瞄准/曳光。
- **IFF 控制台** `/obj/machinery/computer/iff_console`（`iff_console.dm`）—— 读取并（经由 emag）改写舰船的 `faction`。
- **天体测量控制台**（`research/astrometrics.dm`）—— 扫描星系/异常（馈送 `scan` 目标；系统 6 拥有目标）。

最关键的一个 proc 是 `is_sensor_visible(observer)`（`radar.dm:332-354`），它将目标的 `alpha` + 距离转换为一个可见性分数，被 DRADIS、目标锁定、AI 以及打捞/登舰控制台所用。

## 核心游戏循环
1. 船员阅读 DRADIS 寻找接触目标。本舰 = 青色，友军 = 绿色（始终可见），敌军 = 红色，超出分辨率的接触 = 橙色"UNKNOWN"。
2. 被动读取仅显示位于 `max(sensor_range*2, sensor_profile)` 内的敌人；完整身份（名称 + 阵营颜色）仅位于 `max(sensor_range, sensor_profile)` 内。
3. 要看得更远，驾驶员/炮手将雷达切换为**主动**（或按下脉冲）：这会短暂地使发出 ping 的舰船自身 `sensor_range` 变得极大（看见一切），但也会加上一个巨大的 `sensor_profile` 惩罚，因此*每一个*敌对 DRADIS 都能看见 ping 源。
4. 炮手 Ctrl-点击（或使用 DRADIS 辅助瞄准）来**标记/锁定**目标（2 秒 `lockon_time`）；被标记的目标成为战术控制台上可选的目标，并馈送制导武器（开火 = 系统 9）。
5. 隐身舰船（潜行配装、辛迪加潜艇、Alicorn）将 `alpha` 降到 `cloak_factor`；在它们开火、碰撞或 ping 之前，它们在数格之外对传感器几乎不可见。

## 机制

### SENSOR-001
断言：DRADIS 敌方探测有**两个阈值**：一个粗略的"那里有东西"半径 `max(sensor_range*2, target.sensor_profile)`，以及一个精确识别半径 `max(sensor_range, target.sensor_profile)`。默认 `sensor_range` 为 40（战斗机 30），因此默认值为 **80 格粗略 / 40 格精确**。
面向玩家的后果：超过 40 格，敌人渲染为橙色光点，名为 "UNKNOWN"，阵营为 "unaligned"；在 40 格内则为红色、有名、带阵营标记。友方舰船在星系内任何位置始终完全可见，无论距离/隐身。
证据：`radar.dm:380`（`sensor_visible = ... overmap_dist > max(sensor_range*2, sensor_profile) ? 0 : is_sensor_visible`）、`radar.dm:382`（`inRange = overmap_dist <= max(sensor_range, sensor_profile) || friendly || painted`），命名/颜色 `radar.dm:409-411`。`SENSOR_RANGE_DEFAULT 40` / `SENSOR_RANGE_FIGHTER 30`（`__DEFINES/overmap.dm:42-43`）。
条件：仅对敌人；要求相同的 `z`（`radar.dm:381`）。粗略光点还要求 `is_sensor_visible >= SENSOR_VISIBILITY_FAINT (0.5)`，除非目标被标记。
例外 / 覆写：`sensor_range` 是*控制台*的变量，初始化为 `base_sensor_range`（`radar.dm:221`）；战斗机/内部战斗机控制台使用 30。控制台覆写：内部 dradis（幽灵船）30、`internal/large_ship` 40、货物 dradis 50（`radar.dm:190-196,155`）。主动脉冲会暂时设定 `sensor_range = world.maxx`（见 SENSOR-003）。
置信度：HIGH（高）

### SENSOR-002
断言：`sensor_profile` 是舰船被每个观察者探测到的距离上的加成，独立于观察者自身的传感器范围。默认 0；被主动 ping、拦截以及某些弹药暂时提高。
面向玩家的后果：刚刚 ping 过 / 被某些鱼雷命中的舰船，会在约 5-10 秒内对所有敌对 DRADIS 可见，即使隔着整张地图。
证据：截断使用 `max(sensor_range*2, OM.sensor_profile)`（`radar.dm:380`、`physics.dm:441`、`overmap.dm:713`）；`add_sensor_profile_penalty(penalty, remove_in)` / `remove_sensor_profile_penalty`（`radar.dm:69-80`）。调用者：Kadesh 拦截 `add_sensor_profile_penalty(150, 10 SECONDS)`（`types/syndicate.dm:519`）；抛射物命中 `add_sensor_profile_penalty(150, 10 SECONDS)`（`weapons/projectiles_fx.dm:341,584`）；主动 ping（SENSOR-003）。
条件：惩罚是可叠加的，且当给出 `remove_in` 时通过计时器自行恢复；永久的 `remove_in = -1`（默认）永不恢复。
例外 / 覆写：FTL 重生探针舰 `/obj/structure/overmap/ref_0x000000` 硬编码 `sensor_profile = 255`，并覆写 `is_sensor_visible` 使其始终返回 `VERYFAINT`，以及 `handle_cloak` 为空操作（`FTL/ftl_jump_mishaps/jump_mishap_helpers.dm:167-188`）。
置信度：HIGH（高）

### SENSOR-003
断言：**主动雷达 ping** 会极大地膨胀 ping 源自身的 `sensor_profile`（使其对所有人显眼），同时也会膨胀其自身的 `sensor_range`（使其看见一切），持续 `RADAR_VISIBILITY_PENALTY = 5 SECONDS`。脉冲频率受限：`radar_delay` 被钳制在 `MIN_RADAR_DELAY 5s` 与 `MAX_RADAR_DELAY 60s` 之间（默认 5 秒）。
面向玩家的后果：使用主动雷达是一种交换 —— 瞬间的全图态势感知，代价是把自己点亮成"一棵圣诞树" 5 秒。该脉冲还会向 255 格内的*敌*舰发出可听见的 ping（阵营检查会跳过同阵营）。
证据：DRADIS 的 `send_radar_pulse()` 设定 `sensor_range = world.maxx`，加上 `sensor_profile penalty = sensor_range (=world.maxx)` 持续 5 秒，然后恢复 `radar.dm:93-100`。Overmap 的 `send_radar_pulse()` 使 `max_tracking_range` 翻倍，并加上同样的 profile 惩罚持续 5 秒（`radar.dm:82-91`）。`is_sensor_visible` 在脉冲后 5 秒内返回 `SENSOR_VISIBILITY_FULL`（`radar.dm:338`）。仅对敌人的 ping 声经由 `relay_to_nearby(..., faction_check=TRUE)`，它会跳过同阵营（`radar.dm:87`、`overmap.dm:876-878`）。常量 `radar.dm:1-3`；`can_radar_pulse()` 门控 `radar.dm:45-51`。
条件：`can_use_radar` 在 `dradis/minor`（空中交通管制）与 `internal`（战斗机）控制台上为 FALSE（`radar.dm:131-133,192`）；在主 dradis 与 `internal/large_ship` 上为 TRUE。在主动传感器模式下，控制台在 UI 每次自动更新且能使用时自动脉冲（`radar.dm:442-443`）。
例外 / 覆写：脉冲不要求目标；它是全图范围的。ping 源自身的 `sensor_range` 被设定为 `world.maxx`（所有坐标），因此它在 5 秒内看见整个 z 层级。
置信度：HIGH（高）

### SENSOR-004
断言：**隐身在 15 tick 内将 `alpha` 动画降至 `cloak_factor`。** `is_sensor_visible` 对 `alpha` 分桶：
- `0–50` -> `SENSOR_VISIBILITY_GHOST (0)` —— "无法以任何方式探测"。
- `51–100` -> `VERYFAINT (0.25) + 1/dist`，已钳制。
- `101–250` -> `FAINT (0.5) + 1/dist`，已钳制。
- `251–255` -> `FULL (1)`。
面向玩家的后果：标准潜行隐身（`cloak_factor = 100`）仅在大约 4 格内对敌方传感器可见（0.25 + 1/dist >= 0.5 => dist <= 4），且仅在非常近时才完全解析；超出该范围它根本不会绘制在 DRADIS 上（SENSOR-001 中 80 格的门控仍先适用）。进入约 1 格内的隐身舰船会被正常探测到。
证据：`is_sensor_visible` `radar.dm:346-354`；`handle_cloak` `radar.dm:356-370`。定义 `SENSOR_VISIBILITY_*` `__DEFINES/overmap.dm:36-40`。
条件：硬门控 `max(sensor_range*2, sensor_profile)` 在 `is_sensor_visible` 之前应用（`radar.dm:380`），因此隐身只能在该半径内隐藏你；距离项（`1/dist`）保证近距离可击破隐身。
例外 / 覆写：`handle_cloak(CLOAK_TEMPORARY_LOSS)` 重新提亮到 255，并在 `15 SECONDS` 后重新隐身。触发条件：开火（`weapons/.../firing.dm:17`）、碰撞（`physics.dm:475,594,627`）。`ref_0x000000` 覆写 `handle_cloak` 为空操作。找到的隐身系数：潜行配装 100（`gamemodes/pvp/items.dm:260`）、辛迪加潜艇 100 / 精英 80、Alicorn 100（`types/syndicate.dm:442,470,581`）。
置信度：HIGH（高）

### SENSOR-005
断言：**目标标记 / 锁定**是一个刻意的、有时序的动作：`start_lockon` 向目标播放"被锁定"警告，然后在 `lockon_time = 2 SECONDS` 后 `finish_lockon` 将目标加入 `target_painted`。你最多可持有 `max_paints = 3` 个标记（战斗机：1）。标记需要视觉捕获或有传入的数据链。
面向玩家的后果：你无法立即指定目标；有一个 2 秒的窗口，期间受害者会被警告他们正被锁定。超出的标记会驱逐最旧的。
证据：`start_lockon` `overmap.dm:629-640`；`finish_lockon` `overmap.dm:642-663`；门控 `target.is_sensor_visible(src) < SENSOR_VISIBILITY_TARGETABLE (0.70)` 阻止标记低可见性目标（`overmap.dm:647`）；`max_paints = 3`（`overmap.dm:186`），战斗机 1（`fighters/_fighters.dm:39`）。友军误伤防护 `overmap.dm:632`。炮手 Ctrl-点击触发锁定（`tactical.dm`/`overmap.dm:618-621`）。
条件：`SENSOR_VISIBILITY_TARGETABLE = 0.70`（`__DEFINES/overmap.dm:37`）—— 一艘隐身舰船（VERYFAINT+距离）必须非常近才能被标记；alpha 51–100 需要 dist <= 2（0.25+1/2=0.75）才能达到 0.70。
例外 / 覆写：数据链提供的标记绕过可见性检查（`overmap.dm:647` 仅在 `!data_link_origin` 时适用）。`select_target` 设定 `target_lock`（制导武器的"活动"目标）—— 与仅仅被标记不同（`overmap.dm:665-676`）。`can_lock` 变量存在（`overmap.dm:182`）。
置信度：HIGH（高）

### SENSOR-006
断言：被标记目标的跟踪**仅在**其保持可见时**维持**；若舰船停止探测它达 `target_loss_time = 3 SECONDS`，锁定会被解除并显示 "Target lost" 消息。数据链跟踪在接收舰船自身能看到目标后，会交接给其自己的跟踪。
面向玩家的后果：隐身/打断视线约 3 秒即可甩掉敌人的锁定。
证据：`physics.dm:437-447` 中的跟踪循环（使用 `max(dradis.sensor_range*2, OM.sensor_profile)` 与 `is_sensor_visible`）；`target_loss_time = 3 SECONDS`（`overmap.dm:187`）；`dump_lock` `overmap.dm:678-686`；数据链交接 `check_datalink` `overmap.dm:710-722`。
条件：基于信号 —— 若目标被 qdel 或改变 FTL 状态，锁定也会解除（`finish_lockon` 在 `COMSIG_PARENT_QDELETING`、`COMSIG_FTL_STATE_CHANGE` 上注册 `dump_lock` `overmap.dm:660`）。
例外 / 覆写：数据链来源失去*其*锁定时，会连带抛弃依赖的锁定（`overmap.dm:654,710`）。
置信度：HIGH（高）

### SENSOR-007
断言：**数据链**让舰船将其锁定的目标传输给同阵营的友军（该友军须有真人炮手与空闲标记槽），使友军无需视线即可获得锁定。
面向玩家的后果：一艘侦察/电子战舰可以为战列舰的火炮标记目标。
证据：`datalink_transmit` 要求 `can_use_datalink()`、`target.faction == faction` 与 `target_lock`（`overmap.dm:699-707`）；`can_use_datalink` 默认 TRUE（`overmap.dm:695`）。从 DRADIS 的 "hail" 动作调用，当对同阵营目标开启 `dradis_targeting` 时（`radar.dm:284-289`）。
条件：要求发送者持有 `target_lock`，接收者为玩家配员（`!ai_controlled` 且有 `gunner`）且未达 `max_paints`。
例外 / 覆写：一旦接收舰船能自己看见目标，数据链标志被清除，它便独立跟踪（`overmap.dm:719-722`）。
置信度：HIGH（高）

### SENSOR-008
断言：**炮相机**（观察者之眼）默认跟随你自己的舰船；当你有 `target_lock` 时它跟随目标，但仅当目标在 `visual_range` 内（默认 `SENSOR_RANGE_DEFAULT 40`，或舰船 DRADIS 的 `visual_range`）。
面向玩家的后果：炮手的外部视野会自动弧转至锁定目标以便瞄准，但超出视觉范围便会失去它。
证据：`overmap_observer/update` `camera.dm:181-192`；`track_target` `camera.dm:195-203`；`tactical.dm` 的 "toggle_gun_camera" 强制 `overmap_dist > scan_range` 拒绝 `tactical.dm:65-75`。`no_gun_cam` 在战斗机上禁用它（`fighters/_fighters.dm:51`），并被 tactical 读取（`tactical.dm:137`）。
条件：相机对象是 `mob/camera/ai_eye/remote/overmap_observer`，由 `start_piloting` 中的 `observe_ship`/`CreateEye` 创建（`camera.dm:19-129`）。
例外 / 覆写：`visual_range` 是 DRADIS 控制台变量，因此没有 dradis 的舰船回退到 40（`camera.dm:186`）。Dropships 设定 `no_gun_cam = FALSE`（`general_quarters/dropship_types.dm:35`）。
置信度：HIGH（高）

### SENSOR-009
断言：**IFF 只是舰船的 `faction` 字符串。** IFF 控制台显示舰船注册的名称 + 阵营签名，通常为只读：要更改 IFF 必须先对其 emag，然后用多用途工具黑入（`do_after` 时长 `hack_goal`，默认 `2 MINUTES`），之后它在 `syndicate` 与 `nanotrasen` 之间切换 `faction`。
面向玩家的后果：被劫持的 IFF 会翻转友/敌分类 —— 一艘被盗的 NT 舰船可冒充辛迪加（反之亦然），改变 DRADIS 将谁标为友/敌、AI 舰船攻击谁，以及数据链的可用性。
证据：examine 显示 `IFF Signature: [OM.faction]`（`iff_console.dm:55-62`）；emag 门控 `emag_act` 设定 `EMAGGED`（`iff_console.dm:97-104`）；黑入 `multitool_act` -> `do_after(hack_goal)` -> `hack()`（`iff_console.dm:79-95`）；`hack()` 切换阵营（`iff_console.dm:133-162`）。
条件：`hack_goal = 2 MINUTES`（`iff_console.dm:17`），且它在失败时*不重置* —— 一次失败尝试会按已用时间减少 `hack_goal`，最低到 1 秒的下限（`iff_console.dm:87-90`）。黑入进行时会在控制台的无线电上广播 "Unauthorized IFF transponder access detected in [area]"（`iff_console.dm:69-77`）。
例外 / 覆写：`iff_console/boarding` 在辛迪加频道上以已 emag 状态启动，以便登舰者可立即翻转一艘被俘舰船（`iff_console.dm:64-67`）。若舰船处于 `hammerlocked`（正被登舰），控制台在重建时重新 emag 并选取辛迪加/海盗频道（`iff_console.dm:33-49`；`boarding/interiors.dm:102`）。黑入一艘 NT 主 overmap 会生成一支 SolGov 拦截舰队 + Code Charlie 公告（`iff_console.dm:141-155`）。若预设，`faction` 在初始化时镜像回 `OM.faction`（`iff_console.dm:46-49`）。
置信度：HIGH（高）

### SENSOR-010
断言：`faction` 门控几乎所有友/敌逻辑，因此 IFF 真实地驱动战斗。所见到的阵营值：`nanotrasen`、`syndicate`、`solgov`、`pirate`、`hostile`、`unaligned`。
面向玩家的后果：同一艘实体舰船纯粹依据其 `faction` 字符串被当作友军或敌军 —— DRADIS 颜色、数据链、AI 目标选择、打捞/登舰资格，以及导弹锁定敌人。
证据：DRADIS 友军分支 `OM.faction == linked.faction`（`radar.dm:382,402`）；数据链阵营检查（`overmap.dm:700`）；AI 仅寻找 `ship.faction != faction` 且要求 `is_sensor_visible >= TARGETABLE`（`ai-skynet.dm:1725`）；打捞控制台仅列出 `is_sensor_visible(linked) > FAINT` 的舰船（`salvage.dm:60`）；导弹锁定 `add_enemy(firer)`（`weapons/.../autonomy.dm:247-256`）。
条件：`faction` 按舰船类型或由游戏模式设定（`SSstar_system.faction_by_id`，被 AI 舰队设置使用 `ai-skynet.dm:341,784`）。
例外 / 覆写：`can_friendly_fire()`（默认 FALSE）让舰船绕过同阵营锁定阻断（`overmap.dm:626-632`）。SolGov/NT 关系在舰队结盟逻辑中处理（`ai-skynet.dm:767-769`）—— 超出范围（外交）。
置信度：HIGH（高）

### SENSOR-011
断言：DRADIS 可在 `hail_range = 50` 格内**鸣叫（hail）**另一艘舰船，开启双向文本消息（中继给双方船员）。它有一个 10 秒的反刷屏冷却。
面向玩家的后果：与商人、AI 舰船或其他玩家通讯；用于交易与角色扮演，而非战斗。
证据：`hail` 动作 -> 若 `overmap_dist <= hail_range` 且 10 秒冷却则 `try_hail`（`radar.dm:280-300`）；`hail()` 中继格式化文本（`ai-skynet.dm:623-635`）；`hail_range = 50`（`radar.dm:34`），战斗机 30（`radar.dm:191`）。
条件：若开启 `dradis_targeting` 且使用者为驾驶员/炮手，同一次点击改为标记/中继（见 SENSOR-007），因此鸣叫与瞄准共用一个按钮（`radar.dm:284-289`）。
例外 / 覆写：商人覆写 `try_hail`（`traders_items.dm:18`）。货物 DRADIS 将同一次点击重新用于货运投放（`radar.dm:296-298`）。
置信度：HIGH（高）

### SENSOR-012
断言：**异常**在 DRADIS 上以橙色光点显示，针对同一 z 层级上的每个异常（无距离门控），在扫描前显示为 "anomaly"，之后显示名称。扫描在**天体测量**控制台（研究权限）进行，而非 DRADIS。
面向玩家的后果：导航可以看到全星系范围的异常；只有扫描（15 秒星系扫描 / 2 分钟异常扫描）才会命名它们并计入研究/目标进度。
证据：DRADIS 异常循环无距离检查，`(OA.scanned) ? OA.name : "anomaly"`（`radar.dm:376-378`）；天体测量扫描 `scan_goal_system = 15 SECONDS`、`scan_goal_anomaly = 2 MINUTES`（`research/astrometrics.dm:19-20`）；完成时加入 `linked.scanned` 并发送 `COMSIG_ANOMALY_SCANNED`（`astrometrics.dm:137-151`）；`scan` 目标监听它（`gamemodes/overmap/objectives/scan.dm:35`）。
条件：天体测量 `max_range = 40` 光年，且扫描星系需要相同的 `sector`（`astrometrics.dm:81-82`）。DRADIS 上的异常可见性由 `showAnomalies` alpha 滑块切换（`radar.dm:26,378`）。
例外 / 覆写：扫描严格来说并非传感器机制（使用子空间阵列，而非 `is_sensor_visible`）。系统 6 拥有目标；此处仅作记录。
置信度：HIGH（高）

### SENSOR-013
断言：采矿 DRADIS 显示**小行星**并以 `mining_sensor_tier` 门控它们；该等级可通过插入 `mining_sensor_upgrade` 物品来提高（等级越高 = 可见的更有价值/更稀有，按等级 1/2/3 颜色编码）。
面向玩家的后果：矿工只能看见他们实际能开采的小行星；升级会揭示更好的。
证据：小行星分支跳过 `required_tier > mining_sensor_tier`，按等级上色（`radar.dm:385-397`）；通过 `attackby(/obj/item/mining_sensor_upgrade)` 升级（`radar.dm:316-328`）；`show_asteroids` 由 `dradis/mining` 子类型设定（`radar.dm:179-184`）。
条件：`show_asteroids` 必须为 TRUE；小行星可见性滑块 `showAsteroids`。
置信度：HIGH（高）

### SENSOR-014
断言：DRADIS 接触光点有一个**音调警报**：当敌对接触数量增加时，控制台播放 `contact.ogg` 并显示 "DRADIS contact"/"Multiple DRADIS contacts"。传入的制导弹药发射也会触发雷达锁定警告并附带方位。
面向玩家的后果：即使不盯着屏幕，船员也会被声音提醒有新敌人；导弹锁定会向目标宣告方位。
证据：舰船数量增量音调 `radar.dm:419-423`；导弹锁定警告 `on_missile_lock` -> `dradis.relay_sound(launchwarning)` + 方位消息（`weapons/.../autonomy.dm:247-256`），反刷屏 10 秒。
条件：舰船计数在 FTL 状态变化时重置（`radar.dm:214-217`）。
例外 / 覆写：战斗机设定 `start_with_sound = FALSE` 以避免环境音循环吵闹（`radar.dm:16-17,189`）。
置信度：HIGH（高）

### SENSOR-015
断言：**打捞/登舰控制台**依据传感器可见性决定你可以登舰哪些舰船 —— 它仅列出你所在 z 上具有 `interior_mode == INTERIOR_EXCLUSIVE` 且 `is_sensor_visible > SENSOR_VISIBILITY_FAINT` 的舰船。
面向玩家的后果：一艘隐身/潜行的敌对舰船在至少微弱可见之前无法被选为登舰目标。
证据：`salvage.dm:59-62`；打捞还要求目标受损至低于 `required_damage_percentage` 之后 EWAR 干扰才会生效（`salvage.dm:93-95`）。
条件：相同 z；`linked` 必须有一个 overmap。
置信度：HIGH（高）

### SENSOR-016（注 —— AI 传感器使用）
断言：AI 舰船使用 `max_tracking_range`（默认 50，大型船体提高到 90/70）狩猎，并将其与 `max(max_tracking_range, target.sensor_profile)` 合并，要求 `is_sensor_visible >= SENSOR_VISIBILITY_TARGETABLE` 才能保持目标。Ping 会暂时使 AI 的 `max_tracking_range` 翻倍。
面向玩家的后果：一艘潜行舰船可通过降到可瞄准可见性以下来甩掉 AI 追击；一艘 ping 的舰船会从远得多的地方被追击。
证据：`ai-skynet.dm:1511,1725`；`max_tracking_range = 50` 默认（`ai-skynet.dm:1303`）；Kadesh 70 / Fist of Sol 90（`types/syndicate.dm:498,535`）；SolGov 70（`types/solgov.dm:98`）；ping 使其翻倍（`radar.dm:89-90`）。
条件：仅 AI；开火超出范围（系统 9）。
置信度：HIGH（高）（机制），MEDIUM（中）（每船体的精确调校散布在各类型文件中）

## 跨系统依赖
- **Overmap 核心（`overmap.dm`）** 拥有 `sensor_profile`、`cloak_factor`、`target_painted`、`target_lock`、`max_paints`、`lockon_time`；此处一切均读取这些。
- **武器 / 开火（系统 9）**：开火时 `handle_cloak(CLOAK_TEMPORARY_LOSS)`（`weapons/.../firing.dm:17`）；导弹锁定馈送 PDC/AI 的 `torpedoes_to_target` 与发射警告（`weapons/.../autonomy.dm`）；制导武器消耗 `target_lock`。
- **AI（ai-skynet.dm）**：为目标获取与追击消费 `is_sensor_visible` + `max_tracking_range`。
- **登舰 / 打捞**：以 `is_sensor_visible` 作为可登舰门控；`hammerlocked` 与 IFF 控制台重新 emag 相连（`boarding/interiors.dm:102`）。
- **目标（系统 6）**：`scan` 目标监听 `COMSIG_ANOMALY_SCANNED`；`board_ship` 监听 `COMSIG_SHIP_BOARDED`，该信号由 `iff hack()` 发送（`iff_console.dm:129`）。
- **阵营 / 游戏模式**：由游戏模式/舰队代码写入的 `faction` 字符串决定 DRADIS、数据链与 AI 的友/敌。

## 未解问题
- 是否真有玩家可访问的舰船达到 `alpha <= 50`（GHOST，"无法以任何方式探测"）？所有找到的 `cloak_factor` 值均为 80-255，因此 GHOST 桶似乎只能被短暂达到，或通过管理员/typeof 编辑。未确认。
- `radar_delay` 是通过 `ui_act` 中的原始 `input()` 设定的（`radar.dm:305-310`）—— 这是一个嵌入了浏览器输入的 TGUI 控制台；玩家看到的是 BYOND 输入框还是无 TGUI 的提示属于 GUI 关注点，超出范围。
- DRADIS 的 "faint reduced ping" 分支（`radar.dm:413-414`）仅在正好 `FAINT (0.5)` 或被标记时渲染，因为外层门控（`radar.dm:381`）已要求 `>= FAINT`。低于 0.5 的接触是否本应淡显渲染（并依赖门控按设计关闭）含义不明。
- `can_use_datalink()` 在 `nsv13/` 中未找到任何覆写；假定对有玩家炮手的舰船普遍为 TRUE。
- 最大范围数值（`max(sensor_range*2, ...)`）存在于*控制台*上，因此两艘拥有同类 dradis 的舰船会对称地互相探测 —— 但一艘没有 dradis 的舰船仅在 `physics.dm`/`camera.dm` 中回退到 40，而非在 DRADIS UI 路径中。未测试的边缘情况。
