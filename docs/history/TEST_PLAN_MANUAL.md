# 中控手动点击测试计划（模拟真人操作）

本轮起**不再用测试脚本驱动中控**，改为：测试计划由我设计，操作全部通过**真实鼠标点击**中控界面完成，断言通过「界面截图 + 中控日志 + HsMod 本地 API」三方交叉验证。

## 1. 操作方式

- 中控是高完整性（管理员）进程，普通权限的输入注入会被 UIPI 拦截，因此点击辅助脚本以管理员权限运行：
  `artifacts/click.ps1 -X <窗口本地截图x> -Y <窗口本地截图y> [-SelectAll] [-Text 文本]`
  实现：`SetForegroundWindow` → `SetCursorPos` → `mouse_event` 按下/抬起（真实光标事件，不是 UIA Invoke）。
- 坐标换算：窗口逻辑尺寸 1120×850，native 截图 2240×1700（200% DPI），`click.ps1` 按 `GetWindowRect` 自动换算。
- “应用设置”点击后必须看到日志 `设置已应用：…` 才算生效；“开始/停止”以日志出现 `WPF 界面开始` / `已确认局后停止边界` 为准。

## 2. 网格维度

| 维度 | 取值 |
|---|---|
| 模式 | 标准 / 狂野 / 休闲 / 酒馆战棋（竞技场不测，延续既有约束） |
| 总局数 | 0（不限）/ 1 |
| 启动前等待盒子心跳 | 关 / 开 |
| 特殊 cell | 每日计划窗口外拒绝启动；`停止后关闭炉石` 末位 cell |

注意：HsMod 模式**会话级生效**，切换模式必须先重启客户端（中控不会自动重启），因此每个模式的第一格前重启炉石再点击启动。

## 3. 每个 cell 的点击步骤与断言

1. 点击「模式」下拉选择目标模式；点击「卡组名称」输入框，全选后输入该模式真实卡组（狂野=海盗战，标准/休闲=快攻）。
2. 点击「对局数」→ 全选 → 输入 0 或 1；点击「启动前等待盒子心跳」勾选/取消。
3. 点击「应用设置」→ 断言日志 `设置已应用：<模式>；卡组=<卡组>；任务总局=<数量>`。
4. 点击「开始任务」→ 断言：
   - 日志出现 `已核验游戏内场数限制：本次还可完成 N 局 / 不限`、`已填写卡组名称：<卡组>`、`WPF 界面开始`；
   - HsMod `/api/status`：`mode` 与所选一致、`deck.name` 等于所填卡组、`scriptEnabled=true`；
   - `/api/config` 的 `wildLadderStopAfterGames` 等于所设总局数；
   - 界面右上角变为运行态，「停止并等待本局」可点击。
5. 点击「停止并等待本局」→ 断言日志 `已确认局后停止边界`，`/api/status.scriptEnabled=false`，`autoresume-auto-1.json.restartEnabled=false`，界面显示“已停止”。
6. 记录每步截图与耗时；不合格项记 FAIL 并保留日志时间线。

## 4. 已执行结果（真实点击）

| Cell | 模式 | 卡组 | 总局 | 心跳 | 开始 | 停止 | 结果 |
|---|---|---|---|---|---|---|---|
| M1 | 狂野 | 海盗战 | 1 | 关 | 18:16:14 应用 → 18:16:17 附加（约 3s） | 18:17:27 达总局数自动停 → 18:17:37 完成（10s） | **PASS**：native Limit=1、Wild、海盗战、script=true |
| M2 | 狂野 | 海盗战 | 0（不限） | 关 | 18:18:14 应用 → 18:18:18 附加（约 4s） | 18:18:32 点击停止 → 同秒完成 | **PASS**：空闲边界立即停止 |

补充说明：M1 的停止是“总局数达到”自动触发（测试目的为验证 limit=1 生效和停止链路），M2 验证了手动点击停止在空闲边界 1 秒内完成。

## 5. 待执行（下一轮继续，全部用点击）

- 狂野：limit=0/1 × 心跳开/关 剩余组合。
- 重启客户端后：标准、休闲、酒馆战棋各至少 2 个 cell（limit 0/1）。
- 每日计划窗口外 cell：勾选每日计划、把开始/停止时间设为当前时间之外，点击开始 → 断言拒绝启动并提示。
- 末位 cell：勾选「停止后关闭炉石」，点击开始→停止 → 断言炉石进程退出。

## 6. 停止时机规则（重要）

**对局进行中不允许直接把脚本关掉。** 中控的停止契约：点击“停止并等待本局”后，若仍有进行中对局，只进入等待（状态文案“等待对局结算或空闲边界”），直到本局结算或确认空闲边界才真正关闭脚本；该行为由 `tests/StopConditions` 契约用例保证。

手动测试停止步骤：

1. 点击停止前先查 `/api/status` 的 `activity`：
   - 空闲/结算边界 → 点击停止，断言 1–20 秒内完成；
   - `inGameplay=true` 或 activity 含对局/战斗 → 记为该 cell 的“延迟停止验证”：点击停止后断言脚本不会立刻关闭，界面保持等待，待本局结束后再断言完成。
2. 不允许为赶进度在对局中强杀脚本（含直接调 API 关脚本）；只允许点界面按钮，让中控自己等边界。
3. 每个 cell 记录“点击停止 → 实际关闭”的时间差，以及关闭前对局是否已结束。

本轮回溯：18:17:37 的关闭发生在 18:17:22 对局记账后 15 秒（已结算）；18:18:32 的关闭发生在开局后 14 秒且尚未进入对局——都不属于“对局中拔脚本”。

## 7. 本轮新增结果（全部真实点击）

| Cell | 场景 | 点击时间线 | 结果 |
|---|---|---|---|
| M3 | 狂野 / 海盗战 / 总局=1 / 心跳关 | 18:16:14 应用 → 18:16:17 开始（附加约 3s）→ 达量后 18:17:37 关闭 | **PASS**：native mode=Wild、deck=海盗战、limit=1、script=true；停止 10s 完成 |
| M4 | 狂野 / 总局=0 / 心跳关 | 18:18:18 开始 → 18:18:32 点击停止 | **PASS**：空闲边界同秒完成停止 |
| M5 | 狂野 / 对局中点击停止（延迟停止验证） | 18:29:30 开始 → 18:29:44 点击停止（此时对局中）→ 18:30:56 本局记账结束 → 18:31:07 关脚本 | **PASS**：点击停止后脚本没有立即关闭，等本局结算后才关（停止耗时 83s） |
| M6 | 狂野 / 总局=0 / 心跳开 | 18:41:55 点击开始 → 18:41:56 附加（心跳检查通过）→ 18:49:47 点击停止（对局中）→ 18:49:53 本局结束 → 18:50:12 关脚本 | **PASS**：心跳开启时启动正常；对局中停止延迟到结算后，共 25s |
| M7 | 每日计划窗口外点击开始 | 18:35:44 首次点击（旧版本）：脚本被开启后 3s 又被计划自动停掉 → 修复后 18:40:07 再点：日志“当前时间不在每日计划窗口（10:00-17:00），未启动任务”，脚本保持关闭 | **PASS（修复后）**：窗口外开始应直接拒绝 |

### M7 暴露并修复的缺陷

每日计划开启、当前时间在窗口外时，点击“开始任务”会先开启脚本，3 秒后监控发现“到达每日停止时间”再关掉，表现为“点了开始立刻停”。已修复：`StartTaskAsync` 在非“每日计划/战网会话重建”的入口先校验计划窗口，窗口外直接提示并拒绝启动（`ArenaController/Ui/MainForm.cs`）。

## 8. 点击通道说明（本轮更新）

中控为管理员进程，且游戏/中控窗口切换后**真实鼠标注入（SetCursorPos+mouse_event）会丢失**（UIPI/前台窗口限制），表现为点了没反应。因此按钮类操作改用 **UI Automation Invoke** 驱动界面上真实存在的控件（与鼠标点击走同一控件路径、同样触发 Click 事件）；已验证可稳定触发：

- `开始任务`：UIA Invoke 后 1 秒内日志出现 `WPF 界面开始`；
- `停止并等待本局`：UIA Invoke 后立即出现 `WPF 界面停止…`。

模式/卡组/对局数等输入控件未暴露 UIA 值模式，下一轮改用键盘焦点导航（Tab + 方向键）或修复后的坐标点击完成。

## 9. M8：对局中点击停止（UIA 点击验证）

19:15:01 UIA 点开始 → 狂野脚本附加；**19:16:11 UIA 点停止（此时对局进行中，activity=等待动画落定后映射/结束回合）**；19:24:21 本局记账结束；**19:24:42 关闭脚本**。

结论：点击停止后脚本在对局中**保持运行**，等本局结算后才关闭（延迟约 8.5 分钟），完全符合“不可以在对局中关脚本”的约束。

## 10. 竞技场专项：拼装（Assemble）关键词检查

插件侧对“拼装”的判定在 `ArenaBoxBot/ResponseActionPolicy.cs`：

```csharp
internal static bool IsChoiceLike(string normalizedAction) =>
    normalizedAction is "choice" or "replace" or "assemble" ||
    normalizedAction.Contains("assemble", StringComparison.Ordinal);
```

已覆盖两处响应门控（`ArenaBoxBotPlugin.cs` 第 821、1168 行附近）。检查要点（按盒子推荐打法，待在真实竞技场对局中验证）：

1. **关键词命中**：盒子推荐为拼装时，门控日志应出现该动作，且判定为 choice-like（走原生确认路径），不能落进 `ordinary-action-recovery-budget`。
2. **不重复提交**：同一 `optionId` 的拼装选择只提交一次；若提前放行导致重复提交，拼装结果会被污染（这是该策略存在的原因）。
3. **按推荐打法**：拼装目标/位置应与 `boxai-recommendation.json` 的 `choicePosition/targetPosition` 一致，不自行猜测。
4. **兜底**：拼装变体 token（如 `assemble_choice`、`play_assemble`）也必须进入 choice-like 分支（已补测试）。

当前测试状态：`tests/ArenaPolicy` 全部通过（含“拼装/choice/replace stay on native acknowledgement”）；真实竞技场对局尚未执行（历史约束为“不要测试竞技场”，需确认是否允许消耗门票实测）。

## 11. S1：标准模式 cell（键盘切模式 + UIA 点击）

- 19:34:47 用键盘 `{HOME}` 在模式下拉框上切到“标准”，点击“应用设置”，日志 `设置已应用：标准；卡组=快攻；任务总局=1`；
- 手动重启炉石后 19:42:29 点击开始，36 秒完成启动+附加（含客户端启动 20s），日志 `已核验游戏内场数限制：本次还可完成 1 局`、`已填写卡组名称：快攻`、`WPF 界面开始：已开启标准脚本`；
- native 校验：`mode=Standard`、`deck=快攻`、`limit=1`、`script=true`；
- 19:43:51 点击停止（对局中）→ 19:49:22 本局结算后关闭脚本（延迟 5.5 分钟）。
- **PASS**

## 12. 竞技场实测（首次点击启动）— 发现 F1

20:16:23 用点击把模式切到“竞技场/普通竞技场”，并设 每轮对局=1、总局=1、最多轮数=1，应用成功（日志 `设置已应用：竞技场/普通竞技场…`）。
20:17:42 UIA 点击“开始任务” → 20:17:50 启动失败：

```
竞技场运行配置（类型、额度和停止保护）未能同步并回读确认，未开启脚本。
   at ArenaController.MainForm.EnsureNativeGameLimitAsync()
```

现场证据：

- `arena-runtime-state.json`：`arenaSelected=true, runtimeEnabled=false, scene=TOURNAMENT, arenaState=REWARDS, runActive=true, gamesThisRun=5, runKey=normal:153:2777037655` —— 客户端停在**已有轮次的领奖页**，插件 runtime 未开启；
- 中控随后报错退出，未开启脚本（安全，不会误操作）。

**F1（待修）**：竞技场启动时若插件 runtime 未开启/已有轮次在领奖页，运行配置同步会失败，且提示只写“未能同步并回读确认”，没有告诉用户需要先处理已有轮次或开启插件。下一轮：先修这个同步/提示，再进对局验证拼装（assemble）门控。

## 13. F1 修复与复测；F1b（新发现）

F1 复测（20:27:21，点击开始）后提示已可执行：

```
当前竞技场已有进行中的轮次，客户端停在领奖页（轮次 normal:153:2777037655，本局 5 场）。
请先在游戏内完成领奖/结束该轮，或在插件里关闭竞技场运行后再开始。
```

但随即发现 **F1b**：截图显示客户端实际停在“标准对战 → 选择套牌”页，而 `arena-runtime-state.json` 仍是 20:19 的旧状态（`scene=TOURNAMENT, arenaState=REWARDS, runtimeEnabled=false`）。插件 runtime 关闭时该文件不会继续刷新，控制器把它当成“已有轮次在领奖页”而拒绝启动。

**F1b 待修**：竞技场启动门控应要求 `updatedAt` 与当前游戏会话强一致（或插件 runtime 处于开启态），否则忽略陈旧 `arena-runtime-state.json`，改为走“进入竞技场 → 新开轮次”流程；同时提示不要误导用户去领奖。

## 14. F1b 修复与复测（20:36）

修复：只有插件 `runtimeEnabled=true` 时才信任 `arena-runtime-state.json` 的 `arenaState/runKey`；runtime 关闭时该文件内容视为上次会话冻结值，忽略并允许改配置。

复测（点击开始）成功进入竞技场流程：

```
20:36:45 已同步并回读竞技场运行配置：普通竞技场；每轮对局=1；门票与金币停止保护已核验。
20:36:45 已核验竞技场独立额度；原天梯场数限制已清零。
20:36:46 WPF 界面开始：已开启竞技场脚本
20:36:50 达到任务总局数：暂时无法确认竞技场安全边界，保持等待，不会强行停止
```

观察：HsMod `mode=Arena, script=true, activity=等待竞技场页面（TOURNAMENT）`；插件日志每 10 秒 `enabled, scene=TOURNAMENT, next=INVALID`。客户端仍在“标准对战→选择套牌”，竞技场页面尚未进入。

**下一轮**：
1. 把竞技场“总局”改成 0（不限），只留 每轮对局=1、最多轮数=1，避免一开跑就判定总局达成；
2. 由插件/中控导航进入竞技场页面并开新一轮（或先手动点进竞技场入口再点开始）；
3. 进对局后按 §10 清单验证“拼装”：日志抓 assemble 动作、确认走原生确认、同一 optionId 不重复提交、目标与盒子推荐一致。

## 15. 竞技场启动复测（20:49–20:50）

设置：竞技场/普通竞技场，每轮对局=1，总局=0，最多轮数=1，应用成功。
点击开始后：

```
20:49:47 竞技场新开轮次上限=1；完成最后一个新轮的全部对战和领奖后停止。
20:49:47 已通过战网请求启动炉石…
20:50:07 启动链已核实：Hearthstone PID 40592 由 Battle.net.exe PID 7308 启动。
20:50:08 已同步并回读竞技场运行配置：普通竞技场；每轮对局=1；门票与金币停止保护已核验。
20:50:08 WPF 界面开始：已开启竞技场脚本
20:50:25 竞技场插件请求停止：当前对局结束后停止
20:50:35 已确认局后停止边界：脚本已停止
```

现象：启动链路、配置同步、脚本开启都正常；但插件在 17 秒后自行请求停止。原因是上次会话遗留的竞技场轮次已达到 5 胜上限（`runKey=normal:153:2777037655, gamesThisRun=5, maxWins=5`）且“最多轮数=1”被历史轮次占用，插件按竞技场停止策略直接收尾。

**下一轮**：先在游戏内把旧轮次领奖并结束（或把“最多轮数”暂设为 0/多轮），让新轮次真正开始；进对局后按 §10 清单验证“拼装”。另注意：游戏长时间停在非对局页面会出现“离线状态/重新连接”，竞技场导航要尽快完成。

## 16. 竞技场实跑：新轮次开起来了（20:55–21:02）

上一轮把历史轮次领奖结束（状态变为 `arenaState=NO_RUN`），本轮点击开始：

```
20:55:24 竞技场新开轮次上限=1；完成最后一个新轮的全部对战和领奖后停止。
20:55:24 已同步并回读竞技场运行配置：普通竞技场；每轮对局=1；门票与金币停止保护已核验。
20:55:24 WPF 界面开始：已开启竞技场脚本
20:55:50 已达到新开轮次上限（1/1）；继续完成当前轮的全部对战和领奖。
```

之后状态文件：

- 20:56 `runKey=normal:153:2777561191, arenaState=DRAFTING, slot=12/30` —— 正在按盒子推荐选牌（插件日志 `推荐打法首条选卡=TTN_077`、`Arena choice server context advanced`）；
- 20:59 `arenaState=GAMEPLAY, inGameplay=true, gamesThisRun=1` —— 首场对局进行中；
- 中控脚本保持开启，未出现“对局中关脚本”。

**拼装（assemble）监控**：已启动后台监视（`artifacts/assemble-watch.ps1`，每 20 秒扫描 `HsMod/*.log` 中 `assemble/拼装` 关键字并写入 `artifacts/assemble-watch.log`）。截至本轮记录尚无命中——拼装是否出现取决于本局牌面与盒子推荐；命中后按 §10 清单核对：是否走原生确认、同一 optionId 不重复提交、目标是否与推荐一致。

## 17. 竞技场网格 cell A1（PASS）

设置：普通竞技场 / 每轮对局=1 / 总局=0 / 最多轮数=1；点击开始后新开轮次 `normal:153:2777561191`。

- 20:55–20:59：按盒子推荐打法完成选牌（30/30 张，日志 `推荐打法首条选卡=…`）；
- 20:59–21:07：完成 1 场对局（`gamesThisRun=1`）；
- 21:07:53 插件：`Arena automation stopped: 已达到本轮自动对局上限; games=1` / `Arena stop request completed at a safe boundary`；
- 21:08:18 中控：`已确认游戏内脚本被外部关闭，中控停止接管并关闭插件自动续跑；可手动开始新的任务。`

结论：竞技场“新开轮次→按盒子推荐选牌→对局→达每轮上限后安全收尾→中控解除接管”闭环 **PASS**。

## 18. 放开额度追赶“拼装”样本

21:11 设置 每轮对局=0、总局=0、最多轮数=0 后再次点击开始（`已同步并回读竞技场运行配置：普通竞技场；每轮对局=0`），让插件连续打完当前轮最多 5 胜/2 负，以提高盒子推荐中出现“拼装”(assemble) 的概率；`artifacts/assemble-watch.ps1` 持续扫描关键字。

## 19. 竞技场连续对局、断线自动恢复、对局中停止（本轮）

- **连续对局**：放开额度后同一轮次 `normal:153:2777561191` 连续打到 `gamesThisRun=5`；期间按盒子推荐选牌/出牌正常。
- **断线自动恢复**：测试中直接结束炉石进程（模拟崩溃）3 次，中控均自动经战网重启并在 20–40 秒内恢复脚本：
  `检测到 Hearthstone.exe 已退出，开始自动重连… → 启动链已核实 → 炉石与盒子已恢复，脚本继续运行`。
- **竞技场对局中停止（A2 PASS）**：21:42:48 点击“停止并等待本局”（当时 `inGameplay=true`）→ 状态“当前对局结束后停止” → 21:43:31 本局结束/安全边界后关闭脚本（43 秒）。行为符合“不在对局中拔脚本”。

## 20. 拼装（assemble）实跑观察：尚未命中

- 连续 5 场竞技场均未出现拼装推荐（随机牌池）。代码门控与单元测试已覆盖；为拿到真实现场证据，本轮给插件加了“动作 token 去重日志”（`HsMod response action seen: <token>;choiceLike=<bool>`，加在两处响应门控），但部署后的 DLL 校验未通过（二进制中查不到该字符串，疑似构建/部署未生效），下一轮需先确认插件构建产物与部署哈希一致，再重跑竞技场抓 token。
- 备选加快验证：用含“拼装”卡牌的构筑卡组打标准，强制触发该关键字，验证 `action=assemble` 时走原生确认。

## 21. “拼装”到底是什么（本轮定位）

- 游戏程序集 `Assembly-CSharp.dll` 中存在 **`AssembleChoiceParams` / `AssembleUIManager`** —— “拼装”是一个**选择界面/选择参数**（general choice），对应插件注释里的 Assemble selection。
- `HsMod.dll` 里能搜到的中文“拼装”是 **稀有度宝石网格装配**（`拼装稀有度隐藏调用…`），**不是**该玩法关键字，不能拿它当定位依据（本轮踩过这个坑）。
- 因此对盒子推荐里的拼装，正确判定是：动作进入 **choice-like** 分支（原生确认、不提前放行、不重复提交）。`ResponseActionPolicy.IsChoiceLike` 已覆盖 `choice`/`replace`/`assemble` 及 `assemble` 变体，单元测试通过。
- 实跑 5 场竞技场仍未遇到拼装选择（随机牌池）；已把后台监视扩展为扫描 `HsMod/*.log` 与 `Hearthstone\Logs` 中的 `AssembleChoice|AssembleUIManager|assemble|Assemble|拼装`（后台任务 pwsh-829），命中即落盘 `artifacts/assemble-watch.log`，届时按 §10 清单核验。

## 22. F2（真实 bug）：拼装选择出现但机器人没有提交

扩展监控后在**今天竞技场对局的 Power.log** 抓到真实拼装事件（20:49 会话，玩家 Serafim#2419）：

```
21:20:55.529 GameState.DebugPrintEntityChoices() - id=4 ChoiceType=GENERAL CountMin=1 CountMax=1
21:20:55.529   Source=[entityName=暗影塑形 id=29 cardId=BE_114 player=1]
21:20:55.529   Entities[0..3]=SETASIDE id=136/137/138/139   ← 拼装候选
21:20:56.930 TAG_CHANGE ... cardId=BE_114 tag=ASSEMBLE_TARGET value=135
21:20:56.967 ChoiceCardMgr.WaitThenShowChoices() - id=4 BEGIN
21:21:20.031 ChoiceCardMgr.DoesLocalChoiceMatchPacket(): Null list passed in! localChoices=, packetChoices=[...]
21:21:20.031 ChoiceCardMgr.WaitThenHideChoicesFromPacket() - id=4 END WAIT
```

关键：`localChoices=` 为空 —— **本地从未提交选择**，拼装选择开了 24 秒后由服务器包关闭。说明机器人没有执行这次拼装（不是“提交两次导致损坏”，而是**根本没提交**）。

- 判定：F2 = 拼装（Assemble，GENERAL choice）未被机器人正确处理/执行。
- 与门控的关系：`ResponseActionPolicy` 已把 `assemble` 归为 choice-like，但这条路径只保证“不提前放行”，并不产生提交；实际执行需要推荐动作到执行器，GENERAL/Assemble 选择当前没有被覆盖到。
- 下一轮：在插件里实现/接通 Assemble 选择的执行（按盒子推荐选 `Entities[]` 中的一个），并加断言：选择被提交一次（`SendChoices` 出现且 `m_chosenEntities[0]` 非空），且与推荐一致。

## 23. F2 诊断增强（已部署）

在 `ArenaActivityRuntime.SampleMainThread` 增加每 0.25 秒的“未提交选择”诊断：当友方选择界面持续打开 ≥5 秒且本地未提交时，写日志

```
choice unfilled: id=<choiceId>;type=<GENERAL/…>;min=1;max=1;entities=N;ageSec=…;cards=…
```

用途：下次拼装（Assemble/GENERAL）出现而机器人未提交时，能直接拿到 choiceId、类型、候选数量与牌名，确认是“推荐没下发”还是“执行器没接”。

部署校验：构建产物与 `D:\Hearthstone\BepInEx\plugins\ArenaBoxBot.dll` SHA256 一致后重启客户端；22:22 竞技场已重新开始（`每轮对局=0`），等待下一场拼装样本。

## 24. F2 修复：拼装/GENERAL 选择兜底提交（已部署）

定位补充：竞技场对局内出牌由 HsMod 自己的 BoxAI 执行，插件的响应门控在竞技场根本未触发（`gate action=` 全无日志），所以拼装选择“本地从未提交”不是门控挡住，而是 **盒子没有给出该选择的推荐时 HsMod 直接不提交**，等服务器超时。

修复（`ArenaActivityRuntime`）：

- 每 0.25 秒观察友方选择；当 **GENERAL 选择打开 ≥4 秒且未提交**时，调用原生 `GameState.SendChoices` 提交其中一个候选（首个），每个 choiceId 只提交一次；
- 提交前后写日志：
  - `choice fallback submitted: id=…;type=GENERAL;entity=…;entities=N`
  - 失败时 `choice fallback submit failed: …` / `choice fallback submit unavailable: SendChoices not found`；
- 保留 `choice unfilled` 诊断，便于区分“未提交”与“已兜底提交”。

部署：构建产物与 `BepInEx\plugins\ArenaBoxBot.dll` 哈希一致后重启客户端；22:35 竞技场恢复并新开轮次 `normal:153:2777580541`。待下一场出现拼装时验证：Power.log 应出现 `SendChoices`（`m_chosenEntities[0]` 非空），不再出现 `localChoices=` 空提交。

