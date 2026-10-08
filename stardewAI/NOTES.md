# NOTES — StardewAIAssistant 开发笔记

> 给自己回顾用。只记坑和要点，不抄教程。
> 最后更新：2026-10-07

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
   - 字符串直接 `$"x={v}"` 插值；泛型 `<T>` 运行期保留，不是 C++ 模板那套编译期展开。

## 三、代码地图

### C# 端：`ModTemplate/ModEntry.cs`

| 方法/类/字段 | 位置 | 作用 |
|---|---|---|
| `BackendUrl` | L33 | 后端地址 `http://127.0.0.1:8000/chat` |
| `HttpClient` | L39 | 全局复用，15 秒超时 |
| `JsonOptions` | L49 | 小写键名大小写不敏感映射 |
| `_mainThreadActions` | L60 | 主线程任务队列 |
| `TestQuestions` | L65 | 测试问题池（5 个 RAG 验证问题：鱼/礼物/作物/生日/钓点） |
| `_questionIndex` | L78 | 问题池轮转下标，取余自增 |
| `ModEntry`（类） | L30 | Mod 入口，继承 SMAPI 的 `Mod` |
| `Entry` | L80 | 启动时跑一次，订阅 ButtonPressed + UpdateTicked |
| `OnButtonPressed` | L92 | F7 主流程：取测试问题→安检→收集状态（含 Message）→弹"思考中"→异步 POST→解析→投递 HUD |
| `OnUpdateTicked` | L199 | 主线程每帧执行，消费 `_mainThreadActions` 队列 |
| `EnqueueHudMessage` | L227 | 把显示 HUD 的动作打包入队（可在后台线程调） |
| `GameStateData`（内部类） | L254 | 发给后端的模型共 10 字段：基础 PlayerName/Stamina/GameTime/Location/Message(L261-262) + 扩展 Season/IsRaining/DayOfMonth/Money/Health(L264-278) |
| `ChatResponse`（内部类） | L285 | 后端返回模型：Reply/Emotion |

### Python 端：`AIpy/main.py`（RAG 检索主逻辑）

| 块 | 位置 | 作用 |
|---|---|---|
| 向量库初始化 | L13-23 | 模块级只跑一次：`CHROMA_DIR` → `OllamaEmbeddings(bge-m3)` → `Chroma(collection_name="stardew_wiki")` → `retriever`（k=3） |
| `llm` | L26 | 对话模型 `ChatOllama(qwen2.5:3b, temperature=0.7)` |
| `SYSTEM_PROMPT` | L30-38 | 角色设定 + 三条铁律：游戏知识只准依据参考资料 / 资料没有就承认不知道 / 闲聊可自由发挥 |
| `ChatRequest` / `ChatResponse` | L42-52 / L54-56 | 请求 10 个字段：基础 5 个（PlayerName/Stamina/GameTime/Location/Message）+ 扩展 5 个（Season/IsRaining/DayOfMonth/Money/Health） |
| `chat_endpoint` | L61 | `/chat` 主流程，三步：①检索 ②拼提示词 ③调 LLM |
| 查询选择 | L67-70 | 有 `Message` 用 Message 检索；为空回退到"地点+时间+体力"场景描述（**扩展字段暂未参与检索 query**） |
| RAG 检索 | L74-77 | `retriever.ainvoke(query)` 异步召回，`page_content` 用空行拼成 context |
| 季节/天气本地化 | L81-86 | `season_cn` 字典把 spring/summer/fall/winter 译为春季/夏季/秋季/冬季；`IsRaining` bool 译为"正在下雨/天气晴朗" |
| 提示词拼装 | L88-100 | 结构化多行状态块（地点/季节+第N天/天气/时间/体力/270/血量/100/金币）+ 【玩家的问题】+ 【参考资料】 |
| LLM 调用 | L103-106 | SystemMessage + HumanMessage，`ainvoke` 异步，`content.strip()` 后返回（emotion 暂固定 happy） |

### Python 端：`AIpy/src/aipy/build_knowledge_base.py`（建库脚本）

