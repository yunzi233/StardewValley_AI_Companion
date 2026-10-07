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

【重要规则】
1. 当玩家的问题涉及星露谷的游戏知识（作物、鱼、礼物、节日等），
   你必须**严格依据下方【参考资料】回答**，不能编造。
2. 如果【参考资料】里没有相关信息，坦率说"这个我不太清楚"，
   不要用你自己的知识硬答。
3. 与游戏知识无关的闲聊，可以自由发挥。"""


# ========== 数据模型 ==========
class ChatRequest(BaseModel):
    PlayerName: str = "农夫"
    Stamina: float = 100.0
    GameTime: int = 600
    Location: str = "Farm"
    Message: str = ""          # ← 新增

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
    user_msg = (
        f"玩家{req.PlayerName}在{req.Location}，"
        f"时间是{req.GameTime}，体力{req.Stamina:.0f}。\n\n"
        f"【玩家的问题】\n{req.Message}\n\n"
        f"【参考资料】\n{context}\n\n"
        f"请根据参考资料回答玩家的问题。"
    )

    # ---- 3. 发给 LLM ----
    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM_PROMPT),
        HumanMessage(content=user_msg),
    ])

    return ChatResponse(reply=response.content.strip(), emotion="happy")