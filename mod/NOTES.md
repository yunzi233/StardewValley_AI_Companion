# NOTES — StardewAIAssistant 开发笔记

> 给自己回顾用。只记坑和要点，不抄教程。
> 最后更新：2026-10-14

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
    - 解决：嵌入统一走 `aipy/embeddings.py` 的 `FastEmbedEmbeddings`，模型名从 `aipy/config.py` 的 `EMBEDDING_MODEL` 读，建库脚本与 main.py 共用同一处配置；换模型 = 换维度（bge-m3 是 1024，bge-small-zh-v1.5 是 512）= 必须重新全量建库，没有捷径。

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
    - 现象：把 `config.example.json` 复制到 `backend/config.json`（项目根），后端照样报 `缺少配置文件`。
    - 原因：`llm_factory.py` 里写的是 `Path(__file__).parent / "config.json"`，工厂模块在 `src/aipy/`，所以真实配置必须位于 `backend/src/aipy/config.json`；而 example 模板放在 `backend/` 根目录，复制时目标路径容易写错。
    - 解决：`cp config.example.json src/aipy/config.json`（在 backend/ 下执行）。模板和真实配置不同目录这点要在 README 写醒目。
    - 【2026-10-15 已解决】配置路径统一：新增 `aipy.config.CONFIG_FILE = PROJECT_ROOT / "config.json"`，工厂改为 `from aipy.config import CONFIG_FILE`，不再自己拼 `Path(__file__)`；真实配置迁到 `backend/config.json`（模板/真实同目录，坑源铲除）。迁移动机是打包：PyInstaller 模式下 PROJECT_ROOT=exe 所在目录，config.json 放 exe 同级即"项目根"。

18. **`uv uvicorn` 会被当成 uv 的子命令，必须 `uv run`**
    - 现象：执行 `uv uvicorn main:app --reload` 报 `error: unrecognized subcommand 'uvicorn'`。
    - 原因：uv 不是直接执行器，`uvicorn` 不是它的子命令；要用 `uv run` 在项目环境里执行命令。
    - 解决：`uv run uvicorn main:app --port 8000`（`uv run python xxx.py` 同理）。

19. **同步版 `SqliteSaver` 不支持 async：直接抛 `NotImplementedError`**
    - 现象：`build_graph` 里用 `SqliteSaver(sqlite3.connect(...))`，一调 `await graph.ainvoke` 就炸：`NotImplementedError: The SqliteSaver does not support async methods. Consider using AsyncSqliteSaver instead.`。
    - 原因：LangGraph 的同步 Checkpointer 没实现 `aget_tuple`/`aput` 等异步方法，而我们的图全程跑在 async 里（FastAPI + `ainvoke`）。
    - 解决：换 `AsyncSqliteSaver`（`from langgraph.checkpoint.sqlite.aio import ...`）+ `aiosqlite` 依赖。妙处：`aiosqlite.connect()` **不 await 直接传给构造函数**（懒连接）也能用，所以 `build_graph()` 保持同步签名，main.py 不用动。实测文件持久化、模拟重启历史恢复均 OK。

20. **接第三方 Mod 的 API 时，必须用 shared/ 版本的接口文件**
    - 现象：`GetApi<IGenericModConfigMenuApi>` 报 `defines method X which doesn't exist in the API`，或拿到 API 后调方法抛 `MissingMethodException`。
    - 原因：GMCM 的 GitHub 仓库里 `framework/` 目录下的 API 文件是**内部开发版**，跟着作者进度频繁变动；而 `shared/SpaceShared.Mods/APIs/` 才是作者专门维护给**外部 Mod** 的稳定接口。用了 framework/ 版本会导致方法签名不匹配。
    - 解决：从 GMCM 仓库的 `shared/` 路径拿 `IGenericModConfigMenuApi.cs`；下载时如果 raw.githubusercontent.com 被墙，优先走 `ghproxy.com` 镜像。

21. **langgraph-checkpoint-sqlite 3.x：AsyncSqliteSaver 必须在 async 上下文里构造**
    - 现象：某次 `uv sync` 后后端启动直接炸：`RuntimeError: no running event loop`（aio.py 的 `AsyncSqliteSaver.__init__` 里调 `asyncio.get_running_loop()`）。
    - 原因：3.x 的构造函数要求有 running event loop，而 main.py 是模块顶层同步调 `build_graph()`，此时还没有事件循环；老版本没这个要求，升级即回归。
    - 解决：graph 构建挪进 FastAPI lifespan（`@asynccontextmanager`，startup 阶段在 async 上下文里 `build_graph()`）；顶层只留 `init_agent()` 注入依赖。教训：依赖升级后启动失败，先怀疑第三方包的构造时序变化。

22. **SMAPI 4 把 `helper.Content` 拆成了两个：ModContent / GameContent**
    - 现象：用 `helper.GameContent.Load<Texture2D>("assets/robot.png")` 加载自己 Mod 文件夹里的 png，运行时报资源加载失败——它去**游戏内容管线**里找，当然没有。
    - 原因：SMAPI 4 移除了 3.x 的 `helper.Content`，拆成两个：`ModContent.Load` 加载 **Mod 文件夹里的原始文件**（PNG/JSON 等）；`GameContent.Load` 加载**游戏内容管线的 asset**（游戏自带的物品图、音效等）。
    - 解决：Mod 文件夹内的原始文件用 `helper.ModContent.Load<Texture2D>("assets/robot.png")`。
    - 顺带确认：`ModBuildConfig` 会把项目目录下的 `assets/` 整个复制到 Mods 输出目录，不需要改 csproj。

