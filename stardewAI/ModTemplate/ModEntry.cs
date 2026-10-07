// ============================================================
// 需要在文件顶部引入的命名空间：
//   using System;                  // TimeSpan、Exception、Action 等基础类型
//   using System.Collections.Generic; // Queue<T> 主线程任务队列
//   using System.Net.Http;         // HttpClient / StringContent / HttpRequestException
//   using System.Text;             // Encoding.UTF8
//   using System.Text.Json;        // JsonSerializer / JsonException
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

        public override void Entry(IModHelper helper)
        {
            // 1. 保留原有 F7 按键监听（事件处理器用 async void 是 C# 的标准写法）
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 2. 注册每帧 Tick 事件：在游戏主线程上执行排队任务（用于安全显示 HUD）
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
                Message = question                      // 【新增】本次轮到的测试问题，随 JSON 的 Message 字段一起发给后端
            };
            this.Monitor.Log(
                $"[AI伴侣] 收集状态：玩家={state.PlayerName}，体力={state.Stamina}，时间={state.GameTime}，地点={state.Location}",
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

        /// <summary>
        /// 发送给 Python 后端的游戏状态数据结构。
        /// 序列化后字段名为：PlayerName / Stamina / GameTime / Location / Message。
        /// </summary>
        private sealed class GameStateData
        {
            public string PlayerName { get; set; } = string.Empty;
            public float Stamina { get; set; }
            public int GameTime { get; set; }
            public string Location { get; set; } = string.Empty;

            /// <summary>【新增】玩家本次问的问题（取自测试问题池）。</summary>
            public string Message { get; set; } = string.Empty;
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
