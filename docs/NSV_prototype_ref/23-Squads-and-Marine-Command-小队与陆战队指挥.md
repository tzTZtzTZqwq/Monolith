> 本文为 research/evidence/squads.md 的中文翻译。

# 小队与陆战队指挥

NSV13 系统 #23 的研究笔记。真相来源：当前的 `.dm` 代码。所有路径相对于
`D:\code\NSV13`。

## 系统概述

小队是回合持久的 **datum**（`/datum/squad`），将普通船员编组为小型、以颜色区分的火力
小组，各自拥有聊天频道、目标与权限。一个全局单例 `/datum/squad_manager`
（GLOBAL_DATUM_INIT `GLOB.squad_manager`，`squad_manager.dm:1`）在 SS 初始化时预先
创建所有小队，并为整个回合持有静态查找列表。小队在类型层面 **不** 与职业绑定：任何
人类都可被放入其中，但一份硬性的 `disallowed_jobs` 列表阻止指挥/安保/工程/医疗/飞行员/
军火职业自动加入。"Marine（陆战队）" 就是实习船员（Midshipman）这一职业
（`/datum/job/assistant`，`__DEFINES/jobs.dm:137`），即通用船员角色。小队是 AV13 对
"在 General Quarters 期间，谁做什么" 的答案——目标文本与权限映射都围绕 GQ 人员配置
来构建。

小队类型以 4 个具体子类型存在；只有这些会被实例化（`squad_manager.dm:40` 遍历
`subtypesof(/datum/squad)`）：

| 数据 | 名称 | role | 颜色 |
|---|---|---|---|
| `/datum/squad/able` | Able | `DC_SQUAD`（Damage Control） | #e61919 红色 |
| `/datum/squad/baker` | Baker | `MEDICAL_SQUAD` | #4148c8 蓝色 |
| `/datum/squad/charlie` | Charlie | `DC_SQUAD` | #ffc32d 黄色 |
| `/datum/squad/duff` | Duff | `MUNITIONS_SUPPORT` | #c864c8 紫色 |

`SQUAD_TYPES` = 6 个 role 字符串（`__DEFINES/nsv13.dm:98-104`）：DC_SQUAD、
MEDICAL_SQUAD、SECURITY_SQUAD、COMBAT_AIR_PATROL、MUNITIONS_SUPPORT、CIC_OPS。其中
三个 role（Security、CAP、CIC）**没有默认小队**，仅作为重新分配目标存在（通过管理
计算机或低人口自动改派）。小队 → role 是一对多：Able 与 Charlie 都默认为 DC_SQUAD。

## 核心玩法循环

1. 回合开始：构建全部 4 个小队；每个都被 `retask` 到其默认 role
   （`squad_manager.dm:44`），赋予其一个主目标与一份权限列表。
2. 玩家生成；生成 5 秒后，每个职业的 `after_spawn` 运行 `register_squad`
   （`squad_jobs.dm:1-16`）。若该职业被允许，玩家会被加入其 `preferred_squad` 偏好
   （默认 `"Able"`），若不行则加入一个可加入的小队。
3. `add_member` 按军衔等级顺序挑选/更新小队长，给新成员一个寻呼机 + 挂绳（仅在职业
   驱动、`give_items=TRUE` 的加入时），并为其启用小队 HUD/领队定位器
   （`squad_datum.dm:83-104`）。
4. 回合进行约 5 分钟时，`check_squad_assignments` 改派一个低人口小队以覆盖缺失的舰桥
   / 军火 / 工程 role，并开启其权限（`squad_manager.dm:45, 83-108`）。
5. 在 GQ 期间，队友从 **小队补给机** 领取装备，跟随 **领队定位器** 找到队长，并使用
   **寻呼机**（队长广播，成员收听）进行协调。**小队管理计算机**（主管权限）可在回合
   中途改写 role、目标、成员、队长、权限与武器许可。

## 机制

