> 本文为 research/evidence/jobs_ranks.md 的中文翻译。

# 职务、部门、军衔与权限

> 研究笔记。唯一事实来源：`D:\code\NSV13` 处的当前检出。
> NSV 新增内容位于 `nsv13/` 下；源自 `/tg/` 的基础名册位于 `code/` 下。
> 下文的“职务目录”列出的是职务 datum（类型路径），而不只是职务名称。

## 系统概述

一艘舰船的船员由注册进 `SSjob` 的 `/datum/job` datum 定义
（`nsv13/code/controllers/subsystem/job.dm`、`code/modules/jobs/job_types/_job.dm`）。
每个 datum 携带：`title`、`flag`、`department_head`、`department_flag`、
`faction`、名额数量、`supervisors`、`access`/`minimal_access`、`outfit`
（装备）、薪水、`departments`（位标志）、`display_rank`、`mind_traits`
以及 `mail_goodies`。

NSV13 保留了基础 SS13 职务名册（指挥、服务、货运、工程、医疗、科研、安保、硅基），但：

- **新增了一个军械（Munitions）部门** 以及 6 个 NSV 职务（`/datum/job/master_at_arms`、
  `munitions_tech`、`deck_tech`、`air_traffic_controller`、`pilot`、`bridge`），
  位于 `nsv13/code/modules/jobs/job_types/` 下。
- 经由 `code/__DEFINES/jobs.dm` **重命名/重新定位** 了若干基础职务：
  Head of Personnel -> **“Executive Officer”**（`JOB_NAME_HEADOFPERSONNEL`），
  Assistant -> **“Midshipman”**（`JOB_NAME_ASSISTANT`），
  Security Officer -> **“Military Police”**（`JOB_NAME_SECURITYOFFICER`）。
- 在 `nsv13/code/modules/jobs/job_types/marine/{midshipman,military_police}.dm`
  中 **覆写** 了 Assistant 与 Security Officer 的 datum（类型路径与基础相同，
  因此它们完全替换基础行为）。
- 新增一个 **军衔子系统**（`nsv13/code/controllers/subsystem/ranks.dm`），
  用从配置文本文件加载的海军/军衔头衔来装饰名字。
- 新增 **阵营（SolGov）装备替换**（`nsv13/code/modules/jobs/faction.dm`）。

军械部门（`DEPARTMENT_BITFLAG_MUNITIONS (1<<8)`）是把 NSV 职务与舰船操作系统
（7-16、17-23）联结起来的主干。

## 核心玩法循环

1. 船员生成进一个职务 datum；`_job/equip()` 创建银行账户、装备职务着装，
   而 `outfit/job/post_equip()` 依据 `J.get_access()` 构建 ID 卡并设置
   职务指派/姓名。
2. 权限（ID）开启门、储物柜和控制台；军械与舰桥职务是能够接触到战斗控制台
   （舵轮/战术/军械）的那些职务。
3. 回合中，军衔显示在聊天/检查中，并决定小队领导权。
4. `SSjob` 在 5 分钟时运行一个 `check_squad_assignments()` 定时器，重新分配
   空小队，以免任何一个舰船角色完全无人职守。
5. 军械部门收入是一个共享账户（`ACCOUNT_MUN`），由任务/得分事件
   （`SSeconomy`）支付。

## 机制

### JOB-001
断言：NSV13 在基础 SS13 名册之上定义了 6 个额外职务；军械部门是一个独立的权限区域。
面向玩家的后果：舰船拥有一条独立于安保的武器/驾驶职业路径；这些职务持有军械权限，
并使用舵轮/战术/军械/战斗机控制台。
证据：
- `nsv13/code/modules/jobs/job_types/{pilot,munitions_technician,master_at_arms,bridge,air_traffic_controller,fighter_technician}.dm`
- `code/__DEFINES/jobs.dm:50-55` — `BRIDGE_OFFICER`、`MUNITIONS_TECHNICIAN`、
  `DECK_TECHNICIAN`、`PILOT`、`AIR_TRAFFIC_CONTROLLER`、`MASTER_AT_ARMS` 标志。
