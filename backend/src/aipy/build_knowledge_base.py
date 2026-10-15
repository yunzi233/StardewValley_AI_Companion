"""从 wiki_data/ 读取 Markdown 文档，切块 + 向量化，存进 chroma_db/"""
import shutil

from langchain_chroma import Chroma
from langchain_text_splitters import MarkdownHeaderTextSplitter

# 路径、模型名、集合名都从 aipy.config 统一读取，
# 与 main.py 共享同一份配置，改一处两边同时生效。
from aipy.config import CHROMA_DIR, COLLECTION_NAME, EMBEDDING_MODEL, WIKI_DATA_DIR
from aipy.embeddings import FastEmbedEmbeddings


def main():
    # 1. 读取所有 .md 文件，按 ## 切块
    splitter = MarkdownHeaderTextSplitter(
        headers_to_split_on=[("##", "section")],
        strip_headers=False,   # 保留 ## 标题在正文里（提高检索命中率）
    )

    all_docs = []
    for md_file in sorted(WIKI_DATA_DIR.glob("*.md")):
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
    print(f"\n正在向量化（用 {EMBEDDING_MODEL}，第一次会下载模型，稍慢）...")
    embeddings = FastEmbedEmbeddings(model_name=EMBEDDING_MODEL)
    vectorstore = Chroma.from_documents(
        documents=all_docs,
        embedding=embeddings,
        persist_directory=str(CHROMA_DIR),
        collection_name=COLLECTION_NAME,
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