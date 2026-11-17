# 中控与插件解耦方案（基于代码取证）

## 1. 用户提出的原则

中控只保留**原子动作**：安装插件、启动游戏、关闭游戏、开始脚本、停止脚本、返回主菜单、设置卡组名称。
其余功能全部改为「监视游戏状态 + 组合这些原子动作」实现，插件尽量独立。

## 2. 现状取证：中控越界干涉清单

| 位置 | 干涉内容 | 是否属于原子动作 |
|---|---|---|
| `ArenaController/Ui/MainForm.Monitor.cs:595` `SetWatchdogAutoResume` | 直接改 HsMod 看门狗控制文件 `autoresume-*.json` 的 `restartEnabled` / `scriptActive` | ❌ 越界 |
| `ArenaController/Control/ConfigurationApplyGuard.cs:135-143` | 直接改 HsMod 配置：`自动脚本.推荐来源/脚本模式/卡组名称/卡组ID/场数限制/开启或关闭脚本`、`竞技场.脚本模式/竞技场模式/本轮最多对局数`、`HsMod.Init.Language` | ⚠️ 仅「卡组名称」合规，其余越界 |
| `ArenaController/Ui/MainForm.Script.cs:269` `HsModRuntimeLimitClient.ApplyAndVerifyAsync` | 通过 HTTP 改 HsMod 运行上限配置 | ❌ 越界 |
| `ArenaController/Ui/MainForm.Diagnostics.cs:197` `EnsureBoxOverlayClickThrough` | 改盒子进程窗口样式（加 `WS_EX_TRANSPARENT`） | ⚠️ 属于「减少第三方遮挡」，但仍是对其他进程的写操作 |
| `MainForm.Lifecycle.cs:533/547/569` `StopAfterGamePath` | 写标记文件让插件「本局后停止」 | ✅ 可归入「停止脚本」 |
| `MainForm.Logging.cs:56` 构造停止请求 | 写 `controller-stop-*.json` | ✅ 可归入「停止脚本」 |
| `ArenaController/Ui/ArenaRunStopControl.cs:104` | 写 `controller-arena-stop-after-run.json` | ✅ 应归入「停止脚本」的边界语义 |
| `MainForm.Lobby.cs:373` `ReturnHubRequestMarkerPath` | 写返回主菜单请求 | ✅ 归入「返回主菜单」 |
| `MainForm.ModeSwitch.cs:53` `WriteSessionModeBinding` | 写自己的状态文件 | ✅ 中控自有状态，不算干涉 |

四套并行的「停止/返回」标记协议（`controller-stop-*`、`controller-arena-stop-after-run*`、`StopAfterGamePath`、`ReturnHubRequestMarkerPath`）就是当前耦合最重的地方。

## 3. 关于「脚本启动时点不了游戏画面」

代码取证结论：

1. **中控没有任何输入拦截**。全仓库检索 `SetWindowsHookEx` / `LowLevelMouse` / `BlockInput` / `EnableWindow` / `SetCapture` 均无命中；中控窗口只有 `ShowInTaskbar=false` + 最小化宿主，没有 `TopMost`、没有 `Activate()`、没有 `SetForegroundWindow`。
2. 唯一涉及窗口的操作是 `BoxOverlayClickThrough`，它是**反向的**：给盒子的 Qt 覆盖层加 `WS_EX_TRANSPARENT`，让点击穿透到游戏，属于「消除遮挡」，不是拦截。
3. HsMod 配置里有 `应用焦点 = true`，注释明确写着「切回窗口时会重新居中并锁定鼠标光标」。这是 HsMod/游戏原生的焦点处理，脚本运行时反复获得焦点就会表现为「鼠标被拽回中间、点不到画面」。**这是脚本侧行为，不是中控加的。**
4. 因此：想关掉该现象，应把 HsMod 的 `应用焦点` 设为 `false`（插件设置里可改），或在游戏内关闭原生的窗口焦点处理；中控不需要也不应该去写这个配置。

## 4. 目标架构

```
中控（只做原子动作）                       插件（自主执行）
─────────────────────────                ─────────────────────
install-plugin  ──复制 dll──▶            脚本自身逻辑（对局/选牌/拼装/领奖）
start-game / stop-game                   开始脚本 = 置 runtime enabled
set-deck-name   ──命令文件──▶             停止脚本 = 在安全边界关闭 enable
start-script    ──命令文件──▶             返回主菜单 = 导航到 HUB
stop-script     ──命令文件──▶             本轮结束后停止 = 停止脚本的边界语义
return-hub      ──命令文件──▶
─────────────────────────
其余全部只读：controller-scene.json 心跳、/api/status、日志
```

