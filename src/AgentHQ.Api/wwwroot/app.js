// Agent HQ web UI — plain ES modules, no build step, no external requests.
// Reads the same REST surface OpenClaw uses and follows GET /events with Last-Event-ID
// replay, falling back to ?sinceSeq= polling while the stream is down.

// Lifecycle order; also the secondary sort under a status sort and the "all" reading order.
const STATUS_ORDER = [
  "submitted", "refining", "in_review", "implementing", "implemented",
  "verifying", "verified", "done", "deferred", "abandoned",
];
const STATUS_LABEL = {
  submitted: "Submitted", refining: "Refining", in_review: "In review", implementing: "Implementing",
  implemented: "Implemented", verifying: "Verifying", verified: "Verified", done: "Done",
  deferred: "Deferred", abandoned: "Abandoned", fixing: "Fixing",
};
// Action repertoire with the states each action is legal from (mirrors PlanStateMachine;
// the server is still the authority and its refusals are shown verbatim).
const ACTIONS = [
  { id: "sendback", label: "Send back to refining", to: "refining", from: ["in_review"], needsNote: true },
  { id: "implemented", label: "Mark implemented", to: "implemented", from: ["implementing", "fixing"] },
  { id: "verify", label: "Enter verification", to: "verifying", from: ["implemented"] },
  { id: "pass", label: "Pass verification", to: "verified", from: ["verifying"] },
  { id: "fail", label: "Fail → fixing", to: "fixing", from: ["verifying"], needsNote: true },
  { id: "done", label: "Close out (done)", to: "done", from: ["verified"] },
  { id: "defer", label: "Defer", to: "deferred", from: ["submitted", "refining", "in_review", "implementing", "implemented", "verifying", "fixing", "done", "verified"], needsNote: true },
  { id: "reactivate", label: "Reactivate", to: "refining", from: ["deferred"] },
  { id: "abandon", label: "Abandon", to: "abandoned", from: ["submitted", "refining", "in_review", "implementing", "implemented", "verifying", "fixing", "deferred", "done", "verified"], needsNote: true },
];

const state = {
  plans: new Map(),
  repos: [],
  sessions: new Map(),
  health: null,
  lastSeq: 0,
  filter: "all",
  statusFilter: "",
  repoFilter: "",
  search: "",
  sort: "updated",
  dir: "desc",
  selected: null,
  unread: 0,
  stream: null,
  pollTimer: null,
  conn: "idle",
  tab: "content",
};

const $ = (sel) => document.querySelector(sel);
const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined) node.textContent = text;
  return node;
};

// ---------- api ----------

async function api(path, options = {}) {
  const res = await fetch(path, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options,
  });
  const text = await res.text();
  let body = null;
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }
  if (!res.ok) {
    const err = new Error((body && body.error) || `${res.status} ${res.statusText}`);
    err.status = res.status;
    err.body = body;
    throw err;
  }
  return body;
}

// ---------- formatting ----------

const ago = (iso) => {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";
  const s = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
};

const clock = (iso) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });

function toast(message, kind = "") {
  const t = $("#toast");
  t.textContent = message;
  t.className = `toast ${kind}`;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => t.classList.add("hidden"), kind === "error" ? 8000 : 3500);
}