### SQUAD-001
断言：恰好有 4 个小队（Able、Baker、Charlie、Duff），在 SS 初始化时创建一次并存活
整个回合；不存在按回合或动态创建小队。
面向玩家的后果：你会被分配到一个固定的、具名的、带颜色的小队；小队永不消失或增生。
Charlie 明确是 Able 溢出时的 "备用" DC 小队；Duff 是征兵/军火小队。
证据：`squad_datum.dm:159-183`（4 个子类型）；`squad_manager.dm:40-44`
（`for(var/_type in subtypesof(/datum/squad)) ... squads |= squad`）；小队与 role 映射
是 `static`（`squad_manager.dm:5-6`）。
条件：始终。
例外 / 覆写：名称必须匹配硬编码的 teamchat 组件子类型（`simple_teamchat.dm:239-261`）：
`generate_channel()` 构建 `text2path(".../squad/[name]")`（`squad_datum.dm:36-40`），
因此任何名称不是 Able/Baker/Charlie/Duff 的小队都会创建一个空的频道组件。
置信度：HIGH（高）

### SQUAD-002
断言：小队规模名义上以 `max_members = 5` 为上限，但该上限是软的且差一错误；
`add_member` 本身不强制任何限制。
面向玩家的后果：通过偏好小队加入可将小队推至 6+（检查条件是
`length(members) > max_members`，即 `> 5`），而任何转移/挂绳路径完全忽略该上限。
证据：`squad_datum.dm:17`（`max_members = 5`）；`squad_datum.dm:83-104`（add_member，
无规模检查）；`squad_jobs.dm:12`（`$> max_members`）；`squad_manager.dm:126-127`
（`get_joinable_squad` 在 `>= max_members` 时返回 `smallest_squad`）。
条件：仅职业自动加入大致遵守该上限。
例外 / 覆写：`get_joinable_squad`（随机挑选）大致感知上限；偏好小队与手动路径则不然。
置信度：HIGH（高）

### SQUAD-003
断言：一名成员可通过四条不同路径加入小队；只有职业自动加入会强制职业允许/拒绝列表。
面向玩家的后果：(a) 回合开始/中途加入时按职业自动分配；(b) 交给任何人的一条挂绳可让
其加入小队（其 `attack_self` 提示 Yes/No 并移动他们，无职业检查）；(c) 管理计算机可将
任何人类转移到任何小队；(d) `preferred_squad` 角色偏好会偏向自动分配。
证据：`squad_jobs.dm:5-16`（register_squad + disallowed 检查）；`squad_items.dm:211-226`
（挂绳加入，无职业门控）；`squad_computers.dm:100-109` / `:229-238`（转移）；
`preferences.dm:1941-1943` + `character_save.dm:76`（偏好，默认 "Able"）。
条件：register_squad 在生成 5 秒后运行；跳过非人类。
例外 / 覆写：若职业被禁止，偏好小队路径会被拒绝（`squad_jobs.dm:12`）——但挂绳路径
没有此类检查。
置信度：HIGH（高）

### SQUAD-004
断言：领导权按 *军衔等级顺序* 分配，而非按谁先加入或职业权威；第一名成员默认为队长，
后来的加入者可将其取代。
面向玩家的后果：当一个小队成形时，军衔系统认为"最高"的那位成为小队长并获得一条聊天
公告；挂绳/头盔获得一个独特的队长精灵图。
证据：`squad_datum.dm:89-95`（首入者成为队长；加入者取代队长使用
`check_rank_pecking_order`）；`squad_datum.dm:118-128`（assign_leader 重新评估成员中
军衔最高者）。
军衔来源：`compose_rank` → `get_assignment` → 职业的 `display_rank`
（`ranks.dm:105-127`，`_job.dm:388-389`），从 `config/ranks/royal_navy.txt` 加载
（例如 Midshipman=MID，Bridge Staff=SLT，Captain=CPT）。
条件：军衔仅在 `CONFIG show_ranks` 开启时解析；军衔关闭时，`compose_rank` 返回空，
`check_rank_pecking_order` 返回 FALSE，第一名成员索性保持为队长。
例外 / 覆写：**解读 / 可能的反转：** `check_rank_pecking_order`（`ranks.dm:66-78`）
返回 `myClout > theirClout`，其中 clout 是 `config/ranks/pecking_order.txt` 中的
*列表索引*，而该文件是 **资深者在前** 编写的（Grand Admiral 在索引 1 …… Midshipman
在 13）。从顶部开始计数意味着一个更靠后列出（资浅）的军衔得到数值上更高的索引，因而
"outranks（高于）"一个资深军衔——例如 Midshipman（索引 13）胜过 Bridge Staff（索引
10）和 Captain（索引 6）。`assign_leader` 的注释（"Whoever ranks highest is in
charge"）与 `check_outranks` 的措辞（`ranks.dm:57-58`）都断言相反的情形。代码即真相：
如其所写，*文件军衔最低*的有效成员往往成为队长。对机制本身的置信度：HIGH（高）；对
这是否为意图所在：LOW（低）。
置信度：HIGH（高）（机制）/ LOW（低）（意图）

