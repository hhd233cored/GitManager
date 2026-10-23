# GitManager

使用 **WPF + .NET 10 + Git CLI** 构建的 Windows Git 桌面客户端。界面参考 GitHub Desktop 的操作习惯。

## 当前功能

- 多仓库列表：批量添加本地仓库、拖入多个仓库文件夹、切换仓库、克隆 HTTPS / SSH 仓库。
- 仓库列表管理：搜索、显示名称、上移 / 下移、更改本地位置、移除列表记录，重启后保留设置。移除会保留本地文件。
- **三栏常驻布局：左侧 Changes、中间 History、右侧统一 Diff**。Changes 和 History 同时显示，可拖动分隔条调整宽度。
- History 使用 **Commit List + Graph Column**，以紧凑单行显示提交，文字与展开按钮紧贴每行实际占用的轨道，过长内容截断。图中显示分叉、合并与 HEAD；分支和标签引用移到悬停详情。
- 每条历史提交下可独立展开变更文件，显示文件名、目录与原有的 `+ / − / ·` 状态图标；展开期间轨道线连续贯穿文件区域。展开状态在刷新后保留。
- History 悬停显示完整提交说明、描述、用户名、时间、增删行数、SHA、父提交和引用信息。统计按需读取并缓存；作者只显示用户名。
- 右侧顶部将用户名、时间放在提交 Summary 上方，SHA、父提交和完整描述整合在同一滚动区域，去掉头像及描述内部的独立滚动框；下方为共享 Diff。选择 Changes 或历史提交中的文件会切换该 Diff 的内容，保留两边的文件选择。
- 分栏去掉 Changes / History 标题；提交表单去掉“创建提交”“本次提交包含…”和“已选”提示，Summary、Description 与提交按钮采用更小的高度和间距，表单仍可拉伸。
- Changes 下方提供当前仓库资源管理器，显示已跟踪文件及未忽略的未跟踪文件；选择文件或目录可筛选相关历史节点，目录包含全部子目录。
- 路径筛选保留全部已加载提交的行高、顺序与轨道线，只隐藏无关节点和提交文字；点击“全部历史”或筛选条的 × 可恢复。文件变更按当前路径匹配，合并提交与第一父提交比较。
- Changes 与资源管理器之间、提交信息与 Diff 之间、Changes 文件列表与提交表单之间可上下拖动；三栏之间可左右拖动。分隔条支持键盘方向键调整。
- 并排 / 统一 Diff、增删行号、基础语法着色、复制 SHA、复制 Diff、选择并复制 Diff 行。
- Diff 按面板宽度自动换行，不显示横向滚动条；并排两侧按较高的一侧对齐，换行不改变复制得到的原始代码。
- 全局使用无箭头的极细胶囊滚动条，支持滚轮、轨道翻页与拖动。
- 所有分支 / 当前分支历史，按需加载更多提交，按说明、作者、SHA 或引用搜索定位。
- Changes 默认勾选全部变更文件，支持单文件勾选、空格键切换、全选 / 全不选，总勾选框可显示部分选择状态。
- 勾选表示本次提交包含该文件的全部变更；刷新、切换预览文件和仓库保留手动取消的选择，新出现的文件默认勾选。勾选操作不修改 Git 暂存区，提交时保留未选文件已有的暂存内容。
- 默认 Diff 展示文件相对于 HEAD 的全部变更；同时存在已暂存和未暂存修改时，也可单独查看已暂存内容。
- 文件状态使用绿色方框“+”表示新增、黄色方框圆点表示修改、红色方框“-”表示删除；菜单弹出列表使用纯白背景和蓝色悬停项。
- 创建和切换分支、切换远程跟踪分支；同步按钮统一显示 Fetch、Pull（仅快进）、Push 或 Publish branch，右侧下拉菜单可手动选择操作。
- 后台 Git 操作、取消操作、外部文件变更自动刷新、错误信息展示。

