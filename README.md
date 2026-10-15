# 星露谷 AI 陪玩 Mod（Stardew AI Companion）

一个由 **SMAPI Mod（C#）+ 本地 Python AI 后端（FastAPI + LangChain + LangGraph）** 组成的星露谷物语 AI 陪伴系统，**支持 RAG 知识库检索**（ChromaDB + bge-small-zh-v1.5 本地 ONNX 嵌入）与**多轮对话记忆**，对话模型**本地 Ollama / 云端 API（DeepSeek、智谱、通义、Kimi）可一键切换**。

三种对话方式：

- **中文输入框（推荐）**：按 **F7** 打开自研输入框，直接用输入法打中文（IME 正常上屏），回车提问——回复显示在聊天框里（浅蓝色，可翻历史）；输入期间按键全部接管（角色不动、游戏不响应），Esc 随时关闭
- **原版聊天框**：游戏中按 **T** 打开原版聊天框，输入 `AI 海莉喜欢什么礼物` 回车——AI 回复直接显示在聊天框里（浅蓝色，可翻历史）；不带 `AI ` 前缀的消息一切照原版走

AI 会实时读取游戏状态（名字、体力、血量、时间、日期、季节、天气、地点、金币、手持工具、耕种/采矿等级、存档天数），检索本地游戏知识库，记得本次会话聊过的内容（多轮记忆），并给出基于资料的回答。

> **最终目标**：让一个漂浮在玩家身旁的小机器人陪伴你冒险——它感知玩家状态机，结合游戏知识库（RAG）进行多轮智能对话。

---

## 当前进度（v0.9 · 中文输入框）

**✅ 已完成：**
- 端到端通信（C# ↔ Python，HTTP + JSON）
- 游戏内自由提问：**F7 中文输入框**（IME 正常上屏，按键全接管）或原版聊天框输入 `AI 问题`（Harmony 拦截提交），回复都进聊天框
- RAG 知识库（ChromaDB + fastembed `bge-small-zh-v1.5` 本地嵌入，已收录作物 / 鱼类 / NPC 礼物）
- 嵌入改本地 ONNX（2026-10-15）：`OllamaEmbeddings`(bge-m3, 1024 维) → `FastEmbedEmbeddings`(`BAAI/bge-small-zh-v1.5`, 512 维，int8 ~23MB)，检索不再依赖 Ollama
- 游戏状态感知：名字 / 体力 / 血量 / 游戏时间 / 日期（1-28）/ 季节 / 是否下雨 / 地点 / 金币 / 手持工具 / 耕种 & 采矿等级 / 存档已玩天数 / 背包 / NPC 好感度 / 当日任务
- LLM 配置化（BYOK 基础）：`config.json` 切换 provider——本地 `ollama`（免 Key）或云端 `deepseek` / `zhipu` / `qwen` / `kimi`（OpenAI 兼容接口，自带 Key）
- 多轮对话记忆（LangGraph + SQLite 持久化）：AI 记得本次会话聊过的内容，按玩家名隔离历史，**重启后端也不丢**；上下文窗口只带最近 10 轮（完整历史仍存 SQLite，不丢数据）
- 模型参数可配置：回答温度（temperature）在 GMCM 里调节（0.0-2.0，步长 0.1），随配置推送后端实时生效
- 浮动精灵（第一版）：跟随玩家移动 + 上下浮动动画（sin 曲线），`assets/robot.png` 贴图 1:1 绘制，贴图缺失自动回退方块占位

**⏳ 规划中：**
- 主动提醒（精灵主动说话 / 事件触发）
- 继续扩展状态（背包 / 当前工具 / NPC 好感 / 当日任务）

## 架构

