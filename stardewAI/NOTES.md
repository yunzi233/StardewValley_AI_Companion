# NOTES — StardewAIAssistant 开发笔记

> 给自己回顾用。只记坑和要点，不抄教程。
> 最后更新：2026-10-04

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

文件：`ModTemplate/ModEntry.cs`

| 方法/类 | 位置 | 作用 |
|---|---|---|
| `ModEntry`（类） | L30 | Mod 入口，继承 SMAPI 的 `Mod` |
| `Entry` | L62 | 启动时跑一次，订阅 ButtonPressed + UpdateTicked |
| `OnButtonPressed` | L74 | F7 主流程：安检→收集状态→弹"思考中"→异步 POST→解析→投递 HUD |
| `OnUpdateTicked` | L171 | 主线程每帧执行，消费 `_mainThreadActions` 队列 |
| `EnqueueHudMessage` | L199 | 把显示 HUD 的动作打包入队（可在后台线程调） |
| `GameStateData`（内部类） | L218 | 发给后端的数据模型：PlayerName/Stamina/GameTime/Location |
| `ChatResponse`（内部类） | L230 | 后端返回模型：Reply/Emotion |

关键字段：`BackendUrl`(L33)、`HttpClient`(L39)、`JsonOptions`(L49)、`_mainThreadActions`(L60)。

## 四、待办 / 下一步

- [x] Python 后端建立并端到端跑通（2026-10-04）：游戏按 F7 → 后端收到体力/时间 → HUD 显示 reply。
- [ ] 现在 reply 是写死的模板分支（体力<50 提醒睡觉），**还没接 LLM**，下一步接真实大模型。
- [ ] 加防连点：思考中再按 F7 应忽略或排队，别刷一堆请求。
- [ ] 扩充游戏状态感知：季节、天气、背包、金币、当日任务、NPC 好感等。
- [ ] 多轮对话记忆：现在每请求都是独立的，没带历史。
- [ ] 上 RAG：把游戏知识/玩家档案做检索，少占上下文。
- [ ] F7 写进 config 可改键，避免和别的 Mod 撞键。
- [ ] emotion 现在只打日志，后面接表情/立绘显示。

## 五、环境备忘

- Python 后端项目：`D:\AI_Projects\AIpy`（另一个仓库，不在本目录），入口 `main.py`，FastAPI + uvicorn，uv 管理依赖，Python >=3.11。
- 启动：在 `D:\AI_Projects\AIpy` 下执行 `uv run uvicorn main:app --port 8000`（调试热重载可加 `--reload`）。
- 接口：`POST http://127.0.0.1:8000/chat`。入参字段故意写成 PascalCase（PlayerName/Stamina/GameTime/Location）对齐 C# 序列化输出；返回小写 `{"reply":..., "emotion":...}`。改字段两头要一起改。
- 编译命令：在 `ModTemplate` 目录下 `dotnet build`。
- 只验证语法不部署：`dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`。
- DLL 输出：`d:\AI_Projects\stardewAI\ModTemplate\bin\Debug\net6.0\StardewAIAssistant.dll`。
- 游戏路径：`D:\Steam\steamapps\common\Stardew Valley`。
- Mods 目录：`D:\Steam\steamapps\common\Stardew Valley\Mods\StardewAIAssistant`（build 后自动复制，游戏开着会失败）。
- 版本：项目 net6.0；游戏 1.6.15；SMAPI MinimumApiVersion 4.0.0；NuGet `Pathoschild.Stardew.ModBuildConfig` 4.1.0。
- manifest 关键项：UniqueID `yz.StardewAIAssistant`，EntryDll `StardewAIAssistant.dll`。
