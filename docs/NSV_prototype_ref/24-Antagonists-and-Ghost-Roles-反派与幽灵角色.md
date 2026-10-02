> 本文为 research/evidence/antagonists.md 的中文翻译。

# 反派与幽灵角色

范围：谁与船员为敌，以及玩家如何 *加入* 作为对立面。涵盖登舰者反派 datum、幽灵船、
血裔（bloodling）、简易 teamchat、角色偏好选择加入系统、Galactic Conquest（PVP）回合
角色/装备，以及观察者用来加入进行中的游戏所使用的幽灵角色生成器。物理登舰流程（舱体、
EWAR、运输机、KNPC 小队）位于系统 17（`boarding.md`），此处交叉引用而非重复。回合流程
属于系统 1（`round_flow.md`），安保属于系统 22（`security.md`）。

## 系统概述

NSV13 在 *星图* 回合（`SSovermap_mode`）之上叠加了一个 *空间站* 回合（SSticker 游戏
模式）。反派沿这一接缝分列：

- **空间站模式反派**：在回合开始时或通过中场偏好选择：**血裔**
  （`/datum/game_mode/bloodling`）以及（PVP 配置下的）**辛迪加船员**
  （`/datum/game_mode/pvp`）。两者都从 `antag_candidates` 中挑选，并使用标准的
  `add_antag_datum` 路径。
- **星图反派**：当星图回合生成敌对资产时被接上：**登舰者**（玩家或 KNPC，系统 17）
  与 **幽灵船**（一名玩家被赋予一艘敌方星图舰船的控制权）。
- **幽灵角色**：观察者点击一个 `mob_spawn` 低温舱（例如一艘已被登舰的活跃敌舰上的
  辛迪加船员）或回应一次民意征集（`pollGhostCandidates` / `pollCandidatesForMob`）。

选择加入通过角色设置中呈现的 `/datum/role_preference/*` 条目来表达；民意征集与游戏模式
挑选通过 `client.should_include_for_role(...)` 过滤候选者。

## 核心玩法循环

- **登舰者（辛迪加）：** 死亡玩家，中场民意征集 → 在目标舰船上的一艘登舰舱中生成 →
  劫持目标 → 与船员交战。低人口回退是 KNPC 小队成员（系统 17）。
- **登舰者（海盗）：** 相同的民意征集路径，独立的 `pirate_boarder` datum → 共享的团队
  "plunder（掠夺）" 目标。
- **幽灵船：** 一名 *玩家* 被交付一艘敌方阵营的星图舰船（来自舰队遭遇、一个 `vv`
  下拉菜单，或一个管理员动词）并驾驶/与船员交战；没有正式目标。
- **血裔：** 回合开始时的主脑（通风管道中的一只微小血肉生物），通过吸收/击杀成长，
  经过 6 个层级进化，建造仆从，并尝试一场 10 分钟的升华仪式——若船员无法摧毁焦点
  实体，该仪式会以其获胜结束回合。
- **PVP / Galactic Conquest：** 24+ 名玩家分为 NT 船员（普通职业）与一队登上实例化的
  辛迪加舰船（SSV Nebuchadnezzar）的辛迪加船员。双方在星图上争夺；NT 在 700 阵营
  tickets 时或摧毁辛迪加旗舰时获胜；辛迪加通过占领星系（灯塔信标）或核平 NT 舰船
  获胜。

## 机制

### ANTAG-001 — 登舰者反派 datum 承载登舰者的目标
断言：存在两个登舰者 datum——一个面向辛迪加空降兵的叛徒扩展
（`/datum/antagonist/traitor/boarder`），以及一个面向海盗登舰者的海盗扩展
（`/datum/antagonist/pirate/boarder`）——其对玩家可见的内容仅为一个目标，且对海盗
而言还有一个共享的团队目标。
面向玩家的后果：辛迪加登舰者被要求劫持舰船；海盗登舰者共享一个 "loot and pillage"
目标，与一个海盗停靠台（pirate-pad）货舱挂钩。
证据：`nsv13/code/modules/antagonists/boarders/boarders.dm:1-27`——`should_equip =
FALSE`（:5，无 uplink），`show_to_ghosts = TRUE`（:7）。`forge_human_objectives()`
首先添加 `/datum/objective/hijack` 并返回，因此劫持目标始终是第一个目标；殉道者分支
（`prob(20)`，`martyr_compatible`）只有在已存在劫持目标时才可达。
`nsv13/code/modules/antagonists/boarders/pirate_boarders.dm:1-53`——
`/datum/antagonist/pirate/boarder` 持有一个 `/datum/team/pirate/boarder`
（`boarding_crew`），`greet()` 打印 "outnumbered, outgunned, under prepared" 演讲，
`forge_objectives()` 添加 `/datum/objective/loot/plunder`（说明 "Loot and pillage the
ship, transport 50000 credits worth of loot."，带有一条 `//replace me` 注释），绑定到
`/area/shuttle/pirate` 中的任何 `piratepad_control`。
条件：datum 由星图登舰生成器应用，而非游戏模式——见系统 17 BOARD-004/005。
`boarder/on_gain()` 将团队目标并集到成员身上。
例外 / 覆写：劫持目标文本是默认的空间站穿梭机文本，且对于舱体生成的海盗而言海盗停靠台
可能不存在（见 `boarding.md` 中的未解问题）。
置信度：HIGH（高）（datum/目标接线已证实；这些目标在游玩中是否会达成属于解读）。

