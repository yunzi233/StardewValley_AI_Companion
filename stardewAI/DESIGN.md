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
- 已实现（随每次对话请求发给 Python 后端，F7 与聊天框两个入口收集的字段一致）：名字、体力、血量、游戏时间、日期（1-28）、季节、是否下雨、地点、金币
- 待扩展：背包、当前工具、NPC 好感度、当日任务
- Python 端会把季节（spring→春季）、天气（bool→正在下雨/天气晴朗）本地化成中文，再连同其他状态注入 LLM 提示词
- 季节/天气目前只进 LLM 上下文，尚未参与向量检索 query

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
- 存储：ChromaDB + bge-m3（本地向量模型）
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
├─ retrieve 节点：ChromaDB + bge-m3 检索（top-3 片段），失败走条件边短路结束
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
- 注意：无论哪种对话模式，**嵌入模型 bge-m3 始终走本地 Ollama**，所以 Ollama 仍是必装项
- config.json 含密钥，已 gitignore；启动时读取一次，游戏内 GMCM 保存可热更新（走 POST /config，无需重启）

待完善：

- [x] 游戏内配置界面（GMCM，2026-10-13）：厂商/Key/模型/温度四项，保存即推送后端热更新；启动器配置不做
- [x] temperature 可配置（2026-10-14）：GMCM 数值选项 → `POST /config`（校验 0.0-2.0）→ config.json → `create_llm`，实时生效；top-k 已挪进 `aipy/config.py`（2026-10-12）
- [ ] 配置校验与错误提示再细化（模型名写错、额度不足等）

## 当前进度

- [x] 端到端通信（C# ↔ Python）
- [x] 接入本地 Ollama（qwen2.5:3b）
- [x] RAG 知识库（ChromaDB + bge-m3，已收录作物 / 鱼类 / NPC 礼物）
- [x] 状态感知（基础字段：名字 / 体力 / 时间 / 地点）
- [x] 扩展状态感知（第一批：季节 / 是否下雨 / 日期 / 金币 / 血量，C# 收集 + Python 提示词注入）
- [x] 游戏内自由提问（原版聊天框 + Harmony 拦截 `AI ` 前缀，回复进聊天框；F7 保留测试入口）
- [x] BYOK 基础（config.json + LLM 工厂：ollama / deepseek / zhipu / qwen / kimi）
- [ ] 扩展状态感知（第二批：背包 / 当前工具 / NPC 好感度 / 当日任务；季节天气参与检索 query）
- [x] 多轮记忆（LangGraph + AsyncSqliteSaver：retrieve → chat 对话图，历史按玩家名隔离并落盘 SQLite，重启不丢；2026-10-13）
- [x] 浮动精灵（第一版，2026-10-14）：`RenderedWorld` 事件绘制，跟随玩家 + sin 浮动；`ModContent.Load` 加载 `assets/robot.png`（64x64）按图片实际尺寸 1:1 绘制，贴图缺失回退方块占位
- [ ] 主动提醒
- [x] 游戏内配置界面（GMCM，2026-10-14）：C# 端 `ModConfig.cs` + `RegisterGmcmConfig`（GameLaunched 事件），Python 端 `POST /config` + `save_config` + `PlaceholderLLM`；配置改完无需重启后端，对话历史保留。
- [ ] 设置玩家配置界面