### SQUAD-005
断言：死亡或离开的小队长处理方式不同：显式离开会重新分配领导权，死亡则 **不会**。
面向玩家的后果：若队长断开/转移/被移除，剩余成员中（文件）军衔最高者会自动晋升
（`remove_member` → `unset_leader` → `assign_leader`）。若队长仅仅是死亡，没有任何
东西将其从 `members` 中移除，因此 `squad.leader` 仍指向尸体，领队定位器继续将小队
引导至尸体处。
证据：`squad_datum.dm:107-128`（remove_member 晋升；assign_leader）；没有死亡/生命
钩子调用 `remove_member`/`unset_leader`——grep 显示唯一的 `remove_member` 调用者是
转移（`squad_computers.dm:107,236`）与挂绳（`squad_items.dm:224`）。
条件：仅在显式的成员变动时重新分配。
例外 / 覆写：一名主管（party-card）用户无论如何都可通过管理控制台强制降职/晋升
（`squad_computers.dm:90-99`）。
置信度：HIGH（高）

### SQUAD-006
断言：一个小队有**一个 role**（显示为 "主目标"）、一个可选的自由文本 **次目标**、一份
**权限列表**，以及两个开关（权限启用、武器许可）。
面向玩家的后果：role 决定你可以领取哪些补给机装备以及计算机会授予什么权限；`retask`
还会广播 "NEW ASSIGNMENT" 并改写主目标文本。
证据：`squad_datum.dm:42-50`（`retask` 从管理器映射设置 role/primary_objective/
access）；目标文本 `squad_manager.dm:17-36`；权限映射 `squad_manager.dm:9-16`；次
目标为用户键入的文本（`squad_computers.dm:72-82`）。
条件：若 role 已等于 task，retask 为空操作。
例外 / 覆写：次目标除显示外无游戏效果。
置信度：HIGH（高）

### SQUAD-007
断言：小队 role 通过小队 datum 直接给成员授予 **门权限**，而非通过 ID 卡。
面向玩家的后果：在 `access_enabled` 下，基于 role 的小队的每个成员都能打开该 role
权限列表所覆盖的门（例如一个已启用的 DC 小队可打开工程/大气门），纯粹凭借身处小队
之中——无需更改 ID。
证据：`/datum/squad/proc/GetAccess()` 在 `access_enabled` 时返回 `access`
（`squad_datum.dm:55-56`）；`access.dm:19`——任何 `/obj` 上的 `allowed()` 也会对人类
检查 `src.check_access(H.squad)`（`check_access` 调用 `I.GetAccess()`，
`access.dm:67-68`）。
条件：默认关闭（`access_enabled = FALSE`，`squad_datum.dm:8`）。由管理计算机启用
（`squad_computers.dm:83-89`）或在低人口改派时自动启用（`squad_manager.dm:60,79`）。
例外 / 覆写：按小队设置，而非按成员设置。
置信度：HIGH（高）