// Minimal, safe markdown: escapes everything, supports headings, lists, checkboxes,
// fenced code, inline code, bold, links (same-site or https), and paragraphs.
function renderMarkdown(md) {
  const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const inline = (s) => esc(s)
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+|\/[^)\s]*)\)/g, '<a href="$2" rel="noopener">$1</a>');

  const out = [];
  let inCode = false, list = null, inTable = false;
  const closeList = () => { if (list) { out.push(`</${list}>`); list = null; } };
  const closeTable = () => { if (inTable) { out.push("</tbody></table>"); inTable = false; } };

  for (const raw of md.replace(/\r\n/g, "\n").split("\n")) {
    if (raw.trim().startsWith("```")) {
      closeList(); closeTable();
      out.push(inCode ? "</code></pre>" : "<pre><code>");
      inCode = !inCode;
      continue;
    }
    if (inCode) { out.push(esc(raw)); continue; }

    // The drawer already shows the plan name as its title, so the document's own h1 renders
    // as h2 and everything else keeps its relative level (## Goal → h2, ### L-1 → h3).
    const h = /^(#{1,4})\s+(.*)$/.exec(raw);
    if (h) { closeList(); closeTable(); const lvl = Math.min(h[1].length, 4); out.push(`<h${lvl}>${inline(h[2])}</h${lvl}>`); continue; }

    const task = /^(\s*)[-*]\s+\[([ xX])\]\s+(.*)$/.exec(raw);
    if (task) {
      if (list !== "ul") { closeList(); list = "ul"; out.push("<ul>"); }
      out.push(`<li class="task"><input type="checkbox" disabled ${task[2] !== " " ? "checked" : ""}> ${inline(task[3])}</li>`);
      continue;
    }
    const li = /^(\s*)[-*]\s+(.*)$/.exec(raw);
    if (li) {
      if (list !== "ul") { closeList(); list = "ul"; out.push("<ul>"); }
      out.push(`<li>${inline(li[2])}</li>`);
      continue;
    }
    if (/^\s*$/.test(raw)) { closeList(); closeTable(); continue; }

    if (raw.startsWith("|")) {
      closeList();
      const cells = raw.split("|").slice(1, -1).map((c) => c.trim());
      if (cells.every((c) => /^:?-{2,}:?$/.test(c))) continue;
      if (!inTable) { out.push("<table><tbody>"); inTable = true; }
      out.push(`<tr>${cells.map((c) => `<td>${inline(c)}</td>`).join("")}</tr>`);
      continue;
    }
    closeTable();
    out.push(`<p>${inline(raw)}</p>`);
  }
  if (inCode) out.push("</code></pre>");
  closeList(); closeTable();
  return out.join("\n");
}

// ---------- data loading ----------

async function loadHealth() {
  try {
    state.health = await api("/health");
    const ok = state.health.providerSchema === "ok";
    $("#health-dot").className = `dot ${ok ? "ok" : "warn"}`;
    $("#health-text").textContent =
      ok ? `zcode · schema ${String(state.health.providerDetail || "").replace(/[^0-9.]/g, "")}`
         : `provider ${state.health.providerSchema}`;
  } catch {
    $("#health-dot").className = "dot bad";
    $("#health-text").textContent = "service unreachable";
  }
}

async function loadRepos() {
  state.repos = await api("/repositories");
  const options = state.repos
    .map((r) => `<option value="${r.id}">${escapeHtml(r.rootPath)}${r.default ? " (default)" : ""}</option>`)
    .join("");
  $("#repo-filter").innerHTML = `<option value="">All repositories</option>${options}`;
  $("#repo-filter").value = state.repoFilter;
  $("#s-repo").innerHTML = options || `<option value="">register a repository first</option>`;
  const def = state.repos.find((r) => r.default) || state.repos[0];
  if (def) $("#s-repo").value = def.id;
  $("#repo-list").innerHTML = state.repos.length
    ? state.repos.map((r) =>
        `<li>${r.default ? '<span class="badge pri">default</span>' : ""}<code>${escapeHtml(r.rootPath)}</code></li>`).join("")
    : `<li class="muted">none registered yet (discovered from ZCode sessions automatically)</li>`;
}

async function loadPlans() {
  const query = state.repoFilter ? `?repo=${encodeURIComponent(state.repoFilter)}` : "";
  const [plans, ready, blocked] = await Promise.all([
    api(`/plans${query}`),
    api(`/plans?ready=true${state.repoFilter ? `&repo=${encodeURIComponent(state.repoFilter)}` : ""}`),
    api(`/plans?blocked=true${state.repoFilter ? `&repo=${encodeURIComponent(state.repoFilter)}` : ""}`),
  ]);
  state.ready = new Set(ready.map((p) => p.id));
  state.blockedSet = new Set(blocked.map((p) => p.id));
  for (const p of plans) state.plans.set(p.id, { ...state.plans.get(p.id), ...p });
  for (const id of [...state.plans.keys()]) if (!plans.some((p) => p.id === id)) state.plans.delete(id);
  renderTable();
  // A status change can change readiness or blocking; keep the cached sets current too.
  if (state.filter === "ready" || state.filter === "blocked") {
    const suffix = state.repoFilter ? `&repo=${encodeURIComponent(state.repoFilter)}` : "";
    const [r, b] = await Promise.all([
      api(`/plans?ready=true${suffix}`), api(`/plans?blocked=true${suffix}`),
    ]);
    state.ready = new Set(r.map((p) => p.id));
    state.blockedSet = new Set(b.map((p) => p.id));
    renderTable();
  }
  if (state.selected) await openPlan(state.selected, { keepTab: true, quiet: true });
}

