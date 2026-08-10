import "./styles.css";
import packageInfo from "../package.json";
import { HttpKnowledgeProvider } from "./providers/http-provider";
import type { KnowledgeProvider } from "./providers/knowledge-provider";
import { TauriKnowledgeProvider } from "./providers/tauri-provider";
import { searchKnowledge } from "./search";
import type {
  KnowledgeDocument,
  KnowledgeBlock,
  KnowledgeInline,
  KnowledgeManifest,
  KnowledgeSearchIndex,
  KnowledgeTreeNode,
  SearchResult
} from "./types";

const isTauri = "__TAURI_INTERNALS__" in window;
const provider: KnowledgeProvider = isTauri
  ? new TauriKnowledgeProvider()
  : new HttpKnowledgeProvider();

let manifest: KnowledgeManifest | null = null;
let tree: KnowledgeTreeNode | null = null;
let searchIndex: KnowledgeSearchIndex | null = null;
let documentsById = new Map<string, KnowledgeDocument>();
let activeDocumentId: string | null = null;
let searchSequence = 0;

interface SearchReturnState {
  query: string;
  results: SearchResult[];
  scrollTop: number;
}

let searchReturnState: SearchReturnState | null = null;

const app = document.querySelector<HTMLDivElement>("#app")!;
app.innerHTML = `
  <div class="shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">F</div>
        <div>
          <div class="brand-title">飞书知识库离线阅读器</div>
          <div class="brand-subtitle">Feishu Wiki Reader</div>
        </div>
      </div>
      <button id="open-knowledge" class="open-button" type="button">打开离线知识库</button>
      <div class="tree-header">
        <span>知识库目录</span>
        <span id="doc-count" class="tree-count"></span>
      </div>
      <nav id="tree" class="tree" aria-label="知识库目录"></nav>
      <div class="reader-version">版本 ${escapeHtml(packageInfo.version)}</div>
    </aside>
    <main class="main">
      <header class="topbar">
        <label class="search-box">
          <span class="search-icon">⌕</span>
          <input id="search" type="search" autocomplete="off"
                 placeholder="搜索标题或正文（中文全文搜索建议至少输入 2 个字）" />
          <kbd>Ctrl K</kbd>
        </label>
        <div id="knowledge-meta" class="knowledge-meta"></div>
      </header>
      <section id="content" class="content"></section>
    </main>
  </div>`;

const openButton = document.querySelector<HTMLButtonElement>("#open-knowledge")!;
const searchInput = document.querySelector<HTMLInputElement>("#search")!;
const treeElement = document.querySelector<HTMLElement>("#tree")!;
const contentElement = document.querySelector<HTMLElement>("#content")!;
const metaElement = document.querySelector<HTMLElement>("#knowledge-meta")!;
const countElement = document.querySelector<HTMLElement>("#doc-count")!;

openButton.addEventListener("click", async () => {
  try {
    if (await provider.chooseKnowledge()) {
      await loadKnowledge();
    }
  } catch (error) {
    showError(error);
  }
});

let searchTimer: number | undefined;
searchInput.addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(() => void runSearch(), 130);
});

document.addEventListener("keydown", event => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
    event.preventDefault();
    searchInput.focus();
    searchInput.select();
  }
});

treeElement.addEventListener("click", event => {
  const button = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-document-id]");
  if (button?.dataset.documentId) {
    ++searchSequence;
    searchReturnState = null;
    searchInput.value = "";
    void showDocument(button.dataset.documentId);
  }
});

contentElement.addEventListener("click", event => {
  const backToResults = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-back-to-results]");
  if (backToResults) {
    restoreSearchResults();
    return;
  }

  const openOriginal = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-open-original]");
  if (openOriginal?.dataset.openOriginal) {
    void provider.openOriginal(openOriginal.dataset.openOriginal).catch(showError);
  }
  const pageLink = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-page-id]");
  if (pageLink?.dataset.pageId) {
    const targetId = pageLink.dataset.pageId;
    const anchor = pageLink.dataset.anchor;
    if (targetId === activeDocumentId && anchor) {
      document.getElementById(blockAnchor(anchor))?.scrollIntoView({ behavior: "smooth", block: "start" });
    } else {
      ++searchSequence;
      searchReturnState = null;
      searchInput.value = "";
      void showDocument(targetId, undefined, anchor);
    }
    return;
  }
  const result = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-result-id]");
  if (result?.dataset.resultId) {
    if (searchReturnState) {
      searchReturnState.scrollTop = contentElement.scrollTop;
    }
    ++searchSequence;
    searchInput.value = "";
    void showDocument(result.dataset.resultId, searchReturnState?.query);
  }
});