- `code/modules/jobs/access.dm:203-204,224-225,367-378` — 区域 8 “Munitions”
  = `ACCESS_MUNITIONS, ACCESS_MUNITIONS_STORAGE, ACCESS_COMBAT_PILOT,
  ACCESS_TRANSPORT_PILOT, ACCESS_MAA, ACCESS_HANGAR`。
条件：新权限 ID 声明于 `code/__DEFINES/access.dm:67-75`
（`ACCESS_MUNITIONS 69`、`ACCESS_MAA 70`、`ACCESS_MUNITIONS_STORAGE 71`、
`ACCESS_COMBAT_PILOT 72`、`ACCESS_TRANSPORT_PILOT 73`、`ACCESS_HANGAR 79`），
并追加到 `get_all_accesses()`（`access.dm:160`）。
例外 / 覆写：无；保留基础名册。
置信度：HIGH（高）

### JOB-002
断言：军衔在回合/职务子系统初始化时从配置文本文件加载，并存储在
`datum/job.display_rank` 上；该文件可在运行时选择。
面向玩家的后果：你的名字会以军衔缩写作为前缀（例如 “CDR Sergei Koralev”）；
缩写集合取决于服务器选择的军衔结构（royal_navy / military / corporate / sharpe）。
证据：
- `code/controllers/subsystem/job.dm:43-44` — 若 `CONFIG_GET(flag/show_ranks)`
  则 `LoadRanks("[config]/ranks/[CONFIG_GET(string/rank_file)]")`。
- `nsv13/code/controllers/subsystem/ranks.dm:4-30` — `LoadRanks()` 读取
  `Title=Rank` 行并赋值 `J.display_rank`，对未命中项向管理员告警。
- `config/config.txt:559-561` — 自带 `SHOW_RANKS` 与
  `RANK_FILE royal_navy.txt`。
- 军衔文件：`config/ranks/{royal_navy,military,corporate,sharpe}.txt`
  （例如 royal_navy：`Captain=CPT`、`Master At Arms=LTCDR`，`Pilot` 正则匹配
  `Fighter Pilot=SGT`）。
条件：仅当设置了 `show_ranks` 配置标志时生效。`LoadRanks` 的正则为
`"[J.title]=(.+)"`，因此一行只要其键 *包含* 职务名称也会匹配
（例如职务 “Pilot” 会被 `Fighter Pilot=SGT` 行匹配）。未匹配的职务会经由
`select_substitute_rank()`（先尝试 Assistant/Midshipman）或职务 datum 中硬编码的
`display_rank` 默认值获得替代军衔（`master_at_arms.dm:27` = “WO”，
`air_traffic_controller.dm:25` = “SGT”，
`marine/midshipman.dm:7` = “MID”）。`config/ranks/pecking_order.txt` 定义了
相对的资历顺序。
例外 / 覆写：管理员动词 `Change Ranks` / `browse_rank_configs()`
（`ranks.dm:131-158`）可在回合中途加载不同的文件（调用 `LoadRanks`）。
军衔文件引用了不存在的职务（“Flight Leader”、“Marine”、
“Fighter Pilot”）——这些行是惰性的或仅通过子串匹配。
置信度：HIGH（高）

### JOB-003
断言：军衔影响玩家彼此看到的内容——语音前缀、检查文本，以及一条比较性的
“outranks” 行。
面向玩家的后果：你可以一眼/一听辨别谁资历更高；检查某人会告诉你你是否比其军衔更高。
证据：
- `code/game/say.dm:101-102` — 语音名字包含 `[compose_rank(speaker)]`。
- `code/modules/mob/living/carbon/human/examine.dm:21-25` — 当设置了 `show_ranks`
  时名字以军衔为前缀。
- `nsv13/code/controllers/subsystem/ranks.dm:32-61` — `check_outranks()` 返回
  诸如 “You outrank them as a [rank]” / “They outrank you as a [rank]” 的消息。
- 小队/物品聊天也会添加军衔前缀：`nsv13/code/modules/antagonists/simple_teamchat.dm:120,265`；
  小队计算机 `squad_computers.dm:24,39,152,167`。
