// ============================================================
// 需要在文件顶部引入的命名空间：
//   using System;                  // TimeSpan、Exception、Action 等基础类型
//   using System.Collections.Generic; // Queue<T> 主线程任务队列
//   using System.Net.Http;         // HttpClient / StringContent / HttpRequestException
//   using System.Text;             // Encoding.UTF8
//   using System.Text.Json;        // JsonSerializer / JsonException
//   using System.Text.Json.Serialization; // JsonPropertyName（snake_case 字段映射）
//   using System.Threading;        // CancellationTokenSource（/health 3 秒短超时）
//   using System.Diagnostics;      // Process（自动拉起后端进程）
//   using System.IO;               // Path / Directory / StreamWriter（后端日志落盘）
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
using System.Diagnostics;              // Process（自动拉起 Python 后端）
using System.IO;                       // Path / Directory / StreamWriter（backend.log 落盘）
using System.Linq;                     // 背包/好感度汇总用 LINQ 筛选拼接
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;  // JsonPropertyName（/health 的 snake_case 字段映射）
using System.Threading;                // CancellationTokenSource（/health 3 秒短超时）
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics; // Texture2D（浮动精灵贴图）
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
        /// <summary>本地 Python 后端基础地址（/chat、/health、/config 都从这里派生）。</summary>
        private const string BackendBaseUrl = "http://127.0.0.1:8000";

        /// <summary>对话接口地址（POST，现有功能）。</summary>
        private const string BackendUrl = BackendBaseUrl + "/chat";

        /// <summary>健康检查地址（GET，启动检测用；下一步"自动拉起后端 + 轮询就绪"复用）。</summary>
        private const string HealthUrl = BackendBaseUrl + "/health";

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
        /// 全局单例引用。
        /// Harmony 的 Prefix 补丁方法必须是 static，没法直接拿到 Mod 实例，
        /// 所以在 Entry 里把 this 存到这里，补丁拦截到 "AI " 消息后通过它回调实例方法。
        /// </summary>
        internal static ModEntry Instance { get; private set; } = null!;

        /// <summary>玩家通过 GMCM 或 ModConfig.json 配置的 AI 厂商参数。</summary>
        private ModConfig _config = null!;

        /// <summary>跟随玩家的浮动精灵（见 FloatingCompanion.cs，贴图缺失时方块占位）。</summary>
        private FloatingCompanion _companion = new();

        /// <summary>
        /// 中文 IME 输入框（最小原型，见 ChineseInputBox.cs）。
        /// F7 开关；打开期间按键全部 Suppress（角色不动），
        /// 字符经 Game1.game1.Window.TextInput 事件流入（中文 IME 上屏唯一入口）。
        /// </summary>
        private readonly ChineseInputBox _inputBox = new();

        /// <summary>
        /// 浮动精灵的贴图（assets/robot.png）。
        /// 启动时加载一次；加载失败时保持 null，精灵自动回退到方块占位。
        /// </summary>
        private Texture2D? _robotTexture;

        /// <summary>
        /// 自己拉起的后端进程（null = 本会话没拉起过，可能是后端本来就开着）。
        /// 只 Kill 自己拉起的进程，不碰玩家手动启动的后端。
        /// </summary>
        private Process? _backendProcess;

        /// <summary>保护 _backendProcess 的锁（启动/退出回调可能在不同线程）。</summary>
        private readonly object _backendProcessLock = new();

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

            // 0.3 加载浮动精灵贴图（assets/robot.png，编译时随项目一起部署到 Mods 目录）。
            //     只在启动时加载一次，不能放到每帧的 Draw 里；加载失败不致命，
            //     精灵会回退到白色方块占位，只打警告日志。
            //     注意：SMAPI 4 把 3.x 的 helper.Content 拆成了两个——
            //       ModContent.Load = 加载 Mod 文件夹里的原始文件（PNG/JSON 等）
            //       GameContent.Load = 加载游戏内容管线的 asset（游戏自带的图、音效）
            //     robot.png 是 Mod 文件夹的原始文件，必须用 ModContent；
            //     用 GameContent 会去游戏内容管线里找，报资源加载失败。
            try
            {
                _robotTexture = helper.ModContent.Load<Texture2D>("assets/robot.png");
                _companion.Texture = _robotTexture;
                Monitor.Log("已加载精灵贴图 assets/robot.png", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor.Log($"加载精灵贴图失败（将使用方块占位）：{ex.Message}", LogLevel.Warn);
            }

            // 1. 按键监听：F7 开关中文输入框；输入框打开期间接管按键（见 OnButtonPressed）
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 2. 注册每帧 Tick 事件：在游戏主线程上执行排队任务（用于安全显示 HUD/聊天消息）
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // 3. 订阅 GameLaunched 事件：等所有 Mod 初始化完后再调 GMCM 的 API
            //    原因：Entry 执行时其他 Mod 可能还没加载完，此时 GetApi 会返回 null
            //    并触发 SMAPI 警告 "Tried to access a mod-provided API before all mods were initialized."
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            // 3.1 订阅进程退出钩子：游戏退出时优雅关闭自己拉起的后端进程。
            //     注意：SMAPI 4 已移除 GameLoop.Exiting 事件（用 Cecil 反射
            //     IGameLoopEvents 确认，全事件面都没有 Exit 类事件），改用
            //     AppDomain.ProcessExit——窗口关闭 / 游戏菜单退出等正常退出
            //     路径都会在 CLR 关闭前触发它；处理函数须在约 2 秒内完成，
            //     Kill 进程树是毫秒级，足够（任务管理器强杀进程时不触发，可接受）。
            AppDomain.CurrentDomain.ProcessExit += OnProcessExiting;

            // 4. 订阅 RenderedWorld 事件：在世界层之上绘制浮动精灵。
            //    该事件由 SMAPI 在每帧世界绘制完成后触发，e.SpriteBatch 仍处于
            //    Begin/End 之间，可直接叠加绘制；主线程事件，无并发风险。
            helper.Events.Display.RenderedWorld += OnRenderedWorld;

            // 5. 订阅 RenderedHud 事件：中文输入框打开时在屏幕底部画当前文本
            helper.Events.Display.RenderedHud += OnRenderedHud;
        }

        /// <summary>
        /// GameLaunched 事件处理：等所有 Mod 初始化完后再注册 GMCM 配置界面；
        /// 并订阅 MonoGame 原生文本输入事件（中文 IME 上屏的唯一入口）。
        /// </summary>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterGmcmConfig();

            // 订阅 MonoGame 的 GameWindow.TextInput 事件。
            // 为什么放这里：SMAPI 4 的 Input 事件没有 TextInput 封装（反射确认
            // IInputEvents 只有 5 个事件），必须直接接 MonoGame 窗口事件；
            // GameLaunched 时游戏窗口已创建，Game1.game1（public static）非空。
            // 注意：TextInputEventArgs 是 struct，字符在公共字段 e.Character（不是属性）；
            //       e.Key 在 IME 输入时不可靠，不要依赖它。
            Game1.game1.Window.TextInput += OnWindowTextInput;

            // 启动时检测后端状态，不可达则按配置自动拉起（fire-and-forget，不阻塞游戏启动）。
            // 完整流程见 EnsureBackendReadyAsync：健康检查 → 按需拉起 → 轮询等就绪。
            _ = EnsureBackendReadyAsync();
        }

        /// <summary>
        /// RenderedWorld 事件处理：每帧更新并绘制浮动精灵。
        /// 该事件本身运行在游戏主线程的绘制阶段，直接用事件携带的 SpriteBatch 绘制即可。
        /// </summary>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            _companion.Update();
            _companion.Draw(e.SpriteBatch);
        }

        /// <summary>
        /// MonoGame 原生文本输入回调：中文 IME 上屏后每个字符触发一次（英文/符号同理）。
        /// 只在输入框打开时接收；过滤控制字符——\r \n \b \t 等编辑键不在这里处理，
        /// 由 OnButtonPressed 的 Enter/Escape/Back 分支负责（双保险，无论 MonoGame
        /// 是否把编辑键也发成 TextInput 都能正确工作）。
        /// </summary>
        private void OnWindowTextInput(object? sender, TextInputEventArgs e)
        {
            if (_inputBox.IsOpen && !char.IsControl(e.Character))
                _inputBox.Append(e.Character);
        }

        /// <summary>
        /// HUD 绘制回调：输入框打开时在屏幕底部画一行占位文本（不做美化，能显示就行）。
        /// 用 Game1.smallFont——中文版游戏的字体图集含汉字；
        /// 若验证时中文显示为空白/方块，说明字形缺失，需换字体方案（另行调研）。
        /// </summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!_inputBox.IsOpen)
                return;

            // 屏幕底部左侧画一行："输入框：<文本>_"（下划线当光标占位）
            var pos = new Vector2(16f, Game1.uiViewport.Height - 40f);
            e.SpriteBatch.DrawString(Game1.smallFont, $"输入框：{_inputBox.Text}_", pos, Color.White);
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
                        // 保存后立刻重新检测/拉起后端：玩家刚填完 BackendDir 就能马上用，
                        // 不用重启游戏（否则 EnsureBackendReadyAsync 只在 OnGameLaunched 跑一次）
                        _ = EnsureBackendReadyAsync();
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

                // 回答温度：GMCM 接口文件里没有 AddSlider，float 用 AddNumberOption
                // （游戏中表现为 +/- 数值调节），min/max/interval 为可空参数
                gmcm.AddNumberOption(
                    this.ModManifest,
                    getValue: () => _config.Temperature,
                    setValue: v => _config.Temperature = v,
                    name: () => "回答温度",
                    tooltip: () => "0.0-2.0，越高越有创意但越容易胡说。推荐 0.5-0.9",
                    min: 0f,
                    max: 2f,
                    interval: 0.1f
                );

                // 后端目录：自动拉起 Python 后端用（绝对路径，含 main.py 的那个文件夹）
                gmcm.AddTextOption(
                    this.ModManifest,
                    getValue: () => _config.BackendDir,
                    setValue: v => _config.BackendDir = v,
                    name: () => "后端目录路径",
                    tooltip: () => "填 backend 文件夹的绝对路径（如 D:\\xxx\\backend）；留空则不自动拉起"
                );

                // 是否自动拉起后端：开启后游戏启动时若检测不到后端，会自动用 uv 运行
                gmcm.AddBoolOption(
                    this.ModManifest,
                    getValue: () => _config.AutoStartBackend,
                    setValue: v => _config.AutoStartBackend = v,
                    name: () => "自动启动后端",
                    tooltip: () => "游戏启动时自动运行后端；关闭则需手动启动"
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
        /// 按键事件处理：
        /// - F7：开关中文输入框；
        /// - 输入框打开期间：所有按键 Suppress（角色不移动、游戏不响应），
        ///   Enter 提交（复用聊天框对话流程）、Esc 关闭清空、Back 删除末尾字符。
        /// 字符输入不在本事件处理——中文 IME 上屏走 Window.TextInput 事件
        /// （见 OnWindowTextInput，SMAPI 4 没有对应封装）。
        /// </summary>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // F7：开关输入框
            if (e.Button == SButton.F7)
            {
                if (_inputBox.IsOpen)
                    _inputBox.Close();
                else
                    _inputBox.Open();
                return;
            }

            // 输入框未打开时不干预任何按键，一切照原版走
            if (!_inputBox.IsOpen)
                return;

            // 打开期间吃掉所有按键：游戏和其他 Mod 都收不到（角色不会移动）
            Helper.Input.Suppress(e.Button);

            // 回车：提交。复用聊天框入口（内部有安检，回复显示在原版聊天框浅蓝色）
            if (e.Button == SButton.Enter)
            {
                string question = _inputBox.Text;
                _inputBox.Close();
                if (!string.IsNullOrWhiteSpace(question))
                    HandleAiChatQuestion(question);
                return;
            }

            // Esc：关闭并清空（下一帧起角色恢复移动）
            if (e.Button == SButton.Escape)
            {
                _inputBox.Close();
                return;
            }

            // Backspace：删除末尾字符
            if (e.Button == SButton.Back)
                _inputBox.Backspace();
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
                Health = Game1.player.health,

                // ========== 【扩展状态感知】第二批新增字段 ==========
                // 当前手持工具名（游戏内原名，如 "Axe"/"Pickaxe"），空手时为空字符串；
                // 中文翻译交给 Python 端统一处理
                CurrentTool = Game1.player.CurrentTool?.Name ?? "",
                FarmingLevel = Game1.player.farmingLevel.Value,     // 耕种技能等级（0-10）
                MiningLevel = Game1.player.miningLevel.Value,       // 采矿技能等级（0-10）
                DaysPlayed = (int)Game1.stats.DaysPlayed,            // 存档已玩总天数（uint → int）

                // ========== 【扩展状态感知】第二批 b 新增字段 ==========
                // 背包：取前 10 个非空物品，格式 "名称 x数量"。Items 是 Inventory（IList<Item>）。
                Inventory = string.Join(", ",
                    Game1.player.Items
                        .Where(it => it != null)
                        .Take(10)
                        .Select(it => $"{it.Name} x{it.Stack}")),
                // NPC 好感度：遍历 friendshipData（NetStringDictionary<Friendship>），
                // 取好感>0 的前 8 个，心数 = Points / 250（每心 250 点），按好感降序。
                NPCAffection = string.Join(", ",
                    Game1.player.friendshipData.Pairs
                        .Where(p => p.Value.Points > 0)
                        .OrderByDescending(p => p.Value.Points)
                        .Take(8)
                        .Select(p => $"{p.Key}:{p.Value.Points / 250}心")),
                // 当前任务：取第一个未完成的（completed=false），标题 + 当前目标。
                // questLog 是 NetObjectList<Quest>，completed 为 false 表示进行中。
                CurrentQuest = Game1.player.questLog
                    .FirstOrDefault(q => !q.completed.Value) is { } q
                        ? $"{q.questTitle} - {q.currentObjective}"
                        : "",
            };

            // 入口标记，方便在 SMAPI 日志里区分是 F7 还是聊天框触发的
            string entryTag = useChatBox ? "聊天框" : "F7";
            this.Monitor.Log(
                $"[AI伴侣] [{entryTag}] 收集状态：玩家={state.PlayerName}，体力={state.Stamina}，时间={state.GameTime}，地点={state.Location}，" +
                $"季节={state.Season}，下雨={state.IsRaining}，日期={state.DayOfMonth}号，金币={state.Money}，血量={state.Health}，" +
                $"工具={state.CurrentTool}，耕种Lv={state.FarmingLevel}，采矿Lv={state.MiningLevel}，已玩天数={state.DaysPlayed}，" +
                $"背包={state.Inventory}，NPC好感={state.NPCAffection}，当前任务={state.CurrentQuest}",
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

        // ================================================================
        // 后端健康检查（启动时检测一次；下一步"自动拉起后端"复用为轮询）
        // ================================================================

        /// <summary>
        /// 检测后端健康状态。返回 (后端是否可达, LLM 是否已配置)。
        /// 超时 3 秒（比 /chat 的 15 秒短，启动检测不能卡太久）。
        /// 任何异常都视为"后端不可达"，不向调用方抛异常。
        /// internal 可见性：下一步"自动拉起后端 + 轮询等待就绪"会复用本方法。
        /// </summary>
        internal async Task<(bool ok, bool llmConfigured)> CheckBackendHealthAsync()
        {
            try
            {
                // 用 CancellationTokenSource 把超时缩短到 3 秒：
                // 全局 HttpClient 的超时是 15 秒，实际生效的是两者中较短的那个。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using HttpResponseMessage response = await HttpClient.GetAsync(HealthUrl, cts.Token);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(cts.Token);
                HealthResponse? result = JsonSerializer.Deserialize<HealthResponse>(json, JsonOptions);

                // 拿到合法 JSON 且 status=ok 才算后端可达；LLM 配置状态原样透传
                bool ok = string.Equals(result?.Status, "ok", StringComparison.OrdinalIgnoreCase);
                return (ok, ok && (result?.LlmConfigured ?? false));
            }
            catch (Exception)
            {
                // 连接失败 / 3 秒超时（OperationCanceledException）/ 非 2xx / JSON 解析失败，
                // 统一视为"后端不可达"
                return (false, false);
            }
        }

        /// <summary>
        /// 启动时的后端保障流程：健康检查 → 可达则按 LLM 配置情况提示；
        /// 不可达则按配置自动拉起后端并轮询等就绪。
        /// 在 OnGameLaunched 里以 fire-and-forget 方式调用，不阻塞游戏启动。
        /// HUD 显示走 _mainThreadActions 主线程队列（EnqueueHudMessage 内部处理），
        /// 因此 await 之后的后台线程上下文里也能安全调用。
        /// </summary>
        private async Task EnsureBackendReadyAsync()
        {
            var (ok, llmConfigured) = await CheckBackendHealthAsync();
            if (ok)
            {
                // 后端本来就开着（玩家手动启动 / 上次游戏退出时没杀干净），不重复拉起
                if (!llmConfigured)
                    EnqueueHudMessage("AI 伴侣：请在 ESC → Mod 选项里配置 AI 厂商和 Key", isError: true);
                else
                    Monitor.Log("[AI伴侣] 后端已就绪，LLM 已配置", LogLevel.Info);
                return;
            }

            // ---- 后端不可达，分三种情况处理 ----

            // 情况 1：玩家关掉了自动启动 → 提醒手动启动
            if (!_config.AutoStartBackend)
            {
                Monitor.Log("[AI伴侣] 后端不可达且自动启动已关闭，跳过拉起", LogLevel.Info);
                EnqueueHudMessage("AI 伴侣：后端未启动（自动启动已关闭），请手动运行后端", isError: true);
                return;
            }

            // 情况 2：没填后端目录 → 提醒去 GMCM 填
            if (string.IsNullOrWhiteSpace(_config.BackendDir))
            {
                Monitor.Log("[AI伴侣] 后端不可达且未配置 BackendDir，跳过拉起", LogLevel.Info);
                EnqueueHudMessage("AI 伴侣：未配置后端目录，请在 ESC → Mod 选项里填写 backend 路径", isError: true);
                return;
            }

            // 情况 3：目录已填 → 拉起并等就绪
            EnqueueHudMessage("AI 伴侣正在启动后端...");
            bool started = await StartBackendAsync();
            if (!started)
                return; // 失败原因已在 StartBackendAsync 里用 HUD 提示过

            // 就绪后复查 LLM 配置情况，给对应的上线提示。
            // 成功走聊天框（OnGameLaunched 在标题界面触发，HUD 消息此时不显示会被丢弃；
            // 聊天框消息可靠）。错误提示仍走 HUD——玩家进游戏后会重试操作。
            var (_, llmOk) = await CheckBackendHealthAsync();
            if (!llmOk)
                EnqueueHudMessage("AI 伴侣：后端已上线，但还没配置 AI 厂商和 Key（ESC → Mod 选项）", isError: true);
            else
                EnqueueChatMessage("AI 伴侣已上线！", ChatReplyColor);
        }

        /// <summary>
        /// 拉起 Python 后端并轮询 /health 等待就绪（最多 30 秒）。
        /// 自动二选一启动方式：
        ///   - 发布模式：backendDir/backend.exe 存在 → 直接跑 PyInstaller 打包的 exe（玩家侧零依赖，无需 uv/Python）
        ///   - 开发模式：没有 exe → uv run uvicorn（源码 backend/ 目录）
        /// 返回是否成功就绪；任何异常都转为 HUD 提示 + false，不向调用方抛。
        /// 拉起的进程记录到 _backendProcess，游戏退出时由 OnGameExiting 负责关闭。
        /// </summary>
        private async Task<bool> StartBackendAsync()
        {
            // 1. 决定后端目录：GMCM 填了就用填的；没填就用 Mod 目录下的 backend/（发布形态默认位置）
            string backendDir = _config.BackendDir.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(backendDir))
                backendDir = Path.Combine(Helper.DirectoryPath, "backend");

            // 2. 决定启动方式：目录下有打包好的 backend.exe → 发布模式；没有 → 开发模式
            string exePath = Path.Combine(backendDir, "backend.exe");
            bool useExe = File.Exists(exePath);

            ProcessStartInfo psi;
            if (useExe)
            {
                // 发布模式：双击即可跑，不需要 uv、不需要 Python 环境
                psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = backendDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,           // 不弹黑框
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Monitor.Log($"[AI伴侣] 使用打包模式启动后端：{exePath}", LogLevel.Info);
            }
            else
            {
                // 开发模式：走 uv。先校验目录存在（GMCM 里填错路径是常见操作，先拦一道）
                if (!Directory.Exists(backendDir))
                {
                    Monitor.Log($"[AI伴侣] 后端目录不存在：{backendDir}", LogLevel.Warn);
                    EnqueueHudMessage("AI 伴侣：后端目录不存在，请检查 ESC → Mod 选项里的路径", isError: true);
                    return false;
                }

                // UseShellExecute=false 才能重定向输出；重定向后必须持续读流，
                // 否则管道缓冲区写满后 Python 会被阻塞卡死（见下方两个 PumpStream 任务）。
                psi = new ProcessStartInfo
                {
                    FileName = "uv",
                    Arguments = "run uvicorn main:app --port 8000",
                    WorkingDirectory = backendDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Monitor.Log($"[AI伴侣] 使用开发模式启动后端（uv）：{backendDir}", LogLevel.Info);
            }

            try
            {
                // 3. 启动进程。开发模式最常见失败：uv 没装或不在 PATH（Win32Exception）
                Process process;
                try
                {
                    process = Process.Start(psi)!;
                }
                catch (Exception ex)
                {
                    if (!useExe)
                        EnqueueHudMessage("AI 伴侣：拉起失败，找不到 uv，请确认已安装 uv 并加入 PATH", isError: true);
                    else
                        EnqueueHudMessage("AI 伴侣：拉起失败，无法启动 backend.exe（可能被安全软件拦截）", isError: true);
                    Monitor.Log($"启动后端失败：{ex.Message}", LogLevel.Warn);
                    return false;
                }

                // 4. 记录到字段（加锁）：游戏退出时 OnGameExiting 按它 Kill
                lock (_backendProcessLock)
                {
                    _backendProcess = process;
                }

                // 5. 异步读 stdout/stderr 落盘到 backend.log（fire-and-forget）。
                //    AutoFlush 保证每行立即写盘，进程被 Kill 后日志不丢；
                //    两路读流共用一个 writer，写行时加锁；两路都结束后统一 Dispose。
                string logPath = Path.Combine(backendDir, "backend.log");
                var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                var logLock = new object();
                _ = Task.WhenAll(
                    PumpStreamAsync(process.StandardOutput, logWriter, logLock, "stdout"),
                    PumpStreamAsync(process.StandardError, logWriter, logLock, "stderr")
                ).ContinueWith(_ => logWriter.Dispose());

                // 6. 轮询 /health：每秒一次，最多 30 次（后端要加载向量库，首次启动较慢）
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    var (ok, _) = await CheckBackendHealthAsync();
                    if (ok)
                        return true;

                    // 进程提前退出（如端口被占、目录里没有 main.py）→ 不必等满 30 秒
                    if (process.HasExited)
                        break;
                }

                EnqueueHudMessage("AI 伴侣：后端启动超时，请查看 backend 目录下的 backend.log", isError: true);
                return false;
            }
            catch (Exception ex)
            {
                // 兜底：目录校验通过后仍可能出错（如磁盘 IO、权限问题）
                EnqueueHudMessage("AI 伴侣：后端启动失败，详情见 SMAPI 日志", isError: true);
                Monitor.Log($"启动后端异常：{ex}", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>
        /// 后台泵：持续读取进程的一条输出流并写入 backend.log。
        /// 若不读流，重定向的管道缓冲区写满后 Python 端 print 会阻塞（后端假死）。
        /// 进程被 Kill 时流会关闭，ReadLineAsync 返回 null 或抛异常，均按流结束处理。
        /// </summary>
        private static async Task PumpStreamAsync(StreamReader reader, StreamWriter logWriter, object logLock, string tag)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lock (logLock)
                        logWriter.WriteLine($"[{tag}] {line}");
                }
            }
            catch
            {
                // 流被强制关闭（Kill 进程）时的 IO 异常，忽略
            }
        }

        /// <summary>
        /// 进程退出钩子（AppDomain.ProcessExit）：关闭自己拉起的后端进程，
        /// entireProcessTree 连 uv 派生的 python 子进程一起杀。
        /// 只处理 _backendProcess 里记录的进程，玩家手动启动的后端不受影响。
        /// 注意：本方法在 CLR 关闭阶段执行，有约 2 秒时限，不做任何慢操作。
        /// ⚠️ 此阶段 SMAPI 日志的 StreamWriter 可能已 Dispose，调 Monitor.Log 会抛
        ///    ObjectDisposedException（实测崩溃过）——所以整个方法包 try-catch，
        ///    且内部一个 Monitor.Log 都不调，失败静默放过（游戏已在退，无所谓）。
        /// </summary>
        private void OnProcessExiting(object? sender, EventArgs e)
        {
            try
            {
                lock (_backendProcessLock)
                {
                    if (_backendProcess is { HasExited: false } p)
                        p.Kill(entireProcessTree: true);
                    _backendProcess = null;
                }
            }
            catch
            {
                // 游戏关闭阶段的任何异常都静默吞掉：日志已不可用，弹窗更是不可能
            }
        }

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
                    model = cfg.Model,
                    temperature = cfg.Temperature  // 回答温度（0.0-2.0），对齐后端 ConfigRequest
                };

                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 复用全局 HttpClient（15s 超时），地址从 BackendBaseUrl 派生
                var response = await HttpClient.PostAsync(BackendBaseUrl + "/config", content);
                response.EnsureSuccessStatusCode();

                Game1.addHUDMessage(new HUDMessage("AI伴侣：配置已推送到后端"));
                Monitor.Log($"配置已推送：provider={cfg.Provider}, model={cfg.Model}, temperature={cfg.Temperature}", LogLevel.Debug);
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

            // ========== 【扩展状态感知】第二批新增字段 ==========
            /// <summary>当前手持工具名（游戏内原名，如 "Axe"/"Pickaxe"），空手时为空字符串。</summary>
            public string CurrentTool { get; set; } = string.Empty;

            /// <summary>耕种技能等级（0-10）。</summary>
            public int FarmingLevel { get; set; }

            /// <summary>采矿技能等级（0-10）。</summary>
            public int MiningLevel { get; set; }

            /// <summary>存档已玩总天数（用于"第 N 天"的进度表述）。</summary>
            public int DaysPlayed { get; set; }

            // ========== 【扩展状态感知】第二批 b 新增字段 ==========
            /// <summary>背包物品汇总（前 10 个非空物品，格式"名称 x数量"，逗号分隔）。</summary>
            public string Inventory { get; set; } = string.Empty;

            /// <summary>NPC 好感度汇总（好感>0 的前 8 个，格式"NPC名:心数心"，按好感降序）。</summary>
            public string NPCAffection { get; set; } = string.Empty;

            /// <summary>当前未完成任务的标题与目标，没有则空字符串。</summary>
            public string CurrentQuest { get; set; } = string.Empty;
        }

        /// <summary>
        /// /health 接口返回结构：{"status":"ok","llm_configured":true,"provider":"deepseek"}。
        /// </summary>
        private sealed class HealthResponse
        {
            public string Status { get; set; } = string.Empty;

            // 后端字段是 snake_case："大小写不敏感"选项不会忽略下划线，
            // llm_configured 映射不到 LlmConfigured，必须显式指定 JSON 字段名。
            [JsonPropertyName("llm_configured")]
            public bool LlmConfigured { get; set; }
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