### SQUAD-008
断言：小队权限通过 role 专属列表应用，覆盖船员在 GQ 期间必须承担的 role。
面向玩家的后果：DC_SQUAD → 工程/Aux/大气；MEDICAL_SQUAD → 医疗/手术；SECURITY_SQUAD
→ 禁闭室/安保门/运输飞行员/机库；MUNITIONS_SUPPORT → 军火/仓储；COMBAT_AIR_PATROL →
战斗飞行员/军火；CIC_OPS → 主管/RC 公告。
证据：`squad_manager.dm:9-16`（`role_access_map`）。
条件：需要 `access_enabled`（SQUAD-007）。
置信度：HIGH（高）

### SQUAD-009
断言：小队 **通讯** 运行在一个自定义 teamchat 组件上，仅队长可发送；显示军衔与
"(SL)" 标签；消息长度有上限。
面向玩家的后果：寻呼机给 **队长** 一个 "Broadcast Message" 动作（其他成员没有发送
动作）；所有人的寻呼机在收到时哔哔响，并可重读上一条消息。消息硬性上限为 120 个字符。
证据：`simple_teamchat.dm:207-224`（`squad` 组件；`has_send_permission` 仅在
`override_send_permission` 或 `squad.leader == equipper` 时为 TRUE）；`:213`
（120 字符上限）；`:263-266`（消息以军衔 + 队长 "(SL)" 样式化）；寻呼机在装备时授予
该动作（`squad_items.dm:94-101`），`attack_self` 显示最后一条消息（`:88-92`）。
条件：`radio_dependent` 变体需要一个可工作的本地电信广播器
（`simple_teamchat.dm:186-205`）——干扰/通讯中断会阻断小队聊天。
例外 / 覆写：即使没有发送权限，成员仍会 `receive_message`。
`/obj/item/squad_pager/all_channels`（overwatch/舰长/HoP/典狱长/MP/自定义）具有
`global_access = TRUE` 并订阅 **所有** 小队，可向任何小队发送（`squad_items.dm:66-80`，
`custom_outfits.dm:254`，`captain.dm:55`，`warden.dm:55`，`head_of_personnel.dm:60`，
`military_police.dm:142`）。
置信度：HIGH（高）

### SQUAD-010
断言：**Squad Lead Locator（小队长定位器）** HUD 元素是队友的核心定位机制。
面向玩家的后果：成员在 HUD 上看到一个指向同一 Z 层上队长的方向箭头；当队长在另一层
甲板时显示上/下箭头；当自己就是队长时显示 "you are the leader" 图标。检查该元素会
打印队长的真实姓名（或 "UNASSIGNED"）。
证据：`squad_lead_finder.dm:12-59`（按 `get_dir` 决定叠加方向，虚拟 z 比较
`arrow_above`/`arrow_below`，当 `squad.leader == user` 时 `youaretheleader`，`:21-26`
检查）。
条件：仅对成员激活（`handle_hud` 显示/隐藏它，`squad_datum.dm:65-81`）。
例外 / 覆写：若队长已死（SQUAD-005），箭头会追踪尸体。
置信度：HIGH（高）

### SQUAD-011
断言：小队 HUD 数据通道（`SQUAD_HUD`）作为一个视觉元素实际上是 **惰性的**——从未
填充任何小队专属的叠加图标。
面向玩家的后果：队友 **不会** 通过小队系统获得彼此的颜色轮廓 / 状态读数。位置感知
仅来自领队定位器箭头（SQUAD-010）与聊天。没有一目了然的队友生命值显示。
证据：`squad_hud.dm:1-2` 定义了 `/datum/atom_hud/data/human/squad_hud`，其
`hud_icons = list(SQUAD_HUD)`；人类在 `hud_possible` 中包含 `SQUAD_HUD`
（`human_defines.dm:2`）；`prepare_huds` 为其创建一个 **空白** 图像（`state ""`）
（`mob.dm:118-127`）；grep 显示 `SQUAD_HUD`/`DATA_HUD_SQUAD` 未在其他任何地方被赋值，
因此该图标状态从未被设置。`handle_hud` 仅添加/移除 HUD 视图（`squad_datum.dm:65-81`）。
条件：始终。
例外 / 覆写：生命值/状态 HUD 是单独的 `HEALTH_HUD`/`STATUS_HUD` 系统，并非小队专属。
置信度：MEDIUM（中）（基于缺少任何更新器的否定性断言）