async function initialize(): Promise<void> {
  showWelcome();
  try {
    if (await provider.tryLoadDefault()) {
      await loadKnowledge();
    }
  } catch {
    showWelcome();
  }
}

async function loadKnowledge(): Promise<void> {
  setBusy(true);
  try {
    ++searchSequence;
    searchReturnState = null;
    [manifest, tree, searchIndex] = await Promise.all([
      provider.loadManifest(),
      provider.loadTree(),
      provider.loadSearchIndex()
    ]);
    if (manifest.format !== "feishu-offline-knowledge" || ![1, 2, 3].includes(manifest.version)) {
      throw new Error("离线知识库格式不受当前阅读器支持。");
    }
    documentsById = new Map(manifest.documents.map(document => [document.id, document]));
    renderTree();
    countElement.textContent = String(manifest.documents.length);
    metaElement.textContent = `${manifest.name} · ${formatGeneratedTime(manifest.generatedUtc)}`;
    searchInput.value = "";
    const first = manifest.documents.find(document => document.pagePath) ?? manifest.documents[0];
    if (first) {
      await showDocument(first.id);
    } else {
      showEmptyKnowledge();
    }
  } finally {
    setBusy(false);
  }
}

function renderTree(): void {
  if (!tree) return;
  treeElement.replaceChildren(renderTreeNode(tree, 0, true));
}

function renderTreeNode(node: KnowledgeTreeNode, depth: number, expanded = false): HTMLElement {
  if (node.type === "page") {
    return renderPageTreeNode(node, depth, expanded);
  }

  if (node.type === "document") {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `tree-document${node.documentId === activeDocumentId ? " active" : ""}`;
    button.dataset.documentId = node.documentId ?? "";
    button.style.setProperty("--depth", String(depth));
    button.innerHTML = `<span class="file-dot ${escapeAttribute(node.kind ?? "file")}"></span><span>${escapeHtml(node.title)}</span>`;
    return button;
  }

  const details = document.createElement("details");
  details.className = "tree-folder";
  details.open = expanded || depth < 1;
  const summary = document.createElement("summary");
  summary.style.setProperty("--depth", String(depth));
  summary.textContent = node.title;
  details.append(summary);
  const children = document.createElement("div");
  for (const child of node.children) {
    children.append(renderTreeNode(child, depth + 1));
  }
  details.append(children);
  return details;
}

function renderPageTreeNode(node: KnowledgeTreeNode, depth: number, expanded = false): HTMLElement {
  const container = document.createElement("div");
  container.className = "tree-page";

  const row = document.createElement("div");
  row.className = "tree-page-row";
  row.style.setProperty("--depth", String(depth));
  container.append(row);

  const children = document.createElement("div");
  children.className = "tree-page-children";
  const hasChildren = node.children.length > 0;
  let isExpanded = hasChildren && expanded;

  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "tree-page-toggle";
  toggle.title = hasChildren ? "展开或收起子页面" : "没有子页面";
  toggle.disabled = !hasChildren;
  row.append(toggle);

  const setExpanded = (next: boolean): void => {
    isExpanded = hasChildren && next;
    children.hidden = !isExpanded;
    toggle.setAttribute("aria-expanded", String(isExpanded));
    toggle.textContent = hasChildren ? (isExpanded ? "▾" : "▸") : "";
  };
  toggle.addEventListener("click", () => setExpanded(!isExpanded));

  const title = document.createElement("button");
  title.type = "button";
  title.className = `tree-page-title${node.documentId === activeDocumentId ? " active" : ""}${node.documentId ? "" : " navigation-only"}`;
  if (node.documentId) {
    title.dataset.documentId = node.documentId;
  } else if (hasChildren) {
    title.addEventListener("click", () => setExpanded(!isExpanded));
  } else {
    title.disabled = true;
  }
  title.innerHTML = node.documentId
    ? `<span class="file-dot ${escapeAttribute(node.kind ?? "file")}"></span><span>${escapeHtml(node.title)}</span>`
    : `<span class="page-node-dot"></span><span>${escapeHtml(node.title)}</span>`;
  row.append(title);

  for (const child of node.children) {
    children.append(renderTreeNode(child, depth + 1));
  }
  container.append(children);
  setExpanded(isExpanded);
  return container;
}

