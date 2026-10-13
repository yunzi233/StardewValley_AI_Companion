# NOTES — StardewAIAssistant 开发笔记

> 给自己回顾用。只记坑和要点，不抄教程。
> 最后更新：2026-10-13

## 一、踩坑清单

1. **编译报错 `CS0117 HUDMessage 未包含 color 的定义`**
   - 现象：想给 HUD 消息上色，写 `new HUDMessage("...") { color = Color.Red }`，编译直接红。
   - 原因：游戏 1.6.15 的 `HUDMessage` 已经没有 `color` 字段了，老教程/老 API 全是过期的。
   - 解决：删掉 color；错误消息改用 `new HUDMessage(text, HUDMessage.error_type)`，只影响左侧图标，文字颜色游戏统一管。确认方法：反射看了 DLL 字段 + 反编译了 draw 的 IL。

2. **`dotnet build` 最后报 `UnauthorizedAccessException ... Mods\StardewAIAssistant.dll 拒绝访问`**
   - 现象：C# 代码本身编译过了（0 错误），死在最后"自动复制到 Mods 目录"那步。
   - 原因有两个：① 游戏/ SMAPI 还开着，DLL 被占用；② TRAE 沙箱不让写 `D:\Steam`。
   - 解决：关游戏再 build；在 TRAE 里只想验证语法就加 `-p:EnableModDeploy=false`。

3. **用错了关闭部署的参数名**
   - 现象：`-p:DeployModFolder=false` 完全不生效，照样尝试部署。
   - 原因：属性名想当然了，真正读的是 `EnableModDeploy`。
   - 解决：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`。不确定参数名时去 NuGet 包目录里翻 `.targets` 文件。

4. **`dotnet new console -f net6.0` 提示 net6.0 不是有效值**
   - 现象：想建个 net6 小工具反射游戏 DLL，模板只给 net8.0。
   - 原因：本机 SDK 的模板只剩 net8.0，跟项目目标框架是两码事（项目照样能 net6 编译）。
   - 解决：用 net8.0 建临时工具就行，反射读 net6 DLL 没影响，用完删掉。

5. **Windows PowerShell 反射游戏 DLL 报 `Could not load type System.Object from System.Private.CoreLib`**
   - 现象：用自带 PowerShell 5 加载 `Stardew Valley.dll` 直接炸。
   - 原因：游戏是 .NET 6 程序集，老 PowerShell 跑在 .NET Framework 上，不兼容。
   - 解决：别折腾 PS 反射，建个 .NET 6/8 控制台项目正经查。

6. **教训（不算报错）：网上的星露谷 Mod 资料默认按老版本写**
   - 凡是涉及具体字段/方法签名，先按自己游戏版本实测，别信帖子。我这版是游戏 1.6.15 + SMAPI 4.x。

7. **RAG 切片：每个 chunk 必须"自包含"，标题限定信息不能丢**
   - 现象：按 Markdown 标题切块后，问"春季种什么最赚钱"召回的内容缺季节信息，LLM 只能瞎猜。
   - 原因：`MarkdownHeaderTextSplitter` 默认 `strip_headers=True`，会把标题从正文里剥掉；像"# 春季作物"这种上下文只存在于标题，一旦标题独立成块或被剥离，块就变成没有季节限定的裸数据。
   - 解决：切块时 `strip_headers=False` 保留 `##` 标题；整理 wiki 时让标题本身带全限定信息（如"## 蓝爵 (Blue Jazz)"所在文件就是 crops.md，季节写进正文字段"耕种季节：春季"）。判断标准：把一个 chunk 单独拎出来看，不依赖任何外部上下文也能读懂。

8. **向量检索是"语义相似"不是"关键词匹配"，但关键词仍然得在 chunk 里出现**
   - 现象：玩家问"海莉喜欢什么礼物？"，召不回海莉那块。
   - 原因：早期 gift.md 的小节标题只写了人名（"## 海莉"），块里通篇是"最爱/喜欢/一般"，"礼物"这个词根本没出现；嵌入向量对完全没共现过的词命中率很低。
   - 解决：标题统一改成"## 海莉 (Haley) 的礼物偏好"，让"礼物"落在每个 chunk 上；整理新 wiki 时先想玩家会怎么问，把提问里的高频词写进标题/字段名。

