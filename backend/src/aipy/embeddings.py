"""本地嵌入封装：用 fastembed（ONNX Runtime）替代原来的 OllamaEmbeddings。

为什么自写而不直接用 langchain-community 的 FastEmbedEmbeddings：
路线 2 目标是面向玩家打包，尽量少引依赖；这里只需要 Embeddings 接口的两个
方法，几十行即可，没必要为一个小功能拖进庞大的 langchain-community。

模型 BAAI/bge-small-zh-v1.5：中文小模型，向量维度 512（旧 bge-m3 是 1024）。
首次运行会下载 ONNX int8 模型（~23MB）到本地缓存，之后完全离线可用。
换模型 = 换维度 = 必须重建 chroma_db，否则新旧向量无法比较（见 NOTES 踩坑 12）。
"""
from fastembed import TextEmbedding
from langchain_core.embeddings import Embeddings


class FastEmbedEmbeddings(Embeddings):
    """LangChain Embeddings 的最小实现，底层用 fastembed.TextEmbedding。"""

    def __init__(self, model_name: str):
        self._model = TextEmbedding(model_name=model_name)

    def embed_documents(self, texts: list[str]) -> list[list[float]]:
        # 建库/索引侧：文档块 → 向量
        return [vec.tolist() for vec in self._model.embed(texts)]

    def embed_query(self, text: str) -> list[float]:
        # 检索侧：玩家问题 → 向量（BGE 的 query 指令前缀由 fastembed 内部处理）
        return next(iter(self._model.query_embed(text))).tolist()
