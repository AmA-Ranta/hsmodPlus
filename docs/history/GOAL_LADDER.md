# 目标运行记录：快攻 · 标准天梯 → 传说

## 目标
使用「快攻」卡组挂标准天梯，直到传说；期间监控出现的问题，记录并修复。

## 运行配置（2026-11-01 23:33 起）
- 模式：标准（HsMod `WildLadderBot`，`GT_RANKED` / `FT_STANDARD`）
- 卡组：快攻（`deck:9565191512`，名称 `快攻  (标准)`）
- 额度：每轮 0（不限）、总局 0（不限）、最多轮数 0
- 每日计划：关闭；手动开始
- 自动恢复：开启；操作节奏：HumanLike（中控不改动）

## 进展证据
- HsMod 原生日志：
  - `WildLadderBot: 开始匹配，name=快攻, id=9565191512, gameType=GT_RANKED, format=FT_STANDARD`
  - `LadderSessionStats: standard win, rank 1.0 -> 3.3`（2026-11-01 23:41:47，胜）
- 天梯台账 `D:\Hearthstone\BepInEx\HsMod\ladder_session.csv`（每局追加，列：time;mode;result;rank_before;stars_before;legend_before;rank_after;stars_after;legend_after;note;account）
  - 最新行 `2026-11-01 23:41:47;standard;1;1;0;0;3;3;0;...`：胜，段位星从 1.0 升到 3.3，`legend=0`（尚未传说）
- 游戏内 `BoxAI` 出牌日志持续输出（`BoxAI链路提交/自动执行`），说明对局由插件真实执行。

## 已发现并修复的问题
### 1. 卡组门禁被“陈旧对局标记”跳过 → 脚本启动后立即关闭
- 现象：控制器日志 `deck-gate.skipped;reason=gameplay-active`；HsMod 日志 `WildLadderBot: 请在设置中选择当前模式的卡组`，随后 `脚本开关: 关闭`，中控停止接管。
- 根因：`IsConstructedGameplayActive()` 扫描长寿命 `LogOutput.log` 中旧的 `Gameplay.Awake` 标记；停在主界面（HUB）时仍被判定为“对局中”，导致跳过选卡组步骤；HsMod 的 `wildLadderDeckName` 当前值为空，脚本无法开局。
- 修复：`ArenaController/Ui/MainForm.cs` 的 `EnsureConfiguredConstructedDeckAsync` 增加 `IsNativeDeckMissingAsync()`：当原生状态包含 `ladderStatus.deckMissing` / “请在设置中选择当前模式的卡组”时，不再按“对局中”跳过，继续走卡组选择并回读确认。
- 验证：重启后控制器日志出现 `已确认 HsMod 卡组：快攻  (标准)`，随后 `已通过 HsMod 本地接口开启脚本`，`WildLadderBot` 正常开始匹配。

### 2. 结算奖章信息超时被误报为“插件异常”
- 现象：控制器日志 `插件记录了异常…`；原生日志 `[Error] EndGameScreen_MedalInfoTimeOut`、`NetCacheMedalInfo not yet available!`（`leagueId=5 starLevel=3`）。
- 判断：客户端结算页奖章数据轮询超时，下一局匹配正常（23:48:50 已出新胜场），非脚本故障。
- 修复：`IsBenignNativeNotice` 增加 `EndGameScreen_MedalInfoTimeOut`（`NetCacheMedalInfo` 原本已过滤），避免误报/误触发恢复。

### 3. 运行中追加进展
- 23:41:47 胜，rank 1.0 → 3.3（189 局）
- 23:48:50 胜，rank 3.3 → 6.3，`legend=0`
- 中控 23:49:28 重新接管脚本（`已通过 HsMod 本地接口开启脚本`），对局继续。
- 23:54:22 胜，rank 6.3 → 12.1，`legend=0`（累计 102 胜 / 89 负 / 191 局）
- 23:56:58 重新接管（界面/图标重建后）；脚本 `scriptEnabled=True`，对局继续进行。

### 4. 结算页卡牌定义查表噪音
- 现象：`[Error] DefLoader.GetCardDef() - dbId 0 does not map to a cardId`（23:59:08），触发“插件记录了异常”。
- 判断：结算页 dbId=0 查表失败，属客户端日志噪音，下一局 00:00:03 正常进入留牌。
- 修复：`IsBenignNativeNotice` 增加该行过滤。

### 5. 最新进展
- 23:59:20 胜，rank 12.1 → 16.3，`legend=0`（累计 103 胜 / 192 局）；随后 00:00:03 正常进入下一局留牌。