async function showDocument(id: string, returnToSearchQuery?: string, anchor?: string): Promise<void> {
  const knowledgeDocument = documentsById.get(id);
  if (!knowledgeDocument) return;
  const sequence = searchSequence;
  activeDocumentId = id;
  updateTreeSelection(id);
  contentElement.scrollTop = 0;
  contentElement.innerHTML = articleHeader(knowledgeDocument, returnToSearchQuery);

  if (!knowledgeDocument.pagePath) {
    contentElement.insertAdjacentHTML("beforeend", `
      <div class="file-placeholder">
        <div class="file-placeholder-icon">${kindLabel(knowledgeDocument.kind)}</div>
        <h2>${escapeHtml(knowledgeDocument.title)}</h2>
        <p>阅读器保留了此类文件，但暂不在窗口内转换显示。</p>
        ${knowledgeDocument.originalPath ? `<button class="primary-action" data-open-original="${escapeAttribute(knowledgeDocument.originalPath)}">打开原始文件</button>` : ""}
      </div>`);
    return;
  }

  try {
    const page = await provider.loadPage(knowledgeDocument.pagePath);
    if (sequence !== searchSequence || activeDocumentId !== id) return;
    const article = documentElement("article", "article-body");
    article.innerHTML = page.blocks
      ? page.blocks.map(renderKnowledgeBlock).join("")
      : sanitizeArticleHtml(page.html ?? "");
    contentElement.append(article);
    await hydrateImages(article);
    if (anchor) {
      window.requestAnimationFrame(() =>
        document.getElementById(blockAnchor(anchor))?.scrollIntoView({ block: "start" }));
    }
  } catch (error) {
    if (sequence !== searchSequence || activeDocumentId !== id) return;
    const message = error instanceof Error ? error.message : String(error);
    contentElement.insertAdjacentHTML("beforeend", `
      <div class="error-state"><strong>无法读取内容</strong><p>${escapeHtml(message)}</p></div>`);
  }
}

function updateTreeSelection(id: string): void {
  treeElement.querySelectorAll<HTMLButtonElement>("button[data-document-id].active")
    .forEach(button => button.classList.remove("active"));
  const activeButton = [...treeElement.querySelectorAll<HTMLButtonElement>("button[data-document-id]")]
    .find(button => button.dataset.documentId === id);
  activeButton?.classList.add("active");
}

async function runSearch(): Promise<void> {
  const query = searchInput.value.trim();
  const sequence = ++searchSequence;
  if (!query) {
    searchReturnState = null;
    if (activeDocumentId) await showDocument(activeDocumentId);
    return;
  }
  if (!manifest || !searchIndex) return;

  contentElement.innerHTML = `<div class="search-status">正在搜索“${escapeHtml(query)}”……</div>`;
  const results = await searchKnowledge(query, manifest.documents, searchIndex, provider);
  if (sequence !== searchSequence) return;
  renderSearchResults(query, results);
}

function renderSearchResults(query: string, results: SearchResult[], scrollTop = 0): void {
  searchReturnState = { query, results, scrollTop };
  const cards = results.map(result => `
    <button class="result-card" type="button" data-result-id="${escapeAttribute(result.document.id)}">
      <div class="result-title">${highlight(result.document.title, query)}</div>
      <div class="result-path">${escapeHtml(result.document.breadcrumb || manifest?.name || "")}</div>
      ${result.snippet ? `<div class="result-snippet">${highlight(result.snippet, query)}</div>` : ""}
      <span class="result-kind">${kindLabel(result.document.kind)}</span>
    </button>`).join("");
  contentElement.innerHTML = `
    <div class="search-results">
      <div class="results-heading">
        <div><strong>${results.length}</strong> 个结果</div>
        <span>搜索：${escapeHtml(query)}</span>
      </div>
      ${cards || `<div class="empty-state">没有找到匹配内容。</div>`}
    </div>`;
  window.requestAnimationFrame(() => {
    contentElement.scrollTop = scrollTop;
  });
}

function restoreSearchResults(): void {
  if (!searchReturnState) return;
  ++searchSequence;
  const { query, results, scrollTop } = searchReturnState;
  searchInput.value = query;
  renderSearchResults(query, results, scrollTop);
}