async function loadSessions() {
  try {
    const sessions = await api("/sessions");
    state.sessions = new Map(sessions.map((s) => [s.id, s]));
    renderSessions();
  } catch { /* non-fatal */ }
}

async function refreshPlan(id) {
  try {
    const detail = await api(`/plans/${id}`);
    const list = (await api("/plans")).find((p) => p.id === id);
    if (list) state.plans.set(id, { ...list });
    return detail;
  } catch {
    state.plans.delete(id);
    return null;
  }
}

const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------- plans table ----------

function visiblePlans() {
  let plans = [...state.plans.values()];
  if (state.filter === "ready") {
    // Same definition as GET /plans?ready=true: the ready queue from the server.
    plans = plans.filter((p) => state.ready.has(p.id));
  } else if (state.filter === "blocked") {
    plans = plans.filter((p) => state.blockedSet.has(p.id));
  }
  // `implemented` is the verification queue: an implemented plan waits for a verifier.
  if (state.statusFilter) plans = plans.filter((p) => p.status === state.statusFilter);
  if (state.search) {
    const q = state.search.toLowerCase();
    plans = plans.filter((p) => `${p.name} ${p.slug}`.toLowerCase().includes(q));
  }
  return plans;
}

const SORT_KEYS = {
  name: (p) => (p.name || "").toLowerCase(),
  status: (p) => STATUS_ORDER.indexOf(p.status === "fixing" ? "implementing" : p.status),
  blocked: (p) => (p.blocked ? 0 : 1),
  progress: (p) => (p.progress && p.progress.total ? p.progress.checkedCount / p.progress.total : -1),
  priority: (p) => p.priority ?? 0,
  session: (p) => p.session || p.implementedBy || "",
  updated: (p) => new Date(p.updated).getTime() || 0,
};

function sortedPlans(plans) {
  const key = SORT_KEYS[state.sort] || SORT_KEYS.updated;
  const sign = state.dir === "asc" ? 1 : -1;
  return plans.sort((a, b) => {
    const ka = key(a), kb = key(b);
    const cmp = typeof ka === "string" ? ka.localeCompare(kb) : ka - kb;
    // Equal keys fall back to priority, then recency — stable and predictable.
    return sign * cmp || (b.priority ?? 0) - (a.priority ?? 0) || SORT_KEYS.updated(b) - SORT_KEYS.updated(a);
  });
}

function renderTable() {
  const plans = sortedPlans(visiblePlans());
  const body = $("#plans-body");
  body.innerHTML = "";
  for (const p of plans) body.append(planRow(p));
  $("#empty").classList.toggle("hidden", plans.length > 0);
  const total = state.plans.size;
  $("#result-count").textContent =
    plans.length === total ? `${total} plan${total === 1 ? "" : "s"}` : `${plans.length} of ${total} plans`;
  for (const th of document.querySelectorAll("#plans-table thead th")) {
    th.setAttribute("aria-sort", th.dataset.sort === state.sort
      ? (state.dir === "asc" ? "ascending" : "descending") : "none");
  }
}

function planRow(p) {
  const tr = el("tr", (p.blocked ? "blocked" : "") + (p.id === state.selected ? " selected" : ""));
  tr.dataset.id = p.id;
  tr.tabIndex = 0;
  tr.setAttribute("aria-label", `${p.name} (${STATUS_LABEL[p.status] || p.status})`);

  const name = el("td", "col-name");
  name.append(el("div", "plan-title", p.name));
  name.append(el("div", "plan-sub", p.slug));

  const status = el("td", "col-status");
  status.append(el("span", `status status-${p.status}`, STATUS_LABEL[p.status] || p.status));

  const blocked = el("td", "col-blocked");
  if (p.blocked) {
    blocked.append(el("span", "badge blocked", "blocked"));
    if (p.blockedReason) {
      blocked.append(el("div", "why", p.blockedReason));
      blocked.title = p.blockedReason;
    }
  } else {
    blocked.append(el("span", "muted", "—"));
  }

  const progress = el("td", "col-progress");
  const total = p.progress ? p.progress.total : 0;
  const done = p.progress ? p.progress.checkedCount : 0;
  const wrap = el("div", "progress");
  const bar = el("div", "bar");
  const fill = el("i");
  fill.style.width = total ? `${Math.round((done / total) * 100)}%` : "0";
  bar.append(fill);
  wrap.append(bar, el("span", "n", total ? `${done}/${total}` : "0/0"));
  progress.append(wrap);

  const priority = el("td", "num col-priority");
  priority.textContent = p.priority ? `P${p.priority}` : "—";

  const session = el("td", "col-session");
  const sid = p.session || p.implementedBy;
  if (sid) {
    // ZCode ids are "session_<hex>": the kind is the same for every row, the hex tail differs.
    const chip = el("span", "session-chip", sid.replace(/^session_/, "").slice(0, 8));
    chip.title = sid;
    session.append(chip);
  } else {
    session.append(el("span", "muted", "—"));
  }

  const updated = el("td", "col-updated updated");
  updated.textContent = ago(p.updated);
  updated.title = new Date(p.updated).toLocaleString();

  tr.append(name, status, blocked, progress, priority, session, updated);
  tr.addEventListener("click", () => openPlan(p.id));
  tr.addEventListener("keydown", (e) => {
    if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openPlan(p.id); }
  });
  return tr;
}

