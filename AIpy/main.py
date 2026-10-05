from fastapi import FastAPI
from pydantic import BaseModel
from langchain_ollama import ChatOllama
from langchain_core.messages import HumanMessage, SystemMessage

app = FastAPI(title="Stardew AI Companion")

# 本地 Ollama 模型
llm = ChatOllama(model="qwen2.5:3b", temperature=0.7)

SYSTEM_PROMPT = """你是一个星露谷物语里的 AI 伴侣，陪玩家一起冒险。
你说话亲切、简短（1-2 句），偶尔吐槽，偶尔提醒。
绝对不能编造游戏里不存在的信息。"""


class ChatRequest(BaseModel):
    PlayerName: str = "农夫"
    Stamina: float = 100.0
    GameTime: int = 600
    Location: str = "Farm"


class ChatResponse(BaseModel):
    reply: str
    emotion: str = "happy"


@app.post("/chat", response_model=ChatResponse)
async def chat_endpoint(req: ChatRequest):
    print(f"[收到] {req.PlayerName} / 体力 {req.Stamina} / 时间 {req.GameTime} / {req.Location}")

    # 把游戏状态拼成给 AI 的提示
    user_msg = (
        f"玩家{req.PlayerName}现在在{req.Location}，"
        f"游戏时间是{req.GameTime}，体力还有{req.Stamina:.0f}。"
        f"请用一句话跟玩家打个招呼或者提醒点什么。"
    )

    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM_PROMPT),
        HumanMessage(content=user_msg),
    ])

    return ChatResponse(reply=response.content.strip(), emotion="happy")