条件：`CONFIG_GET(flag/show_ranks)` 必须开启；两个军衔都必须出现在
`GLOB.pecking_order` 中，否则不显示比较。
例外 / 覆写：小队可以将个人的军衔覆写为高于其职务的军衔（小队 `squad_rank`，
`compose_rank` 的 virtualspeaker 路径）。
置信度：HIGH（高）

### JOB-004
断言：军衔（资历顺序）决定小队领导权，也被用于避免意外降级。
面向玩家的后果：陆战队小队中军衔最高的成员自动成为其队长；当新成员加入时，
只有当其军衔高于现任队长时才会接管。
证据：
- `nsv13/code/modules/squads/squad_datum.dm:83-95`（add_member：首位成员为
  队长；资历更高的加入者会成为队长）。
- `squad_datum.dm:118-128`（`assign_leader` 选取 `compose_rank()` 最高者）。
- `nsv13/code/controllers/subsystem/ranks.dm:66-78` — `check_rank_pecking_order`。
- `nsv13/code/modules/squads/squad_datum.dm:90-92` 使用 `H.compose_rank()`。
条件：仅当军衔能在 `pecking_order` 中解析时生效；否则返回
FALSE 并保留现任队长。
例外 / 覆写：位于 `squad.disallowed_jobs` 中的职务（部门主管、安保、飞行员、
军械技师、ATC、舰桥等——`squad_datum.dm:21-26`）不能加入小队。
当 Midshipman 被分配了 ENGINE/MEDICAL/MUNITIONS 部门时，Assistant/Midshipman
职务会明确阻止小队注册（`marine/midshipman.dm:120-131`）。
置信度：HIGH（高）

### JOB-005
断言：ID 权限依据 `jobs_have_minimal_access` 配置标志，从 `access`（完整）或
`minimal_access`（精简）计算得出；ID 在 `post_equip` 中构建并被洗牌。
面向玩家的后果：一个职务能否进入可选房间取决于服务器配置；权限同时把关控制台、
武器储物柜和气闸。
证据：
- `code/modules/jobs/job_types/_job.dm:247-259` — `get_access()` 在
  `jobs_have_minimal_access` 时返回 `minimal_access`，否则返回 `access`；当
  `everyone_has_maint_access` 时追加 `ACCESS_MAINT_TUNNELS`。
- `_job.dm:351-365` — `post_equip` 设置 `C.access = J.get_access()`、
  `shuffle_inplace`、注册姓名/职务指派/HUD。
- `code/modules/jobs/access.dm:4-30`（`/obj/allowed`）——检查手持物品、穿戴的
  ID，或 **小队**（`H.squad`）权限。
条件：`gen_access()`/`check_access_list()` 比较 `req_access`（全部必需）与
`req_one_access`（任一必需）。
例外 / 覆写：覆写 `get_access()` 的职务会添加额外权限，例如 MAA
添加 `check_config_for_sec_maint()`（`master_at_arms.dm:35-38`），Security Officer
同样如此（`marine/military_police.dm:37-40`）。
置信度：HIGH（高）

### JOB-006
断言：特定控制台与装备受 ID 把关，限于军械/舰桥权限。
面向玩家的后果：只有军械/舰桥/匹配角色能够操作火控、战斗机控制以及 EWAR/IFF
控制台；飞行员需要 `ACCESS_COMBAT_PILOT` 才能进入战斗机。
证据（权限 → 目标）：
- `ACCESS_MUNITIONS` → 军械控制计算机
  （`nsv13/code/game/machinery/computer/munitions.dm:9`）、舰船武器自动化
  （`.../revision2/automation.dm:264`）、高斯弹药装填器
  （`.../ammunition/gauss_ammo.dm:34`）、ATC 储物柜（`custom_closets.dm:62`）。
- `ACCESS_MAA` → 战斗机控制台
  （`fighters/control_console.dm:5`）、MAA 储物柜/衣柜（`custom_closets.dm:3`）。
- `ACCESS_COMBAT_PILOT` → 进入战斗机（`fighters/_fighters.dm:37`）、
  战斗飞行员储物柜（`custom_closets.dm:78`）。
- `ACCESS_TRANSPORT_PILOT` → 运输机（`general_quarters/dropship_types.dm:151`
  及 `:178`）、运输飞行员储物柜（`custom_closets.dm:91-104`）。
