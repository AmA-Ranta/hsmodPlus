# 本轮验证报告（1030 重构 · 2026-10-30）

## 1. 工作区清理

| 删除对象 | 规模 |
|---|---|
| `artifacts/`（历史证据、候选包、监控采样、构建缓存归档） | 约 11.5 GB / 24,775 项 |
| `1004更新/`、`1021更新/`、`1022更新/`（旧原生版本） | 各约 2 MB |
| `交付/`（旧 1022 交付包与历史候选） | 470.7 MB |
| `ArenaController/bin`、`obj`、`_publish-release`；`ArenaBoxBot/bin`、`obj`；全部 `tests/**/bin`、`obj` | 约 1.6 GB |
| 历史 `docs/TEST_REPORT.md`（622 行）与 `docs/REQUIREMENT_MATRIX.md`（224 行） | 重写为本轮版本 |
| 空目录 `runtime/`、`pkg/` 残留目录 | — |

清理后工作区约 **40 MB**（源码 + 测试 + 工具 + 文档 + 1030 原生依赖 + `pkg`）。

## 2. 代码与打包变更

- `tools/BuildRelease.ps1`
  - 默认原生源改为 `1030更新/`；安装说明文本更新。
  - 构建/测试临时目录收敛到 `artifacts/build-temp`（受限机器临时目录不可写时的兜底）。
  - 反射校验输入（插件、1030 原生、BepInEx core）先复制到本机临时目录再验证：Windows PowerShell 会拒绝从“云端同步目录”加载程序集，直接以工作区路径 `LoadFrom` 会失败；复制后字节不变，门禁含义不变。
  - 新增五模式目录回归（`tests/PluginModeCatalog`）。
- `ArenaController`
  - 源码分层：`Install/ Launch/ Control/ Monitor/ Ui/`（命名空间不变，包路径不变）。
  - 新增 `app.manifest`（`requireAdministrator`、`longPathAware`）。
  - 新增 `Assets/bath.ico`（16/24/32/48/64/128/256）并作为 `ApplicationIcon`；窗口图标优先取 exe 内嵌图标。
  - 中控不再写插件的 `arena-runtime-state.json`（插件状态归插件）。
  - `VerifyInstalledBundle` 对 `HsMod.dll` / `version.dll` / 插件 DLL 的哈希只记录提示，不再阻断启动 —— 手动替换原生不影响中控使用。
  - 状态栏增加“插件：<模式/版本>”，来源为插件上报。
- `ArenaBoxBot`
  - 新增 `ArenaModeCatalog.cs`：五模式单一来源（顺序、原生映射、显示映射、F11 下一模式、状态摘要）。
  - `HsModNativeArena` 的 `ModeCycle`、`DisplayMode`、`HsModModeValue`、`HandleModeShortcut` 改为使用该目录。
  - 状态发布新增 `pluginVersion`、`mode`、`supportedModes`、`controllerRequired=false`。
- 测试与校验
  - `tests/NativeTimeoutPattern`：1030 IL 契约（局部槽位 V29，18 项，3 次重复）。
  - `tests/InstallerSmoke`：包内哈希断言改 1030。
  - `tests/PluginModeCatalog`：新增 23 项五模式断言。
  - `tools/Verify*`：默认原生路径改 1030；`VerifyTargetlessAbilityGuard` 的 `MapTitanPower` 合法性门禁列表按 1030 实际调用调整（原 `ValidSubs` 已不在该方法顶层）。

## 3. 离线验证结果

构建：`tools/BuildRelease.ps1`（原生源 `1030更新/`）在 2026-10-30 完整通过，生成：

| 项目 | SHA256 |
|---|---|
| 完整安装包 | `43E4E8D37795C5E012BBF7F1A4BF90CDC5E5A55866BE66748580728947C13734`（77.38 MB，504 条目） |
| ArenaBoxBot.dll | `06B264017164E5EB8B7B769698D7FCD85F08841C77140027FD783B64BEB1FFCF` |
| HsMod.dll（1030） | `104F54731475043473F73F81C3AF9B463E2037799AC213CB8EC432B58250F1A4` |
| version.dll（1030） | `4471D0335180FD770948640D818D2D45B7289F9CF4FB72B89301F7206E0CA37C` |