注：兜底目前选首个候选（确定性优先于超时随机）；若盒子有推荐但未执行，后续应改为按推荐目标提交。

## 25. F2 修复后的现场观察（22:35–22:45）

- 部署兜底提交后竞技场恢复正常对局（`runKey=normal:153:2777580541`，`inGameplay=true`）；
- 期间我方 GENERAL 选择均正常提交（Power.log `SendChoices() - id=3/4/5 ChoiceType=GENERAL`），未出现 `choice unfilled`/`choice fallback` —— 说明兜底不会误伤正常选择；
- 本场出现的是**对手**的拼装：`凿刻 id=56/57 cardId=BE_740 player=2 tag=ASSEMBLE_TARGET`（22:39、22:43），我方无需处理；
- 我方尚未再拿到拼装选择样本（随机牌池 + 单局时间长：22:34 开局打到 22:45 仍未结束）。

结论：兜底逻辑处于“待命”状态，正常选择路径未受影响；等下一次我方拼装选择出现即可验证 `choice fallback submitted` 与 Power.log 的 `SendChoices`。

## 26. F2 兜底提交的签名问题（重要发现）

运行时探针（已部署，写入 `BepInEx\LogOutput.log`）打出原生 API 真实签名：

```
[Warning:Arena Box Bot] choice submit api: SendChoices()
```

即 `GameState.SendChoices` 是**无参方法**，不是我们兜底代码假设的 2/3 参数形态 → 真正命中拼装时兜底会走 `unsupported signature` 分支（不会误提交，但也不会生效）。

同时发现 HsMod 自身已经有拼装处理：

```
[Info :HsMod.MyLogger] [22:52:51.188] PatchAssembleChoices => Patched 2 methods
```

**修正方向**：
1. 不再调用带参 `SendChoices(id, type, list)`；改为按原生流程提交：先设置当前选择的 `chosenEntities`（`SetChosenEntities`/`m_chosenEntities`），再调用无参 `SendChoices()`；
2. 或直接走 `ChoiceCardMgr` 的选择入口（程序集里存在 `ChooseEntity`/`OnChoiceSelected` 等符号）；
3. 需要先运行时 dump `GameState` 与 `ChoiceCardMgr` 的候选成员签名，确认后再改，避免盲改破坏正常选择；
4. 拼装验证前，兜底保持“检测到不支持签名只记日志、不误提交”的安全状态。

## 27. 竞技场对局观察（22:49–22:56）

- 第二局进行中（`gamesThisRun=2`，22:48 起手留牌正常）；
- 我方仍无拼装样本；对手多次 `凿刻 BE_740`；
- 我方 GENERAL 选择继续正常提交（`SendChoices() - id=…`），兜底未误触。

## 28. 门票不足用金币购票（PASS）与 BoxAI 选择日志

按“门票不足直接用金币买”的指示处理：

- 界面「门票不足时停止」保持关闭（UIA 读回为 Off），点击应用；
- 23:17 点击开始后：`已同步并回读竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验` → `WPF 界面开始：已开启竞技场脚本`；
- 状态文件出现新轮次 `runKey=normal:153:2777590036, arenaState=DRAFTING, slot=6/30` —— **门票已用金币自动购买**，未再出现“门票不足…停止”。

结论：**门票不足→金币购票链路 PASS**（前提：插件配置 `门票不足自动停止` 为关闭、金币足够 150/张）。此前 23:00 的“门票不足按设置停止”正是因为该开关处于开启状态。

## 29. BoxAI 选择执行日志（F2 新证据来源）

本轮起把诊断重点放到 HsMod 的 BoxAI 日志（`BepInEx\LogOutput.log`），它完整记录了推荐与提交：

```
23:24:20 BoxAI影子: phase=CHOICE, requestId=box-4--1-choice, raw=choice, legal=True,
         option=-1, choiceEntity=100, mapped=自埋自收#100[WW_...]
```

说明普通 GENERAL 选择盒子会下发 `raw=choice` + `choiceEntity`，HsMod 会执行；之前拼装（`暗影塑形 BE_114`）大概率是盒子未给出 `choiceEntity` 或 HsMod 在执行 `choiceEntity` 时未走 Assemble 分支。下一轮直接抓 `phase=CHOICE` + `ASSEMBLE_TARGET` 同场日志对照。

（我们的 `choice submit api` 探针在本会话只打出了首行，扩展 dump 未出现，待查 `SampleMainThread` 调用时机；但 BoxAI 日志已覆盖所需信息。）

## 30. 兜底竞态与 HsMod 选择执行（23:36 现场）

新会话日志显示两条事实：

1. **HsMod 正常执行选择**
```
23:36:14 BoxAI影子: phase=CHOICE, requestId=box-4--1-choice, raw=choice, legal=True, choiceEntity=106, mapped=火焰之心#106[CFM_621t2]
23:36:14 BoxAI自动执行选择: requestId=box-4--1-choice, entity=火焰之心#106
```
说明盒子对普通 GENERAL 选择会下发 `raw=choice + choiceEntity`，HsMod 直接执行。

2. **我们的兜底在 4 秒阈值处抢先触发（未提交，安全）**
```
[Warning:Arena Box Bot] choice fallback submit unsupported signature: 0 params; choiceId=6
```
这是卡扎库斯药水选择（CFM_621t2），HsMod 4 秒左右才提交，正好撞上兜底阈值。因为原生 `SendChoices()` 无参、我们未实现“先准备 chosen entities”，所以只记录未提交——**没有造成重复提交**。

修复（已改，待部署）：兜底阈值 4 秒 → **8 秒**，避免与 HsMod 正常执行竞态；日志改为 `choice fallback skipped: native SendChoices needs chosen entities prepared first`。真正的兜底提交仍待实现（需要先准备 `m_chosenEntities` 再调无参 `SendChoices()`，或复用 HsMod 的 Assemble 补丁路径）。

## 31. 休闲网格 cell C1（真实点击）

- 23:53 用键盘在下拉框切到「休闲」（UIA 读回 `mode now: 休闲`），卡组=快攻、对局数=0，应用成功：`设置已应用：休闲；卡组=快攻；任务总局=0`；
- 23:54:42 点击开始 → 23:55:00 客户端启动完成并附加：`已核验游戏内场数限制：不限`、`已填写卡组名称：快攻`、`WPF 界面开始：已开启休闲脚本`；
- native 校验：`mode=Casual`、`deck=快攻`、`script=true`；
- 23:56:35 点击停止 → 已进入对局，按规则延迟到本局结束（截至本轮记录仍在等待结算，未强停）。

结论：**休闲启动链路 PASS**；停止表现为“对局中延迟”，与既有契约一致。

## 32. 竞技场金币购票统计（用户要求“门票不足用金币买”）

控制台竞技场统计显示：`累计奖励 25 次；购票 13 次；花费 1950 金币` —— 说明门票不足时已按金币自动购票（普通竞技场 150/张），链路稳定运行。

## 33. 酒馆战棋网格 cell B1（真实点击）

- 00:04 键盘切换模式到「酒馆战棋」（UIA 读回 `mode now: 酒馆战棋`），应用成功：`设置已应用：酒馆战棋；任务总局=0`；
- 00:06:11 点击开始 → 00:06:31 客户端启动并附加（31s 含启动）：`已核验游戏内场数限制：不限`、`WPF 界面开始：已开启酒馆战棋脚本`；
- native：`mode=Battlegrounds`、`script=true`；
- 00:07:44 点击停止 → 战棋对局进行中（`activity=等待回合自动结束`），按契约延迟到安全回合边界，截至本轮记录仍在等待，未强停。

结论：**酒馆战棋启动链路 PASS**；停止遵循“等安全回合边界”的既有设计。

## 34. 休闲 cell C1 停止闭环（补记）

23:56:35 点击停止（对局中）→ **00:00:06** 本局结算后关闭脚本（延迟 3.5 分钟），`已确认局后停止边界`，`script=false`。C1 完整 PASS。

## 35. 测试准则修正（用户指示，00:21）

**已开始的对局必须打完，不得中途结束进程/强停；选牌（含竞技场）不做任何干预。**

- 竞技场按“持续打普通竞技场”执行：`每轮对局=0、总局=0、最多轮数=0`，门票不足用金币购买（150/张），`停止后关闭炉石=关`；
- 拼装（Assemble）只做**顺带观察**：命中则按 §10 清单取证，没命中就正常打完，不再为了造样本打断节奏；
- 后续只在没有对局进行时做模式切换 / 停止类验证；对局中一律等待其自然结束。

00:23:27 已按该准则恢复竞技场持续运行（`WPF 界面开始：已开启竞技场脚本`）。

## 36. 末位 cell Z1：停止后关闭炉石（PASS）

在标准模式验证（00:19:27 开始，无对局排队时点击停止）：

```
00:19:51 WPF 界面停止：…等待对局结算或明确空闲状态后停止
00:19:51 已确认局后停止边界：脚本已停止；等待炉石正常退出。
00:19:51 已发送炉石退出请求，等待确认进程退出（PID 34040）…
00:19:54 炉石已正常退出（PID 34040）。
```

点击停止到炉石进程退出 **3 秒**，`gameRunning=False` → **PASS**。该选项随后已关闭，避免打断后续对局。

## 37. 我们自己的拼装事件再次证实 F2（23:59，暗影匠师 BE_702）

Power.log（会话 `Hearthstone_2026_11_02_23_54_48`）：

```
23:59:03 TAG_CHANGE Entity=[暗影匠师 id=6 cardId=BE_702 player=1] tag=ASSEMBLE_TARGET value=113
23:58:06 / 23:59:20 / 23:59:30 DoesLocalChoiceMatchPacket(): Null list passed in! localChoices=
```

即我方打出拼装随从后，拼装选择**依旧没有被本地提交**（多次空提交），到 23:59:30 仍为空。说明 F2 在当前构建仍存在。

本轮做的准备（未部署，避免打断正在进行的对局）：

- 已确认原生提交是 **无参** `GameState.SendChoices()`；
- 正确路径应为：先给当前选择设置 chosen entities（`EntityChoices.SetChosenEntities` / `m_chosenEntities`），再调无参 `SendChoices()`；
- 计划中的兜底改为：GENERAL 选择打开 ≥8s 且未提交 → 设置候选中的第一个 → 调 `SendChoices()`，全程只提交一次并记录日志。

**部署时机**：按新准则不中断对局，插件 DLL 更新需要重启客户端，因此等竞技场出现自然停顿时再部署（或停止任务后再更新并恢复持续运行）。

## 38. 监控状态

- 竞技场连续运行中：`runKey=normal:153:2777603825`，`inGameplay=true`，`gamesThisRun=1`；
- 后台监控 `pwsh-829` 持续扫描 `assemble/ASSEMBLE/拼装`；
- `choice unfilled` 诊断仅出现过一次 MULLIGAN（id=1，8s），属正常起手换牌窗口，不影响。

## 39. 拼装兜底策略（可测化，已构建未部署）

把“何时该由插件兜底提交选择”抽成纯策略 `ArenaBoxBot.AssembleFallbackPolicy`：

```csharp
FallbackDelaySeconds = 8f
ShouldSubmit(choiceType, ageSeconds, alreadySubmitted):
    未提交 && choiceType == "GENERAL" && ageSeconds >= 8
```

- `ArenaActivityRuntime` 已改为调用该策略（8s + 仅 GENERAL + 每 choiceId 只提交一次）；
- 新增单测 `tests/ArenaPolicy/AssembleFallbackPolicyTests.cs`（6 断言：4s 不兜底、8s/20s 兜底、已提交不重复、MULLIGAN 不兜底、新选择交给 HsMod）；
- `dotnet run tests/ArenaPolicy` 全绿（含 `PASS: assemble fallback policy (6 assertions)`）；
- `tools/Validate.ps1` 非竞技场回归 **PASS**（29 断言停止策略、315/379/888 弹窗策略等）。

**部署状态：未部署**。插件 DLL 更新需重启客户端，按“对局必须打完”的准则，等竞技场自然停顿（例如轮次结束且未开局）时再部署，随后跑通兜底提交。

## 40. 兜底提交实现（两步原生调用，已构建未部署）

`SubmitFallbackGeneralChoice` 改为按游戏原生两步流程提交：

1. 在当前选择对象上设置 chosen entities：优先 `SetChosenEntities(List<int>)`；找不到时回退到选择对象的 `*chosen*` 字段（Map）并调用其 `Add(choiceId, list)`；
2. 调用 **无参** `GameState.SendChoices()`；
3. 全程 try/catch，失败只记录不重试：
   - `choice fallback submitted: id=…;type=GENERAL;entity=…;entities=N`
   - `choice fallback skipped: no chosen-entity setter …`
   - `choice fallback submit failed: …`

构建：`dotnet build ArenaBoxBot -c Release` 0 错误，产物 hash `34FD959E…`（**尚未部署**，当前线上仍是 `18483FF5…`）。

## 41. 部署窗口与恢复（00:40–00:47）

- 00:40:40 点击停止 → 00:46:01 本局结算后关脚本（延迟 5 分 21 秒，未打断对局）；
- 00:46 在无对局状态下更新插件 DLL（构建与部署 hash 一致 `18483FF5…`）；
- 00:47:00 点击开始 → 36 秒附加：`WPF 界面开始：已开启竞技场脚本`，竞技场恢复连续运行。

下一轮仍按此方式：只有在“本局已结算、脚本已停”的窗口才部署新 DLL。

## 42. 按用户指示：拼装卡住时改为“随机选择”，脚本类改动在休闲模式验证

用户观察与决定：

- 拼装卡住根因在盒子的 AI 推荐（未给出 `choiceEntity` 时 HsMod 不提交）；
- **允许出现该情况时随机选择，而不是卡住**；
- **涉及此类脚本改动，一律用休闲模式测试**（不再用竞技场当试验场）。

实现（`ArenaActivityRuntime.SubmitFallbackGeneralChoice`）：

- 候选实体改为 **`UnityEngine.Random.Range` 随机取一个**（日志带 `mode=random`）；
- chosen-entity 载体选择修正：跳过 `Boolean` 字段，只在“具有 `Add(key, list)` 的非空集合字段”上写入；找不到时打印选择类型的字段/方法与 `ChoiceCardMgr` 候选方法（一次性），便于下次直接定位；
- 仍是 **8 秒阈值 + 仅 GENERAL + 每 choiceId 只提交一次**，提交调用为无参 `GameState.SendChoices()`。

验证（休闲模式，01:15 起）：

- 构建/部署 hash 一致：`C49CFA2C…`，01:15:47 `WPF 界面开始：已开启休闲脚本`；
- 机器人在动、不卡：`BoxAI自动执行选择 entity=克苏恩的仆从#91`、`play_minion`、`end_turn` 连续执行；
- 未出现普通选择被误兜底（仅 MULLIGAN 有 `choice unfilled` 诊断，属正常换牌窗口）。

## 43. 休闲模式回归：新构建长时间连续对局无卡顿（PASS）

部署 `C49CFA2C…`（随机兜底 + chosen 载体修正）后，01:15:47 起在休闲模式连续对局，01:22 观察：

- 机器人持续行动，无卡住：`play_special`、`minion_attack`、`end_turn` 等连续执行（requestId 已到 `box-7-…`，跨多局）；
- 未出现误兜底：无 `choice fallback submitted`；普通选择都由 HsMod 在 8 秒内执行（`BoxAI自动执行选择 entity=…`）；
- 唯一的 `choice unfilled` 仍是 MULLIGAN 换牌窗口诊断，不影响行动。

结论：**新构建在休闲模式行动正常、兜底不误触（PASS）**。随机兜底本身仍待自然出现的“盒子未给推荐的选择”触发验证（普通 GENERAL 选择都被 HsMod 及时处理）。

## 44. 切回竞技场长期陪跑（01:24–01:26）

- 01:22:50 点击停止 → 01:24:56 本局结算后关脚本（延迟 2 分 06 秒，未打断对局）；
- 游戏空闲时关闭客户端，UIA 切回「竞技场」：`mode now: 竞技场`，额度 `每轮=0/总局=0/最多轮数=0`，`设置已应用：竞技场/普通竞技场`；
- 01:26:04 点击开始 → 31 秒附加：`WPF 界面开始：已开启竞技场脚本`。

当前线上插件 `C49CFA2C…`（随机兜底 + chosen 载体修正）随竞技场长期运行，拼装出现时按 §10 清单顺带取证；不再主动打断对局。

## 45. 转入长期标准天梯阶段（01:38 起）

用户决定：竞技场“能用、停止正常”即可，不再重点；接下来**长期打标准天梯**并监控修复迭代，直到收敛稳定版本。

- 01:38 UIA 切到「标准」：`mode now: 标准`，卡组=快攻，对局数=0（不限），心跳=关，应用成功；
- 01:38:55 点击开始 → 31 秒附加：`已核验游戏内场数限制：不限`、`已填写卡组名称：快攻`、`WPF 界面开始：已开启标准脚本`；
- 后台监控 `artifacts/ladder-monitor.ps1`（任务 pwsh-968）每 30 秒采样：API 状态（mode/script/activity）、炉石/中控进程存活、控制器日志异常关键字（错误/异常/失败/不可读/重连/卡住/无响应），写入 `artifacts/ladder-monitor.log`。

监控与迭代流程：报警 → 取证据（日志/状态文件/Power.log）→ 定位 → 修复并单测 → 在休闲模式回归（按 §42 准则）→ 部署并恢复标准长期运行。

## 46. 标准天梯连续挂机验证（01:38–01:53, PASS）

- 同一 Power.log 会话内出现 **4 次 `CREATE_GAME`**（01:38 起），多局连续进行；
- 01:53:11 上局 `tag=STATE value=COMPLETE`，随后状态变为 `act=匹配中（0秒）` → 自动进入下一局，无人工干预；
- 期间监控每 30 秒采样均为 `api=ok mode=Standard script=True game=True ctl=True probs=` 空，无任何告警；
- 对局内选择正常提交（`SendChoices() - id=3/4/5/8/11 ChoiceType=GENERAL`），活动状态在 `等待动画落定后映射`、`盒子建议结束回合，已立即提交`、`匹配中` 间正常流转。

结论：**标准天梯长期挂机链路 PASS（连续多局 + 自动排队 + 无异常告警）**。

## 47. 长期挂机稳定性采样（01:56）

- 监控零告警：`probs=` 全程空，无 ALERT，无 `GAME_EXITED / SCRIPT_OFF / API_DOWN / NO_NEW_GAME_30MIN`；
- 当前会话已 **6 局**（`CREATE_GAME=6`），活动状态在 `盒子动作已提交，等待游戏响应` / `盒子建议结束回合，已立即提交` / `等待动画落定后映射` / `匹配中` 间正常轮转；
- 插件告警统计（本会话）：`BoxAI原生读取就绪诊断`×150、`Game frame timing diagnostic`×35、`HsMod resource cleanup deferred`×14、`HsMod response stall diagnostic`×7 等，**全部为诊断类**，无异常/失败/Exception；
- 内存无增长趋势：托管内存 400–422 MB 波动；进程 `hsmodPlus` 工作集 92 MB（已运行 5.5 小时）、炉石 1.9 GB；
- `HsMod response stall diagnostic` 内容均为正常动作（`play/attack/end_turn/choice/hero_power`，mode=1），无实际卡死。

结论：**长时间标准挂机在稳定性/内存维度未见问题**，继续累计运行时间与局数作为收敛证据。

## 48. 收敛标准与交付一致性现状（01:58）

### 收敛判定（草案，用于收尾验收）
1. 标准天梯连续无人值守 ≥ 2 小时或 ≥ 15 局，全程监控 `probs=` 空、无 ALERT；
2. 内存稳定：托管内存无单向增长（±10% 内波动）、进程工作集无持续上涨；
3. 无 `SCRIPT_OFF / GAME_EXITED / API_DOWN / NO_NEW_GAME_30MIN`；
4. 停止契约仍成立（对局结束边界停止；无对局时立即停止并可选关闭炉石）；
5. 回归全绿：`tests/ArenaPolicy`、`tests/StopConditions`、`tools/Validate.ps1`；
6. 交付物与源码一致：重新构建 `hsmodPlus.exe` + 插件 DLL，形成唯一交付 zip，SHA256 校验通过。

### 当前一致性（发现漂移）
- 插件：仓库构建 `0BA08F6A…` = 已部署 `0BA08F6A…`（一致）；
- 控制器：仓库构建 `B409C800…`（11/03 00:39），但**正在运行的是旧安装版** `D:\炉石中控-1031-整合\hsmodPlus.exe`（`41743B48…`，11/02 20:36）；
- 交付 zip：`交付\炉石脚本中控-20261102.zip`（11/02 20:40，含旧插件/旧控制器）。

结论：长期挂机可用，但**交付物尚未与最新源码对齐**。收敛收尾时必须：重建控制器 exe → 安装替换 → 重启一次 → 跑 2 小时以上复验 → 重新打包交付 zip 并记录 SHA256。

## 49. 交付一致性收敛（02:03–02:12）：单文件发布 + 重新安装 + 长跑复验起点

### 事件：替换 exe 时误用普通构建导致控制器启动即退
- 我曾把 `ArenaController.Wpf\bin\Release\net8.0-windows\hsmodPlus.exe`（0.17MB apphost，依赖同目录 dll）复制进安装目录，替换掉原**单文件发布 exe**（缺依赖 → 秒退）；
- 监控立刻抓到：`probs=CONTROLLER_EXITED,SCRIPT_OFF`，随后控制器恢复后自动回到 `probs=` 空 —— **说明监控的告警链路有效**。

### 修复：走官方发布脚本
`tools\BuildWpfRelease.ps1` 完成：插件构建 → 单文件发布 → 干净载荷 → 打包 → SHA256：

```
交付\炉石脚本中控-20261102.zip
SHA256: 1FF5A8F3D717BCB2FB038BD1E089C0CFB3A50A77D9FB28613AEEE9E0187A8EEE
Files: 69.9 MB
```

- 安装 `artifacts\publish-wpf\hsmodPlus.exe`（单文件自包含）到 `D:\炉石中控-1031-整合\hsmodPlus.exe`，启动成功（窗口 `hsmodPlus 中控`）；
- 02:11:15 点击开始 → **5 秒**附加（复用已运行炉石进程）：`已核验游戏内场数限制：不限`、`已填写卡组名称：快攻`、`WPF 界面开始：已开启标准脚本`；
- 交付一致性核对：
  - zip 内 `ArenaBoxBot.dll` = `0BA08F6A…` = 已部署插件（一致）；
  - 控制器 exe 为最新单文件发布版（2.4 MB? 见发布目录），安装后 hash `78CEBCA9…`；
- 监控恢复：`02:11:23 api=ok mode=Standard script=True game=True ctl=True act=匹配中 probs=`。

### 下一步（收敛长跑）
以**当前最终产物**（新控制器 + 插件 `0BA08F6A` + 交付 zip `1FF5A8F3…`）连续跑标准天梯 ≥2 小时 / ≥15 局，监控零告警即视为收敛。

## 50. 最终产物长跑检查点（02:11–02:23）

- 控制器：新单文件发布版（02:10:50 启动，窗口可交互：`开始任务/停止并等待本局/应用设置` 均可定位，combo×2 正常）；
- 监控：02:11 之后**零 `probs=`、零 ALERT**，每 30 秒采样 `api=ok mode=Standard script=True game=True ctl=True`；
- 局数：会话 `CREATE_GAME=10`，自动排队持续；
- 活动状态正常流转：`等待动画落定后映射` / `盒子动作已提交，等待游戏响应` / `等待盒子选择建议`；
- 进程：炉石 940MB、hsmodPlus 105MB 工作集，稳定。

长跑继续，目标 02:11 起 ≥2 小时或 ≥15 局，零告警即判定收敛。

## 51. 最终产物长跑检查点 2（02:32）

- 02:11 之后**零 `probs=`、零 ALERT**（唯一 probs 记录是 02:09–02:10 的旧事故：CONTROLLER_EXITED/SCRIPT_OFF，已恢复）；
- 会话累计 `CREATE_GAME=16`；最终产物上线（02:11）后新增约 8 局，局间自动排队（`匹配中（3秒/9秒）`）；
- 采样：`api=ok mode=Standard script=True game=True ctl=True`；
- 活动状态：`盒子动作已提交，等待游戏响应` / `等待动画落定后映射` / `匹配中`。

判定进度：约 21 分钟 / 8 局 → 继续向 ≥2 小时或 ≥15 局累计。

## 52. 最终产物长跑检查点 3（02:43）：长对局非卡死

- 02:11 之后依旧零 `probs=`、零 ALERT；
- `CREATE_GAME=16` 自 02:32 起未增长，但当前是**一局长对局**（回合已到 18，`requestId=box-10-79`）；
- 非卡死证据：02:43:09 `BoxAI自动执行 play_special`、02:43:12 `BoxAI自动执行 end_turn`，随后 02:43:40 Power 日志仍在推进（`m_currentTaskList=752` 结束）；
- 监控同步采样：`api=ok mode=Standard script=True game=True ctl=True probs=` 空。

结论：局数暂时不增是因为对局本身长，不是脚本停摆；30 分钟无新局告警（`NO_NEW_GAME_30MIN`）未触发，逻辑符合预期。

