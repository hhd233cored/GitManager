# GitManager

使用 **WPF + .NET 10 + Git CLI** 构建的 Windows Git 桌面客户端。界面参考 GitHub Desktop 的操作习惯。

## 当前功能

- 多仓库列表：批量添加本地仓库、拖入多个仓库文件夹、切换仓库、克隆 HTTPS / SSH 仓库。
- 仓库列表管理：搜索、显示名称、上移 / 下移、更改本地位置、移除列表记录，重启后保留设置。移除会保留本地文件。
- **三栏常驻布局：左侧 Changes、中间 History、右侧统一 Diff**。Changes 和 History 同时显示，可拖动分隔条调整宽度。
- History 使用 **Commit List + Graph Column**，以紧凑单行显示提交，文字与展开按钮紧贴每行实际占用的轨道，过长内容截断。图中显示分叉、合并与 HEAD；分支和标签引用移到悬停详情。
- 每条历史提交下可独立展开变更文件，显示文件名、目录与原有的 `+ / − / ·` 状态图标；展开期间轨道线连续贯穿文件区域。展开状态在刷新后保留。
- History 悬停显示完整提交说明、描述、用户名、时间、增删行数、SHA、父提交和引用信息。用户名与时间放在同一行。统计按需读取并缓存；作者只显示用户名。选中提交仅高亮标题行，点击变更文件才单独高亮该文件。
- 右侧顶部将用户名、时间放在提交 Summary 上方，SHA、父提交和完整描述整合在同一滚动区域，去掉头像及描述内部的独立滚动框；下方为共享 Diff。选择 Changes 或历史提交中的文件会切换该 Diff 的内容，保留两边的文件选择。
- 分栏去掉 Changes / History 标题；提交表单去掉“创建提交”“本次提交包含…”和“已选”提示，Summary、Description 与提交按钮采用更小的高度和间距，表单仍可拉伸。
- Changes 下方提供当前仓库资源管理器，显示已跟踪文件及未忽略的未跟踪文件；选择文件或目录可筛选相关历史节点，目录包含全部子目录。
- 路径筛选保留全部已加载提交的行高、顺序与轨道线，只隐藏无关节点和提交文字；点击“全部历史”或筛选条的 × 可恢复。文件变更按当前路径匹配，合并提交与第一父提交比较。
- Changes 与资源管理器之间、提交信息与 Diff 之间、Changes 文件列表与提交表单之间可上下拖动；三栏之间可左右拖动。分隔条支持键盘方向键调整。
- 固定并排 Diff、增删行号、基础语法着色、复制 SHA、复制 Diff、选择并复制 Diff 行；已移除“并排 / 统一”切换按钮。
- Diff 按面板宽度自动换行，不显示横向滚动条；并排两侧按较高的一侧对齐，换行不改变复制得到的原始代码。
- 全局使用无箭头的极细胶囊滚动条，支持滚轮、轨道翻页与拖动。
- 所有分支 / 当前分支历史，按需加载更多提交，按说明、作者、SHA 或引用搜索定位。
- Changes 默认勾选全部变更文件，支持单文件勾选、空格键切换、全选 / 全不选，总勾选框可显示部分选择状态。
- 勾选表示本次提交包含该文件的全部变更；刷新、切换预览文件和仓库保留手动取消的选择，新出现的文件默认勾选。勾选操作不修改 Git 暂存区，提交时保留未选文件已有的暂存内容。
- 默认 Diff 展示文件相对于 HEAD 的全部变更；同时存在已暂存和未暂存修改时，也可单独查看已暂存内容。
- 文件状态使用绿色方框“+”表示新增、黄色方框圆点表示修改、红色方框“-”表示删除；菜单弹出列表使用纯白背景和蓝色悬停项。
- 顶部仓库、分支、同步按钮采用紧凑的图标与两行文字排列，高度 51、宽度约为原先一半。创建和切换分支、切换远程跟踪分支；同步按钮统一显示 Fetch、Pull（仅快进）、Push 或 Publish branch，去掉右侧下拉箭头，手动操作保留在“仓库”菜单。
- Stash 保存全部或勾选文件，可填写说明、包含新增文件；支持文件预览、Apply、Pop、删除和恢复暂存状态，复用右侧 Diff。
- 分支菜单提供 Rebase，历史提交右键可 Revert、重置到此处、变基到此处，或撤销最近一次提交并保留修改；支持 Soft / Mixed / Hard，合并提交的 Revert 需指定主线父提交。
- 同步菜单提供常规同步、合并、变基、采用远程版本和采用本地已提交版本，获取远程后显示当前分支与上游的具体差异；双方分叉时主按钮显示“解决分叉”。
- 冲突处理窗口显示当前与传入版本，支持选择一侧或编辑结果并标记解决；顶部操作条提供继续、中止，重启应用后也能检测未完成的操作。
- 修改历史或覆盖版本前保留恢复点；变基、合并和撤销前自动保存未提交改动，完成或中止后恢复。Stash 与恢复点独立于普通历史图显示。
- 后台 Git 操作、取消操作、外部文件变更自动刷新、错误信息展示。
- 可收起的 Git 助手侧栏：生成勾选文件的提交说明草稿、解释提交或 Diff、Fetch 后分析同步差异，支持流式回答和停止。
- 助手聊天支持 Markdown 标题、强调、列表、代码、引用、表格和网页链接，支持选择复制及流式更新；顶部仓库/模型提示、快捷按钮和底部状态提示已移除。
- 助手支持自定义 OpenAI 兼容服务和模型、本机模型，以及关闭 Diff 内容共享；密钥按服务地址分别存入 Windows 凭据管理器。
- 本机 stdio MCP 服务只开放当前选中的仓库。外部客户端可读取状态、历史、Diff，或提出操作计划；实际写操作需在界面审阅并执行。

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