命令协议统一为单个文件 `controller-command.json`：
```json
{ "schemaVersion": 2, "requestId": "guid", "action": "start|stop|returnHub|setDeck",
  "stopBoundary": "immediate|afterMatch|afterArenaRun", "deckName": "...", "issuedAtUtcTicks": 0 }
```
插件写 `controller-command-receipt.json` 回执，中控只等回执 + 监视心跳，不再写任何游戏侧状态。

## 5. 迁移顺序（每步都可真实验证）

- **S1 统一命令协议**：把四套标记合并成 `controller-command.json`（插件先兼容旧文件，验证通过后删除旧协议）。验证：构造停止、竞技场轮后停止、返回主菜单三条路径真实跑通。
- **S2 收回配置写权限**：中控不再写 `场数限制`/`推荐来源`/`语言`/`竞技场模式` 等，改由插件在启用时按自身设置应用；安装阶段允许一次性写默认值（属于「安装插件」）。
- **S3 看门狗移交**：删掉 `SetWatchdogAutoResume`，由插件自己维护 `restartEnabled`/`scriptActive`。
- **S4 盒子窗口样式**：保留为可选「点击穿透」辅助，默认开启但可关，并在文档中标注它是对第三方覆盖层的修正，不属于脚本控制。
- **S5 断言**：每步给命令协议加纯函数断言（请求匹配、边界判定、回执校验），并入 `tests/ArenaPolicy`。
## 6. 进度记录

### S1 统一命令协议（2026-11-10 已完成第一半）

已落地：
- 新增 `Shared/ControllerCommandContract.cs`：`controller-command.json` / `controller-command-receipt.json`，
  动作 `stop`、`returnHub`，边界 `immediate`、`afterMatch`；校验请求绑定（进程 PID + 启动时间 + 签发时间），
  回执必须严格晚于请求。补 29 条断言（`tests/ArenaPolicy/ControllerCommandContractTests.cs`）。
- 插件新增 `ArenaBoxBot/ControllerCommandRuntime.cs`：在 `Update()` 里读取命令，翻译成插件内部既有触发
  （`controller-stop-after-game.marker` / `controller-return-hub.marker`），并写回执。**旧标记现在只有插件自己写。**
- 控制器新增 `MainForm.Commands.cs`：`PublishControllerCommand` / `HasControllerCommandReceipt` / `ClearControllerCommand`。
- 已迁移：`MainForm.Lifecycle.cs` 三处「局后停止」写入、`MainForm.Lobby.cs` 的「返回主菜单」写入。
  现在控制器对游戏侧的写入只剩：命令文件、自己的会话绑定文件；不再直接写任何游戏标记。

未完成（S2 起）：
- `ConstructedStopContract`、`ArenaRunStopContract` 两套带状态的停止协议仍在用独立文件（下一轮并入命令协议）。
- 控制器仍直接写 HsMod 配置（`ConfigurationApplyGuard`、`HsModRuntimeLimitClient`、`NativeInstallService`），
  计划改为插件在 `start` 命令时自行应用。
- 看门狗文件写入（`SetWatchdogAutoResume`）计划由插件接管。

### S2 第一步：运行期配置移交插件（2026-11-11 00:08 已验证）

新命令 `configure`：控制器把「模式 / 场数限制 / 竞技场类型与额度 / 门票金币保护」一次性发给插件，
插件自己通过 HsMod 本机 API 应用并写回执（`ok` + `detail`）。控制器不再调用
`HsModRuntimeLimitClient` / `ArenaRuntimeConfigurationClient`（旧路径仅作为“插件版本过旧”时的兜底）。

真机日志：
```
[00:08:12] 已确认客户端在主界面（HUB），继续开启脚本。
[00:08:12] 已通过插件应用游戏内场数限制：不限
[00:08:14] HsMod 已选择卡组：快攻  (标准)
[00:08:14] 已通过 HsMod 本地接口开启脚本。
```
插件侧：
```
[Info : Arena Box Bot] controller configure applied: mode=Standard;limit=0;ok=True;detail=applied
```
没有出现“退回直接配置路径”，说明走的是插件路径。契约断言 31 条（含 configure 缺 Mode 必须拒绝）。

