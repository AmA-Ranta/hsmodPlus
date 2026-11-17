# 工具入口

## 构建与发布（现行唯一入口）

- `Validate.ps1`：构建 WPF/引擎/插件及默认离线回归。
- `BuildWpfRelease.ps1`：完整整合发布；自动调用完整性与哈希校验。
- `VerifyDelivery.ps1`：校验 ZIP 全部必需载荷，包括完整 UnstrippedCorlib。

## 专项验证

`Verify*.ps1` 是不同关注点的 IL/契约检查，不是重复的构建脚本。部分检查需要本机炉石托管程序集或正在运行的本地 API；使用前先检查参数。
`Summarize*.ps1`、`GridTest.ps1`、`LadderWatch.ps1` 用于结果汇总或现场观察。

## 真实 UI 与现场验收

- `harness/uia-drive.ps1`、`harness/uia-setmode.ps1`：受版本管理的 UI 操作实现；调用方不得依赖 artifacts 中的副本。
- `harness/mode-matrix.ps1`、`harness/combined-semantics.ps1`：实机模式/边界验证。
- `LiveUi.ps1` 与 `live/`：现场 UI 辅助工具。
- `harness/resilience.ps1`：**故障注入**，会主动结束游戏/盒子/中控；只允许在用户明确授权的测试环境执行，不属于普通回归。
- `Deploy-PluginSafely.ps1`：旧现场插件部署辅助，依赖本机路径、权限与场景快照；不是通用安装器。正常交付安装请使用中控“安装 / 修复插件”。不要在对局中部署。

所有现场工具均不被默认发布构建调用。输出写入 artifacts，不得将用户凭据或机器状态提交 Git。

## 已退役工具

一次性 MainForm 拆分生成器、硬编码旧安装目录的 install-latest、依赖临时 install-step2 的 safe-deploy 已移出当前仓库；历史仍在 Git，原本机副本保留于仓库外归档。
