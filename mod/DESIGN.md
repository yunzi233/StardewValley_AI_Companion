# DESIGN — 星露谷 AI 伴侣设计蓝图

## 一句话目标
一个漂浮在玩家身旁的像素机器人，陪玩家一起冒险——能对话、能感知状态、能主动提醒、懂星露谷知识。

## 四个核心模块

### 1. 浮动精灵（视觉存在感）
- 进游戏后，一个像素机器人在玩家身旁漂浮
- 跟随玩家移动，有上下浮动待机动画
- 对话入口已打通：按 T 在原版聊天框输入 `AI 问题`（主要方式），F7 为快捷测试；未来精灵出现后对话入口不变
- 情绪状态（reply 里的 emotion 字段）可以驱动机器人表情变化

### 2. 状态感知（知道玩家在干嘛）
- 已实现（随每次对话请求发给 Python 后端，F7 与聊天框两个入口收集的字段一致）：名字、体力、血量、游戏时间、日期（1-28）、季节、是否下雨、地点、金币、手持工具、耕种/采矿等级、存档天数、背包、NPC 好感度、当日任务
- 待扩展：暂无
- Python 端会把季节（spring→春季）、天气（bool→正在下雨/天气晴朗）本地化成中文，再连同其他状态注入 LLM 提示词
- 季节/天气/地点会作为前缀拼进向量检索 query（如 `[春季 晴天 Farm] 玩家原话`），提升状态相关召回

### 3. 主动提醒（不是被动的工具）
- 不对话时，AI 偶尔主动开口提醒
- 触发机制（待定）：时间跳变 / 体力阈值 / 场景变化 / 冷却到期
- 决策权交给 Python 端的 LLM（判断"此刻值不值得说话"）
- 返回 `{"action": "silent"}` 时保持安静，避免打扰

### 4. 星露谷知识库（RAG）
- 预想方案（已修正）：不是每次请求调 Wiki API，而是预先抓取 + 清洗 + 向量化
- 数据来源：stardewvalleywiki.com（MediaWiki API）
- 抓取策略：**不做"总结"**——数字/价格/掉落率原样保留，只让 AI 清洗废话段落
- 首批主题：四季作物 / 鱼类图鉴 / NPC 礼物 / 祭献包 / 矿洞掉落
- 存储：ChromaDB + fastembed `BAAI/bge-small-zh-v1.5`（本地 ONNX 嵌入，中文 512 维）
- 查询：检索 top-K 相关片段 → 塞给 LLM → LLM 基于片段回答

### 5. 输入体验（✅ 已实现：借原版聊天框，绕开自研输入框）
- 最终采用的方案：**不自研输入框**，直接复用星露谷 1.6 原版聊天框
  - 玩家按 T 开框（原版行为，完全不拦截 T 键），输入 `AI 问题` 回车
  - Harmony Prefix 补丁 `ChatBox.textBoxEnter(string)`：`AI ` 前缀的消息跳过原版发送，转给后端；其他消息（普通聊天、`/` 命令）一律放行
  - AI 回复经主线程队列调 `chatBox.addMessage(text, Color.LightSkyBlue)` 显示在聊天框，天然带历史记录
  - F7 保留为测试问题池入口（HUD 显示），与聊天框入口共用同一套状态收集和后端流程
- 原计划难点（SMAPI 中文 IME、接管 Window.TextInput）因此被规避——中文输入由原版聊天框自身承担
- 关键查证（反射 1.6.15）：输入控件是 `Game1.chatBox.chatBox`（类型 `ChatTextBox`，`Text` 在基类 `TextBox`）；回车走 `textBoxEnter(TextBox)` → 内部调 `textBoxEnter(string)` → 之后无条件 `reset()` + `clickAway()`，所以补丁只打 string 重载，清空/关框由原版自动完成

### 6. AI 神力（远期构想）
- 玩家对话请求 AI 帮忙做农活（浇水 / 收获 / 喂动物）
- **不控制角色寻路**，而是 AI 返回 `{"action": "water_all"}`，C# 直接改游戏状态
- 优点：跳过寻路算法和进程冲突，实现简单、画面有"魔法感"
- AI 有权拒绝 / 吐槽（玩家请求过多时："您完全不想干活是吗"）

## 数据流

玩家按 T 输入 `AI 问题`（Harmony 拦截）/ 按 F7（测试问题池）
↓
C# Mod 收集游戏状态（10 个字段，JSON）
↓ HTTP POST
FastAPI 后端
↓
LangGraph 对话图（agent/graph.py，AsyncSqliteSaver 按 thread_id=玩家名 存对话历史到 data/checkpoints.db）：
├─ retrieve 节点：ChromaDB + fastembed `bge-small-zh-v1.5` 检索（top-3 片段），失败走条件边短路结束
└─ chat 节点：历史消息 + 参考资料 + 结构化游戏状态 + 问题 拼提示词，调 LLM
    ├─ 本地：Ollama（qwen2.5:3b，免 Key）
    └─ 云端：DeepSeek / 智谱 / 通义 / Kimi（OpenAI 兼容，BYOK）