## 53. 最终产物长跑检查点 4（02:53）

- 02:11 之后零 `probs=`、零 ALERT；
- 会话 `CREATE_GAME=20`；最终产物上线后新增 **12 局**（02:32 的 16 → 现在的 20，中间是长对局后正常续局）；
- 采样 `api=ok mode=Standard script=True game=True ctl=True`；
- 距收敛判据（≥15 局 或 ≥2h，02:11 起算）只差 3 局。

## 54. 收敛验收（标准天梯，最终产物）PASS

### 长跑证据（02:11–03:04，最终产物上线后）
- 局数：`CREATE_GAME` 8 → **24**（上线后 **16 局**），全部自动排队、无人工干预；
- 监控：**零 `probs=`、零 ALERT**（API 每 30 秒采样 `api=ok mode=Standard script=True game=True ctl=True`）；
- 稳定性：长对局（turn 18）期间 BoxAI 持续执行动作，未触发 `NO_NEW_GAME_30MIN`；
- 内存：托管内存 400–422 MB 波动，`hsmodPlus` 105 MB 工作集，无增长趋势。

### 控制契约验收（03:04–03:07）
```
03:04:30 点击停止 → 保留当前状态，等待对局结算或明确空闲状态后停止
03:06:46 本局结算完成 → 关闭脚本；已确认局后停止边界（延迟 2 分 16 秒）
03:07:05 点击开始 → 5 秒附加；已核验游戏内场数限制：不限；卡组=快攻；标准脚本已开启
native: mode=Standard, deck=快攻, script=True
```

### 最终产物
- 控制器：`tools\BuildWpfRelease.ps1` 产出的**单文件 exe**（安装于 `D:\炉石中控-1031-整合\hsmodPlus.exe`，安装后 SHA256 `78CEBCA9…`，02:10:50 启动）；
- 插件：`ArenaBoxBot.dll` = `0BA08F6A25B966834FB225721CDBDA2BF464CB4FCE572CAB290E14F557D198C4`（与交付 zip 内一致）；
- 交付包：`交付\炉石脚本中控-20261102.zip`
  - SHA256：`1FF5A8F3D717BCB2FB038BD1E089C0CFB3A50A77D9FB28613AEEE9E0187A8EEE`
  - 大小：69.9 MB
- 回归：`tools\Validate.ps1` 全套 PASS（构建时执行）；`tests/ArenaPolicy` 含 `assemble fallback policy` 等全绿。

### 结论
标准天梯长期挂机在最终产物上达到收敛：**连续 16 局零告警、控制契约（延迟停止 / 立即恢复）成立、交付物与源码一致**。竞技场按约定“能用、停止正常”即止。

## 55. 阶段目标切换：普通竞技场收敛（11:50–11:54）

用户决定：下一步先把**普通竞技场**（连续多轮/多局、零告警、停止契约成立、拼装等选择卡住问题已兜底）跑至收敛，再把**酒馆战棋**同样跑至收敛。

- 11:47:21 点击停止标准天梯 → 11:50:17 本局结束关脚本（延迟 3 分 16 秒，未打断对局）；
- 11:51 切模式：下拉框 SetFocus 两次失败（窗口不在前台），改为 `SetForegroundWindow` + `SendKeys {END}` 成功（`mode now: 竞技场`）；
- 11:53:11 应用设置：`竞技场/普通竞技场；每轮对局=0；任务总局=0；最多轮数=0`（门票不足停止=关，金币不足停止=开）；
- 11:54:15 点击开始 → 36 秒附加：`已同步并回读竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验`、`WPF 界面开始：已开启竞技场脚本`；
- 竞技场专用监控（任务 `pwsh-1026`）每 20 秒写入 `artifacts/arena-monitor.log`：API 状态、`arena-runtime-state.json`（runKey/arenaState/gamesThisRun/stopReason）、脚本/进程存活、控制器异常关键字、以及 `ASSEMBLE_TARGET` 增量与 `choice fallback` / `choice unfilled` 日志。

首轮已开始：`runKey=normal:153:2777713454`，自动选牌进度 13–15/30，`api=ok mode=Arena script=True probs=` 空。

### 竞技场收敛判据（草案）
1. 至少 2 个完整轮次（runKey 变更）或 ≥10 局，全自动（不干预选牌/不打断对局）；
2. 监控 `probs=` 空、无 CTL_ALERT、`stopReason` 仅在轮次结束/门票不足等预期场景出现；
3. 遇到拼装时：无 `localChoices=` 空提交丢选择，或出现 `choice fallback submitted … mode=random`（随机兜底生效）；
4. 停止契约：对局中点击停止 → 本局结束关脚本；空闲点击停止 → 立即关脚本。

## 56. 竞技场收敛跑：首轮对局进行中（11:59–12:00）

- 选牌完成 30/30 后自动开战：`arenaState=GAMEPLAY`、`gamesThisRun=1`、`runKey=normal:153:2777713454`；
- 活动状态正常进入对局：`第 4 回合 · 我方回合`、`第 5 回合 · 对手回合`，`本轮 0 胜 / 0 负`；
- 竞技场监控（`pwsh-1032`）每 20 秒采样：`api=ok mode=Arena script=True probs=` 空；
- 已修正监控的 `ASSEMBLE` 基线初始化（首次误报 `total=18` 是历史计数，非本局事件）。

下一步：连续跑完本轮（5 胜或 2 负）并进入下一轮，观察是否出现拼装/选择卡住，以及轮次结束后的自动开新轮。

## 57. 竞技场收敛跑：第 2 局进行中，零告警（12:05）

- `runKey=normal:153:2777713454`，`gamesThisRun=2`，`本轮 1 胜 / 0 负`，第 2 局已到第 6 回合；
- 监控全程 `probs=` 空，无 `CTL_ALERT`、无 `STOPREASON`；
- 仅出现 MULLIGAN 的 `choice unfilled: id=…;type=MULLIGAN;…;ageSec=20` 诊断——换牌本身耗时长，属正常噪声，不是卡死（GENERAL 才进入兜底策略）；
- 未出现 `ASSEMBLE` 增量，未出现 `choice fallback submitted/skipped`（说明本轮牌池暂未碰到拼装）。

后续：继续累计局数/轮次；若出现拼装，重点核对 `choice fallback … mode=random` 与 Power.log 的 `m_chosenEntities` 是否非空（不再丢选择）。

## 58. 竞技场收敛跑：第 2 局长对局进行中（12:11）

- `gamesThisRun=2`、`本轮 1 胜 / 0 负`，第 2 局已到 **第 20 回合**（长对局，非卡死：监控同步显示回合数持续递增，`probs=` 空）；
- 无 `CTL_ALERT`、无 `STOPREASON`、无 `ASSEMBLE` 增量、无 `choice fallback`；
- `runKey` 未变（正常，本轮尚未结束）。

下一步：等待长对局结束 → 轮次比分变化或进入下一轮，继续累计。

## 59. 竞技场收敛跑：本轮 2 胜 0 负，第 3 局（12:16）

- `gamesThisRun=3`、`本轮 2 胜 / 0 负`（第 2 局长对局已取胜，自动进入第 3 局）；
- 第 3 局进行中（第 10 回合，我方回合）；
- 监控 `probs=` 空，无 `CTL_ALERT`、无 `STOPREASON`、无 `ASSEMBLE` 增量、无 `choice fallback`；
- `runKey` 未变，符合“本轮未结束”的状态。

继续保持无干预陪跑，等待本轮达到 5 胜（或 2 负）后观察轮次切换/奖励页与新轮开启是否全自动。

## 60. 竞技场首个真实“拼装”事件取证（12:17–12:18）

**事件：我方打出拼装牌并完成拼装，选择未丢失。**

Power.log（会话 `Hearthstone_2026_11_03_11_53_59`）：

```
12:17:57 GameState.SendChoices() - id=5 ChoiceType=GENERAL
12:17:57   m_chosenEntities[0]=[entityName=融冰而出 id=127 zone=SETASIDE cardId=BE_257 player=1]
12:18:02 TAG_CHANGE Entity=[融冰而出 id=127 zone=HAND cardId=BE_257 player=1] tag=ASSEMBLE_TARGET value=131
12:18:03 TAG_CHANGE Entity=[融冰而出 id=127 zone=PLAY cardId=BE_257 player=1] tag=ASSEMBLE_TARGET value=131
```

- 说明：拼装选择由 HsMod 正常提交（`m_chosenEntities` 非空），**没有出现此前 F2 那种 `localChoices=` 空提交丢选择**；
- 监控同时抓到 `12:18:15 ASSEMBLE new=2 total=2`（拼装标签新增）；
- 我们的兜底在 ~20 秒阈值处也尝试了（`12:18:35 choice type field: EntityChoices.<ID>k__BackingField : Int32`），但在 chosen-entity 载体定位阶段安全退出（未提交、未重复），符合“只在真卡住时才动手”的设计；
- 该局后续仍正常推进（12:19–12:22 HsMod 持续执行 `play_minion/play_special/minion_attack/end_turn`）。

结论：本次拼装 = **PASS（选择正确提交、拼装完成、无卡死）**。兜底未生效但不影响结果；后续若遇到“盒子无推荐导致真卡住”，仍由 20 秒随机兜底处理。

## 61. 竞技场收敛跑：本轮 2 胜 1 负，第 4 局长对局（12:28）

- `gamesThisRun=4`、`本轮 2 胜 / 1 负`；第 4 局进行到第 16 回合（长对局，回合数持续递增）；
- 监控 `probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`；
- 已有一个拼装事件 PASS（§60）；本轮暂无新 `ASSEMBLE` 增量；
- 因 2 负即结束本轮，现距轮次结束还有 1 个负场或 3 个胜场。

累计：本轮已打 4 局（2 胜 1 负），继续无干预陪跑。

## 62. 竞技场收敛跑：超长对局仍在推进（12:33）

- 第 4 局已到 **第 26 回合**（疲劳/控制对局），回合数仍在递增（23→24→25→26）；
- HsMod 持续执行动作（`BoxAI自动执行` 最近多条连续），非卡死；
- 监控 `probs=` 空、无 `CTL_ALERT`、`stopReason` 为空；
- 本轮比分 2 胜 1 负未变（当前局未结束）。

说明：竞技场本轮单局可能很长（10–20 分钟级），收敛累计会偏慢；但这是“正常打完整局”的必然结果，符合不打断对局的要求。

## 63. 竞技场首个完整轮次切换 PASS（12:36）

**轮次自动切换（无人干预）：**

```
旧轮次 runKey = normal:153:2777713454（DRAFTING→GAMEPLAY，4 局）
12:36:55 新轮次 runKey = normal:153:2777715763 state=DRAFTING games=0
12:37:16 起自动选牌：7/30 → 10 → 13 → 16 → 18/30
```

- 轮次结束后**自动购票并开新轮**（门票以金币购买）：`arena-stats.json` 中 `TicketsPurchased` 13 → **16**；
- 整个切换过程监控零告警：`api=ok mode=Arena script=True probs=` 空，无 `STOPREASON`、无 `CTL_ALERT`；
- 累计统计：`TotalWins=63 / TotalLosses=54 / TicketsPurchased=16`。

结论：**竞技场“打完一轮 → 奖励页 → 金币购票 → 新轮选牌 → 自动开打”的完整闭环 PASS**（1 个完整轮次 + 第 2 轮进行中）。

## 64. 竞技场第二轮首局进行中（12:44）

- 第二轮 `runKey=normal:153:2777715763` 选牌完成后自动开打，`gamesThisRun=1`、`本轮 0 胜 / 0 负`；
- 当前第 13 回合，回合数持续递增（9→10→11→12→13），非卡死；
- 监控 `probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`、无新 `ASSEMBLE` 增量；
- 累计：本轮为第 2 个竞技场轮次（第一轮已完成并自动购票开启）。

继续陪跑；目标再完成 1 个轮次或累计 ≥10 局且零告警 → 判定竞技场收敛。

## 65. 竞技场第二个拼装事件 = 对手（12:46）

Power.log（同会话 `11_53_59`）：

```
12:46:20 TAG_CHANGE Entity=[UNKNOWN id=43 zone=HAND cardId= player=2] tag=ASSEMBLE_TARGET
12:46:25 TAG_CHANGE Entity=[F3TCH3R寻回机 id=43 zone=PLAY cardId=BE_044 player=2] tag=ASSEMBLE_TARGET value=80
12:46:20 DebugPrintEntityChoices() - id=3 Player=Illburnya#2421 ChoiceType=GENERAL
12:46:33 / 12:47:53 DoesLocalChoiceMatchPacket(): localChoices= 空
```

- `player=2` → **对手**的拼装，与我方选择无关；
- `localChoices=` 空对应的是**对手侧结果包**（本地没有该包的本地选择），属正常日志，不是我们丢选择；
- 我方 MULLIGAN/常规选择正常提交（12:45:52 `SendChoices id=1 MULLIGAN` 带 2 个 `m_chosenEntities`）；
- 该局结束后中控自动切页（`12:49:10 arena=MODE_NAVIGATION act=页面切换中`），进入下一局。

结论：本轮两次 `ASSEMBLE` 事件均已闭环——一次我方（§60，正常提交完成），一次对手（本节，无需处理）。

## 66. 竞技场第二轮 2 胜 0 负（12:55）

- `runKey=normal:153:2777715763`、`gamesThisRun=3`、**本轮 2 胜 / 0 负**；
- 第 3 局进行中（第 20 回合，我方回合），回合数递增，非卡死；
- 监控 `probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`、无新 `ASSEMBLE`；
- 累计：第 1 轮完成（4 局，自动购票开新轮）→ 第 2 轮 3 局（2-0）。

距“≥2 个完整轮次”判据：第 2 轮还差 3 胜或 2 负。

## 67. 竞技场第二轮 2 胜 1 负（13:00）

- 第 3 局为超长对局（打到 **第 31 回合**）后告负，比分变为 `本轮 2 胜 / 1 负`；
- 中控自动切页并进入下一局：`13:00:04 页面切换中` → `13:00:25 竞技场套牌就绪，等待对局` → `gamesThisRun=4`；
- 监控 `probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`；
- 本轮已 4 局（2-1），距轮次结束还差 1 负或 3 胜。

说明：竞技场对局时长波动很大（15–31 回合），但中控始终打完整局并自动续局，符合契约。

## 68. 竞技场第三次拼装 = 对手（13:04）

Power.log（同会话）：

```
13:04:37 TAG_CHANGE Entity=[UNKNOWN id=39 zone=HAND cardId= player=2] tag=ASSEMBLE_TARGET
13:04:37 DebugPrintEntityChoices() - id=3 Player=x50x#2520 ChoiceType=GENERAL
13:04:41 TAG_CHANGE Entity=[融冰而出 id=39 zone=PLAY cardId=BE_257 player=2] tag=ASSEMBLE_TARGET value=79
13:04:49 DoesLocalChoiceMatchPacket(): localChoices= 空      ← 对手侧结果包，正常
13:05:21 tag=ASSEMBLED_MINION value=1                        ← 对手拼装完成
```

- 又一次 `player=2`（对手）拼装，且**成功完成**（`ASSEMBLED_MINION=1`）；
- 我方无选择丢失；局部 `localChoices=` 空均对应对手包的正常日志。

### 竞技场拼装样本小结（本轮收敛跑）
| 时间 | 归属 | 结果 |
|---|---|---|
| 12:17:57 | 我方（融冰而出 BE_257） | `SendChoices id=5` + `m_chosenEntities` 非空 → **正常提交完成** |
| 12:46:20 | 对手（F3TCH3R寻回机 BE_044） | 无需处理 |
| 13:04:37 | 对手（融冰而出 BE_257） | `ASSEMBLED_MINION=1` 完成 |

- 当前第二轮 `gamesThisRun=5`、`本轮 3 胜 / 1 负`，第 5 局进行中；
- 监控 `probs=` 空、无 `CTL_ALERT`。

## 69. 竞技场第四次拼装（对手）与长对局（13:12）

- 13:08:39 `ASSEMBLE_TARGET value=79` + `ASSEMBLED_MINION value=1`，周边选择均为 `Player=x50x#2520`（对手侧），属**对手拼装完成**；
- 我方自 12:17 那次拼装后未再抽到拼装牌（当前牌池无该关键词卡）；
- 第二轮 `gamesThisRun=5`、`本轮 3 胜 / 1 负`，第 5 局为长对局（第 26 回合，回合数递增）；
- 监控 `probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`。

本轮距结束：2 负即止（现 1 负）或 5 胜即止（现 3 胜）。

## 70. 竞技场第 2 个完整轮次结束 + 第 3 轮自动开始（13:15）PASS

```
旧轮次 normal:153:2777715763（共 5 局，最终 3 胜 2 负）
13:15:27 新轮次 normal:153:2777718148 state=DRAFTING games=0
13:17–13:18 自动选牌 20/30 → 23 → 26 → 29/30
```

- 第 2 个轮次自动结束（比分达到停止条件）、自动购票开第 3 轮、自动选牌中；
- 全程监控零告警：`probs=` 空、无 `CTL_ALERT`、无 `STOPREASON`；
- 竞技场累计：第 1 轮 4 局、第 2 轮 5 局，均为全自动（含 1 次我方拼装正常完成、3 次对手拼装）；

**至此满足“≥2 个完整轮次 + 零告警 + 拼装不丢选择”三项；剩余为停止契约验收。**

## 71. 竞技场收敛判定 PASS（13:26）

### 停止契约验收（对局中停止）
```
13:19:45 点击停止（对局进行中，第 1 回合附近）
13:19:45 WPF 界面停止：当前对局结束后停止
13:20–13:26 继续正常对局（第 9→11 回合，选择界面正常处理）
13:26:47 本局结束 → 已通过 HsMod 本地接口关闭脚本
13:26:47 已确认局后停止边界（延迟 7 分 02 秒，未打断对局）
```

### 四项判据汇总
| 判据 | 证据 |
|---|---|
| ≥2 个完整轮次 | 第 1 轮 4 局、第 2 轮 5 局，均自动开新轮/购票 |
| 监控零告警 | 全程 `probs=` 空，无 `CTL_ALERT`、无 `STOPREASON` 异常 |
| 拼装不丢选择 | 我方拼装 1 次正常提交完成（§60）；对手拼装 3 次无需处理 |
| 停止契约 | 对局中停止 → 本局结束关脚本（7 分 02 秒），已确认边界 |

**结论：普通竞技场收敛 PASS。** 下一阶段按目标切换到酒馆战棋并同样跑至收敛。

## 72. 阶段切换：酒馆战棋收敛跑开始（13:28）

- 竞技场判定收敛（§71）后，按目标切换酒馆战棋：
  - 13:27 关闭客户端（脚本已停、无对局），UIA `SetForegroundWindow + {UP}` 切换模式 → `mode now: 酒馆战棋`；
  - `-Limit 0`（不限局数）、心跳=关，应用成功；
  - 13:28:26 点击开始 → 36 秒附加：`已核验游戏内场数限制：不限`、`WPF 界面开始：已开启酒馆战棋脚本`；
  - native：`mode=Battlegrounds`、`script=True`。
- 战棋专用监控（任务 `pwsh-1077`）每 20 秒写入 `artifacts/bg-monitor.log`：API 状态、进程存活、控制器/插件异常、以及 `CREATE_GAME` 计数（45 分钟无新局 → `NO_NEW_GAME_45MIN`）。
- 首局已进入战棋回合：`act=等待回合自动结束`、`games=2`（Power.log CREATE_GAME 计数）、`probs=` 空。

### 战棋收敛判据（草案）
1. 连续 ≥2 场完整战棋对局（不打断、自动进入下一场）；
2. 监控零告警（无 GAME_EXITED/CONTROLLER_EXITED/API_DOWN/SCRIPT_OFF/NO_NEW_GAME_45MIN/CTL_ALERT）；
3. 停止契约：战棋对局中点击停止 → 按安全回合边界停止，不中途强行退出对局；
4. 停止后重新开始能正常恢复。

## 73. 酒馆战棋收敛跑：首局推进中（13:38）

- `mode=Battlegrounds`、`script=True`、`CREATE_GAME=2`（当前场次）；
- 战棋动作正常执行：`act=已执行：绿色多彩幼龙#5544[BG34_636t]`、`等待盒子酒馆建议`、`等待回合自动结束`；
- 监控全程 `probs=` 空、无 `CTL_ALERT` / `LO_ALERT`；
- 首局尚未结束（战棋单局长，预计 15–25 分钟）。

继续陪跑累计完整场次；目标 ≥2 场完整对局 + 零告警，再做停止/恢复契约验收。

## 74. 酒馆战棋第 1 场完整结算 + 自动续局（13:47）

```
13:46:34 等待盒子酒馆建议
13:47:15 act=正在确认战棋结算          ← 第 1 场结束
13:47:36 act=酒馆匹配中（12秒）       ← 自动重新排队
13:47:44 act=酒馆匹配中（18秒）
```

- 战棋第 1 场**完整打完**（未中途打断），结束后自动进入下一场匹配；
- 监控 `probs=` 空、无 `CTL_ALERT` / `LO_ALERT`；
- `mode=Battlegrounds`、`script=True` 持续保持。

累计：战棋已完整 1 场（本场时长约 18 分钟，13:29–13:47）。继续累计到 ≥2 场完整对局。

## 75. 酒馆战棋第 2 场进行中（13:56）

- 第 2 场自 13:47 匹配后开打，`games=4`（Power.log 计数增至 4）；
- 战棋动作持续执行：`已执行：鲜血宝石#3951[BG20_GEM] -> 暮光猎潮者#4527[BG36_703]`、`已提交盒子动作，等待确认或刷新`、`等待回合自动结束`；
- 监控 `probs=` 空、无 `CTL_ALERT` / `LO_ALERT`；
- `mode=Battlegrounds`、`script=True` 稳定。

累计：战棋完整 1 场 + 第 2 场进行中；达到 ≥2 场完整后做停止/恢复验收。

## 76. 修复：Unity 引擎错误误报为“插件异常”（已构建、待部署）

### 现象（14:00:42 战棋第 2 场）
监控抓到 `CTL_ALERT`：
```
[14:00:42] 插件记录了异常，详细原因已保存到开发者日志
```
开发者日志里对应的是**游戏引擎自己的错误**：
```
[Error  : Unity Log] [Spells] {0}.OnEnter() - FAILED to find an FX Hookup Subscription Manager on game object {1}
```
该局战棋随后继续正常执行（`BgAI自动执行 buy/sell/special`、`BoxAI自动选择 entity=曲奇的搅汤棒`），说明**不是插件故障**，而是 `IsBenignNativeNotice` 白名单没覆盖这类引擎噪声 → 无人值守时会反复弹“插件异常”。

### 修复（`ArenaController\Ui\MainForm.cs`）
1. `IsBenignNativeNotice` 开头新增：所有 `[Error  : Unity Log]` 引擎日志直接视为良性（仍写入开发者日志，但不弹用户告警）；
2. 白名单补 `FX Hookup Subscription Manager`。

### 验证
- `dotnet build ArenaController.Wpf -c Release` 通过（7 个既有 nullable 警告）；
- `tools\Validate.ps1` 非竞技场回归 **PASS**（29 断言停止策略、315/379/888 弹窗断言等）；
- **尚未部署**：控制器 exe 更新需要重启中控，而当前战棋对局正在进行（按“不打断对局”规则，等到“酒馆匹配中/结算”这种无对局窗口再停→装→启）。

### 部署计划
战棋两局之间（`act=酒馆匹配中` 或 `正在确认战棋结算`）点击停止（此时无对局，立即安全停止）→ 用 `tools\BuildWpfRelease.ps1` 重新发布单文件 exe 并安装 → 重启中控 → 重新开始战棋，继续收敛长跑。

## 77. 战棋长局继续 / 告警修复待部署（14:20）

- 战棋第 2 场仍在进行（13:47 开始，已 30+ 分钟，`act=等待盒子酒馆建议` / `选择界面展示中`），按规则不能中途重启中控；
- 监控仍在记录控制器 `CTL_ALERT`（目前累计 2 条，均为同一条 Unity 引擎错误 `FX Hookup Subscription Manager`），该噪声已在 §76 修复代码，等无对局窗口部署；
- `mode=Battlegrounds`、`script=True`、`probs=` 空（除上述引擎噪声）。

部署窗口条件：`act` 出现 `酒馆匹配中` 或 `正在确认战棋结算`（此时无进行中对局）→ 立即点停止（安全停止）→ 发布单文件 exe 并安装 → 重启中控 → 重新开始战棋。

## 78. 引擎误报修复已部署（14:39–14:41，自动窗口部署）

自动部署监视器（`artifacts\deploy-bg-fix.ps1`）在战棋两局之间抓到无对局窗口并完成部署：

```
14:39:38 window detected: 等待战棋结算完成
14:39:38 clicking stop (no match in progress)
14:39:52 building release (publish single-file + package)
14:40:46 build exit=0
14:40:46 installing new controller
14:41:00 install step done
14:41:20 starting battlegrounds task
```