- `ACCESS_MUNITIONS_STORAGE` → 军械储物柜（`custom_closets.dm:30,46`）。
- 星图舰船对象声明 `req_one_access = list(ACCESS_HEADS,
  ACCESS_MUNITIONS, ACCESS_SEC_DOORS, ACCESS_ENGINE)`
  （`nsv13/code/modules/overmap/overmap.dm:19`）——即“拥有”舰船/控制台层权限的
  船员集合。
- 打捞/EWAR 遥测扰乱器（`computer/salvage.dm`）与 IFF 控制台
  （`machinery/iff_console.dm`）声明 **没有** `req_access`——它们由物理位置
  （CIC）把关，且可被 emag，而非由 ID 把关。
条件：经由 `/obj/allowed()` 检查，该函数也接受小队权限。
例外 / 覆写：`fighter_controller.emag_act` 绕过 MAA 检查
（`control_console.dm:15-19`）。
置信度：对所枚举的门控为 HIGH（高）；本轮未定位到强制执行星图对象自身
`req_one_access` 的确切调用点（见未解问题）。

### JOB-007
断言：Midshipman（基础 Assistant）是“陆战队”通才；生成时会被分配一个部门，
该部门授予相应的无线电 + 门/控制台权限以及一条臂章。
面向玩家的后果：Midshipman 可被推入工程/医疗/科研/补给/军械，从而在该回合获得
对应部门的权限。
证据：`nsv13/code/modules/jobs/job_types/marine/midshipman.dm:72-118`
（对 `preferred_security_department` 的 switch；设置耳机、臂章配件，以及
`W.access |= dep_access`）。军械分支给予
`list(ACCESS_MUNITIONS, ACCESS_MUNITIONS_STORAGE)` 与军械耳机。
条件：`GLOB.available_depts`（`security_officer.dm:42`）列出
ENGINEERING/MEDICAL/SCIENCE/SUPPLY/MUNITIONS；“None” 使 Midshipman
不被分配。Midshipman 是溢出角色（`job.dm:15`）。
例外 / 覆写：若未被分配部门，`register_squad` 允许加入小队；若其持有
ENG/MED/MUN 权限，则小队注册被阻止。
置信度：HIGH（高）

### JOB-008
断言：Military Police（基础 Security Officer）是舰船的执法职务；MP 会被分配一个
部门执勤岗位并获得部门权限。
面向玩家的后果：MP 可被分配巡逻某个部门（例如军械，给予
`ACCESS_MUNITIONS, ACCESS_MUNITIONS_STORAGE`），并可能被传送至其检查站。
证据：`nsv13/code/modules/jobs/job_types/marine/military_police.dm:44-123`
（相同的 `preferred_security_department` switch、`GLOB.available_depts`、
`W.access |= dep_access`、可选传送至
`/area/security/checkpoint/<dept>`）。着装使用
`/obj/item/card/id/job/security_officer` 以及 MP 制服/护甲
（`military_police.dm:125-199`）。重命名经由 `JOB_NAME_SECURITYOFFICER`
（`code/__DEFINES/jobs.dm:180`）。
条件：`CONFIG_GET(flag/sec_start_brig)` 控制他们是留在禁闭室，否则他们移动到
部门检查站。
例外 / 覆写：该文件 **覆写** 了基础 `/datum/job/security_officer`。
置信度：HIGH（高）

### JOB-009
断言：在 SolGov 阵营的舰船上，每个职务的着装都会经由 `New()` 钩子替换为 SolGov
变体，而非通过单独的职务类型。
面向玩家的后果：在 SolGov 回合中，相同的职务会以不同的制服/配件出现。
证据：`nsv13/code/modules/jobs/faction.dm:1-6` — `/datum/job/New()` 检查
`SSmapping.config.ship_type` 是否为 `/obj/structure/overmap/nanotrasen/solgov`
且若是则设置 `outfit = text2path("[outfit]/solgov")`。SolGov 着装子类型定义于
`faction.dm:12-204`（例如
`/datum/outfit/job/pilot/solgov`、`/datum/outfit/job/captain/solgov`）。
条件：仅在 SolGov 地图配置下；缺失的 `.../solgov` 子类型回退到默认值
（text2path 返回 null → 着装不变）。
例外 / 覆写：这是基于地图类型的外观替换。
置信度：HIGH（高）