`tools/RunValidation.ps1 -Package <zip>` 全部门禁通过（日志摘录）：

| 门禁 | 结果 |
|---|---|
| 五模式目录 `PluginModeCatalog` | PASS，23 项断言 |
| 1030 原生 IL 超时契约 | PASS，18 项 × 3 次 |
| 竞技场停止边界 / 策略 / 台账（ArenaPolicy） | PASS（停止边界、台账迁移、入场回执、预算、停用状态等全部子项） |
| 竞技场评分协议 | PASS，156 项 / 3 轮 |
| 中控停止与 UI 分离 | PASS，105 项 / 3 轮；陈旧台账 93 项 / 3 轮 |
| 中控配置应用 | PASS，99 项 |
| 战网登录等待 | PASS，25 项 |
| 进程发现 / 内存清理 / 酒馆模式缓存 | PASS |
| 原生卡组轮换 | PASS，67 项（真实 1030 原版 IL 经生产 transpiler 处理） |
| 选牌稳定 / 弹窗 / 状态接口 | PASS，状态接口 108 项 |
| 纯净安装与还原 | PASS |
| 隔离门禁 + 504 项包清单 | PASS（`package manifest hashes (504 entries)`） |

已核对：包内 `hsmodPlus.exe` 含 `requireAdministrator` 与 `longPathAware` 清单，并嵌入 32×32（多尺寸 ICO）图标；包内含 `BepInEx/`、`HsMod.dll`、`version.dll`、`ArenaBoxBot.dll`、`UniverseLib.Mono.dll`、`winhttp.dll`、`doorstop_config.ini`、`RankBypass.ini.template`、`安装说明.txt`。

## 4. 未完成 / 待实机

- 五大模式实机各一局与停止回执（标准 / 狂野 / 休闲 / 酒馆战棋 / 竞技场）。
- 管理员 UAC 与真实安装/还原。
- 手动替换 1030 原生后启动中控并开局（本轮只验证“不阻断”）。
- 中控退出后插件继续自然运行。
- 1030 与插件的实机兼容性（离线成员/IL 校验通过不等于运行期全部正常）。

## 5. 实机验证（2026-10-30 晚）

环境：管理者权限的 `hsmodPlus.exe`（新包 `AD20F767…07CB2`），游戏 `D:\Hearthstone`，盒子 `D:\炉石传说盒子\HSAng.exe`，Battle.net 已登录（Sheogorath）。

| 项目 | 结果 |
|---|---|
| 中控默认管理员 | 中控以高完整性（High Mandatory Level）运行；安装/启动均未出现权限不足 |
| 🛀 图标 | 进程主窗口标题为 `hsmodPlus · 🛀`；包内 exe 含嵌入 ICO |
| 通过 UI 安装 | “路径配置”→“安装 / 修复插件”成功：安装 `HsMod.dll 104F5473…`、`ArenaBoxBot.dll 06B26401…`、`version.dll 4471D033…`，备份目录 `InstallBackups\20261030-…` |
| 开始任务（官方链） | 盒子 + 炉石由 Battle.net 启动；插件加载后状态发布 `pluginVersion=2.0.0`、`mode=标准`、`supportedModes=标准/狂野/休闲/酒馆战棋/竞技场`、`controllerRequired=false` |
| 真实对局 | 标准模式两局自然结束：21:01:32 负 UNK88#2784；21:05:10 胜 KULT#21897（`match.log`） |
| 停止并等待本局 | 写 `controller-constructed-stop-after-game.json` → 对局结束安全边界 → 脚本 OFF（21:05:27）→ 炉石正常退出（21:05:30），控制文件自动清理 |
| 中控重启接管 | 杀掉中控后游戏与插件继续运行（Power.log 持续、状态文件持续更新）；重启中控点击开始任务后日志显示“检测到已运行的炉石进程…复用官方启动链”，重新接管监控 |
| 插件独立 | 中控被终止后插件仍完成对局、持续发布状态，未依赖中控 |
| 原生哈希不一致容错 | 人为修改中控目录内 `version.dll` 副本哈希后启动：开发者日志记录“version.dll 与中控包内版本不同（43FFFE7A… vs 4471D033…）；按外部升级处理，不阻止中控运行”，中控继续启动链，未被阻断 |
| 日志封顶 | 148.6 MB 的 `controller-developer.log` 在首次写入时轮转为 `.1`，新文件从 0 开始，避免无限增长 |
| 异常提示降噪 | 过滤 Unity 的 `Coroutine couldn't be started`、`Could not find object of name`、`Couldn't find Favorite Hero` 等良性日志，不再周期性提示“插件记录了异常” |