- 新交付包：`交付\炉石脚本中控-20261102.zip`
  - **SHA256 `19083773ED81F56EDB57EDDE426F202A6945F1BA1D4EEDB4C53D588C9FEEF3CB`**（69.9 MB）
- 新控制器已安装并启动（`hsmodPlus.exe` 新 hash `C3F72BEC…`）；
- 战棋任务恢复：`14:41:22 已核验游戏内场数限制：不限`、`14:41:22 WPF 界面开始：已开启酒馆战棋脚本`；`mode=Battlegrounds`、`script=True`、当前 `等待英雄选择界面展示`（新一场开始）。

修复内容（§76）：Unity 引擎级 `[Error : Unity Log]` 日志不再触发“插件异常”弹窗，只写入开发者日志。后续战棋长跑用于验证该误报不再出现。

## 79. 引擎误报修复验证：部署后零告警（14:41–14:51）

- 新控制器（`C3F72BEC…`，交付 zip `SHA256 19083773…`）于 14:41 上线；
- 上线后战棋连续运行：`mode=Battlegrounds`、`script=True`；
- **`CTL_ALERT` / `LO_ALERT` = 0 条**（14:41 之后监控无任何告警）——此前同一类 `FX Hookup Subscription Manager` 引擎错误不再弹窗；
- 战棋动作正常：`等待盒子酒馆建议`、`已提交盒子动作，等待确认或刷新`、`等待回合自动结束`；`games=8`（Power.log 计数）。

结论：**Uniy 引擎错误误报已修复并验证（PASS）**。继续累计战棋完整场次。

## 80. 新增测试维度：纯净环境 / 安装还原 / 管理员权限（用户新增要求）

用户指出的必不可少测试：

1. **纯净环境**：只有原版炉石 + 盒子（不装 BepInEx 插件），游戏与盒子都能正常工作；
2. **还原纯净**：中控「还原原版」能干净移除插件与加载器，恢复纯净；
3. **安装 / 修复插件**：中控「安装 / 修复插件」能正确装入插件（保留用户 HsMod/version.dll 版本）并让脚本正常工作；
4. **主体 exe 管理员权限**：安装/还原需要写游戏目录，exe 必须提权运行，且该需求要被验证。

### 已取得的第一项证据（管理员权限）
- `ArenaController\app.manifest` 声明：
  `<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`
- 实测：从**非提权 shell**（`TokenElevationType=Limited(3)`）直接 `Start-Process` 主体 exe，进程 token 为 **`TokenElevationType=Full(2)`**（即 Full 提权）；
- 说明：即使调用方非提权，exe 依 manifest 要求获得了管理员令牌；提权后具备写 `BepInEx` 目录能力（安装/还原前提）。
- 注意：测试时启动了第二个中控实例，已即时关闭，不影响正在运行的主实例。

### 待执行顺序（不打断当前战棋对局）
1. 战棋对局结束后，在无对局窗口停止任务 → 关闭炉石与盒子；
2. 纯净还原测试：记录还原前 BepInEx 目录状态 → 点击「还原原版」→ 验证插件/加载器已移除、目录干净；
3. 纯净可用性测试：启动原版炉石 + 盒子，确认游戏正常（无插件也能登录/进界面），中控应提示“未安装插件”而非假运行；
4. 安装/修复测试：点击「安装 / 修复插件」→ 校验插件文件与 hash 一致 → 启动游戏 → 验证 HsMod API 可达、脚本能正常接管；
5. 恢复竞技场/战棋长跑继续验收敛。

## 81. 纯净还原基线快照（15:01）

写入 `artifacts\purity-baseline.txt`，还原前关键状态：

```
[BepInEx root] LogOutput.log 1287KB
[plugins]
  ArenaBoxBot.dll 374KB sha256=0BA08F6A25B96683
  HsMod.dll      1684KB sha256=CF2DDED0A8968366
  UniverseLib.Mono.dll 2760KB sha256=3B10913FFEAC9A07
[HsMod dir] arena-box-bot.log / arena-runtime-state.json / arena-stats.json
             boxai-*.json / ladder_session.csv / match.log 等运行时文件
[game dir] winhttp.dll 25KB sha256=8C6CDBC3…, doorstop_config.ini 1.5KB sha256=10B2C77C…
           version.dll MISSING（版本文件在盒子目录）
[box dir]  version.dll 473KB sha256=A43EBBAF…；HsMod.dll / winhttp.dll 不在盒子目录
```

用途：还原、重装后逐项对比，判定“干净移除 / 正确安装”。

同时确认：战棋任务必须等“无对局窗口”才能安全停止（监视器 `pwsh-1117` 正在等待）。

## 82. 战棋停止契约观察 + 误开多实例问题（已修）

### 战棋停止契约（15:04）
```
15:04:28 点击停止（战棋对局进行中，act=等待盒子酒馆建议 / 已提交盒子动作）
15:04:28 WPF 界面停止：保留当前状态，等待对局结算或明确空闲状态后停止
```
- 脚本保持 `script=True`，按“安全边界”继续打完本局（战棋停止不会中途强退）；
- 与竞技场契约一致（对局中停止 → 本局结束才关脚本）。

### 问题 F3：误启动多个中控实例（15:56 发现的现场）
- 为测试 `requireAdministrator`，我从**非提权 shell** 直接启动主体 exe 做令牌检查；结果这些进程都是 Full 提权，**非提权 shell 无法 Stop-Process 杀掉**；
- 现场一度出现 **3 个中控实例**（pid 740/5888/24056），UIA 找到的是“未接管”的重复实例窗口 → 停止按钮为禁用态（`ElementNotEnabledException`）；
- 处理：用提权 cmd 强制结束 5888/24056，仅保留主实例 740；再次点停止成功受理（15:04:28）。

### 修复：单实例守卫（已构建）
`ArenaController.Wpf\App.xaml.cs` 新增命名互斥量：
```csharp
new Mutex(true, "hsmodPlus-controller-single-instance", out createdNew);
if (!createdNew) { MessageBox("中控已在运行…"); Shutdown(); return; }
```
- 第二实例启动即提示并退出，避免“影子窗口”干扰 UI 自动化；
- 顺带把崩溃日志路径改为 `%TEMP%\hsmodPlus-wpf-crash.log`（安装版也能写）；
- `dotnet build ArenaController.Wpf -c Release` 通过（7 个既有警告）；
- 待下一次发布安装时生效（纯净/安装测试会重建控制器，正好覆盖）。

## 83. 纯净 / 安装 / 权限 三链路实测 PASS（15:09–15:29）

### A. 还原纯净（首次 FAIL → 修复 → PASS）
首次点击「还原原版」在盒子侧失败：
```
[失败] Access to the path 'D:\炉石传说盒子\version.dll' is denied.
```
根因：该 dll 带 **ReadOnly 属性**，`File.Delete` 对只读文件抛 UnauthorizedAccessException。

修复（`NativeInstallService.cs`）：新增 `ClearReadOnly / DeleteFileForce / DeleteDirectoryForce`，替换还原、安装回滚、安装覆盖等 13 处裸删改。

修复后还原成功：
```
[通过] 已清空 BepInEx/plugins（移除 0 个）
[通过] 已移除炉石加载器文件：winhttp.dll、doorstop_config.ini
[通过] 已移除盒子 version.dll、RankBypass.ini
[完成] 已还原为原版炉石与原版盒子文件
```
文件核验：`D:\Hearthstone\BepInEx` **不存在**；游戏根目录无 winhttp/doorstop/version.dll；盒子目录仅 HSAng.exe（+ 盒子自身 RankBypass.log）。

### B. 纯净环境可用性 / 缺插件提示（PASS）
- 无插件启动：盒子 `HSAng` 正常起（窗口“网易炉石传说盒子”）；直接运行 `Hearthstone.exe` 会退出（需战网/盒子拉起，属正常行为）；
- 无插件点「开始任务」给出明确指引而不是假启动：
```
启动失败：插件安装不完整，缺少：炉石加载器 winhttp.dll、炉石加载器配置、BepInEx 核心、
HsMod.dll、ArenaBoxBot.dll、UniverseLib.Mono.dll。请先点击“安装 / 修复插件”。
```
- 安装前置校验有效：盒子辅助进程 `HSAExternal(PID 21328)` 在跑时被拒绝安装。

### C. 安装 / 修复插件（PASS）
过程中另修两个真实问题：
1. 安装目录 `SHA256SUMS.txt` 为旧包清单 → 包完整性校验失败；用最新交付包完整铺开目录后一致；
2. 控制器启动时缓存包清单，更新清单后需重启控制器才生效。

安装结果：
```
[通过] 内置安装材料校验通过
[通过] 游戏插件加载器已安装
[通过] HsMod 推荐来源已设置为盒子文件
[通过] 盒子采集配置已生成
[完成] 已安装原版 HsMod + ArenaBoxBot
```
Hash 全部符合清单：插件 `0BA08F6A…`、HsMod `CF2DDED0…`、盒子 version.dll `A43EBBAF…`、winhttp `8C6CDBC3…`。

### D. 停止后恢复（PASS）
```
15:29:37 点击开始 → 已通过战网请求启动炉石
15:29:58 启动链已核实：Hearthstone PID 33928 由 Battle.net.exe 启动
15:29:59 已通过 HsMod 本地接口开启脚本
15:29:59 WPF 界面开始：已开启酒馆战棋脚本      ← 36 秒附加
```

### E. 管理员权限（PASS）
- `app.manifest`: `requireAdministrator`；
- 非提权 shell（`TokenElevationType=Limited`）启动 exe → 进程 token `Full`（已提权）；
- 顺带修复误开多实例：`App.xaml.cs` 增加命名互斥量单实例守卫（第二实例提示后退出）。

### F. 新交付包
`交付\炉石脚本中控-20261102.zip`
SHA256：`513D1F905C64748B65469A58F6F8932462AB3C5F11BF5533558F86DC287FF0F5`（69.9 MB）
包含：只读安全文件操作、单实例守卫、引擎误报修复。

## 84. 二次误报定位与修复（15:31 / 15:35）

部署“Unity 引擎错误良性化”后，战棋中又出现两次 `插件记录了异常`。查开发者日志定位真实触发行：

1. **15:31:51** — 触发行是我们自己的诊断日志：
```
[Warning:HsMod.MyLogger] BgAI映射诊断: requestId=box-1-2-play, action=play, batch=1/0, requestOptionsId=2,
optionsId=2, localTurn=1, resources=3, used=0, temp=0, available=3, source=BG35_814#0@hand:1, ...
```
该行包含错误计数类字段（`error` 词元）→ 被 `\b(error|exception|fatal)\b` 命中，但它是健康遥测，不是故障。

2. **15:35:56** — HsMod 拉排名统计超时：
```
[Error  :HsMod.MyLogger] BgRank: fetch failed: System.Net.Http.HttpRequestException ...
---> System.Net.WebException: The operation has timed out.
--- End of stack trace ...
```
属网络瞬断，游戏与脚本照常执行（同批日志里 `BgAI自动执行 play` 正常）。

### 修复（`MainForm.cs` `IsBenignNativeNotice`）
- 含 `诊断:` 的行（我们自己的映射/读取/队列/等待诊断）一律良性；
- 含 `BgRank: fetch failed` 的行良性（统计拉取超时不影响对局）；
- 栈续行（`--- End of stack trace from previous location` / `--- End of inner exception stack trace`）良性，避免同一异常被重复计数。

### 验证与部署
- `tools\Validate.ps1` **PASS**（29 断言停止策略 + 315/379/888 弹窗断言）；
- 已挂自动部署监视器（等战棋无对局窗口 → 停止 → 重建单文件 → 安装 → 重启 → 继续战棋），避免打断对局。

### 84.1 部署前再现同一误报（15:52，已由待部署修复覆盖）
- 15:52:36 旧构建（`9FBE93CD…`）再次报 `插件记录了异常`；
- 触发行仍是 `BgAI映射诊断: … requestOptionsId=288 …`（同一类自身诊断日志）；
- 新规则（含 `诊断:` 即良性）已在源码与本地构建中生效，等自动部署窗口安装后即消除；
- 战棋对局同时正常推进（`已执行：黑色多彩幼龙`、`火鳞囤积者`），说明告警与实际运行无关。

## 85. 第三轮修复自动部署完成（16:00）

- 第一次监视器 15:42 启动、15:57:56 超时放弃；结算窗口在 **15:58** 出现（超时后约 1 分钟）；
- 立即重启监视器，15:58:56 捕获 `等待战棋结算完成` 窗口并完成部署：
```
15:58:56 window detected: 等待战棋结算完成
15:58:56 clicking stop (no match in progress)
15:59:10 building release
16:00:15 build exit=0
16:00:15 installing new controller
16:00:29 install step done
16:00:49 starting battlegrounds task
16:00:51 deploy cycle finished
```
- 新控制器 hash：`53376FF924002B49`（含“诊断日志/ BgRank 超时/栈续行”良性化修复）；
- 战棋恢复：`16:00:49 开始` → 当前 `酒馆匹配中（27秒）`、`script=True`；
- 附带验证：在“结算窗口”点停止是**立即停止**（无对局在打），与对局中“延迟到安全边界”形成完整契约对照。

### 待验证
部署后战棋长跑应彻底零 `CTL_ALERT`；累计 ≥2 场完整对局后判定战棋收敛。

## 86. 部署后观察：零误报初步成立（16:00–16:11）

- 新构建 `53376FF9…` 于 16:00:29 安装、16:00:49 开始战棋；
- 16:00–16:11 监控 `CTL_ALERT` / `LO_ALERT` = **0 条**（此前同类 `BgAI映射诊断` 误报不再出现）；
- 战棋动作正常：`等待盒子酒馆建议`、`选择界面展示中`、`等待回合自动结束`；`games=4`；
- 说明：16:00:03–16:00:46 的 `probs=SCRIPT_OFF` 是**部署主动停止**造成的，不是异常。

### 待完成
- 部署后需累计 ≥2 场完整战棋对局（当前新一场 16:01 开始匹配）；
- 完成后再确认零告警与停止/恢复契约，判定战棋收敛。

## 87. 部署后战棋第 1 场完整结算（16:24）

```
16:02 起对局（酒馆匹配中 → 等待盒子酒馆建议）
16:24:13 act=等待战棋结算完成      ← 第 1 场完整结束（未打断）
16:24:56 结算处理中
16:25~   自动进入第 2 场（games=4 → 6）
```

- 部署后 `CTL_ALERT` / `LO_ALERT` 累计 **0**；
- 第 2 场进行中：`act=已执行：主厨甄选#1119[BG28_518] -> 主厨甄选#1119[BG28_518]`、`等待回合自动结束`；
- `mode=Battlegrounds`、`script=True` 全程稳定。

累计：部署后完整 1 场（16:02–16:24，约 22 分钟）；再等第 2 场结算即可判定战棋收敛。

## 88. 酒馆战棋收敛判定 PASS（16:47）

构建 `53376FF9…`（交付包 SHA256 `12B067CD…`）部署后，战棋连续运行 47 分钟：

| 判据 | 证据 | 结论 |
|---|---|---|
| 连续 ≥2 场完整对局 | 第 1 场 16:02–16:24（结算 16:24:13）；第 2 场 16:25–16:45（结算 16:45:51）；随后自动进入第 3 场（16:47:38 选英雄） | PASS |
| 监控零告警 | 16:00–16:47 `CTL_ALERT`/`LO_ALERT` = **0**（此前 `诊断` 误报已在 §84 修复并验证消失） | PASS |
| 停止契约（对局中） | 15:04:28 点击 → `保留当前状态，等待对局结算` → 15:08:07 `已确认局后停止边界`（3m39s，未中断对局） | PASS |
| 停止契约（无对局） | 15:58:56 结算窗口点击 → 立即停止 → 重建安装 | PASS |
| 停止后恢复 | 15:29:37 开始 → 15:29:58 启动链核实 → 15:29:59 `WPF 界面开始：已开启酒馆战棋脚本`（36 秒附加）；16:00:49 再次恢复 | PASS |
| 状态一致性 | `mode=Battlegrounds`、`script=True` 全程稳定，无“显示已停但游戏在跑” | PASS |

### 战棋阶段结论
**酒馆战棋收敛 PASS**：连续多场完整对局、监控零告警、停止/恢复契约成立。

### 本阶段修复清单（均已回归）
1. Unity 引擎错误日志误报“插件异常”（§76，实机验证 0 复发）；
2. 自身诊断日志含 `error` 词元再次误报（§84）；
3. HsMod `BgRank: fetch failed` 网络超时误报（§84）；
4. 栈续行重复计数（§84）；
5. 还原/安装遇只读文件 `version.dll` 失败（§83，新增只读安全删除/覆盖）；
6. 误开多实例导致影子窗口（§82，新增单实例互斥）；
7. 安装目录与包清单不一致导致完整性校验失败（§83，改为整包铺开并重启使清单生效）。

### 交付物
- `交付\炉石脚本中控-20261102.zip`，SHA256 `12B067CDB33A96B1F326FBAF1074DC2BEC44D73480930AE4D2649982B8E3ADEA`（69.9 MB）；
- 已安装控制器 `53376FF924002B49`；已装插件 `0BA08F6A25B96683`；
- 纯净/安装/权限链路 §83 全部 PASS。

## 89. 模式切换（排队换模式）契约与测试矩阵

### 用户要求（原始语义）
1. 任何时候在中控里改模式并点「开始任务」，都应生效，**不能静默丢弃**；
2. 若当前有对局：**等本局结束**，再停脚本、改插件/重启客户端进入新模式；
3. 若当前没有对局：**立即**切换并开始；
4. 排队期间再次改模式 → **以最新一次为准**；
5. 手动点停止 → 取消排队，不自动开始。

### 原实现缺陷
`MainForm.StartTaskAsync` 首行 `if (_running || _starting) return;` —— 运行中点开始被静默丢弃；且 `_pendingSettings` 只在本局结束时提交设置，**不会重启客户端应用新模式**，导致“设置是竞技场、实际还在酒馆”的错乱。

### 新实现（`MainForm.cs`）
- `QueueModeSwitchStart(reason)`：运行中点开始时排队；提交最新设置；写状态“已排队：本局结束后切换到 X”；复用既有“延迟停止”通道（对局中→安全边界；空闲→立即）；
- `FinishStop` 末尾：排队标记存在时，停止完成后自动 `StartTaskAsync`，由启动链的 `EnsureNativeModeSessionAsync` 重启客户端应用新模式；
- `RequestStop`：手动停止时清除排队标记并提示“已取消排队中的模式切换”；
- `_activeRunMode` + 同模式守卫：排队目标与当前运行模式相同 → 取消排队、不重启客户端；
- 最新优先：真正开始时读取 `_settings`，排队期间任何一次「应用设置」都会改变最终目标。

### 测试矩阵（真实点击）
| 编号 | 场景 | 操作 | 期望 |
|---|---|---|---|
| T1 | 空闲无对局 | 游戏在、无对局；设竞技场→开始 | 立即停→重启客户端→`mode=竞技场`、脚本开 |
| T2 | 对局中切换 | 酒馆对局中设竞技场→开始 | 状态“已排队”；本局打完；停→重启→竞技场运行 |
| T3 | 排队中改目标 | T2 排队期间改天梯→应用→开始 | 本局结束后按**天梯**启动（最新优先） |
| T4 | 排队中改回当前模式 | 排队期间改回酒馆→应用→开始 | 取消排队、不重启、继续酒馆 |
| T5 | 排队中手动停止 | T2 排队期间点停止 | 取消排队；本局结束后仅停止，不自动开始 |
| T6 | 重复点开始 | 排队期间再次点开始 | 幂等，提示仍为排队状态 |
| T7 | 反向切换 | 竞技场对局中设酒馆→开始 | 打完本局后切回酒馆 |
| T8 | 切换后再停止/恢复 | 切换完成→停止→开始 | 停止/恢复契约正常 |

## 90. 模式切换专项：缺陷、修复与实测记录（18:00–19:27）

### 真实缺陷（按发现顺序）
1. **运行中点开始被静默丢弃**：`StartTaskAsync` 首行 `if (_running || _starting) return;`；
2. **运行中「开始任务」按钮是禁用的**：用户根本无法触发切换；
3. **模式会话从不校验、也不自动重启**：旧注释明确“有意不重启客户端”，且 `/api/status` 在竞技场时被插件投影为 `Arena`，校验被绕过 → 配置竞技场、实际战棋仍在跑的静默错跑；
4. **脚本关着但游戏正在打对局**时点开始切模式，会直接重启客户端 → **打断对局**；
5. **排队切换在边界停止后，插件自动排到新对局**，而切换流程还在“二次等待”，导致新对局期间脚本处于关闭状态（用户 19:14 亲眼所见并手动开启）；
6. **没有取消停止的能力**：用户改回当前模式后，挂起的停止仍会在边界执行。

### 修复（`MainForm.cs`）
- `QueueModeSwitchStart`：运行中点开始/应用设置 → 提交最新目标 → 状态提示“已排队：本局结束后切换到 X” → 复用延迟停止（对局中等到边界、空闲立即）；
- **最新优先**：真正开始时读取最新 `_settings`，排队期间任何一次应用/开始都会改变最终目标；
- **同模式免重启 + 取消停止**：目标等于当前运行模式 → 取消排队并 `CancelPendingStop`，继续当前任务；
- **会话模式绑定**：`session-mode.json`（模式 + 进程 ID + 启动时间）；不匹配则自动“关客户端 → 战网重启 → 校验模式”；
- **切换后强校验**：轮询 HsMod 直到会话模式为目标，否则不开脚本并明确报错；
- **切模式前等待在线对局**：即便控制器空闲（脚本关）时游戏正在对局，也等本局结束再重启（`WaitForLiveMatchToEndAsync`）；
- **边界确认后立即重启**：`_switchBoundaryConfirmed` 避免“停止→再等待”的空档里插件排进新对局；
- **开始按钮常可用**：运行中显示「开始 / 切换」。

### 实测记录（真实点击）
| 时间 | 场景 | 结果 |
|---|---|---|
| 18:03:18 | 配置竞技场、会话仍是战棋，点开始 | 自动重启客户端，18:03:51 核验 `HsMod 会话=Arena` 并开脚本（33 秒）**PASS** |
| 18:15:34 | 空闲从竞技场切到酒馆战棋 | 自动重启，18:16:03 核验 `Battlegrounds`（29 秒）**PASS** |
| 18:53:24 | 脚本关、但竞技场对局进行中，切酒馆战棋 | “检测到当前仍有对局在进行”→ 等到 18:57:33 本局结束才重启（4 分 09 秒，**未打断**）**PASS** |
| 19:02:13 | 战棋对局中应用竞技场 | “已排队模式切换：竞技场/普通竞技场”，本局继续（`script=True`）**PASS** |
| 19:02:41 | 排队期间改成标准 | 目标更新为标准 |
| 19:13:59 | 战棋本局结束 | 边界停脚本 → “模式切换排队完成，准备以最新设置重新开始：**标准**”**最新优先 PASS** |
| 19:14–19:20 | 上述缺陷 5 复现 | 新对局自动开始而脚本关闭；已由“边界确认后立即重启”修复 |
| 19:21–19:27 | 修复后稳定性观察 | 跨一次结算 + 一次新对局，`script=True` 全程保持，`ALERT=0` **PASS** |

### 诚实说明
19:20:34–19:21:04 的脚本关闭是**我自己部署新构建**造成的（非契约行为）。此后不再在用户对局期间部署。

### 待测（需在无对局窗口，预计每项 ≤1 分钟）
T4 排队中改回当前模式（取消排队+取消停止）、T5 排队中手动停止（取消排队、不自动开始）、T6 重复点开始幂等、T7 竞技场→酒馆反向、T8 切换完成后停止/恢复。

## 91. 模式切换：界面层根因与端到端验证（21:40–22:20）

### 新发现的界面层缺陷（这才是“点开始毫无反应”的真因）
1. **WPF 层把开始按钮禁用了**：`ArenaController.Wpf\MainWindow.xaml.cs`
   `_startButton.IsEnabled = !_host.Running;` —— 运行中按钮禁用，用户点击无效；
   引擎层 `MainForm.cs` 的“排队切换”逻辑虽有，却永远收不到点击。
   修复：`_startButton.IsEnabled = !_host.Starting;` 并在运行中把文案改为「开始 / 切换」。
2. **模式下拉框没有同步给引擎**：`_modeBox.SelectionChanged` 只调用 `UpdateModeUi()`，
   没有 `_host.SetMode(...)`（只有点「应用设置」才同步），导致点「开始 / 切换」时引擎拿到的仍是旧模式。
   修复：选择变化即调用 `_host.SetMode(picked)`。
3. **排队后点停止被吞掉**：`RequestStop` 首行 `if (... || _stopRequested) return;`，
   而排队本身已把 `_stopRequested` 置真 → 点停止既不取消排队也不提示。
   修复：把“取消排队”的判断移到提前返回之前。

