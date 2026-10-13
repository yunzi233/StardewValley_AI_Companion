// ============================================================
// 需要在文件顶部引入的命名空间：
//   using System;                  // TimeSpan、Exception、Action 等基础类型
//   using System.Collections.Generic; // Queue<T> 主线程任务队列
//   using System.Net.Http;         // HttpClient / StringContent / HttpRequestException
//   using System.Text;             // Encoding.UTF8
//   using System.Text.Json;        // JsonSerializer / JsonException
//   using Microsoft.Xna.Framework; // Color（聊天框消息颜色 LightSkyBlue）
//   using HarmonyLib;              // Harmony 补丁入口（0Harmony.dll 由 SMAPI 4 自带）
//   using StardewModdingAPI;       // Mod / IModHelper / Context
//   using StardewModdingAPI.Events;// 各类事件参数（ButtonPressed / UpdateTicked）
//   using StardewValley;           // Game1 / HUDMessage
// 说明：游戏 1.6.15 的 HUDMessage 不再提供 color 字段，文字颜色由游戏统一控制，
//       错误提示通过 whatType = HUDMessage.error_type 显示官方错误图标。
// ============================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using GenericModConfigMenu;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewAIAssistant
{
    /// <summary>
    /// AI 伴侣 Mod 入口类。
    /// 流程：按 F7 → 收集游戏状态 → 立刻弹出“思考中”提示
    ///       → 异步 POST 给本地 Python 后端 → 解析 reply 并在游戏内显示。
    /// </summary>
    public class ModEntry : Mod
    {
        /// <summary>本地 Python 后端接口地址。</summary>
        private const string BackendUrl = "http://127.0.0.1:8000/chat";

        /// <summary>
        /// 全局唯一的 HttpClient（官方推荐做法：整个 Mod 生命周期复用同一个实例，
        /// 避免每次请求都 new 一个导致 TCP 套接字耗尽）。
        /// </summary>
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15) // 15 秒超时，防止后端无响应时请求一直挂着
        };

        /// <summary>
        /// JSON 反序列化选项。
        /// Python 返回的是小写键名（reply/emotion），开启“大小写不敏感”后
        /// 才能正确映射到 C# 里的 PascalCase 属性。
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 主线程任务队列。
        /// 重要：await 之后的代码可能运行在线程池后台线程上，而 HUD 等游戏 UI
        /// 操作必须在游戏主线程执行，否则会有随机崩溃风险。
        /// 后台线程把“显示 HUD”的动作塞进队列，由 UpdateTicked 事件在主线程取出执行。
        /// </summary>
        private readonly Queue<Action> _mainThreadActions = new();

        /// <summary>
        /// 【新增】预设的测试问题池。每次按 F7 轮流取一个发给 Python 后端。
        /// </summary>
        private static readonly string[] TestQuestions =
        {
            "夏季晴天能钓什么鱼？",
            "海莉喜欢什么礼物？",
            "春季种什么最赚钱？",
            "亚历克斯的生日是什么时候？",
            "太阳鱼在什么地方钓？"
        };

        /// <summary>
        /// 【新增】当前轮到问题池里的第几个问题。
        /// 按下 F7 后取余自增，实现 0→1→...→4→0 循环。
        /// </summary>
        private int _questionIndex = 0;

        /// <summary>
        /// 全局单例引用。
        /// Harmony 的 Prefix 补丁方法必须是 static，没法直接拿到 Mod 实例，
        /// 所以在 Entry 里把 this 存到这里，补丁拦截到 "AI " 消息后通过它回调实例方法。
        /// </summary>
        internal static ModEntry Instance { get; private set; } = null!;

        /// <summary>玩家通过 GMCM 或 ModConfig.json 配置的 AI 厂商参数。</summary>
        private ModConfig _config = null!;

        public override void Entry(IModHelper helper)
        {
            // 0. 保存单例，供 Harmony 静态补丁使用
            Instance = this;

            // 0.1 读取本地配置（SMAPI 会自动生成 ModConfig.json，首次启动用默认值）
            _config = helper.ReadConfig<ModConfig>();

            // 0.2 安装 Harmony 补丁。
            //     0Harmony.dll 由 SMAPI 4 自带（位于游戏目录 smapi-internal/ 下），
            //     不需要额外安装 NuGet 包；用 Mod 的 UniqueID 作为补丁域 ID 即可。
            //     PatchAll 会自动扫描本程序集里所有带 [HarmonyPatch] 特性的类
            //     （本项目即 ChatBoxTextBoxEnterPatch）。
            new Harmony(this.ModManifest.UniqueID).PatchAll();

            // 1. 保留原有 F7 按键监听（事件处理器用 async void 是 C# 的标准写法）
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 2. 注册每帧 Tick 事件：在游戏主线程上执行排队任务（用于安全显示 HUD/聊天消息）
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // 3. 订阅 GameLaunched 事件：等所有 Mod 初始化完后再调 GMCM 的 API
            //    原因：Entry 执行时其他 Mod 可能还没加载完，此时 GetApi 会返回 null
            //    并触发 SMAPI 警告 "Tried to access a mod-provided API before all mods were initialized."
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        }

        /// <summary>
        /// GameLaunched 事件处理：等所有 Mod 初始化完后再注册 GMCM 配置界面。
        /// </summary>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterGmcmConfig();
        }

        /// <summary>
        /// 注册 GenericModConfigMenu 配置界面。
        /// 注意：不能在 Entry 里调 GetApi——Entry 执行时其他 Mod 可能还没初始化完，
        /// 此时 GetApi 会返回 null 并触发 SMAPI 警告；必须等 GameLaunched 事件。
        /// </summary>
        private void RegisterGmcmConfig()
        {
            try
            {
                var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
                if (gmcm is null)
                {
                    Monitor.Log("未检测到 GenericModConfigMenu，跳过游戏内配置界面注册；请手动编辑 Mods/StardewAIAssistant/ModConfig.json", LogLevel.Info);
                    return;
                }

                // 注册配置界面：reset 恢复默认，save 写回 JSON 并推送到后端
                gmcm.Register(
                    this.ModManifest,
                    reset: () => _config = new ModConfig(),
                    save: () =>
                    {
                        Helper.WriteConfig(_config);
                        _ = PushConfigToBackendAsync(_config);
                    }
                );

                gmcm.AddTextOption(
                    this.ModManifest,
                    getValue: () => _config.Provider,
                    setValue: v => _config.Provider = v,
                    name: () => "AI 厂商",
                    tooltip: () => "deepseek / qwen / zhipu / kimi / ollama",
                    allowedValues: new[] { "deepseek", "qwen", "zhipu", "kimi", "ollama" }
                );

                gmcm.AddTextOption(
                    this.ModManifest,
                    getValue: () => _config.ApiKey,
                    setValue: v => _config.ApiKey = v,
                    name: () => "API Key",
                    tooltip: () => "云端厂商必填；本地 Ollama 留空"
                );

                gmcm.AddTextOption(
                    this.ModManifest,
                    getValue: () => _config.Model,
                    setValue: v => _config.Model = v,
                    name: () => "模型名",
                    tooltip: () => "如 deepseek-chat / qwen-plus / glm-4-flash / qwen2.5:3b"
                );

                Monitor.Log("已注册 GenericModConfigMenu 配置界面", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // GMCM 注册失败不崩溃 Mod，只打日志；玩家仍可手动编辑 ModConfig.json
                Monitor.Log($"注册 GMCM 配置界面失败：{ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// F7 按键处理：收集状态、弹“思考中”提示、异步请求 Python 后端。
        /// </summary>
        private async void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // 只响应 F7 键，其他按键直接忽略
            if (e.Button != SButton.F7)
                return;

            // 防御性检查：存档没加载好时（例如停在主菜单），Game1.player 可能为 null，
            // 此时直接提示并返回，避免空引用异常导致游戏崩溃。
            if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
            {
                Game1.addHUDMessage(new HUDMessage("AI伴侣：请先进入存档后再呼叫我～"));
                return;
            }

            // ========== 【新增】从测试问题池轮流取一个问题 ==========
            // 先按当前索引取出问题，再让索引 +1 并对数组长度取余，
            // 这样问到最后一个后会自动回到第 0 个，循环使用。
            string question = TestQuestions[_questionIndex];
            _questionIndex = (_questionIndex + 1) % TestQuestions.Length;

            // 按下 F7 先在 HUD 里显示本次问的内容，方便调试确认轮到了哪个问题
            Game1.addHUDMessage(new HUDMessage($"玩家问：{question}"));

            // 走共用核心流程：状态收集 → “思考中” → POST → 解析 → 显示回复 → 异常处理
            // useChatBox=false 表示回复显示在 HUD（F7 入口）；
            // 状态收集、思考中提示、POST、解析、4 个 catch 全在 SendToBackendAsync 里，
            // 改字段/改提示/改超时只需改一处。
            await SendToBackendAsync(question, useChatBox: false);
        }

        /// <summary>
        /// 每一帧在游戏主线程触发：取出后台线程排队的任务并执行。
        /// </summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            while (true)
            {
                Action action;

                // 加锁保护：后台线程可能同时往队列里塞任务
                lock (_mainThreadActions)
                {
                    if (_mainThreadActions.Count == 0)
                        return; // 队列空了，本帧处理完毕

                    action = _mainThreadActions.Dequeue();
                }

                // 在锁外执行，避免 HUD 逻辑反过来阻塞后台线程入队
                action.Invoke();
            }
        }

        /// <summary>
        /// 把一条 HUD 消息排队到游戏主线程执行（本方法可在任意线程安全调用）。
        /// </summary>
        /// <param name="text">要显示的文字</param>
        /// <param name="isError">
        /// 是否为错误消息：true 时使用 HUDMessage.error_type 类型，
        /// 游戏会显示官方的错误图标（文字颜色在 1.6 中由游戏统一控制）。
        /// </param>
        private void EnqueueHudMessage(string text, bool isError = false)
        {
            lock (_mainThreadActions)
            {
                // 真正的 Game1.addHUDMessage 调用被包成 Action，延迟到主线程执行
                _mainThreadActions.Enqueue(() =>
                {
                    HUDMessage message = isError
                        ? new HUDMessage(text, HUDMessage.error_type) // 带错误图标的消息
                        : new HUDMessage(text);                       // 普通消息
                    Game1.addHUDMessage(message);
                });
            }
        }

        // ================================================================
        // 聊天框交互（按 T 开原版聊天框，输入 "AI 问题" 回车）
        // 入口是 Harmony 补丁 ChatBoxTextBoxEnterPatch：
        //   玩家按回车 → 原版 ChatBox.textBoxEnter(string) 被 Prefix 拦截
        //   → "AI " 开头则跳过原版发送逻辑，回调到本方法
        // 注意：Prefix 运行在游戏主线程，因此本方法开头到第一个 await 之前
        //       都在主线程上，可以安全收集游戏状态；await 之后与 F7 流程一样
        //       靠 _mainThreadActions 队列回到主线程显示。
        // ================================================================

        /// <summary>聊天框里 AI 回复使用的文字颜色。</summary>
        private static readonly Color ChatReplyColor = Color.LightSkyBlue;

        /// <summary>聊天框里错误提示使用的文字颜色。</summary>
        private static readonly Color ChatErrorColor = Color.OrangeRed;

        /// <summary>
        /// 对话核心流程（F7 与聊天框两个入口共用，消除原先 95% 的重复代码）。
        /// 涵盖：收集状态 → “思考中”提示 → 异步 POST → 解析 → 显示回复 → 4 个 catch。
        /// 安检留在各入口里（提示方式不同：HUD vs 聊天框），本方法假定已通过安检。
        /// </summary>
        /// <param name="question">玩家本次的问题原文</param>
        /// <param name="useChatBox">
        /// true = 聊天框入口（回复/提示显示在原版聊天框，浅蓝色，可翻历史）；
        /// false = F7 入口（回复/提示显示在 HUD）。
        /// </param>
        private async Task SendToBackendAsync(string question, bool useChatBox)
        {
            // ---- 1. 在游戏主线程收集当前游戏状态（10 个字段，两个入口字段一致）----
            // 本方法被 await 调用，调用方（F7/聊天框）在第一个 await 之前都运行在游戏主线程，
            // 因此这里可以安全读取 Game1.player / Game1.currentLocation 等游戏对象。
            GameStateData state = new()
            {
                PlayerName = Game1.player.Name,
                Stamina = Game1.player.Stamina,
                GameTime = Game1.timeOfDay,
                Location = Game1.currentLocation.Name,
                Message = question,                     // 本次问题（F7 来自测试池，聊天框来自玩家输入）
                Season = Game1.currentSeason,
                IsRaining = Game1.isRaining,
                DayOfMonth = Game1.dayOfMonth,
                Money = Game1.player.Money,
                Health = Game1.player.health
            };

            // 入口标记，方便在 SMAPI 日志里区分是 F7 还是聊天框触发的
            string entryTag = useChatBox ? "聊天框" : "F7";
            this.Monitor.Log(
                $"[AI伴侣] [{entryTag}] 收集状态：玩家={state.PlayerName}，体力={state.Stamina}，时间={state.GameTime}，地点={state.Location}，" +
                $"季节={state.Season}，下雨={state.IsRaining}，日期={state.DayOfMonth}号，金币={state.Money}，血量={state.Health}",
                LogLevel.Info);
            this.Monitor.Log($"[AI伴侣] [{entryTag}] 问题内容：{question}", LogLevel.Info);

            // ---- 2. 即时反馈“思考中”（本地 3B 模型首次生成可能要等十几秒，避免玩家以为卡了）----
            if (useChatBox)
                EnqueueChatMessage("AI伴侣正在思考中...", ChatReplyColor);
            else
                EnqueueHudMessage("AI伴侣正在思考中...");

            // ---- 3. 异步 POST 到 Python 后端（复用全局 HttpClient，不卡游戏主线程）----
            try
            {
                string requestJson = JsonSerializer.Serialize(state);
                using StringContent content = new(requestJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await HttpClient.PostAsync(BackendUrl, content);
                response.EnsureSuccessStatusCode();

                string responseJson = await response.Content.ReadAsStringAsync();
                this.Monitor.Log($"[AI伴侣] [{entryTag}] 后端返回内容：{responseJson}", LogLevel.Debug);

                // ---- 4. 解析 JSON 并提取 reply 字段 ----
                ChatResponse? result = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
                string? reply = result?.Reply;

                // reply 为空时给出提示，而不是显示空白消息
                if (string.IsNullOrWhiteSpace(reply))
                {
                    if (useChatBox)
                        EnqueueChatMessage("AI伴侣：（后端返回了空内容）", ChatErrorColor);
                    else
                        EnqueueHudMessage("AI伴侣：（后端返回了空内容）");
                    return;
                }

                // emotion 字段只写入 SMAPI 控制台日志（需求只要求在游戏里显示 reply）
                if (!string.IsNullOrEmpty(result?.Emotion))
                    this.Monitor.Log($"[AI伴侣] [{entryTag}] 当前情绪：{result.Emotion}", LogLevel.Debug);

                // ---- 5. 显示 AI 回复（按入口选择 HUD 或聊天框）----
                if (useChatBox)
                    EnqueueChatMessage($"AI伴侣：{reply}", ChatReplyColor);
                else
                    EnqueueHudMessage($"AI伴侣：{reply}");
            }
            catch (HttpRequestException)
            {
                // 连接不到后端：Python 没启动、端口不对、防火墙拦截等都会走到这里
                this.Monitor.Log($"[AI伴侣] [{entryTag}] 无法连接 Python 后端：{BackendUrl}", LogLevel.Error);
                if (useChatBox)
                    EnqueueChatMessage("AI大脑连接失败，请检查Python服务是否启动", ChatErrorColor);
                else
                    EnqueueHudMessage("AI大脑连接失败，请检查Python服务是否启动", isError: true);
            }
            catch (OperationCanceledException)
            {
                // 请求超过 HttpClient.Timeout（15 秒）会抛出此异常
                this.Monitor.Log($"[AI伴侣] [{entryTag}] 请求超时（>{HttpClient.Timeout.TotalSeconds:0}s）", LogLevel.Error);
                if (useChatBox)
                    EnqueueChatMessage("AI大脑响应超时，请稍后再试", ChatErrorColor);
                else
                    EnqueueHudMessage("AI大脑响应超时，请稍后再试", isError: true);
            }
            catch (JsonException ex)
            {
                // 后端返回的内容不是合法 JSON，或字段结构对不上
                this.Monitor.Log($"[AI伴侣] [{entryTag}] 解析后端 JSON 失败：{ex.Message}", LogLevel.Error);
                if (useChatBox)
                    EnqueueChatMessage("AI伴侣返回的数据格式有误", ChatErrorColor);
                else
                    EnqueueHudMessage("AI伴侣返回的数据格式有误", isError: true);
            }
            catch (Exception ex)
            {
                // 兜底异常处理：任何意外错误都记录日志并提示，绝不让游戏崩溃
                this.Monitor.Log($"[AI伴侣] [{entryTag}] 发生未知错误：{ex}", LogLevel.Error);
                if (useChatBox)
                    EnqueueChatMessage("AI伴侣出现未知错误，请查看 SMAPI 控制台", ChatErrorColor);
                else
                    EnqueueHudMessage("AI伴侣出现未知错误，请查看 SMAPI 控制台", isError: true);
            }
        }

        /// <summary>
        /// 聊天框入口（由 Harmony 补丁 ChatBoxTextBoxEnterPatch 在主线程调用）。
        /// 职责仅限：安检 → 调用共用的 SendToBackendAsync（回复走聊天框通道）。
        /// 安检留在入口里的原因：F7 和聊天框的提示方式不同（HUD vs chatBox.addMessage）。
        /// </summary>
        /// <param name="question">去掉 "AI " 前缀后的问题原文</param>
        public async void HandleAiChatQuestion(string question)
        {
            // 防御性检查：主菜单等状态下不处理
            if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
            {
                EnqueueChatMessage("AI伴侣：请先进入存档后再呼叫我～", ChatErrorColor);
                return;
            }

            // 走共用核心流程：回复显示在原版聊天框（浅蓝色，可翻历史）
            await SendToBackendAsync(question, useChatBox: true);
        }

        /// <summary>
        /// 把一条聊天框消息排队到游戏主线程执行（本方法可在任意线程安全调用）。
        /// ChatBox.addMessage 是游戏 UI 操作，必须在主线程调用，
        /// 因此与 HUD 消息一样走 _mainThreadActions 队列。
        /// </summary>
        /// <param name="text">要显示的文字</param>
        /// <param name="color">文字颜色</param>
        private void EnqueueChatMessage(string text, Color color)
        {
            lock (_mainThreadActions)
            {
                // chatBox 在极早期（如游戏刚启动）可能还没创建，用 ?. 防空引用
                _mainThreadActions.Enqueue(() => Game1.chatBox?.addMessage(text, color));
            }
        }

        /// <summary>
        /// 供 Harmony 补丁调用的聊天框消息入口（补丁是独立类，访问不到 private 方法）。
        /// 目前用于提示 "AI" 命令用法，统一用回复色显示。
        /// </summary>
        internal void EnqueueChatMessageFromPatch(string text)
            => EnqueueChatMessage(text, ChatReplyColor);

        /// <summary>
        /// 把 GMCM 里的配置推送到 Python 后端（POST /config）。
        /// GMCM 的 save 回调是同步 Action，所以这里 fire-and-forget：
        /// 用 _ = 丢弃 Task，方法内部用 try-catch 兜底所有异常。
        /// </summary>
        private async Task PushConfigToBackendAsync(ModConfig cfg)
        {
            try
            {
                // 后端 config.json 的字段名是 snake_case，这里手动映射
                var payload = new
                {
                    provider = cfg.Provider,
                    api_key = cfg.ApiKey,
                    model = cfg.Model
                };

                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 复用全局 HttpClient（15s 超时），地址硬编码为后端 /config
                var response = await HttpClient.PostAsync("http://127.0.0.1:8000/config", content);
                response.EnsureSuccessStatusCode();

                Game1.addHUDMessage(new HUDMessage("AI伴侣：配置已推送到后端"));
                Monitor.Log($"配置已推送：provider={cfg.Provider}, model={cfg.Model}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                // 后端没启动 / 网络错误 / 返回非 2xx 等都走这里
                Game1.addHUDMessage(new HUDMessage("AI伴侣：后端未启动，配置已保存到本地；启动后端后请重新保存"));
                Monitor.Log($"推送配置失败：{ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 发送给 Python 后端的游戏状态数据结构。
        /// 序列化后字段名为：PlayerName / Stamina / GameTime / Location / Message /
        /// Season / IsRaining / DayOfMonth / Money / Health。
        /// </summary>
        private sealed class GameStateData
        {
            public string PlayerName { get; set; } = string.Empty;
            public float Stamina { get; set; }
            public int GameTime { get; set; }
            public string Location { get; set; } = string.Empty;

            /// <summary>玩家本次问的问题（取自测试问题池）。</summary>
            public string Message { get; set; } = string.Empty;

            // ========== 【扩展状态感知】新增字段 ==========
            /// <summary>当前季节："spring"/"summer"/"fall"/"winter"。</summary>
            public string Season { get; set; } = string.Empty;

            /// <summary>今天是否下雨。</summary>
            public bool IsRaining { get; set; }

            /// <summary>今天是当月第几天（1-28，星露谷每月只有 28 天）。</summary>
            public int DayOfMonth { get; set; }

            /// <summary>玩家当前金币。</summary>
            public int Money { get; set; }

            /// <summary>玩家当前血量。</summary>
            public int Health { get; set; }
        }

        /// <summary>
        /// Python 后端返回的数据结构，对应 JSON：{"reply": "xxx", "emotion": "happy"}。
        /// 配合 PropertyNameCaseInsensitive = true，小写键名可自动映射到下列属性。
        /// </summary>
        private sealed class ChatResponse
        {
            public string Reply { get; set; } = string.Empty;
            public string Emotion { get; set; } = string.Empty;
        }
    }
}