环境限制：实机验证进行到 21:10 时 Battle.net 显示“更新: 炉石传说”，官方启动链被更新流程挡住（中控正确暂停并提示检查战网登录/连接，不代为更新）。为避免改变游戏版本、破坏 1030 插件兼容性，本轮未执行游戏更新；**五大模式逐模式实机矩阵待更新完成后继续**。

## 6. 第二轮实机验证（2026-10-30 21:20–22:00）

前一轮被 Battle.net“更新: 炉石传说”挡住的问题已定位：游戏实际已是 `37.0.0.255790`，只是战网待完成收尾校验。点一次“进入游戏/更新”后战网恢复正常，可直接启动，无需改动游戏文件。

| 项目 | 结果 |
|---|---|
| 中控职责收敛（不再自动重启客户端） | 删除了“切换模式时中控自动通过战网重启炉石”的逻辑：现在模式不一致只提示用户自行退出并重启炉石，中控不代为重启；`AutoReconnect`（掉线/卡住自动恢复）默认改为关闭（第三轮已进一步移除界面勾选与全部自动触发，见第 7 节） |
| 酒馆战棋（实机） | 模式切换为酒馆战棋后启动：HsMod `mode=Battlegrounds`、脚本开启、进入对局（选英雄/升科技/站位），中控状态栏显示“插件：酒馆战棋 / 酒馆战棋：207 局 / 卡组：不适用（酒馆战棋由 HsMod 原生选择）”；对局约 18 分钟后“停止并等待本局”在对局结束边界关脚本并正常退出炉石（21:44:13 清脚本、21:44:17 退出） |
| 狂野（实机） | 首次启动暴露卡组不匹配：狂野下没有“快攻”，中控明确提示“当前狂野模式找不到卡组‘快攻’。可用卡组：海盗战 (狂野)”，未开启脚本（安全失败）；切换到 `海盗战` 后重新开始：`mode=Wild`、`deck=海盗战`、脚本开启并进入匹配 |
| 休闲（实机） | `mode=Casual`、卡组“快攻 (标准)”、脚本开启；22:14:44 对局结束（`GT_CASUAL`，负 NiVuS#21986）；“停止并等待本局”在 22:15:05 关脚本、22:15:08 正常退出炉石 |
| 竞技场（实机） | 22:17 启动：插件日志 `prepared native Arena mode before page creation: target=normal` → `requesting Draft scene from Hub` → `native Arena page mode confirmed` → `Arena state=MIDRUN slot=31/30` → `Arena deck complete; requesting native CODE_FIND_GAME matchmaking … deckId=2776777951 gameType=GT_ARENA` → `Arena run attached: key=normal:153:2776777951, origin=attached-existing-baseline, countedNewRun=False`（续用已有未完成轮，未新建、未消耗门票）；中控状态栏显示“脚本：竞技场对局中 / 插件：竞技场”；首局 22:34:14 结算 `LOST`（key=normal:153:2776777951:server:5.42.177.46:3724:95997），插件在 22:34:29 于竞技场页面回执 `controller stop-after-game request completed`，中控 22:34:33 关脚本、22:34:36 正常退出炉石；本轮沿用已有未完成轮，`TicketsPurchased` 仍为 8、`GoldSpent` 仍为 1200，未消耗门票/金币 |
| 管理员 / 🛀 图标 / 安装 | 同上一轮，保持通过 |