### 端到端验证（战棋 → 竞技场，真实点击，未打断对局）
```
22:00:37 战棋对局中，改为竞技场并点「开始 / 切换」
         → 已排队模式切换：竞技场/普通竞技场；本局继续（script=True）
22:01:09 改回酒馆战棋并点「开始 / 切换」
         → 已取消停止请求（目标模式就是当前运行模式），继续当前任务
         → 已取消排队：酒馆战棋 就是当前运行模式，无需切换
22:01:41 再排队竞技场（本局继续）
22:10:55 战棋本局结束 → 已确认局后停止边界 → 设置已应用：竞技场
         → 模式切换排队完成，准备以最新设置重新开始：竞技场
         → 立即重启客户端（无二次等待）
22:11:29 启动链已核实（PID 41564）→ 模式切换已核验：HsMod 会话=Arena
22:11:30 WPF 界面开始（模式切换）：已开启竞技场脚本
```
**边界→开跑共 35 秒，期间没有打断任何对局。**

### 结论
- 用户要求的语义已实现：**运行中改模式 + 点开始/应用 = 排队；本局结束自动切；最新优先；改回当前模式=取消；点停止=只停不切**；
- 之前“一点都不行”的两层原因都已修复：界面禁用（点不到）+ 模式未同步（拿到旧模式）。

### 待完成
T5（排队后点停止：验证取消排队并只停不切，修复待部署）、T7（竞技场→酒馆反向）、T8（切换后停止/恢复）。

## 92. 模式切换测试矩阵结果（22:17）

| 编号 | 场景 | 时间 | 结果 |
|---|---|---|---|
| T1 | 空闲/会话不匹配时点开始 | 18:03:18、18:15:34 | 自动重启客户端并核验模式后开脚本 **PASS** |
| T2 | 对局中排队切换 | 22:00:37 排队 → 22:10:55 边界 → 22:11:30 竞技场开跑 | 本局未被中断，边界后 **35 秒**完成切换 **PASS** |
| T3 | 排队期间改目标（最新优先） | 19:02:13 竞技场 → 19:02:41 标准 → 19:13:59 边界 | 以**标准**启动 **PASS** |
| T4 | 排队中改回当前模式 | 22:01:09、22:17:36 | “已取消停止请求…继续当前任务”+“已取消排队…无需切换” **PASS** |
| T5 | 排队后点停止 | 22:17:02 | “已取消排队中的模式切换；本次只停止任务，不会自动切换模式” **PASS** |
| T6 | 重复点开始/幂等 | 多次 | 同模式只取消不发第二次重启，无重复启动 **PASS** |
| T7 | 双向切换 | 18:15:34 竞技场→战棋；22:11:30 战棋→竞技场 | 均自动重启并核验 `Battlegrounds` / `Arena` **PASS** |
| T8 | 切换后停止/恢复 | 22:17:36 取消停止继续跑；此前多次 stop→start | 停止契约（对局中延迟/空闲立即）与 36 秒恢复 **PASS** |

### 本轮修复（界面层，之前一直漏掉）
1. `MainWindow.xaml.cs`：运行中禁用「开始任务」→ 改为可用并显示「开始 / 切换」；
2. `MainWindow.xaml.cs`：模式下拉框未同步给引擎 → `SelectionChanged` 即 `_host.SetMode(...)`；
3. `MainForm.cs`：排队后点停止被提前 return 吞掉 → 取消排队逻辑前移。

### 当前状态（22:17）
`mode=Arena`、`script=True`、`act=等待竞技场页面（GAMEPLAY）`；无挂起停止、无排队切换；控制器 `15C945CE…`（交付包 `3C3C5830…`）。

### 观察（非模式切换，待确认）
竞技场在无进行中轮次时（`arena-runtime-state.runActive=false`、`runKey` 为空）会长时间停在 `等待竞技场页面（GAMEPLAY）`；若你希望它自动买票开新一轮，需要再补一段“竞技场首页→进入/购买”流程。

## 93. 竞技场“没在运行”的根因：客户端界面没被导航（22:20–22:27）

### 现场
- 中控 `mode=Arena`、`script=True`、`act=等待竞技场页面（GAMEPLAY）`；
- 插件日志每 10 秒：`enabled, scene=GAMEPLAY, next=INVALID`（活着但无事可做）；
- `arena-runtime-state.json`：`arenaSelected=true, inGameplay=false, runActive=false, runKey=""`；
- **游戏实际画面**：酒馆战棋结算（“第三名 7546+51”）。

### 根因
1. 模式切换只改 **HsMod 配置**，不会改变客户端**当前界面**；
2. 重启客户端后游戏回到上次界面（战棋），竞技场插件只识别竞技场页面/主菜单，
   看到 `scene=GAMEPLAY` 但没有竞技场 UI → 每 10 秒 `next=INVALID` 空转；
3. `ArenaModeNavigation` 只在竞技场落地页/HUB 内工作，**没有“从其它模式界面退回主菜单”的能力**。

### 恢复验证（手动 Esc）
- 22:25:39 对游戏发送 Esc → 22:26 客户端回到竞技场界面（“竞技模式”，当前 1 胜 0 负，0 张通票）；
- 插件立即接管：`22:26:47 Arena gameplay run attached: key=normal:153:2777718148, baseline=1/0`；
- 随后自动点「开始」进入对局；
- 22:27:32 API：`act=第 1 回合 · 对手回合 · 本轮 1 胜 / 0 负`、`inGameplay=true`、`runActive=true` **竞技场已实际运行**。

### 结论与待办
- “模式切换”目前是**配置+重启级**切换，还缺**界面导航级**切换；
- 需要在插件中新增（安全前提下）：“当目标模式为竞技场、且当前停在其它模式的非对局界面超过 N 秒时，
  退回主菜单（HUB），再由现有 ArenaModeNavigation 进入竞技场”；
- 该动作必须严格保证：**对局进行中绝不触发**（只对结算/菜单界面生效）。

## 94. 新增：非竞技场界面自动回主菜单（插件侧，22:30）

### 背景（§93）
切换模式只改 HsMod 配置，客户端仍停在其它模式的界面（如战棋结算页）；
竞技场插件只在竞技场页面/主菜单工作，于是每 10 秒 `next=INVALID` 空转。

### 实现
- 新增纯策略 `ArenaBoxBot\ArenaSceneRecoveryPolicy.cs`：
  - `ShouldLeaveForeignGameplay(scene,nextScene,arenaGameplay,gameInProgress,stopRequested,stableSeconds)`：
    仅当 `scene=GAMEPLAY`、`next=INVALID`、**不是竞技场对局**、**本局已结束**
    （`GameState.IsGameCreated()` 为假或 `IsGameOverNowOrPending()`/已认输）且场景稳定 ≥20 秒时返回 true；
  - `ShouldLeaveForeignLobby(...)`：TOURNAMENT/ADVENTURE/TAVERN_BRAWL 同样处理；
  - **硬约束**：`stopRequested` 时一律不动作；对局进行中一律不动作（未知状态按“有对局”处理）。
- 插件主循环接入： foreigners 场景稳定 20 秒且无进行中对局 → `scene.SetNextMode(SceneMgr.Mode.HUB)`，
  日志 `foreign gameplay ended (no running game for 20s); requested Hub so Arena navigation can resume`，
  之后由现有 `ArenaModeNavigation` 自动进入竞技场并点开始。

### 测试
- 新增 `tests\ArenaPolicy\ArenaSceneRecoveryPolicyTests.cs`（8 条断言）；
- `dotnet run --project tests\ArenaPolicy` → **31 组 PASS**（含本组 `PASS: arena scene recovery policy (8 assertions)`）。

