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
    """检索知识库；用本轮玩家原话 + 当前状态（季节/天气/地点）做 query。"""
    ps = state["player_state"]
    current_question = state["messages"][-1].content
    # 把季节/天气/地点拼进检索 query，提升与状态相关的召回：
    # 例如玩家只问"钓什么鱼"时，夏季晴天的上下文能帮助召回对应季节/天气的鱼块。
    season_cn = {"spring": "春季", "summer": "夏季",
                 "fall": "秋季", "winter": "冬季"}.get(ps.get("season", ""), "")
    weather = "下雨" if ps.get("is_raining") else "晴天"
    location = ps.get("location", "")
    context_tag = " ".join(t for t in [season_cn, weather, location] if t)
    query = f"[{context_tag}] {current_question}" if context_tag else current_question
    try:
        docs = await _retriever.ainvoke(query)
        context = "\n\n".join(d.page_content for d in docs)
        print(f"[检索] query={query!r}，召回 {len(docs)} 块")
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
    # 工具名翻译：游戏内原名 → 中文；映射表没有的工具直接显示原名。
    # 空手时 current_tool 为 ""，状态块里不写工具行。
    tool_cn = {"Axe": "斧头", "Pickaxe": "镐子", "Hoe": "锄头", "WateringCan": "水壶",
               "Scythe": "镰刀"}.get(ps.get("current_tool", ""), ps.get("current_tool", ""))
    tool_line = f"- 工具：{tool_cn}\n" if ps.get("current_tool") else ""
    # 背包 / NPC 好感 / 当前任务：空值不显示该行
    inv_line = f"- 背包：{ps.get('inventory', '')}\n" if ps.get("inventory") else ""
    npc_line = f"- NPC 好感：{ps.get('npc_affection', '')}\n" if ps.get("npc_affection") else ""
    quest_line = f"- 当前任务：{ps.get('current_quest', '')}\n" if ps.get("current_quest") else ""

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
        f"{tool_line}"
        f"- 技能：耕种 Lv.{ps.get('farming_level', 0)} / 采矿 Lv.{ps.get('mining_level', 0)}\n"
        f"{inv_line}"
        f"{npc_line}"
        f"{quest_line}"
        f"- 体力：{ps['stamina']:.0f} / 270\n"
        f"- 血量：{ps['health']} / 100\n"
        f"- 金币：{ps['money']}\n"
        f"- 进度：存档已玩 {ps.get('days_played', 0)} 天\n\n"
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