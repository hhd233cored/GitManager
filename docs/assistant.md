# Git 助手与 MCP

内置助手采用 C#、Microsoft.Extensions.AI 和官方 OpenAI .NET 适配器，MCP 服务采用官方 C# SDK。当前固定 AI 包为 10.11.0，MCP SDK 为 2.3.0。二者共享 AI 抽象依赖，升级时应一起验证。

## 使用

1. 打开仓库，点击右上角“助手” → “设置”。
2. 填写支持 Chat Completions 和工具调用的 API 基础地址及模型名。地址包含服务要求的 `/v1` 等前缀，不包含 `/chat/completions`。可以连接云端兼容服务，或 Ollama、LM Studio 等提供的本机兼容接口。
3. 云端填写自己的 API Key，本机服务允许不填。密钥按规范化后的地址分别保存在 Windows 凭据管理器；更改地址不会把旧地址的密钥发送给新服务。清除密钥针对当前填写的地址。
4. 连接测试发送一次简短请求，不能保证该模型的所有能力；首次使用时还应验证具体模型的工具调用。服务不支持流式响应时可关闭流式显示。
5. “允许共享仓库内容”控制 Diff、源码、搜索片段与项目概况，内置助手和 MCP 同时受限；关闭后仍可读取 Git 状态及提交元数据。“启用项目概况、文件浏览、源码阅读与代码搜索”可单独关闭项目阅读，保留 Diff。额外排除路径每行一个相对文件、目录或 `*` / `?` 通配模式，对两种入口和 Diff 同时生效。内容返回给工具调用者后，可能会被发往其使用的模型服务。
6. 对话只保留在本次应用会话中，切换仓库/分支停止旧分析并清空；修改助手设置取消当前分析并清空后续使用的模型历史。

可直接在聊天中提出以下请求；文本支持 Markdown 格式、选择和复制：

- **提交说明**：依据勾选文件的全部工作区变更生成草稿，不受文件列表搜索影响，不自动创建提交。查看后填入 Summary / Description，勾选或文件内容变化时应重新生成。
- **解释提交**：读取所选提交说明、变更统计和关键文件 Diff。历史提交右键也可进入。
- **解释 Diff**：读取所选历史文件或工作区文件。首版不分析 Stash 预览。
- **同步分析**：先 Fetch，再比较当前分支与上游。分析不会自动 Pull 或 Push。
- **描述 Git 操作**：可生成同步、变基、重置或撤销计划，计划卡片可预览、取消，或由用户明确执行。
- **理解项目**：例如“解释项目架构”“登录流程经过哪些模块”“结合源码解释这次提交”。助手通过项目概况、目录、字面搜索和按行阅读取得依据。聊天中可展开“读取与检索记录”，源码引用点击后在应用内只读窗口显示行号；文件已变化时显示提示，仓库或分支已切换时旧引用失效。

侧栏保留聊天、设置、草稿和操作预览；不再展示仓库/模型标签、四个快捷按钮和底部步骤提示。Markdown 支持标题、粗体、斜体、删除线、嵌套列表、任务列表、引用、代码块、表格及网页链接。HTML 以普通文字显示，图片显示替代文字，避免自动访问外部资源。

## 结构

```text
WPF 界面 ──────────────────────────────┐
内置助手 → 类型化工具接口 ──────────────┤
外部 MCP 客户端 → stdio MCP → 本机管道 ─┤
                                      ↓
                  RepositoryCoordinator + RepositoryTools
                                      ↓
                    预览 / 校验 / 恢复点 / GitService
                                      ↓
                                  Git CLI
```

- `GitManager.Services`：共享调度、工具、计划、内容访问策略和本机通信，不依赖模型供应商或 WPF。Git 状态/写操作与 UI 共用串行队列；项目阅读在队列内捕获仓库身份及策略后，到后台执行，最多两个并行读取，返回前再次校验身份及策略。
- `GitManager.Agent`：模型适配、工具循环、上下文预算、流式输出及草稿。未来接入 LangChain/LangGraph 时可以沿用相同工具接口。
- `GitManager.Mcp`：独立 stdio 进程，只桥接到运行中的 GitManager，不另起 Git 执行实例。日志写 stderr，stdout 保留给 MCP。
- WPF：连接设置、Windows 凭据、可拉伸侧栏、计划审批、草稿填入及既有冲突窗口。

## 配置 MCP

在助手设置中启用本机 MCP，复制窗口提供的配置。发布脚本也会生成 `mcp-config.json`，例如：

```json
{
  "mcpServers": {
    "gitmanager": {
      "command": "C:\\path\\to\\GitManager\\mcp\\GitManager.Mcp.exe",
      "args": []
    }
  }
}
```

具体导入位置由 MCP 客户端决定。GitManager 需保持运行并打开允许访问的仓库。本机管道限当前 Windows 用户使用，每用户启用一个宿主；其他窗口不能同时接管同名管道。停用 MCP 后撤销访问。

客户端先调用 `get_active_repository` 获得不透明仓库 ID，后续调用携带该 ID。切换仓库或分支使旧 ID 失效。工具不接受任意仓库路径。

