# 仓库整理记录（2026-11-17）

## 本次范围

只调整目录、构建/工具引用与文档，不重写生产逻辑，不改动炉石、盒子和中控安装目录，不改写 Git 历史。
保留上一轮完整运行库补包修复。

## 去冗余

- `1031更新/` 中的两个依赖迁入 `pkg/Native/`，文件字节及 SHA256 不变；打包和专项验证同步改路径。
- 大量 artifacts 实机临时脚本、截图、日志、解压副本及旧 ZIP 移到仓库外，不作为构建输入。
- 交付目录仅保留完整修正版；之后每次发布仍由唯一构建脚本生成。
- 一次性 `tools/SplitMainForm` 代码生成器，以及依赖硬编码本机/临时脚本的旧 `harness/safe-deploy`、`install-latest` 退役。本机原件在外部归档，Git 历史也可恢复。
- 历史交付、重构、测试报告、天梯目标与人工流水归入 `docs/history/`。当前 README、架构与人工验收只保留有效说明。
- mode-matrix/combined-semantics 改为直接引用同目录受控 UI 驱动，不依赖 artifacts 的副本。

## 归档位置

本机归档：`D:\万有引力号-归档\cleanup-20261117-170916`（初次移动 1916 个文件）。
其中保留原 artifacts、历史交付和退役工具；不是源码构建依赖，不应复制回当前仓库。
用户数据、账号状态、现场日志不得进入 Git。

## 不应误删的内容

- `ArenaController` 与 `ArenaController.Wpf` 分别是引擎和前端，并不是重复代码。
- `Shared` 是双方唯一契约来源；测试引用生产源文件是合理复用。
- `pkg/Hearthstone/BepInEx/UnstrippedCorlib` 的 18 个 DLL 是加载器必需依赖，不是可再生成的本项目输出。
- 不同测试工程和 Verify 专项工具覆盖不同约束，本次不凭名称相似就删除。

## 验证方法

1. 发布与专项工具无活动的旧日期目录或临时 UI 脚本引用。
2. 所有 PowerShell 工具可解析；Markdown 相对链接存在。
3. 运行默认 Validate 和 ArenaPolicy 离线策略回归。
4. 重新打完整整合包；校验所有必需载荷、SHA256 清单和原生基线哈希。
5. 审查 Git 差异：仅结构/工具/文档变更，不影响生产代码。

已完成：修改的 PowerShell 脚本可解析、Markdown 链接检查通过、默认 Validate 通过、ArenaPolicy 策略测试通过；重建 `hsmodPlus-20261117-clean.zip`（48 项、76 MB）并通过完整载荷及哈希校验。现有 nullable 编译警告保留，未借整理之机修改业务。

实机对局和安装写入不属于本次整理验证；不以离线 PASS 声称完成游戏内验收。