```
[按 F7 输入框] --IME 文本 (GameWindow.TextInput)--> [C# Mod (ModEntry.cs)]
[按 T 聊天框] --Harmony 拦截 "AI " 前缀-----------> [C# Mod (ModEntry.cs)]
                                      |
                                      v HTTP POST /chat
                              [FastAPI :8000]
                                      |
                                      v
                         [LangGraph 对话图: retrieve -> chat]
                                      |
                                      v
                    [ChromaDB 检索 (bge-small-zh-v1.5, top-3)]
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
- **LLM 工厂模式**：[llm_factory.py](backend/src/aipy/llm_factory.py) 启动时读 `config.json`，按 `provider` 创建 `ChatOllama` 或 `ChatOpenAI`（四个云端厂商的 base_url 已内置预设）；嵌入模型始终用本地 fastembed 的 `bge-small-zh-v1.5`（ONNX 随后端自带），与对话模型选型、是否装 Ollama 无关
- **游戏内配置（GMCM）**：玩家 ESC → Mod 选项 → 填厂商/Key/模型/回答温度 → 保存时 C# 推送到后端 `POST /config` → 写 `config.json` + 重建 LLM 注入对话图，无需重启；后端未启动时配置只存本地 ModConfig.json，启动后需重新保存
- **后端自动拉起**：GMCM 里填 `后端目录路径`（backend 文件夹绝对路径）+ 开 `自动启动后端` 后，进游戏 C# 先探活 `GET /health`，后端没起就自动用 uv 拉起并轮询等就绪（最多 30 秒），日志落 `backend/backend.log`；游戏退出时自动关闭 C# 拉起的后端进程（手动启动的不受影响）
- `await` 之后的代码运行在线程池后台线程，所有 UI 操作（HUD / 聊天框）通过主线程任务队列（`Queue<Action>` + `UpdateTicked`）调度回游戏主线程执行，避免随机崩溃
- 全局复用单个 `HttpClient`，15 秒超时，请求期间游戏照常运行不卡顿
- 向量库在后端启动时只加载一次；每次请求先做向量检索（top-3），把参考资料片段和游戏状态一起注入提示词
- **多轮记忆**：`/chat` 请求进入 LangGraph 对话图（`retrieve → chat` 两个节点），对话历史由 `add_messages` reducer 自动累积、`AsyncSqliteSaver` 按 `thread_id`（玩家名）落盘到 `backend/data/checkpoints.db`——重启后端历史不丢；删掉该文件即可清空全部记忆
- System Prompt 严格约束：游戏知识必须依据参考资料回答，资料里没有就直说"我不太清楚"；向量检索不擅长的逻辑条件（如"晴天"误召回"雨天"鱼）交给 LLM 二次筛选
- 未进入存档时发起对话会友好提示，不会崩溃

## 目录结构

```
AI_Projects/
├── backend/                      # Python AI 后端
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
│   │   ├── embeddings.py      # 本地嵌入封装：fastembed（ONNX）实现 LangChain Embeddings
│   │   └── build_knowledge_base.py  # 建库脚本：wiki_data → chroma_db
│   ├── data/                  # 对话历史 SQLite（checkpoints.db，本地生成，已 gitignore）
│   └── chroma_db/             # 向量库（脚本本地生成，已 gitignore）
├── mod/                 # C# SMAPI Mod
│   ├── ModTemplate/
│   │   ├── ModEntry.cs        # Mod 入口：F7/聊天框两条对话流程、GMCM 配置、浮动精灵、线程队列、HTTP 请求
│   │   ├── ChatBoxTextBoxEnterPatch.cs  # Harmony 补丁：拦截聊天框 "AI " 消息
│   │   ├── FloatingCompanion.cs  # 浮动精灵：跟随玩家 + sin 浮动动画，贴图缺失回退方块
│   │   ├── assets/
│   │   │   └── robot.png      # 精灵贴图（64x64，随仓库分发，按图片实际尺寸 1:1 绘制）
│   │   ├── ChineseInputBox.cs # 中文 IME 输入框（F7 开关，纯状态类；字符接收与绘制在 ModEntry）
│   │   ├── ModConfig.cs       # GMCM 配置模型：Provider / ApiKey / Model / Temperature
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
- 嵌入模型：fastembed `BAAI/bge-small-zh-v1.5`（ONNX int8，随后端自带，中文 512 维；知识库向量化不随对话 provider 改变，也不需要本地 Ollama）

## 环境要求