function markSelected() {
  for (const tr of document.querySelectorAll("#plans-body tr")) {
    tr.classList.toggle("selected", tr.dataset.id === state.selected);
  }
}

// ---------- sessions ----------

function renderSessions() {
  const list = [...state.sessions.values()]
    .sort((a, b) => new Date(b.timeUpdated) - new Date(a.timeUpdated))
    .slice(0, 15);
  $("#sessions").innerHTML = list.length
    ? list.map((s) => {
        const repo = (state.repos.find((r) => s.rootPath && s.rootPath.startsWith(r.rootPath)) || {}).rootPath || s.rootPath;
        return `<li title="${escapeHtml(repo || "")}">
          <span class="st ${s.status}">${s.status}</span>
          <span class="name">${escapeHtml(s.title)}</span>
          <span class="muted">${ago(s.timeUpdated)}</span></li>`;
      }).join("")
    : `<li class="muted">no sessions yet</li>`;
}

// ---------- activity ----------

function describe(evt) {
  const p = evt.payload || {};
  const type = evt.type;
  const detail = [];
  if (type.startsWith("plan.")) {
    if (type === "plan.status_changed") {
      detail.push(`${p.slug || p.name}: ${p.from} → ${p.to}`);
      detail.push(`by ${p.actor || p.actorType}`);
      if (p.command) detail.push(p.command);
      if (p.separationOfDutiesWarning) detail.push("⚠ separation-of-duties");
    } else if (type === "plan.blocked" || type === "plan.unblocked") {
      detail.push(`${p.slug}: ${type === "plan.blocked" ? "blocked" : "unblocked"}`);
      if (p.reason) detail.push(`“${p.reason}”`);
    } else if (type === "plan.updated") {
      detail.push(`${p.slug} edited → v${p.version}`);
      if (p.progress) detail.push(`${p.progress.checkedCount ?? p.progress.checked}/${p.progress.total} steps`);
    } else {
      detail.push(`${p.slug || p.name || p.planId}${p.status ? ` (${p.status})` : ""}`);
    }
  } else if (type.startsWith("session.")) {
    detail.push(p.title || p.sessionId);
    if (p.status) detail.push(p.status);
  } else if (type === "hook.rejected") {
    detail.push(p.reason);
  } else {
    detail.push("");
  }
  return detail.filter(Boolean).join(" · ");
}

function addActivity(evt) {
  const list = $("#activity-list");
  const kind = evt.type.startsWith("plan.") ? "plan"
    : evt.type.startsWith("session.") ? "session"
    : evt.type === "hook.rejected" ? "rejected" : "other";
  const li = el("li", kind);
  li.dataset.seq = evt.seq;
  const head = el("div");
  head.append(el("span", "t", clock(evt.at) + " "));
  head.append(el("span", "type", evt.type));
  const body = el("div", "", describe(evt));
  const details = el("details");
  details.append(el("summary", "", "payload"));
  const pre = el("pre", "", JSON.stringify(evt.payload, null, 2));
  details.append(pre);
  li.append(head, body, details);
  list.prepend(li);
  while (list.children.length > 200) list.lastChild.remove();
  if (isClosed($("#activity-panel"))) setUnread(state.unread + 1);
}

// ---------- panels (activity slide-over + plan fly-out) ----------

const isClosed = (node) => node.classList.contains("hidden");

function syncScrim() {
  $("#scrim").classList.toggle("hidden", isClosed($("#drawer")) && isClosed($("#activity-panel")));
}

function setActivity(open) {
  const panel = $("#activity-panel");
  panel.classList.toggle("hidden", !open);
  panel.setAttribute("aria-hidden", String(!open));
  $("#btn-activity").setAttribute("aria-expanded", String(open));
  if (open) setUnread(0);
  syncScrim();
}