### JOB-010
断言：`MEDAL_*` define 是成就标识符，**不是** 军衔或权限授予。
面向玩家的后果：完成舰船功绩会解锁持久的“勋章”/成就，发放信用点；
它们不改变军衔、权威或权限。
证据：
- `nsv13/code/__DEFINES/medal.dm:1-13` — 字符串 ID（例如
  `MEDAL_PIRATE_EXTERMINATOR "Pirate Exterminator"`、`TORPCOUNT_SCORE`）。
- `nsv13/code/datums/achievements/nsv_achievements.dm:1-69` — `/datum/award/achievement/misc/*`
  与 `/datum/award/score/torpcount` 将这些用作 `database_id`，并带有 `reward`
  信用点值。
条件：需要成就/DB 系统。
例外 / 覆写：Master At Arms / HoS 储物柜包含
`/obj/item/storage/box/radiokey/*` 与 `/obj/item/storage/lockbox/medal/sec`
（Secmedals）——这些实体勋章物品与上述 define 无关。
置信度：HIGH（高）

### JOB-011
断言：`SSjob` 在回合开始数分钟后重新分配空小队，以便在某个角色无人担任时
关键岗位仍有覆盖。
面向玩家的后果：如果没有人选择 Bridge Staff / Munitions Technician
/ Engineering，剩余陆战队小队会被自动分配至 CIC、Munitions
Support 或 Damage Control，并获得相应权限。
证据：`nsv13/code/modules/squads/squad_manager.dm:82-108`
（`check_squad_assignments` 检查 `SSjob.GetJob("Bridge Staff")`、
`"Munitions Technician"`、Engineer+Atmos 当前岗位之和，然后
`assign_squad(...)`）。`assign_squad` 启用 `access_enabled`，使被分配小队的
权限列表（`role_access_map`，第 9-16 行）生效。
条件：在 `squad_manager/New()` 后 5 分钟运行。
例外 / 覆写：`role_access_map` 在小队被指派期间授予临时作战权限
（例如 `DEPARTMENT` 工程权限、`ACCESS_MUNITIONS`、`ACCESS_HEADS`）。
置信度：HIGH（高）

### JOB-012
断言：NSV 职务有自定义的“邮件礼物”（随机的回合开始邮件赠礼）；Cook 覆写会
教授一个制作配方。
面向玩家的后果：次要的风味/奖励；Cook 的配方（“Hungry
Gunpowder Bag”：火药袋 + 普通汉堡 + 镭）是一个军械风味的玩笑制作。
证据：
- `nsv13/code/modules/jobs/custom_job_mail.dm:1-56` — ATC、
  Bridge、MAA、Munitions Tech、Pilot 的 `mail_goodies`。
- `nsv13/code/modules/jobs/job_types/cook.dm:1-3` — 意在覆写
  `/datum/job/cook/after_spawn`，调用
  `teach_crafting_recipe(/datum/crafting_recipe/hungrypowder)`。
- `nsv13/code/datums/components/crafting/recipes.dm:1-9` — 该配方 datum。
条件 / 例外：Cook 那行写作
`/datum/job/cook/datum/job/after_spawn(...)`，一个畸形类型路径，几乎可以肯定
是在一个虚假的嵌套类型上定义了 proc，而非覆写
`/datum/job/cook/after_spawn`——视为很可能已死的代码（其实际触发与否为
LOW 置信度）。
置信度：MEDIUM（中）（邮件：HIGH（高）；Cook 覆写：LOW（低）——很可能已损坏）

## 职务目录

