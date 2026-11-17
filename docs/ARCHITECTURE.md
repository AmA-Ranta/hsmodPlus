# 运行架构

## 生产组件

| 工程 | 产物 | 职责 |
|---|---|---|
| `ArenaController.Wpf` | `hsmodPlus.exe` | WPF 桌面界面、设置展示与动作入口 |
| `ArenaController` | `hsmodPlus.Core`（随单文件发布） | 安装、官方启动链、监控、设置暂存、每日计划、安全停止与模式切换编排 |
| `ArenaBoxBot` | `ArenaBoxBot.dll` | BepInEx 插件，游戏内自动化、状态发布、命令处理与安全边界回执 |
| `Shared` | 双方引用的源码契约 | 原子命令、构筑停止、竞技场轮次停止格式 |

引擎中的隐藏 WinForms 承载用于复用现有控件/线程与控制逻辑，不是第二个对外前端；整理仓库不能直接删掉它。

```text
WPF → ControllerHost → 中控引擎
                          │ 本地 HTTP / 文件协议
                          ▼
                    HsMod + ArenaBoxBot → 游戏原生动作
```

## 职责与安全边界

- 中控管理安装与进程，不负责选牌或出牌策略；游戏内动作由插件/HsMod 完成。
- 插件可独立运行，不依赖中控保持开启；运行配置与命令通过契约和本机接口确认。
- 用户设置先暂存，“应用设置”后空闲立即生效；局中变更排队到安全边界。
- 停止、模式切换和部署不得打断正在进行的对局；必须区分新鲜场景、会话绑定和历史日志。
- Battle.net 登录需用户处理，中控不读取/输入凭据；无有效登录前置条件时等待。
- HsMod/version 目标文件已存在时安装器保留用户版本；缺失时使用完整发布包内副本。

解耦契约细节见 [DECOUPLING_PLAN.md](DECOUPLING_PLAN.md)。

## 当前关键协议与状态

游戏侧 `<炉石目录>/BepInEx/`：

- `plugins/`：ArenaBoxBot、HsMod、UniverseLib。
- `HsMod/controller-command.json` / `controller-command-receipt.json`：原子命令及回执，绑定进程与启动时间。
- `HsMod/controller-arena-stop-after-run.json` / `…-completed.json`：竞技场本轮结束停止请求/回执。
- `HsMod/controller-constructed-stop-after-game.json` / `…-completed.json`：构筑/战棋局后停止请求/回执。
- `HsMod/arena-runtime-state.json`、`controller-scene.json`：运行观测；竞技场专用字段不能直接当作其它模式总开关。
- `HsMod/arena-stats.json`：竞技场持久化台账。
- `config/HsMod.cfg`、`config/local.hsmod.arenaboxbot.cfg`：游戏侧配置。

中控侧 `%LOCALAPPDATA%/ArenaBoxController/`：已应用设置、会话绑定、用户日志、开发日志。
这些本机数据不纳入源码仓库或发布材料。

本机 HsMod 接口：`127.0.0.1:58744–58748`，状态/配置/脚本接口通过本地 token 访问。
模式目录由插件的 `ArenaModeCatalog` 定义，目录回归负责验证五模式一致性。

## 依赖与发布

外部二进制只保留 `pkg/` 一份，详见 [依赖说明](../pkg/README.md)。
`pkg/Native/` 取代原日期目录；`pkg/Hearthstone/BepInEx/UnstrippedCorlib/` 是 Doorstop 配置指定的必需目录，不是可删缓存。
炉石托管程序集仍来自开发机游戏安装，构建前提见根 README。

唯一发布入口是 `tools/BuildWpfRelease.ps1`：

1. 默认运行 `tools/Validate.ps1`（构建与默认离线回归）。
2. 构建插件、发布 WPF 自包含单文件。
3. 从 `pkg/` 组装完整载荷并生成 SHA256SUMS。
4. 生成 ZIP 后强制调用 `tools/VerifyDelivery.ps1`，检查所有依赖、清单哈希与本次关键产物。

输出在忽略目录 `交付/`；构建和测试缓存在 `artifacts/` 与各工程 bin/obj。源码整理不将产物提升为构建输入。

## 测试与历史

- `tests/` 按关注点分工程；共享策略通过源码引用测试，不复制生产实现。
- `Validate.ps1` 运行默认非竞技场套件；`tests/ArenaPolicy` 单独运行离线策略回归。
- `tools/Verify*.ps1` 覆盖专项原生 IL/协议；部分依赖游戏环境，不等同纯离线回归。
- `tools/harness/` 是真实 UI 验收，默认构建不执行；故障注入必须另行授权。
- 现行人工验收见 [TEST_PLAN_MANUAL.md](TEST_PLAN_MANUAL.md)，旧实机流水和旧版本说明归档到 [history/](history/README.md)。

此前删除的 BuildRelease/BuildThinRelease/RunValidation 等入口不再出现在现行操作流程中。