function toggleActivity() {
  setActivity(isClosed($("#activity-panel")));
}

function setUnread(n) {
  state.unread = n;
  const badge = $("#activity-count");
  badge.textContent = n > 99 ? "99+" : String(n);
  badge.classList.toggle("hidden", n === 0);
}

function closeDrawer() {
  $("#drawer").classList.add("hidden");
  state.selected = null;
  markSelected();
  syncScrim();
}

function applyEvent(evt, { silent = false } = {}) {
  // Dedupe (replay overlap + polling) strictly by seq.
  if (evt.seq <= state.lastSeq && !silent) {
    const existing = $(`#activity-list li[data-seq="${evt.seq}"]`);
    if (existing) return;
  }
  state.lastSeq = Math.max(state.lastSeq, evt.seq);
  $("#conn-seq").textContent = `seq ${state.lastSeq}`;
  if (!silent) addActivity(evt);

  const p = evt.payload || {};
  switch (evt.type) {
    case "plan.created":
    case "plan.updated":
    case "plan.status_changed":
    case "plan.session_linked":
    case "plan.blocked":
    case "plan.unblocked":
      void refreshPlan(p.planId).then(() => {
        renderTable();
        if (state.selected === p.planId && !document.querySelector("#drawer").classList.contains("hidden")) {
          void openPlan(p.planId, { keepTab: true, quiet: true });
        }
      });
      break;
    case "session.created":
    case "session.updated":
      loadSessions();
      break;
    default:
      break;
  }
}

// ---------- event stream ----------

function setConn(kind, text) {
  state.conn = kind;
  $("#conn-dot").className = `dot ${kind}`;
  $("#conn-text").textContent = text;
}

// Every type this build knows how to handle. Anything else is treated as a V2/extension type.
const KNOWN_EVENTS = [
  "session.created", "session.updated", "plan.created", "plan.updated", "plan.status_changed",
  "plan.session_linked", "plan.blocked", "plan.unblocked", "hook.rejected",
];
let watchUnknownTypes = null;
let maxSeenSeq = 0;

async function scanForUnknownTypes() {
  try {
    const res = await api(`/events?sinceSeq=${maxSeenSeq}`);
    const unknown = res.events.filter((e) => !KNOWN_EVENTS.includes(e.type) && e.seq > (Number(maxSeenSeq) || 0));
    for (const evt of unknown) applyEvent(evt);
    maxSeenSeq = Math.max(maxSeenSeq, res.lastSeq || 0);
  } catch { /* offline: the reconnect path reports it */ }
}

function connectStream() {
  state.stream?.close();
  clearInterval(state.pollTimer);
  clearInterval(watchUnknownTypes);
  const source = new EventSource(`/events?lastEventId=${state.lastSeq}`);
  state.stream = source;
  setConn("polling", "events: connecting…");

  source.onopen = () => {
    setConn("live", "events: live");
    clearInterval(state.pollTimer);
  };
  // EventSource only surfaces named events, and the server names each event by its type, so
  // listen for both the known types and the default (unnamed) channel — the latter is how a
  // future type (V2 conversations) still reaches the activity panel with no code change.
  const handler = (msg) => {
    let payload = {};
    try { payload = JSON.parse(msg.data); } catch { payload = { raw: msg.data }; }
    const seq = Number(msg.lastEventId || 0);
    const type = msg.type || "message";
    maxSeenSeq = Math.max(maxSeenSeq, seq);
    applyEvent({ seq, type, payload, at: new Date().toISOString() });
  };
  for (const t of KNOWN_EVENTS) source.addEventListener(t, handler);
  // Events whose type arrived after this build are still surfaced: a slow poll watches for
  // event types the named listeners cannot see, so V2 types show up in the panel untouched.
  watchUnknownTypes = setInterval(scanForUnknownTypes, 5000);

  source.onerror = () => {
    if (state.conn === "live" || state.conn === "polling") {
      setConn("offline", "events: reconnecting…");
      if (source.readyState === EventSource.CLOSED) reconnectWithCursor();
      startPolling();
    }
  };
}