23. **SMAPI 4 没有任何文本输入事件：中文 IME 只能直连 MonoGame 的 `GameWindow.TextInput`**
    - 背景：想自研中文输入框，但 `Keyboard.GetState()` 只报物理键，收不到输入法上屏的字符，必须接窗口的文本事件。
    - 反射查证（MetadataLoadContext 扫 StardewModdingAPI.dll / MonoGame.Framework.dll / Stardew Valley.dll）：
      - SMAPI 4 的 `IInputEvents` 只有 5 个事件（ButtonsChanged / ButtonPressed / ButtonReleased / CursorMoved / MouseWheelScrolled），**没有 TextInput / TextEdited**（3.x 曾有，4.0 移除）；`StardewModdingAPI.Events` 命名空间下也没有相关 EventArgs。
      - MonoGame 侧存在：`Microsoft.Xna.Framework.GameWindow.TextInput`，签名 `event EventHandler<Microsoft.Xna.Framework.TextInputEventArgs> TextInput`。
      - `TextInputEventArgs` 是 **struct**，成员是**公共字段不是属性**：`char Character`（IME 上屏字符，中文逐字触发一次事件）、`Keys Key`（IME 输入时不可靠，别依赖）。
      - `Game1.game1` 是 **public static** 字段；GameLaunched 时窗口已创建，可安全订阅。
    - 解决：GameLaunched 里 `Game1.game1.Window.TextInput += OnWindowTextInput;`，回调读 `e.Character`；`\r \n \b \t` 等控制字符用 `char.IsControl` 过滤，Enter/Esc/Backspace 走 SMAPI ButtonPressed 分支（双保险）。
    - 字符链路：SDL_TEXTINPUT → `Sdl.Keyboard.TextInputEvent` → `GameWindow.TextInput`（DesktopGL 版，反射见到 Sdl+Keyboard+TextInputEvent 佐证）。

24. **项目文件夹改名后，Python 虚拟环境里的脚本蹦床（trampoline）失效**
    - 现象：`AIpy/` → `backend/`（根目录 `AI_Projects` → `StardewValley_AI_Companion`）后，`uv run uvicorn main:app --port 8000` 报 `error: uv trampoline failed to canonicalize script path`（此前已输出 `Built aipy @ file:///…` 并重装 1 个包）。
    - 原因：uv 在 Windows 上把控制台脚本入口编译成 `.exe` 蹦床，**内部写死了 venv 的绝对路径**。建 venv 时是 `D:\AI_Projects\AIpy\.venv`，改名后变成 `D:\StardewValley_AI_Companion\backend\.venv`，旧蹦床解引用失败。依赖清单（`pyproject.toml` / `uv.lock`）未变，**不是依赖坏了**；uv 只因可编辑包 `aipy` 的路径变化自动重建了 `aipy.exe`，不会顺带刷新 `uvicorn.exe`。
    - 解决：在 `backend/` 下执行 `uv sync --reinstall-package uvicorn`，强制重装该包并用新路径重生成蹦床；其它控制台脚本若报同样错，同一命令换个包名即可。
    - 佐证：修复前 `.venv\Scripts\uvicorn.exe` 时间戳停留在建 venv 当天（2026-10-4），修复后刷新为当日，`uvicorn --version` 恢复输出。
    - 附带：`pyvenv.cfg` 里 `prompt = AIpy` 是旧名残留，**只影响手动 `activate` 的提示符**，无害。
    - 对比：数据库路径不受影响——`config.py` 用 `Path(__file__).resolve().parents[2]` 相对推导 `PROJECT_ROOT`，`chroma_db/`、`data/checkpoints.db` 随目录搬家自动跟随，无需重建。

25. **fastembed 首次下载模型：国内直连 HuggingFace 会超时，且缓存落在系统临时目录**
    - 现象：`FastEmbedEmbeddings("BAAI/bge-small-zh-v1.5")` 首次构造时连报 `Could not download model from HuggingFace: timed out`，重试 3 次后 `ValueError: Could not load model ... from any source`。
    - 原因：fastembed 底层用 `huggingface_hub` 拉 ONNX 模型，默认端点 `huggingface.co` 在国内不稳定；它自带的备用源（GCS）同样连不上。
    - 解决：先设镜像再跑——PowerShell `$env:HF_ENDPOINT="https://hf-mirror.com"`（`huggingface_hub` 自动识别该环境变量），再执行建库脚本；模型 ~23MB，约 20 秒拉完。
    - 附带：默认缓存目录是 `tempfile.gettempdir()/fastembed_cache`（即系统临时目录），**可能被清理工具清掉**，之后首次检索会重新下载。【2026-10-15 已解决】`FastEmbedEmbeddings` 现在把 `cache_dir` 固定为 `backend/models/`（config.py 的 `MODELS_DIR`）：已下载则离线加载、没下载则下到这里；fastembed 0.9 的 `download_model` 缓存命中时不联网，`HF_HUB_OFFLINE=1` 下实测可正常出 512 维向量。打包时整个 `models/` 随包分发即零下载（目录结构是 HF 风格 `models--Qdrant--bge-small-zh-v1.5/`，实际仓库名是 Qdrant 镜像而非 BAAI 原仓库）。
    - 附带：重建向量库前必须先停掉运行中的后端——旧 `chroma_db/*.bin` 被占用时 `shutil.rmtree` 直接报 `PermissionError [WinError 32]`。

26. **SMAPI 4 没有 `GameLoop.Exiting` 事件（3.x 的 API 被 4.0 移除），退出钩子要用 `AppDomain.ProcessExit`**
    - 现象：照旧文档写 `helper.Events.GameLoop.Exiting += ...` + `ExitingEventArgs`，编译报 CS0246 找不到 `ExitingEventArgs`。
    - 根因：SMAPI 4.0 移除了该事件（旧版它就触发不稳定）。用 Mono.Cecil 反射 `StardewModdingAPI.dll` 确认：`IGameLoopEvents` 全部事件里没有任何 Exit 类事件，整个程序集的 `*EventArgs` 里也没有 `ExitingEventArgs`；只剩内部方法 `SGame.OnExiting`（MonoGame `Game.OnExiting` 的重写，Mod 无法订阅）。
    - 解决：`AppDomain.CurrentDomain.ProcessExit += OnProcessExiting`——窗口关闭 / 游戏菜单退出等正常退出路径都会在 CLR 关闭前触发；注意两点：① 处理函数有约 2 秒时限，只做 `Kill(entireProcessTree:true)`（毫秒级）够用；② 任务管理器强杀 / 崩溃时不触发（旧 `GameLoop.Exiting` 同样不保证，可接受）。
    - 附带：反射 SMAPI 程序集别用 `Assembly.LoadFrom` + `GetTypes()`（缺依赖抛 ReflectionTypeLoadException），用游戏自带的 `smapi-internal\Mono.Cecil.dll` 的 `ModuleDefinition.ReadModule` 读类型/事件最稳。

