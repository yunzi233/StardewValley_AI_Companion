from pathlib import Path

from fastapi import FastAPI
from pydantic import BaseModel

from langchain_chroma import Chroma
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_ollama import ChatOllama, OllamaEmbeddings

app = FastAPI(title="Stardew AI Companion")

# ========== 启动时加载向量库（只做一次，不要放在请求里）==========
CHROMA_DIR = Path(__file__).parent / "chroma_db"

print("正在加载向量库...")
embeddings = OllamaEmbeddings(model="bge-m3")
vectorstore = Chroma(
    persist_directory=str(CHROMA_DIR),
    embedding_function=embeddings,
    collection_name="stardew_wiki",
)
retriever = vectorstore.as_retriever(search_kwargs={"k": 3})   # 每次召回 3 块
print(f"向量库加载完成")

# 对话模型（保持你原来的）
llm = ChatOllama(model="qwen2.5:3b", temperature=0.7)


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
    Season: str = "spring"       # ← 新增
    IsRaining: bool = False      # ← 新增
    DayOfMonth: int = 1          # ← 新增
    Money: int = 500             # ← 新增
    Health: int = 100            # ← 新增

class ChatResponse(BaseModel):
    reply: str
    emotion: str = "happy"


# ========== 接口 ==========
@app.post("/chat", response_model=ChatResponse)
async def chat_endpoint(req: ChatRequest):
    print(f"[收到] {req.PlayerName} / 体力 {req.Stamina} / 时间 {req.GameTime} / {req.Location}")

    # ---- 1. 检索知识库（用玩家状态拼一个"查询意图"）----
    # 这里先用简单策略：把玩家状态也拼进查询里，让检索更贴合上下文
    # # 如果玩家带了消息，用它检索；否则回退到场景描述
    if req.Message:
        query = req.Message
    else:
        query = f"玩家在{req.Location}，游戏时间{req.GameTime}，体力{req.Stamina:.0f}"

    # 以后可以让 C# 传来"玩家实际说的话"，这里先用状态代替
    # 现在先假装玩家问的是通用问题，用当前场景做 query
    retrieved_docs = await retriever.ainvoke(query)
    context = "\n\n".join(doc.page_content for doc in retrieved_docs)

    print(f"[检索] 召回 {len(retrieved_docs)} 块")

    # ---- 2. 拼用户消息（含游戏状态 + 参考资料）----
    # 季节翻译
    season_cn = {
        "spring": "春季", "summer": "夏季",
        "fall": "秋季", "winter": "冬季"
    }.get(req.Season, req.Season)

    weather_text = "正在下雨" if req.IsRaining else "天气晴朗"

    user_msg = (
        f"玩家{req.PlayerName}的游戏状态：\n"
        f"- 地点：{req.Location}\n"
        f"- 季节：{season_cn}，第 {req.DayOfMonth} 天\n"
        f"- 天气：{weather_text}\n"
        f"- 时间：{req.GameTime}\n"
        f"- 体力：{req.Stamina:.0f} / 270\n"
        f"- 血量：{req.Health} / 100\n"
        f"- 金币：{req.Money}\n\n"
        f"【玩家的问题】\n{req.Message}\n\n"
        f"【参考资料】\n{context}\n\n"
        f"请结合游戏状态和参考资料回答玩家的问题。"
    )

    # ---- 3. 发给 LLM ----
    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM_PROMPT),
        HumanMessage(content=user_msg),
    ])

    return ChatResponse(reply=response.content.strip(), emotion="happy")