from typing import Annotated
from typing_extensions import TypedDict
from langgraph.graph.message import add_messages


class State(TypedDict):
    """LangGraph 的状态。
    messages：用 add_messages reducer 自动累积对话历史（Checkpointer 持久化）
    player_state：本轮游戏状态，每次覆盖（不累积）
    context：本轮 RAG 检索结果，每次覆盖
    error：错误标记，"retrieval_failed" / "llm_failed" / 空
    """
    messages: Annotated[list, add_messages]
    player_state: dict
    context: str
    error: str