| 工具 | 功能 |
| --- | --- |
| `get_active_repository` | 当前开放的仓库及 ID |
| `get_repository_status` | 分支、状态、冲突及缓存同步信息，最多 200 个文件 |
| `get_commit_history` | 当前分支历史，`limit` 最多 50，`skip` 分页，不提供作者邮箱 |
| `get_commit_details` | 完整 SHA 对应的提交、统计和文件列表 |
| `get_diff` | 相对文件路径；可选完整 `commitId` 或工作区 `staged` |
| `get_project_overview` | 工作区静态目录概要、扩展名统计、项目清单及 README 片段，附来源和哈希 |
| `list_files` | Git 文件清单的目录浏览；`filePath` 为相对目录或空根目录，`depth` 1–8，`limit` 最多 200，按 `nextSkip` 分页 |
| `read_file` | 工作区文件按 1 起始的 `startLine/endLine` 阅读，每次最多 200 行、约 16,000 字符，附行号、SHA256、截断状态及 `nextLine` |
| `search_code` | `query` 按字面匹配，可用 `filePath` 限定目录/文件，`matchCase` 控制大小写；每页最多 100 处，附行号、哈希和引用 |
| `get_sync_analysis` | `refreshRemote=true` 先 Fetch，否则明确标为缓存 |
| `create_operation_plan` | 返回计划 ID、固定 SHA 和具体影响 |
| `apply_operation_plan` | 转为等待界面审批，不执行 Git 写操作 |
| `get_operation_status` | 查询等待、完成、失败、失效或冲突等状态 |

计划 action：`sync`、`sync_merge`、`sync_rebase`、`sync_remote`、`sync_local`、`rebase`、`reset_soft`、`reset_mixed`、`reset_hard`、`revert`。同步系列先 Fetch，其余传入 `target`。撤销合并提交需指定 `mainline`。

## 执行与恢复

计划保存在内存，15 分钟后不能执行，重启后旧 ID 无效。执行前核对仓库选择、分支、HEAD、完整暂存条目和所有变更文件内容；状态变化需重新生成。实际 Git 调用还会验证原有预览及远程租约。

执行前标为 running；完成、失败、取消等终态不自动重试，重复请求不重复执行。中途退出后以 Git 实际状态及已有恢复点为准，不承诺跨程序崩溃的事务回滚。元数据写入 `%LOCALAPPDATA%\GitManager\operations.jsonl`，不保存模型密钥、对话或文件内容。

修改历史前复用现有恢复点。Rebase / Merge / Revert 遇到冲突后，通过原有冲突窗口、继续和中止流程处理。写操作需用户在具体预览中执行；覆盖文件/历史还需勾选确认。

## 首版范围

- 快捷入口由程序先读取数据；同步分析保证先尝试 Fetch，再交给模型解释。提交草稿先读取最多 12 个勾选文件的 Diff，模型可在预算内补充；未读取文件和截断会在草稿卡片明确标注。
- 单文件 Diff 最多约 18,000 字符，历史说明及文件列表有上限并标记截断。普通聊天支持最多 32 次数据工具调用、约 160,000 字符工具上下文、16 轮模型工具循环及 5 分钟；提交草稿等专用任务维持 16 次、100,000 字符、8 轮和 3 分钟。提交草稿不使用项目工具扩大所选文件范围。
- 工作区单文件超过 1 MB、链接文件/目录，以及常见密钥路径（`.env`、私钥、证书、凭据等）不向助手提供内容。不能识别所有自定义敏感文件，应按仓库数据要求选择模型服务。
- 仓库内容和工具结果视为数据，不能代表操作授权。模型只有白名单工具，没有通用 Shell 或任意 Git 命令入口。
- 内容共享开关和额外排除路径由服务端执行，MCP 不能绕过。默认不读 `.git`、常见密钥、本机凭据、链接目录/文件、依赖和构建目录；Windows 读取前还校验已打开文件句柄的实际路径。`.gitignore` 控制未跟踪文件是否进入清单，不能替代敏感路径设置。
- 项目工具读取 **当前工作区**，包含未提交修改，不读取任意历史版本。单文件最多 1 MB，支持 UTF-8 与带 BOM 的 UTF-16；不读二进制。文件清单最多 50,000 个可读文件、Git 输出最多 4 Mi 字符；单次搜索最多扫描 3,000 个文件或约 24 Mi 文本字符，工具限时 25 秒。`scanLimited=true` 时需缩小目录继续查找，不应将未搜到解释为项目中不存在。
- 项目概况只静态读取清单和文档，不加载项目、不运行构建配置。概况片段总计约 16,000 字符；代码搜索是文本匹配，不是语义引用或完整调用图。Roslyn/LSP、向量检索、通用终端及构建/测试任务尚未接入。
- 内容缓存位于内存，按 SHA256 复用解析结果并限制容量；每次读取仍重新校验文件字节，不能仅凭修改时间命中。项目概况按仓库/分支身份、文件清单、访问策略和文档哈希缓存，切换上下文或策略使缓存失效。`%LOCALAPPDATA%\\GitManager\\project-cache` 仅保存概况来源路径、分支与哈希记录，不保存源码、对话或模型摘要；重启后重新读取源码生成概况。
- 本次实现 MCP **服务端**；连接第三方 MCP、自动改代码、生成冲突补丁和可持久化多步骤工作流留作扩展。

参考：[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/ichatclient)、[MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)、[Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。
