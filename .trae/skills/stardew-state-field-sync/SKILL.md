---
name: stardew-state-field-sync
description: 为星露谷AI陪玩Mod新增游戏状态字段，贯通C# SMAPI端与Python FastAPI端。添加GameStateData字段或扩展状态感知时使用。非字段类改动不适用。
---

# 星露谷状态字段同步

为星露谷AI陪玩Mod新增一个游戏状态字段的全链路流程。每个新字段都必须覆盖所有层，漏掉任何一层都会导致 C#↔Python 契约断裂。

## 硬约束

- **C# `GameStateData` 与 Python `ChatRequest` 字段必须同步**，不匹配会导致反序列化失败。
- **Python 字段必须给默认值**，这样没传该字段的旧版 C# 不会崩。
- `chat_node` 用 `ps.get("key", default)` 读状态，禁止直接 `ps["key"]`。
- 改完 C# 必须 `dotnet build`；改完 Python 必须 `ast.parse`。
- Commit message 用临时文件 `git commit -F <file>`——PowerShell 5 没有 heredoc。

## 工作流程

### 1. 确认游戏 API（SDV 版本变更或 API 不熟时）

用临时的 `MetadataLoadContext` 反射项目（net8.0）加载 `D:\Steam\steamapps\common\Stardew Valley\Stardew Valley.dll`，确认成员的准确类型与签名。注意 **Netcode 类型**：

- `NetBool` / `NetInt` / `NetString` / `NetFloat`——通过 `.Value` 访问。
- `NetStringDictionary<T,TRef>`——用 `.Pairs` 遍历（返回 `KeyValuePair<string, T>`）。
- `NetObjectList<T>`——实现了 `IList<T>`，可直接用 LINQ。
- `Inventory`（`player.Items`）——实现了 `IList<Item>`。

### 2. C# 端（`mod/ModTemplate/ModEntry.cs`）

1. 给 `GameStateData` 加属性，带默认值和 XML 文档注释。
2. 在 `SendToBackendAsync` 的对象初始化器里加收集代码（接在最后一个字段后、`};` 前）。
3. 把字段追加到 `Monitor.Log` 的状态打印行。
4. 补需要的 `using`（如 `System.Linq`）。
5. 在 `ModTemplate/` 目录跑 `dotnet build`——必须 0 错误。

### 3. Python 端

**`backend/main.py`** 的 `ChatRequest` 模型：
- 加字段并给默认值（空串 / 0 / false），保证向后兼容。

**`backend/main.py`** 的 `player_state` 字典：
- 加 snake_case 键，从 `req.<Field>` 映射。

**`backend/src/aipy/agent/graph.py`** 的 `chat_node`：
- 在中文状态块里给新字段拼一行展示。
- 空值的字段不要显示该行（用 `line = f"- X：{v}\n" if v else ""`，再以 `f"{line}"` 注入）。

**`backend/src/aipy/agent/graph.py`** 的 `retrieve_node`（仅当该字段应影响检索时）：
- 把状态作为前缀拼进 query，例如 `[春季 晴天 Farm] 玩家原话`。
- C# 传的季节是英文，拼 query 前复用已有的 `season_cn` 映射翻译成中文。

### 4. 验证

- C#：在 `ModTemplate/` 跑 `dotnet build`。
- Python：`python -c "import ast; ast.parse(open('backend/main.py').read()); ast.parse(open('backend/src/aipy/agent/graph.py').read())"`。（本机若 `python` 不在 PATH，用 Coze 运行时的 python：`C:\Users\WIN\.coze\runtimes\...\python\python.exe`。）

### 5. 同步文档

- `README.md`：特性清单行、`/chat` JSON 示例、字段表行、检索说明（若改了）、路线图勾选。
- `mod/NOTES.md`：`GameStateData` 字段数行、新增完成记录（写明 API 坑点如 NetBool）、接口字段列表。
- `mod/DESIGN.md`：状态感知已实现清单、检索说明（若改了）、路线图勾选。

### 6. 提交

把 message 写入临时文件，然后：

```
git add <files>
git commit -F <temp-file>
```

由用户手动 push。
