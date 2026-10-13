"""LangGraph 对话图：retrieve → chat。
   由 main.py 在启动时注入 llm / retriever / system_prompt。"""
import traceback
from langgraph.graph import StateGraph, START, END
import aiosqlite
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from langchain_core.messages import SystemMessage, HumanMessage
from aipy.config import CHECKPOINT_DB
from .state import State

# 由 init_agent 注入的依赖（保持简单，GMCM 阶段再改成 getter）
_llm = None
_retriever = None
_system_prompt = ""


def init_agent(llm, retriever, system_prompt):
    """main.py 启动时调用一次，注入依赖。"""
    global _llm, _retriever, _system_prompt
    _llm = llm
    _retriever = retriever
    _system_prompt = system_prompt


async def retrieve_node(state: State):
    """检索知识库；用本轮玩家原话做 query。"""
    # state["messages"][-1] 是本轮输入的 HumanMessage（玩家原话）
    query = state["messages"][-1].content
    try:
        docs = await _retriever.ainvoke(query)
        context = "\n\n".join(d.page_content for d in docs)
        print(f"[检索] 召回 {len(docs)} 块")
        return {"context": context, "error": ""}
    except Exception:
        print(f"[错误] 检索失败：\n{traceback.format_exc()}")
        return {"context": "", "error": "retrieval_failed"}


async def chat_node(state: State):
    """拼 messages 调 LLM；把本轮完整状态+资料注入 user_msg。"""
    ps = state["player_state"]
    # 季节翻译
    season_cn = {"spring": "春季", "summer": "夏季",
                 "fall": "秋季", "winter": "冬季"}.get(ps.get("season", ""), ps.get("season", ""))
    weather_text = "正在下雨" if ps.get("is_raining") else "天气晴朗"

    # 本轮玩家原话
    current_question = state["messages"][-1].content
    # 之前的历史（不含本轮）——只带最近 20 条（约 10 轮对话），
    # 更早的历史仍完整保留在 SQLite 里（不丢数据），只是不再塞给 LLM。
    # 取舍：控制 token 成本（云端省钱 / 本地小模型不超上下文）vs 保留完整历史。
    history = state["messages"][-21:-1]

    user_msg = (
        f"玩家{ps['player_name']}的游戏状态：\n"
        f"- 地点：{ps['location']}\n"
        f"- 季节：{season_cn}，第 {ps['day_of_month']} 天\n"
        f"- 天气：{weather_text}\n"
        f"- 时间：{ps['game_time']}\n"
        f"- 体力：{ps['stamina']:.0f} / 270\n"
        f"- 血量：{ps['health']} / 100\n"
        f"- 金币：{ps['money']}\n\n"
        f"【玩家的问题】\n{current_question}\n\n"
        f"【参考资料】\n{state['context']}\n\n"
        f"请结合游戏状态和参考资料回答玩家的问题。"
    )

    try:
        response = await _llm.ainvoke(
            [SystemMessage(content=_system_prompt)] + history + [HumanMessage(content=user_msg)]
        )
        return {"messages": [response], "error": ""}
    except Exception:
        print(f"[错误] LLM 调用失败：\n{traceback.format_exc()}")
        return {"error": "llm_failed"}


def _after_retrieve(state: State):
    """检索失败就跳过 chat，直接结束。"""
    return END if state.get("error") == "retrieval_failed" else "chat"


def build_graph():
    # 确保 data/ 目录存在（数据库文件不在 Git 里，换机后首次运行需自建）
    CHECKPOINT_DB.parent.mkdir(parents=True, exist_ok=True)

    # 用 AsyncSqliteSaver（同步版 SqliteSaver 不支持 async，实测会抛
    # NotImplementedError——本图全程走 await graph.ainvoke）。
    # aiosqlite.connect() 不 await 直接传入即可（懒连接），因此
    # build_graph 保持同步签名，main.py 无需改动。
    conn = aiosqlite.connect(str(CHECKPOINT_DB))
    checkpointer = AsyncSqliteSaver(conn)

    builder = StateGraph(State)
    builder.add_node("retrieve", retrieve_node)
    builder.add_node("chat", chat_node)
    builder.add_edge(START, "retrieve")
    builder.add_conditional_edges("retrieve", _after_retrieve, {"chat": "chat", END: END})
    builder.add_edge("chat", END)
    # SQLite Checkpointer：对话历史按 thread_id 落盘到 data/checkpoints.db，
    # 重启后端后历史仍在（MemorySaver 时代会清空）
    return builder.compile(checkpointer=checkpointer)