9. **向量检索不擅长逻辑条件判断：top-K 里会混入不符合条件的结果**
   - 现象：问"夏季晴天能钓什么鱼？"，召回的鱼里混着"夏季雨天"才出现的鱼。
   - 原因：向量相似度只保证"主题相关"，季节/天气/时间这种结构化过滤条件它不会精确执行；纯向量库要做这个得配 metadata filter，而我们的条件写在正文里。
   - 解决：召回 top-3 后把**条件判断交给 LLM 二次筛选**——System Prompt 里写死"严格依据参考资料回答，资料没有就说不知道"，鱼块正文里"出现天气：晴天/雨天"字段齐全，LLM 能自己排除。别指望检索一步到位，R（检索）和 G（生成）各管一段。

10. **改完 C# 不 `dotnet build`，游戏跑的永远是旧 DLL**
    - 现象：给 `GameStateData` 加了 `Message` 字段、Python 端也收到了旧格式请求，以为序列化坏了，查半天。
    - 原因：游戏加载的是 `Mods/StardewAIAssistant` 目录里的 DLL，源码改了但没 build，旧 DLL 不会自己更新；Python 端热改见效太快，对比之下容易忽略 C# 这步。
    - 解决：形成肌肉记忆——**改 C# → 关游戏 → `dotnet build` → 开游戏验证**。怀疑"代码没生效"时先看 build 输出时间和 SMAPI 日志，别先怀疑逻辑。

11. **重建向量库不能直接追加，会产生重复块**
    - 现象：wiki 改完重新跑入库脚本，同一问题召回多条一模一样的内容。
    - 原因：`Chroma.from_documents` 每次都是纯追加，旧块不会因为"源文件变了"被自动替换。
    - 解决：建库脚本采用全量重建——`shutil.rmtree(chroma_db)` 删旧库再写入。现阶段数据量小，全量重建最简单；以后量大了再研究按 `source` metadata 做增量 upsert。

12. **建库和查询用的嵌入模型必须是同一个**
    - 现象：换了嵌入模型后检索结果明显变差/维度报错。
    - 原因：chunk 向量是用建库时的模型编码的，查询时换模型，两个向量不在同一个语义空间，相似度没有意义。
    - 解决：`build_knowledge_base.py` 和 `main.py` 里写死同一个 `OllamaEmbeddings(model="bge-m3")`；换模型 = 重新全量建库，没有捷径。

13. **向量库只在启动时加载一次，别放进请求流程**
    - 现象/教训：Chroma 持久化目录的加载（含 collection 初始化）有开销，写在 `/chat` 请求里会让每次对话都白等。
    - 解决：`main.py` 顶部模块级初始化 `vectorstore` 和 `retriever`，FastAPI 启动时执行一次；请求里只调 `retriever.ainvoke(query)`。

14. **SMAPI 4 的 0Harmony.dll 藏在 smapi-internal/，ModBuildConfig 不会自动引用**
    - 现象：代码写好了 `using HarmonyLib;` 和 `[HarmonyPatch]`，编译报找不到 HarmonyLib 命名空间。
    - 原因：SMAPI 4 把 Harmony 放在游戏目录的 `smapi-internal\0Harmony.dll`，`Pathoschild.Stardew.ModBuildConfig` 4.1.0 只自动引用 SMAPI 本身和游戏 DLL，不含 internal 目录。
    - 解决：csproj 里显式加引用，并设 `Private=false`（运行时由 SMAPI 提供，不随 Mod 复制，避免版本冲突）：
      `<Reference Include="0Harmony"><HintPath>$(GamePath)\smapi-internal\0Harmony.dll</HintPath><Private>false</Private></Reference>`