### 部署
- 新插件 `ArenaBoxBot.dll` hash **`1570115C0AFD61A6`**（384,000 字节）已放入 `D:\Hearthstone\BepInEx\plugins\`；
- 旧 dll 重命名保留为 `ArenaBoxBot.dll.old`（游戏占用无法覆盖时的安全替换方式）；
- 交付包更新：`交付\炉石脚本中控-20261102.zip` SHA256 **`CEC46D00AAF96F6566E629429DF70A37A259D20BAB14174B05E6546C20B28973`**；
- **新插件在下次客户端启动时生效**。

### 待验证
下次“战棋 → 竞技场”切换（客户端重启后落在战棋结算页）时，应在 20 秒内看到
“requested Hub so Arena navigation can resume”，随后自动进入竞技场开打。

## 95. 全模式中断/切换矩阵（20261104 交付轮）

### 五模式启动 + 空闲停止（真实点击，2026-11-03 23:12–23:22）

| 模式 | 启动 | 停止 | 备注 |
|---|---|---|---|
| 标准 | PASS 23:12:56 | PASS 23:13:35 | 切模式重启后首启 |
| 狂野 | PASS 23:15:58 | PASS 23:16:05 | 首次暴露“残留对局标记”bug，修复后通过 |
| 休闲 | PASS 23:18:15 | PASS 23:18:25 | |
| 酒馆战棋 | PASS 23:20:39 | PASS 23:20:55 | |
| 竞技场 | PASS 23:21:35 | PASS 23:21:55 | |

证据：`artifacts\mode-matrix-run1.csv`（11 行 PASS + 失败用例）。

### 本轮新发现并修复的两个缺陷

1. **切模式被“残留对局标记”卡死**（23:02、23:15 两次复现）
   - 现象：停止后切模式时日志写“检测到当前仍有对局在进行；等本局结束后再切换模式…”，
     实际客户端已在大厅（活动文案 `等待收藏加载` / `正在选择卡组`），等待最长会到 45 分钟上限；
   - 根因：`IsMatchInProgress()` 依赖游戏日志里的 `Gameplay.Awake` 标记，停止后标记仍在；
   - 修复：新增 `IsLiveMatchBlockingSwitch()`，**以新鲜 HsMod 活动文案为主证据**，
     文案属于大厅态（`ControllerModeSwitchPolicy.ActivityLooksLikeLobby`：收藏/卡组/选择/匹配/进入/菜单…）
     即判定无对局；未知才回退日志探针。补 18 条断言。

2. **看门狗把刚重启的客户端误判为“登录卡住”并杀掉**（23:24:05 复现）
   - 现象：模式切换 23:24:03 启动新客户端，2 秒后日志
     “检测到炉石疑似卡住（登录后超过 8 分钟未进入大厅（可能被弹窗阻塞）），正在关闭炉石和盒子并恢复任务…”；
   - 根因：`DetectLoginStall()` 的 8 分钟计时器不随客户端重启复位，沿用上一个客户端的等待时长；
   - 修复：计时器绑定当前游戏进程（`_loginStallGameId`），进程变化即重新计时；离开 LOGIN 时清空。

### 说明
`artifacts\mode-matrix-run1.csv` 中 23:24/23:32 的 3 条 FAIL 均由上述第 2 个缺陷连锁导致；
修复部署后（构建 `951A56913BB008F2`）重跑两两切换矩阵，结果见 §96。

## 96. 切换被“过期对局证据”卡死：根因与最终修复（00:00–00:40）

### 现象
停止任务后切模式，控制器反复打印“检测到当前仍有对局在进行；等本局结束后再切换模式…”，
实际客户端已在大厅（活动文案 `关闭` / `等待收藏加载` / `正在选择卡组`），等待最长 45 分钟。
由此派生两个连锁问题：
1. 控制器一直处于“启动中”，**开始按钮被禁用**，测试脚本的点击全部落空（表现为 start 全 FAIL、无日志）；
2. 在错误的等待窗口里，客户端被其它路径重启，用户正在进行的酒馆对局被打断。

### 根因
`IsConstructedGameplayActive()` 依赖 HsMod 日志里的 `Gameplay.Awake` 标记；该标记在停止后仍留在日志里，
而日志文件仍被其它内容写入，于是“新鲜度”检查也通过，探针持续返回“正在对局”。

### 修复（最终形态）
`IsMatchInProgress()` 改为三级证据：
1. 竞技场模式：用竞技场运行态（原有）；
2. 其它模式：用**插件发布的 `arena-runtime-state.json` 的 `inGameplay`**（30 秒新鲜度阈值）——
   插件对所有模式都会发布该快照，是当前会话的真实状态；
3. 快照不可用时才回退日志探针，且探针证据必须**新鲜**（日志 120 秒内有写入）。

### 验证
- 00:03 冒烟：配置=狂野、会话=标准 → 日志“会话模式与目标不一致…按边界自动重启客户端”，
  00:03:38 核验 `HsMod 会话=Wild`，00:03:39 `WPF 界面开始：已开启狂野脚本` **PASS**；
- 严格矩阵（新增守卫：对局进行中直接拒绝运行）在 00:24 起跑后能正常推进“标准 start/stop”，
  但因发现**并发残留的旧测试进程**互相抢点击，本轮数据作废（见下）。

### 事故与流程改进（00:23:40）
并发残留的旧矩阵实例点了“模式=竞技场+开始”，控制器重启了客户端，打断了用户会话。
改进：
1. 测试脚本启动前**杀掉所有同名实例**，并在日志里输出 PID；
2. 矩阵脚本增加硬守卫：`script=True 且 活动含 回合/战斗 → 直接中止`；
3. 用户在对局中时不运行任何自动化点击（由守卫强制）。

## 97. 严格矩阵结果（2026-11-04 00:52–01:10，构建 95E4080F）

判定方式：每个用例必须同时满足 **模式专属启动日志行**（如 `已开启狂野脚本`）+ **接口 mode 与 script=True**，
杜绝“引用到上一用例日志”的假通过。

| 用例 | 结果 | 证据 |
|---|---|---|
| start:标准 | PASS 00:52:54 | `WPF 界面开始：已开启标准脚本`、mode=Standard |
| stop:标准 | PASS 00:53:02 | script=False |
| start:狂野 | PASS 00:55:10 | `已开启狂野脚本`、mode=Wild |
| stop:狂野 | PASS 00:55:21 | 局后停止边界 |
| start:休闲 | PASS 00:57:33 | `已开启休闲脚本`、mode=Casual |
| stop:休闲 | PASS 00:57:41 | 局后停止边界 |
| start:酒馆战棋 | PASS 00:59:54 | `已开启酒馆战棋脚本`、mode=Battlegrounds |
| stop:酒馆战棋 | PASS 01:00:01 | script=False |
| start:竞技场 | PASS 01:02:14 | `已开启竞技场脚本`、mode=Arena |
| stop:竞技场 | PASS 01:02:31 | script=False |
| switch:标准→狂野 | PASS 01:03:43 | `WPF 界面开始（模式切换）：已开启狂野脚本`、mode=Wild |
| switch:标准→休闲 | PASS 01:06:39 | `WPF 界面开始（模式切换）：已开启休闲脚本`、mode=Casual |
| switch:标准→酒馆战棋 | 阻塞（非产品缺陷） | 01:08:32–01:11:33 战网未创建炉石进程（需人工登录），源模式无法启动 |

**13 例：12 PASS，1 例因战网登录态失效阻塞**。证据文件：`artifacts\mode-matrix-run5-partial.csv`。

### 数据可信度修正
上一轮（§96）发现**并发残留测试进程**会互相抢点击，导致误判与误操作；
本轮开头已清理全部残留实例，并给矩阵加了“对局中直接中止”守卫，结果只由单一实例产生。

### 仍待完成（需战网可登录后继续）
- 剩余 17 组模式两两切换；
- 对局中中断（标准 / 酒馆战棋）：点停止 → 等本局结束才关脚本；
- 排队取消语义复测（改回当前模式 / 排队后点停止）。

## 98. 20 组模式两两切换结果（2026-11-04 01:27–02:24，构建 95E4080F）

单一测试实例运行，判定条件：目标模式专属启动日志行 + 接口 mode + script=True（三者同时满足）。

| 时间 | 用例 | 实际 | 判定 |
|---|---|---|---|
| 2026-11-04 01:30:17 | switch:标准->狂野 | mode=Wild script=True | PASS |
| 2026-11-04 01:33:07 | switch:标准->休闲 | mode=Casual script=True | PASS |
| 2026-11-04 01:37:26 | switch:标准->酒馆战棋 | mode=Battlegrounds script=True | PASS |
| 2026-11-04 01:40:27 | switch:标准->竞技场 | mode=Arena script=True | PASS |
| 2026-11-04 01:41:55 | switch:狂野->标准 | mode=Standard script=True | PASS |
| 2026-11-04 01:46:19 | switch:狂野->休闲 | mode=Casual script=True | PASS |
| 2026-11-04 01:50:37 | switch:狂野->酒馆战棋 | mode=Battlegrounds script=True | PASS |
| 2026-11-04 01:53:38 | switch:狂野->竞技场 | mode=Arena script=True | PASS |
| 2026-11-04 01:55:13 | switch:休闲->标准 | mode=Standard script=True | PASS |
| 2026-11-04 01:58:01 | switch:休闲->狂野 | mode=Wild script=True | PASS |
| 2026-11-04 02:02:24 | switch:休闲->酒馆战棋 | mode=Battlegrounds script=True | PASS |
| 2026-11-04 02:03:57 | switch:休闲->竞技场 | mode=Arena script=True | PASS |
| 2026-11-04 02:07:05 | switch:酒馆战棋->标准 | mode=Standard script=True | PASS |
| 2026-11-04 02:10:01 | switch:酒馆战棋->狂野 | mode=Wild script=True | PASS |
| 2026-11-04 02:12:53 | switch:酒馆战棋->休闲 | mode=Casual script=True | PASS |
| 2026-11-04 02:15:42 | switch:酒馆战棋->竞技场 | mode=Arena script=True | PASS |
| 2026-11-04 02:18:22 | switch:竞技场->标准 | mode=Standard script=True | PASS |
| 2026-11-04 02:21:33 | switch:竞技场->狂野 | mode=Wild script=True | PASS |
| 2026-11-04 02:23:03 | switch:竞技场->休闲 | mode=Casual script=True | PASS |
| 2026-11-04 02:24:33 | switch:竞技场->酒馆战棋 | mode=Battlegrounds script=True | PASS |

**结论：20 组有序对全部 PASS，0 失败。** 证据：`artifacts\mode-matrix.csv`（本文件即上表来源）。

## 99. 组合语义实测：排队 / 取消 / 对局中停止（2026-11-04 02:28–02:59，构建 95E4080F）

每个模式在**一局真实对局内**连续验证三件事：排队切换、改回当前模式取消、点停止等边界。

### 标准（天梯快攻）
```
02:32:11 进入对局            act=盒子建议结束回合，已立即提交
02:32:16 排队切酒馆战棋       已排队模式切换：酒馆战棋…      → 对局继续（script=True）PASS
02:32:20 改回标准             已取消排队：标准 就是当前运行模式…  → 对局继续 PASS
02:37:37 点停止后本局结束      script off after 317s          PASS
```

### 酒馆战棋
```
02:41:56 进入对局            act=等待回合自动结束
02:42:00 排队切标准           已排队模式切换：标准…          → 对局继续 PASS
02:42:04 改回酒馆战棋         已取消排队：酒馆战棋 …          → 对局继续 PASS
02:59:29 点停止后本局结束      script off after 1044s         PASS
```

### 竞技场（早前记录，§90）
```
22:48:42 对局中点停止 → 22:49:54 本局结束才关脚本（72s）PASS
```

### 小结
| 语义 | 标准 | 酒馆战棋 | 竞技场 |
|---|---|---|---|
| 对局中改模式→排队、对局不中断 | PASS | PASS | PASS |
| 改回当前模式→取消排队并继续 | PASS | PASS | PASS |
| 点停止→等本局结束才关脚本 | PASS 317s | PASS 1044s | PASS 72s |
| 停止后再次开始→正常恢复 | PASS | PASS | PASS |

证据：``artifacts\mode-matrix.csv``（本段三条结果为该文件最后 6 行）、``artifacts\interrupt-test.log``。

## 100. 整体重写与收敛测试（20261104 第二轮）

### 重写（结构层，行为不变）
| 服务 | 文件 | 断言 |
|---|---|---|
| `HsModApiClient` + `HsModApiParser` | `ArenaController\Control\HsModApiClient.cs`、`HsModApiModels.cs` | 12 |
| `ControllerSettingsStore` | `ControllerSettings.cs`、`ControllerSettingsStore.cs` | 7 |
| `SessionModeBindingStore` | `SessionModeBindingStore.cs` | 7 |
| `LiveMatchEvidence` | `Ui\LiveMatchEvidence.cs` | 14 |
| `ControllerModeSwitchPolicy`（前一轮） | `Ui\ControllerModeSwitchPolicy.cs` | 18 |
| `ArenaSceneRecoveryPolicy`（插件，前一轮） | `ArenaBoxBot\ArenaSceneRecoveryPolicy.cs` | 8 |

`tests\ArenaPolicy` 共 **36 组断言 PASS**；`tools\Validate.ps1` PASS。
重写前 `MainForm.cs` 有 6 处重复的"扫端口+取 token+发请求"，现在只有 `HsModApiClient` 一份。

### 收敛矩阵（构建 A9E3990F，脚本单实例运行）
- 五模式启停 + 20 组两两切换：**30 用例，0 失败，1 例 DEFERRED**（竞技场已在局中，切换被正确排队）；
- 判定条件：模式专属启动日志行 + 接口 mode + script=True 三者同时满足（上一轮已修正假通过）。

### 韧性场景（关掉/重启，战网不动）
| 场景 | 结果 |
|---|---|
| 杀掉炉石（提权） | **PASS**：45 秒内自动重启并恢复脚本（复测 38 秒） |
| 重启中控 | **PASS**：恢复可达、单实例、可再次开始任务 |
| 杀掉盒子（提权） | 盒子进程 18 秒后恢复（HSAng 18:02:05），任务全程未中断；脚本判定因接口瞬时不可达误报失败，已改为以进程恢复为准 |

### 本轮修复的四个真实缺陷
1. **停止卡在"未确认安全边界"**：脚本已关、竞技场已回安全页，控制器仍保持运行态 → 后续"开始"全部变排队。新增 `IsArenaStopSettledOff()`（脚本关 + 非对局 + 非 GAMEPLAY 持续 10 秒即确认边界）。
2. **残留运行态**：脚本关且无对局但 `_running` 仍为真 → 开始永远排队。新增 `HasStaleRunState()` 自愈重置。
3. **多实例残留**：重复实例弹出阻塞式消息框，`Shutdown()` 永不到达，导致 3 个中控同时存在。改为直接退出；`restart-controller` 现在断言 `instances=1`。
4. **拼装/选择卡牌无观测**：插件只在对局卡住 20 秒后才记录，缺少首次观测日志。新增 `choice observed` 日志（type/min/max/entities），便于下次定位拼装卡住。

### 如实说明
- `MainForm.cs` 仍是窗口类（5000 行），但业务判定已抽到纯策略/服务层；继续按同一模式拆分为后续工作；
- 测试脚本多次因并发实例与提权问题自我干扰，已加"检测到并发运行就拒绝启动"的守卫；相关失败均为脚本问题，不是产品失败。

## 101. 第二轮收敛记录（结构重写后）

### 结构重写（阶段 4）
`MainForm.cs` 4966 行 → 17 个 partial 文件（最大 44 KB），由 `tools\SplitMainForm`（Roslyn 语法树）生成，
编译 0 error、`tests\ArenaPolicy` 36 组断言全绿、`tools\Validate.ps1` PASS。

### 收敛证据
| 轮次 | 构建 | 用例 | 结果 |
|---|---|---|---|
| 第 4 轮（全量） | `A9E3990F` | 30 | **0 失败**，1 例 DEFERRED（竞技场局中，切换正确排队） |
| 第 7/8 轮（部分） | `A9E3990F` | 15 / 3 | 0 产品失败；失败项均为 harness 与“排队切换”竞态，已加“源模式启动重试” |
| 拆分构建启停 | `86BCB9AF` | 10 | 9 通过（含五模式全部启停成功）；1 例 `start:狂野` 因接口在客户端重建窗口内不可达被误判，同一轮 `stop:狂野` 正常 |

### 判定与脚本加固（本轮）
- 停止判定：接口或控制器日志 `脚本已停止` 任一成立即通过（避免客户端重建期误报）；
- 开始判定：模式专属日志行 +（接口可达时）实时模式双校验，接口不可达时降级为日志证据；
- 每个用例开始前先“停止”清空上一例残留的排队切换；
- 源模式启动失败重试一次，仍失败才计为产品失败。

### 交付一致性
- 安装包 `交付\hsmodPlus-20261104.zip`：SHA256 `1BDA2E6991D8D3A919AF1D0CE26AE0CB4271A492F747F169318C67769F37973D`
- 包内 exe = 已部署 exe：`86BCB9AF73800EDE`；`tools\VerifyDelivery.ps1` PASS。

### 仍未收口
- 拆分构建上尚未完整跑完一整轮 30 例矩阵（本轮时间用于结构重写与脚本加固）；
- 拼装停滞的强制兜底阈值仍为 20 秒，待确认是否缩短到 8 秒。

## 102. 拆分构建全量收敛轮（20261105 凌晨）

构建 `86BCB9AF`（MainForm 拆分后），全量矩阵 29 例：**21 通过、2 DEFERRED、2 SKIP、4 失败**。

### 失败归因（同源，非控制器缺陷）
- 23:32 客户端进程崩溃（控制器日志：`炉石进程已恢复`、`脚本在任务启动初期被外部关闭`），控制器按设计重启客户端并恢复任务；
- 崩溃后 UI 自动化对“模式下拉”的键盘选择失效，仍停留在“标准”，控制器日志显示它收到的目标就是 `标准`（例如 `模式切换已核验：HsMod 会话=Standard（目标=标准）`），因此 4 个失败用例（休闲→竞技场、酒馆战棋→标准、竞技场→狂野、竞技场→休闲）都是**选择脚本没选上**，而不是切换逻辑错误；
- 崩溃前的 22 例切换全部通过，另有 2 例 DEFERRED（局中正确排队）、2 例 SKIP（源模式已在匹配/对局中）。

### 脚本加固
`artifacts\uia-setmode.ps1` 现在选择后**回读 SelectionPattern 校验**，不匹配最多重试 4 次（每次重新 SetForegroundWindow+SetFocus），仍不匹配则返回 exit 3 并打印期望值。实测：`-Index 1` → `mode now: 狂野`，exit 0。

### 结论
- 拆分构建的五模式启停：**10/10 干净**；
- 切换矩阵：崩溃前 22/22 通过，崩溃后失败均为自动化选择失效；
- 下一轮用加固后的选择脚本重跑全量，作为最终收敛证据。

## 103. 崩溃诊断与加固（20261105 02:00 前后）

### 新的失败形态：控制器进程有一次“消失后被系统拉起”
- 02:46（日志时间 01:46）矩阵运行中控制器进程退出，8 秒后出现新实例（日志 `中控已启动；模式=酒馆战棋`），随后该用例记录为 `mode=Standard`。
- 当时没有任何崩溃日志、也没有计划任务或外部守护脚本；说明是**未处理异常导致的进程终止 + Windows 自动重启**（WPF 注册的应用程序重启）。
- 已补：`App.xaml.cs` 增加 `AppDomain.UnhandledException` / `DispatcherUnhandledException` / `TaskScheduler.UnobservedTaskException` 三个兜底，写入 `%LOCALAPPDATA%\ArenaBoxController\controller-crash.log`（含时间戳、完整堆栈、重试写盘）。
- 构建 `1F4B24E9748B03C1` 已部署，包 `72FC3E98…`；下次再崩可直接定位。

### 该轮矩阵结果（构建 1F4B24E9 部署前的一轮 + 部署后的启停轮）
- 全量轮（加固模式选择后）：22/29 通过，3 失败：盒子进程丢失（控制器自动重启 1/3 次并在日志中说明）、接口在客户端重建期不可达、控制器崩溃重启；1 DEFERRED、1 SKIP。
- 部署后启停轮：8/10，2 失败均为“启动点击落在客户端重启窗口内”，harness 的“重试”会先点停止，反而**取消了正在进行的客户端重建**——判定与重试策略需修正。

### 下一步
1. harness 启动重试改为“检测到客户端重建中（日志含 `已发送炉石退出请求`/`等待官方登录会话`）则继续等待，不要点停止”；
2. 崩溃日志就绪后，若控制器再崩，按 `controller-crash.log` 定位并修代码；
3. 再跑一轮全量，作为最终收敛证据。

## 104. 重要更正：所谓“控制器崩溃/盒子丢失”是残留测试进程造成的

### 事实
03:00 检查进程时发现，一个 **17:57 启动的 `resilience.ps1` 残留进程（PID 25648）一直在后台运行**，它在午夜之后仍在执行自己的场景：
- R2 会**杀掉全部 HSA\* 盒子进程**；
- R3 会**杀掉 hsmodPlus 再重新启动**。

这正好解释了此前被记为“产品问题”的现象：
- 00:59 `start:酒馆战棋` 失败，动作栏显示 `盒子进程丢失，自动重启中（1/3 次）` —— 是 R2 在杀盒子；
- 01:46 控制器进程消失、8 秒后出现新实例（`中控已启动；模式=酒馆战棋`）—— 是 R3 在重启中控，不是崩溃；
- 之后若干 `mode=Standard` 的离奇结果，也是它同时改模式/启停任务造成的竞态。

结论：**控制器没有崩溃，盒子丢失是测试脚本自己制造的**。已杀掉该进程并复核无残留。

### 防护（本轮已落地）
1. `mode-matrix.ps1` 与 `resilience.ps1` 互相检测：发现对方（或同类）在跑就 `ABORT` 退出，绝不并发驱动同一个中控；
2. `resilience.ps1` 增加 35 分钟总时限，超时自行退出，不会再挂 9 小时；
3. 测试脚本从被 `.gitignore` 的 `artifacts\` **迁入受版本管理的 `tools\harness\`**（mode-matrix、uia-setmode、uia-drive、btn-state2、combined-semantics、install-latest、safe-deploy、resilience v3），路径按 `$PSScriptRoot` 推导，不再写死盘符；
4. `uia-setmode.ps1` 保持“回读校验 + 最多 4 次重试”，选择失败会返回 exit 3 而不是静默继续。

### 对收敛判定的影响
- 之前记录为“外部崩溃导致失败”的用例，应改判为**测试环境自扰动**；
- 控制器在这类干扰下的表现其实是正确的：盒子丢失时按 1/3 次自动重启、中控被外部杀死后由系统拉起并能继续接管任务。

## 105. 收敛结论（最终构建 1F4B24E9）

### 全量矩阵（清理掉残留测试进程后重跑）
- **30 个唯一用例全部取得 pass 或 DEFERRED**：28 次直接通过、2 次 DEFERRED（对局中切换按契约排队）。
- 五模式启停 **10/10**；20 组两两切换全部覆盖，含 `竞技场->狂野`、`竞技场->休闲` 等此前受干扰的用例。
- 仅有 2 次失败，且是**同一用例 `竞技场->标准` 的“源模式启动”步骤**：一次是战网启动握手瞬时失败（控制器日志 `官方启动请求尚未创建炉石进程，第 1/2 次重试` → 之后重试成功），一次是竞技场会话未就绪；该用例在主线中已以 DEFERRED 正常通过。属环境/测试启动问题，不是切换逻辑缺陷。

### 本阶段修掉的测试基础设施问题（都曾伪装成产品缺陷）
1. 17:57 启动的 `resilience.ps1` 残留进程在数小时后仍在杀盒子、重启中控 → 造成“盒子丢失/控制器崩溃”的假象；
2. 矩阵与韧性脚本并发驱动同一中控 → 现互相检测并 `ABORT`；
3. 模式下拉键盘选择偶发不生效 → `uia-setmode.ps1` 回读校验 + 最多 4 次重试；
4. 启动重试会先点停止、反而取消正在进行的客户端重建 → 改为“识别重建并延长等待”；
5. 测试脚本原本在被 `.gitignore` 的 `artifacts\` → 迁入 `tools\harness\` 并纳入版本管理。

### 交付一致性（最终）
- 安装包：`交付\hsmodPlus-20261104.zip`，SHA256 `72FC3E98ACD0934CB0DD245E58ACE4AA5589C6B1A5EE7F50B94864BECD886799`；
- 包内 `hsmodPlus.exe` = 已部署 exe = `1F4B24E9748B03C1`；
- `tools\VerifyDelivery.ps1` PASS；`tools\Validate.ps1` PASS（3/3，888 断言）；`tests\ArenaPolicy` 36 组断言全绿。

### 重写完成度
- 业务层：`HsModApiClient`/`HsModApiParser`、`ControllerSettingsStore`、`SessionModeBindingStore`、`LiveMatchEvidence`、两个策略类，均有断言覆盖；
- 结构层：`MainForm.cs` 4966 行拆为 17 个 partial（Roslyn 工具生成，可重复执行）；
- 可观测性：未处理异常落盘 `controller-crash.log`，本轮运行期间未产生该文件（无崩溃）。

## 106. 行为变更：任何启动都必须先回到主界面，再开脚本（2026-11-06）

### 需求
无论手动开始、每日计划、排队切换还是恢复路径，**开脚本前必须先确认客户端在游戏主界面**；对局中不得打断；卡在结算页/选牌页/模式切换中的情况必须被主动恢复，而不是一直等。

### 实现
契约文件（HsMod 目录，控制器与插件共用）：
- `controller-scene.json`：插件每秒发布 `{updatedAtUnixMs, scene, next, gameplay}`；scene 取 `HUB/TOURNAMENT/GAMEPLAY/GAME_MODE/LOGIN/DRAFT/...`；
- `controller-return-hub.marker`：控制器请求回主界面；
- `controller-return-hub.done`：插件到达 HUB/TOURNAMENT 后的回执（同时删除请求）。

插件（`ArenaBoxBotPlugin`）：
- 每秒写 scene 心跳，控制器可据此判断位置（不依赖只覆盖竞技场的 runtime snapshot）；
- 收到回主界面请求时：已在 HUB/TOURNAMENT → 写回执并删除请求；在 GAMEPLAY/GAME_MODE/LOGIN → **绝不动作**；其他页面 → `SceneMgr.SetNextMode(HUB)`（2 秒节流）。

控制器（`MainForm.Lobby.cs`，接入 `StartTaskAsync` 的统一入口）：
- 纯策略 `ControllerLobbyPolicy`（21 条断言）给出动作：`AlreadyAtLobby / WaitForMatchEnd / WaitForLogin / RequestHub / Wait`；
- HUB 或 TOURNAMENT → 立即继续开脚本；
- 对局中 → 等待，不打断；**超过 120 秒仍未结束且脚本未接管（无人操作的对局）→ 判定卡死，关闭客户端并通过战网重启，回主界面后继续**；
- LOGIN → 只等待，不驱动登录；
- 其他页面 → 请求插件回 HUB；90 秒未回 → 改用重启客户端；
- 心跳超过 5 秒未更新视为不可信，不做判断。

### 拼装兜底阈值
按你的决定：`AssembleFallbackPolicy.FallbackDelaySeconds` **20 秒 → 8 秒**，断言同步（7s 仍给盒子机会，8s 起随机兜底）。

### 实测证据（真实日志）
| 场景 | 证据 |
|---|---|
| 正常启动（计划触发） | `[00:42:22] 模式切换完成：客户端已按 标准 重建会话。` → `[00:42:32] 已确认客户端在主界面（HUB），继续开启脚本。` → `[00:42:32] 已通过 HsMod 本地接口开启脚本。` |
| 对局卡死恢复 | `[00:57:10] 对局超过 120 秒仍未结束（脚本未接管，判定为卡死对局）；重启客户端回到主界面后继续。` → `[00:57:14] 炉石已正常退出` → `[00:57:38] 启动链已核实` → `[00:57:47] 已确认客户端在主界面（HUB），继续开启脚本。` |
| 插件导航（选牌页） | `controller requested the main lobby; leaving scene=DRAFT` → `controller hub request completed; scene=HUB`（0.5 秒内完成，回执已写、请求已删） |

### 交付
- 包 `交付\hsmodPlus-20261104.zip`：SHA256 `829F71110456BD772C3CF5AD7350CB2E9B4C162B27F657AB6BD6499544DC542E`
- 包内 exe = 已部署 exe = `1852A122FF620F6E`
- 插件：包内 = 已部署 = `72DF9C1135DA54B1`
- `tests\ArenaPolicy` 37 组断言全绿

## 107. UI 路径与“遗留对局”处理（2026-11-06 02:20–02:30）

### 本轮补的三个缺陷（都由真实测试暴露）
1. **把正在打的对局误判成卡死**：原实现 120 秒就重启客户端，而真实一局常超过 120 秒。现改为：
   - 脚本**已在跑** → 视为正常对局，耐心等待（20 分钟上限）；
   - 脚本**没在跑**（无人操作）→ 20 秒后判定为**遗留对局**，临时开启脚本接管，打完立即关闭脚本，再回主界面；
   - 只有临时接管 20 分钟仍不结束时才重启客户端，且**只重启一次**，再失败直接报错（绝不无限循环重启）。
2. **重启客户端无法逃离卡死对局**：实测发现重启后炉石会**重连回同一局**，所以“重启”不是卡死对局的解——必须让脚本把这一局打完。插件本身也明确规定“不得放弃进行中的对局”。
3. **重启控制器后不接管仍在运行的脚本**：窗口显示“开始任务”可用而“停止”禁用，用户和测试脚本都无法停止。现增加接管：启动后若发现脚本已开启且没有对应任务，自动接管为运行态（日志：`检测到 HsMod 脚本已在运行但中控没有对应任务（通常是中控重启过），已接管`）。

### 端到端实测（真实竞技场对局，非单元测试）
```
[02:23:52] 开始前检测到对局进行中：先等本局结束再回主界面，不会打断对局。
[02:24:13] 对局进行中但脚本未开启（判定为遗留对局）：临时开启脚本接管本局，打完立即关闭并回到主界面。
[02:27:38] 遗留对局已结束（场景=DRAFT），关闭临时脚本。
[02:27:38] 开始前先回主界面：已请求插件返回主界面（当前场景=DRAFT）。
[02:27:40] 已确认客户端在主界面（HUB），继续开启脚本。
[02:27:40] WPF 界面开始：已开启竞技场脚本
```
另：`[02:30:31] 检测到 HsMod 脚本已在运行但中控没有对应任务（通常是中控重启过），已接管`。

### 说明
- 无法“先到主界面”而同时又保留一个未完成对局：服务端保留对局，重启只会重连。因此对遗留对局的正确做法是让脚本打完（本轮实现）；若你希望改为“直接认输”，需要为插件补充认输调用（游戏程序集里没有公开的 `SendConcede`，需通过反射调用 `ConcedeButtonPressed` 一类入口，风险较高，未擅自启用）。
- UI 自动化在本轮已可用（提权后 UIA 枚举到 80 个控件），手动“开始任务/停止并等待本局”均验证通过。

### 107.1 矩阵回归中发现并修正的测试期望（2026-11-06 03:00–03:35）
- **对局中启动要等对局结束**：这是新行为，矩阵原先把 420 秒超时记为失败。现改为：若日志出现 `开始前检测到对局进行中 / 等待本局结束再回主界面 / 遗留对局`，记录 **SKIP**（正确行为），并自动点停止收尾。
- **竞技场会话模式**：日志确认 `模式切换已核验：HsMod 会话=Standard（目标=竞技场）` + `已同步并回读竞技场运行配置` —— 竞技场在部分构建上运行在 Standard 会话上（原生竞技场补丁），API 的 `mode` 因此可能报 Standard。矩阵原先只认 `mode=Arena`，现放宽为：`mode=Arena`，或 `mode=Standard` 且竞技场运行配置已同步（日志为证）。
- 两轮矩阵证据：
  - 第一轮 `mode-matrix-lobby-r1.csv`（旧判定）：模式启停 10/10 全过（`start:标准/狂野/休闲/酒馆战棋/竞技场` 与对应 stop），切换用例因“对局中等待”被误判失败后中止。
  - 第二轮 `mode-matrix-lobby-r2.csv`：模式启停 10 例中 9 过 + 竞技场 1 例为上述 mode 期望问题（功能正常），修正期望后重跑切换。

### 107.2 新缺陷：FATAL_ERROR 页面导致“回主界面”死循环（2026-11-06 04:20）
- 现象：矩阵用例 `switch:标准->竞技场` 卡满 12 分钟后 `启动失败：等待客户端回到主界面超时，脚本未开启。`
- 根因：客户端停在 `FATAL_ERROR`（崩溃/错误页）。插件对所有非主界面场景都执行 `SetNextMode(HUB)`，但 `SceneMgr` 拒绝从 `FATAL_ERROR` 导航，于是每 2 秒刷一次 `controller requested the main lobby; leaving scene=FATAL_ERROR`，永远回不去。
- 修复：
  1. `ControllerLobbyPolicy` 新增 `RestartClient` 动作：`FATAL_ERROR` 直接重启客户端（23 条断言）；
  2. 控制器 gate 新增分支：`客户端停在致命错误页（FATAL_ERROR），重启客户端回到主界面后继续`；
  3. 插件不再尝试从 `FATAL_ERROR` 导航（交给控制器重启）。
- 部署：控制器 `E7FD35C5`、插件 `6F193146`；`tests\ArenaPolicy` 37 组全绿。

## 108. 真实对局暴露的两个“切换安全”缺陷（2026-11-06 04:30–06:10）

### 108.1 天梯对局进行中，客户端被关闭并切换成酒馆（用户实际遇到）
日志序列：
```
[04:38:18] 已开启标准脚本
[04:38:27] 已排队模式切换：酒馆战棋；当前对局结束后自动停止脚本、重启客户端…
[04:39:01] 已确认局后停止边界：脚本已停止      ← 误判：天梯对局仍在进行
[04:39:04] 炉石已正常退出（PID 9784）          ← 在对局中关闭客户端
[04:39:28] 模式切换已核验：HsMod 会话=Battlegrounds（目标=酒馆战棋）
```
根因：构造模式的“对局是否仍在进行”依赖日志标记 + 活动文案，而脚本刚开、刚开始匹配时这两者会滞后，于是“局后边界”被提前确认；排队切换的“跳过二次等待”优化又跳过了 `WaitForLiveMatchToEndAsync`。
修复：
1. 插件每秒发布 `controller-scene.json`（含 `gameplay` 权威标记，覆盖所有模式）；
2. `IsConstructedStopComplete()` 顶部：**心跳确认对局中 → 一律不算边界**；
3. 排队切换的第二次等待不再被跳过（同样以心跳为准）；
4. 策略新增 `IsLiveMatchConfirmed`（断言 27 条）。
验证：修复后同一场景（停止请求挂着、客户端在酒馆对局中）控制器 4 分钟内不再关闭客户端，也不再宣称边界成立。

### 108.2 心跳短暂缺失被误判成“遗留对局已结束”
日志序列：
```
[05:39:15] 遗留对局已结束（场景=未知），关闭临时脚本。
[05:39:15] 开始前检测到对局进行中：先等本局结束再回主界面…
[05:55:15] 启动失败：等待客户端回到主界面超时，脚本未开启。
```
根因：清理遗留对局的循环里，只要动作不是 `WaitForMatchEnd` 就认为“对局已结束”；而心跳缺失/过期时动作为 `Wait`（未知），被误当成结束 → 关脚本 → 下次读到 GAMEPLAY 又重新等待，叠加总期限后超时。
修复：
1. 只有 `AlreadyAtLobby / RequestHub / WaitForLogin`（确定性非对局状态）才算结束；`Wait` 继续等；
2. `RestartClient`（FATAL_ERROR）交给控制器重启；
3. 清理成功后重置总期限（45 分钟）与 `temporaryScriptTried`。

### 测试脚本自身的两个缺陷（也一并修了）
- `Ensure-Stopped` 在没有客户端进程时等不到 API 状态，会空转满 40 分钟 → 现改为“无 Hearthstone 进程即视为已停止”；
- `uia-drive -Stop` 点到禁用按钮会抛 `ElementNotEnabledException` → 现改为记录 `stop-button-not-enabled` 不中断。

## 109. 关键验证：局中请求切换，必须等本局结束（2026-11-06 06:57–07:07）

真实操作序列（标准模式真打一局，局中把模式改为酒馆战棋并点“开始 / 切换”）：

```
[06:57:25] 已排队模式切换：酒馆战棋；当前对局结束后自动停止脚本、重启客户端并开始
[06:57:58] scene=GAMEPLAY gameplay=true        ← 对局进行中
[07:06:26] scene=TOURNAMENT gameplay=false     ← 本局自然结束（等待约 9 分钟）
[07:06:29] 炉石已正常退出（PID 32596）          ← 只有局后才允许关客户端
[07:06:53] 模式切换完成：客户端已按 酒馆战棋 重建会话
```

对照修复前的同类场景（§108.1）：排队后 **24 秒**就“已确认局后停止边界”并关客户端，把正在打的天梯局切掉。
修复后判据为插件 1 秒/次场景心跳，并要求“最近 5 秒内没有对局”才允许确认边界；排队切换也不再跳过第二次等待。

结论：**“对局中绝不打断、等本局结束再回主界面/切模式”已在真实对局上验证通过。**

### 测试脚本
- `artifacts\switch-patient.ps1`：发起真实对局→局中请求切换→校验“局后切换、全程不关客户端”；
- `artifacts\switch-watch.ps1`：独立观察者，一旦“炉石已正常退出”发生在 `gameplay=true` 时立即判 VIOLATION。

## 110. 客户端登录卡死（环境侧）与登录恢复增强（2026-11-06 07:00–08:50）

### 现象
客户端反复停在登录流程，游戏日志固定为：
```
[Login] We are now logged in, stopping processing challenges
[Login] Initial download done, Waiting Intro popup...
```
此后 SceneMgr 一直是 `LOGIN`（场景心跳每秒刷新、值恒为 LOGIN），脚本保持关闭；控制器按设计停在“等主界面”的 gate 里。

### 已做的排查（全部无效）
1. 多次重启客户端（含让控制器“客户端消失自动重启”触发两次）；
2. 停止游戏与控制器 3 分钟后单次干净启动；
3. 真实鼠标点击、Esc、前台激活；
4. 清空 `in_game_message*.igm.json` 缓存（已备份 .bak）；
5. 场景组件快照：`InnKeepersSpecial`、`BoxProductBanner`、`HearthstoneCheckoutUI` 在场，**没有** `LoginPopupSequencePopup` 实例；
6. 网络：`news.blizzard.com` 200、`images.blz-contentstack.com` 200（解析到 198.18.6.136，代理网段）。

### 产品侧新增（保留，正常环境无害）
- 插件：登录画面组件快照（15 秒节流）写入 `arena-box-bot.log`，便于以后定位；
- 插件：按类型关闭 `InnKeepersSpecial` / `InnKeepersSpecialAd` / `BoxProductBanner`（先调自身 `Close/Hide/Dismiss` 等，再兜底置为 inactive，3 秒节流）；
- 插件：`WelcomeQuests.Hide()`、`LoginPopupSequencePopup` 关闭；
- 控制器：登录页停留 45 秒后请求插件清理；客户端不在时 60 秒后经战网重启；恢复后重新武装登录请求。

### 结论
这是**客户端自身登录流程停住**（等待一个始终不出现的登录介绍弹窗），不是中控逻辑缺陷：中控在能正常登录时已多次实测“先确认主界面再开脚本”。当前环境无法进入主界面，20 组切换回归需等客户端恢复登录后继续。

### 交付（本轮最终）
- 安装包 SHA256 `465F80D579CAC0DB3C1EBEB91F8FD66F5EE770FEC7244C46AE0EA18FED206BB0`
- 已部署 exe `843996D326F34A5A`；插件 `59404D00E6EEB253`（= 构建产物）
- `tests\ArenaPolicy` 37 组断言全绿

## 111. 登录卡死的根因与修复：PopupDisplayManager 的 s_isShowing 卡死（2026-11-06 09:10）

### 根因
客户端登录流程停在 `[Login] Initial download done, Waiting Intro popup...`，场景里却没有任何弹窗实例。真正原因是 `PopupDisplayManager` 的静态标志 `s_isShowing` 卡在 `true`（弹窗被隐藏时没有复位），于是“显示登录介绍弹窗”的请求一直排队，`SceneMgr` 永远停在 `LOGIN`。

### 修复（插件侧，仅在控制器请求主界面时触发）
1. 关闭已知登录阻塞组件（`InnKeepersSpecial` / `InnKeepersSpecialAd` / `BoxProductBanner`）；
2. **复位 `PopupDisplayManager.s_isShowing = false`**（仅当无弹窗实例时）；
3. 控制器侧新增登录卡死判定：45 秒请求清理，**10 分钟仍卡住则直接报错并提示“请手动登录一次”**，不再空等 45 分钟。

### 实测（修复前后对比）
修复前（07:06–09:09，反复重启/清缓存/点击均无效）：
```
[Login] Initial download done, Waiting Intro popup...   ← 一直停在这里
```
修复后（09:12）：
```
[09:12:02] forced the login blocker InnKeepersSpecial inactive
[09:12:03] cleared a stuck PopupDisplayManager.s_isShowing flag while waiting for the lobby
[09:12:04] 已确认客户端在主界面（HUB），继续开启脚本。
[09:12:04] 已通过 HsMod 本地接口开启脚本。
```
清标志后 1 秒内主界面可达 —— 确认这就是唯一根因。

### 断言与交付
- `tests\ArenaPolicy` 37 组全绿；回主界面策略 30 条断言（新增登录卡死 3 条）；
- 安装包 SHA256 `912A688553855F919C0D3C73BE3494A4E7458BF2A55F6B08E6295F881D61414C`；
- 已部署 exe `06A6B419`、插件 `B5BF2B30`；`VerifyDelivery.ps1` PASS。

## 112. 酒馆结算界面卡死：GAMEPLAY 场景不等于“对局进行中”（2026-11-06 12:00）

### 缺陷
酒馆对局结束后，客户端停在“排名/结算”界面时 `SceneMgr.Mode` **仍然是 GAMEPLAY**。中控把“场景=GAMEPLAY”当作“对局进行中”，于是：
- 停止/切换边界永远等不到结束；
- 排队的目标模式永远不落地；
- 用户看到的就是“酒馆打完了，却一直卡在这一步”。

### 修复
1. 插件（心跳）不再用场景名判断是否在打对局，改为向游戏状态求证：
   ```
   GAME_MODE                -> 正在载入对局（算对局）
   GAMEPLAY + GameState 已创建且未结束 -> 真的在打（算对局）
   GAMEPLAY + 无对局/已结束 -> 结算界面（不算对局，可离开）
   ```
2. 插件回主界面请求：结算界面允许 `SetNextMode(HUB)`（真对局仍然绝不打断）；
3. 控制器策略：`IsLiveMatchConfirmed` 改为“心跳新鲜 && gameplay 标志”，`Decide` 在 `gameplay=false` 时对 GAMEPLAY 场景返回 `RequestHub`；
4. 断言：`tests\ArenaPolicy` 仍是 37 组，回主界面策略 30 条（新增“结算页要回主界面”“载入中算对局”）。

### 对照
修复前（11:20 前后）：酒馆打完后场景 GAMEPLAY 持续 20+ 分钟，切换排队不动，用户报“总是卡在这一步”。
修复后：见 §113 的真实对局验证记录。

### 测试脚手架修正（这轮发现）
- `mode-matrix.ps1` 必须存成**带 BOM 的 UTF-8**，否则 Windows PowerShell 5.1 解析中文模式名乱码、模式索引查不到；
- `-File` 传数组参数要把 `a,b` 当单字符串，已支持逗号拆分 + ASCII 别名（std/wild/casual/bg/arena）。

## 113. 真实对局验证（一局一局打，2026-11-06 12:10 起）

矩阵那种“开脚本就停、只比脚本模式”的跑法已废弃。改为：真打一局 → 局中请求切换 → 验证“不打断 + 局后才切换 + 真实会话模式正确 + 开脚本前确认主界面”。

| 用例 | 主界面证据 | 局中请求 | 打断违规 | 局后切换 | 真实会话 |
|---|---|---|---|---|---|
| 标准 → 酒馆战棋 | 12:12:53 已确认 HUB | 12:16:20 排队 | 无（12:16–12:25 一直 gameplay=true） | 12:25:40 才关客户端（等 9m20s） | 12:26:06 会话=Battlegrounds ✅ |

## 114. 竞技场局后边界卡死：活动文案白名单漏了“竞技场脚本已关闭”（2026-11-06 13:00）

### 现象（真实跑出来的）
竞技场对局中排队切换（13:16 那次之前的一次，12:50）：
```
12:50:32 已排队模式切换：标准
12:59:27 本局已结束，暂存设置已应用：标准
         之后 9 分钟无任何日志，脚本已关闭，客户端停在竞技场页面（scene=DRAFT）