### ANTAG-002 — 幽灵船 = 一名玩家被赋予一艘敌方星图舰船
断言：`ghost_ship(mob/target)` 将一艘 AI 星图舰船转换为玩家驾驶的舰船：它解除 AI
控制，删除 AI 飞行员/炮手假人，增强舰船，添加 DRADIS/战术控制台，插入一个与被选中
观察者绑定的 "skeleton" 人类 mob，并授予 `/datum/antagonist/ghost_ship` datum。
幽灵船 **没有目标**——该 datum 仅是一个追踪/封禁标记。
面向玩家的后果：一名中途加入的玩家可以驾驶并操控一整艘敌方主力舰（或一架大幅增强的
战斗机）与船员交战。
证据：`nsv13/code/modules/overmap/overmap_ghosts.dm`：
- `ghost_ship()`（:34-96）：若 `role == MAIN_OVERMAP`（:39）或
  `istype(src, /obj/structure/overmap/small_craft)`（:43）则拒绝——"cannot be ghost
  controlled"。设置 `ai_controlled = FALSE`，qdel `pilot`/`gunner`，运行
  `spec_ghostship_changes()`，添加 `/obj/machinery/computer/ship/dradis/internal` +
  `tactical/internal`（按质量门控的 dradis），将所有 gauss 武器强制为
  `OSW_CONTROL_GUNNER`，生成 `/mob/living/carbon/human/species/skeleton`，设置
  `ghost.key`，`add_antag_datum(/datum/antagonist/ghost_ship)`，设置
  `overmap_deletion_traits = DAMAGE_ALWAYS_DELETES`，并授予星图动词
  （toggle_brakes/inertia、show_dradis/tactical、move_mode、cycle_firemode）。
- `ghost_key_check()`（:98-110）：一旦存在密钥，`start_piloting(ghost,
  OVERMAP_USER_ROLE_PILOT|GUNNER)`，添加 `reassume_ship_control` 动词，打开战术
  控制台，设置 `ghost_controlled = TRUE`。
- `spec_ghostship_changes()`（:112-122）：对于 `mass == MASS_TINY` 的战斗机，将完整度
  ×6、推力 ×3.5/×2、机动性 ×2、speed_limit ×2.5，并设置
  `shots_left`/`max_shots_left = 500`。
`nsv13/code/modules/antagonists/ghostship/ghost_ship.dm:1-8`：
`/datum/antagonist/ghost_ship`（`show_name_in_check_antagonists = TRUE`，
`show_in_antagpanel = FALSE`，`banning_key = ROLE_GHOSTSHIP`）——无目标，注释：
"Used for tracking and because the role_preferences needs an antag_datum to point
at."
条件：飞行员选择由调用方完成。存在三个入口点（见 ANTAG-003）。
例外 / 覆写：`override_ghost_ships` 管理员开关阻止自动生成；`mobs_in_ship += ghost`
使飞行员接收呼叫；`DAMAGE_ALWAYS_DELETES` 阻止常规的无人舰船删除倒计时。
置信度：HIGH（高）。

### ANTAG-003 — 幽灵船如何进入回合
断言：幽灵船通过三种方式创建：(1) 在足够玩家活跃时，AI 舰队 **遭遇** 时以 20% 概率
自动生成；(2) 通过任何星图舰船上的管理员 `vv` "Make Ghost Ship" 下拉菜单；(3) 通过
星图游戏模式控制器的管理员动词 "spawn ghost ship"。它们都通过 Ghost Ship 角色偏好
向幽灵征集。
面向玩家的后果：船员遭遇的一艘敌舰可能突然由一名真实的对手驾驶；管理员也可按需注入
一艘。
证据：`nsv13/code/modules/overmap/ai-skynet.dm` 的 `fleet/encounter()`（:681-732）：
若 `override_ghost_ships` 则提前返回；`if(!prob(20)) return`；玩家门限——>15 名活跃
玩家 ⇒ 战斗机 + 驱逐舰 + 战列舰，>10 ⇒ 仅战斗机，否则中止（"insufficent players"）。
民意征集：`pollGhostCandidates("Do you wish to pilot a [initial(selected_ship.faction)]
[initial(selected_ship.name)]?", ROLE_GHOSTSHIP,
/datum/role_preference/midround_ghost/ghost_ship, 20 SECONDS, POLL_IGNORE_GHOSTSHIP)`
（:724），随后发布该舰的新闻、设置 `current_system`，并调用 `ghost_ship()`。管理员
路径：`overmap_ghosts.dm:4-30`（`vv_get_dropdown`/`vv_do_topic`，"Open"/"Choose"
飞行员）与 `nsv13/code/controllers/subsystem/overmap_mode.dm:700-745`（管理员控制器
动词，舰船列表来自
`typesof(/obj/structure/overmap/{nanotrasen,spacepirate,syndicate,solgov}/ai)`）。
条件：`POLL_IGNORE_GHOSTSHIP`（`code/_globalvars/lists/poll_ignore.dm:17,32`）让幽灵
可在一个回合内永久拒绝。Ghost Ship 偏好键为 `ROLE_GHOSTSHIP`。
例外 / 覆写：`toggle_ghost_ships` 管理员动词翻转
`SSovermap_mode.override_ghost_ships`（`overmap_mode.dm:747-750`）。
置信度：HIGH（高）。

