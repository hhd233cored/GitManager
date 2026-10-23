# GitManager

使用 **WPF + .NET 10 + Git CLI** 构建的 Windows Git 桌面客户端。界面参考 GitHub Desktop 的操作习惯。

## 当前功能

- 打开本地仓库、拖入仓库文件夹、最近打开记录、克隆 HTTPS / SSH 仓库。
- **History 左侧：Commit List + Graph Column**，显示分叉、合并、HEAD、分支与标签。
- **History 右侧顶部：提交说明、作者、作者时间、SHA 和父提交信息**。
- **History 右侧下方：变更文件列表 + Diff**，支持调整分栏宽度。
- 并排 / 统一 Diff、增删行号、基础语法着色、复制 SHA、复制 Diff、选择并复制 Diff 行。
- 所有分支 / 当前分支历史，按需加载更多提交，按说明、作者、SHA 或引用搜索定位。
- Changes 文件暂存 / 取消暂存、全部暂存、提交标题和描述、创建提交。
- 同时包含已暂存与未暂存内容的文件显示半选，可分别查看两种 Diff。
- 创建和切换分支、切换远程跟踪分支、Fetch、Pull（仅快进）、Push、发布分支。
- 后台 Git 操作、取消操作、外部文件变更自动刷新、错误信息展示。

提交图与 Git 的父子关系对应。搜索保留整个图，通过选择和 Enter 定位匹配提交；合并提交的文件和 Diff 与第一父提交对比。

## 运行

需要 Windows、**.NET 10 SDK** 和已加入 PATH 的 **Git for Windows**。没有第三方 NuGet 依赖。

```powershell
dotnet run --project src/GitManager.App
```

直接打开指定仓库：

```powershell
dotnet run --project src/GitManager.App -- --repository "C:\path\to\repository"
```

也可以运行工作区根目录的 `run.ps1`。启动后选择“打开本地仓库”，或将文件夹拖入窗口。

## 发布

```powershell
./publish.ps1
```

输出到 `artifacts/app/GitManager.exe`，为依赖 .NET 10 Windows Desktop Runtime 的框架依赖版本。应用设置保存在 `%LOCALAPPDATA%\GitManager\settings.json`。

## 验证

Git 集成检查会创建独立测试仓库与本地 bare 远程，覆盖图连接、分页连续性、根提交、合并、中文与特殊路径、重命名、暂存、混合状态、提交、克隆、分支发布及快进拉取。

```powershell
dotnet run --project tests/GitManager.Tests -- --fixture artifacts/preview-repository
```

WPF 界面检查会打开上述测试仓库，验证布局、数据绑定、搜索、页面切换、Diff 模式、选择保留和窄窗口，并导出实际 WPF 渲染截图：

```powershell
dotnet run --project tests/GitManager.UiChecks -- artifacts/preview-repository artifacts/screenshots
```

`artifacts/preview-repository` 是自动生成的示例仓库，包含真实 Git 分叉和合并，用于界面预览；应用不会自动创建示例数据。

## 代码结构

```text
src/GitManager.Core/
  GitRunner.cs        无 shell 拼接的 Git 进程调用与取消
  GitService.cs       仓库、历史、Diff、暂存与远程操作
  GraphLayout.cs      与 UI 无关的图轨道布局
  DiffParser.cs       Unified Diff 解析与并排行配对
src/GitManager.App/
  ViewModels/         界面状态与异步操作
  Controls/           按行绘制的提交图、按可见区域绘制的 Diff
  Themes/             浅色工作区与深色工具栏
  Dialogs/            克隆与新建分支窗口
tests/
  GitManager.Tests/   Git 与布局集成检查
  GitManager.UiChecks/ WPF 界面与截图检查
```

当前版本使用已有的 Git 凭据 / SSH 配置。GitHub OAuth 与 PR、逐行暂存、交互式 Rebase、冲突编辑器、图片 Diff 和完整深色主题尚未实现。
