# 星露谷 AI 陪玩 Mod（Stardew AI Companion）

一个由 **SMAPI Mod（C#）+ 本地 Python AI 后端（FastAPI + LangChain + LangGraph）** 组成的星露谷物语 AI 陪伴系统，**支持 RAG 知识库检索**（ChromaDB + bge-m3）与**多轮对话记忆**，对话模型**本地 Ollama / 云端 API（DeepSeek、智谱、通义、Kimi）可一键切换**。

两种对话方式：

- **原版聊天框**：游戏中按 **T** 打开原版聊天框，输入 `AI 海莉喜欢什么礼物` 回车——AI 回复直接显示在聊天框里（浅蓝色，可翻历史）；不带 `AI ` 前缀的消息一切照原版走
- **F7 快捷测试**：轮流发送内置的 5 个测试问题，回复显示在 HUD，用于调试

AI 会实时读取游戏状态（名字、体力、血量、时间、日期、季节、天气、地点、金币），检索本地游戏知识库，记得本次会话聊过的内容（多轮记忆），并给出基于资料的回答。

> **最终目标**：让一个漂浮在玩家身旁的小机器人陪伴你冒险——它感知玩家状态机，结合游戏知识库（RAG）进行多轮智能对话。

---

## 当前进度（v0.7 · 多轮记忆）

**✅ 已完成：**
- 端到端通信（C# ↔ Python，HTTP + JSON）
- 游戏内自由提问：原版聊天框输入 `AI 问题`（Harmony 拦截提交，回复进聊天框）；F7 保留为快捷测试
- RAG 知识库（ChromaDB + bge-m3 嵌入，已收录作物 / 鱼类 / NPC 礼物）
- 游戏状态感知：名字 / 体力 / 血量 / 游戏时间 / 日期（1-28）/ 季节 / 是否下雨 / 地点 / 金币
- LLM 配置化（BYOK 基础）：`config.json` 切换 provider——本地 `ollama`（免 Key）或云端 `deepseek` / `zhipu` / `qwen` / `kimi`（OpenAI 兼容接口，自带 Key）
- 多轮对话记忆（LangGraph + SQLite 持久化）：AI 记得本次会话聊过的内容，按玩家名隔离历史，**重启后端也不丢**；上下文窗口只带最近 10 轮（完整历史仍存 SQLite，不丢数据）

**⏳ 规划中：**
- 浮动精灵 + 主动提醒
- 继续扩展状态（背包 / 当前工具 / NPC 好感 / 当日任务）
- 游戏内配置界面（当前需手动编辑 config.json）；模型参数（temperature）可配置

## 架构

```
[按 T 聊天框] --Harmony 拦截--> [C# Mod (ModEntry.cs)]
[按 F7 测试]  -----------------> [C# Mod (ModEntry.cs)]
                                      |
                                      v HTTP POST /chat
                              [FastAPI :8000]
                                      |
                                      v
                         [LangGraph 对话图: retrieve -> chat]
                                      |
                                      v
                    [ChromaDB 检索 (bge-m3, top-3)]
                                      |
                                      v 参考资料 + 历史消息
                              [LangChain 拼提示词]
                                      |
                                      v
                         [LLM 工厂: llm_factory.py]
                          /                      \
                         v                        v
              [本地 Ollama qwen2.5:3b]   [云端 OpenAI 兼容 API]
                         \                      /
                          v                    v
                         [主线程任务队列]
                          /              \
                         v                v
              [聊天框消息(浅蓝)]    [HUD 消息(仅 F7)]
```