### SQUAD-012
断言：小队 **头盔不是功能性的头盔摄像头**；headcam 标志是残留的。
面向玩家的后果："Squad Helmet Cam Monitor" 电视屏存在于地图上，但没有代码路径在小队
头盔上创建摄像头，因此它监视着一个无人广播的网络。`has_headcam` 被声明并设置，但从未
被读取。
证据：`squad_helmets.dm:1-3`（`/obj/machinery/computer/security/telescreen/squadcam`，
网络 `"squad_headcam"`）；`squad_items.dm:160`（`has_headcam = TRUE`），`:302`
（仅有颜色的头盔设为 FALSE）；grep 未找到 `has_headcam` 的读取者，也未找到这些物品
创建的任何摄像头。
条件：始终。
例外 / 覆写：该监视器与 `"squad_headcam"` 网络出现在登舰运输机/炮艇与舰船地图中
（`_maps/templates/boarding/*`，`_maps/map_files/*`）。
置信度：MEDIUM（中）（否定性断言）

### SQUAD-013
断言：**小队管理计算机** 是命令侧的控制界面；它也作为 ntOS 程序存在，两者都受主管
权限门控。
面向玩家的后果：主管可以按小队：发送一条全小队消息、设置 role（"主目标"）、键入
次目标、切换提权权限、设置/降职队长、在小队之间转移成员、切换隐藏（阻止加入）、切换
武器许可，并打印一条备用挂绳（5 秒冷却）。地图实例位于大多数舰船上以及登舰运输机上。
证据：`squad_computers.dm:5-127`（机器；`req_one_access = ACCESS_HEADS`，`:9-10`）；
`:131-256`（ntOS 程序，`required_access = list(ACCESS_HEADS)`）；安装在指挥控制台上
（`console_presets.dm:64-69`）；地图放置 `_maps/.../*dmm`（运输机、Gladius、Galactica、
Aetherwhisp 等）。
条件：机器 `req_one_access = ACCESS_HEADS`。
例外 / 覆写：UI 不是机制；上述动作才是。
置信度：HIGH（高）

### SQUAD-014
断言：**小队补给机** 按用户的 **小队 role** 发放装备，且除非小队拥有武器许可，否则
扣发火器；套件是需要归还的借用物。
面向玩家的后果：成员只会看到其 `allowed_roles` 包含其小队 role 的装备。若
`weapons_clearance` 关闭，所有 `/obj/item/gun` 与 `/obj/item/ammo_box` 条目会从套件
中剥离。装备是 "借出" 的：在你归还物品之前（或为每件物品缴纳 300 信用点罚款），你
不能再领取新套件。
证据：`squad_vendor.dm:57-73`（`ui_data` 按 `H.squad.role` 过滤套件）；`:83-101`
（vend；无许可时使用 `requires_weapons_clearance = list(/obj/item/ammo_box,
/obj/item/gun)` `:37` 剥离武器）；`:102-137`（return_gear / pay_fine，
`total = length(must_return)*300`）；补给机权限
`req_one_access = list(ACCESS_HOP, ACCESS_HOS)`（`:33`）。
条件：用户必须身处一个小队（若无 `H.squad`，`ui_act` 直接返回）。
例外 / 覆写：补给机按 `loadout_type` 分两种：`/obj/machinery/squad_vendor`（NT 品牌）
与 `/obj/machinery/squad_vendor/solgov`。
置信度：HIGH（高）