| 组件 | 版本要求 |
|---|---|
| 星露谷物语 | 1.6.15（Steam 版） |
| SMAPI | 4.x（MinimumApiVersion 4.0.0） |
| .NET SDK | 6.0（编译 Mod） |
| Python | ≥ 3.11，使用 [uv](https://docs.astral.sh/uv/) 管理依赖 |
| 对话模型（二选一） | **本地模式**：[Ollama](https://ollama.com/)（默认端口 11434）+ `qwen2.5:3b`；**云端模式**：DeepSeek / 智谱 / 通义 / Kimi 任一厂商的 API Key |
| 嵌入模型 | 无需额外安装——`fastembed` 的 `BAAI/bge-small-zh-v1.5`（ONNX int8，~23MB）随 Python 依赖自带，首次建库自动下载并缓存，之后离线可用 |

### 首次运行前置准备

1. **Python 环境**：装 Python 3.11+ 和 [uv](https://docs.astral.sh/uv/)
2. **游戏 Mod 环境**：装 SMAPI 4.x + [GenericModConfigMenu](https://www.nexusmods.com/stardewvalley/mods/5098)
3. **嵌入模型**：无需手动安装——建库时 `fastembed` 会自动下载 `BAAI/bge-small-zh-v1.5`（ONNX int8，~23MB）并缓存到本地，之后完全离线可用（国内网络下载超时则先设镜像 `$env:HF_ENDPOINT="https://hf-mirror.com"`）
4. **GMCM 前置 dll**：把 `GenericModConfigMenu.dll` 复制到 `mod/ModTemplate/libs/`（详见下文"编译并部署 Mod"）
5. **构建知识库**：
   ```bash
   cd backend
   uv run python src/aipy/build_knowledge_base.py
   ```

## 配置文件说明

项目涉及 **两个配置文件**，**两份都包含明文 API Key，都不应提交到 Git**：

| 文件 | 位置 | 用途 | 谁写 |
|---|---|---|---|
| `ModConfig.json` | `Mods/StardewAIAssistant/`（游戏侧） | 游戏读取，决定推送给后端的配置 | GMCM 保存时自动写入 |
| `config.json` | `backend/src/aipy/`（后端侧） | 后端读取，决定用哪个 LLM | 后端收到推送后自动写入 |

**首次配置流程**：
1. 启动 Python 后端（`uv run uvicorn main:app --reload --port 8000`）
2. 启动游戏，ESC → 配置 MOD → Stardew AI Assistant
3. 填入 Provider / API Key / Model / Temperature，点保存
4. 两个配置文件会自动生成，之后无需再填

**如果你没装 GMCM**，可以手动创建 `Mods/StardewAIAssistant/ModConfig.json`：
```json
{
  "Provider": "deepseek",
  "ApiKey": "sk-你的密钥",
  "Model": "deepseek-chat"
}
```

支持的 Provider 与推荐模型：

| Provider | 推荐模型 | 获取 Key |
|---|---|---|
| `deepseek` | `deepseek-chat` | `https://platform.deepseek.com` |
| `qwen` | `qwen-plus` | `https://dashscope.console.aliyun.com` |
| `zhipu` | `glm-4-flash` | `https://open.bigmodel.cn` |
| `kimi` | `moonshot-v1-8k` | `https://platform.moonshot.cn` |
| `ollama` | 本地模型（如 `qwen2.5:3b`） | 需先装 [Ollama](https://ollama.com) |

## 快速开始

### 1. 准备对话模型

安装 [Ollama](https://ollama.com/download)（仅本地对话模式需要；**嵌入模型无需准备**——fastembed 的 `BAAI/bge-small-zh-v1.5` 随 Python 依赖自带，建库时自动下载）；对话模型仅在"本地模式"下需要：

```bash
ollama pull 本地模型   # 对话模型（仅本地模式需要，如 qwen2.5:3b）
```

Ollama 安装后一般会自动在后台运行（默认端口 `11434`）；若没有，手动执行 `ollama serve`。用云端 provider 时完全不需要 Ollama。

### 2. 准备知识库（首次必做）

知识库源文件放在 `backend/wiki_data/`，为 **UTF-8 编码的 Markdown**：

- 建库脚本按 `##`（二级标题）切块，**每个 `##` 小节成为一个 chunk**
- 每个 chunk 必须自包含：标题里的限定信息（如"春季作物""XX 的礼物偏好"）要留在正文中，不能只写一个孤立的"# 春季作物"
- 玩家可能提问的关键词（如"礼物""晴天"）应直接出现在对应 chunk 的标题或正文里，否则向量检索召不回
- 现有三份：[crops.md](backend/wiki_data/crops.md)（作物）、[fish.md](backend/wiki_data/fish.md)（鱼类）、[gift.md](backend/wiki_data/gift.md)（NPC 礼物），可直接照格式扩充

运行建库脚本（在 `backend/` 目录下）：

```bash
uv sync                                                      # 首次安装依赖
uv run python src/aipy/build_knowledge_base.py
```

脚本会：读取 `wiki_data/*.md` → 按 `##` 切块（保留标题）→ fastembed `bge-small-zh-v1.5` 向量化 → 写入 `chroma_db/`，末尾还会跑一条自测查询打印命中块。

> 注意：
> - 脚本采用**全量重建**（先删除旧 `chroma_db/` 再写入），修改 wiki 后重跑即可，不会产生重复数据
> - 首次运行会先下载嵌入模型（~23MB，缓存到 `backend/models/`，**不再落在系统临时目录**），之后完全离线；国内网络下载超时先设 `$env:HF_ENDPOINT="https://hf-mirror.com"` 再跑
> - 打包分发时 `chroma_db/` 与 `models/` 都随包带上即可，玩家侧零下载、零建库
> - 重建前先停掉正在运行的后端（旧 `chroma_db/` 文件被占用会报 `PermissionError [WinError 32]`）
> - `chroma_db/` 是本地生成物，**不随仓库分发**（已加入 `.gitignore`），换机器或重新克隆后必须重新建库，否则后端启动/检索会报错

### 3. 配置对话模型

**方式一（推荐）：游戏内 GMCM 配置**
1. 安装 [GenericModConfigMenu](https://www.nexusmods.com/stardewvalley/mods/5098)（可选，但推荐）
2. 启动游戏 → ESC → 选项 → 往下找到 "Mod 选项" → Stardew AI Assistant
3. 填四项：
   - **AI 厂商**：`deepseek` / `qwen` / `zhipu` / `kimi` / `ollama`
   - **API Key**：云端必填，本地 Ollama 留空
   - **模型名**：如 `deepseek-chat` / `qwen-plus` / `glm-4-flash` / `qwen2.5:3b`
   - **回答温度**：0.0-2.0（步长 0.1），越高越有创意但越容易胡说，推荐 0.5-0.9
4. 保存 → C# 自动推送到后端 `POST /config` → 写 `config.json` + 重建 LLM，**无需重启后端**
5. 后端未启动时保存：配置只写本地 `ModConfig.json`，HUD 提示"后端未启动，配置已保存到本地；启动后端后请重新保存"

**方式二：手动编辑配置文件（无 GMCM 时）**

后端启动时从 **`src/aipy/config.json`** 读取对话模型配置。仓库只提供模板 [config.example.json](backend/config.example.json)，真实配置含 API Key，已加入 `.gitignore` 不会被提交。

把模板复制到 `src/aipy/` 下并改名（⚠️ 注意目标位置在 `src/aipy/`，不是项目根目录——工厂按自己所在目录找 config.json）：

```bash
# 在 backend/ 目录下
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

- `provider` 可选：`ollama` / `deepseek` / `zhipu` / `qwen` / `kimi`，云端四家的接口地址已在 [llm_factory.py](backend/src/aipy/llm_factory.py) 内置预设，不用自己填 base_url
- 各家模型名填对应官方文档的型号（如智谱 `glm-4-flash`、通义 `qwen-plus`、Kimi `moonshot-v1-8k`）
- 缺少 config.json 或云端模式没填 Key：后端**仍能启动**（用 PlaceholderLLM 占位，回复提示"请先在游戏里配置"），收到 GMCM 推送后自动切换为真实模型

### 4. 启动 AI 后端

```bash
cd backend
uv run uvicorn main:app --port 8000      # 启动服务（调试可加 --reload）
# 或等价：uv run python main.py （程序内置入口，与打包 exe 的运行方式一致）
```

启动日志出现「向量库加载完成」和 `[LLM] provider=..., model=...` 即表示知识库与对话模型均正常载入。

> 注意命令必须是 `uv run uvicorn ...`，直接写 `uv uvicorn` 会报 `unrecognized subcommand 'uvicorn'`。
>
> **嫌每次手动启动麻烦？** 编译好 Mod 后可以在游戏内配置自动拉起：ESC → Mod 选项 → 「后端目录路径」填 backend 文件夹的绝对路径，保持「自动启动后端」开启，之后进游戏会自动拉起 / 退出游戏自动关闭，无需手动操作。

### 5. 编译并部署 Mod

```bash
cd mod/ModTemplate
dotnet build
```

- 编译前请修改 [StardewAIAssistant.csproj](mod/ModTemplate/StardewAIAssistant.csproj) 中的 `GamePath` 为你的游戏安装路径
- **首次编译前需准备 GMCM 前置 dll**：从 [GenericModConfigMenu 发布页](https://www.nexusmods.com/stardewvalley/mods/5098)下载并解压，把 `GenericModConfigMenu.dll` 放到 `mod/ModTemplate/libs/` 下（该 dll 不入库；`libs/IGenericModConfigMenuApi.cs` 接口文件已随仓库提供，取自 GMCM 仓库 `shared/` 稳定版）
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
  "CurrentTool": "WateringCan", "FarmingLevel": 3,
  "MiningLevel": 2, "DaysPlayed": 41,
  "Inventory": "Paragon x5, CopperOre x12",
  "NPCAffection": "Abigail:10心, Leah:8心",
  "CurrentQuest": "海莉的请求 - 给海莉送一个红罂粟",
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
| `CurrentTool` | string | 当前手持工具名（游戏内原名如 `WateringCan`），空手为 `""` |
| `FarmingLevel` | int | 耕种等级（0-10） |
| `MiningLevel` | int | 采矿等级（0-10） |
| `DaysPlayed` | int | 存档已玩总天数 |
| `Inventory` | string | 背包物品汇总（前 10 个非空物品，格式 `"名称 x数量"`，逗号分隔） |
| `NPCAffection` | string | NPC 好感度汇总（好感>0 的前 8 个，格式 `"NPC名:心数心"`，按好感降序） |
| `CurrentQuest` | string | 当前未完成任务的标题与目标（`"标题 - 当前目标"`），没有则 `""` |
| `Message` | string | 玩家问题 |

- `Message` 为玩家问题（来自 F7 中文输入框或聊天框 `AI ` 提问）；后端把 `季节/天气/地点` 作为前缀拼进检索 query（如 `[春季 晴天 Farm] 玩家问题`），其余状态字段由后端整理成结构化中文状态块（季节会译为"春季"、`IsRaining` 译为"正在下雨/天气晴朗"）注入 LLM 提示词

**响应**（小写键名，C# 端通过 `PropertyNameCaseInsensitive` 映射）：

```json
{ "reply": "你好呀 农夫！...", "emotion": "happy" }
```

### POST /config（GMCM 推送配置）

`POST http://127.0.0.1:8000/config`

**请求**（snake_case，与后端 config.json 对齐）：

```json
{ "provider": "deepseek", "api_key": "sk-xxx", "model": "deepseek-chat", "temperature": 0.7 }
```

- `provider` 白名单：`ollama` / `deepseek` / `zhipu` / `qwen` / `kimi`，其他值返回 400
- `provider != ollama` 时 `api_key` 必填，`model` 必填；`temperature` 必须在 0.0-2.0；校验失败返回 400 + 错误详情
- 成功：写 `src/aipy/config.json` → 重建 LLM → 重新注入对话图，返回 `{"status": "ok", "provider": ..., "model": ..., "temperature": ...}`；**对话历史（SQLite）保留，不重建 graph**
- C# 端 GMCM 保存时自动推送；后端未启动时 C# 只写本地 `ModConfig.json`

注意事项：
- `/chat` 的 `GameTime` 为游戏内时间，`600` 表示早上 6:00
- `/chat` 请求字段名是**故意**用 PascalCase 对齐 C# 输出的，修改字段时两端必须同步修改

## Roadmap

- [x] 接入真实 LLM：本地 Ollama + LangChain（`qwen2.5:3b`）
- [x] RAG 知识库：ChromaDB + bge-m3，已收录作物 / 鱼类 / NPC 礼物
- [x] 扩充状态感知（第一批）：季节、天气、日期、金币、血量（C# 收集 + Python 注入提示词）
- [x] 游戏内自由提问：借原版聊天框实现（按 T → `AI 问题` → Harmony 拦截），回复进聊天框可翻历史；v0.9 起另有 F7 中文输入框（见下）
- [x] LLM provider / 模型可配置：`config.json` + LLM 工厂，支持 ollama / deepseek / zhipu / qwen / kimi（BYOK 基础）
- [x] 游戏内配置界面（GMCM，2026-10-14）：ESC → Mod 选项 → 填厂商/Key/模型 → 保存推送后端 `POST /config`，写文件 + 重建 LLM，无需重启；无 GMCM 时可手动编辑 `src/aipy/config.json`
- [x] 模型参数可配置化（2026-10-14）：temperature 全链路打通——GMCM 数值选项 → `POST /config`（校验 0.0-2.0）→ 写 config.json → 重建 LLM 实时生效；检索 top-k 已挪进 `aipy/config.py`
- [x] 中文 IME 输入框（v0.9，2026-10-14）：F7 开关自研输入框，直连 MonoGame `GameWindow.TextInput` 接收 IME 上屏字符（SMAPI 4 无 TextInput 封装，见 NOTES 踩坑 23）；打开期间按键全接管防误操作，回车复用聊天框回复链路
- [x] 漂浮机器人（第一版，2026-10-14）：robot.png 贴图渲染 + 跟随玩家 + sin 浮动动画
- [x] 扩充状态感知（第二批 a，2026-10-14）：当前工具 / 耕种 / 采矿等级 / 存档天数
- [ ] 防连点：思考中重复提问应忽略或排队
- [x] 扩充状态感知（第二批 b，2026-10-15）：背包、NPC 好感、当日任务；并让季节/天气参与检索 query
- [x] 多轮对话记忆：LangGraph 对话图 + AsyncSqliteSaver（SQLite 落盘 `backend/data/checkpoints.db`），按玩家名隔离历史，重启后端不丢
- [ ] F7 / `AI ` 前缀改为可配置，避免与其他 Mod 冲突
- [ ] `emotion` 字段驱动机器人表情 / 立绘变化
- [ ] 气泡对话 UI：AI 回复以气泡形式从机器人贴图头上冒出（现为聊天框 / HUD 文字）
- [ ] 主动提醒：不对话时 AI 偶尔主动提示玩家（触发机制待定，如体力低 / 节日 / 任务到期）
- [ ] 知识库扩充：把星露谷 wiki 内容总结成知识库文档，减少对 wiki 的实时请求
- [x] 后端生命周期打通（2026-10-15）：`GET /health` 零开销探活 + C# 启动时检测 / 自动拉起（uv，轮询最多 30 秒，日志落 `backend.log`）/ 游戏退出自动关闭自己拉起的进程；GMCM 新增「后端目录路径」「自动启动后端」
- [x] 后端打包准备就绪（2026-10-15）：`main.py` 内置程序入口（`python main.py` / 双击 exe 直接跑，无需命令行参数）；嵌入模型缓存固定到 `backend/models/`（已下载则离线加载，打包随包分发零下载）；`chroma_db` 缺失时不再崩溃——跳过检索，AI 回答"不清楚"并打印建库指引
- [ ] **面向玩家打包（当前优先级，路线 2）**：PyInstaller 打包 exe（随包带上 `chroma_db/` + `models/`）+ 把"自动拉起 uv"换成"拉起打包好的 exe"，做到玩家零环境依赖

## 常见问题

| 现象 | 原因与解决 |
|---|---|
| 后端启动日志显示 `[LLM] 未配置...` | 正常现象——等你在游戏里 GMCM 配置完会自动切换；或手动 `cp config.example.json src/aipy/config.json` 后重启（⚠️ 必须放在 `src/aipy/` 下，放项目根目录不生效） |
| 云端模式报 401 / 认证失败 | API Key 没填、填错或欠费；检查 `src/aipy/config.json` 的 `api_key` 与 `model` 名称；或检查 GMCM 里填的 Key 是否含空格 |
| 手动改了 config.json 没反应 | 手动改的配置只在后端启动时读取。**推荐用游戏内 GMCM 保存**（走 `/config` 接口热更新，无需重启） |
| AI 回复驴唇不对马嘴 / 像串了别人的对话 | 多轮记忆按玩家名（thread_id）隔离：改了角色名 = 新会话；测试残留的旧历史会一直保留，想让 AI"失忆"就删 `backend/data/checkpoints.db` 后重启后端 |
| `uv uvicorn` 报 `unrecognized subcommand` | 少了 `run`，正确命令是 `uv run uvicorn main:app --port 8000` |
| 输入 `AI 问题` 被当成普通聊天发出去 | Harmony 补丁未生效：看 SMAPI 启动日志有无补丁加载记录；确认 `dotnet build` 后 Mods 目录 DLL 已更新 |
| HUD 提示「AI大脑连接失败」 | Python 后端未启动或端口不是 8000。可在 GMCM 填「后端目录路径」开启自动拉起；或手动启动后端 |
| HUD 提示「后端启动超时」 | 查看 `backend/backend.log` 定位：常见原因是端口被占（残留了旧后端进程）、目录里没有 main.py、或首次拉起时 uv 要解析依赖超过 30 秒 |
| HUD 提示「拉起失败，找不到 uv」 | uv 未安装或不在 PATH：重开终端执行 `uv --version` 验证；游戏进程继承的 PATH 需包含 uv 所在目录 |
| 后端日志报 Ollama 连接错误 | 仅当对话 provider=`ollama` 时才需要 Ollama：确认服务已运行（默认端口 11434）或执行 `ollama serve`。注意：嵌入已改用 fastembed，检索不再依赖 Ollama |
| 后端启动报找不到 `chroma_db` / 集合为空 | 还没建知识库，先跑 `uv run python src/aipy/build_knowledge_base.py` |
| 建库时下载嵌入模型超时（`Could not download model ... from any source`） | 国内访问 HuggingFace 超时；先设镜像 `$env:HF_ENDPOINT="https://hf-mirror.com"` 再重跑建库脚本 |
| 检索结果答非所问 | 检查 wiki 对应 chunk 是否自包含、是否含玩家提问的关键词；向量检索不懂逻辑条件，靠 LLM 二次筛选，详见 [NOTES.md](mod/NOTES.md) |
| 本地模型首次回复很慢 | 首次调用需把对话/嵌入模型加载进内存/显存，属正常现象；可先 `ollama run qwen2.5:3b` 预热，嫌慢可切换云端 provider |
| 改了 C# 代码游戏里没生效 | 忘记 `dotnet build`，游戏跑的还是 Mods 目录里的旧 DLL |
| HUD 提示「响应超时」 | 后端卡住超过 15 秒，检查后端控制台日志 |
| `dotnet build` 报 `UnauthorizedAccessException` | 游戏/SMAPI 正在运行占用 DLL，先关闭游戏 |
| 游戏内 HUD 文字无法自定义颜色 | 游戏本体 1.6.15 移除了 `HUDMessage.color`，错误提示改用 `error_type` 图标；聊天框消息不受此限制（可用 Color 上色） |

更多踩坑细节（反射查 API、沙箱限制、C# 与 C++ 差异等）见 [mod/NOTES.md](mod/NOTES.md)。

## 开源协议

本项目采用 **分部分授权**：

| 部分 | 协议 | 说明 |
|---|---|---|
| 源代码（C# / Python） | **MIT** | 可自由使用、修改、分发 |
| `backend/wiki_data/` 下的知识库数据 | **CC BY-NC-SA 4.0** | 来源于星露谷 Wiki，遵循原协议 |
| 引用的第三方库 | 各自原协议 | 见 `THIRD_PARTY_LICENSES.txt` |

**简单说**：你可以随便用、改、分享这个项目的代码，但不能把基于 wiki 数据的功能用于商业用途。

## 数据来源与致谢

- **知识库数据**：来自 [星露谷物语 Wiki](https://stardewvalleywiki.com/)（中文/英文），
  遵循 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/) 协议。
  本项目仅提取事实性数据（作物价格、鱼的出现条件、NPC 礼物偏好等），
  并对原始描述进行了格式重写。原始版权归 Wiki 贡献者所有。
- **SMAPI**：由 [Pathoschild](https://github.com/Pathoschild/SMAPI) 开发
- **GenericModConfigMenu**：由 [spacechase0](https://github.com/spacechase0/StardewValleyMods) 开发
- **bge-small-zh-v1.5 嵌入模型**：由 BAAI 发布，MIT 协议；本地推理由 [fastembed](https://github.com/qdrant/fastembed)（ONNX Runtime）提供
- **LangChain / LangGraph / FastAPI / ChromaDB**：各自官方团队开发，MIT 协议
