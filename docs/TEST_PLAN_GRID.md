# 中控网格测试计划（Grid Test Plan）

目标：对 hsmodPlus 中控做**矩阵式（grid）**验证，覆盖任意可用模式的配置、启动、基础校验与停止语义，并保留可复跑证据。

## 1. 测试维度（网格）

| 维度 | 取值 | 说明 |
|---|---|---|
| 模式 Mode | 标准、狂野、休闲、酒馆战棋（竞技场=配置项，注①） | 天梯/战棋走 `wildLadderPlayMode`；竞技场走独立块 |
| 对局数 MaxTotalGames | 0（不限）、1 | 天梯/战棋只有对局数，无“轮数”概念 |
| 启动前等待盒子心跳 | false、true | 影响启动门禁 |
| 每日计划 | 关闭、窗口内、窗口外（注②） | 窗口外应自动停止 |
| 停止后关闭炉石 | false、true（注③） | 仅末位 cell 可用，避免关闭后续 cell 依赖的客户端 |
| 设置应用方式 | 直接启动读盘、暂存后“应用设置” | 本次用“写配置→重启中控”保证确定性 |

注①：统筹要求“不测试竞技场”；竞技场只做**配置/边界**离线校验（额度、门票/金币、本轮边界），不下场对局。
注②：窗口内/外通过写 `StartTime`/`StopTime` 构造；窗口外的 cell 应在启动被“每日计划”拦截。
注③：`CloseGameAfterStop=true` 的 cell 放最后执行。

## 2. 每个 Cell 的步骤与断言

1) 停止中控 → 写入 `%LOCALAPPDATA%\ArenaBoxController\controller-settings.json`（保留路径字段，仅改网格参数）。
2) 管理员启动中控；等待日志 `中控已启动；模式=<Mode>`。
3) UIA 点击「开始任务」；等待 ≤120 秒出现以下之一：
   - PASS：日志含 `WPF 界面开始：已开启<Mode>脚本` 或 `已通过 HsMod 本地接口开启脚本`，且 HsMod 状态 `scriptEnabled=true`；
   - BLOCKED：日志出现需要人工的登录/战网等待（如“等待战网登录”）；记为 BLOCKED 并继续下一 cell（不强行登录）。
4) 校验配置生效：
   - HsMod `mode` 与模式映射一致（标准=Standard、狂野=Wild、休闲=Casual、酒馆战棋=Battlegrounds）；
   - 构筑模式 `deck.name` 等于配置卡组；
   - 对局数限制：`wildLadderStopAfterGames` 等于 `MaxTotalGames`（0=不限）。
5) 停止：UIA 点击「停止并等待本局」；≤180 秒内应出现 `已确认局后停止边界` / `已停止`。
   最终断言：HsMod `scriptEnabled=false` 且 `autoresume-auto-1.json` 的 `restartEnabled=false`；中控日志无“监控轮询失败”。
6) 记录一行：cellId、模式、限制、心跳、开始耗时(s)、停止耗时(s)、结果(PASS/FAIL/BLOCKED/SKIPPED)、关键证据。

## 3. 网格规模（最小可执行集）

- 主线：4 模式 × 对局数{0,1} × 心跳{false,true} = 16 cells。
- 追加：每日计划窗口外（标准/酒馆）×1、关闭炉石后停止（最后一个 cell）×1。
- 竞技场：仅离线配置断言（`tests/StopConditions` 等），本次不记入 live 网格。

## 4. 证据与产物

- `artifacts/grid-results.csv`：逐 cell 结果。
- `docs/GRID_REPORT.md`：汇总、失败分组、复跑命令。
- 失败即修：同一失败在修复后重跑该 cell 与相邻 cell（回归）。

## 5. 执行

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\GridTest.ps1 -Cells standard,bg -MaxCells 6
```

（`-Cells` 可传 `all`，或逗号分隔的 `standard,wild,casual,bg,schedule`。）