↓（远期：长期玩家档案 + 主动提醒决策）
C# 收到 {reply, emotion}
↓ 主线程任务队列
聊天框消息（浅蓝色，可翻历史）/ HUD 弹字（F7）/ 未来：机器人气泡、表情变化


## 发布时的两种模式（BYOK）——✅ 基础已实现

当前实现（`src/aipy/llm_factory.py` + `src/aipy/config.json`，模板为 `config.example.json`）：

- **local（本地模式）**：`provider: "ollama"`，免 Key；玩家自己装 Ollama + 拉 `qwen2.5:3b`；`base_url` 可改（默认本机 11434）
- **cloud（云端模式）**：`provider` 选 `deepseek` / `zhipu` / `qwen` / `kimi`，填自己的 `api_key` 和 `model`；四家 base_url 已在工厂里预设，全部走 OpenAI 兼容接口
- 注意：嵌入模型已改为 fastembed `BAAI/bge-small-zh-v1.5`（ONNX，随后端自带），**Ollama 只在"本地对话模式"下才需要**
- config.json 含密钥，已 gitignore；启动时读取一次，游戏内 GMCM 保存可热更新（走 POST /config，无需重启）

待完善：

- [x] 游戏内配置界面（GMCM，2026-10-13）：厂商/Key/模型/温度四项，保存即推送后端热更新；启动器配置不做
- [x] temperature 可配置（2026-10-14）：GMCM 数值选项 → `POST /config`（校验 0.0-2.0）→ config.json → `create_llm`，实时生效；top-k 已挪进 `aipy/config.py`（2026-10-12）
- [ ] 配置校验与错误提示再细化（模型名写错、额度不足等）

## 当前进度

- [x] 端到端通信（C# ↔ Python）
- [x] 接入本地 Ollama（qwen2.5:3b）
- [x] RAG 知识库（ChromaDB + fastembed `bge-small-zh-v1.5`，已收录作物 / 鱼类 / NPC 礼物；2026-10-15 由 bge-m3 切换而来）
- [x] 状态感知（基础字段：名字 / 体力 / 时间 / 地点）
- [x] 扩展状态感知（第一批：季节 / 是否下雨 / 日期 / 金币 / 血量，C# 收集 + Python 提示词注入）
- [x] 扩展状态感知（第二批 a，2026-10-14）：当前工具 / 耕种 / 采矿等级 / 存档天数（C# 收集 → Python 状态块，工具名英文→中文映射，空手不显示）
- [x] 游戏内自由提问（原版聊天框 + Harmony 拦截 `AI ` 前缀，回复进聊天框；v0.9 另有 F7 中文输入框）
- [x] 中文 IME 输入框（v0.9，2026-10-14）：F7 开关，直连 MonoGame `GameWindow.TextInput` 接收 IME 上屏字符（SMAPI 4 无封装，见 NOTES 踩坑 23）；打开期间按键全接管防误操作，回车复用聊天框回复链路
- [x] BYOK 基础（config.json + LLM 工厂：ollama / deepseek / zhipu / qwen / kimi）
- [x] 扩展状态感知（第二批 b，2026-10-15）：背包 / NPC 好感度 / 当日任务（C# 收集 → Python 状态块，空值不显示）；季节/天气/地点前缀进检索 query
- [x] 多轮记忆（LangGraph + AsyncSqliteSaver：retrieve → chat 对话图，历史按玩家名隔离并落盘 SQLite，重启不丢；2026-10-13）
- [x] 浮动精灵（第一版，2026-10-14）：`RenderedWorld` 事件绘制，跟随玩家 + sin 浮动；`ModContent.Load` 加载 `assets/robot.png`（64x64）按图片实际尺寸 1:1 绘制，贴图缺失回退方块占位
- [ ] 主动提醒
- [x] 游戏内配置界面（GMCM，2026-10-14）：C# 端 `ModConfig.cs` + `RegisterGmcmConfig`（GameLaunched 事件），Python 端 `POST /config` + `save_config` + `PlaceholderLLM`；配置改完无需重启后端，对话历史保留。
- [ ] 设置玩家配置界面
- [ ] 气泡对话 UI：AI 回复以气泡形式从机器人贴图头上冒出（现为聊天框 / HUD 文字）
- [ ] 知识库扩充：把星露谷 wiki 内容总结成知识库文档，减少对 wiki 的实时请求
- [x] 嵌入改本地 ONNX（2026-10-15）：bge-m3(Ollama, 1024 维) → fastembed `bge-small-zh-v1.5`(512 维, int8 ~23MB)，`chroma_db/` 全量重建，Ollama 不再是嵌入必装项
- [x] 后端生命周期打通（2026-10-15）：`GET /health` 零开销探活；C# 启动时检测 → 不可达按 GMCM 配置自动用 uv 拉起（轮询最多 30 秒，日志落 `backend.log`）→ 游戏退出（`AppDomain.ProcessExit`，SMAPI 4 已移除 GameLoop.Exiting）Kill 自己拉起的进程树；GMCM 新增 BackendDir / AutoStartBackend
- [ ] **面向玩家打包（当前优先级，路线 2）**：PyInstaller 打包 exe + 把"自动拉起 uv"换成"拉起打包好的 exe"，做到玩家零环境依赖