// EventSource reconnects on its own; when it gives up (CLOSED) we poll until it is back.
function startPolling() {
  clearInterval(state.pollTimer);
  state.pollTimer = setInterval(async () => {
    try {
      const res = await api(`/events?sinceSeq=${state.lastSeq}`);
      for (const evt of res.events) applyEvent(evt);
      if (state.conn !== "live") {
        setConn("polling", "events: polling");
        // Probe SSE readiness; when it opens, onopen flips us back to live.
        if (!state.stream || state.stream.readyState === EventSource.CLOSED) {
          const probe = new EventSource(`/events?lastEventId=${state.lastSeq}`);
          probe.onopen = () => { probe.close(); connectStream(); };
          probe.onerror = () => probe.close();
        }
      }
    } catch {
      setConn("offline", "events: offline");
    }
  }, 2000);
}

function reconnectWithCursor() {
  // Repeated seqs are harmless: EventSource sends Last-Event-ID, and applyEvent dedupes.
  state.stream?.close();
  setTimeout(connectStream, 300);
}

// ---------- drawer ----------

async function openPlan(id, { keepTab = false, quiet = false } = {}) {
  const plan = state.plans.get(id);
  if (!plan) return;
  state.selected = id;
  if (!keepTab) state.tab = "content";
  $("#drawer").classList.remove("hidden");
  markSelected();
  syncScrim();
  $("#d-name").textContent = plan.name;
  const repo = state.repos.find((r) => r.id === plan.repositoryId) || {};
  $("#d-meta").textContent = [plan.slug, repo.rootPath || "", STATUS_LABEL[plan.status] || plan.status,
    `v${plan.version}`, `updated ${ago(plan.updated)}`].filter(Boolean).join(" · ");

  let detail;
  try {
    [detail] = await Promise.all([api(`/plans/${id}`), (async () => {
      if (state.tab !== "content") return;
      const content = await api(`/plans/${id}/content`).catch(() => null);
      $("#d-content").innerHTML = content ? renderMarkdown(content) : "<p class='muted'>no content</p>";
    })()]);
  } catch (e) {
    if (!quiet) toast(e.message, "error");
    return;
  }

  renderActions(plan, detail);
  $("#d-priority").value = plan.priority ?? 0;
  renderHistory(detail.history || []);
  renderDeps(detail.dependencies || [], detail);
  for (const btn of document.querySelectorAll(".tab")) btn.classList.toggle("active", btn.dataset.tab === state.tab);
  $("#d-content").classList.toggle("hidden", state.tab !== "content");
  $("#d-history").classList.toggle("hidden", state.tab !== "history");
}

function renderActions(plan, detail) {
  const box = $("#d-actions");
  box.innerHTML = "";
  $("#d-warnings").classList.add("hidden");

  const available = ACTIONS.filter((a) => a.from.includes(plan.status));
  if (!available.length) box.append(el("p", "muted", "No further actions from this state."));
  for (const action of available) {
    const btn = el("button", "btn", action.label);
    btn.dataset.action = action.id;
    btn.addEventListener("click", () => performAction(plan, action));
    box.append(btn);
  }

  const blockBox = $("#d-block");
  blockBox.innerHTML = "";
  if (plan.blocked) {
    blockBox.append(el("p", "", `Blocked: ${plan.blockedReason || "(no reason)"}`));
    const unblock = el("button", "btn", "Unblock");
    unblock.dataset.action = "unblock";
    unblock.addEventListener("click", async () => {
      const resolution = prompt("Resolution note (optional):") ?? "";
      await run(() => uiTransition(plan, "unblock", resolution));
    });
    blockBox.append(unblock);
  } else {
    const reason = el("input", "");
    reason.placeholder = "what is blocking?";
    reason.id = "d-block-reason";
    const block = el("button", "btn", "Block");
    block.dataset.action = "block";
    block.addEventListener("click", async () => {
      const text = reason.value.trim();
      if (!text) return toast("A block reason is required.", "error");
      await run(() => uiTransition(plan, "block", text));
    });
    blockBox.append(reason, block);
  }
}

// Every UI action is recorded as a /plan command with actor "andy", so the browser and the
// hook feed write the same history vocabulary and the same state-machine rules apply.
async function uiTransition(plan, command, note) {
  return api(`/plans/${plan.id}/commands`, {
    method: "POST",
    body: JSON.stringify({ command, note: note || "", by: "andy" }),
  });
}

async function performAction(plan, action) {
  let note = null;
  if (action.needsNote) {
    const labels = { fail: "What failed (verification findings)?", sendback: "Concerns to address:", defer: "Why defer?", abandon: "Why abandon?" };
    note = prompt(labels[action.id] || "Note:");
    if (note === null) return;
    if (action.id === "fail" && !note.trim()) return toast("A failure reason is required.", "error");
  }
  await run(async () => {
    const result = await uiTransition(plan, action.id, note);
    if (result && result.warnings && result.warnings.length) {
      const box = $("#d-warnings");
      box.innerHTML = "";
      box.append(el("strong", "", "Warnings"));
      for (const w of result.warnings) box.append(el("div", "", w));
      box.classList.remove("hidden");
    }
    return result;
  });
}