### ANTAG-004 — 血裔回合开始：通风管道内的一名主脑，外加奴仆
断言：`/datum/game_mode/bloodling` 需要 20 名玩家，并在一条合适的未焊接通风管道内
（网络 > 20 台机器，否则回退到 `GLOB.xeno_spawn`）精确生成一只 **主脑**，形式为一只
微小的 `/mob/living/simple_animal/bloodling`；任何额外被选中的候选者会成为
`/datum/antagonist/changeling/bloodling_thrall`。
面向玩家的后果：回合开始时，船员中有一只几乎隐形的血肉生物藏在通风管道里，以及
0–1 名预先制造好的变色龙仆从。
证据：`nsv13/code/game/gamemodes/bloodling.dm`：
- `required_players = 20`，`required_enemies = 2`，`restricted_jobs =
  list("AI","Cyborg")` + 配置保护时的受保护职业（:8-13）。
- `pre_setup()`（:26-56）：`bloodling_amount = 2`（:23），调用 `spawn_bloodling()`，
  然后通过 `antag_pick(..., ROLE_BLOODLING)` 挑选 `num_bloodlings` 名候选者。
- `spawn_bloodling()`（:82-105）：在空间站 Z 层扫描 `unary/vent_pump`，未焊接，父网络
  `other_atmos_machines.len > 20`；回退到 `GLOB.xeno_spawn`；在 `vent.loc` 返回一只
  新血裔。
- `post_setup()`（:58-72）：第一只血裔成为主脑（`master.key = theMaster.key`，
  `master.mind.add_antag_datum(/datum/antagonist/bloodling)`，qdel 其旧躯体）；其余
  获得 `add_antag_datum(/datum/antagonist/changeling/bloodling_thrall)`。
- `make_antag_chance()`（:108-126）：一名持有血裔偏好（且不在受限职业中）的中途加入者
  若无主脑则成为主脑，否则成为奴仆。
条件：`restricted_jobs` 可能因配置标志而增长（`protect_roles_from_antagonist` 等）。
例外 / 覆写：`reroll_friendly = 1`。若既无通风管道也无异形生成点，设置会失败并提示
"Map error! No suitable vent networks / Xeno spawn waypoints found!"。
置信度：HIGH（高）。

### ANTAG-005 — 血裔生物质、进化与能力
断言：血裔的力量是一个存储 `biomass` 的 `/datum/component/bloodling`。生命值 ==
生物质，最大生命值 == `final_form_biomass`（1500）；受到伤害会移除生物质；更高的
生物质意味着 **更慢** 的移动，但对应更大的进化层级（1–6），后者解锁能力并放大近战/
物体伤害与环境破坏。
面向玩家的后果：血裔在小时虚弱而快速，在大时缓慢而耐打；它必须主动收集生物质
（吸收 mob、召回残骸）以持续成长与存活。
证据：`nsv13/code/modules/antagonists/bloodling.dm`：
- `/mob/living/simple_animal/bloodling/Life` 设置 `health = biomass.biomass`、
  `maxHealth = biomass.final_form_biomass`，在 ≤0 时死亡（:74-79）；基础
  `health/maxHealth = 200`（:17-18）。
- `damage_react`（:150-157）执行 `remove_biomass(amount)`，生成 `bloodling` 黏液
  贴花，并抖动。
- `update_mob()`（:194-225）：`evolution_step = CLAMP(round(biomass/50),1,6)`，应用
  一个乘法移动速度修正（`MOVESPEED_ID_BLOODLING_BIOMASS`）——越大越慢——调用
  `SSticker.mode.check_win()`，更新图标/生命值，并按 `unlock_tier`/`lockAtTier`
  授予/移除能力动作。`max_evolution = 6`，`final_form_biomass = 1500`（:109-113）。
- `on_evolve(step)`（:97-106）：`melee_damage = 2.5*step`，`obj_damage = 5*step`，
  层级 4 时 `ENVIRONMENT_SMASH_WALLS`，层级 8 时 `_RWALLS`（在最大 6 时不可达）。
- 能力表 `unlock_tiers`（:119-133）与每能力消耗（biomass_cost）：hide（层级 0，层级 4
  时锁定）、thermalvision、absorb（0）、call_remnant（5）、infest（75，层级 2）、
  build（层级 2）、transfer_biomass（层级 3）、ground_pound（层级 3，0）、
  dissonant_shriek（层级 3，30）、give_life（层级 4，75）、whiplash（层级 4，25）、
  heal（层级 4，50）、ascend（层级 6，500）。
- `absorb/action`（:623-663）：对 view(1) 内一个目标施加 10 秒光束，造成
  `take_overall_damage(0,0,50)`，将其碾碎，掉落一个价值 `mob_size*50` 生物质的
  `/obj/effect/temp_visual/bloodling_remnant`。
- `call_remnant`（:567-598）：牵引附近的残骸，范围随 `last_evolution` 缩放。
- `build`（:1043-1081）：径向菜单用于 ratwarren（40）/ harvester（20）/ tank（30）
  ——无意识的 `bloodling_minion` 壳，幽灵稍后可入住（give_life）。
条件：`hide` 强制 `MOB_SIZE_TINY`、`PASSDOOR|PASSTABLE|PASSMOB`、`ventcrawler =
TRUE`，因此血裔可爬行通风管道（与通风管道生成相匹配）。
例外 / 覆写：已升华的最终形态（"theMaster"）保持无限生物质（见 ANTAG-006）。
置信度：HIGH（高）。