15. **1.6 聊天框 API 和老资料对不上：字段改名、addMessage 必须带 Color**
    - 现象：网上教程说的 `Game1.chatBox.textBox.Text` 在 1.6.15 编译不过；`addMessage("文本")` 单参数重载也不存在。
    - 原因（反射 1.6.15 实测）：1.6 文本输入控件换成了新类 `ChatTextBox`（继承老 `TextBox`），ChatBox 里的 public 字段也叫 `chatBox`——即 `Game1.chatBox.chatBox`；`Text` 属性定义在基类 `TextBox` 上。消息方法只有 `addMessage(string, Color)`，另有 `addInfoMessage(string)` / `addErrorMessage(string)` 自动配色。
    - 解决：读文本用 `Game1.chatBox.chatBox.Text`；显示消息用 `Game1.chatBox.addMessage(text, Color.LightSkyBlue)`。激活状态判断用 `Game1.IsChatting`（静态属性）或 `chatBox.isActive()`（小写 is）。

16. **Harmony 补丁打哪个重载？先读 IL 再动手，别想当然**
    - 现象：`ChatBox.textBoxEnter` 有两个重载（`string` 和 `TextBox sender`），不确定回车提交走哪个、补丁打几个。
    - 查证方法：建个 net8 临时控制台项目，用 `MetadataLoadContext` 反射签名（只加载元数据，不依赖 MonoGame 运行）；再用 `PEReader` + `MetadataReader` 直接读 IL，扫 `call/callvirt` 指令看方法间调用关系。
    - 结论：回车先触发 `textBoxEnter(TextBox sender)`，它内部调 `textBoxEnter(string text)`（真正执行命令/聊天发送），**返回后无条件调用 `reset()` + `clickAway()`**。所以 Prefix 只打 string 重载即可：拦截后返回 false 跳过发送，输入框清空和关聊天框由外层自动完成，不用自己收尾。

17. **config.json 的查找路径跟着代码文件走，不跟着启动目录走**
    - 现象：把 `config.example.json` 复制到 `AIpy/config.json`（项目根），后端照样报 `缺少配置文件`。
    - 原因：`llm_factory.py` 里写的是 `Path(__file__).parent / "config.json"`，工厂模块在 `src/aipy/`，所以真实配置必须位于 `AIpy/src/aipy/config.json`；而 example 模板放在 `AIpy/` 根目录，复制时目标路径容易写错。
    - 解决：`cp config.example.json src/aipy/config.json`（在 AIpy/ 下执行）。模板和真实配置不同目录这点要在 README 写醒目。

18. **`uv uvicorn` 会被当成 uv 的子命令，必须 `uv run`**
    - 现象：执行 `uv uvicorn main:app --reload` 报 `error: unrecognized subcommand 'uvicorn'`。
    - 原因：uv 不是直接执行器，`uvicorn` 不是它的子命令；要用 `uv run` 在项目环境里执行命令。
    - 解决：`uv run uvicorn main:app --port 8000`（`uv run python xxx.py` 同理）。

19. **同步版 `SqliteSaver` 不支持 async：直接抛 `NotImplementedError`**
    - 现象：`build_graph` 里用 `SqliteSaver(sqlite3.connect(...))`，一调 `await graph.ainvoke` 就炸：`NotImplementedError: The SqliteSaver does not support async methods. Consider using AsyncSqliteSaver instead.`。
    - 原因：LangGraph 的同步 Checkpointer 没实现 `aget_tuple`/`aput` 等异步方法，而我们的图全程跑在 async 里（FastAPI + `ainvoke`）。
    - 解决：换 `AsyncSqliteSaver`（`from langgraph.checkpoint.sqlite.aio import ...`）+ `aiosqlite` 依赖。妙处：`aiosqlite.connect()` **不 await 直接传给构造函数**（懒连接）也能用，所以 `build_graph()` 保持同步签名，main.py 不用动。实测文件持久化、模拟重启历史恢复均 OK。

## 二、核心机制备忘