async function run(fn) {
  try {
    const result = await fn();
    toast("Done.", "ok");
    await refreshPlan(state.selected);
    renderTable();
    await openPlan(state.selected, { keepTab: true, quiet: true });
    return result;
  } catch (e) {
    const box = $("#d-warnings");
    box.innerHTML = "";
    box.append(el("strong", "", e.status === 422 ? "Refused" : "Error"));
    box.append(el("div", "", e.message));
    if (e.body && e.body.warnings) for (const w of e.body.warnings) box.append(el("div", "", w));
    box.classList.remove("hidden");
    toast(e.message, "error");
    return null;
  }
}

function renderHistory(history) {
  const box = $("#d-history");
  box.innerHTML = "";
  if (!history.length) { box.append(el("p", "muted", "No transitions yet.")); return; }
  for (const h of [...history].reverse()) {
    const row = el("div", "hrow");
    const move = el("div", "move");
    move.textContent = `${h.fromStatus ?? "—"} → ${h.toStatus}`;
    row.append(move);
    const meta = el("div", "muted");
    meta.textContent = [clock(h.at), h.actorType, h.actorSessionId, h.command, h.gitHead ? h.gitHead.slice(0, 8) : null]
      .filter(Boolean).join(" · ");
    row.append(meta);
    if (h.note) row.append(el("div", "", h.note));
    if (h.separationOfDutiesWarning) row.append(el("div", "sod", "⚠ separation of duties: implemented and verified by the same session"));
    box.append(row);
  }
}

function renderDeps(deps, detail) {
  const box = $("#d-deps");
  box.innerHTML = "";
  if (!deps.length) { box.append(el("li", "muted", "none")); return; }
  for (const d of deps) {
    const li = el("li");
    li.append(el("span", "badge", d.status), document.createTextNode(` ${d.slug}`));
    li.style.cursor = "pointer";
    li.addEventListener("click", () => { const plan = state.plans.get(d.id); if (plan) openPlan(d.id); });
    box.append(li);
  }
}

// ---------- dialogs (submit / repositories) ----------

function openSubmit() {
  if (!state.repos.length) { toast("Register a repository first.", "error"); $("#repos-dialog").showModal(); return; }
  $("#s-warnings").classList.add("hidden");
  $("#submit-dialog").showModal();
}

async function submitPlan(event) {
  event.preventDefault();
  const repo = $("#s-repo").value;
  const name = $("#s-name").value.trim();
  const markdown = $("#s-markdown").value;
  try {
    const result = await api("/plans", { method: "POST", body: JSON.stringify({ repo, name, markdown }) });
    if (result.warnings && result.warnings.length) {
      const box = $("#s-warnings");
      box.innerHTML = "";
      for (const w of result.warnings) box.append(el("div", "", w));
      box.classList.remove("hidden");
    }
    $("#submit-dialog").close();
    $("#s-name").value = "";
    toast(`Submitted ${result.slug}`, "ok");
    await loadPlans();
    void openPlan(result.id);
  } catch (e) {
    toast(e.message, "error");
  }
}

async function registerRepo(event) {
  event.preventDefault();
  const rootPath = $("#r-path").value.trim();
  if (!rootPath) return;
  try {
    await api("/repositories", {
      method: "POST",
      body: JSON.stringify({ rootPath, isDefault: $("#r-default").checked }),
    });
    $("#r-path").value = "";
    await loadRepos();
    toast("Repository registered.", "ok");
  } catch (e) {
    toast(e.message, "error");
  }
}

// ---------- wiring ----------

$("#filters").addEventListener("click", async (e) => {
  const chip = e.target.closest(".chip");
  if (!chip) return;
  // Chips toggle: pressing the active one again returns to the unfiltered table.
  state.filter = chip.classList.contains("active") ? "all" : chip.dataset.filter;
  for (const c of document.querySelectorAll(".chip")) c.classList.toggle("active", c === chip);
  // ready/blocked are server-side queries; re-ask so the chip always shows the live answer.
  if (state.filter === "ready" || state.filter === "blocked") {
    try { await loadPlans(); return; } catch { /* fall back to the cached sets */ }
  }
  renderTable();
});
$("#repo-filter").addEventListener("change", async (e) => {
  state.repoFilter = e.target.value;
  state.plans.clear();
  await loadPlans();
});
$("#search").addEventListener("input", (e) => { state.search = e.target.value; renderTable(); });
$("#filter-status").addEventListener("change", (e) => { state.statusFilter = e.target.value; renderTable(); });

