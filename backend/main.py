import traceback
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from langchain_chroma import Chroma
from langchain_core.messages import HumanMessage, SystemMessage, AIMessage
from aipy.config import CHROMA_DIR, COLLECTION_NAME, EMBEDDING_MODEL, TOP_K
from aipy.embeddings import FastEmbedEmbeddings
from aipy.llm_factory import load_config, create_llm, save_config
from aipy.agent.graph import init_agent, build_graph

# graph 在 lifespan（async 上下文）里构建：langgraph-checkpoint-sqlite 3.x 的
# AsyncSqliteSaver.__init__ 内部会调用 asyncio.get_running_loop()，
# 在模块顶层同步调用 build_graph() 没有事件循环，会直接抛 RuntimeError。
graph = None


@asynccontextmanager
async def lifespan(_app: FastAPI):
    global graph
    graph = build_graph()
    yield


app = FastAPI(title="Stardew AI Companion", lifespan=lifespan)

# ========== 系统提示词 ==========
SYSTEM_PROMPT = """你是一个星露谷物语里的 AI 伴侣，陪玩家一起冒险。
你说话亲切、简短（1-3 句），偶尔吐槽，偶尔提醒。

【核心规则（必须严格遵守）】
你的默认状态是"不知道"。只有当【参考资料】里**明确写有**答案时，
你才可以回答游戏知识类问题。

判断流程：
1. 玩家问的是游戏知识（作物、鱼、礼物、价格、时间等）→ 先看【参考资料】
   - 资料里有相关内容 → 基于资料回答，不许添加资料外的信息
   - 资料里没有 → 直接回答"这个我不太清楚，建议你查一下 Wiki"
2. 玩家问的是与游戏无关的事（天气、编程、闲聊）→ 直接说"这不是我擅长的，我只懂星露谷"
3. 玩家问的是游戏知识，但属于"常识类"（怎么开背包、怎么睡觉）→ 可以自由回答

【绝对禁止】
- 编造【参考资料】里没有的具体数字、日期、价格、掉落率
- 用"我记得""一般来说"这类模糊词掩护编造
- 明明资料里没有，却给出一个看起来很具体的答案

【正确示例】
玩家："怎么升级农舍？" → 资料里没有 → 回答："升级农舍的细节我知识库里没收录，你可以在星露谷 Wiki 上查'农舍'页面。"
玩家："海莉喜欢什么？" → 资料里有 → 回答："海莉最爱向日葵、椰子、粉红蛋糕和水果沙拉。"（只能提资料里写的，不许加）"""

# ========== 数据模型 ==========
class ChatRequest(BaseModel):
    PlayerName: str = "农夫"
    Stamina: float = 100.0
    GameTime: int = 600
    Location: str = "Farm"
    Message: str = ""
    Season: str = "spring"
    IsRaining: bool = False
    DayOfMonth: int = 1
    Money: int = 500
    Health: int = 100
    # ========== 【扩展状态感知】第二批新增字段（都有默认值，C# 老版本不传也不崩） ==========
    CurrentTool: str = ""       # 当前手持工具名（游戏内原名），空手为 ""
    FarmingLevel: int = 0       # 耕种等级（0-10）
    MiningLevel: int = 0        # 采矿等级（0-10）
    DaysPlayed: int = 0         # 存档已玩总天数

    # ========== 【扩展状态感知】第二批 b 新增字段 ==========
    Inventory: str = ""         # 背包物品汇总（"Paragon x5, CopperOre x12"）
    NPCAffection: str = ""      # NPC 好感度汇总（"Abigail:10心, Leah:8心"）
    CurrentQuest: str = ""      # 当前未完成任务的标题与目标


class ChatResponse(BaseModel):
    reply: str
    emotion: str = "happy"


class ConfigRequest(BaseModel):
    provider: str
    api_key: str = ""
    model: str
    temperature: float = 0.7  # 生成随机度（0.0-2.0），C# 老版本推送不带此字段时用默认值