1. **async/await 的线程切换**
   - `await` 不卡主线程：结果没回来，主线程先回去继续渲染；响应到了，后续代码默认跑在**线程池后台线程**上（SMAPI 没给主线程同步上下文）。
   - 所以 await 之后**不能直接调游戏 UI / 读写游戏对象**，会随机崩。

2. **主线程队列模式（EnqueueHudMessage + OnUpdateTicked 成对出现）**
   - 后台线程只把"要干的 UI 动作"包成 `Action` 塞进 `Queue`；主线程在每帧 tick 里 Dequeue 出来执行。
   - 本质是生产者-消费者；代价最多晚一帧（1/60 秒，无感），两头都要 `lock`。以后凡是异步回调要碰游戏，照抄这个套路。

3. **HttpClient 全局复用**
   - `static readonly` 一个实例用到底，别每次按 F7 都 new。
   - 频繁创建销毁会耗尽 TCP 套接字（微软官方坑）。顺手设了 15 秒 `Timeout`。

4. **using 的两种意思**
   - 顶部 `using Xxx;` = 引入命名空间。
   - 代码里 `using var x = ...` = 出作用域自动 `Dispose()` 释放资源，异常时也会释放。两码事，别混。

5. **C# 对比 C++ 最容易混的几点**
   - 没有 `delete`：GC 自动回收；非托管资源才用 `IDisposable` / using。
   - `{ get; set; }` 是属性不是字段；`new()` 可按左边类型推断，不用写全。
   - `using` 不是 `#include`，没有头文件；`?.` 和 `??` 专门防空引用。
   - 字符串直接用 `$"x={v}"` 插值；泛型 `<T>` 运行期保留，不是 C++ 模板那套编译期展开。

6. **Harmony Prefix 拦截模式（聊天框功能的核心套路）**
   - `Entry` 里 `new Harmony(ModManifest.UniqueID).PatchAll()` 一次安装；补丁类带 `[HarmonyPatch(typeof(目标类), nameof(目标类.方法), new[]{参数类型})]`，重载靠参数类型数组区分。
   - Prefix 返回 `bool`：`true` 继续执行原方法，`false` 跳过；方法参数名与原版一致即可自动注入（如 `string text_to_send`）。
   - Prefix 必须是 static，访问不到 Mod 实例——靠 `ModEntry.Instance` 静态单例回调实例方法。
   - 补丁本身运行在游戏主线程；HTTP 仍异步发出，回复照旧走 `_mainThreadActions` 队列回主线程 `addMessage`。
   - 只拦自己认识的输入（`AI ` 前缀），其余一律 `return true`——最大限度不破坏原版行为（多人聊天、`/` 命令全不受影响）。

7. **LangGraph 多轮记忆的三个关键件**
   - `State`（TypedDict）：`messages` 挂 `add_messages` reducer，节点返回的 message 自动**追加**进历史列表；其他字段（player_state/context/error）不挂 reducer，每轮**覆盖**。
   - Checkpointer：`compile(checkpointer=...)` 后，按调用时的 `config={"configurable": {"thread_id": ...}}` 存每个会话的 State 快照——我们用玩家名当 thread_id，天然按角色隔离。
   - 请求入口只放本轮玩家原话 `messages=[HumanMessage(...)]`，历史由 Checkpointer 自动注入；**别自己把历史拼进 messages**，会重复。
   - 持久化用 `AsyncSqliteSaver` + `aiosqlite.connect(CHECKPOINT_DB)`（懒连接），历史落盘 `AIpy/data/checkpoints.db`，重启后端不丢；想清空记忆就删这个文件（同步版 SqliteSaver 不支持 async，见踩坑 19）。

## 三、代码地图

### C# 端：`ModTemplate/ModEntry.cs`