### ANTAG-006 — 侵染、奴仆、仆从，以及升华胜利条件
断言：`infest`（以及 `give_life`）将其他生物转化为仆役：人类成为
`/datum/antagonist/changeling/bloodling_thrall`（阉割版变色龙）；无意识 mob 成为
`/datum/antagonist/bloodling/minion`；`give_life` 向一名幽灵征集（`ROLE_SENTIENCE`）
来扮演一只新获得感知的生物。主脑的终极目标是在一个指定的 "consciousness grid"
区域内 **升华**，代价为 500 生物质，触发一个 10 分钟的末日计时器；若船员无法击杀
由此产生的 3000 HP 实体，则回合由血裔获胜。
面向玩家的后果：船员面对一个潜行成长的生物，它会转化他们自己人并建造血肉仆从，
最终以一场喧闹的倒计时事件和一场结束回合的 boss 战告终。
证据：`nsv13/code/modules/antagonists/bloodling.dm`：
- `infest/mob`（:230-237）：`enslave_mind_to_creator`，添加
  `changeling/bloodling_thrall`（人类）或 `bloodling/minion`（其他）；若目标是
  AI/overmind/被 mindshield 保护者，则通过星图转发一条 `bloodling_awaken` 警告。
- `changeling/bloodling_thrall`（:240-275）：`powers_override` = 一份特定的阉割版
  变色龙能力列表（无大型治疗），`geneticpoints = 5`。
- `give_life/action`（:802-834）：`pollCandidatesForMob("Do you want to play as a
  bloodling minion?", ROLE_SENTIENCE, ...)`；成功后转移密钥，授予感知，
  `copy_languages`，然后免费 `infest`；若无候选者则退还生物质。
- `bloodling` 反派 datum（:277-342）：`give_objectives = TRUE`，目标
  `/datum/objective/bloodling_ascend`（"Ascend to your final form."）；`minion`
  子类型使用 `/datum/component/bloodling/lesser`（`final_form_biomass = 200`，能力集
  精简，:673-683）与目标 `/datum/objective/bloodling_serve`（自动完成）。
- `bloodling_ascend/New()`（:369-380）：在空间站 Z 层挑选
  `SUMMON_POSSIBILITIES` 个持有 `VALID_TERRITORY` 标志的随机区域；文本将它们列为
  唯一有效的升华地点。
- `ascend/action`（:1155-1189）：需要 `bloodling` datum 且身处一个有效的
  `summon_spots` 区域；500 生物质；用墙将主脑围住（`resin/wall` 环 + 卷须）；优先级
  公告 "Patmos-Omega 'end-of-the-world' class event"；冷却 15 分钟。
- `begin_the_beginning_of_the_end`（:1191-1202）：公告 T-5 分钟，
  `set_security_level("delta")`，`SSshuttle.registerHostileEnvironment(user)` +
  `SSshuttle.lockdown = TRUE`（无逃脱），5 分钟计时器。
- `begin_the_end`（:1204-1224）：公告 "T-10 minutes"，生成
  `/mob/living/simple_animal/hostile/eldritch/armsy/prime/bloodling_ascended`
  （3000 HP，:1237-1245），`mind.transfer_to`，然后在 10 分钟后 `bloodling_win`。
- `bloodling_win`（:1227-1235）：`sound_to_playing_players(alarm.ogg)` +
  `Cinematic(CINEMATIC_CULT)`。
- `game_mode/bloodling/check_win()`（`bloodling.dm:74-77`）：当且仅当主脑存活、为
  `bloodling_ascended` 且 `B.biomass >= B.final_form_biomass` 时为真（两者在升华后
  经 `update_biomass` 设为 INFINITY，:1265-1268）。
- 幽灵也可以点击已升华的主脑以仆从身份加入（`attack_ghost`，:1249-1255，生成一只
  50/50 的 harvester/tank 并将其奴役）。
条件：升华需要层级 6（≥300 生物质）以及 500 生物质的消耗，因此主脑必须先处于或接近
最大生物质。`SSshuttle.lockdown` 与敌对环境会阻止逃生穿梭机。
例外 / 覆写：`infest/proc/ask_special_absorb`（:696-755）让层级 4+ 的血裔 "dominate"
一个 AI overmind 或一个 blob overmind（3× 施法时间，喧闹）——一次强力的可选取胜手段。
置信度：HIGH（高）（时机/条件已证实；平衡性属于解读）。

### ANTAG-007 — 简易 teamchat（血裔蜂巢意识 + 小队无线电）
断言：`/datum/component/simple_teamchat` 通过一个 "Broadcast Message" 动作提供分立
的文本频道；血裔获得 `bloodling` 频道（键 "Alien Hivemind"），陆战队获得
Able/Baker/Charlie/Duff 小队无线电。`radio_dependent` 变体需要同一 Z 层上一台可运行
的电信机器；`squad` 变体仅允许小队长发送，除非被覆写。
面向玩家的后果：血裔奴仆共享一个私密的蜂巢意识聊天；小队传呼仅在通讯在线时才发出，
且（通常）由队长发出。
证据：`nsv13/code/modules/antagonists/simple_teamchat.dm`：组件核心（:38-274）；键
（:7-11）；`bloodling` 频道（:268-274）由
`/datum/antagonist/bloodling/apply_innate_effects` 授予（bloodling.dm:331）；
`radio_dependent/can_message` 需要用户所在 Z 层有一台可运行的
`telecomms/relay|hub|server`，否则播放静电噪声（:186-205）；`squad` 子类型
`has_send_permission` 限制为 `squad.leader`（:221-224）；小队键
Able/Baker/Charlie/Duff（:239-261）。
条件：消息长度上限（默认 MAX_MESSAGE_LEN；小队寻呼机 120，:213）。
例外 / 覆写：次要系统——仅记录；此处不细述 TGUI/动词界面。
置信度：HIGH（高）。