### S2 完成：控制器不再拥有运行期配置（2026-11-11 00:40 真机验证）

- 控制器删除了直连兜底（`HsModRuntimeLimitClient` / `ArenaRuntimeConfigurationClient`），
  启动流程只有一条：发 `configure` 命令 → 等插件回执（`ok`）→ 才开脚本；失败则明确报错不开脚本。
- 真机验证（新中控，标准与竞技场各一次）：
```
[00:08:12] 已通过插件应用游戏内场数限制：不限
[00:40:34] 已确认客户端在主界面（HUB），继续开启脚本。
[00:40:34] 已通过插件应用竞技场运行配置：普通竞技场；每轮对局=0；门票与金币停止保护已核验。
[00:40:34] 已通过 HsMod 本地接口开启脚本。
```
插件侧 `controller configure applied: mode=Standard/Arena;ok=True`。
- 期间还验证了“开始前检测到遗留对局”：先临时接管打完本局，再回主界面配置并开脚本，全程未打断。

### S4 可行性结论：启动前 cfg 写入必须留在控制器（2026-11-11 01:41 实测）

想用 HsMod 本机 API 的 `/config` 把模式写进 cfg，从而让控制器不再直接写 `HsMod.cfg`。实测：
- `wildLadderPlayMode` 的 `canWrite=True`，POST 后 `/api/status.mode` 立即变成 `Standard`（运行态确实切了）；
- 但 `HsMod.cfg` 的 `[自动脚本] 脚本模式` **没有**跟着变（POST 前后都是 Standard，恢复 Arena 后也没变）。

即 API 只改运行态、不持久化到启动用的 cfg 键。冷启动/重启后的模式仍取决于 cfg 文件，
所以“启动前写 cfg”必须由控制器在启动游戏前完成——这属于“启动游戏”的准备，不是运行期干涉。
结论：S4 不做；把这条写进文档作为边界说明。

## 7. 当前架构与职责边界（2026-11-11 总结）

### 插件（游戏内，唯一碰游戏状态的一方）
| 原子动作 / 职责 | 实现 |
| --- | --- |
| 开始脚本 / 停止脚本 | 复用 HsMod 原生开关；控制器只通过接口触发 |
| 返回主菜单 | `controller-command.json` action=`returnHub` |
| 停止（局后边界） | `controller-command.json` action=`stop` + `boundary=`immediate/afterMatch` |
| 运行期配置 | action=`configure` → 插件自己调 HsMod 本机 API 并写回执（`Ok/Detail`） |
| 竞技场轮后停止 | 插件跟踪轮次/领奖页，写 `controller-arena-stop-after-run-completed.json` |
| 无推荐选择的兜底 | 5 秒后按 `GameState.AddChosenEntity` + `SendChoices` 随机提交（换牌除外） |
| 状态发布 | `arena-runtime-state.json`、`controller-scene.json` 等 |

### 控制器（只做启动/关闭游戏 + 四个原子动作 + 监控）
| 职责 | 说明 |
| --- | --- |
| 安装/修复插件 | 安装维护面板 |
| 启动/关闭游戏 | 战网启动链、按边界关客户端、模式切换后重建会话 |
| 开始/停止脚本、返回主菜单、设置卡组名称 | 通过命令或 HsMod 本机接口，原子化 |
| 启动前写 HsMod.cfg | **保留**：API 改模式不落 cfg（见 S4 结论），属“启动游戏”准备 |
| 看门狗 lease | **保留**：该文件由 HsMod 自身维护，控制器写 false 用于压制停止后的自动续跑；移交会与 HsMod 抢写 |
| 监控 | 只读状态：场景、脚本开关、竞技场轮次、回执 |

### 已删除的越界
- 直接写 `controller-stop-after-game.marker` / `controller-return-hub.marker`（改为命令协议）；
- 直接调 HsMod 运行期配置接口（改为 `configure` 命令 + 回执）；
- 停止后基于过期探针的误接管（加冷却与连续确认）。

### 测试方式（用户要求）
所有验证均为**人工操作中控**：真鼠标点击（先置顶窗口再点）、读界面与日志；不使用脚本自动化替代真实点击。