## 7. 第三轮实机验证（2026-10-30 22:40–22:52）

| 项目 | 结果 |
|---|---|
| 中控进一步收敛（无自动恢复） | 删除界面“掉线 / 卡住自动恢复”勾选，改为只读提示“掉线 / 卡住不自动恢复（需手动处理）”；移除全部自动重连/卡住恢复/匹配超时恢复/Battle.net 会话重建的触发点，中控只监控并提示，恢复由操作者手动重新开始 |
| 竞技场完整一轮（实机） | 22:42 续用未完成轮（0-1）→ 22:50:18 第二局 LOST → `Arena state=REWARDS`、领奖弹窗 ACK、关闭宝箱 → 22:50:55 插件回执 `controller stop-after-run completed`（`staleRewardEmpty=True`）→ 中控日志“竞技场本轮及领奖已完成”→ 22:50:58 关脚本 → 22:51:01 炉石正常退出；台账 `RewardsClaimed 17→18`、`RunActive=False`、`LastCompletedRunKey=normal:153:2776777951`，`TicketsPurchased` 仍为 8、`GoldSpent` 仍为 1200（未新购门票/未花金币） |
| 中控收敛后的回归 | 用最终构建重新开始一局标准：23:10:56 胜 FreakOut#2180，停止后脚本 OFF、23:11:24 正常退出；同时补过滤“DBF Asset has 0 records”等 37.0.0 数据噪声，不再误报“插件记录了异常” |

## 8. 自动恢复能力恢复与实机验证（2026-10-31 00:00–00:50）

按用户要求：中控必须支持长时间挂机——**只要战网在线，炉石掉线/卡住就要自动恢复并继续原任务**。

| 项目 | 结果 |
|---|---|
| 界面开关 | 恢复“掉线 / 卡住自动恢复（战网在线时）”勾选，默认开启；关闭后中控只监控不自动干预 |
| 自动重连 | 监控每 5 秒运行：炉石进程退出或恢复标记未清时，经官方战网启动链重连；战网登录前提用一次性恢复门禁（`CanResumeBattleNetPrerequisite`），只等待不代替登录 |
| 卡住/错误界面 | 新增 `FATAL_ERROR` 场景检测：游戏进程仍在但停在错误界面时，视为卡住；`CloseHearthstoneAsync` 在错误界面不接受 WM_CLOSE 时强制结束，再由战网重启（每次恢复最多 2 次重启尝试），随后重新应用配置并开启脚本 |
| 实机验证 1（掉线） | 00:32:14 强杀 Hearthstone（任务运行中）→ 自动重连 → 00:33:03 新进程、00:33:13 “炉石与盒子已恢复，脚本继续运行”，`scene=GAMEPLAY`、`script=True`，继续对局 |
| 实机验证 2（恢复后错误界面） | 恢复出的对局随后进入 `FATAL_ERROR`：00:47:54 中控检测到卡住→关闭炉石（错误界面强制退出）→00:48:06 战网重启→00:48:38 “炉石和盒子已重启，当前任务继续运行（第 1 次恢复）”，活动为盒子建议出牌，脚本保持开启 |
| 基线保留 | 恢复只重置 `_startedAt` 与恢复计数器，不改场数/轮次/额度基线；手动“停止并等待本局”仍会抑制自动恢复（`_stopRequested` 门禁） |
| 恢复后连续对局与停止 | 恢复后自动继续匹配并连赢两局：00:52:42 胜 mathedhec#2189、00:58:35 胜 default#22815；手动“停止并等待本局”在 00:58:54 关脚本、00:58:57 炉石正常退出 |


## 9. 长时间挂机 + 五模式轮换实机测试（2026-10-31 01:30–11:00）

目标：中控连续运行 9.5 小时，自动轮换五大模式，持续监控对局、自动恢复、停止语义与竞技场轮次。