### ANTAG-008 — 角色偏好是玩家选择加入的方式
断言：选择加入反派/幽灵角色是一个按角色的 `/datum/role_preference` 条目，其
`antag_datum` 用于显示/封禁检查。NSV13 添加了 Bloodling（反派）、Syndicate
Crewmember/Galactic Conquest（反派），以及 Ghost Ship + Boarder（中场幽灵）。
面向玩家的后果：玩家必须在角色设置中启用相关偏好（且未被职业封禁）才会被提供/选中
这些角色；通过民意征集的 "Never this round" 拒绝会将其加入民意忽略列表。
证据：`code/modules/antagonists/role_preference/_role_preference.dm:1-28`（基础
datum，`ROLE_PREFERENCE_CATEGORY_ANAGONIST` 为 `per_character = TRUE`；midround_ghost
类别）。`nsv13/code/modules/antagonists/role_preference/role_antagonists.dm:1-8`：
`/datum/role_preference/antagonist/bloodling` → `/datum/antagonist/bloodling`；
`/datum/role_preference/antagonist/pvp` → `/datum/antagonist/nukeop/syndi_crew`。
`nsv13/code/modules/antagonists/role_preference/role_midrounds.dm:1-8`：
`/datum/role_preference/midround_ghost/ghost_ship` → `/datum/antagonist/ghost_ship`；
`/datum/role_preference/midround_ghost/boarder` → `/datum/antagonist/traitor/boarder`。
游戏模式指向这些：`/datum/game_mode/bloodling.role_preference =
/datum/role_preference/antagonist/bloodling`（bloodling.dm:5）；
`/datum/game_mode/pvp.role_preference = /datum/role_preference/antagonist/pvp`
（pvp.dm:19）。候选者过滤：`code/__HELPERS/game.dm:438-469`（`pollCandidates` →
`client.should_include_for_role(banning_key, role_preference_key, poll_ignore_key)`）。
条件：`POLL_IGNORE_GHOSTSHIP` / 幽灵民意忽略类别让玩家可在一个回合内拒绝
（`poll_ignore.dm`）。
例外 / 覆写：`boarder` 民意征集传递的封禁键为 `ROLE_OPERATIVE`（系统 17 BOARD-003）。
置信度：HIGH（高）。

### ANTAG-009 — PVP 角色分配（NT vs 辛迪加）
断言：`/datum/game_mode/pvp`（"Galactic Conquest"）需要 24 名玩家；它从一个 JSON
地图实例化一艘随人口缩放的辛迪加舰船，挑选
`max(1, round(num_players/2.5))` 名辛迪加候选者，并且 `assign_jobs()` 给每人分配其
`preferred_syndie_role`（若空闲），否则按 role 优先级降序自动填充进关键角色，最后将
剩余者丢进无限的 "Autofill"（陆战队/步兵）role。其他人都是普通的 NT 船员。
面向玩家的后果：在标准回合中，辛迪加一方大约占总人口的五分之一，其余人配置 NT 舰船；
选择了特定辛迪加职业的玩家通常能得到它，其余人被安置在舰船需要人的地方。
证据：`nsv13/code/game/gamemodes/pvp/pvp.dm`：
- `required_players = 24`，`role_preference = /datum/role_preference/antagonist/pvp`，
  `overflow_role = CONQUEST_ROLE_GRUNT`（:16-20，:40），`false_report_weight = 10`。
- `pre_setup()`（:87-123）：按人口区间挑选一张地图（hammurabiPVP ≤30 /
  astraeusPVP 31-39 / babylonPVP 40+），`instance_ship_from_json`，在辛迪加舰船
  （`force_loss`）与主星图（`force_win`）上注册 `COMSIG_PARENT_QDELETING` 处理器，
  设置 `SSovermap_mode.mode = /datum/overmap_gamemode/galactic_conquest`，并挑选
  `enemies_to_spawn = max(1, round(num_players()/2.5))` 名候选者进 `pre_nukeops`。
- `post_setup()`（:131-140）：`assign_jobs()`，设置 `SSstar_system.time_limit`
  （2 小时 30 分），交换舰队生成速率使得 NT（而非辛迪加）派出舰队并将辛迪加的速率推
  至 2 小时，并注册一个敌对环境（禁止撤离）。
- `assign_jobs()`（:56-84）：两轮——理想 role 分配，然后按优先级顺序沿关键 role
  下降，否则 `overflow.assign()`。
`nsv13/code/game/gamemodes/pvp/roles.dm`：`/datum/conquest_role_handler` 从
`subtypesof(/datum/syndicate_crew_role)` 构建 `roles`（:79-82）；
`/datum/syndicate_crew_role/assign` 调用 `candidate.add_antag_datum(antag_datum_type)`
（:94-99）。
条件：若无 `antag_candidates`，设置失败（"Not enough syndicate crew candidates"，
pvp.dm:120-123）。
例外 / 覆写：`/client/proc/select_syndie_role()` 打开 `SyndieJobSelect` TGUI 以设置
`prefs.preferred_syndie_role`（roles.dm:3-51）。
置信度：HIGH（高）。

