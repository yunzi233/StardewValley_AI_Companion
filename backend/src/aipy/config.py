"""项目级常量：集中管理嵌入模型、集合名、检索参数、路径。

改这里一处，main.py 和 build_knowledge_base.py 同时生效，
避免出现"改了一处忘了另一处"导致向量维度不匹配/集合找不到的坑。
"""
import sys
from pathlib import Path

# 路径模式：打包后（PyInstaller）用 exe 所在目录作为项目根；
# 开发模式用源码目录（src/aipy/ 上溯两级到 backend/）。
# PyInstaller 下 __file__ 不再指向源码目录（指向临时解包目录），
# 所有从 __file__ 派生的路径都会错位，必须切换到 sys.executable。
if getattr(sys, "frozen", False):
    # PyInstaller 打包模式：sys.executable 是 exe 的绝对路径，
    # 玩家修改 config.json、增删 chroma_db 等都在 exe 同级目录进行
    PROJECT_ROOT = Path(sys.executable).resolve().parent
else:
    # 开发模式：parents[0]=aipy/, parents[1]=src/, parents[2]=backend/
    PROJECT_ROOT = Path(__file__).resolve().parents[2]

# ---- 后端配置文件（含 API Key）----
# 打包模式下放 exe 同级目录，玩家好找、好改、好备份；
# 开发模式下等同于 backend/config.json（路径已从 src/aipy/ 统一迁移到根）。
CONFIG_FILE = PROJECT_ROOT / "config.json"

# ---- 向量库 ----
CHROMA_DIR = PROJECT_ROOT / "chroma_db"
COLLECTION_NAME = "stardew_wiki"

# ---- 嵌入模型本地缓存（fastembed 的 cache_dir）----
# 打包分发：把已下载的模型放进 models/ 随包带走，运行时零下载、完全离线；
# 开发模式：该目录为空时自动下载到这里（代替原来落在系统临时目录、易被清理的缓存）
MODELS_DIR = PROJECT_ROOT / "models"

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