### 6. 最新进展（紧凑 UI 上线后）
- 00:06:11 胜，rank 16.3 → 20.3；00:12:49 胜，rank 20.3 → 24.3，`legend=0`（累计 105 胜 / 194 局）。
- 00:14 控制台改为紧凑双页：通用任务只有“对局数”，竞技场独立块含轮次/每局/门票金币/本轮边界；停止策略不再含门票/金币。
- 安装/修复改为版本解耦：保留用户 HsMod.dll / version.dll，不再强制覆盖或校验其哈希。

### 7. 长对局中状态接口静默被误判“卡死” → 重启游戏
- 现象：00:39:21 控制器日志“检测到炉石疑似卡住（HsMod 状态接口超过 90 秒未响应），正在关闭炉石和盒子并恢复任务”，随后 00:39:36 重启炉石（PID 9508 → 23456）；但当时对局仍在正常出牌（BoxAI 日志连续）。
- 根因：`ClientRecoveryMonitor.Observe` 非竞技场 runtime 阈值实际是 20 秒（与提示文案 90 秒不一致），长回合/长动画期间本地接口被阻塞即触发恢复。
- 修复：非竞技场阈值改为 90 秒；新增“原生日志仍在更新（≤60 秒）”豁免——对局活跃时不因接口静默重启游戏；`IsNativeLogFreshlyActive()` 提供该证据。
- 验证：01:05:03 中控重新接管，对局继续（00:54:39 胜后进入长局）。

### 8. 进展
- 00:54:39 胜，rank 28.3 → 30.1，`legend=0`；累计 108 胜 / 90 负 / 283 行台账。

### 9. 新增客户端噪音过滤
- `InitializeCustomSpawn is not properly constructed. Actor component missing.`（随从生成钩子早于 Actor 组件）会让中控误报“插件记录了异常”；已加入 `IsBenignNativeNotice`。
- 验证：非竞技场回归全绿；01:14:04 部署新构建并重新接管。

### 10. 本轮进展
- 01:04:05 胜，rank 30.1 → 32.3；01:12:03 负，rank 32.3 → 32.2（累计 109 胜 / 91 负）。
- 01:05–01:12 的长对局期间未再被误判重启（90 秒阈值 + 原生日志活跃豁免生效）。

### 11. 传说判定与当前进度
- 台账 201 条标准记录显示段位为 **1–50 递增**：`2026-11-01 16:27:34` 拿到 51.0 后，下一批记录 `legend` 列变为非 0（传说）。因此 **段位 51.0 = 传说**。
- 当前 `01:19:39` 负，段位 32.2 → 32.1，`legend=0`；距 51 还差约 19 段。
- 近 8 局: `W L L W W W L L`（109 胜 / 93 负 / 202 局）。
- 新增 `tools/LadderWatch.ps1`：读取 `ladder_session.csv` 输出胜负、当前段位、近 8 局走势，并在 `legend` 非 0 时输出 `LEGEND`，便于每轮快速核对目标。

### 12. 本轮进展
- 01:23:50 胜，32.1 → 33.1；01:28:21 胜，33.1 → 34.1，`legend=0`。
- 累计 111 胜 / 93 负 / 204 局；段位 34.1，距传说（51.0）约 17 段。
- 本轮无异常、无自动恢复触发（长对局稳定）。

### 13. 本轮进展（续）
- 01:33:39 胜，段位 34.1 → 36.1（本局 +2 段），`legend=0`；累计 112 胜 / 93 负 / 205 局。
- 当前段位 36.1，距传说（51.0）约 15 段；近 8 局 `L W W W L L W W`。
- 无异常、无恢复触发。

### 14. 本轮进展
- 01:39:26 胜，段位 36.1 → 37.2，`legend=0`；累计 113 胜 / 93 负 / 206 局。
- 当前 37.2，距传说（51.0）约 14 段；无异常、无恢复。

### 15. 本轮进展
- 01:44:27 胜，段位 37.2 → 38.3，`legend=0`；累计 114 胜 / 93 负 / 207 局。
- 当前 38.3，距传说（51.0）约 13 段；5 连胜，无异常/无恢复。

### 16. 本轮进展
- 01:52:26 负，段位 38.3 → 38.2，5 连胜被终结（对局约 8 分钟）；累计 114 胜 / 94 负 / 208 局。
- 当前 38.2，距传说（51.0）约 13 段；无异常/无恢复。

### 17. 本轮进展
- 01:57:34 负，段位 38.2 → 38.1；近 4 局 1 胜 3 负，累计 114 胜 / 95 负 / 209 局。
- 观察：段位 38 附近进入拉锯（+1/-1 段），胜率需 >50% 才能稳步推进；当前无技术异常。