27. **PyInstaller 打包 chromadb：全部组件 importlib 动态加载，必须 `collect_submodules("chromadb")`**
    - 现象：backend.spec 第一版打包出的 exe 启动即挂 `ModuleNotFoundError: No module named 'chromadb.api.rust'`（栈在 `chromadb.config.get_class → importlib.import_module`）。
    - 根因：chromadb 的各组件（api 实现 / segment / db / 遥测）都是运行时按 config 里的字符串 `importlib.import_module("chromadb.xxx.Yyy")` 动态加载的，PyInstaller 静态分析一个都跟不到；逐个补 hiddenimports 没完没了（修完 api.rust 还有下一个）。
    - 解决：spec 里 `collect_submodules("chromadb")` 一网打尽——只收集 .py 模块，体积可控；**别用 collect_all**（会把测试数据冗余资源全塞进去）。同理 uvicorn 的 `loops/protocols/lifespan` 也是运行时动态挑选实现，要在 hiddenimports 里显式列 `.auto` + 具体实现模块（如 `uvicorn.protocols.http.h11_impl`）。
    - 附带：数据文件（chroma_db/models/config.json）**不打进 exe**，放 exe 同级目录即可——config.py 的 frozen 分支让 PROJECT_ROOT=exe 所在目录，exe 同级就是"项目根"；验证 exe 别用"命令结束即回收子进程"的终端包装器，要用独立后台任务持有进程再测 /health。

28. **SMAPI 的配置文件默认叫 `config.json`（不是类名 `ModConfig.json`）**
    - 现象：文档/代码注释里长期写 `ModConfig.json`，实际玩家目录里 SMAPI 生成的是 `Mods/StardewAIAssistant/config.json`——按文档找文件找不到。
    - 根因：`helper.ReadConfig<ModConfig>()` / `WriteConfig` 的默认文件名就是 `config.json`，类名 ModConfig 只是 C# 类型名，不决定磁盘文件名。
    - 解决：文档里统一区分两个同名 `config.json`——游戏侧 `Mods/StardewAIAssistant/config.json`（Provider/ApiKey/Model/Temperature/BackendDir/AutoStartBackend，SMAPI 默认文件名）vs 后端侧 `backend/config.json`（provider/api_key/model/temperature，aipy.config.CONFIG_FILE）；README 配置文件表格已更新。

29. **`uvicorn.run()` 阻塞：`if __name__ == "__main__"` 块必须放文件末尾**
    - 现象：main.py 里把入口块插在 /chat 和 /config 两个路由之间，启动后 GET /config 直接 404。
    - 根因：`uvicorn.run(app, ...)` 阻塞在当前行，写在中间会导致后面的 `@app.post` 装饰器根本执行不到，路由注册丢失。
    - 解决：入口块永远放文件末尾（所有路由注册之后）；开发调试用 `uv run uvicorn main:app`（uvicorn 命令行自己 import main 模块，不经过 `__main__` 块），打包 exe 才走 `python main.py` / 双击 exe 走 `__main__`。

30. **PyInstaller 打包后 `Path(__file__)` 失效，PROJECT_ROOT 必须按 `sys.frozen` 分支**
    - 现象：打包后所有从 `Path(__file__)` 派生的路径（chroma_db / models / config.json）全部错位。
    - 根因：PyInstaller 下 `__file__` 指向临时解包目录（onedir 的 `_internal/`），不是 exe 所在目录；玩家改配置、换知识库需要在 exe 同级操作，不可能去 `_internal/` 里找。
    - 解决：`config.py` 的 `PROJECT_ROOT` 加 frozen 分支——`getattr(sys, "frozen", False)` 为 True（打包模式）时用 `Path(sys.executable).resolve().parent`，exe 同级即"项目根"；开发模式保持 `Path(__file__).resolve().parents[2]`。所有派生常量（CHROMA_DIR / MODELS_DIR / CONFIG_FILE / CHECKPOINT_DB / WIKI_DATA_DIR）不用改，自动跟随。

31. **StartBackendAsync 并发重复调用：backend.log 被占用 + 重复起进程**
    - 现象：填了 BackendDir 后保存 GMCM，日志显示 StartBackendAsync 被调两次（OnGameLaunched + save 回调的 EnsureBackendReadyAsync），第二次抛 `IOException: The process cannot access the file '...\backend.log' because it is being used by another process`。
    - 根因：a) 第一次打开的 `StreamWriter`（AutoFlush=true）从未 Dispose，一直持有 backend.log 句柄，第二次 `new StreamWriter(path, append:false)` 要独占写 → 冲突；b) 第二次调用不知道 `_backendProcess` 里已存着一个存活进程，没有 a 拦着会再起一个 exe → 8000 端口冲突。
    - 解决：① writer 提升为字段 `_backendLogWriter`，`OnProcessExiting` 里 Dispose 释放句柄（Dispose 幂等，和泵流任务的 ContinueWith 重复调用不抛）；② 方法开头加幂等检查——锁内读 `_backendProcess is { HasExited: false }`，存活则打日志"后端进程已存在，跳过重复启动，仅等待就绪"，跳过全部启动+开日志逻辑，直接走公共轮询分支；③ 进程重启场景（上次 HasExited=true 再次走到启动逻辑）打开新 writer 前先 Dispose 旧 writer。
    - 教训：凡是"打开文件句柄 + fire-and-forget 后台任务持有"的资源，句柄所有权要提升到字段级并明确 Dispose 时机；可能并发进入的启动方法先做幂等检查再干活。

32. **BackendDir 空值拦截让自动推断成死代码**
    - 现象：发布模式下玩家 BackendDir 留空，后端不自动拉起——EnsureBackendReadyAsync 里"BackendDir 为空 → HUD 提示 + return"的分支直接拦截，StartBackendAsync 里"为空则推断为 Mod 目录下 backend/"的逻辑永远走不到。
    - 根因：判空逻辑在上游 EnsureBackendReadyAsync 写了一份（早期版本），下游 StartBackendAsync 后来加了自动推断，两份策略矛盾，上游的先执行。
    - 解决：删掉上游的空值拦截分支，目录决策统一收口到 StartBackendAsync；下游补"既无 backend.exe 也无 pyproject.toml → 提示未找到后端"的兜底检查（发布/开发形态各认标志物，目录不存在时 File.Exists 自然为 false，顺带覆盖"路径填错"场景）。
    - 教训：同一策略的判断只能有一个权威落点，上下游各写一份必有一个变死代码。

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
   - 持久化用 `AsyncSqliteSaver` + `aiosqlite.connect(CHECKPOINT_DB)`（懒连接），历史落盘 `backend/data/checkpoints.db`，重启后端不丢；想清空记忆就删这个文件（同步版 SqliteSaver 不支持 async，见踩坑 19）。