| 方法/类/字段 | 位置 | 作用 |
|---|---|---|
| `BackendUrl` | L38 | 后端地址 `http://127.0.0.1:8000/chat` |
| `HttpClient` | L44 | 全局复用，15 秒超时 |
| `JsonOptions` | L54 | 小写键名大小写不敏感映射 |
| `_mainThreadActions` | L65 | 主线程任务队列（HUD 和聊天框消息共用） |
| `TestQuestions` | L70 | 测试问题池（5 个 RAG 验证问题：鱼/礼物/作物/生日/钓点） |
| `_questionIndex` | L83 | 问题池轮转下标，取余自增 |
| `Instance` | L90 | 静态单例，供 Harmony 静态补丁回调实例方法 |
| `ModEntry`（类） | L30 | Mod 入口，继承 SMAPI 的 `Mod` |
| `Entry` | L92 | 启动时跑一次：`Harmony.PatchAll()` + 订阅 ButtonPressed/UpdateTicked |
| `OnButtonPressed` | L114 | F7 入口（精简后只管：F7 过滤→安检→取测试问题→显示"玩家问"HUD→调 `SendToBackendAsync(useChatBox:false)`） |
| `OnUpdateTicked` | L147 | 主线程每帧执行，消费 `_mainThreadActions` 队列 |
| `EnqueueHudMessage` | L175 | 把显示 HUD 的动作打包入队（可在后台线程调） |
| `ChatReplyColor` / `ChatErrorColor` | L201 / L204 | 聊天框回复浅蓝 / 错误橙红的颜色常量 |
| **`SendToBackendAsync`** | **L216** | **对话核心流程（两入口共用，消除原先 95% 重复）**：收集 10 字段状态→"思考中"→异步 POST→解析→按 `useChatBox` 分支显示→4 个 catch（HttpRequestException/OperationCanceled/Json/Exception） |
| `HandleAiChatQuestion` | L329 | 聊天框入口（精简后只管：安检→调 `SendToBackendAsync(useChatBox:true)`） |
| `EnqueueChatMessage` | L349 | 把 `chatBox.addMessage(text, color)` 打包入队；浅蓝=回复/提示，橙红=错误 |
| `EnqueueChatMessageFromPatch` | L362 | internal 包装，供补丁类调用用法提示 |
| `GameStateData`（内部类） | L370 | 发给后端的模型共 10 字段：基础 PlayerName/Stamina/GameTime/Location/Message + 扩展 Season/IsRaining/DayOfMonth/Money/Health |
| `ChatResponse`（内部类） | L401 | 后端返回模型：Reply/Emotion |

### C# 端：`ModTemplate/ChatBoxTextBoxEnterPatch.cs`（Harmony 补丁）

| 成员 | 位置 | 作用 |
|---|---|---|
| `[HarmonyPatch]` 特性 | L24 | 目标 `ChatBox.textBoxEnter(string)` 重载（靠 `new[]{typeof(string)}` 区分） |
| `AiPrefix` | L28 | 常量 `"AI "`（大写 AI + 半角空格） |
| `Prefix` | L34 | Trim 后判断前缀：非 AI 消息 `return true` 放行；空问题回用法提示；正常问题回调 `Instance.HandleAiChatQuestion` 后 `return false` 跳过原版发送 |

### Python 端：`AIpy/main.py`（FastAPI 入口 + LangGraph 调度）

| 块 | 位置 | 作用 |
|---|---|---|
| `SYSTEM_PROMPT` | L16-38 | 角色设定 + 铁律：游戏知识只准依据参考资料 / 资料没有就承认不知道 / 非游戏话题拒答，附正反示例 |
| `ChatRequest` / `ChatResponse` | L40-51 / L54-56 | 请求 10 个字段：基础 5 个（PlayerName/Stamina/GameTime/Location/Message）+ 扩展 5 个（Season/IsRaining/DayOfMonth/Money/Health） |
| 向量库初始化 | L58-67 | 模块级只跑一次：从 `aipy.config` 读常量 → `OllamaEmbeddings` + `Chroma` → `retriever`（k=`TOP_K`） |
| LLM 创建 + 图构建 | L69-76 | `load_config()` → `create_llm()` 工厂创建；`init_agent()` 注入 llm/retriever/system_prompt → `graph = build_graph()` |
| `chat_endpoint` | L79-131 | `/chat` 主流程（检索/拼提示词/调 LLM 已下沉到图节点）：①`thread_id=玩家名`（记忆按角色隔离）②组 `player_state` dict（9 字段）③`graph.ainvoke`（try 包裹，异常返回友好错误）④按 `result["error"]` 分支返回 retrieval_failed / llm_failed 提示 ⑤成功取 `messages[-1].content` 作回复 |