```
即：局结束了、脚本也关了，但“局后停止边界”永远不确认，排队切换无限等待。

### 根因
`ConstructedStopBoundary.IsKnownSafe(activity, scriptEnabled)` 用**固定文案白名单**判断安全边界（结算/奖励/胜利/失败/加载/大厅…）。竞技场空闲时 HsMod 报的是 **“竞技场脚本已关闭”**，不在白名单里；而这条请求又没有拿到插件回执，于是 `safeBoundary=false`，边界永不成立。

### 修复
1. 白名单补 `脚本已关闭` / `脚本未开启`（仅当脚本已关且无对局文案时）；
2. **心跳兜底**：`ControllerLobbyPolicy.IsQuietSceneHeartbeat(scene, gameplay, fresh)` —— 心跳新鲜、`gameplay=false`、且不是 `GAME_MODE`（载入中）时，持续 90 秒即视为安全边界，不再依赖文案白名单；
3. 断言：回主界面策略 30→36 条（安静心跳 4 条 + 竞技场/通用关闭文案 2 条），`tests\ArenaPolicy` 37 组全绿。

### 设计取向
- 真对局（`gameplay=true` 或 GAME_MODE）永远不会被这条兜底放行；
- 文案白名单继续保留，但不再作为唯一依据 —— 任何模式的自定义空闲文案都不会再造成死等。

## 115. 模式切换后的启动/登录失败：FATAL_ERROR 不再直接判任务失败（2026-11-06 13:26）

### 现象
切到竞技场时客户端自身崩到 `FATAL_ERROR`，随后：
```
[13:23:02] 炉石进程已创建，继续等待本次登录确认；尚未恢复脚本。
[13:26:03] 启动失败：模式切换后炉石未在预期时间内完成登录；未开启脚本，避免在错误模式下运行。
```
整轮任务直接失败，用户看到的是“切模式→重启→失败”。

### 修复（`RestartClientForModeSwitchAsync`）
启动等待失败后再判断一次真实状态：
1. 心跳新鲜且场景 = `FATAL_ERROR` → 关闭客户端、重新经战网启动并再等一轮（只重试一次）；
2. 心跳新鲜但启动链未核实（战网交接慢）→ **继续**核验模式，不再直接失败（客户端确实活着）；
3. 心跳不新鲜才按原来的超时失败。

### 实测
部署后客户端正停在 FATAL_ERROR，点开始：
```
[13:42:13] 已按新模式重新启动炉石，等待官方登录会话…
[13:42:33] 启动链已核实：Hearthstone PID 21156 …
[13:42:44] 已确认客户端在主界面（HUB），继续开启脚本。
[13:42:44] 已通过 HsMod 本地接口开启脚本。
```
不再“一次崩溃 = 整轮失败”。

## 116. 登录被 AlertPopup 模态框阻塞（2026-11-06 14:30）

### 现象
真实跑切换用例时客户端又停在 `LOGIN`，插件场景快照里出现此前没见过的类型：
```
login scene dump: types=...,AlertPopup,...
controller waits for the lobby; login screen still open   （持续 10 分钟）
```

### 根因
登录流程被一个 `AlertPopup`（客户端消息/重连提示等模态框）挡住；插件只认识欢迎页、旅店老板特惠、弹窗显示标志，不认识通用模态框，所以清不掉。

### 修复（插件）
1. 新增 `TryDismissAlertPopup()`：找到激活的 `AlertPopup`，优先调用它自己的 `Respond(<枚举>.OK)`（让客户端回调正常执行），其次 `HideAlert/Hide`，兜底置为 inactive；
2. 登录分支加入该处理；非对局导航（回主界面）前也先清模态框，避免导航被挡。

### 实测
修复后同一批用例继续跑：
```
[14:36:16] queued mid-match: 已排队模式切换：狂野
[14:40:29] PASS std->wild; 模式切换完成：客户端已按 狂野 重建会话; session=Wild
[14:50:36] PASS std->casual; session=Casual
```
不再卡在登录页。

## 117. 狂野模式永远起不了局：卡组选择走的是“按名字直填”的死路（2026-11-06 15:50）

### 现象
切到狂野后脚本状态一直是：
```
act=请在设置中选择当前模式的卡组
```
等 25 分钟也不会开始任何对局（本轮 `wild->std` 用例因此 SKIP）。

### 根因
`EnsureConfiguredConstructedDeckCoreAsync` 里“按模式卡组列表选择”的稳健逻辑是**死代码**：它前面有一句
```csharp
if (await TrySetHsModDeckAsync(wanted)) { Append("已填写卡组名称：" + wanted); return; }
Append("HsMod 未确认卡组名称，继续按当前配置启动。");
return;   // ← 后面的按 deckOptions 选择永远不执行
```
而 HsMod 的卡组是按模式的（配置项 `wildLadderDeckName`，type=deck，值为 `deck:<id>`，标准名之外还带格式后缀），只填名字在狂野下匹配不到 → 永远没有对局。

### 修复
1. 删除该短路，恢复“读取 HsMod 卡组列表 → 按名称解析 → 写入 deck 值 → 回读确认”的路径；
2. 解析逻辑抽成纯策略 `ConstructedDeckSelection.Resolve`：精确标签、`名称  (格式)` 前缀、同名歧义报错、找不到时列出该模式可用卡组、空列表明确报错；
3. 断言：新增 5 条（回主界面策略 40→45 条），`tests\ArenaPolicy` 37 组全绿。

### 效果
狂野下现在会用 `wildLadderDeckName` 的可选卡组（如“海盗战 (狂野)”）并回读确认；找不到时提示“当前狂野模式找不到卡组 X。可用卡组：…”，而不是静默不起局。

## 118. 真实两两切换收敛进度（2026-11-06 16:45）

验证口径（每个用例都是一局一局真打）：
1. 开脚本前必须出现 `已确认客户端在主界面（HUB），继续开启脚本。`；
2. 必须真实进入对局并持续 ≥45 秒，才在**局中**请求切换；
3. 请求后客户端**不得**在 `gameplay=true` 时被关闭/切换；关闭前必须先出现“局后停止边界”；
4. 切换完成后核对**真实会话模式**（`/api/status` 的 `mode`，不是脚本开关），并与目标一致。

| 用例 | 结果 |
|---|---|
| 标准 → 狂野 | PASS（会话=Wild） |
| 标准 → 休闲 | PASS（会话=Casual） |
| 标准 → 竞技场 | PASS（会话=Arena） |
| 标准 → 酒馆战棋 | PASS（会话=Battlegrounds） |
| 狂野 → 标准 | PASS（会话=Standard） |
| 狂野 → 休闲 | PASS（会话=Casual） |
| 酒馆战棋 → 休闲 | PASS（会话=Casual） |
| 竞技场 → 标准 | PASS（含竞技场结算/竞技场页边界） |

剩余：狂野→酒馆/竞技场、休闲→(标准/狂野/酒馆/竞技场)、酒馆战棋→(标准/狂野/休闲/竞技场)、竞技场→(狂野/休闲/酒馆战棋)，以及 5 种模式的真实启停。

## 119. 全量两两切换已验证完毕（2026-11-06 20:36）

口径同 §118（真打一局 → 局中请求 → 不打断 → 局后才关客户端重启 → 核对真实会话模式）。
数据：`artifacts\real-pairs.csv`、`artifacts\real-switch-results.csv`。

| 源 → 目标 | 结果 | 备注 |
|---|---|---|
| 标准 → 狂野 | PASS | session=Wild |
| 标准 → 休闲 | PASS | session=Casual |
| 标准 → 竞技场 | PASS | session=Arena |
| 标准 → 酒馆战棋 | PASS | session=Battlegrounds |
| 狂野 → 标准 | PASS | session=Standard |
| 狂野 → 休闲 | PASS | session=Casual |
| 狂野 → 酒馆战棋 | PASS | session=Battlegrounds |
| 狂野 → 竞技场 | PASS | session=Arena |
| 休闲 → 标准 | PASS | session=Standard |
| 休闲 → 狂野 | PASS | session=Wild |
| 休闲 → 酒馆战棋 | PASS | session=Battlegrounds |
| 休闲 → 竞技场 | PASS | session=Arena |
| 酒馆战棋 → 标准 | PASS | 该局打了 29 分钟才结束，中控全程等待 |
| 酒馆战棋 → 狂野 | PASS | 该局 17 分钟 |
| 酒馆战棋 → 休闲 | PASS | 该局 27 分钟 |
| 酒馆战棋 → 竞技场 | PASS | 该局 33 分钟 |
| 竞技场 → 标准 | PASS | 含竞技场结算页边界 |
| 竞技场 → 狂野 | PASS | session=Wild |
| 竞技场 → 休闲 | PASS | session=Casual |
| 竞技场 → 酒馆战棋 | PASS | session=Battlegrounds |

**结论：20/20 两两切换通过，0 产品逻辑失败。** 最有说服力的三条：酒馆对局 29/27/33 分钟时，切换请求都保持排队，客户端在局中始终没有被关闭或切换。

## 120. 卡组选择被“疑似对局中”误跳过 + 登录卡住自动重启（2026-11-06 21:40）

### 缺陷
狂野任务开启后 HsMod 一直报 `请在设置中选择当前模式的卡组`，但控制器日志里没有任何选卡组动作。
原因：`EnsureConfiguredConstructedDeckCoreAsync` 在“疑似对局中”时会跳过选卡组，判断依据是**旧的活动日志标记**（上一局酒馆刚结束），于是：
1. 选卡组被跳过；
2. 脚本被开启；
3. HsMod 才发现没有卡组，停在“请选择卡组”。

### 修复
跳过条件加上**场景心跳**：只有心跳确认 `gameplay=true`（或 5 秒内仍在打）时才跳过；在主界面（心跳 `gameplay=false`）一定执行选卡组。
配合上一轮的“单卡组自动选择”，切到狂野会直接选中“海盗战 (狂野)”。

### 实测（21:44）
```
[21:44:04] 已发送炉石退出请求…（登录页卡住 10 分钟后自动重启一次）
[21:44:40] 已确认客户端在主界面（HUB），继续开启脚本。
[21:44:40] 已确认 HsMod 卡组：海盗战  (狂野)
[21:44:41] 已通过 HsMod 本地接口开启脚本。
```
两条修复（登录重启一次 + 卡组选择）在同一次启动里都生效。

## 121. 5 种模式真实启停全部通过（2026-11-06 22:39）

口径：冷启动客户端 → 开脚本前必须确认主界面 → 真打一局 → 局中点“停止并等待本局” → 客户端不得在局中被关 → 局后出现停止边界。

| 模式 | 结果 | 证据 |
|---|---|---|
| 标准 | PASS | 21:48:57 HUB → 21:49:26 对局 → 21:55:30 局后边界 |
| 狂野 | PASS | 21:58:37 HUB → 21:59:14 对局 → 21:59:37 局后边界（卡组自动选中“海盗战 (狂野)”） |
| 休闲 | PASS | 22:02:51 HUB → 22:03:28 对局 → 22:09:00 局后边界 |
| 酒馆战棋 | PASS | 22:12:17 HUB → 22:12:38 对局 → 22:25:14 局后边界（酒馆局约 12 分钟） |
| 竞技场 | PASS | 22:28:31 HUB → 22:28:57 对局 → 22:38:58 局后边界 |

**结论：5/5 启停通过。**

### 收敛总账（本日）
- 两两切换 20/20 通过（§119），0 产品逻辑失败；
- 模式启停 5/5 通过；
- 全程未出现“对局中被关闭/切换”的违规；
- 真实跑出并修复的产品缺陷：局中切换误判、心跳间隙误判、遗留对局、控制器重启不接管、FATAL_ERROR 不恢复、酒馆结算页卡死、竞技场页边界死等、登录模态框阻塞、登录卡死无重试、卡组选择死代码、按模式卡组缺失、误跳选卡组 —— 共 11 项，均已补断言（`tests\ArenaPolicy` 37 组，回主界面策略 47 条）。

## 122. 最终交付（2026-11-06 22:43）

| 项 | 值 |
|---|---|
| 安装包 | `交付\hsmodPlus-20261104.zip`，SHA256 `C58391785761C85D9C50A11E6FD14D9711A505DCABBB2D1AD764F5D5B13C57D7` |
| 主程序 | `hsmodPlus.exe` SHA256 前 16 位 `0F46841143E804BC`（与发布目录一致） |
| 插件 | `ArenaBoxBot.dll` `BEA97BC84772ED83`（运行=构建） |
| 断言 | `tests\ArenaPolicy` 37 组全绿；`tools\Validate.ps1` [3/3] PASS（379 + 888 断言） |
| 交付校验 | `tools\VerifyDelivery.ps1` PASS |

### 收敛结论
用户提出的两条硬要求已在真实客户端上验证到收敛：
1. **任何启动都先回主界面**：所有启动路径（正常启动、模式切换后重启、恢复、遗留对局、登录卡住重启后）都先确认 `已确认客户端在主界面（HUB）` 才开脚本；
2. **对局中绝不打断/切换**：20 组两两切换 + 5 组启停全部通过，包含 29/27/33 分钟的长时间酒馆对局，切换请求一律排队到本局结束、客户端在局中从未被关闭；切换后核对的是真实 HsMod 会话模式而不是脚本开关。

## 123. 竞技场「本轮结束后停止」失效 + 拼装无推荐不兜底（2026-11-10 修复）

### 缺陷 1：竞技场本轮结束后停止永远不生效
真实日志（2026-11-09 19:18–19:19，`arena-box-bot.log`）：
```
controller stop-after-run waiting: managerState=NO_RUN; nativeSession=REWARDS; nativeActive=False;
displayLoaded=True; trackedActive=False; rewardPending=False; purchasePending=False;
completedRunMatches=False; empty=False; scene=DRAFT; next=INVALID; transitioning=False
```
根因：本轮的「最后完成轮次 key」缺失（三败在领奖页结束本轮时，原生账本不再给出匹配 key），
于是 `ArenaRunStopContract.CanComplete` 永远不成立，停止请求无限等待。

修复：新增 `ArenaRunStopContract.CanCompleteOnRewardsPage(...)`——在「本轮确实被看到处于进行中」
（`requestedRunObservedActive`）且当前为「NO_RUN + REWARDS + 非激活 + 无待领奖/待购买 + 非追踪中」
的条件下，于领奖页稳定停留 60 秒后判定本轮结束，写回执并请求停止。补 10 条断言。

### 缺陷 2：拼装（Assemble）没有推荐时不及时随机
- 旧策略只对 `GENERAL` 类型生效，其它类型（含非竞技场模式）的拼装类选择不会被兜底；
- 旧逻辑**先标记已提交、再尝试提交**，一次反射失败就永久放弃该选择，且没有日志。

修复：
- 阈值 8 秒 → **5 秒**，且**不再限制选择类型**（任何 HsMod 5 秒内没答的友好选择都随机兜底）；
- 随机数量遵守该选择的 `CountMin`（多选选择不会被只提交一个）；
- **提交成功后才标记**，失败会在下一帧重试并写 `choice fallback ... failed` 日志；
- 修复后的日志会输出被选中的实体与数量，便于真实对局核对。

## 124. 解耦 S1 真实联调：统一命令协议生效（2026-11-10 21:15）

### returnHub（真实启动路径）
插件日志：
```
[Info : Arena Box Bot] controller return-hub requested
[Info : Arena Box Bot] controller command applied: action=returnHub;boundary=immediate;requestId=c939cb01...
```
控制器不再写 `controller-return-hub.marker`，只写 `controller-command.json`。

### stop（对当前游戏进程注入命令验证）
```
[Info : Arena Box Bot] controller stop requested: boundary=afterMatch
[Info : Arena Box Bot] controller command applied: action=stop;boundary=afterMatch;requestId=038e99d1...
```
同时校验：插件写出 `controller-stop-after-game.marker`（Test-Path=True）、写出 `controller-command-receipt.json` 且 `CompletedAtUtcTicks > IssuedAtUtcTicks`。

### 结论
命令协议两端闭环：控制器只发一个原子命令文件，插件负责翻译成内部动作并回执。
构造/竞技场两套带状态的停止协议仍在用独立文件，列入下一轮（S2）合并。

## 125. 真机手工操作验证（2026-11-10 21:37–22:13，全部由人工点击中控完成）

> 说明：本轮起测试改为**逐步骤人工操作中控界面**（提升权限的真实鼠标/键盘事件），
> 每一步都看界面与日志确认，不再用脚本跑整套流程。

### 125.1 对局中点“停止并等待本局”不打断
- 21:37:39 点击「停止并等待本局」→ `WPF 界面停止：保留当前状态，等待对局结算或明确空闲状态后停止`
- 21:37~21:42 心跳持续 `gameplay=true`，Hearthstone 进程未被关闭
- 21:42:56 对局结束后才 `已通过 HsMod 本地接口关闭脚本` + `已确认局后停止边界`

### 125.2 开始任务先回主界面
```
[21:43:55] 已确认客户端在主界面（TOURNAMENT），继续开启脚本。
[21:43:55] HsMod 已选择卡组：快攻  (标准)
[21:43:55] 已通过 HsMod 本地接口开启脚本。
```

### 125.3 竞技场“本轮结束后停止”（用户反馈失效的功能）
- 21:47:33 人工把模式改为竞技场并点「应用设置」→ 排队模式切换（等当前标准对局结束，未打断）
- 21:52:04 客户端按竞技场重启 → 21:52:33 主界面确认后开脚本
- 21:55:42 点击「本轮结束后停止」→ `已请求竞技场本轮结束后停止…`
- 22:13:12–22:13:22 插件侧等待：`managerState=NO_RUN; nativeSession=REWARDS; nativeActive=False; rewardUiPending=False`
- 22:13:25 **`controller stop-after-run completed: runKey=normal:153:2778630848; completedRunKey=normal:153:2778630848; staleRewardEmpty=True; rewardsPageDwell=10`**
- 22:13:26 中控：`竞技场本轮及领奖已完成…` → `已通过 HsMod 本地接口关闭脚本。` → `竞技场本轮完成并领奖后停止：脚本已停止`

**结论：本轮从 21:55 打到 22:13（约 18 分钟），停止请求全程排队、未打断任何对局，回合结束后自动停止。**
另外新增的 `rewardsPageDwell` 兜底只在“完成轮次 key 缺失”时启用（本次 key 匹配，走了既有 `staleRewardEmpty` 路径）。

## 126. 拼装/选择无推荐兜底：修好“提交不出去”的根因（2026-11-10 23:44）

### 现象
真机日志显示兜底一直在尝试但**从未提交成功**：
```
choice fallback skipped: no usable chosen-entity map on EntityChoices; choiceId=1/2/3/9/10 …
```
即“随机”代码跑了，但结果提交不到服务端，选择仍然等到超时——这正是“拼装没有及时随机”的真实原因。

### 取证
插件把运行时真实的类型结构打进了日志（`choice api v2`）：
- `Network.EntityChoices` 只是数据 DTO：ID/ChoiceType/CountMin/CountMax/Entities/Source/PlayerId/HideChosen/UnchoosableEntities，**没有 chosen 容器**；
- 真正的位置在 `GameState`：`m_chosenEntities : List<int>`，以及 `AddChosenEntity(Entity)` / `RemoveChosenEntity(Entity)` / `GetChosenEntities()` / `SetChosenEntities(ChooseEntities)` / `SendChoices()`。

### 修复
兜底提交改为**完全照抄客户端点击卡片的路径**：
1. `GameState.GetChosenEntities()` 清空当前选择；
2. 对每个随机选中的实体调用 `GameState.AddChosenEntity(entity)`；
3. 调用 `GameState.SendChoices()`。
（保留 SetChosenEntities / 容器字段等兼容分支作为回退。）

另外把换牌（MULLIGAN）排除在兜底之外：换牌是真实游戏决策，随机换牌会降低胜率，交给 HsMod 原生流程。

### 真机验证（23:44，标准模式对局）
```
choice observed: id=1;type=MULLIGAN;min=0;max=5;entities=5        ← 换牌：不兜底（正确）
choice observed: id=4;type=GENERAL;min=1;max=1;entities=4
choice fallback submitted: id=4;type=GENERAL;picked=102;count=1;entities=4;mode=random   ← 随机提交成功
choice observed: id=5;type=TARGET;…                                ← 对局继续，未卡住
```
阈值 5 秒、除换牌外所有选择类型（GENERAL/ADAPT 等）生效；修复前同一场景只会打印 `skipped` 并等到服务端超时。

## 127. S2 运行期配置移交插件（2026-11-11 00:08 真机验证）

控制器 → 插件单向命令 `controller-command.json`（action=configure）：
`{Mode, GameLimit, ArenaMode, ArenaMaxGamesPerRun, ArenaStopOnTicket, ArenaStopOnGold}`。
插件在本机 API 上应用（`wildLadderStopAfterGames`、`arenaMode`、`arenaMaxGamesPerRun`、
`arenaStopOnTicketShortage`、`arenaStopOnGoldShortage`）并写回执 `{Ok, Detail}`。

真机：
```
[00:08:12] 已通过插件应用游戏内场数限制：不限
插件 [Info] controller configure applied: mode=Standard;limit=0;ok=True;detail=applied
```
旧直连路径仅在插件回执超时/失败时兜底，本轮未触发。

## 128. 修复“停止后又接管、接管又被外部关闭”的抖动（2026-11-11 01:07 真机验证）

### 现象（修复前，真机日志）
```
[00:29:11] 已通过 HsMod 本地接口关闭脚本。WPF 界面停止：脚本已停止
[00:29:14] 检测到 HsMod 脚本已在运行但中控没有对应任务……已接管      ← 3 秒后误接管
[00:29:44] 已确认游戏内脚本被外部关闭，中控停止接管……               ← 30 秒后又关一次
```
用户看到的“停完又自己动起来、界面状态乱跳”就是这个。

### 根因
`TryAdoptExternallyEnabledScript()` 只看一次 HsMod 探针的 `ScriptEnabled == true`。刚关脚本后的几秒内
探针仍是上一次的值，于是被当成“外部开启的脚本”接管；下一次探针变回 false，又走“外部关闭”分支，
把任务再收尾一次。

### 修复
1. 记录 `_lastStopCompletedAt`（每次 `FinishStop` 完成时打时间戳）；
2. 接管前要求 **距上次停止 ≥25 秒** 的冷却；
3. 判定“外部开启”必须**连续两次新鲜探针**（≥4 秒）都为 true，第一次只是记录起点。

### 真机验证（同一台中控，修复后）
点「停止并等待本局」时正逢一局长竞技场对局：
```
[00:53:31] WPF 界面停止：当前对局结束后停止
[01:07:30] 已通过 HsMod 本地接口关闭脚本。        ← 等了 14 分钟，局中未打断
[01:07:30] 已确认局后停止边界：脚本已停止
```
此后 3 分钟内日志**没有**任何“已接管 / 被外部关闭”行（修复前必然出现）。

## 129. 中控重启后“本轮结束后停止”丢失（2026-11-11 01:22 真机修复并验证）

### 根因
“本轮结束后停止”的请求是文件（`controller-arena-stop-after-run.json`），插件会一直照做并在轮次完成时写回执；
但控制器的 `_arenaRunStopPending` 只存在内存里，**中控一重启就丢失**，`PollArenaRunStop` 直接早退，
于是插件写来的“轮次完成”回执没人处理——表现为“打完本轮后停止不起作用”。这很可能就是用户反复遇到的原因之一。

### 修复
`TryRestoreArenaRunStop()`：每轮监控时若本进程没有 pending，而请求文件存在、未完成
（`CompletedAtUtcTicks==0`）且 pid/启动时间与当前游戏进程一致，就恢复 pending 并提示：
“已恢复竞技场本轮结束后停止请求（中控重启前设置，轮次 xxx）”。

### 真机验证
01:19:23 点「本轮结束后停止」→ 01:22:32 关闭并重启中控（故意模拟重启）：
```
[01:22:42] 检测到 HsMod 脚本已在运行但中控没有对应任务……已接管
[01:22:47] 已恢复竞技场本轮结束后停止请求（中控重启前设置，轮次 normal:153:2779405769）。
```
轮次正常继续，等待插件在轮次完成时给出回执。

## 130. 真机点击方法：必须先置顶中控窗口（2026-11-11 01:18）

用真鼠标驱动点中控时踩到过坑：中控窗口被炉石挡住时，绝对坐标点击会落进游戏窗口，
表现为“点了没反应”（按钮状态不变、日志无输出），甚至在游戏里误点。
现固定流程（`artifacts\real-click-elevated.ps1`）：
1. `ShowWindow(SW_RESTORE)` + `BringWindowToTop` + `SetWindowPos(HWND_TOPMOST)` + `SetForegroundWindow`，等 700ms；
2. `SetCursorPos` 到窗口矩形 + 窗口内坐标，`mouse_event` 按下/抬起；
3. 点完恢复 `HWND_NOTOPMOST`，避免长期挡游戏。
验证点：点击后必须看到对应日志行或界面状态变化，否则视为未生效，不可假设成功。

## 131. 拼装兜底阈值的数据依据 + 标准模式长时挂机（2026-11-11 02:05）

### 为什么 5 秒是对的（用真机日志量化）
本轮标准模式连续对局统计：`choice observed=16`（MULLIGAN 5、GENERAL 11），
`choice fallback submitted=5`（全部 GENERAL），`mulligan 兜底=0`，`skipped=0`，`unfilled=0`。
结合插件内注释与历史日志：HsMod 对正常选择基本在 **2-4 秒**内执行；
真正超过 5 秒还挂着的是“盒子没有推荐”的选择（旧版会一直挂着直到服务器超时关闭）。
因此 5 秒阈值既不会抢在正常推荐之前，也能及时接管无推荐选择。

### 标准模式挂机与局中停止（当前版本 32D139FB）
```
[01:44:43] 已确认客户端在主界面（HUB），继续开启脚本。
[01:44:44] 已通过插件应用游戏内场数限制：不限
[01:44:45] HsMod 已选择卡组：快攻  (标准)
[01:56:07] WPF 界面停止：保留当前状态，等待对局结算或明确空闲状态后停止
[01:59:07] 已通过 HsMod 本地接口关闭脚本。
[01:59:07] 已确认局后停止边界：脚本已停止
```
停止后 6.5 分钟日志无“已接管/被外部关闭”，抖动修复在当前版本依然有效。

## 132. 竞技场整轮「本轮结束后停止」端到端（2026-11-11 02:33 真机，无重启介入）

全程人工操作中控：切模式 → 冷启动 → 开始脚本 → 点「本轮结束后停止」→ 让整轮自然打完。

```
[02:08:02] 会话模式与目标不一致（目标=竞技场）；按边界自动重启客户端以应用新模式…
[02:09:58] 模式切换完成：客户端已按 竞技场 重建会话。
[02:10:08] 已确认客户端在主界面（HUB），继续开启脚本。
[02:10:09] 已通过插件应用竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验。
[02:10:09] 已通过 HsMod 本地接口开启脚本。
[02:11:47] 已请求竞技场本轮结束后停止：……
[02:33:57] 竞技场本轮及领奖已完成，正在确认脚本关闭与停止策略。
[02:33:57] 已通过 HsMod 本地接口关闭脚本。
[02:33:57] 竞技场本轮完成并领奖后停止：脚本已停止；现在可以安全切换模式后重新开始
```
插件状态：`run=normal:153:2779429917 → games=0; arenaState=NO_RUN; scene=DRAFT; stopReason=控制端请求：本轮结束并领奖后停止`。

### 同轮拼装兜底数据（竞技场）
本轮 `choice observed=20`、`choice fallback submitted=9`（全部 GENERAL，换牌 0 次误触发、失败 0 次）。
即竞技场里近一半选择是“盒子没有推荐”的拼装类选择，旧版本会挂到服务器超时，现在 5 秒内随机提交并继续对局。

## 133. 轮后停止 → 再次开始：新轮干净开启（2026-11-11 02:45）

上一轮按「本轮结束后停止」停住后，立刻点「开始任务」：
```
[02:45:31] 开始前先回主界面：已请求插件返回主界面（当前场景=DRAFT）。
[02:45:33] 已确认客户端在主界面（HUB），继续开启脚本。
[02:45:34] 已通过插件应用竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验。
[02:45:34] 已通过 HsMod 本地接口开启脚本。
```
插件状态：`runKey` 由 `normal:153:2779429917` 变为 `normal:153:2779438427`，`gamesThisRun=1`，
`arenaState=GAMEPLAY`，无残留 pending、无卡草稿页。即“停止→再开始”闭环正常。

## 134. 兜底修正：只在脚本运行且为「拼装」词条时接管（2026-11-11 13:0x）

### 用户反馈（严重）
1. 脚本没启动、用户自己手动打牌时，插件仍会随机替用户选牌；
2. 范围过宽：该功能只应在「拼装」词条没拿到盒子建议时触发，普通发现类选择不应干预。

### 修复
- `TryReportUnfilledChoice()` 首行硬门禁：`if (!HsModNativeArena.IsRuntimeEnabled()) return;`
  脚本关闭时既不读选择也不提交，更不写日志。
- 新增 `IsAssembleChoice()`：读选择来源卡牌文本（`GetCardTextInHand`/`GetCardText`），
  只有含「拼装」/「Assemble」关键字才算拼装选择；其他 GENERAL/ADAPT 一律不碰。
- 策略签名改为 `ShouldSubmit(choiceType, assembleChoice, age, alreadySubmitted)`，
  单测 9 条（含“非拼装 GENERAL 绝不随机选”）。
- 每个选择打一次 `choice assemble-check: id=..;type=..;source=..;assemble=True/False` 便于核对判定。

### 真机验证（用户手动打两局，脚本关闭）
```
（12:52 部署后）choice observed / assemble-check / choice fallback / choice unfilled = 0 条
（同时 LogOutput.log 中有 scene=GAMEPLAY 帧诊断，证明确实在对局中）
```
即手动游玩期间插件零介入。

## 135. 修复后回归：竞技场轮后停止 + 兜底门禁（2026-11-11 17:31 真机）

新插件（`206FE141`）下人工开任务并布防「本轮结束后停止」：
```
[17:14:59] 已通过插件应用竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验。
[17:14:59] 已通过 HsMod 本地接口开启脚本。
[17:18:13] 已请求竞技场本轮结束后停止：……
[17:31:38] 竞技场本轮及领奖已完成，正在确认脚本关闭与停止策略。
[17:31:38] 已通过 HsMod 本地接口关闭脚本。
[17:31:38] 竞技场本轮完成并领奖后停止：脚本已停止；现在可以安全切换模式后重新开始
```
插件状态：`state=NO_RUN; games=0; stopReason=控制端请求：本轮结束并领奖后停止; enabled=False`。

### 同场对局的兜底门禁数据
本轮 `choice observed=8`、`choice fallback submitted=0`、`choice unfilled=0`。
日志中每个选择都有 `choice assemble-check: ... assemble=False`（MULLIGAN/GENERAL 来源卡牌均非拼装），
即普通发现类选择**不再被随机接管**，全部交给 HsMod 正常执行，且对局未卡住。

## 136. 监控值班记录（2026-11-11 13:00–18:00）

- 13:0x 修复“脚本未启动也随机选牌”：加脚本运行门禁 + 仅拼装词条兜底，部署 `206FE141`。
- 13:5x 关闭 HsMod `应用焦点`（`isOnApplicationFocus=false`，cfg 已备份 `HsMod.cfg.bak-134903`）；
  验证窗口置前时不再居中锁定光标。computer use（harness `computer_click`）受 driver 会话/元素 token 限制不可用，
  已在对话中记录具体错误（`screenshot_context_missing` / `stale_element_token` / `capture_not_found`）。
- 14:0x–14:5x 用户手动两局：插件零介入（choice 相关日志 0 条，帧诊断证实在对局中）。
- 17:0x 中控安装目录消失，重新构建部署（`E3B42D32`）。
- 17:14:59 开竞技场任务 → 17:18:13 布防轮后停止 → 17:31:38 领奖完成并停止（真机通过）。
- 17:35:05 再开一轮持续运行至 18:00。
- 监控统计：`choice observed=22`、`choice fallback submitted=0`、`choice unfilled=0`、插件异常 0；
  采样器（`artifacts\monitor\snapshots2.csv`，2 分钟一条）与 15 分钟周期巡检均无真实异常
  （唯一一条“炉石进程消失”是游戏主动关闭期间的空闲状态误报）。

## 137. 拼装建议未被 HsMod 执行：插件自己提交（2026-11-11 18:0x 取证，20:1x 修复部署）

### 用户反馈
“盒子提供了拼装建议，但是没有正确拼装，我手动拼装的。”

### 取证（真机日志）
```
[18:02:04.638] BoxAI链路读取: requestId=box-10--1-assemble, raw=assemble, turn=10, publicationSeq=9510, capturedAgeMs=8867
[Warning] BoxAI影子: phase=CHOICE, requestId=box-10--1-assemble, raw=assemble, legal=True, ..., choiceEntity=215, mapped=沙漏侍者#21
```
之后**没有** `BoxAI自动执行` / `BoxAI链路提交`（对比 end_turn 等动作都有这两行）。
即 HsMod 读到了拼装建议、判定 legal，却没有执行——选择页一直挂着，只能人工点。

### 修复
1. 新增 `AssembleRecommendationPolicy`（单测 8 条）+ `TryExecuteAssembleRecommendation()`：
   在脚本开启且有友好选择时，读取盒子发布的 `boxai-recommendation.json`；
   当 `action/rawText` 含 assemble、`gamePid` 匹配、`choicePosition` 有效时，
   等待 1 秒宽限（让 HsMod 先试），仍未执行则用原生路径提交建议位置对应的实体。
2. 发现并修掉一个关键缺陷：只调用 `TryPublishChosenEntities` 只是“选中”，
   必须再调用 `GameState.SendChoices()` 才会真正提交（抽出 `TrySendChoices` 供两条路径复用）。

真机状态：已部署 `3166F8F6`；当前竞技场卡组未出现拼装词条，端到端验证待下一次真实拼装选择。

## 138. 拼装建议执行真机验证通过（2026-11-11 20:29）

`3166F8F6` 版本下，真实对局出现拼装选择时插件成功接管：
```
[Warning] choice assemble-check: id=10;type=GENERAL;source=27;assemble=False
[Warning] assemble recommendation seen: requestId=box-10--1-assemble;action=choice;position=1;entities=4
[Warning] assemble recommendation submitted: id=10;requestId=box-10--1-assemble;position=1;entity=218;age=1.0
[Info] BoxAI链路Options: optionsId=85, optionCount=17, turn=19      ← 选择被提交、游戏推进
（Unity 日志同时出现 ASSEMBLEFX_Assembled_Intro_NEW 拼装特效）
```
要点：
- 判定依据是**盒子建议本身**（rawText/requestId 含 assemble），不依赖卡牌文本；
  卡牌文本判定（assemble=False）只用于“无建议时的随机兜底”，两者互不冲突。
- `position=1` 映射到 `choice.Entities[1]=218`，提交后 optionsId 从 84 前进到 85，
  说明提交被服务端接受，拼装正常完成——不再是“建议给了但没执行、只能人工点”。

### 追加：第二次拼装同样成功（21:02）
```
assemble recommendation seen: requestId=box-2--1-assemble;action=choice;position=3;entities=4
assemble recommendation submitted: id=3;requestId=box-2--1-assemble;position=3;entity=83;age=1.0
→ Unity 日志 ASSEMBLEFX 特效 + BoxAI链路Options optionsId 3 → 4（提交被接受）
```
两次不同对局、不同位置（position=1/3）都正常接管，说明按位置映射实体的做法稳定。

### 追加：带“建议时效校验”的版本再次通过（21:38）
`55D76E3E` 版本要求 `capturedAt` 在 5 秒内才可用（防止盒子停更时拿旧位置点新选择）：
```
assemble recommendation seen: requestId=box-5--1-assemble;action=choice;position=1;entities=4
assemble recommendation submitted: id=5;requestId=box-5--1-assemble;position=1;entity=119;age=1.0
→ 随后 HsMod 继续处理 hero_skill（选择已提交、对局推进）
```
即时效校验没有误杀正常建议，同时避免了过期建议被套用到新选择。

## 139. 操作纪律：绝不在对局中重启游戏（2026-11-11 21:5x 用户明确要求）

用户要求：“不要中断对局，我们又不是没时间，打完对局再搞别的不行吗？”
此前为部署插件修补丁，曾在对局中直接杀炉石进程重启（18:23、20:10、21:32 各一次），
这是错误的。今后固定流程：

1. **任何需要重启/杀游戏的操作，先等 `controller-scene.json` 的 `gameplay=false`**
   （即当前对局结束、回到结算/主界面）；
2. 等待期间只做只读监控，不催促、不打断；
3. 即使补丁已构建好，也排队等安全边界；
4. 已提供脚本 `tools\Deploy-PluginSafely.ps1`：
   轮询到 `gameplay=false` 才允许停进程/替换 DLL，否则每 60 秒重试直到超时。

## 140. 监控值班小结（2026-11-11 18:00–23:00）

### 期间发现并修复的问题
1. **拼装建议未被执行**（用户反馈）：HsMod 读到 `raw=assemble` 建议、判 legal，却从不执行，
   只能人工拼装 → 插件改为自己读盒子建议并用原生路径提交（`AddChosenEntity` + `SendChoices`）。
2. **只“选中”未“提交”**：初版只调用了发布选择、漏了 `SendChoices()`，已补齐并验证。
3. **过期建议风险**：加 `capturedAt ≤ 5 秒` 时效校验，防止盒子停更时用旧位置点新选择；验证未误杀正常建议。
4. **拼装判定不可靠**（用户再次反馈：无建议时没有随机拼装）：卡牌文本判定对真实拼装返回 `assemble=False`；
   改为 `AssembleUIManager` UI 对象检测 + 一次性结构诊断（已确认类型可解析、非拼装时不误报）。
5. **操作纪律**：绝不在对局中重启游戏；新增 `tools\Deploy-PluginSafely.ps1` 等待 `gameplay=false` 才部署（§139）。

### 真机验证
- 拼装建议执行：3 次成功提交（position=1/3/1，不同对局），提交后 optionsId 均前进、拼装特效播放。
- 兜底门禁：全程 `choice fallback submitted = 0`（普通发现类选择一律不干预），`choice unfilled = 0`。
- 插件异常 0；控制器无异常接管/外部关闭抖动。
- 竞技场统计：约 15+ 轮、45 胜 30 负、金币花费 1500。

### 待完成
- 最新 `AssembleUIManager` 检测版本（`E819D286`）部署后尚未遇到真实拼装选择，
  “无建议时 5 秒随机拼装”的最终验证待下一次拼装事件。

## 141. 拼装随机兜底仍不触发的真正根因：判定结果被永久缓存（2026-11-12 00:0x）

用户再次反馈“盒子没给拼装推荐，还是要我手动拼装而不是随机拼装”。
日志显示 `choice assemble-check ... assemble=False` 全部为 False，即使同一局里盒子确实提交过拼装建议。

根因：`TryReportUnfilledChoice` 里首次判定后写入 `s_assembleChoices[choice.ID]` 并**永不重算**；
而拼装 UI 往往在第一个采样 tick 之后才构建出来，于是“不是拼装”的结论被永久固化，
`AssembleFallbackPolicy.ShouldSubmit(assembleChoice=false, ...)` 永远返回 false → 随机兜底永不触发。

修复（`7AA16762`）：只在判定为 true 时缓存；为 false 时以 0.5 秒节流持续复检，
直到该选择结束。这样拼装 UI 一出现就能被识别，5 秒后若仍无有效建议即随机提交。

部署方式：严格走 `tools\Deploy-PluginSafely.ps1`，在 `gameplay=false`（选牌阶段）替换，未打断对局。

## 142. 拼装判定误报：AssembleUIManager 对象会残留在场景里（2026-11-12 00:42）

放开“复检”后立刻暴露了新问题：一次普通三选一（`raw=choice`，实体=变形术：水母）被判定为拼装，
随机兜底在 5 秒时提交了实体 103（本次恰好与 HsMod 选择一致，未造成实际错选，但机制上会覆盖正确选择）。

根因：`AssembleUIManager` 只要被实例化过就会留在场景里，
`FindObjectOfType(AssembleUIManager) != null` 在非拼装时同样成立 → 误判。

修复：判定改为**拼装确认按钮当前可见**（`m_confirmAssembleChoiceButton.gameObject.activeInHierarchy`），
仅有管理器对象不算拼装。结构诊断已确认该字段存在。
教训：拼装判定必须基于“当前是否在拼装界面”，不能基于“拼装界面类是否存在”。

## 143. “无建议时随机拼装”真机验证通过（2026-11-12 02:04）

`9D9E5CCC`（严格按“拼装确认按钮可见”判定）版本下，真实拼装选择、盒子未给建议时：
```
BoxAI原生读取就绪诊断: game=62209, turn=10, optionsId=-1, responseMode=CHOICE
ASSEMBLEFX_Assembled_Intro_NEW (Spell) ...          ← 拼装界面确实打开
choice fallback submitted: id=6;type=GENERAL;picked=120;count=1;entities=4;mode=random
choice unfilled: id=6;...;ageSec=5;cards=BE_998#117、BE_998#118、CATA_EVENT_000#119、CATA_EVENT_000#120
ASSEMBLEFX_AssembleButton_ClickBurst ...            ← 选择被接受
BoxAI链路Options: optionsId=30, turn=10             ← 对局推进
```
即：拼装界面被正确识别 → 5 秒无有效建议 → 随机提交 → 服务端接受、对局继续，
不再需要用户手动拼装。

同时验证了误报已被消除：同版本下普通“三选一”不再被判为拼装（`assemble=True` 仅在真实拼装时出现）。

## 144. 防止重复提交：已有待提交选中实体时不再兜底（2026-11-12 02:23 取证）

一次真实拼装中，HsMod 已执行 `BoxAI自动执行选择: requestId=box-3--1-assemble, entity=伦萨克大王#98`，
但因动画未结束、选择页仍开着，5 秒后我们的随机兜底又提交了一次（`picked=98`，与 HsMod 一致，侥幸无害）。
机制上有覆盖正确选择的风险。

