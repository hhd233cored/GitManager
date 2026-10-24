# Git 助手与 MCP

内置助手采用 C#、Microsoft.Extensions.AI 和官方 OpenAI .NET 适配器，MCP 服务采用官方 C# SDK。当前固定 AI 包为 10.11.0，MCP SDK 为 2.3.0。二者共享 AI 抽象依赖，升级时应一起验证。

## 使用

1. 打开仓库，点击右上角“助手” → “设置”。
2. 填写支持 Chat Completions 和工具调用的 API 基础地址及模型名。地址包含服务要求的 `/v1` 等前缀，不包含 `/chat/completions`。可以连接云端兼容服务，或 Ollama、LM Studio 等提供的本机兼容接口。
3. 云端填写自己的 API Key，本机服务允许不填。密钥按规范化后的地址分别保存在 Windows 凭据管理器；更改地址不会把旧地址的密钥发送给新服务。清除密钥针对当前填写的地址。
4. 连接测试发送一次简短请求，不能保证该模型的所有能力；首次使用时还应验证具体模型的工具调用。服务不支持流式响应时可关闭流式显示。
5. “允许将所选文件的 Diff 发往上述模型服务”控制内置助手能否获得文件内容；关闭后仍可读取仓库及提交元数据。对话保留在本次应用会话中，按仓库/分支切换清空。

可直接在聊天中提出以下请求；文本支持 Markdown 格式、选择和复制：

- **提交说明**：依据勾选文件的全部工作区变更生成草稿，不受文件列表搜索影响，不自动创建提交。查看后填入 Summary / Description，勾选或文件内容变化时应重新生成。
- **解释提交**：读取所选提交说明、变更统计和关键文件 Diff。历史提交右键也可进入。
- **解释 Diff**：读取所选历史文件或工作区文件。首版不分析 Stash 预览。
- **同步分析**：先 Fetch，再比较当前分支与上游。分析不会自动 Pull 或 Push。
- **描述 Git 操作**：可生成同步、变基、重置或撤销计划，计划卡片可预览、取消，或由用户明确执行。

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

- `GitManager.Services`：共享调度、工具、计划和本机通信，不依赖模型供应商或 WPF。当前一个宿主串行处理 UI、工具操作及仓库切换。
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
- 单文件 Diff 最多约 18,000 字符，历史说明及文件列表有上限并标记截断。内置助手每任务最多 16 次数据工具调用、约 100,000 字符工具上下文及 8 轮模型工具循环，最多 3 分钟。
- 工作区单文件超过 1 MB、链接文件/目录，以及常见密钥路径（`.env`、私钥、证书、凭据等）不向助手提供内容。不能识别所有自定义敏感文件，应按仓库数据要求选择模型服务。
- 仓库内容和工具结果视为数据，不能代表操作授权。模型只有白名单工具，没有通用 Shell 或任意 Git 命令入口。
- 内置助手的 Diff 共享开关针对配置的模型连接。启用 MCP 后，外部客户端可通过相同路径规则读取当前仓库 Diff，使用其自己的模型。
- 本次实现 MCP **服务端**；连接第三方 MCP、自动改代码、生成冲突补丁和可持久化多步骤工作流留作扩展。

参考：[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/ichatclient)、[MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)、[Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。