### SQUAD-015
断言：存在两套平行的装备集（NT 弹道型 vs SolGov 能量型），各自有
Marine/Leader/Engineer/Medic 角色、Standard 与 Hazardous-Environment 变体，以及一个
仅限队长的套件。
面向玩家的后果：标准套件给予一件制服、轻型小队护甲、头盔、致命 Glock 弹匣 + 手枪
（NT）或一把复古激光枪（SolGov）。Leader 获得一个扩音器 +
`/obj/item/clothing/head/helmet/ship/squad/leader`。Engineer（role 为 DC/Munitions/
CAP）获得工具腰带、焊接眼镜、损管箱。Medic（MEDICAL_SQUAD）获得急救包 + 医疗喷雾。
"Space" 变体换入加压宇航服/太空头盔（减速，全身防护）以供 EVA/登舰使用。
证据：`squad_vendor.dm:176-266`（loadout datum、`allowed_roles`、`leader_only`）；
每个套件的 `items = list(...)`（`:179`，`:189`，`:194`，`:205`，`:216` 为 NT；
`:228-266` 为 SolGov）。
条件：role 必须在 `allowed_roles` 中；基础 `/datum/squad_loadout` 允许所有
`SQUAD_TYPES`（`:181`）。
例外 / 覆写：`leader_only` 已声明（`:180`，在队长套件上设为 TRUE），但补给机 UI 过滤
器（仅检查 `allowed_roles`）**未强制** 它——任何具有允许 role 的成员，只要其 role
匹配，都可选择队长套件。（注：队长套件保留 `allowed_roles = SQUAD_TYPES`，因此实际
上不受 role 门控。）
置信度：HIGH（高）（内容）/ MEDIUM（中）（`leader_only` 似乎未作为门控使用）

### SQUAD-016
断言：**Damage Control Kit（损管套件）** 是标准的 DC 装备，包含具有自身密封剂修复
循环的破口修补充气技术。
面向玩家的后果：该套件装有 3 个智能金属泡沫化学手榴弹、5 个充气件、1 个密封剂、
1 把撬棍。充气墙阻挡大气，但仅有 25 点完整度，被任何锋利/尖锐物品击中时会破裂/放气；
破损的充气件和受损的墙用密封剂修复（5 秒动作）。
证据：`squad_items.dm:15-21`（PopulateContents）；`:421-478`（充气结构：
`CanAtmosPass = ATMOS_PASS_DENSITY`，完整度 25，`is_sharp()`/`is_pointed` 刺破，
密封剂 do_after 修复）。
条件：标准。
置信度：HIGH（高）

### SQUAD-017
断言：小队 **状态面板** 提供了成员在 HUD/聊天之外看到的唯一叙事性小队信息。
面向玩家的后果："Squad" 状态标签显示 Assigned Squad、Squad Leader、Primary
Objective 与 Secondary Objective（或 "None"）。
证据：`squad_human.dm:5-31`（`get_stat_tab_squad`）；接入状态菜单于
`human.dm:108-113`。
条件：在 Squad 标签上拉取（慢更新模式）。
置信度：HIGH（高）

### SQUAD-018
断言：**请求控制台（Requests console）** 可直接对小队发信（紧急警报 + 自由文本小队
消息），将部门映射到小队 role。
面向玩家的后果：某个部门控制台发送工程/医疗/安保紧急情况时，也会广播到匹配的小队；
一种 "write squad message" 模式让控制台可按部门向小队频道发消息（舰桥/CIC→CIC_OPS，
军火→MUNITIONS，安保→SECURITY，医疗→MEDICAL，工程→DC，机库→CAP）。
证据：`requests_console.dm:302-327`（紧急 → `role_squad_map` 广播）；
`:381-411`（writeSquad / sendSquadMessage → `S.broadcast`）。
条件：小队 role 必须存在于 `role_squad_map` 中。
置信度：HIGH（高）

### SQUAD-019
断言：小队成员资格 **广泛可得**，并非仅限陆战队；有资格自动加入的是整个非指挥/
非安保/非工程/非医疗船员群体。
面向玩家的后果：由于 `register_squad` 定义在基础 `/datum/job` 上
（`squad_jobs.dm:1-3`），每个职业在生成后都会尝试加入小队——但 `disallowed_jobs`
排除了 AI/机械人、指挥（舰长/HoP/舰桥/MAA）、飞行员、军火技师、ATC、所有安保、所有
医疗、所有工程。因此实习船员（Marine）、货运、科研、服务、化学师/遗传学家/病毒学家
等可以自动加入；受保护的部门如有编组也是通过手动分配。
证据：`squad_jobs.dm:1-16`；`squad_datum.dm:21-27`（`disallowed_jobs` 列表）；
`get_joinable_squad` 会跳过被禁止者，除非其位于 `allowed_jobs` 中
（`squad_manager.dm:110-128`）。
条件：手动路径（挂绳/计算机）绕过职业过滤器（SQUAD-003）。
例外 / 覆写：`/datum/job/assistant/register_squad`（`midshipman.dm:120-131`）还会
拒绝携带工程/医疗/军火部门权限的实习船员加入小队（被分配到部门的助手选择退出小队）。
置信度：HIGH（高）

