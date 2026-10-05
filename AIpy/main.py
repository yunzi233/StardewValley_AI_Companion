from fastapi import FastAPI
from pydantic import BaseModel

app = FastAPI(title="Stardew AI Companion")


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

    if req.Stamina < 50:
        return ChatResponse(
            reply=f"⚠️ {req.PlayerName}，体力只剩 {req.Stamina:.0f} 了，快回农场睡觉！",
            emotion="worried",
        )

    return ChatResponse(
        reply=f"你好呀 {req.PlayerName}！现在是 {req.GameTime}，你在 {req.Location}，体力 {req.Stamina:.0f}。"
    )