### 18. 本轮进展
- 02:04:35 胜，段位 38.1 → 38.3，`legend=0`；累计 115 胜 / 95 负 / 210 局。
- 盒子链路健康：心跳 2 秒内、推荐调用持续写入、对局内发布胜率 46.19%——无技术异常。
- 当前 38.3，距传说（51.0）约 13 段。

### 19. 结算后退赛提示被误报为异常
- 现象：02:09:45 胜局结算时出现 `Network.OnGameServerDisconnectEvent() - ... BNet error: ERROR_RPC_PEER_UNKNOWN`（`[Info]`），以及 `RpcController.SendData() - Not connected`（过滤后仍触发）→ 中控提示“插件记录了异常”。
- 判断：对局结束退出游戏服务器时的正常断开，下一局匹配正常。
- 修复：`IsBenignNativeNotice` 增加 `Disconnected from game server with BNet error` 与 `ERROR_RPC_PEER_UNKNOWN`；新构建 02:14:03 部署并接管。

### 20. 本轮进展
- 02:04:35 胜，38.1 → 38.3；02:09:45 胜，38.3 → 39.2，`legend=0`。
- 累计 116 胜 / 95 负 / 211 局；当前 39.2，距传说约 12 段。

### 21. 补充（本轮尾巴）
- 02:12:57 胜，39.2 → 40.3；累计 117 胜 / 95 负 / 212 局，当前段位 40.3，距传说（51.0）约 10 段。
- 交付包重建：SHA256 `A65C1F29D3D83156B4831B2E72E5D88EDF065A14BA25DA981516B69AF268EBDC`。

### 22. 本轮进展
- 02:19:25 胜，段位 40.3 → 42.1，`legend=0`；累计 118 胜 / 95 负 / 213 局。
- 新增的退赛噪音过滤生效：本局结算未再出现“插件记录了异常”。
- 当前 42.1，距传说（51.0）约 9 段。

### 23. 本轮进展
- 02:24:30 胜，段位 42.1 → 42.3（星数推进），`legend=0`；累计 119 胜 / 95 负 / 214 局。
- 当前 42.3，距传说（51.0）约 8 个段位；无异常/无恢复。

### 24. 本轮进展
- 02:29:11 胜，段位 42.3 → 43.2，`legend=0`；累计 120 胜 / 95 负 / 215 局。
- 当前 43.2，距传说（51.0）约 8 段；无异常/无恢复。

## 待观察项
1. 每局结果与段位推进（`ladder_session.csv` 每局一行；`legend_*` 变为非 0 即到达传说）。
2. 长局/动画等待（`ladderStatus.waitAnimation`）是否出现卡死或超时。
3. `EndGameScreen_MedalInfoTimeOut`（`leagueId=5 starLevel=3`）偶发：客户端奖章信息超时；目前未阻断下一局匹配，继续观察。
4. 盒子推荐调用（`hsang-recommend-call.log`）失败率与超时。
5. 掉线/战网登录页：按既有策略暂停等待人工，不自动输入凭据。

## 监控方式
- `%LOCALAPPDATA%\ArenaBoxController\controller.log` / `controller-developer.log`
- `D:\Hearthstone\BepInEx\LogOutput.log`
- `D:\Hearthstone\BepInEx\HsMod\ladder_session.csv`
- HsMod 本地接口：`GET http://127.0.0.1:58744/api/status`（`X-HsMod-Token`，token 在首页 `<meta name="hsmod-token">`）

## 19/20. 停止条件迭代修复（2026-11-02 午间）

### 19.1 “中控显示已停止，但游戏和插件还在跑”
- 证据：04:30:48 会话断开触发恢复；04:31:31 重启后重新开启脚本；**04:32:28** 控制器观察到脚本 off 并直接脱离（“已确认游戏内脚本关闭，中控停止接管”）；之后插件自身自动续跑，对局一直持续到 11:31 打上传说，而中控界面停留在“已停止”。
- 根因：`ObserveNativeStop` 对“刚启动/刚恢复 120 秒内”的短暂脚本关闭没有区分，按用户 F12 停止处理并脱离，且脱离时未关闭插件自动续跑。
- 修复（`ArenaController/Control/ConstructedStopPolicy.cs` + `MainForm.ObserveNativeStop`）：
  - 关闭证据需持续 ≥20 秒，避免瞬时抖动误判；
  - 若距最近一次确认开启脚本 <120 秒，判定为启动期瞬态 → **自动重新开启并继续接管**，不脱离；
  - 真正的外部关闭：脱离中控，同时 `SetWatchdogAutoResume(false, strict:true)` 关闭插件自动续跑，状态文案改为“脚本被外部关闭（中控已停止接管）”，避免“已停止但仍在打”。