8. **Mod 间 API 调用（GMCM 集成）**
   - 时序：Entry 执行时其他 Mod 可能还没初始化完，此时 `GetApi` 会返回 null 并触发 SMAPI 警告 `Tried to access a mod-provided API before all mods were initialized.`——必须订阅 `GameLaunched` 事件再调。
   - 获取：`helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu")`，返回 null 表示玩家没装 GMCM，跳过注册即可。
   - 注册：`Register` + `AddTextOption` 三连 + `AddNumberOption`（数值参数如 temperature 用它，min/max/interval 是 `float?` 可空参数）；save 回调是同步 `Action`，里面要 fire-and-forget 异步推送（`_ = PushConfigToBackendAsync(...)`）。
   - 接口文件：必须用 GMCM 仓库 `shared/` 目录下的版本，framework/ 是内部开发版（见踩坑 20）。

9. **中文 IME 输入框的按键接管（v0.9 自研输入框）**
   - 字符流：只在 `IsOpen` 时接收 `Window.TextInput` 的 `e.Character`，控制字符（`char.IsControl`）一律丢弃。
   - 按键流：输入框打开期间 `Helper.Input.Suppress(e.Button)` 吃掉所有按键——SMAPI 会屏蔽到按键释放为止，角色不会移动、游戏不响应；Esc / Enter / Backspace 在 Suppress 之后自行处理。
   - 提交复用：回车后调 `HandleAiChatQuestion(text)`（与聊天框补丁同一入口），回复照常进聊天框。
   - 显示：`RenderedHud` 事件里用 `Game1.smallFont` 画一行文本（中文版字体图集含汉字，实测中文正常显示）。
   - 打开键是硬编码 F7；GMCM 有 `AddKeybindList`（接口文件 L112），后续配置化时 ModConfig 字段用 `KeybindList` 类型即可。

## 三、代码地图

### C# 端：`ModTemplate/ModEntry.cs`

| 方法/类/字段 | 位置 | 作用 |
|---|---|---|
| `BackendUrl` | L40 | 后端地址 `http://127.0.0.1:8000/chat` |
| `HttpClient` | L46 | 全局复用，15 秒超时 |
| `JsonOptions` | L54 | 小写键名大小写不敏感映射 |
| `_mainThreadActions` | L67 | 主线程任务队列（HUD 和聊天框消息共用） |
| `Instance` | L74 | 静态单例，供 Harmony 静态补丁回调实例方法 |
| `ModEntry`（类） | L30 | Mod 入口，继承 SMAPI 的 `Mod` |
| `_config` | L77 | GMCM 配置（Provider/ApiKey/Model/Temperature） |
| `_companion` | L80 | 浮动精灵实例（见 FloatingCompanion.cs 小节） |
| `_inputBox` | L87 | 中文输入框实例（见 ChineseInputBox.cs 小节） |
| `_robotTexture` | L93 | 精灵贴图（启动时 ModContent.Load 加载，失败保持 null 走方块占位） |
| `_backendProcess` | L114 | 自己拉起的后端进程（null=本会话没拉起过）；退出钩子按它 Kill，不碰玩家手动启动的后端 |
| `_backendProcessLock` | L117 | 保护 `_backendProcess` 的锁（启动协程与退出钩子可能在不同线程） |
| `Entry` | L119-176 | 启动时跑一次：单例保存 → `ReadConfig<ModConfig>()` → `Harmony.PatchAll()` → ModContent.Load 精灵贴图（try-catch 兜底）→ 订阅 ButtonPressed/UpdateTicked/GameLaunched/**ProcessExit**/RenderedWorld/RenderedHud |
| `OnGameLaunched` | L178-196 | GameLaunched 事件处理：RegisterGmcmConfig + 订阅 `Game1.game1.Window.TextInput`（中文 IME 唯一入口，见踩坑 23）+ fire-and-forget `EnsureBackendReadyAsync`（启动保障流程） |
| `OnRenderedWorld` | L170-174 | 每帧世界绘制完成后：`_companion.Update()` + `_companion.Draw(e.SpriteBatch)` |
| `OnWindowTextInput` | L182-186 | MonoGame 原生文本输入回调：`IsOpen && !char.IsControl(e.Character)` 才 Append（中文 IME 上屏逐字触发） |
| `OnRenderedHud` | L193-201 | 输入框打开时屏幕底部画 `输入框：<文本>_`（`Game1.smallFont`，占位样式） |
| `RegisterGmcmConfig` | L224-330 | GetApi → Register + AddTextOption×4（Provider/ApiKey/Model/BackendDir）+ AddNumberOption（回答温度 0.0-2.0 步长 0.1）+ AddBoolOption（AutoStartBackend）；save 回调写 JSON + fire-and-forget 推送后端；try-catch 兜底 |
| `OnButtonPressed` | L285-324 | F7 开关输入框；打开期间 `Helper.Input.Suppress` 吃掉所有按键 → Enter 提交（复用 `HandleAiChatQuestion`）/ Esc 关闭 / Back 删除末尾字符 |
| `OnUpdateTicked` | L329 | 主线程每帧执行，消费 `_mainThreadActions` 队列 |
| `EnqueueHudMessage` | L357 | 把显示 HUD 的动作打包入队（可在后台线程调） |
| `ChatReplyColor` / `ChatErrorColor` | L383 / L386 | 聊天框回复浅蓝 / 错误橙红的颜色常量 |
| **`SendToBackendAsync`** | **L398** | **对话核心流程（两入口共用）**：收集 14 字段状态→"思考中"→异步 POST→解析→按 `useChatBox` 分支显示→4 个 catch（HttpRequestException/OperationCanceled/Json/Exception） |
| `HandleAiChatQuestion` | L520 | 聊天框/输入框共用提交入口（安检→调 `SendToBackendAsync(useChatBox:true)`） |
| `EnqueueChatMessage` | L540 | 把 `chatBox.addMessage(text, color)` 打包入队；浅蓝=回复/提示，橙红=错误 |
| `EnqueueChatMessageFromPatch` | L553 | internal 包装，供补丁类调用用法提示 |
| `PushConfigToBackendAsync` | L561-590 | GMCM save 回调 fire-and-forget：POST /config 推送 provider/api_key/model/temperature；成功 HUD 提示，失败 HUD+日志 |
| `GameStateData`（内部类） | L597 | 发给后端的模型共 17 字段：基础 5（PlayerName/Stamina/GameTime/Location/Message）+ 第一批 5（Season/IsRaining/DayOfMonth/Money/Health）+ 第二批 a 4（CurrentTool/FarmingLevel/MiningLevel/DaysPlayed）+ 第二批 b 3（Inventory/NPCAffection/CurrentQuest） |
| `ChatResponse`（内部类） | L641 | 后端返回模型：Reply/Emotion |
| `CheckBackendHealthAsync` | L605 附近 | internal，GET `/health`（3 秒 CTS 超时）→ `(ok, llmConfigured)`；任何异常=不可达；"自动拉起轮询"复用同一方法 |
| `EnsureBackendReadyAsync` | L671-712 | 启动保障流程：健康检查 → 可达按 LLM 配置提示；不可达分三种（自动启动关闭 / BackendDir 空 / 有效）→ `StartBackendAsync` → 就绪后复查 LLM 给上线提示 |
| `StartBackendAsync` | L719-799 | 校验目录 → `ProcessStartInfo`（FileName=uv，`uv run uvicorn main:app --port 8000`，重定向输出，CreateNoWindow）→ 记入 `_backendProcess` → 双流读日志落 `backend.log` → 轮询 `/health` 每秒一次最多 30 次；uv 不在 PATH / 进程早退 / 超时都有对应 HUD 提示 |
| `PumpStreamAsync` | L806-825 | 后台泵：逐行读进程 stdout/stderr 写 `backend.log`（AutoFlush）。必须读流——重定向后不读，管道缓冲区写满会把 Python 卡死 |
| `OnProcessExiting` | L833-850 | `AppDomain.ProcessExit` 钩子（SMAPI 4 无 GameLoop.Exiting，见踩坑 26）：Kill 自己拉起的后端进程树，约 2 秒时限只做快操作 |
| `HealthResponse`（内部类） | ChatResponse 前 | `/health` 返回模型：Status + `llm_configured`（snake_case 必须显式 `[JsonPropertyName]`，大小写不敏感选项不忽略下划线） |