提交图与 Git 的父子关系对应。每条第一父提交链保留自己的轨道颜色；不同支线到达同一个祖先时，在该祖先节点处汇合，避免主线被支线颜色替换。主轨道居左，同时存在的非主轨道按创建该轨道的提交时间从旧到新排列，较新的支线居右，包含未合并分支。先依据完整的轻量拓扑规划支线的占位区间，每条支线在到达祖先前保持固定列，不会因后来出现的支线而中途拐弯；不重叠的支线可以复用轨道列。提交说明、作者和描述按页加载，所有页沿用同一份拓扑布局。搜索保留整个图，通过选择和 Enter 定位匹配提交；路径筛选后只定位可见提交。合并提交的文件、统计和 Diff 均与第一父提交对比。

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

点击顶部仓库选择器可切换仓库，并通过“管理仓库列表…”编辑显示名称、位置和顺序，或使用 `Ctrl+Shift+O` 打开管理窗口。列表会兼容升级之前保存的最近仓库记录，并且不再限制为 10 个。

顶部同步按钮根据已知的上游状态选择主要操作：远程有新提交时显示 Pull，本地有待推送提交时显示 Push，未设置上游时显示 Publish branch，其余状态显示 Fetch。Fetch 使用当前分支的远程（优先上游，否则优先 origin）；下拉菜单保留手动 Fetch / Pull / Push。双方历史分叉时，Pull 会保持仅快进策略并提示处理分叉。

当前分支选择器可切换本地分支，或从远程分支创建本地跟踪分支。自动刷新不会禁用选择器或忽略切换操作；如果未提交的修改会被覆盖，保留现有分支与文件，并显示 Git 错误。

## 发布

```powershell
./publish.ps1
```

输出到 `artifacts/app/GitManager.exe`，为依赖 .NET 10 Windows Desktop Runtime 的框架依赖版本。应用设置保存在 `%LOCALAPPDATA%\GitManager\settings.json`。

## 验证

Git 集成检查会创建独立测试仓库与本地 bare 远程，覆盖图连接、分页连续性、较新支线居右、支线汇合后的颜色、根提交、合并、增删统计（含重命名与二进制文件）、批量路径历史匹配、资源管理器文件范围、中文与特殊路径、重命名、暂存、混合状态、勾选文件提交、未选文件暂存保留、大批量路径、克隆、分支发布及快进拉取。

```powershell
dotnet run --project tests/GitManager.Tests -- --fixture artifacts/preview-repository
```

WPF 界面检查会打开上述测试仓库，验证三栏布局、数据绑定、搜索、共享 Diff 来源切换、Diff 模式、选择保留、窄窗口与 12,000 行虚拟化；还会复现自动刷新与添加仓库的竞争，检查多仓库持久化和同步按钮的实际 Fetch / Pull / Push / Publish 流程。Changes 检查覆盖真实勾选框点击、部分选择状态、刷新与仓库切换、提交钩子失败后重试、未选文件暂存保留及菜单配色，并导出实际 WPF 渲染截图：

```powershell
dotnet run --project tests/GitManager.UiChecks -- artifacts/preview-repository artifacts/screenshots
```

工作区检查还覆盖分支菜单与自动刷新竞争、远程跟踪、阻止覆盖本地修改、资源树文件 / 目录选择、空历史占位、连续切换筛选和面板尺寸调整。可追加 `--workspace-only` 单独运行这些检查。

外观与 Diff 检查覆盖紧凑历史、按行轨道留白、长标题截断、悬停详情、并排 / 统一换行、中文与 Unicode、超过 5,000 字符的连续文本、换行后复制、缩放面板、12,000 行绘制和胶囊滚动条交互。可追加 `--appearance-only` 单独运行这些检查。

常驻布局检查覆盖多提交独立展开、文件状态图标、贯穿文件区的轨道、历史 / 工作区文件复用同一个 Diff、刷新保留展开和预览来源、用户名与统计缓存。可追加 `--layout-only` 单独运行这些检查。

SkillTrust 实际案例可通过 Git 检查的 `--inspect-repository C:/Users/33612/Documents/GitHub/SkillTrust` 参数只读检查备份 / MCP 支线及分页稳定性；界面检查以该仓库作为首个参数并追加 `--review-only`，可导出修复后的轨道和提交描述截图，不切换分支或修改该仓库文件。

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