- 实机验证：11:56 接管后手动 POST `/api/script {"enabled":false}` 模拟外部关闭，11:57:24 与 11:57:54 控制器两次自动重开，日志“检测到脚本在任务启动初期被外部关闭，已自动重新开启并继续接管”，状态保持“运行中”。

### 19.2 停止请求卡在“正在确认结算”
- 证据：12:06:29 点“停止并等待本局”，12:09:50 脚本已 off，但 HsMod 活动文本停在“正在确认结算”，控制器 2 分钟仍未完成停止（旧 `ConstructedStopBoundary` 只认 idle/lobby/hub/菜单/等待盒子建议）。
- 修复：脚本已关闭且活动不含 gameplay 词时，把“结算/确认结算/奖励/胜利/失败/结果/Victory/Defeat/Reward/EndGame”视为安全边界。
- 测试：`tests/StopConditions` 新增结算边界断言（23 项总计），`tools/Validate.ps1` 已纳入并通过。

### 19.3 运行中但空转：卡组无效自愈
- 证据：11:56 接管后脚本 on、界面“运行中”，但活动=`所选卡组已删除或不适用于当前模式，请重新选择`，卡组选项为空，任务不下场。
- 修复：`ObserveInvalidDeck()` + `EnsureConfiguredConstructedDeckCoreAsync(force:true)`，检测到该状态每 30 秒强制重走卡组选择门禁。
- 验证：12:05:39 日志“已确认 HsMod 卡组：快攻（标准）”“检测到游戏报告卡组无效，已重新选择指定卡组并恢复对局”，随后 12:06 进入匹配。

### 19.4 游戏内点击被盒子悬浮窗吞掉（用户反馈）
- 现象：中控运行时无法点击游戏内插件设置/弹窗；此前可以。
- 定位：枚举窗口命中 `HSAng.exe` 的 `Qt5QWindowIcon` / “Deck Info Embedded Window”，全屏矩形（2,58–3074,1837），仅 `WS_EX_LAYERED` 而**没有** `WS_EX_TRANSPARENT`，鼠标命中的像素被该窗口吃掉；手动以管理员加 `WS_EX_TRANSPARENT` 后 `WindowFromPoint(100,100)` 由悬浮窗变为游戏窗口，点击恢复正常。
- 实现：`BoxOverlayPolicy`（判定 Qt 悬浮窗候选）+ `BoxOverlayClickThrough`（管理员下给 `WS_EX_LAYERED` 悬浮窗补 `WS_EX_TRANSPARENT|WS_EX_NOACTIVATE`，幂等）+ 监控 tick 每 3 秒执行一次，命中时日志“已让盒子悬浮窗点击穿透”。
- 测试：`tests/StopConditions` 增加 5 条悬浮窗策略断言（游戏窗口与无关盒子窗口不动）。
- 注意：实机日志显示该悬浮窗随后被盒子自行重建/关闭，未能稳定复现；若再次出现点击被吞，可先按 F10 隐藏游戏内信息框、或盒子“切换外置”，本项策略仍会尝试自动穿透。

### 20. 本轮交付状态
- `tests/StopConditions` 23 项断言；`tools/Validate.ps1`（构建 + 非竞技场回归）全绿；竞技场未测试。

### 21. 停止请求仍卡在“正在确认结算” → 补齐完成条件（14:2x 实机验证）
- 复现：13:57:51 点停止 → 14:00:10 脚本已 off，直到 14:05 控制器仍未完成停止。
- 诊断：完成条件 `IsConstructedStopComplete` 只认插件回执 + 旧日志 GAMEPLAY 标记；回执与进程校验中还用了 `Process.StartTime` 与 HsMod 毫秒级时间**精确相等**（实际因 UTC/精度永远不等）。
- 修复：
  - `ConstructedStopContract.CanCompleteStop` 增加“脚本已关闭且处于结算/空闲安全边界（持续 ≥15 秒）”完成条件，不再被缺失回执或陈旧 GAMEPLAY 标记卡死；
  - 状态开始时间比较改为 2 秒容差；
  - 完整链路：停止请求 → 脚本关闭 → 结算画面识别 → 完成并释放中控（面板显示“已停止”）。
- 实机验证（14:23:26 请求）：14:23:42 关闭脚本并立即完成，“已确认局后停止边界：脚本已停止”；**停止全程 16 秒**，不再卡结算。脚本 off、插件自动续跑 off、游戏保持运行。

### 22. 测试补充
- `tests/StopConditions` 现为 27 项断言（停止策略、结算边界、停止完成契约、悬浮窗穿透策略）。