### C# 端：`ModTemplate/ModConfig.cs`（GMCM 配置模型）

| 属性 | 位置 | 作用 |
|---|---|---|
| `Provider` | L7 | AI 厂商：deepseek / qwen / zhipu / kimi / ollama，默认 deepseek |
| `ApiKey` | L10 | 云端厂商 API Key；本地 Ollama 留空 |
| `Model` | L13 | 模型名，默认 deepseek-chat |
| `Temperature` | L16 | 回答温度 0.0-2.0（默认 0.7），GMCM 数值选项 |
| `BackendDir` | L19 | backend 目录绝对路径（含 main.py 的文件夹），自动拉起后端用；留空不拉起 |
| `AutoStartBackend` | L22 | 游戏启动时是否自动拉起后端（默认 true，需 BackendDir 已填） |

### C# 端：`ModTemplate/ChatBoxTextBoxEnterPatch.cs`（Harmony 补丁）

| 成员 | 位置 | 作用 |
|---|---|---|
| `[HarmonyPatch]` 特性 | L24 | 目标 `ChatBox.textBoxEnter(string)` 重载（靠 `new[]{typeof(string)}` 区分） |
| `AiPrefix` | L28 | 常量 `"AI "`（大写 AI + 半角空格） |
| `Prefix` | L34 | Trim 后判断前缀：非 AI 消息 `return true` 放行；空问题回用法提示；正常问题回调 `Instance.HandleAiChatQuestion` 后 `return false` 跳过原版发送 |

### C# 端：`ModTemplate/FloatingCompanion.cs`（浮动精灵，新增）

| 成员 | 位置 | 作用 |
|---|---|---|
| `OffsetFromPlayer` | L18 | 相对玩家的偏移 (40, -70)：右侧、头顶上方（世界像素） |
| `Texture` | L24 | 精灵贴图，ModEntry 启动时注入；null（素材缺失）时 Draw 回退方块占位 |
| `_bobPhase` | L27 | 浮动动画相位，每帧累加 `dt * 3f`（速度），sin 曲线驱动 |
| `Update` | L32-35 | 推进浮动相位（用 `Game1.currentGameTime` 的帧间隔） |
| `Draw` | L39-77 | `Context.IsWorldReady` 安检 → 玩家 `getStandingPosition()` + 偏移 → sin 浮动（幅度 4px）→ `Game1.GlobalToLocal` 世界坐标转屏幕坐标 → 1:1 绘制贴图（layerDepth 0.9：盖住地面、被玩家/NPC 遮挡） |

### C# 端：`ModTemplate/ChineseInputBox.cs`（中文 IME 输入框，v0.9 新增）

| 成员 | 位置 | 作用 |
|---|---|---|
| `IsOpen` | L17 | 输入框开关（打开期间按键全接管） |
| `Text` | L20 | 当前输入文本（支持中文） |
| `Open` / `Close` | L26 / L32 | 打开（清空旧内容）/ 关闭（清空文本） |
| `Append` | L39 | 追加一个字符（由 ModEntry 的 OnWindowTextInput 调用，控制字符已过滤） |
| `Backspace` | L43 | 删除末尾字符（`Text[..^1]` 范围语法） |
| `Clear` | L50 | 清空文本，保持打开状态 |

纯状态类：字符接收（`Window.TextInput`）、按键分发（`ButtonPressed`）、HUD 绘制都在 ModEntry.cs。

### Python 端：`backend/main.py`（FastAPI 入口 + LangGraph 调度）