**关键设计**：
- **双入口复用同一套后端流程**：聊天框提问经 Harmony 补丁（`ChatBox.textBoxEnter(string)` 的 Prefix）截获，`"AI "` 前缀才拦截，其他消息原样放行；F7 测试问题池逻辑独立保留
- **LLM 工厂模式**：[llm_factory.py](AIpy/src/aipy/llm_factory.py) 启动时读 `config.json`，按 `provider` 创建 `ChatOllama` 或 `ChatOpenAI`（四个云端厂商的 base_url 已内置预设）；嵌入模型始终用本地 Ollama 的 bge-m3，与对话模型选型无关
- **游戏内配置（GMCM）**：玩家 ESC → Mod 选项 → 填厂商/Key/模型 → 保存时 C# 推送到后端 `POST /config` → 写 `config.json` + 重建 LLM 注入对话图，无需重启；后端未启动时配置只存本地 ModConfig.json，启动后需重新保存
- `await` 之后的代码运行在线程池后台线程，所有 UI 操作（HUD / 聊天框）通过主线程任务队列（`Queue<Action>` + `UpdateTicked`）调度回游戏主线程执行，避免随机崩溃
- 全局复用单个 `HttpClient`，15 秒超时，请求期间游戏照常运行不卡顿
- 向量库在后端启动时只加载一次；每次请求先做向量检索（top-3），把参考资料片段和游戏状态一起注入提示词
- **多轮记忆**：`/chat` 请求进入 LangGraph 对话图（`retrieve → chat` 两个节点），对话历史由 `add_messages` reducer 自动累积、`AsyncSqliteSaver` 按 `thread_id`（玩家名）落盘到 `AIpy/data/checkpoints.db`——重启后端历史不丢；删掉该文件即可清空全部记忆
- System Prompt 严格约束：游戏知识必须依据参考资料回答，资料里没有就直说"我不太清楚"；向量检索不擅长的逻辑条件（如"晴天"误召回"雨天"鱼）交给 LLM 二次筛选
- 未进入存档时发起对话会友好提示，不会崩溃

## 目录结构

```
AI_Projects/
├── AIpy/                      # Python AI 后端
│   ├── main.py                # 入口：FastAPI + LangGraph 对话图调度，POST /chat + POST /config
│   ├── config.example.json    # LLM 配置模板（随仓库分发，真实 config.json 已 gitignore）
│   ├── pyproject.toml         # uv 依赖管理
│   ├── wiki_data/             # 知识库源文件（Markdown，随仓库分发）
│   │   ├── crops.md           # 四季作物
│   │   ├── fish.md            # 鱼类图鉴
│   │   └── gift.md            # NPC 礼物偏好
│   ├── src/aipy/
│   │   ├── agent/             # LangGraph 对话图（多轮记忆）
│   │   │   ├── state.py       # 图状态：messages（自动累积历史）/ player_state / context / error
│   │   │   └── graph.py       # retrieve → chat 两节点 + AsyncSqliteSaver 记忆持久化
│   │   ├── llm_factory.py     # LLM 工厂：读/写 config.json 创建本地/云端对话模型
│   │   ├── config.json        # 【自行创建，不入库】真实配置（含 API Key）
│   │   └── build_knowledge_base.py  # 建库脚本：wiki_data → chroma_db
│   ├── data/                  # 对话历史 SQLite（checkpoints.db，本地生成，已 gitignore）
│   └── chroma_db/             # 向量库（脚本本地生成，已 gitignore）
├── stardewAI/                 # C# SMAPI Mod
│   ├── ModTemplate/
│   │   ├── ModEntry.cs        # Mod 入口：F7/聊天框两条对话流程、GMCM 配置、线程队列、HTTP 请求
│   │   ├── ChatBoxTextBoxEnterPatch.cs  # Harmony 补丁：拦截聊天框 "AI " 消息
│   │   ├── ModConfig.cs       # GMCM 配置模型：Provider / ApiKey / Model
│   │   ├── libs/              # GMCM 前置：IGenericModConfigMenuApi.cs（入库，用 shared/ 稳定版）+ GenericModConfigMenu.dll（不入库，见编译章节）
│   │   ├── manifest.json      # SMAPI 清单（UniqueID: yz.StardewAIAssistant）
│   │   └── StardewAIAssistant.csproj
│   ├── DESIGN.md              # 设计蓝图（模块规划与数据流）
│   └── NOTES.md               # 开发笔记（踩坑记录，值得一看）
└── .gitignore
```

## 技术栈

**C# 端（SMAPI Mod）**
- .NET 6 / C#、SMAPI 4.x、`System.Net.Http.HttpClient` + `System.Text.Json`
- NuGet：`Pathoschild.Stardew.ModBuildConfig` 4.1.0
- Harmony（SMAPI 4 自带的 `0Harmony.dll`）：Prefix 补丁拦截原版聊天框提交
- GenericModConfigMenu（可选，玩家自行安装）：游戏内配置界面，保存时推送到后端