打开已有上游的仓库或切换分支后，同步按钮先显示 Fetch；获取成功后，远程领先时变为 Pull，本地领先时变为 Push，没有差异时保持 Fetch。Fetch 只获取信息，Pull / Push 由下一次点击执行；不会仅根据打开前缓存的领先/落后数量直接决定操作。未设置上游时显示 Publish branch。按钮右侧不再另列上下箭头数量及上游名称。Fetch 使用当前分支的远程（优先上游，否则优先 origin）；“仓库”菜单保留手动 Fetch / Pull / Push 和同步策略。双方历史分叉时，主按钮打开同步策略预览；手动 Pull 仍保持仅快进策略。

“采用远程版本”将当前分支和已跟踪文件对齐到本次 Fetch 获取的上游提交，原历史、未提交文件和暂存状态保存到恢复点；忽略文件不清理，有覆盖风险时会阻止操作。“采用本地已提交版本”只上传当前 HEAD，以预览时的远程 SHA 执行 `force-with-lease`，并保留被替换的远程提交；预览后远程有新提交时推送会被拒绝，远程分支保护照常生效。两项操作均需在具体预览中明确确认。

Changes 上方的 Stash 与恢复按钮，或 Repository 菜单，可以打开 Stash 和备份列表。恢复点保存在当前仓库的 `refs/gitmanager/` 下，文件快照独立引用，删除普通 Stash 后仍可恢复。恢复时需位于备份所属分支，当前状态会先创建新的备份。应用 Stash 前工作区需清空；冲突中止只清理此次恢复引入的新文件，保留无关新增文件。二进制或非 UTF-8 冲突文件支持采用一侧版本，子模块冲突需使用外部 Git 工具。

当前分支选择器可切换本地分支，或从远程分支创建本地跟踪分支。自动刷新不会禁用选择器或忽略切换操作；如果未提交的修改会被覆盖，保留现有分支与文件，并显示 Git 错误。

## 发布

```powershell
./publish.ps1
```

输出到 `artifacts/app/GitManager.exe`，同时发布 `mcp/GitManager.Mcp.exe` 和 `mcp-config.json`。依赖 .NET 10 Windows Desktop Runtime。可用 `./publish.ps1 -OutputDirectory artifacts/app-assistant` 输出到独立目录。

应用设置保存在 `%LOCALAPPDATA%\GitManager\settings.json`；助手设置单独保存在 `assistant.json`，密钥不写入这两个文件。

## Git 助手与 MCP

点击右上角“助手”，在“设置”中填写 API 基础地址、模型名称和 API Key。模型需支持 Chat Completions 工具调用；本机地址可使用 HTTP，其余地址使用 HTTPS。连接测试会发送一次简短请求。

在聊天中要求生成提交说明、解释当前提交或 Diff、分析同步差异。提交草稿查看后点击“填入提交区”，仍由用户编辑和提交；历史右键也可要求解释提交。获取远程最新信息会先 Fetch，不会自动 Pull/Push。切换仓库或分支会停止旧分析并清除上下文；草稿填入前重新核对文件内容和勾选范围。

同步按钮下方显示实际的上次 Fetch 时间，例如 `Last fetched 27 minutes ago`，从仓库的 `FETCH_HEAD` 读取并定时更新，也支持 worktree。打开仓库仍先显示 Fetch，保留获取后再决定 Pull/Push 的流程。

MCP 默认关闭，在助手设置中启用后，复制窗口中的配置，或使用发布目录的 `mcp-config.json`。GitManager 需保持运行；MCP 服务无需模型密钥，外部客户端使用自己的模型。UI、助手和 MCP 共用仓库操作队列，实际 Git 操作复用已有恢复点及冲突处理。

助手可为同步、Rebase、Reset 和 Revert 生成计划。点击计划的“查看预览”后才可执行，覆盖历史还需明确勾选确认。过期或工作区/暂存区变化会使计划失效，同一计划不会重复执行。工具、架构及限制见 [助手接入说明](docs/assistant.md)。

## 验证