// Header click toggles direction on the current key and starts descending on a new one.
for (const th of document.querySelectorAll("#plans-table thead th[data-sort]")) {
  th.addEventListener("click", () => {
    const key = th.dataset.sort;
    if (state.sort === key) state.dir = state.dir === "asc" ? "desc" : "asc";
    else { state.sort = key; state.dir = key === "name" || key === "session" ? "asc" : "desc"; }
    renderTable();
  });
}

$("#btn-close-drawer").addEventListener("click", closeDrawer);
for (const tab of document.querySelectorAll(".tab")) {
  tab.addEventListener("click", () => {
    state.tab = tab.dataset.tab;
    for (const t of document.querySelectorAll(".tab")) t.classList.toggle("active", t === tab);
    $("#d-content").classList.toggle("hidden", state.tab !== "content");
    $("#d-history").classList.toggle("hidden", state.tab !== "history");
  });
}
$("#priority-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const plan = state.plans.get(state.selected);
  if (!plan) return;
  const priority = Number($("#d-priority").value) || 0;
  await run(() => api(`/plans/${plan.id}`, { method: "PATCH", body: JSON.stringify({ priority }) }));
});
$("#btn-submit").addEventListener("click", openSubmit);
$("#btn-repos").addEventListener("click", () => $("#repos-dialog").showModal());
$("#btn-cancel-submit").addEventListener("click", () => $("#submit-dialog").close());
$("#btn-cancel-repo").addEventListener("click", () => $("#repos-dialog").close());
$("#btn-clear").addEventListener("click", () => { $("#activity-list").innerHTML = ""; setUnread(0); });
$("#btn-activity").addEventListener("click", () => toggleActivity());
$("#btn-close-activity").addEventListener("click", () => setActivity(false));
$("#scrim").addEventListener("click", () => {
  // Same dismissal as the X buttons: background click closes the front-most layer.
  if (!isClosed($("#drawer"))) closeDrawer();
  else if (!isClosed($("#activity-panel"))) setActivity(false);
});
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  if (!isClosed($("#drawer"))) closeDrawer();
  else if (!isClosed($("#activity-panel"))) setActivity(false);
});
// While a panel is open, a press anywhere outside it dismisses it. This is a dismissal press,
// so it is consumed (capture phase, default prevented): on a narrow screen the fly-out covers
// the table, and without this the press would also open the row underneath.
// Elements layered above the panels (dialogs, toast) are not "outside".
document.addEventListener("pointerdown", (e) => {
  const t = e.target;
  if (!(t instanceof Element)) return;
  if (t.closest("dialog, .toast, .drawer, .activity-panel, #topbar")) return;
  const open = !isClosed($("#drawer")) || !isClosed($("#activity-panel"));
  if (!open) return;
  e.preventDefault();
  e.stopPropagation();
  if (!isClosed($("#drawer"))) closeDrawer();
  else setActivity(false);
}, true);
setInterval(() => { renderTable(); renderSessions(); }, 30_000);

// Panels start below the topbar; when the bar wraps (narrow screens) they follow its height.
function syncTopbarHeight() {
  const bar = $("#topbar");
  const h = bar.getBoundingClientRect().height;
  if (h === 0) return;
  document.documentElement.style.setProperty("--topbar-h", `${Math.round(h)}px`);
  document.body.style.gridTemplateRows = `${Math.round(h)}px minmax(0, 1fr)`;
}
new ResizeObserver(syncTopbarHeight).observe($("#topbar"));

(async function init() {
  syncTopbarHeight();
  await loadHealth();
  await loadRepos();
  await loadPlans();
  await loadSessions();
  // Start from the server's current position; history before load is not shown as live tail.
  // The unknown-type watcher starts one step back so an extension event recorded just before
  // the page opened still surfaces.
  try {
    const boom = await api("/events?sinceSeq=0");
    state.lastSeq = boom.lastSeq;
    maxSeenSeq = Math.max(0, boom.lastSeq - 50);
    $("#conn-seq").textContent = `seq ${state.lastSeq}`;
  } catch { /* stream will catch up */ }
  connectStream();
  setInterval(loadHealth, 30_000);
})();