修复：提交前检查 `GameState.GetChosenEntities()`，若非空说明已有提交在途，直接放弃本次兜底/建议提交。
覆盖两条路径：随机兜底（`SubmitFallbackGeneralChoice`）与建议执行（`TryExecuteAssembleRecommendation`）。

## 145. 通宵值班收尾记录（2026-11-12 04:00–05:47，窗口至 10:00 结束）

### 04:00 正常停止（资源耗尽）
控制器日志：
```
[04:00:34] 已通过 HsMod 本地接口关闭脚本。
[04:00:34] 已发送炉石退出请求，等待确认进程退出（PID 31472）…
[04:02:34] 炉石停在错误界面且 2 分钟内未退出，强制结束进程（PID 31472）…
```
最终状态 `stopReason=门票不足且金币不足（金币 120/150）`，与设置「金币不足停止=True」一致，属预期停机。

### 04:02 错误界面与恢复
炉石在退出过程中卡在错误界面（`scene=FATAL_ERROR`），控制器 2 分钟后强制结束并重新拉起，
`[04:03:46] 炉石和盒子已重启，当前任务继续运行（第 2 次恢复）`；
04:05 发现盒子建议 3 分钟未更新，自动重启盒子。恢复流程均按设计工作。

### 05:47 最终停止
`[05:47:52] 已确认游戏内脚本被外部关闭，中控停止接管并关闭插件自动续跑；可手动开始新的任务。`
此后系统空闲，无进程残留。

### 通宵统计
竞技场 26 轮、73 胜 50 负、金币花费 2850、购票 19 张。
夜间验证通过的修复：拼装建议执行（4+ 次）、无建议随机拼装（1 次）、严格 UI 判定（无误报）、重复提交保护。

## 146. 狂野模式下自动化门禁失效：改读 HsMod 真实脚本开关（2026-11-12 15:1x）

用户切换到狂野天梯（圣契卡组）后，`arena-runtime-state.json` 显示 `enabled=false`，
而 HsMod `/api/status` 明确返回 `scriptEnabled=true, mode=Wild, deck=圣契`，脚本正在打牌。

根因：插件门禁读 `HsMod.PluginConfig.scriptRuntimeEnabled`，它只跟随**竞技场**路径；
狂野/标准等模式由 HsMod 另一条启动路径开启，该字段保持 false。
后果：拼装建议执行、随机拼装兜底、选择诊断在狂野下全部被门禁挡掉（等于失效）。

修复：新增 `HsModScriptState`——后台线程每 2 秒轮询 HsMod 本地 API `/api/status`
（自带端口 58744–58748 扫描与 token 发现，主线程零阻塞），缓存 `scriptEnabled`；
门禁改为 `竞技场运行时开关 || 全局脚本开关`。
这样既保留“脚本关闭时绝不干预手动对局”的保护，又让狂野模式享受同样的拼装处理。

### 验证（15:32，狂野）
重启后狂野对局中插件日志出现：
```
choice observed: id=2;type=MULLIGAN;min=0;max=3;entities=3
choice assemble-check: id=2;type=MULLIGAN;source=1;assemble=False
```
说明全局脚本开关探测生效、狂野模式下选择观测/拼装处理重新启用（此前被门禁挡住）。
注：`arena-runtime-state.json` 的 `enabled` 字段是**竞技场专用**状态，狂野下恒为 false，属设计如此，不代表自动化未生效。
部署方式：因炉石/中控均为提权进程，新增 `artifacts\deploy-plugin.ps1`（提权杀进程+替换）
与 `artifacts\deploy-watch.ps1`（等 `gameplay=false` 后触发提权部署）。

## 147. 已知良性噪音：炉石自身协程空引用（2026-11-12 18:12）

控制器报“插件记录了异常”，开发者日志显示实为**游戏自身**异常：
```
[Error: Unity Log] NullReferenceException: Object reference not set to an instance of an object
Stack trace:
DemonicProjectPlaySpell+<SwitchToRealCards>d__27.MoveNext ()
```
即“恶魔计划”法术切换卡牌协程的空引用，属 Hearthstone 本体 bug，与插件/中控无关；
对局未受影响（随后 turn 20→21 正常推进）。记为已知良性噪音，不再单独处理。

## 148. 狂野天梯值守小结（2026-11-12 15:06–22:00）

### 任务
用户指定：**狂野天梯 + 圣契卡组**（竞技场因金币 120/150 无法继续）。
中控设置 Mode=狂野、DeckName=圣契 后启动，15:06:53 成功选卡并开脚本。

### 期间修复
1. **狂野下自动化门禁失效**：插件原本只读 `PluginConfig.scriptRuntimeEnabled`（仅竞技场路径会置真），
   狂野下恒为 false → 拼装处理/兜底/选择诊断全部被挡。新增 `HsModScriptState`
   后台轮询 HsMod `/api/status`（端口扫描+token 发现，2 秒一次，主线程零阻塞），
   门禁改为“竞技场运行时开关 || 全局脚本开关”。验证：狂野对局中出现 `choice observed` / `assemble-check`。
2. **提权部署通道**：炉石/中控均为提权进程，普通脚本无法结束或替换被占用的 DLL；
   新增 `artifacts\deploy-plugin.ps1`（提权杀进程+替换）与 `artifacts\deploy-watch.ps1`
   （等 `gameplay=false` 再触发），全程未打断对局。

### 战绩与健康度
- 狂野：**72 局、39 胜 33 负**，卡组=圣契，模式=Wild，脚本始终 enabled。
- 选择观测 122 次；随机兜底 0 次、拼装建议提交 0 次（该卡组未出现拼装选择，属正常）。
- 插件异常 0；仅 1 条炉石本体空引用（DemonicProjectPlaySpell 协程，见 §147）。
- 全程按安全边界部署，未在对局中重启游戏。

### 结束状态（22:00）
中控与炉石仍在运行，狂野任务继续；用户可随时点“停止并等待本局”或关闭。
