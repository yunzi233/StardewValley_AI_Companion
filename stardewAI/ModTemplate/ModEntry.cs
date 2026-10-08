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
using HarmonyLib;
using Microsoft.Xna.Framework;
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

        public override void Entry(IModHelper helper)
        {
            // 0. 保存单例，供 Harmony 静态补丁使用
            Instance = this;

            // 0.1 安装 Harmony 补丁。
            //     0Harmony.dll 由 SMAPI 4 自带（位于游戏目录 smapi-internal/ 下），
            //     不需要额外安装 NuGet 包；用 Mod 的 UniqueID 作为补丁域 ID 即可。
            //     PatchAll 会自动扫描本程序集里所有带 [HarmonyPatch] 特性的类
            //     （本项目即 ChatBoxTextBoxEnterPatch）。
            new Harmony(this.ModManifest.UniqueID).PatchAll();

            // 1. 保留原有 F7 按键监听（事件处理器用 async void 是 C# 的标准写法）
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 2. 注册每帧 Tick 事件：在游戏主线程上执行排队任务（用于安全显示 HUD/聊天消息）
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
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

            // ========== 第 1 步：在游戏主线程收集当前游戏状态 ==========
            // （此方法在 await 之前的部分都运行在游戏主线程，可以安全读取游戏数据）
            GameStateData state = new()
            {
                PlayerName = Game1.player.Name,          // 玩家名字
                Stamina = Game1.player.Stamina,         // 当前体力（float 类型）
                GameTime = Game1.timeOfDay,             // 当前游戏时间（int，如 600 表示早上 6:00）
                Location = Game1.currentLocation.Name,  // 当前地点（如 "Farm"；如需区分同名室内建筑可改用 NameOrUniqueName）
                Message = question,                     // 【新增】本次轮到的测试问题，随 JSON 的 Message 字段一起发给后端
                // ========== 【扩展状态感知】以下是新增字段 ==========
                Season = Game1.currentSeason,           // 当前季节："spring"/"summer"/"fall"/"winter"
                IsRaining = Game1.isRaining,            // 今天是否下雨（true/false）
                DayOfMonth = Game1.dayOfMonth,          // 今天是当月第几天（1-28，星露谷每月只有 28 天）
                Money = Game1.player.Money,              // 玩家当前金币（int）
                Health = Game1.player.health             // 玩家当前血量（int）
            };
            this.Monitor.Log(
                $"[AI伴侣] 收集状态：玩家={state.PlayerName}，体力={state.Stamina}，时间={state.GameTime}，地点={state.Location}，" +
                $"季节={state.Season}，下雨={state.IsRaining}，日期={state.DayOfMonth}号，金币={state.Money}，血量={state.Health}",
                LogLevel.Info);

            // ========== 第 2 步：按下 F7 的瞬间立刻弹出“思考中”提示（零延迟反馈） ==========
            Game1.addHUDMessage(new HUDMessage("AI伴侣正在思考中..."));

            // ========== 第 3 步：异步 POST 到 Python 后端（绝不卡游戏主线程） ==========
            try
            {
                // 把状态对象序列化为 JSON。
                // System.Text.Json 默认保持属性原名，因此输出：
                // {"PlayerName":"...","Stamina":100.0,"GameTime":600,"Location":"Farm","Message":"..."}
                string requestJson = JsonSerializer.Serialize(state);

                // 构造请求体：JSON 字符串 + UTF-8 编码 + application/json 媒体类型
                using StringContent content = new(requestJson, Encoding.UTF8, "application/json");

                // await 期间游戏照常渲染、运行，玩家不会感到卡顿
                using HttpResponseMessage response = await HttpClient.PostAsync(BackendUrl, content);

                // 如果 HTTP 状态码不是 2xx（如 404 / 500），主动抛出 HttpRequestException
                response.EnsureSuccessStatusCode();

                // 读取后端返回的 JSON 文本，形如：{"reply": "xxx", "emotion": "happy"}
                string responseJson = await response.Content.ReadAsStringAsync();
                this.Monitor.Log($"[AI伴侣] 后端返回内容：{responseJson}", LogLevel.Debug);

                // ========== 第 4 步：解析 JSON 并提取 reply 字段 ==========
                ChatResponse? result = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
                string? reply = result?.Reply;

                // reply 为空时给出提示，而不是显示空白消息
                if (string.IsNullOrWhiteSpace(reply))
                {
                    EnqueueHudMessage("AI伴侣：（后端返回了空内容）");
                    return;
                }

                // emotion 字段只写入 SMAPI 控制台日志（需求只要求在游戏里显示 reply）
                if (!string.IsNullOrEmpty(result?.Emotion))
                    this.Monitor.Log($"[AI伴侣] 当前情绪：{result.Emotion}", LogLevel.Debug);

                // 回到游戏主线程显示 AI 回复
                EnqueueHudMessage($"AI伴侣：{reply}");
            }
            catch (HttpRequestException)
            {
                // 连接不到后端：Python 没启动、端口不对、防火墙拦截等都会走到这里
                this.Monitor.Log("[AI伴侣] 无法连接 Python 后端：http://127.0.0.1:8000", LogLevel.Error);
                EnqueueHudMessage("AI大脑连接失败，请检查Python服务是否启动", isError: true);
            }
            catch (OperationCanceledException)
            {
                // 请求超过上面设置的 15 秒 Timeout 会抛出此异常
                EnqueueHudMessage("AI大脑响应超时，请稍后再试", isError: true);
            }
            catch (JsonException ex)
            {
                // 后端返回的内容不是合法 JSON，或字段结构对不上
                this.Monitor.Log($"[AI伴侣] 解析后端 JSON 失败：{ex.Message}", LogLevel.Error);
                EnqueueHudMessage("AI伴侣返回的数据格式有误", isError: true);
            }
            catch (Exception ex)
            {
                // 兜底异常处理：任何意外错误都记录日志并提示，绝不让游戏崩溃
                this.Monitor.Log($"[AI伴侣] 发生未知错误：{ex}", LogLevel.Error);
                EnqueueHudMessage("AI伴侣出现未知错误，请查看 SMAPI 控制台", isError: true);
            }
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
        /// 处理玩家在聊天框里输入的 "AI xxx" 问题。
        /// 由 Harmony Prefix 在游戏主线程调用，内部异步请求后端、不阻塞游戏。
        /// </summary>
        /// <param name="question">去掉 "AI " 前缀后的问题原文</param>
        public async void HandleAiChatQuestion(string question)
        {
            // 防御性检查（与 F7 一致）：主菜单等状态下不处理
            if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
            {
                EnqueueChatMessage("AI伴侣：请先进入存档后再呼叫我～", ChatErrorColor);
                return;
            }

            // ========== 在游戏主线程收集当前游戏状态（字段与 F7 完全一致） ==========
            GameStateData state = new()
            {
                PlayerName = Game1.player.Name,
                Stamina = Game1.player.Stamina,
                GameTime = Game1.timeOfDay,
                Location = Game1.currentLocation.Name,
                Message = question,                     // 本次的问题来自聊天框，而不是测试问题池
                Season = Game1.currentSeason,
                IsRaining = Game1.isRaining,
                DayOfMonth = Game1.dayOfMonth,
                Money = Game1.player.Money,
                Health = Game1.player.health
            };
            this.Monitor.Log(
                $"[AI伴侣] 聊天框提问：玩家={state.PlayerName}，体力={state.Stamina}，时间={state.GameTime}，地点={state.Location}，" +
                $"季节={state.Season}，下雨={state.IsRaining}，日期={state.DayOfMonth}号，金币={state.Money}，血量={state.Health}\n" +
                $"[AI伴侣] 问题内容：{question}",
                LogLevel.Info);

            // 在聊天框里给一个即时反馈（本地 3B 模型首次生成可能要等十几秒）
            EnqueueChatMessage("AI伴侣正在思考中...", ChatReplyColor);

            // ========== 异步 POST 到 Python 后端（与 F7 共用同一个 HttpClient） ==========
            try
            {
                string requestJson = JsonSerializer.Serialize(state);
                using StringContent content = new(requestJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await HttpClient.PostAsync(BackendUrl, content);
                response.EnsureSuccessStatusCode();

                string responseJson = await response.Content.ReadAsStringAsync();
                this.Monitor.Log($"[AI伴侣] 后端返回内容：{responseJson}", LogLevel.Debug);

                ChatResponse? result = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
                string? reply = result?.Reply;

                if (string.IsNullOrWhiteSpace(reply))
                {
                    EnqueueChatMessage("AI伴侣：（后端返回了空内容）", ChatErrorColor);
                    return;
                }

                if (!string.IsNullOrEmpty(result?.Emotion))
                    this.Monitor.Log($"[AI伴侣] 当前情绪：{result.Emotion}", LogLevel.Debug);

                // 回复显示在原版聊天框里（带历史，玩家可以向上翻看）
                EnqueueChatMessage($"AI伴侣: {reply}", ChatReplyColor);
            }
            catch (HttpRequestException)
            {
                this.Monitor.Log("[AI伴侣] 无法连接 Python 后端：http://127.0.0.1:8000", LogLevel.Error);
                EnqueueChatMessage("AI大脑连接失败，请检查Python服务是否启动", ChatErrorColor);
            }
            catch (OperationCanceledException)
            {
                EnqueueChatMessage("AI大脑响应超时，请稍后再试", ChatErrorColor);
            }
            catch (JsonException ex)
            {
                this.Monitor.Log($"[AI伴侣] 解析后端 JSON 失败：{ex.Message}", LogLevel.Error);
                EnqueueChatMessage("AI伴侣返回的数据格式有误", ChatErrorColor);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"[AI伴侣] 发生未知错误：{ex}", LogLevel.Error);
                EnqueueChatMessage("AI伴侣出现未知错误，请查看 SMAPI 控制台", ChatErrorColor);
            }
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