### ANTAG-010 — 辛迪加船员角色与装备
断言：辛迪加一方是按优先级顺序排列的一组 `/datum/syndicate_crew_role`——舰长、
战略家、军需官、舰桥人员（×2）、技师（×4）、CAG、飞行员（×4）、陆战队中士、小丑、
帮厨，以及 Autofill/Grunt（∞）——各自映射到一个拥有自身套装与 ID 权限的
`/datum/antagonist/nukeop/syndi_crew*` 子类型。该反派 datum 设置
`give_objectives = FALSE`（他们的目标是赢得游戏），并配备一套带有辛迪加锁定无线电与
武器认证植入体的船员套装。舰长额外获得一个舰船装备选择器并创建核弹小队；军需官获得
一个辛迪加货运控制台与预算 ID，且舰船拥有一枚可召唤的核弹（`pvp_nuke_spawner`）。
面向玩家的后果：每个辛迪加职业都是一个独特的战斗/支援装备，拥有完成它的权限；舰长
选择战列巡洋舰的模块装备，核弹可由一名获授权的船员召唤到 NT 舰船上。
证据：`nsv13/code/game/gamemodes/pvp/roles.dm`：
- `/datum/antagonist/nukeop/syndi_crew`（:53-64）：`nukeop_outfit =
  /datum/outfit/syndicate/no_crystals/syndi_crew`，`banning_key = ROLE_SYNDI_CREW`，
  `give_objectives = FALSE`；`greet()` 公告摧毁 NT 舰船。
- 角色定义带 `preference_flag` = `CONQUEST_ROLE_*` 与 `max_count`：captain
  （:103-118，还设置 `theGame.nuke_team`），strategist（:223-255，`essential =
  FALSE`），requisitions（:257-281），bridge（:283-300，×2），technician（:302-320，
  ×4），cag（:322-338），pilot（:340-356，×4），sergeant（:358-374），clown
  （:376-394，`essential = FALSE`），line_cook（:396-412，`essential = FALSE`），
  marine/Autofill（:414-439，`max_count = INFINITY`）。
- 舰长套装（:191-206）：`id = /obj/item/card/id/syndi_crew/captain`，`r_hand =
  /obj/item/ship_loadout_selector`，战斗手套，syndcapt 披风，`command_radio = TRUE`，
  `uplink_type = null`。队长 greeted（:215-220）告知他们挑选一个舰船装备并使用
  "lighthouse beacon"。
- 基础套装 post_equip（:174-184）：`H.ears` 无线电设为 `FREQ_SYNDICATE` +
  `freqlock`，`H.faction += "Syndicate"`，`implants =
  list(/obj/item/implant/weapons_auth)`。
- ID 子类型（:121-170）携带 `ACCESS_SYNDICATE` + role 权限。
`nsv13/code/game/gamemodes/pvp/items.dm`：`/obj/item/pvp_nuke_spawner`（:39-62，将
`nuclearbomb/syndicate` 召唤到用户所在地格，仅在空间站层级，权限 `150`）；
`/datum/antagonist/nukeop/syndi_crew/move_to_spawnpoint`（:64-65）强制移动到
`GLOB.syndi_crew_spawns` 地标；`/obj/machinery/conquest_beacon`（灯塔，:86-231）以
100 阵营影响力占领一个星系；`/obj/item/ship_loadout_selector`（:283-336）应用
`stealth`/`interceptor`/默认装备之一。
条件：`nukeop/on_gain()` 仍会运行 `equip_op()`、`give_alias()`、
`memorize_code/frequency`（`code/modules/antagonists/nukeop/nukeop.dm:52-66`）；
`forge_objectives()` 因 `give_objectives = FALSE` 而提前返回（:115-117）。
例外 / 覆写：`syndi_crew` 套装使用 `/datum/outfit/syndicate/no_crystals`，因此无
uplink/crystals（`items.dm:3-5`）；舰长有 `uplink_type = null` 并引导买家去找军需官。
置信度：HIGH（高）。

### ANTAG-011 — PVP 胜利条件
断言：Galactic Conquest 在一方被宣告为胜利者时结束：若辛迪加旗舰
（SSV Nebuchadnezzar）被摧毁则 NT 获胜；若 NT 主星图被摧毁，或通过 tickets 目标
（NT 累积 `F.tickets + 700`），则辛迪加获胜。用灯塔信标占领星系会给予影响力。
NT 侧目标为 `/datum/overmap_objective/tickets/nt`（700 tickets）。
面向玩家的后果：回合是一场围绕该星系的拉锯战；摧毁敌方舰船会立即结束它，否则影响力/
tickets 会累积趋向一次 NT ticket 胜利。
证据：`nsv13/code/game/gamemodes/pvp/pvp.dm`：
- `force_loss()`（:147-150）——辛迪加舰船被摧毁 ⇒ `winner = NT faction`。
- `force_win()`（:153-156）——NT 主星图被摧毁 ⇒ `winner = Syndicate faction`。
- `check_win()`（:158-166）——当存在一个非 NT 的胜利者或 `nukes_left == 0` 时为真。
- `check_finished()`（:168-178）——在宣告胜利者时结束；若特工已死但一枚核弹仍在计时
  则保持回合存活；`end_on_team_death = FALSE`（:48）。