| 块 | 位置 | 作用 |
|---|---|---|
| `SYSTEM_PROMPT` | L16-38 | 角色设定 + 铁律：游戏知识只准依据参考资料 / 资料没有就承认不知道 / 非游戏话题拒答，附正反示例 |
| `ChatRequest` / `ChatResponse` / `ConfigRequest` | L40-51 / L54-56 / L59-62 | 请求 10 个字段：基础 5 个（PlayerName/Stamina/GameTime/Location/Message）+ 扩展 5 个（Season/IsRaining/DayOfMonth/Money/Health）；ConfigRequest 供 GMCM 推送 |
| `PlaceholderLLM` | L65-73 | 未配置时占位，ainvoke 返回固定提示 |
| 向量库初始化 | L100-128 | 模块级只跑一次：`CHROMA_DIR` 存在 → `FastEmbedEmbeddings` + `Chroma` → `retriever`（k=`TOP_K`）；缺失 → 打印警告 + `_NullRetriever`（检索返回空，AI 回答"不清楚"，后端不崩） |
| `_NullRetriever` | L101-108 | chroma_db 缺失的兜底检索器：`ainvoke` 恒返回 `[]`，chat_node 拿空 context 走"不知道"分支 |
| lifespan | L21-25 | async 上下文里 `build_graph()`——AsyncSqliteSaver 3.x 构造时要 running loop（见踩坑 21）；顶层 `graph = None` |
| LLM 创建 | L104-110 | try-except：`load_config()` → `create_llm()` 工厂创建，失败用 PlaceholderLLM 占位 |
| 依赖注入 | L115 | `init_agent()` 注入 llm/retriever/system_prompt（graph 已在 lifespan 构建好） |
| `chat_endpoint` | L101-155 | `/chat` 主流程（检索/拼提示词/调 LLM 已下沉到图节点）：①`thread_id=玩家名`（记忆按角色隔离）②组 `player_state` dict（9 字段）③`graph.ainvoke`（try 包裹，异常返回友好错误）④按 `result["error"]` 分支返回 retrieval_failed / llm_failed 提示 ⑤成功取 `messages[-1].content` 作回复 |
| `update_config` | L157-184 | `/config` 主流程：校验 provider/api_key/model → `save_config` 写文件 → `create_llm` 重建 → 重新 `init_agent`（不重建 graph，历史保留） |

### Python 端：`backend/src/aipy/agent/`（LangGraph 对话图，新增）

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

### Python 端：`backend/src/aipy/llm_factory.py`（LLM 工厂）

| 块 | 位置 | 作用 |
|---|---|---|
| `PROVIDERS` | L9-14 | 云端厂商预设 base_url：deepseek / zhipu（智谱）/ qwen（通义）/ kimi |
| `CONFIG_FILE` | L9 import | 从 `aipy.config` 导入（`PROJECT_ROOT / "config.json"`：开发=backend/config.json，打包=exe 同级）；`load_config` / `save_config` 共用，工厂不再自拼路径 |
| `load_config` | L19-25 | config.json 不存在直接抛 FileNotFoundError 并提示复制 example |
| `save_config` | L28-34 | 覆盖写 config.json（UTF-8，缩进 2），供 /config 接口调用 |
| `create_llm` | L37-62 | `ollama` → `ChatOllama(model, base_url, temperature)`；其他 provider 校验 api_key 非空后返回 `ChatOpenAI(model, api_key, base_url=预设, temperature)`；temperature 读 cfg（默认 0.7，老 config.json 无此字段时兼容） |

### Python 端：`backend/src/aipy/config.py`（项目级常量，新建）

集中管理原先散落在 main.py 和 build_knowledge_base.py 里的 5 处硬编码，改一处两边同时生效。

| 常量 | 位置 | 作用 |
|---|---|---|
| `PROJECT_ROOT` | L13-19 | **frozen 分支**：PyInstaller 打包模式（`sys.frozen`）= `Path(sys.executable).parent`（exe 同级即项目根）；开发模式 = `Path(__file__).resolve().parents[2]` → `backend/`。打包后 `__file__` 指向临时解包目录，全部派生路径会错位，必须切 sys.executable |
| `CONFIG_FILE` | L24 | `PROJECT_ROOT / "config.json"`，后端配置（含 API Key）；打包模式放 exe 同级，玩家好找好改好备份 |
| `CHROMA_DIR` | L27 | `PROJECT_ROOT / "chroma_db"`，向量库持久化目录 |
| `COLLECTION_NAME` | L28 | `"stardew_wiki"`，Chroma 集合名（main.py 和建库脚本必须一致） |
| `EMBEDDING_MODEL` | L37 | `"BAAI/bge-small-zh-v1.5"`，fastembed 本地 ONNX 嵌入模型名；换模型=换维度必须重建库（原 `OLLAMA_BASE_URL` 已随 Ollama 嵌入一并移除） |
| `MODELS_DIR` | L33 | `PROJECT_ROOT / "models"`，fastembed 缓存目录（`cache_dir`）：已下载离线加载、缺失自动下载到此；打包时随包分发 |
| `TOP_K` | L40 | `3`，检索召回块数 |
| `WIKI_DATA_DIR` | L43 | `PROJECT_ROOT / "wiki_data"`，知识库源 Markdown 目录 |
| `CHECKPOINT_DB` | L47 | `PROJECT_ROOT / "data" / "checkpoints.db"`，对话历史 SQLite 文件（AsyncSqliteSaver 落盘用，已 gitignore） |

### Python 端：`backend/src/aipy/embeddings.py`（本地嵌入封装，新增）

用 fastembed（ONNX Runtime）替代 Ollama 嵌入，让检索不再依赖 Ollama 服务（路线 2 零环境依赖的前提）。自写封装而非引 `langchain-community`：只需 `Embeddings` 两个方法，避免为小功能拖进庞大依赖。

| 块 | 位置 | 作用 |
|---|---|---|
| `FastEmbedEmbeddings` | L17-30 | 继承 `langchain_core.embeddings.Embeddings`，构造即加载 `fastembed.TextEmbedding`；`cache_dir` 默认指向 `MODELS_DIR`（backend/models/，本地优先、缺失才下载） |
| `embed_documents` | L21-23 | 建库/索引侧：`TextEmbedding.embed(texts)` → `list[list[float]]` |
| `embed_query` | L25-27 | 检索侧：`TextEmbedding.query_embed(text)`（BGE 的 query 指令前缀由 fastembed 内部处理） |

### Python 端：`backend/src/aipy/build_knowledge_base.py`（建库脚本）