智能体检查使用独立测试仓库、本机模拟模型和真正的 MCP stdio 客户端，不调用付费模型。覆盖流式/非流式工具调用、上下文与内容共享范围、共享队列、快照校验、只审批不执行、重复请求和恢复点；界面检查覆盖侧栏、草稿及仓库切换取消。

```powershell
dotnet run --project tests/GitManager.AgentChecks
dotnet run --project tests/GitManager.UiChecks -- artifacts/preview-repository artifacts/assistant-screenshots --assistant-only
dotnet run --project tests/GitManager.UiChecks -- artifacts/preview-repository artifacts/chat-presentation-screenshots --chat-ui-only
```

Git 集成检查会创建独立测试仓库与本地 bare 远程，覆盖图连接、分页连续性、较新支线居右、支线汇合后的颜色、根提交、合并、增删统计（含重命名与二进制文件）、批量路径历史匹配、资源管理器文件范围、中文与特殊路径、重命名、暂存、混合状态、勾选文件提交、未选文件暂存保留、大批量路径、克隆、分支发布及快进拉取。新增操作检查覆盖 Stash 部分文件与新增文件、稳定 ID、暂存恢复、三种 Reset、恢复点、变基/合并/Stash 冲突的继续与中止、重启检测、合并 Revert 主线及本地/远程版本覆盖和过期租约拒绝。

```powershell
dotnet run --project tests/GitManager.Tests -- --fixture artifacts/preview-repository
```

WPF 界面检查会打开上述测试仓库，验证三栏布局、数据绑定、搜索、共享 Diff 来源切换、固定并排 Diff、选择保留、窄窗口与 12,000 行虚拟化；还会复现自动刷新与添加仓库的竞争，检查多仓库持久化和同步按钮的实际 Fetch / Pull / Push / Publish 流程。Changes 检查覆盖真实勾选框点击、部分选择状态、刷新与仓库切换、提交钩子失败后重试、未选文件暂存保留及菜单配色，并导出实际 WPF 渲染截图：

```powershell
dotnet run --project tests/GitManager.UiChecks -- artifacts/preview-repository artifacts/screenshots
```

工作区检查还覆盖分支菜单与自动刷新竞争、远程跟踪、阻止覆盖本地修改、资源树文件 / 目录选择、空历史占位、连续切换筛选和面板尺寸调整。可追加 `--workspace-only` 单独运行这些检查。

外观与 Diff 检查覆盖紧凑历史、按行轨道留白、长标题截断、悬停详情、换行、中文与 Unicode、空白新增行行号、超过 5,000 字符的连续文本、换行后复制、缩放面板、12,000 行绘制和胶囊滚动条交互。空行与非空行采用固定首行基线，并将绘制裁剪在所属行内，避免空行行号与上一行重叠。可追加 `--appearance-only` 单独运行这些检查。

操作界面检查验证 Stash 与历史共用 Diff、冲突编辑器、操作条与继续按钮、工作区和暂存状态恢复、备份列表、覆盖预览确认及文本换行符保留。Git 和 WPF 检查均可追加 `--operations-only` 单独运行新增操作流程。

常驻布局检查覆盖多提交独立展开、文件状态图标、贯穿文件区的轨道、历史 / 工作区文件复用同一个 Diff、刷新保留展开和预览来源、用户名与统计缓存。可追加 `--layout-only` 单独运行这些检查。

SkillTrust 实际案例可通过 Git 检查的 `--inspect-repository C:/Users/33612/Documents/GitHub/SkillTrust` 参数只读检查备份 / MCP 支线及分页稳定性；界面检查以该仓库作为首个参数并追加 `--review-only`，可导出修复后的轨道和提交描述截图，不切换分支或修改该仓库文件。

`artifacts/preview-repository` 是自动生成的示例仓库，包含真实 Git 分叉和合并，用于界面预览；应用不会自动创建示例数据。

## 代码结构

```text
src/GitManager.Core/
  GitRunner.cs        无 shell 拼接的 Git 进程调用与取消
  GitService.cs       仓库、历史、Diff、暂存与远程操作
  GitService.Operations.cs  变基、重置、撤销、冲突处理
  GitService.Recovery.cs    Stash、恢复点及持久操作状态
  GitService.Sync.cs        远程预览、合并与版本采用策略
  GraphLayout.cs      与 UI 无关的图轨道布局
  DiffParser.cs       Unified Diff 解析与并排行配对
src/GitManager.App/
  ViewModels/         界面状态与异步操作
  Controls/           按行绘制的提交图、按可见区域绘制的 Diff
  Themes/             浅色工作区与深色工具栏
  Dialogs/            仓库管理、Stash、恢复点、操作预览与冲突编辑器
tests/
  GitManager.Tests/   Git 与布局集成检查
  GitManager.UiChecks/ WPF 界面与截图检查
```

当前版本使用已有的 Git 凭据 / SSH 配置。GitHub OAuth 与 PR、逐行暂存、交互式 Rebase（拖动/合并/拆分提交）、图片 Diff 和完整深色主题尚未实现。