| 职务（名称） | 类型路径 | 部门（位标志） | 主要职责 | 关键权限 | 操作的系统 |
|---|---|---|---|---|---|
| Master At Arms | `/datum/job/master_at_arms` | COMMAND + MUNITIONS | 军械部门主管；掌管武器/飞行员；宣布 “Munitions” | MAA, MUNITIONS(+storage), COMBAT/TRANSPORT_PILOT, HANGAR, HEADS, RC_ANNOUNCE | 9 Ship Combat, 10 Munitions, 16 Fighters |
| Munitions Technician | `/datum/job/munitions_tech` | MUNITIONS | 建造/装填/发射舰船武器，制造弹药 | MUNITIONS(+storage), HANGAR, CONSTRUCTION, MINERAL_STOREROOM | 10 Munitions, 9 Combat, 11 PD/EWAR |
| Deck Technician (“Fighter Technician”) | `/datum/job/deck_tech` | MUNITIONS | 维护/建造战斗机（文件为 `fighter_technician.dm`） | MUNITIONS(+storage), HANGAR, CONSTRUCTION | 16 Fighters |
| Air Traffic Controller | `/datum/job/air_traffic_controller` | MUNITIONS | Dradis/发射与交通控制 | MUNITIONS, HANGAR | 8 Sensors/Radar, 16 Fighters |
| Pilot | `/datum/job/pilot` | MUNITIONS | 驾驶战斗战斗机或运输机/运输飞船（偏好选择） | MUNITIONS, COMBAT_PILOT, TRANSPORT_PILOT, HANGAR | 16 Fighters, 7 Piloting, 17 Boarding |
| Bridge Staff | `/datum/job/bridge` | MUNITIONS + COMMAND | 值守导航（舵轮）与战术控制台 | HEADS, RC_ANNOUNCE, KEYCARD_AUTH, CONSTRUCTION | 7 Piloting, 8 Sensors, 9 Combat, 4 FTL |
| Midshipman (Assistant) | `/datum/job/assistant` | SERVICE（由部门授予权限） | 陆战队通才 / 溢出；加入小队 | (maint) + 分配的部门权限 | 23 Squads, 17 Boarding, 15 Damage Control |
| Military Police (Security Officer) | `/datum/job/security_officer` | SECURITY | 舰船执法；部门检查站 | SECURITY, BRIG, SEC_DOORS, WEAPONS, KEYCARD_AUTH（+ 分配的部门） | 22 Security |
| Captain | `/datum/job/captain` | COMMAND | 舰船指挥官（CoC #1） | ALL/CAPTAIN | All |
| Executive Officer (HoP) | `/datum/job/head_of_personnel` | COMMAND/SERVICE | 重新分配职务、ID、服务；CoC #2 | HOP, CHANGE_IDS, HEADS | All |
| Head of Security | `/datum/job/head_of_security` | SECURITY | CoC #3 | HOS, SECURITY, ARMORY | 22 Security |
| Chief Engineer | `/datum/job/chief_engineer` | ENGINEERING | CoC #5 | CE, ENGINE | 13 Power, 12 Hull, 15 DC |
| Chief Medical Officer | `/datum/job/chief_medical_officer` | MEDICAL | CoC #6 | CMO, MEDICAL | 21 Medical |
| Research Director | `/datum/job/research_director` | SCIENCE | CoC #7 | RD, RESEARCH | 20 Research |
| Warden | `/datum/job/warden` | SECURITY | 禁闭室/军械库 | ARMORY, BRIG | 22 Security |
| Station Engineer / Atmospheric Tech | `/datum/job/station_engineer`, `/datum/job/atmospheric_technician` | ENGINEERING | 反应堆/电力/大气 | ENGINE, ATMOS, CONSTRUCTION | 13 Power, 14 Atmos, 15 DC |
| Medical Doctor/Paramedic/Chemist/Geneticist/Virologist | `code/modules/jobs/job_types/*` | MEDICAL | 舰船医疗舱；paramedic 额外获得 `ACCESS_MUNITIONS`（`paramedic.dm:16-25`） | MEDICAL, MORGUE, SURGERY, CLONING | 21 Medical |
| Scientist / Roboticist / Exploration Crew | `/datum/job/scientist`, `roboticist`, `exploration_crew` | SCIENCE | 科研/科技网；外派小队；探索船员生成时获得一个角色（科研/医疗/工程）（`exploration_team.dm:31-52`） | RESEARCH, TOX, EXPLORATION | 20 Research, 18 Salvage, 17 Away |
| Quartermaster / Cargo Tech / Shaft Miner | `/datum/job/quartermaster`, `cargo_technician`, `shaft_miner` | CARGO | 后勤/采矿 | CARGO, MINING, QM, VAULT | 19 Cargo, 18 Salvage |
| Bartender/Cook/Botanist/Janitor/Curator/Chaplain/Clown/Mime/Lawyer/Gimmick | `code/modules/jobs/job_types/*` | SERVICE | 平民/舰船生活品质 | KITCHEN, BAR 等 | — |
| AI / Cyborg | `/datum/job/ai`, `/datum/job/cyborg` | SILICON | 舰船 AI/硅基 | （内部） | All（只读） |