### Python 端：`AIpy/src/aipy/agent/`（LangGraph 对话图，新增）

`state.py`：

| 块 | 位置 | 作用 |
|---|---|---|
| `State`（TypedDict） | L6-14 | `messages` 挂 `add_messages` reducer 自动累积历史；`player_state` / `context` / `error` 不挂 reducer，每轮覆盖 |

`graph.py`：

| 块 | 位置 | 作用 |
|---|---|---|
| `init_agent` | L15-20 | main.py 启动时注入 llm/retriever/system_prompt（模块级全局变量，做配置界面时再改 getter） |
| `retrieve_node` | L23-34 | 用本轮玩家原话（`messages[-1]`）做 query 检索知识库；try 包裹，失败返回 `error="retrieval_failed"` |
| `chat_node` | L39-76 | 季节/天气本地化 + 结构化状态块 + 【玩家的问题】+ 【参考资料】拼 user_msg；历史只取最近 20 条（`[-21:-1]`，约 10 轮，控 token）；`SystemMessage + 历史 + 本轮` 调 LLM；失败返回 `error="llm_failed"` |
| `_after_retrieve` | L78-80 | 条件边：`retrieval_failed` 直接 END 跳过 chat，否则进 chat |
| `build_graph` | L83-102 | `mkdir` 建 `data/` → `aiosqlite.connect(CHECKPOINT_DB)` 懒连接 → `AsyncSqliteSaver` → `StateGraph`：START → retrieve →（条件边）→ chat → END → `compile(checkpointer=...)`，历史落盘 SQLite |

### Python 端：`AIpy/src/aipy/llm_factory.py`（LLM 工厂）

| 块 | 位置 | 作用 |
|---|---|---|
| `PROVIDERS` | L9-14 | 云端厂商预设 base_url：deepseek / zhipu（智谱）/ qwen（通义）/ kimi |
| `CONFIG_PATH` | L16 | `Path(__file__).parent/"config.json"` → 真实配置在 `src/aipy/` 下（不是项目根） |
| `load_config` | L19-25 | config.json 不存在直接抛 FileNotFoundError 并提示复制 example |
| `create_llm` | L28-50 | `ollama` → `ChatOllama(model, base_url)`；其他 provider 校验 api_key 非空后返回 `ChatOpenAI(model, api_key, base_url=预设)`；temperature 两边都写死 0.7 |

### Python 端：`AIpy/src/aipy/config.py`（项目级常量，新建）

集中管理原先散落在 main.py 和 build_knowledge_base.py 里的 5 处硬编码，改一处两边同时生效。

| 常量 | 位置 | 作用 |
|---|---|---|
| `PROJECT_ROOT` | L10 | `Path(__file__).resolve().parents[2]` → `AIpy/` 根目录 |
| `CHROMA_DIR` | L13 | `PROJECT_ROOT / "chroma_db"`，向量库持久化目录 |
| `COLLECTION_NAME` | L14 | `"stardew_wiki"`，Chroma 集合名（main.py 和建库脚本必须一致） |
| `EMBEDDING_MODEL` / `OLLAMA_BASE_URL` | L17-18 | `"bge-m3"` / `http://127.0.0.1:11434`，嵌入模型固定走本地 Ollama |
| `TOP_K` | L21 | `3`，检索召回块数 |
| `WIKI_DATA_DIR` | L24 | `PROJECT_ROOT / "wiki_data"`，知识库源 Markdown 目录 |
| `CHECKPOINT_DB` | L28 | `PROJECT_ROOT / "data" / "checkpoints.db"`，对话历史 SQLite 文件（AsyncSqliteSaver 落盘用，已 gitignore） |