function articleHeader(document: KnowledgeDocument, returnToSearchQuery?: string): string {
  return `
    <div class="article-header">
      ${returnToSearchQuery ? `
        <div class="search-return-row">
          <button class="back-to-results" type="button" data-back-to-results>
            <span aria-hidden="true">←</span> 返回搜索结果
          </button>
          <span class="search-return-query" title="${escapeAttribute(returnToSearchQuery)}">搜索：${escapeHtml(returnToSearchQuery)}</span>
        </div>` : ""}
      <div class="breadcrumbs">${escapeHtml(document.breadcrumb || manifest?.name || "")}</div>
      <div class="article-title-row">
        <h1>${escapeHtml(document.title)}</h1>
        ${document.originalPath ? `<button class="secondary-action" data-open-original="${escapeAttribute(document.originalPath)}">打开原文件</button>` : ""}
      </div>
    </div>`;
}

function renderKnowledgeBlock(block: KnowledgeBlock): string {
  const id = ` id="${escapeAttribute(blockAnchor(block.id))}"`;
  const content = renderInlines(block.inlines, block.text ?? "");
  const children = block.children?.map(renderKnowledgeBlock).join("") ?? "";
  switch (block.type) {
    case "paragraph":
      return `<p${id}>${content || "<br>"}</p>${children}`;
    case "heading": {
      const level = Math.min(6, Math.max(1, block.level ?? 2));
      return `<h${level}${id}>${content}</h${level}>${children}`;
    }
    case "bullet":
      return `<div class="block-list-item"${id}><span class="block-marker">•</span><div>${content}${children}</div></div>`;
    case "ordered":
      return `<div class="block-list-item"${id}><span class="block-marker">1.</span><div>${content}${children}</div></div>`;
    case "todo":
      return `<div class="block-list-item"${id}><span class="block-marker">${block.checked ? "☑" : "☐"}</span><div>${content}${children}</div></div>`;
    case "code":
      return `<pre${id}><code>${escapeHtml(block.text ?? "")}</code></pre>${children}`;
    case "quote":
    case "quoteContainer":
      return `<blockquote${id}>${content}${children}</blockquote>`;
    case "callout":
      return `<aside class="callout"${id}>${content}${children}</aside>`;
    case "divider":
      return `<hr${id}>`;
    case "image":
      return block.assetPath
        ? `<figure${id}><img data-asset-path="${escapeAttribute(block.assetPath)}" alt="${escapeAttribute(block.fileName ?? "图片")}"><figcaption>${escapeHtml(block.fileName ?? "")}</figcaption></figure>${children}`
        : `<div class="unsupported-block"${id}>图片资源未能下载</div>${children}`;
    case "file":
      return block.assetPath
        ? `<button class="attachment-card" type="button" data-open-original="${escapeAttribute(block.assetPath)}"${id}><span>附件</span><strong>${escapeHtml(block.fileName ?? "打开附件")}</strong></button>${children}`
        : `<div class="unsupported-block"${id}>附件资源未能下载：${escapeHtml(block.fileName ?? "未命名附件")}</div>${children}`;
    case "subpages":
      return block.links.length
        ? `<nav class="subpage-list"${id}><div class="block-label">子页面</div>${renderBlockLinks(block)}</nav>`
        : "";
    case "toc":
      return block.links.length
        ? `<nav class="page-toc"${id}><div class="block-label">本页目录</div>${renderBlockLinks(block)}</nav>`
        : "";
    case "table":
      return `<div class="normalized-table"${id}>${children}</div>`;
    case "tableCell":
      return `<div class="normalized-table-cell"${id}>${content}${children}</div>`;
    case "container":
      return `<div class="block-container"${id}>${content}${children}</div>`;
    default:
      return `<div class="unsupported-block"${id}><strong>暂不支持的内容块</strong><span>飞书块类型 ${block.sourceType ?? "未知"}</span>${content ? `<p>${content}</p>` : ""}${children}</div>`;
  }
}

function renderBlockLinks(block: KnowledgeBlock): string {
  return block.links.map(link => `
    <button type="button" class="page-link-card" data-page-id="${escapeAttribute(link.targetPageId)}"
            ${link.anchor ? `data-anchor="${escapeAttribute(link.anchor)}"` : ""}>
      <span>${escapeHtml(link.title)}</span><span aria-hidden="true">→</span>
    </button>`).join("");
}

