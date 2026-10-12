import traceback
from pathlib import Path

from fastapi import FastAPI
from pydantic import BaseModel

from langchain_chroma import Chroma
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_ollama import OllamaEmbeddings
from aipy.config import CHROMA_DIR, COLLECTION_NAME, EMBEDDING_MODEL, OLLAMA_BASE_URL, TOP_K
from aipy.llm_factory import load_config, create_llm
from aipy.agent.graph import init_agent, build_graph

app = FastAPI(title="Stardew AI Companion")

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


class ChatResponse(BaseModel):
    reply: str
    emotion: str = "happy"

# ========== 启动时加载向量库（只做一次）==========
print("正在加载向量库...")
embeddings = OllamaEmbeddings(model=EMBEDDING_MODEL, base_url=OLLAMA_BASE_URL)
vectorstore = Chroma(
    persist_directory=str(CHROMA_DIR),
    embedding_function=embeddings,
    collection_name=COLLECTION_NAME,
)
retriever = vectorstore.as_retriever(search_kwargs={"k": TOP_K})
print("向量库加载完成")

# ========== 对话模型 ==========
_config = load_config()
llm = create_llm(_config)
print(f"[LLM] provider={_config['provider']}, model={_config['model']}")

# ========== 初始化 LangGraph 对话图 ==========
init_agent(llm=llm, retriever=retriever, system_prompt=SYSTEM_PROMPT)
graph = build_graph()

# ========== 接口 ==========
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
            reply="AI 暂时不可用：知识库检索服务（Ollama）可能没启动，请检查后重试。",
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