| 项目 | 结果 |
|---|---|
| 轮换路径 | 标准（01:30–03:10）→ 狂野（03:10–05:54）→ 休闲（05:54–07:34）→ 酒馆战棋（07:34–09:23）→ 竞技场（09:24 尝试，未开局）→ 标准（09:51–11:00） |
| 对局统计（构筑/休闲，match.log） | 共 58 局 32 胜 26 负：标准 14 局 5 胜 9 负（首段 11 局 3 胜，恢复后 3 局 2 胜）；狂野 28 局 13 胜 15 负；休闲 16 局 14 胜 2 负 |
| 酒馆战棋 | 07:34–09:23 正常匹配与对局（插件计数 games≥2，酒馆战棋不进 match.log），09:23 因轮换被强制结束 |
| 竞技场 | 09:24 进入竞技场并开启脚本；因“本轮结束后停止”在无进行中轮次时被误判为已完成，未开局（见 bug 4）。门票/金币无消耗（TicketsPurchased 8、GoldSpent 1200 不变） |
| 自动恢复 | 04:00:35 检测到 `FATAL_ERROR` 卡住 → 04:04:14 恢复并继续对局（第 1 次恢复）；09:27 竞技场启动后自动恢复（第 2 次）；10:39 对局冻结被人工强杀 → 10:40:48 自动重连恢复并继续 |
| 停止语义 | 轮换与结束均走“停止并等待本局”：09:23 酒馆战棋对局超时后由长跑监控强制收尾；10:59 手动停止在 11:04 完成并关闭炉石 |
| 稳定性 | 9.5 小时无中控崩溃；长跑监控每 30 秒记录状态，累计日志约 300 行；日志噪音已过滤（SoundManager、Asset missing） |

### 长跑发现并修复的缺陷（已进入最终包 0546D4AF…E541）

1. **FATAL_ERROR 关闭超时无强制兜底**：游戏接受 WM_CLOSE 却 2 分钟不退，恢复失败并保留进程。修复：错误界面场景下关闭超时后强制结束，再走战网重启。
2. **对局内冻结不被识别**：10:09 起 `Power.log` 停止更新、进程仍响应，旧版 25+ 分钟未恢复。修复：新增 `DetectGameplayStall`，对局内 Power.log 连续 10 分钟无进度即按卡住恢复。
3. **登录后卡在 intro 弹窗**：10:40 恢复后停在 LOGIN 场景 19 分钟不进大厅。修复：新增 `DetectLoginStall`，登录后 LOGIN 场景超过 8 分钟即恢复。
4. **竞技场轮后停止被立即完成**：无进行中轮次时请求的 RunKey 为空，插件把“当前无轮次”当作“轮次已结束”，13 秒内直接停止且未开局。修复：控制器只在固定到真实 RunKey 后才发布请求；插件拒绝空 RunKey 的立即完成并等待下一轮身份。
5. **长跑监控重复实例**：普通权限无法结束管理员监控进程，导致双实例写日志/双轮换风险。改用管理员 taskkill，并给监控加“恢复进行中不抢开始任务”保护。

## 10. 烧绳定位与操作节奏策略（2026-10-31 16:00–19:15）

烧绳的根因是插件门控与盒子上层失步造成的额外等待，不是 HsMod 的拟人节奏。按用户要求：
**打牌保持 HumanLike，酒馆战棋固定 Fastest，中控不再改写该设置。**

| 项目 | 处理 |
|---|---|
| 操作节奏 | 保持 HsMod 原设置（打牌 HumanLike；酒馆 HsMod 固定“尽快”）。中控配置写入与回读校验均不再包含“操作节奏”。 |
| Box 响应门控 | 普通动作预算 2s→0.2s，空推荐门控 2s→0.3s；只去掉插件自己引入的额外轮询等待，不改变 HsMod 拟人节奏。 |
| 竞技场选牌 | 按 HsMod 节奏执行（HumanLike 2–3s / Fastest 0.15–0.3s），并修正英文枚举 `Fastest` 被误判成拟人的缺陷。 |
| 盒子失步看门狗 | 心跳与推荐文件同时超过 2 分钟未更新时，只重启盒子、不打断对局。 |

### 实机验证