# ========== 占位 LLM（未配置时启动用）==========
class PlaceholderLLM:
    """config.json 缺失或无效时的占位模型，避免后端启动失败。
       玩家在游戏里通过 GMCM 保存配置后，/config 接口会重建真实 LLM。"""

    async def ainvoke(self, messages, **kwargs):
        return AIMessage(
            content="请先在游戏里配置 AI 厂商和 API Key（ESC → Mod 选项 → Stardew AI Assistant）"
        )

# ========== 启动时加载向量库（只做一次）==========
class _NullRetriever:
    """chroma_db 缺失时的兜底检索器：永远返回空结果。
       不让后端崩溃——chat_node 拿到空 context，按系统提示词的规则
       回答"这个我不太清楚，建议你查一下 Wiki"，玩家能明确感知知识库没带上。"""

    async def ainvoke(self, query):
        print(f"[检索] 知识库未加载，跳过检索：query={query!r}")
        return []


if CHROMA_DIR.exists():
    print("正在加载向量库...")
    embeddings = FastEmbedEmbeddings(model_name=EMBEDDING_MODEL)
    vectorstore = Chroma(
        persist_directory=str(CHROMA_DIR),
        embedding_function=embeddings,
        collection_name=COLLECTION_NAME,
    )
    retriever = vectorstore.as_retriever(search_kwargs={"k": TOP_K})
    print("向量库加载完成")
else:
    # 打包分发时 chroma_db/ 必须随包带上（建好的知识库直接复用，不用重新向量化）；
    # 缺失不致命：跳过检索而不是崩溃，具体修法打印在日志里
    print(f"[警告] 未找到知识库目录 {CHROMA_DIR}，检索功能不可用（AI 会回答『不清楚』）。")
    print("       开发模式：在 backend/ 下执行 uv run python -m aipy.build_knowledge_base 建库")
    print("       打包分发：把 backend/chroma_db/ 整个目录随包带上")
    vectorstore = None
    retriever = _NullRetriever()

# ========== 对话模型 ==========
# config.json 不存在或格式错时用 PlaceholderLLM 启动，不阻塞后端；
# 玩家在游戏里通过 GMCM 保存配置后，/config 接口会重建真实 LLM。
# _config 先兜底为空 dict：未配置启动时该变量也始终存在，/health 读取不会 NameError。
_config: dict = {}
try:
    _config = load_config()
    llm = create_llm(_config)
    print(f"[LLM] provider={_config['provider']}, model={_config['model']}")
except Exception as e:
    print(f"[LLM] 未配置（{type(e).__name__}: {e}），等待游戏内配置推送...")
    llm = PlaceholderLLM()

# ========== 初始化对话依赖 ==========
# 注：graph 本体已在 lifespan（async 上下文）里构建（AsyncSqliteSaver 需要 running loop），
# 这里只注入 llm / retriever / system_prompt
init_agent(llm=llm, retriever=retriever, system_prompt=SYSTEM_PROMPT)

# ========== 接口 ==========
@app.get("/health")
async def health_check():
    """健康检查：极快返回——不触发检索、不调用 LLM、不读磁盘、不发网络请求。
       供 C# 启动时检测后端是否就绪、LLM 是否已配置；
       下一步"自动拉起后端 + 轮询等待就绪"会复用本接口。"""
    llm_configured = not isinstance(llm, PlaceholderLLM)
    return {
        "status": "ok",
        "llm_configured": llm_configured,
        # 注意：/config 推送成功后只重建全局 llm、不回写 _config，
        # 因此运行中切换配置后 provider 字段可能滞后，仅供 C# 日志参考。
        "provider": _config.get("provider", "") if llm_configured else "",
    }