| 块 | 位置 | 作用 |
|---|---|---|
| import config | L9 | 从 `aipy.config` 读路径/模型名/集合名（不再自己算 `parents[2]`） |
| import embeddings | L10 | `from aipy.embeddings import FastEmbedEmbeddings`（替代原 `langchain_ollama.OllamaEmbeddings`） |
| 切块器 | L15-18 | `MarkdownHeaderTextSplitter` 按 `##` 切，`strip_headers=False` 保留标题（保证 chunk 自包含） |
| 读文件切块 | L20-27 | `WIKI_DATA_DIR.glob("*.md")`，每块打 `source` metadata（来源文件名） |
| 删旧库 | L32-33 | `shutil.rmtree(CHROMA_DIR)` 全量重建，防重复追加 |
| 向量化入库 | L35-41 | `FastEmbedEmbeddings(model_name=EMBEDDING_MODEL)` 编码，`Chroma.from_documents(collection_name=COLLECTION_NAME)` 持久化 |
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
- [x] 游戏内输入框自研方案（2026-10-14，v0.9）：F7 中文输入框落地——SMAPI 4 无 TextInput 事件，直连 MonoGame `GameWindow.TextInput`（见踩坑 23）；F7 测试问题池（TestQuestions/_questionIndex）已删除。
- [ ] 加防连点：思考中重复提问应忽略或排队，别刷一堆请求（F7 和聊天框两个入口都要防）。
- [x] 扩展状态感知第二批 a（2026-10-14）：CurrentTool / FarmingLevel / MiningLevel / DaysPlayed 全链路（C# GameStateData → Python ChatRequest（带默认值向后兼容）→ chat_node 状态块加工具行（空手不显示，英文名→中文映射）/ 技能等级行 / 存档天数行）。
- [x] 扩展状态感知第二批 b（2026-10-15）：Inventory / NPCAffection / CurrentQuest 全链路。C#：背包取 `Items` 前10个非空物品 `"名称 x数量"`；好感度遍历 `friendshipData.Pairs` 取好感>0 前8个降序，心数=`Points/250`；任务取 `questLog` 第一个 `!completed.Value` 的 `"标题 - 当前目标"`。Python：ChatRequest 三字段默认 `""` 向后兼容；retrieve_node 把季节/天气/地点拼成 `[春季 晴天 Farm]` 前缀进检索 query；chat_node 状态块加背包/NPC好感/当前任务三行，空值不显示。踩坑：`Quest.completed` 是 `NetBool` 不是 `bool`，必须用 `.Value`。
- [x] 多轮对话记忆（2026-10-12）：LangGraph 对话图（retrieve → chat）+ Checkpointer，`thread_id=玩家名` 隔离历史；检索/拼提示词/调 LLM 下沉到图节点，main.py 只剩调度。
- [x] 记忆持久化（2026-10-13）：MemorySaver 换 AsyncSqliteSaver，历史落盘 `backend/data/checkpoints.db`，重启后端不丢（同步版 SqliteSaver 不支持 async，见踩坑 19）。
- [x] 历史长度上限（2026-10-13）：chat_node 只把最近 20 条（`[-21:-1]`，约 10 轮）塞给 LLM，更早历史仍完整留在 SQLite；token 成本可控且不丢数据。
- [x] 游戏内配置界面（GMCM，2026-10-14）：C# 端 `ModConfig.cs` + `RegisterGmcmConfig`（GameLaunched 事件），Python 端 `POST /config` + `save_config` + `PlaceholderLLM`；配置改完无需重启后端，对话历史保留。
- [x] 模型参数可配置（2026-10-14）：temperature 全链路打通——C# `ModConfig.Temperature` + GMCM `AddNumberOption`（0.0-2.0 步长 0.1）→ `POST /config`（校验 0.0-2.0）→ `save_config` 落盘 → `create_llm` 读 cfg 重建 LLM 实时生效；检索 top-k 已挪进 `aipy/config.py`（2026-10-12）。
- [ ] 玩家档案 RAG：目前知识库只有游戏通识，还没有玩家个人档案检索。
- [ ] F7 / `AI ` 前缀写进配置可改，避免和别的 Mod 撞键（已调研：GMCM 有 `AddKeybind`/`AddKeybindList` API，见 libs 接口文件 L103/L112，ModConfig 字段用 `KeybindList` 类型即可，SMAPI 原生支持序列化）。
- [ ] emotion 现在固定 happy 只打日志（失败时返回 error），后面接表情/立绘显示。
- [ ] 气泡对话 UI：AI 回复以气泡形式从机器人贴图头上冒出（现为聊天框 / HUD 文字）。
- [ ] 主动提醒：不对话时 AI 偶尔主动提示玩家（触发机制待定，如体力低 / 节日 / 任务到期）。
- [ ] 知识库扩充：把星露谷 wiki 内容总结成知识库文档，减少对 wiki 的实时请求。
- [x] 嵌入改本地 ONNX（2026-10-15）：`OllamaEmbeddings`(bge-m3, 1024 维) → `FastEmbedEmbeddings`(`BAAI/bge-small-zh-v1.5`, 512 维, int8 ~23MB)；新增 `aipy/embeddings.py` 封装，`config.py` 换模型常量并移除 `OLLAMA_BASE_URL`，`chroma_db/` 全量重建；经 5 组问题验证检索质量（鱼类/礼物命中准确，季节类问题受 KB 结构限制）。
- [x] 后端生命周期打通（2026-10-15）：Python 端 `GET /health`（不触发检索/LLM，返回 status/llm_configured/provider；`_config` 兜底空 dict 防 NameError）；C# 端 `CheckBackendHealthAsync`（3 秒超时）→ `EnsureBackendReadyAsync`（可达提示 / 不可达按 GMCM 配置自动拉起）→ `StartBackendAsync`（uv 拉起 + 双流写 backend.log + 轮询 30 秒）→ 退出钩子 `AppDomain.ProcessExit` Kill 进程树（SMAPI 4 无 GameLoop.Exiting，见踩坑 26）；GMCM 新增 BackendDir / AutoStartBackend 两配置项。
- [x] 后端打包准备就绪（2026-10-15）：`main.py` 末尾内置 `__main__` 入口（`uvicorn.run(app, "127.0.0.1", 8000)`，**必须放在所有路由注册之后**，放中间会被 run 阻塞导致后面的路由注册不到——实测踩过）；嵌入模型缓存固定 `backend/models/`（离线优先，实测 `HF_HUB_OFFLINE=1` 可出 512 维向量）；`chroma_db` 缺失时 `_NullRetriever` 兜底不崩（AI 回答"不清楚"）。
- [x] PyInstaller 打包第一阶段（2026-10-15）：`backend.spec`（onedir，数据文件不打进 exe）→ `dist/backend/backend.exe`；exe 同级放 chroma_db/models/config.json = 发布形态，实测 /health 200 + /chat 检索问答全链路通过（chromadb 全组件动态 import 需 `collect_submodules`，见踩坑 27）；打包命令 `uv run pyinstaller backend.spec --noconfirm`
- [x] C# 双模式启动（2026-10-16）：`StartBackendAsync` 自动判断——`BackendDir` 下有 `backend.exe` 走打包模式（玩家零依赖），否则回退 `uv run` 开发模式；`BackendDir` 留空自动推断为 Mod 目录下 `backend/`（删掉上游空值拦截死代码，见踩坑 32）；并发调用幂等（writer 提为字段 `_backendLogWriter` + 进程存活检查，见踩坑 31）；目录下既无 exe 又无 pyproject.toml 时提示"未找到后端"。
- [x] 发布包组装（2026-10-16）：`tools/build_release.py` 一键产出 `release/StardewAIAssistant-v0.9.zip`（约 149MB / 275 文件：Mod DLL + assets + manifest + PyInstaller onedir 产物 + chroma_db/models/wiki_data/config.example.json；自动剔除 config.json/data/backend.log）；`PLAYER-README.md` 玩家向安装说明（打进包内改名 README.md）；项目根 .gitignore 加 `release/`。
- [ ] 面向玩家打包收尾（当前主线）：发布包纯净环境实测（解压即用）+ PLAYER-README 占位链接填写 + 体积优化。