function renderInlines(inlines: KnowledgeInline[] | undefined, fallback: string): string {
  if (!inlines?.length) return escapeHtml(fallback);
  return inlines.map(inline => {
    let value = escapeHtml(inline.text);
    if (inline.code) value = `<code>${value}</code>`;
    if (inline.bold) value = `<strong>${value}</strong>`;
    if (inline.italic) value = `<em>${value}</em>`;
    if (inline.underline) value = `<u>${value}</u>`;
    if (inline.strike) value = `<s>${value}</s>`;
    if (inline.targetPageId) {
      return `<button type="button" class="inline-page-link" data-page-id="${escapeAttribute(inline.targetPageId)}">${value}</button>`;
    }
    if (inline.url && /^(https?:|mailto:)/iu.test(inline.url)) {
      return `<a href="${escapeAttribute(inline.url)}" target="_blank" rel="noreferrer">${value}</a>`;
    }
    return value;
  }).join("");
}

async function hydrateImages(article: HTMLElement): Promise<void> {
  const images = [...article.querySelectorAll<HTMLImageElement>("img[data-asset-path]")];
  await Promise.all(images.map(async image => {
    const path = image.dataset.assetPath;
    if (!path) return;
    try {
      image.src = await provider.loadAssetData(path);
    } catch {
      image.replaceWith(Object.assign(document.createElement("div"), {
        className: "unsupported-block",
        textContent: "图片资源无法读取"
      }));
    }
  }));
}

function blockAnchor(id: string): string {
  return `block-${id.replace(/[^a-zA-Z0-9_-]/gu, "-")}`;
}

function showWelcome(): void {
  treeElement.innerHTML = `<div class="tree-empty">尚未打开知识库</div>`;
  contentElement.innerHTML = `
    <div class="welcome">
      <div class="welcome-mark">F</div>
      <h1>打开离线知识库</h1>
      <p>按飞书原有层级浏览文档，或使用标题和全文搜索快速找到内容。</p>
      ${provider.mode === "tauri" ? `<button id="welcome-open" class="primary-action" type="button">选择知识库目录</button>` : ""}
    </div>`;
  document.querySelector<HTMLButtonElement>("#welcome-open")?.addEventListener("click", () => openButton.click());
}

function showEmptyKnowledge(): void {
  contentElement.innerHTML = `<div class="empty-state">这个离线知识库中暂无文件。</div>`;
}

function showError(error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  contentElement.innerHTML = `<div class="error-state"><strong>无法读取内容</strong><p>${escapeHtml(message)}</p></div>`;
}

function setBusy(busy: boolean): void {
  openButton.disabled = busy;
  searchInput.disabled = busy;
}

function formatGeneratedTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "" : `更新于 ${date.toLocaleString("zh-CN")}`;
}

function kindLabel(kind: string): string {
  return ({ docx: "DOCX", pdf: "PDF", spreadsheet: "XLSX", image: "IMG", file: "FILE" } as Record<string, string>)[kind] ?? "FILE";
}

function documentElement(tag: string, className: string): HTMLElement {
  const element = document.createElement(tag);
  element.className = className;
  return element;
}

function sanitizeArticleHtml(html: string): string {
  const document = new DOMParser().parseFromString(html, "text/html");
  document.querySelectorAll("script, style, iframe, object, embed, form, input, button").forEach(node => node.remove());
  document.body.querySelectorAll("*").forEach(element => {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim().toLowerCase();
      if (name.startsWith("on") || (name === "href" && value.startsWith("javascript:"))) {
        element.removeAttribute(attribute.name);
      }
    }
  });
  return document.body.innerHTML;
}

function highlight(value: string, query: string): string {
  if (!query) return escapeHtml(value);
  const expression = new RegExp(escapeRegExp(query), "gi");
  let last = 0;
  let html = "";
  for (const match of value.matchAll(expression)) {
    const index = match.index ?? 0;
    html += escapeHtml(value.slice(last, index));
    html += `<mark>${escapeHtml(match[0])}</mark>`;
    last = index + match[0].length;
  }
  return html + escapeHtml(value.slice(last));
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  }[character] ?? character));
}

function escapeAttribute(value: string): string {
  return escapeHtml(value);
}

void initialize();