@app.post("/chat", response_model=ChatResponse)
async def chat_endpoint(req: ChatRequest):
    print(f"[收到] {req.PlayerName} / 体力 {req.Stamina} / 时间 {req.GameTime} / {req.Location} / Message={req.Message!r}")

    # 线程 ID：用玩家名隔离历史（未来可换成"存档名+玩家名"）
    config = {"configurable": {"thread_id": req.PlayerName}}

    # 构造本轮输入：messages 里只放玩家原话（历史由 Checkpointer 自动注入）
    player_state = {
        "player_name": req.PlayerName,
        "stamina": req.Stamina,
        "game_time": req.GameTime,
        "location": req.Location,
        "season": req.Season,
        "is_raining": req.IsRaining,
        "day_of_month": req.DayOfMonth,
        "money": req.Money,
        "health": req.Health,
        # 【扩展状态感知】第二批新增字段
        "current_tool": req.CurrentTool,
        "farming_level": req.FarmingLevel,
        "mining_level": req.MiningLevel,
        "days_played": req.DaysPlayed,
        # 【扩展状态感知】第二批 b 新增字段
        "inventory": req.Inventory,
        "npc_affection": req.NPCAffection,
        "current_quest": req.CurrentQuest,
    }

    # 调 LangGraph：retrieve → chat，Checkpointer 自动维护历史
    try:
        result = await graph.ainvoke(
            {
                "messages": [HumanMessage(content=req.Message)],
                "player_state": player_state,
                "context": "",
                "error": "",
            },
            config=config,
        )
    except Exception:
        print(f"[错误] 图执行失败：\n{traceback.format_exc()}")
        return ChatResponse(
            reply="AI 暂时不可用：内部错误，请检查后端日志。",
            emotion="error",
        )

    # 按图里记录的错误类型给不同提示
    if result.get("error") == "retrieval_failed":
        return ChatResponse(
            reply="AI 暂时不可用：知识库检索失败，请检查后端日志。",
            emotion="error",
        )
    if result.get("error") == "llm_failed":
        return ChatResponse(
            reply="AI 暂时不可用：模型调用失败，请检查 API Key、网络或 Ollama 服务。",
            emotion="error",
        )

    # 成功：最后一条 AI 消息就是回复
    reply = result["messages"][-1].content
    return ChatResponse(reply=reply.strip(), emotion="happy")


@app.post("/config")
async def update_config(cfg: ConfigRequest):
    """接收 C# 从游戏里推来的配置：写文件 + 重建 LLM + 重新注入 graph"""
    # 1. 校验
    if cfg.provider not in ["ollama", "deepseek", "qwen", "zhipu", "kimi"]:
        raise HTTPException(400, f"不支持的 provider: {cfg.provider}")
    if cfg.provider != "ollama" and not cfg.api_key.strip():
        raise HTTPException(400, f"provider={cfg.provider} 时必须填 api_key")
    if not cfg.model.strip():
        raise HTTPException(400, "model 不能为空")
    if not (0.0 <= cfg.temperature <= 2.0):
        raise HTTPException(400, "temperature 必须在 0.0 到 2.0 之间")

    # 2. 写文件（temperature 一起落盘，下次启动直接生效）
    save_config({
        "provider": cfg.provider,
        "api_key": cfg.api_key,
        "model": cfg.model,
        "temperature": cfg.temperature,
    })

    # 3. 重建 LLM
    try:
        new_llm = create_llm({
            "provider": cfg.provider,
            "api_key": cfg.api_key,
            "model": cfg.model,
            "temperature": cfg.temperature,
        })
    except Exception as e:
        print(f"[错误] 重建 LLM 失败：\n{traceback.format_exc()}")
        raise HTTPException(400, f"配置无效，LLM 创建失败：{e}")

    # 4. 更新全局 + 重新注入 graph（graph.py 的 _llm 是模块级变量，重新 init 即可，不用重建图）
    global llm
    llm = new_llm
    init_agent(llm=new_llm, retriever=retriever, system_prompt=SYSTEM_PROMPT)

    print(f"[LLM] 已切换为 provider={cfg.provider}, model={cfg.model}, temperature={cfg.temperature}")
    return {"status": "ok", "provider": cfg.provider, "model": cfg.model, "temperature": cfg.temperature}


# ========== 程序入口（打包友好）==========
# 必须放在所有路由注册之后：uvicorn.run() 会阻塞在本行，写在中间会导致
# 后面的路由定义执行不到。PyInstaller 打包成 exe 后，双击或被 C# Mod
# 拉起时直接运行本文件即可，无需命令行参数（uvicorn 命令行只用于开发调试）。
if __name__ == "__main__":
    import uvicorn

    # host 固定 127.0.0.1：后端只服务本机的游戏 Mod，不暴露到局域网
    uvicorn.run(app, host="127.0.0.1", port=8000)