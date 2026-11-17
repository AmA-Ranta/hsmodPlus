# 万有引力号 · 炉石脚本中控

唯一生产入口是 WPF 中控 `hsmodPlus.exe`，游戏插件是 `ArenaBoxBot.dll`。
中控负责安装、官方启动链、配置与监控；游戏内动作由独立插件和 HsMod 执行。

## 仓库结构

```text
HsController.sln       生产工程、测试与现场工具解决方案
ArenaController.Wpf/   WPF 前端，输出 hsmodPlus.exe
ArenaController/       中控引擎 hsmodPlus.Core（隐藏 WinForms 承载，不是第二套发布 UI）
ArenaBoxBot/           net48 游戏插件
Shared/                中控与插件共享契约（单一来源）
tests/                 各关注点的回归测试
tools/                 构建、发布校验、专项验证与真实 UI 验收工具
pkg/                   必要的外部二进制依赖与模板（唯一构建输入）
docs/                  当前架构、规范、测试入口；history/ 为历史记录
artifacts/             本机生成的构建、测试和实机证据（不入 Git）
交付/                  生成的完整发布 ZIP（不入 Git）
```

## 构建前提

Windows、.NET 8 SDK，以及可构建 net48 的参考程序集环境。插件仍需要本机炉石托管程序集，默认路径为
`D:\Hearthstone\Hearthstone_Data\Managed`（见 `ArenaBoxBot/ArenaBoxBot.csproj`）；仓库不分发游戏程序集。
依赖来源及维护规则见 [pkg/README.md](pkg/README.md)。

## 唯一构建与发布入口

```powershell
# 构建前端/引擎、插件，并运行默认离线回归（不会操作游戏）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Validate.ps1

# 竞技场及模式策略离线回归（不会操作游戏）
dotnet run --project .\tests\ArenaPolicy -c Release

# 完整整合发布：默认先校验，版本默认当天日期
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\BuildWpfRelease.ps1 -Version 20261117

# 独立复查发布包完整性与全部清单哈希
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\VerifyDelivery.ps1 -Zip .\交付\hsmodPlus-20261117.zip
```

发布包包含中控、插件、HsMod/盒子载荷、BepInEx 及完整 `UnstrippedCorlib`、安装说明和 SHA256 清单。
不要单独复制 exe 代替完整安装包。历史候选包与临时调试脚本不作为构建输入。

## 使用与安全边界

1. 将整合包完整解压到新目录，运行 `hsmodPlus.exe`。
2. 安装/修复前完全退出炉石和盒子，确认路径后执行“安装 / 修复插件”。目标已有 HsMod/version 时保留用户版本。
3. 修改设置后点击“应用设置”；对局中编辑排队到安全边界。
4. 运行中切换模式可点“开始 / 切换”；停止用“停止并等待本局”。**不得为了部署而打断对局。**
5. 每日计划要求中控保持运行；战网需要人工登录时，中控等待，不读取或输入凭据。

## 文档入口

- [架构与职责](docs/ARCHITECTURE.md)
- [编码规范](docs/CODING_STANDARD.md)
- [UI 规范](docs/UI_DESIGN.md)
- [解耦协议](docs/DECOUPLING_PLAN.md)
- [人工验收入口](docs/TEST_PLAN_MANUAL.md)
- [工具分类与危险操作说明](tools/README.md)
- [历史证据索引](docs/history/README.md)

## 仓库维护规则

- 业务代码与共享契约只保留一份；不同关注点的测试不是重复实现，不随意删除。
- `pkg/` 的二进制是必要外部输入，不是本项目编译产物；不能因“都是 DLL”而删除。
- `bin/obj`、发布包、日志、截图、实机状态均不入 Git；历史产物存放在仓库外。
- 历史日期文档只作证据，不作为当前操作指南；现行命令以本 README 为准。
- 本次整理保留既有功能，不进行业务重写，也不改写 Git 历史。
支撑本次整理的记录见 [docs/REPOSITORY.md](docs/REPOSITORY.md)。