注：**不存在** 名为 “Flight Leader” 或 “Fighter Pilot” 的职务——那些只作为
`config/ranks/*.txt` 中的行出现且是惰性的。名为 “Pilot” 的职务仅通过正则子串
匹配到 “Fighter Pilot=...” 军衔行。

## 跨系统依赖

- **系统 7（驾驶/物理）：** Bridge Staff + Pilot 使用舵轮控制台
  （`computer/helm.dm`）与大型舰船 WASD 物理；`relaymove` 要求身为该舰的飞行员。
- **系统 8（导航/传感器/IFF）：** 舰桥人员值守 Dradis/星图；
  ATC 使用次级 Dradis；IFF 控制台位于 CIC，无 ID 门控，可被 emag。
- **系统 9（舰船战斗）：** 战术控制台（`computer/tactical.dm`）是炮手席；
  其自身无 ID 门控——船员由房间 + 星图权限把关。
- **系统 10（军械）：** Munitions Technician/MAA；`ACCESS_MUNITIONS` 把关
  军械控制台、武器自动化、弹药装填器、高斯。
- **系统 11（点防御/EWAR）：** 打捞/EWAR 扰乱器（开放控制台）；
  自动化上的 `ACCESS_MUNITIONS`。
- **系统 12/13/14/15（船体/电力/大气/损管）：** Station Engineer/Atmos tech，
  以及任何经由 `role_access_map[DC_SQUAD]` 被重新分配至 Damage Control 的小队。
- **系统 16（战斗机）：** Pilot（`ACCESS_COMBAT_PILOT`）、Deck Technician
  （维修）、MAA（战斗机控制台）、ATC（发射/交通）。
- **系统 17（登舰/外派）：** Midshipmen + 陆战队小队；运输飞行员
  驾驶运输飞船（`ACCESS_TRANSPORT_PILOT`）。
- **系统 18/19（打捞/货运）：** 采矿 Dradis（`req_one_access_txt "31;48"`）、
  货运投送器、受 `ACCESS_MUNITIONS` 把关的军械货包。
- **系统 22（安保）：** Military Police + HoS/Warden。
- **系统 23（小队）：** 军衔驱动小队领导权；Midshipmen 是
  主要的小队兵源。
- **系统 6/2（任务/阵营）：** 军械部门账户 `ACCOUNT_MUN`
  是奖励汇集处；SolGov 地图类型替换所有着装。

## 未解问题

1. `/obj/structure/overmap` 的 `req_one_access` 究竟在哪里被执行？
   在 `overmap.dm`、`physics.dm`、`camera.dm` 或
   `_ship.dm` 中均未发现 `allowed()` 调用；舵轮/战术控制台自身也未定义任何
   `req_access`。它可能由别处的通用移动/`CanPass`/点击路径消费，或
   实际上已是残留（真正的门控由物理舰桥门完成）。
2. 舵轮/战术控制台是否真的不受 ID 门控？已确认控制台 datum 上没有
   `req_access`；需要确认基础 `/obj/machinery/computer`
   在打开 UI 时不会添加访问检查。
3. Cook 覆写路径
   `/datum/job/cook/datum/job/after_spawn` 是畸形的——需确认它是否
   编译为一个生效的覆写，还是死代码。
4. `exploration_crew` 角色分配使用一个 `static` 计数器，每次
   非视觉生成时递增；需确认其跨晚期加入/回合重启的行为。
5. 当 `show_ranks` 关闭时，`display_rank` 默认值（例如 MAA “WO”、ATC “SGT”）
   是否曾被看到（检查/语音中的军衔文本受该标志保护）。