**Python 端（AI 后端）**
- Web：FastAPI + Uvicorn，依赖管理用 uv
- LLM 编排：LangChain（`langchain-ollama` / `langchain-openai` / `langchain-chroma` / `langchain-text-splitters`）+ LangGraph（对话图 + AsyncSqliteSaver 多轮记忆持久化）
- LLM 供应方（config.json 切换）：本地 Ollama，或云端 OpenAI 兼容接口（DeepSeek / 智谱 GLM / 通义千问 / Kimi）
- 向量库：ChromaDB（本地持久化，无需起数据库服务）
- 嵌入模型：本地 Ollama `bge-m3`（知识库向量化，不随对话 provider 改变）

## 环境要求

| 组件 | 版本要求 |
|---|---|
| 星露谷物语 | 1.6.15（Steam 版） |
| SMAPI | 4.x（MinimumApiVersion 4.0.0） |
| .NET SDK | 6.0（编译 Mod） |
| Python | ≥ 3.11，使用 [uv](https://docs.astral.sh/uv/) 管理依赖 |
| 对话模型（二选一） | **本地模式**：[Ollama](https://ollama.com/)（默认端口 11434）+ `qwen2.5:3b`；**云端模式**：DeepSeek / 智谱 / 通义 / Kimi 任一厂商的 API Key |
| 嵌入模型（必需本地） | Ollama 的 `bge-m3`——无论对话用本地还是云端，知识库向量化都走本地 Ollama，必须 `ollama pull bge-m3` |

## 快速开始

### 1. 准备本地 Ollama

安装 [Ollama](https://ollama.com/download)。**嵌入模型 `bge-m3` 无论用哪种对话模式都必须拉取**（知识库向量化固定走本地）；对话模型仅在"本地模式"下需要：

```bash
ollama pull bge-m3       # 嵌入模型（必做，知识库向量化用）
ollama pull qwen2.5:3b   # 对话模型（仅本地模式需要）
```

Ollama 安装后一般会自动在后台运行（默认端口 `11434`）；若没有，手动执行 `ollama serve`。

### 2. 准备知识库（首次必做）

知识库源文件放在 `AIpy/wiki_data/`，为 **UTF-8 编码的 Markdown**：

- 建库脚本按 `##`（二级标题）切块，**每个 `##` 小节成为一个 chunk**
- 每个 chunk 必须自包含：标题里的限定信息（如"春季作物""XX 的礼物偏好"）要留在正文中，不能只写一个孤立的"# 春季作物"
- 玩家可能提问的关键词（如"礼物""晴天"）应直接出现在对应 chunk 的标题或正文里，否则向量检索召不回
- 现有三份：[crops.md](AIpy/wiki_data/crops.md)（作物）、[fish.md](AIpy/wiki_data/fish.md)（鱼类）、[gift.md](AIpy/wiki_data/gift.md)（NPC 礼物），可直接照格式扩充

运行建库脚本（在 `AIpy/` 目录下）：

```bash
uv sync                                                      # 首次安装依赖
uv run python src/aipy/build_knowledge_base.py
```

脚本会：读取 `wiki_data/*.md` → 按 `##` 切块（保留标题）→ bge-m3 向量化 → 写入 `chroma_db/`，末尾还会跑一条自测查询打印命中块。

> 注意：
> - 脚本采用**全量重建**（先删除旧 `chroma_db/` 再写入），修改 wiki 后重跑即可，不会产生重复数据
> - 首次向量化较慢（bge-m3 要逐个 chunk 编码），属正常现象
> - `chroma_db/` 是本地生成物，**不随仓库分发**（已加入 `.gitignore`），换机器或重新克隆后必须重新建库，否则后端启动/检索会报错

### 3. 配置对话模型

**方式一（推荐）：游戏内 GMCM 配置**
1. 安装 [GenericModConfigMenu](https://www.nexusmods.com/stardewvalley/mods/5098)（可选，但推荐）
2. 启动游戏 → ESC → 选项 → 往下找到 "Mod 选项" → Stardew AI Assistant
3. 填三项：
   - **AI 厂商**：`deepseek` / `qwen` / `zhipu` / `kimi` / `ollama`
   - **API Key**：云端必填，本地 Ollama 留空
   - **模型名**：如 `deepseek-chat` / `qwen-plus` / `glm-4-flash` / `qwen2.5:3b`
4. 保存 → C# 自动推送到后端 `POST /config` → 写 `config.json` + 重建 LLM，**无需重启后端**
5. 后端未启动时保存：配置只写本地 `ModConfig.json`，HUD 提示"后端未启动，配置已保存到本地；启动后端后请重新保存"

**方式二：手动编辑配置文件（无 GMCM 时）**

后端启动时从 **`src/aipy/config.json`** 读取对话模型配置。仓库只提供模板 [config.example.json](AIpy/config.example.json)，真实配置含 API Key，已加入 `.gitignore` 不会被提交。

把模板复制到 `src/aipy/` 下并改名（⚠️ 注意目标位置在 `src/aipy/`，不是项目根目录——工厂按自己所在目录找 config.json）：

```bash
# 在 AIpy/ 目录下
cp config.example.json src/aipy/config.json
```

然后按所选模式编辑 `src/aipy/config.json`：

**本地模式（免 Key，默认推荐先跑通）：**

```json
{
  "provider": "ollama",
  "model": "qwen2.5:3b",
  "base_url": "http://127.0.0.1:11434"
}
```

**云端模式（响应更快，需自备 API Key）：**

```json
{
  "provider": "deepseek",
  "api_key": "sk-你的密钥",
  "model": "deepseek-chat"
}
```

- `provider` 可选：`ollama` / `deepseek` / `zhipu` / `qwen` / `kimi`，云端四家的接口地址已在 [llm_factory.py](AIpy/src/aipy/llm_factory.py) 内置预设，不用自己填 base_url
- 各家模型名填对应官方文档的型号（如智谱 `glm-4-flash`、通义 `qwen-plus`、Kimi `moonshot-v1-8k`）
- 缺少 config.json 或云端模式没填 Key：后端**仍能启动**（用 PlaceholderLLM 占位，回复提示"请先在游戏里配置"），收到 GMCM 推送后自动切换为真实模型

### 4. 启动 AI 后端

```bash
cd AIpy
uv run uvicorn main:app --port 8000      # 启动服务（调试可加 --reload）
```

启动日志出现「向量库加载完成」和 `[LLM] provider=..., model=...` 即表示知识库与对话模型均正常载入。

> 注意命令必须是 `uv run uvicorn ...`，直接写 `uv uvicorn` 会报 `unrecognized subcommand 'uvicorn'`。

### 5. 编译并部署 Mod

```bash
cd stardewAI/ModTemplate
dotnet build
```

- 编译前请修改 [StardewAIAssistant.csproj](stardewAI/ModTemplate/StardewAIAssistant.csproj) 中的 `GamePath` 为你的游戏安装路径
- **首次编译前需准备 GMCM 前置 dll**：从 [GenericModConfigMenu 发布页](https://www.nexusmods.com/stardewvalley/mods/5098)下载并解压，把 `GenericModConfigMenu.dll` 放到 `stardewAI/ModTemplate/libs/` 下（该 dll 不入库；`libs/IGenericModConfigMenuApi.cs` 接口文件已随仓库提供，取自 GMCM 仓库 `shared/` 稳定版）
- build 成功后 DLL 会**自动复制**到 `Mods/StardewAIAssistant` 目录
- ⚠️ 游戏/SMAPI 开着时 build 会因 DLL 被占用而失败，请先关闭游戏
- 只想验证语法、不部署：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`

### 6. 开始对话

1. 启动 SMAPI 进入游戏并读取存档（主菜单中发起对话只会得到提示）
2. **聊天框提问（主要方式）**：按 **T** 打开原版聊天框 → 输入 `AI ` 开头的消息（如 `AI 海莉喜欢什么礼物`）→ 回车
   - 聊天框先出现浅蓝色「AI伴侣正在思考中...」，随后出现 `AI伴侣: ...` 回复，可翻看聊天历史
   - 只输入 `AI` 没有问题时会收到用法提示；不带 `AI ` 前缀的消息完全照原版处理
3. **F7 快捷测试（调试用）**：每按一次轮流发送内置问题池中的一个问题（共 5 个），回复显示在 HUD
4. SMAPI 控制台与后端终端可查看状态收集、检索命中块数与返回内容

## 通信协议（C# ⇄ Python）

### POST /chat（对话）

`POST http://127.0.0.1:8000/chat`

**请求**（PascalCase，与 C# `System.Text.Json` 默认序列化对齐）：

```json
{
  "PlayerName": "农夫", "Stamina": 87.5, "Health": 100,
  "GameTime": 1330, "DayOfMonth": 12,
  "Season": "summer", "IsRaining": false,
  "Location": "Farm", "Money": 5320,
  "Message": "海莉喜欢什么礼物？"
}
```

字段说明：

| 字段 | 类型 | 含义 |
|---|---|---|
| `PlayerName` | string | 玩家名字 |
| `Stamina` | float | 当前体力 |
| `Health` | int | 当前血量（上限 100） |
| `GameTime` | int | 游戏内时间，`600` = 早上 6:00 |
| `DayOfMonth` | int | 当月第几天（1-28，星露谷每月 28 天） |
| `Season` | string | 季节：`spring` / `summer` / `fall` / `winter` |
| `IsRaining` | bool | 今天是否下雨 |
| `Location` | string | 当前地点（如 `Farm`） |
| `Money` | int | 当前金币 |
| `Message` | string | 玩家问题 |

- `Message` 为玩家问题（主要来自聊天框 `AI ` 提问，F7 测试时来自内置问题池）；后端优先用它做向量检索，为空时回退为当前场景描述（地点/时间/体力）
- 其余状态字段**不参与检索**，由后端整理成结构化中文状态块（季节会译为"春季"、`IsRaining` 译为"正在下雨/天气晴朗"）注入 LLM 提示词

**响应**（小写键名，C# 端通过 `PropertyNameCaseInsensitive` 映射）：

```json
{ "reply": "你好呀 农夫！...", "emotion": "happy" }
```

### POST /config（GMCM 推送配置）

`POST http://127.0.0.1:8000/config`

**请求**（snake_case，与后端 config.json 对齐）：

```json
{ "provider": "deepseek", "api_key": "sk-xxx", "model": "deepseek-chat" }
```

- `provider` 白名单：`ollama` / `deepseek` / `zhipu` / `qwen` / `kimi`，其他值返回 400
- `provider != ollama` 时 `api_key` 必填，`model` 必填；校验失败返回 400 + 错误详情
- 成功：写 `src/aipy/config.json` → 重建 LLM → 重新注入对话图，返回 `{"status": "ok"}`；**对话历史（SQLite）保留，不重建 graph**
- C# 端 GMCM 保存时自动推送；后端未启动时 C# 只写本地 `ModConfig.json`

注意事项：
- `/chat` 的 `GameTime` 为游戏内时间，`600` 表示早上 6:00
- `/chat` 请求字段名是**故意**用 PascalCase 对齐 C# 输出的，修改字段时两端必须同步修改

## Roadmap

- [x] 接入真实 LLM：本地 Ollama + LangChain（`qwen2.5:3b`）
- [x] RAG 知识库：ChromaDB + bge-m3，已收录作物 / 鱼类 / NPC 礼物
- [x] 扩充状态感知（第一批）：季节、天气、日期、金币、血量（C# 收集 + Python 注入提示词）
- [x] 游戏内自由提问：借原版聊天框实现（按 T → `AI 问题` → Harmony 拦截），绕开了自研输入框的中文 IME 难题；回复进聊天框可翻历史
- [x] LLM provider / 模型可配置：`config.json` + LLM 工厂，支持 ollama / deepseek / zhipu / qwen / kimi（BYOK 基础）
- [x] 游戏内配置界面（GMCM，2026-10-14）：ESC → Mod 选项 → 填厂商/Key/模型 → 保存推送后端 `POST /config`，写文件 + 重建 LLM，无需重启；无 GMCM 时可手动编辑 `src/aipy/config.json`
- [ ] 模型参数可配置化（`temperature=0.7` 仍写死；检索 top-k 已挪进 `aipy/config.py`）
- [ ] 漂浮机器人：贴图渲染、跟随玩家移动、待机动画
- [ ] 防连点：思考中重复提问应忽略或排队
- [ ] 扩充状态感知（第二批）：背包、当前工具、NPC 好感、当日任务；并让季节/天气参与检索 query
- [x] 多轮对话记忆：LangGraph 对话图 + AsyncSqliteSaver（SQLite 落盘 `AIpy/data/checkpoints.db`），按玩家名隔离历史，重启后端不丢
- [ ] F7 / `AI ` 前缀改为可配置，避免与其他 Mod 冲突
- [ ] `emotion` 字段驱动机器人表情 / 立绘变化

## 常见问题

| 现象 | 原因与解决 |
|---|---|
| 启动后端报 `缺少配置文件 .../config.json` | 还没创建配置：`cp config.example.json src/aipy/config.json` 再编辑；⚠️ 必须放在 `src/aipy/` 下（与 llm_factory.py 同目录），放项目根目录不生效；**v0.8 起后端无配置也能启动**（PlaceholderLLM 占位，回复提示去游戏里配置） |
| 云端模式报 401 / 认证失败 | API Key 没填、填错或欠费；检查 `src/aipy/config.json` 的 `api_key` 与 `model` 名称；或检查 GMCM 里填的 Key 是否含空格 |
| 改了 config.json 没反应 | 配置只在后端**启动时读取一次**，改完必须重启 uvicorn |
| AI 回复驴唇不对马嘴 / 像串了别人的对话 | 多轮记忆按玩家名（thread_id）隔离：改了角色名 = 新会话；测试残留的旧历史会一直保留，想让 AI"失忆"就删 `AIpy/data/checkpoints.db` 后重启后端 |
| `uv uvicorn` 报 `unrecognized subcommand` | 少了 `run`，正确命令是 `uv run uvicorn main:app --port 8000` |
| 输入 `AI 问题` 被当成普通聊天发出去 | Harmony 补丁未生效：看 SMAPI 启动日志有无补丁加载记录；确认 `dotnet build` 后 Mods 目录 DLL 已更新 |
| HUD 提示「AI大脑连接失败」 | Python 后端未启动或端口不是 8000，先启动后端 |
| 后端日志报 Ollama 连接错误 | Ollama 服务未运行（默认端口 11434），执行 `ollama serve`。注意：用云端对话模型时也仍需 Ollama 提供 bge-m3 嵌入 |
| 后端启动报找不到 `chroma_db` / 集合为空 | 还没建知识库，先跑 `uv run python src/aipy/build_knowledge_base.py` |
| 建库时报 `model "bge-m3" not found` | 嵌入模型没拉取，执行 `ollama pull bge-m3` |
| 检索结果答非所问 | 检查 wiki 对应 chunk 是否自包含、是否含玩家提问的关键词；向量检索不懂逻辑条件，靠 LLM 二次筛选，详见 [NOTES.md](stardewAI/NOTES.md) |
| 本地模型首次回复很慢 | 首次调用需把对话/嵌入模型加载进内存/显存，属正常现象；可先 `ollama run qwen2.5:3b` 预热，嫌慢可切换云端 provider |
| 改了 C# 代码游戏里没生效 | 忘记 `dotnet build`，游戏跑的还是 Mods 目录里的旧 DLL |
| HUD 提示「响应超时」 | 后端卡住超过 15 秒，检查后端控制台日志 |
| `dotnet build` 报 `UnauthorizedAccessException` | 游戏/SMAPI 正在运行占用 DLL，先关闭游戏 |
| 游戏内 HUD 文字无法自定义颜色 | 游戏本体 1.6.15 移除了 `HUDMessage.color`，错误提示改用 `error_type` 图标；聊天框消息不受此限制（可用 Color 上色） |

更多踩坑细节（反射查 API、沙箱限制、C# 与 C++ 差异等）见 [stardewAI/NOTES.md](stardewAI/NOTES.md)。

## 许可证

待定（TBD）