- `OnNukeExplosion`（:142-144）递减 `nukes_left`（从 1 开始，:31）。
- `set_round_result()`（:180-202）将结果映射到 `STATION_NUKED` / `PVP_SYNDIE_*`
  新闻报道。
`nsv13/code/game/gamemodes/overmap/objectives/tickets.dm`：`tickets/nt`（:25-28），
`ticket_amount = 700`，`assigned_faction = FACTION_ID_NT`，目标 = 当前 + 700。
`nsv13/code/game/gamemodes/overmap/galactic_conquest.dm:1-16`：
`/datum/overmap_gamemode/galactic_conquest`（`starting_faction = "nanotrasen"`，
`fixed_objectives = list(/datum/overmap_objective/tickets/nt)`）。
`nsv13/code/modules/overmap/overmap.dm` / `items.dm` 的征服信标添加
`points_per_capture = 100`。
条件：`SSstar_system.time_limit`（2 小时 30 分）是硬性上限；星图时间限制子系统在达到
该值时结束回合（`controllers/subsystem/starsystem.dm:18,36`）。
例外 / 覆写：若海盗阵营累积超过他人，`set_round_result` 还会处理一个
`FACTION_ID_PIRATES` 的 "partial win"。
置信度：HIGH（高）。

### ANTAG-012 — 幽灵角色生成器：供观察者使用的 engram（印痕）
断言：`nsv13/code/game/objects/effects/spawners/custom_ghost_role.dm` 定义了 NSV13
低温舱幽灵生成器：`/obj/effect/mob_spawn/human/nsv13/syndicate_crew`（以及
`/pilot`）——放置在辛迪加 `carrier` 登舰内部空间内的辛迪加士兵/飞行员防守方——以及
`/obj/effect/mob_spawn/human/nsv13/nt_prisoner`——一名被俘的 NT 囚犯（明确 **不是**
反派）。初始化时它们会 `notify_ghosts(...)`，以便观察者点击生成。
面向玩家的后果：当陆战队登上辛迪加航母时，幽灵可以解冻为辛迪加船员/飞行员来防守它；
NT 囚犯舱（若存在）提供一个非反派的 "活着逃离辛迪加舰船" 角色。
证据：`nsv13/code/game/objects/effects/spawners/custom_ghost_role.dm`：
- `syndicate_crew`（:1-11）："Syndicate Soldier"，`roundstart = FALSE`，套装
  `/datum/outfit/syndicate/sleeper/soldier`，`assignedrole = "Syndicate Soldier"`，
  无反派 datum。
- `syndicate_crew/pilot`（:13-19）："Syndicate Pilot"，飞行员套装，告知他们驾驶/
  保护舰桥。
- `Initialize`（:28-32）：`notify_ghosts("A Syndicate Crewmember is about to thaw from
  cryo on \the [A.name].", source = src, action = NOTIFY_ATTACK, flashwindow = FALSE)`。
- `nt_prisoner`（:34-57）：以囚犯为风味，`<span class='big bold'><span
  class='danger'>THIS IS NOT AN ANTAGONIST ROLE!</span>`，套装
  `/datum/outfit/sleeper/prisoner`。
- `Destroy` 将该舱换成 `empty_sleeper` 装饰。
放置：`syndicate_crew`（×2）与 `syndicate_crew/pilot`（×5）出现在
`_maps/templates/boarding/syndicate/carrier.dmm`。`nt_prisoner` **未发现任何地图放置**。
套装（`nsv13/code/modules/clothing/custom_outfits.dm`）：soldier（护甲背心、头盔、
战斗刀、辛迪加指挥 ID），pilot（黑色手套、辛迪加指挥 ID），prisoner（囚犯制服、橙色
鞋子、囚犯 ID）。辛迪加休眠套装的 `tc = 0`。
条件：幽灵生成器使用基础 `mob_spawn/attack_ghost` 流程
（`code/modules/awaymissions/corpse.dm:38-63`）：回合必须已开始、
`GHOSTROLE_SPAWNER` 标志开启、未使用、未被职业封禁（此处 `banType` 为空）、确认提示，
然后 `create(ckey)`。这些生成器没有 `antagonist_type`/`objectives` ⇒ 非反派。
例外 / 覆写：与许多 tg 生成器不同，`use_cooldown` 保留为默认值（FALSE）——无最近
死亡冷却。相关但不同的生成器（`mob_spawn/human/syndicate/boarding*`）记录于
`boarding.md` BOARD-011。
置信度：机制部分为 HIGH（高）；`nt_prisoner` 是否在任何地方被使用为 MEDIUM（中）
（仅找到定义）。

## 反派名录