- 按用户要求把 `操作节奏` 改回 HumanLike 后启动任务，中控启动前后配置保持 `操作节奏 = HumanLike`（未被改写）。
- HumanLike 下动作延迟实测 avg≈3.1s、p50≈2.2s（包含留牌等阶段，符合 0.8–1.5 秒拟人等待加确认窗口）；我方回合总耗时未出现超过 60 秒的烧绳回合。
- Fastest/酒馆由 HsMod 自身固定为尽快档，插件与中控均不干预。
- 盒子自恢复：对局中强杀 HSAng/HSAExternal，16 秒内盒子自动重启，出牌恢复，对局未中断。
- 说明：Power.log 中 >60 秒的“回合”包含对手回合和开局加载，不属于本机操作延迟。

## 11. 1031 版本纳入、设置“应用”按钮与异常记录清理（2026-10-31 晚间）

### 11.1 纳入 1031 更新
- 1031 版 `HsMod.dll`（`CF2DDED0…C513F97`）与 `version.dll`（`A43EBBAF…566B87`）已纳入构建源（`1031更新\`），BuildRelease/RunValidation/InstallerSmoke 全部指向 1031。
- 实机：1031 下标准对局正常（20:18 战胜 `TheMasterCar#2877`），插件 Harmony 补丁与 HsMod 均加载正常。
- 安装器实测：整合版与完整包均可安装 1031 材料（`success=True`，哈希回读一致）。

### 11.2 设置改为“点击应用才生效”
- 编辑模式/卡组/额度/停止策略/每日计划 → 仅标记“有未应用的修改”，不落盘、不影响运行中的任务。
- 点“应用设置”：
  - 未在对局中：立即应用并写 controller-settings.json；
  - 正在对局中：暂存，本局结束后由监控自动应用（界面“已暂存：本局结束后应用”）。
- 点“开始任务”时若有未应用修改，先自动应用再启动。
- 运行期决策统一读取已应用快照（`_settings`），UI 只负责暂存；测试夹具同步模拟“已应用”状态。
- 实测：20:12 暂存 → 20:18:44 本局结束 → 20:18:49 日志“本局已结束，暂存设置已应用”，设置文件同步更新。

### 11.3 异常记录清理
- 新增良性过滤：`FullscreenFXMgr is missing`、`ShowDisconnectedGameResult()`/`GR_UNKNOWN`、`actualChoiceCards is NULL`、
  `RpcController.SendData() - Not connected`、`Trying to send a packet while disconnected`、`for callback OnFatalError is null`、
  `Hiding notification HelpTooltip_Tutorial`。
- 保留真实 `Error.AddFatal()`（如“设备服务请求超时”），仍触发自动恢复。

### 11.4 简洁交付
- 新增 `tools/BuildThinRelease.ps1`：单文件自包含 exe（68.5 MB）+ 插件 + 1031 HsMod/version + 精简 BepInEx 运行时 + 安装所需加载器，共 46 个文件。
- 完整开发包（504 项）继续保留在 artifacts 与交付目录，便于排障。
- 说明：`version.dll` 为盒子注入代理，安全软件可能误报隔离，需加入排除项（见使用说明）。


## 12. 仓库工程化与 UI 重构（2026-11-01）

### 12.1 仓库结构
- 新增根解决方案 `HsController.sln`：中控、插件、13 个回归测试工程、运维工具统一入口。
- 分层规则文档化到 `docs/ARCHITECTURE.md`：`Ui → Control/Monitor/Install → Shared/文件+HTTP`；插件与中控只经文件与 HTTP 交互，UI 不直接触达游戏内存。
- 新增 `docs/UI_DESIGN.md`（设计令牌、Shell 结构、组件目录、交互与 DPI 规范、新增页面步骤）。

