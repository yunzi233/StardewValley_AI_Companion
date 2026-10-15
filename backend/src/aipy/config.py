"""项目级常量：集中管理嵌入模型、集合名、检索参数、路径。

改这里一处，main.py 和 build_knowledge_base.py 同时生效，
避免出现"改了一处忘了另一处"导致向量维度不匹配/集合找不到的坑。
"""
from pathlib import Path

# 项目根目录（config.py 在 src/aipy/ 下，上溯两级到 backend/）
# parents[0]=aipy/, parents[1]=src/, parents[2]=backend/
PROJECT_ROOT = Path(__file__).resolve().parents[2]

# ---- 向量库 ----
CHROMA_DIR = PROJECT_ROOT / "chroma_db"
COLLECTION_NAME = "stardew_wiki"

# ---- 嵌入模型（fastembed 本地 ONNX，无需 Ollama 服务；与对话模型选型无关）----
# 换模型 = 换向量维度，必须重建 chroma_db（见 NOTES 踩坑 12）
EMBEDDING_MODEL = "BAAI/bge-small-zh-v1.5"

# ---- 检索参数 ----
TOP_K = 3

# ---- 知识库源文件目录 ----
WIKI_DATA_DIR = PROJECT_ROOT / "wiki_data"

# ---- 对话历史持久化文件（SQLite，AsyncSqliteSaver）----
# 放在 backend/data/ 下，不要混进 chroma_db/；文件本地生成，已 gitignore
CHECKPOINT_DB = PROJECT_ROOT / "data" / "checkpoints.db"