### Python 端：`AIpy/src/aipy/build_knowledge_base.py`（建库脚本）

| 块 | 位置 | 作用 |
|---|---|---|
| import config | L10 | 从 `aipy.config` 读路径/模型名/集合名（不再自己算 `parents[2]`） |
| 切块器 | L15-18 | `MarkdownHeaderTextSplitter` 按 `##` 切，`strip_headers=False` 保留标题（保证 chunk 自包含） |
| 读文件切块 | L20-27 | `WIKI_DATA_DIR.glob("*.md")`，每块打 `source` metadata（来源文件名） |
| 删旧库 | L32-33 | `shutil.rmtree(CHROMA_DIR)` 全量重建，防重复追加 |
| 向量化入库 | L34-41 | `OllamaEmbeddings(EMBEDDING_MODEL, base_url=OLLAMA_BASE_URL)` 编码，`Chroma.from_documents(collection_name=COLLECTION_NAME)` 持久化 |
| 自测 | L44-49 | 用"夏季晴天能钓到什么鱼？"查 top-2 并打印命中块，肉眼验召回质量 |

## 四、待办 / 下一步

- [x] Python 后端建立并端到端跑通（2026-10-04）：游戏按 F7 → 后端收到体力/时间 → HUD 显示 reply。
- [x] 接入本地 LLM（2026-10-05）：Ollama `qwen2.5:3b` + LangChain，替换写死的模板分支。
- [x] RAG 知识库（2026-10-07）：wiki_data（作物/鱼/礼物）→ ChromaDB + bge-m3，F7 测试问题池验证检索链路。
- [x] 扩展状态感知第一批（2026-10-08）：C# 新增 Season/IsRaining/DayOfMonth/Money/Health 收集，Python 端 ChatRequest 同步并在提示词里做季节/天气本地化。
- [x] 原版聊天框交互（2026-10-08）：Harmony Prefix 拦截 `ChatBox.textBoxEnter(string)`，`AI ` 前缀走 AI、其他消息照原版；回复浅蓝显示在聊天框；F7 测试池保留。
- [x] LLM 配置化 / BYOK 基础（2026-10-10）：llm_factory.py + config.json，支持 ollama/deepseek/zhipu/qwen/kimi；config.json 已 gitignore。
- [x] 代码重构：消除重复与硬编码（2026-10-12）：
  - C# 抽 `SendToBackendAsync` 合并 F7/聊天框两入口（删 ~80 行重复，霰弹式修改消失）
  - Python `/chat` 加两层 try-except，失败返回友好文本而非 500，traceback 打完整堆栈
  - 新建 `aipy/config.py` 集中管理嵌入模型/集合名/检索 top-k/路径，消除 main.py 与 build_knowledge_base.py 之间的 5 处硬编码
- [ ] 游戏内输入框自研方案：已被"原版聊天框"方案替代，除非以后要富文本/特殊 UI，否则不再做。
- [ ] 加防连点：思考中重复提问应忽略或排队，别刷一堆请求（F7 和聊天框两个入口都要防）。
- [ ] 扩展状态感知第二批：背包、当前工具、当日任务、NPC 好感等；并考虑把季节/天气拼进检索 query（目前只进 LLM 上下文）。
- [x] 多轮对话记忆（2026-10-12）：LangGraph 对话图（retrieve → chat）+ Checkpointer，`thread_id=玩家名` 隔离历史；检索/拼提示词/调 LLM 下沉到图节点，main.py 只剩调度。
- [x] 记忆持久化（2026-10-13）：MemorySaver 换 AsyncSqliteSaver，历史落盘 `AIpy/data/checkpoints.db`，重启后端不丢（同步版 SqliteSaver 不支持 async，见踩坑 19）。
- [x] 历史长度上限（2026-10-13）：chat_node 只把最近 20 条（`[-21:-1]`，约 10 轮）塞给 LLM，更早历史仍完整留在 SQLite；token 成本可控且不丢数据。
- [ ] 模型参数可配置：检索 top-k 已挪进 `aipy/config.py`（2026-10-12）；`temperature=0.7` 仍在 `llm_factory.py` 两处写死，待挪进 config.json；配置改完目前要重启后端。
- [ ] 玩家档案 RAG：目前知识库只有游戏通识，还没有玩家个人档案检索。
- [ ] F7 / `AI ` 前缀写进配置可改，避免和别的 Mod 撞键。
- [ ] emotion 现在固定 happy 只打日志（失败时返回 error），后面接表情/立绘显示。
- [ ] main.py L12 有一行未使用的 `from langchain.chat_models import init_chat_model`，下次改动时顺手清理。