| 块 | 位置 | 作用 |
|---|---|---|
| 路径常量 | L10-12 | `parents[2]` 回到 `AIpy/` 根；输入 `wiki_data/`，输出 `chroma_db/` |
| 切块器 | L17-20 | `MarkdownHeaderTextSplitter` 按 `##` 切，`strip_headers=False` 保留标题（保证 chunk 自包含） |
| 读文件切块 | L23-29 | glob 所有 `*.md`，每块打 `source` metadata（来源文件名） |
| 删旧库 | L34-35 | `shutil.rmtree` 全量重建，防重复追加 |
| 向量化入库 | L37-43 | bge-m3 编码，`Chroma.from_documents` 持久化到 `chroma_db/` |
| 自测 | L47-51 | 用"夏季晴天能钓到什么鱼？"查 top-2 并打印命中块，肉眼验召回质量 |

## 四、待办 / 下一步

- [x] Python 后端建立并端到端跑通（2026-10-04）：游戏按 F7 → 后端收到体力/时间 → HUD 显示 reply。
- [x] 接入本地 LLM（2026-10-05）：Ollama `qwen2.5:3b` + LangChain，替换写死的模板分支。
- [x] RAG 知识库（2026-10-07）：wiki_data（作物/鱼/礼物）→ ChromaDB + bge-m3，F7 测试问题池验证检索链路。
- [x] 扩展状态感知第一批（2026-10-08）：C# 新增 Season/IsRaining/DayOfMonth/Money/Health 收集，Python 端 ChatRequest 同步并在提示词里做季节/天气本地化。
- [ ] 游戏内输入框：现在 F7 只能轮流发测试问题池，还不能让玩家打字（SMAPI 中文 IME 是难点）。
- [ ] 加防连点：思考中再按 F7 应忽略或排队，别刷一堆请求。
- [ ] 扩展状态感知第二批：背包、当前工具、当日任务、NPC 好感等；并考虑把季节/天气拼进检索 query（目前只进 LLM 上下文）。
- [ ] 多轮对话记忆：现在每请求都是独立的，没带历史（计划用 LangGraph）。
- [ ] 玩家档案 RAG：目前知识库只有游戏通识，还没有玩家个人档案检索。
- [ ] F7 写进 config 可改键，避免和别的 Mod 撞键。
- [ ] emotion 现在固定 happy 只打日志，后面接表情/立绘显示。

## 五、环境备忘

- Python 后端目录：`D:\AI_Projects\AIpy`（与本 Mod 同一个根仓库），入口 `main.py`，FastAPI + uvicorn，uv 管理依赖，Python >=3.11。
- Ollama 模型：对话 `qwen2.5:3b`，嵌入 `bge-m3`，两个都要先 `ollama pull`。
- 建知识库：在 `AIpy/` 下执行 `uv run python src/aipy/build_knowledge_base.py`（注意脚本在 `src/aipy/` 下，不是 scripts/）；输入 `wiki_data/*.md`，输出本地 `chroma_db/`（已 gitignore，换机要重建）。
- 启动：在 `D:\AI_Projects\AIpy` 下执行 `uv run uvicorn main:app --port 8000`（调试热重载可加 `--reload`）。
- 接口：`POST http://127.0.0.1:8000/chat`。入参字段故意写成 PascalCase 对齐 C# 序列化输出，共 10 个：PlayerName/Stamina/Health/GameTime/DayOfMonth/Season/IsRaining/Location/Money/Message；返回小写 `{"reply":..., "emotion":...}`。改字段两头要一起改，且改完 C# 别忘了重新 build。
- 编译命令：在 `ModTemplate` 目录下 `dotnet build`。
- 只验证语法不部署：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`。
- DLL 输出：`d:\AI_Projects\stardewAI\ModTemplate\bin\Debug\net6.0\StardewAIAssistant.dll`。
- 游戏路径：`D:\Steam\steamapps\common\Stardew Valley`。
- Mods 目录：`D:\Steam\steamapps\common\Stardew Valley\Mods\StardewAIAssistant`（build 后自动复制，游戏开着会失败）。
- 版本：项目 net6.0；游戏 1.6.15；SMAPI MinimumApiVersion 4.0.0；NuGet `Pathoschild.Stardew.ModBuildConfig` 4.1.0。
- manifest 关键项：UniqueID `yz.StardewAIAssistant`，EntryDll `StardewAIAssistant.dll`。