## 五、环境备忘

- Python 后端目录：`D:\StardewValley_AI_Companion\backend`（与本 Mod 同一个根仓库），入口 `main.py`，FastAPI + uvicorn，uv 管理依赖，Python >=3.11。
- 嵌入模型：fastembed `BAAI/bge-small-zh-v1.5`（ONNX int8），随 Python 依赖自带、无需 Ollama；模型缓存固定在 `backend/models/`（gitignore，打包随包分发；开发模式首次下载到该目录，国内先设 `HF_ENDPOINT=https://hf-mirror.com`）。Ollama 只在"本地对话模式"下需要（`qwen2.5:3b`），云端模式不需要本地对话模型。
- LLM 配置：真实配置文件是 `backend/config.json`（2026-10-15 起与模板同在 backend/ 根，旧"必须在 src/aipy/ 下"的规则已废除，见踩坑 17），模板 `backend/config.example.json` 入库、真实 config.json 已 gitignore。provider 可选 ollama/deepseek/zhipu/qwen/kimi；云端四家 base_url 写死在 `src/aipy/llm_factory.py` 的 PROVIDERS 表。**v0.8 起后端无配置也能启动**（PlaceholderLLM 占位），收到 GMCM 推送后自动切换为真实模型，无需重启；temperature 随 /config 一起推送并落盘（0.0-2.0，默认 0.7）。
- 游戏内配置（GMCM）：玩家 ESC → Mod 选项 → 填厂商/Key/模型/回答温度 → 保存时 C# POST `/config` → 后端写 `config.json` + 重建 LLM 注入对话图；后端未启动时配置只存本地游戏侧 `config.json`（SMAPI 默认文件名，不是 ModConfig.json），启动后需重新保存。GMCM 接口文件必须用 `shared/` 版本（见踩坑 20）。
- 建知识库：在 `backend/` 下执行 `uv run python src/aipy/build_knowledge_base.py`（注意脚本在 `src/aipy/` 下，不是 scripts/）；输入 `wiki_data/*.md`，输出本地 `chroma_db/`（已 gitignore，换机要重建）。
- 启动：三种方式——① 手动：在 `D:\StardewValley_AI_Companion\backend` 下执行 `uv run uvicorn main:app --port 8000`（调试热重载可加 `--reload`；切记是 `uv run`，`uv uvicorn` 会报错）或等价 `uv run python main.py`；② 自动：GMCM 开 AutoStartBackend（BackendDir 可留空，自动推断为 Mod 目录下 `backend/`），进游戏 C# 双模式拉起——目录下有 `backend.exe` 走打包模式直跑 exe，否则走 `uv run` 开发模式（日志落各自目录的 `backend.log`，游戏退出时自动关闭）；③ 打包 exe：`uv run pyinstaller backend.spec --noconfirm` 后双击 `dist/backend/backend.exe`（数据文件放 exe 同级，见踩坑 27 附带）。
- 接口：`POST http://127.0.0.1:8000/chat`。入参字段故意写成 PascalCase 对齐 C# 序列化输出，共 17 个：PlayerName/Stamina/Health/GameTime/DayOfMonth/Season/IsRaining/Location/Money/CurrentTool/FarmingLevel/MiningLevel/DaysPlayed/Inventory/NPCAffection/CurrentQuest/Message；返回小写 `{"reply":..., "emotion":...}`。另有 `GET /health`（启动检测用，零开销：不检索不调 LLM，返回 `{"status":"ok","llm_configured":bool,"provider":str}`，注意 provider 在运行中切换配置后可能滞后）与 `POST /config`。改字段两头要一起改，且改完 C# 别忘了重新 build。
- 多轮记忆：LangGraph `AsyncSqliteSaver`（SQLite 落盘 `backend/data/checkpoints.db`），`thread_id` 用玩家名隔离；重启后端历史不丢，删该文件即清空（见核心机制备忘第 7 条、踩坑 19）。
- Harmony：dll 在 `D:\Steam\steamapps\common\Stardew Valley\smapi-internal\0Harmony.dll`，csproj 显式引用 + `Private=false`；Entry 里 `new Harmony(ModManifest.UniqueID).PatchAll()` 安装。
- 编译命令：在 `ModTemplate` 目录下 `dotnet build`。
- 只验证语法不部署：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`。
- DLL 输出：`d:\StardewValley_AI_Companion\mod\ModTemplate\bin\Debug\net6.0\StardewAIAssistant.dll`。
- 游戏路径：`D:\Steam\steamapps\common\Stardew Valley`。
- Mods 目录：`D:\Steam\steamapps\common\Stardew Valley\Mods\StardewAIAssistant`（build 后自动复制，游戏开着会失败）。
- 版本：项目 net6.0；游戏 1.6.15；SMAPI MinimumApiVersion 4.0.0；NuGet `Pathoschild.Stardew.ModBuildConfig` 4.1.0。
- manifest 关键项：UniqueID `yz.StardewAIAssistant`，EntryDll `StardewAIAssistant.dll`。