## 五、环境备忘

- Python 后端目录：`D:\AI_Projects\AIpy`（与本 Mod 同一个根仓库），入口 `main.py`，FastAPI + uvicorn，uv 管理依赖，Python >=3.11。
- Ollama 模型：嵌入固定用 `bge-m3`（必装，RAG 向量化）；对话模型本地模式用 `qwen2.5:3b`，云端模式不需要本地对话模型，两个模型都要先 `ollama pull`。
- LLM 配置：真实配置文件是 `AIpy/src/aipy/config.json`（⚠️ 不在项目根），模板 `AIpy/config.example.json` 入库、真实 config.json 已 gitignore。provider 可选 ollama/deepseek/zhipu/qwen/kimi；云端四家 base_url 写死在 `src/aipy/llm_factory.py` 的 PROVIDERS 表。配置启动时读一次，改完重启后端。
- 建知识库：在 `AIpy/` 下执行 `uv run python src/aipy/build_knowledge_base.py`（注意脚本在 `src/aipy/` 下，不是 scripts/）；输入 `wiki_data/*.md`，输出本地 `chroma_db/`（已 gitignore，换机要重建）。
- 启动：在 `D:\AI_Projects\AIpy` 下执行 `uv run uvicorn main:app --port 8000`（调试热重载可加 `--reload`；切记是 `uv run`，`uv uvicorn` 会报错）。
- 接口：`POST http://127.0.0.1:8000/chat`。入参字段故意写成 PascalCase 对齐 C# 序列化输出，共 10 个：PlayerName/Stamina/Health/GameTime/DayOfMonth/Season/IsRaining/Location/Money/Message；返回小写 `{"reply":..., "emotion":...}`。改字段两头要一起改，且改完 C# 别忘了重新 build。
- 多轮记忆：LangGraph `AsyncSqliteSaver`（SQLite 落盘 `AIpy/data/checkpoints.db`），`thread_id` 用玩家名隔离；重启后端历史不丢，删该文件即清空（见核心机制备忘第 7 条、踩坑 19）。
- Harmony：dll 在 `D:\Steam\steamapps\common\Stardew Valley\smapi-internal\0Harmony.dll`，csproj 显式引用 + `Private=false`；Entry 里 `new Harmony(ModManifest.UniqueID).PatchAll()` 安装。
- 编译命令：在 `ModTemplate` 目录下 `dotnet build`。
- 只验证语法不部署：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`。
- DLL 输出：`d:\AI_Projects\stardewAI\ModTemplate\bin\Debug\net6.0\StardewAIAssistant.dll`。
- 游戏路径：`D:\Steam\steamapps\common\Stardew Valley`。
- Mods 目录：`D:\Steam\steamapps\common\Stardew Valley\Mods\StardewAIAssistant`（build 后自动复制，游戏开着会失败）。
- 版本：项目 net6.0；游戏 1.6.15；SMAPI MinimumApiVersion 4.0.0；NuGet `Pathoschild.Stardew.ModBuildConfig` 4.1.0。
- manifest 关键项：UniqueID `yz.StardewAIAssistant`，EntryDll `StardewAIAssistant.dll`。
