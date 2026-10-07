"""从 wiki_data/ 读取 Markdown 文档，切块 + 向量化，存进 chroma_db/"""
import shutil
from pathlib import Path

from langchain_chroma import Chroma
from langchain_ollama import OllamaEmbeddings
from langchain_text_splitters import MarkdownHeaderTextSplitter

# 路径（脚本在 src/aipy/ 下，用 parents[2] 回到项目根目录 AIpy/）
PROJECT_DIR = Path(__file__).resolve().parents[2]
WIKI_DIR = PROJECT_DIR / "wiki_data"
CHROMA_DIR = PROJECT_DIR / "chroma_db"


def main():
    # 1. 读取所有 .md 文件，按 ## 切块
    splitter = MarkdownHeaderTextSplitter(
        headers_to_split_on=[("##", "section")],
        strip_headers=False,   # 保留 ## 标题在正文里（提高检索命中率）
    )

    all_docs = []
    for md_file in sorted(WIKI_DIR.glob("*.md")):
        text = md_file.read_text(encoding="utf-8")
        chunks = splitter.split_text(text)
        for c in chunks:
            c.metadata["source"] = md_file.name
        all_docs.extend(chunks)
        print(f"  {md_file.name}: {len(chunks)} 块")

    print(f"\n总共 {len(all_docs)} 个块")

    # 2. 向量化 + 存库（先删旧库，避免重复追加）
    if CHROMA_DIR.exists():
        shutil.rmtree(CHROMA_DIR)
    print("\n正在向量化（用 bge-m3，第一次会比较慢）...")
    embeddings = OllamaEmbeddings(model="bge-m3")
    vectorstore = Chroma.from_documents(
        documents=all_docs,
        embedding=embeddings,
        persist_directory=str(CHROMA_DIR),
        collection_name="stardew_wiki",
    )
    print(f"入库完成 → {CHROMA_DIR}")

    # 3. 快速自测
    print("\n[自测] 问：'夏季晴天能钓到什么鱼？'")
    results = vectorstore.similarity_search("夏季晴天能钓到什么鱼？", k=2)
    for i, doc in enumerate(results, 1):
        print(f"\n--- 命中 {i}（来源：{doc.metadata.get('source')}）---")
        print(doc.page_content[:200] + "...")


if __name__ == "__main__":
    main()