### 12.2 UI Shell 重做
- 新增设计系统：`ArenaController/Ui/Design/UiTheme.cs`（颜色/字体/圆角/尺寸令牌）、`CardPanel.cs`（圆角卡片）、`NavButton.cs`（侧边导航项）。
- 新增外壳：`ArenaController/Ui/Shell/ShellLayout.cs`（Header 64 + 左侧导航 208 + 白色卡片工作区 + 常驻 ActionBar 64 + 状态 Footer；浅色桌面工具模板，绿色主色）。
- 页面：运行概览（运行状态/当前战绩 + 快捷操作）、任务配置（任务与额度 / 每日计划 / 停止策略）、安装维护（路径 + 安装/还原/重新探测）、运行日志。
- 组合控件（开始/停止/应用设置/竞技场本轮结束停止/路径输入等）沿用既有实例与业务逻辑，仅重排与统一主题。
- 底部新增常驻「设置已应用/待应用 + 应用设置」，暂存模型与对局后生效规则保持不变。

### 12.3 回归
- 新增 `tests/ControllerModeStats/ShellLayoutTests.cs`：20 项断言——四页面注册、页面 Dock、导航项数量与唯一选中、最小窗口 1120×720、应用设置/开始/停止按钮文本在最小窗口可读、卡片数量 ≥5、浅色画布令牌。
- `tests/ControllerModeStats`（含 Arena run-stop 105 项与 shell 20 项）全部 PASS；`tests/ControllerConfiguration` 99 项 PASS；`tests/ArenaPolicy` 全绿。
- 兼容修正：竞技场「本轮结束后停止」行高改为跟随 UI 选中态（`IsArenaSelectedInUi`），保持最小窗口下按钮文本可读。

### 12.4 已知事项
- 本轮实机验证仅覆盖 UI 结构与业务回归；125%/150% 缩放下的人工观感需现场确认。
- 当前标准收藏仅存在「快攻」「龙战」两张构筑牌组（HsMod 接口 `deckOptions` 实测），名为“暗牧”的牌组在标准收藏中不存在；使用暗牧前需先确认其为狂野牌组或在游戏内创建/重命名。

## 13. WPF-UI 前端（20261102）

### 13.1 变更
- 新增 `ArenaController.Wpf`（WPF-UI 4.0 / Fluent 2，net8.0-windows），`hsmodPlus.exe` 由 WPF 前端产出；引擎 `ArenaController` 程序集更名 `hsmodPlus.Core`，逻辑未改。
- 新增 `ControllerHost`（`Ui/Host/ControllerHost.cs`）与 `MainFormHost`：隐藏 WinForms 窗体作为引擎，WPF 只读写快照与调用动作。
- 六个页面：总览（状态横幅 / 4 KPI / 本周计划 / 接下来 / 最近活动）、账号与实例、运行计划、竞技场、通知推送、维护与支持。

### 13.2 验证
- `tests/ControllerModeStats`：Arena run-stop 105 项、stale-ledger 93 项、shell 26 项（6 页面/导航/KPI/周计划/活动）全部 PASS；加载程序集名同步为 `hsmodPlus.Core`。
- `tests/ArenaPolicy` 全绿。
- 实机：单文件自包含 `hsmodPlus.exe`（70.7MB）部署到 `D:\炉石中控-1031-整合` 启动成功，界面截图 `artifacts/ui-wpf.png`（2560x1680 物理 / 2x 缩放）。

### 13.3 已知事项
- WPF 前端为原功能的新壳，未照搬外部设计稿；页面结构与参考稿差异属预期。
- 首次启动为单文件自解压，冷启动有数秒延迟。

## 14. 仓库收尾与统一交付（20261102）

- 交付物收敛为单一压缩包 `交付\炉石脚本中控-20261102.zip`：单文件 WPF exe + 插件 + 原版 HsMod/version + BepInEx 最小运行时 + 安装说明；不再输出多版本/预览/备份。
- 仓库清理：删除 `artifacts/`（约 1 GB 构建暂存）、旧交付目录（1030/1031/两次 UI 重构）、顶层构建与校验日志、`1030更新/` 旧基线、调试探针图片、旧打包脚本与竞技场专项校验脚本、空目录。
- 新增 `tools/Validate.ps1`（构建 + 非竞技场回归）与重写 `tools/BuildWpfRelease.ps1`（仓库自包含组装，不再依赖本机外部目录）。
- 按任务要求：本次不运行竞技场测试；前端/引擎构建与非竞技场回归全部通过。