| 反派 / 幽灵角色 | 加入方式 | 目标 | 装备 / 备注 |
|---|---|---|---|
| 辛迪加登舰者 | 中场幽灵民意征集（`role_preference/midround_ghost/boarder`），≥20 名活跃玩家；否则 KNPC 回退 | `/datum/objective/hijack`（+20% 殉道者） | 登舰舱；SMG/霰弹枪/医疗套件（`syndicate/odst/*`）；无 uplink（`should_equip=FALSE`）。系统 17。 |
| 海盗登舰者 | 同一民意征集，海盗阵营路径 | 团队 `/datum/objective/loot/plunder`（"50000 信用点"） | 太空海盗登舰者套装（lead/sapper/gunner）；不向船员公告。系统 17。 |
| 幽灵船 | 舰队遭遇 20% 民意征集 / 管理员 `vv` / 管理员动词（`ROLE_GHOSTSHIP`） | 无正式目标——驾驶/与船员交战 | 敌方星图舰船；战斗机被增强（×6 完整度，500 发弹药）；获得 DRADIS + 战术控制台。 |
| 血裔主脑 | 回合开始 `game_mode/bloodling`（20 名玩家）/ 中途加入偏好 | 升华（`objective/bloodling_ascend`）——生成末日实体；结束回合 | 微型通风管道爬行者；生物质组件；hide/thermal/absorb/infest/build/ascend 能力；增强仆从。 |
| 血裔奴仆（变色龙） | 与主脑一同被选中 / 被侵染的人类 | 侍奉（`objective/bloodling_serve`，自动完成） | 阉割版变色龙能力集，`geneticpoints = 5`，血裔蜂巢意识聊天。 |
| 血裔仆从 | 侵染非人类 / give_life 幽灵民意征集 / 点击已升华的主脑 | 侍奉 | `/mob/living/simple_animal/bloodling_minion`（收割者，或坦克）；`/datum/component/bloodling/lesser`。 |
| 辛迪加船员（Galactic Conquest） | PVP 回合开始，`role_preference/antagonist/pvp`；11 个子角色 + Autofill | 赢得游戏（无目标）；占领星系 / 核弹 / 摧毁 NT 舰船 | 角色专属套装 + ID，辛迪加锁定频率的无线电，武器认证植入体，无 uplink。 |
| 辛迪加船员防守方（幽灵） | 点击航母内部上的 `nsv13/syndicate_crew` 低温舱 | 防守舰船 | 士兵/飞行员休眠套装；非反派。 |
| NT 囚犯（幽灵） | 点击 `nsv13/nt_prisoner` 舱（未发现地图放置） | 活着逃脱 | 囚犯制服/ID；明确不是反派。 |

## 跨系统依赖

- **系统 17（`boarding.md`）：** 实际的登舰者生成（`spawn_boarders` →
  `spawn_player_boarders`/`spawn_knpcs`）、登舰舱、EWAR 内部空间加载、IFF 夺取、
  KNPC 小队、幽灵防守方。此处的登舰者 datum 是那一流程之上的 *目标* 层。
- **系统 1（`round_flow.md`）：** 游戏模式选择（`SSovermap_mode` 挑选
  `/datum/overmap_gamemode`，而非空间站游戏模式）；`SSticker` 并行运行空间站模式
  （`bloodling`、`pvp`）；`SSstar_system.time_limit` 对 PVP 设上限。
- **系统 22（`security.md`）：** 血裔受限/受保护职业（安保 role）、升华期间的 delta
  警报与穿梭机封锁；PVP 陆战队小队/军械库装备（系统 23 小队，从 boarding.md 引用）。
- **星图 / 舰队（`fleets_ai.md`、`overmap.md`）：** 幽灵船与 KNPC/登舰者舰船是
  `obj/structure/overmap` 资产；`AI_FLAG_BOARDER` 对登舰侵略性进行门控；灯塔信标 /
  阵营 tickets 驱动 PVP 计分（`SSstar_system.factions`）。
- **TGUI（有意不细述）：** `SyndieJobSelect`、`ShipLoadout`、
  `OvermapGamemodeController`。

## 未解问题

1. **幽灵船 "目标"：** `/datum/antagonist/ghost_ship` 不携带任何目标——其意图是纯粹的
   涌现式 PvP，还是本意有一个目标层（例如掠夺/登上 NT 舰船）但被放弃？（`overmap_ghosts.dm`
   中不存在任何与目标相关的代码。）
2. **`nt_prisoner` 生成器** 被完整定义，但没有 `.dmm` 放置它；它是死内容，还是为计划
   中的地图预留的？
3. **血裔升华目标层级：** `on_evolve` 处理层级 ≥8（`ENVIRONMENT_SMASH_RWALLS`），但
   `max_evolution = 6` 与 `final_form_biomass = 1500`（⇒ 300 生物质时为层级 6）意味着
   层级 8 的破墙在正常成长中不可达——是死分支，还是为升华形态所设？
4. **血裔 `check_win` 与 `bloodling_win`：** `game_mode/bloodling/check_win()` 一旦
   存在拥有 INFINITY 生物质的升华主脑就返回真，这会在升华时立即触发，而非在 10 分钟
   计时器之后；这两条路径在回合结束时如何交互，仅靠阅读未能厘清。
5. **PVP ticket 目标** 是 `F.tickets + 700`，在目标创建时计算（动态基线），因此有效
   的胜利阈值取决于起始阵营 tickets——需确认 NT 是否以某种方式 "起始" 带有 tickets，
   从而改变实际数值。
6. **`enemies_to_spawn = max(1, round(num_players()/2.5))`**——代码注释说 "on a
   standard 30 pop this'll be ... 2"（它做的是 `30/2.5 = 12`），因此注释与公式不一致；
   真实比例约为 40% 辛迪加，而非 ~17%。注释似乎已过时。
7. **`syndicate_crew` 的 `move_to_spawnpoint`** 强制移动到一个随机的
   `GLOB.syndi_crew_spawns` 地标；若实例化地图缺少它们，行为未定义（未显示空值守卫）。