### SQUAD-020
断言：低人口自动改派在约 5 分钟时自动填补关键的 GQ role。
面向玩家的后果：若没有舰桥人员，一个小队被改派为 CIC_OPS；若没有军火技师，改为
MUNITIONS_SUPPORT；若没有空间站工程师 + 大气技师（合计），改为 DC_SQUAD。被选中的
小队获得 `lowpop_retasked`（以便日后不会被调走）并启用其权限，同时发布一条
"WhiteRapids Bureaucratic Corps" 公告。
证据：`squad_manager.dm:45`（5 分钟计时器）；`:53-80`（`assign_squad` 优先 DC 小队，
避免已被改派的，启用权限）；`:83-108`（`check_squad_assignments`）。
条件：只触发一次；仅在相关职业当前岗位为零时。
例外 / 覆写：若一个小队已持有该 role 且有成员，则仅启用其权限而不改派（`:55-60`）。
置信度：HIGH（高）

## 跨系统依赖

- **职业（#25）：** `register_squad` 是基础 `/datum/job` 的 proc，从 `after_spawn`
  （5 秒延迟）调用。实习船员（`/datum/job/assistant`）是 Marine；军事警察
  （`/datum/job/security_officer`，`military_police.dm`）携带全局全频道寻呼机，但本身
  位于 `disallowed_jobs` 上。
- **登舰 / 外遣行动（#17）：** 集成是 **地图层面而非代码层面** 的——`squad_manager`
  计算机与 `squad_vendor` 被放置在登舰运输机/炮艇模板上
  （`_maps/templates/boarding/{dropship,dropship_main,gunship,dropship_syndicate}.dmm`）；
  小队头盔/作战服/补给机提供陆战队 EVA 装备。`general_quarters/*.dm` 中 **没有** 任何
  小队引用（经 grep 确认）。没有代码将登舰发射与小队绑定。
- **安保（#22）：** `SECURITY_SQUAD` 作为 role/权限/补给机目标存在，但没有默认小队；
  其目标文本是 "assist security repelling boarders"。请求控制台的安保紧急情况会
  广播给它（`requests_console.dm:310`）。
- **军衔子系统：** `config/ranks/*.txt` + `pecking_order.txt` 驱动队长选择与显示的
  军衔（见 SQUAD-004）。
- **渲染/通讯：** `small_teamchat` 组件 + 电信可用性对小队聊天进行门控。

## 未解问题

- `check_rank_pecking_order` 的索引比较（SQUAD-004）是真正的反转 bug 还是有意为之？
  代码路径清晰，但与其自身注释及 `pecking_order.txt` 的排序相矛盾。需要对谁成为队长
  进行一场实盘确认。
- 装备上的 `leader_only`（SQUAD-015）从未被补给机查询——是 bug 还是死字段？
- `SQUAD_HUD` datum（SQUAD-011）真的未被使用，还是别处（例如物种/叠加系统）在运行时
  添加了一个未被 `SQUAD_HUD`/`DATA_HUD_SQUAD` grep 找到的更新器？
- `squad_helmets.dm` 的头盔摄像头监视器没有广播者——头盔摄像头是有意为之但未完成？
- 是否有任何管理员/GQ 事件会在 5 分钟低人口流程之外调用 `assign_squad`/`retask`？
  Grep 只显示回合开始路径。
- `all_channels` 寻呼机的 `LateInitialize` 设置有 `squad_channel.squad = squad`，其中
  `squad` 是（空的）proc 参数，而非循环变量 `S`（`squad_items.dm:75-80`）——无害，
  因为发送权限被覆写为 TRUE，但值得记录。
