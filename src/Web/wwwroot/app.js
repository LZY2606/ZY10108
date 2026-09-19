const $ = (id) => document.getElementById(id);
let currentProject = null;

function toast(msg, isError = false) {
  const t = $("toast");
  t.textContent = msg;
  t.style.background = isError ? "#b91c1c" : "#111827";
  t.classList.add("show");
  setTimeout(() => t.classList.remove("show"), 3200);
}

function idemKey() {
  return crypto.randomUUID();
}

async function api(path, options = {}) {
  const res = await fetch(path, options);
  const text = await res.text();
  let body;
  try { body = JSON.parse(text); } catch { body = text; }
  if (!res.ok) {
    const msg = body && body.message ? body.message : res.statusText;
    throw new Error(msg);
  }
  return body;
}

async function post(path, body) {
  return api(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "Idempotency-Key": idemKey() },
    body: JSON.stringify(body ?? {}),
  });
}

async function refreshProjects() {
  const list = await api("/api/projects");
  const ul = $("project-list");
  ul.innerHTML = "";
  for (const p of list) {
    const li = document.createElement("li");
    li.innerHTML = `<a href="#">${p.name}</a> <code>${p.id}</code> [${p.status}] levels=${p.levels} revisions=${p.revisions} published=${p.published ?? "-"}`;
    li.querySelector("a").onclick = (e) => { e.preventDefault(); openProject(p.id); };
    ul.appendChild(li);
  }
}

async function openProject(id) {
  currentProject = id;
  $("project-panel").hidden = false;
  $("cur-project").textContent = id;
  await refreshProject();
}

async function refreshProject() {
  if (!currentProject) return;
  const p = await api(`/api/projects/${currentProject}`);
  $("project-status").innerHTML =
    `状态 <b>${p.status}</b> · 层级 ${p.levels.length} · 规则版本 ${p.ruleVersion} · 已发布修订 <code>${p.publishedRevisionId ?? "-"}</code>`;
  renderRevisions(p);
  renderEvents(p);
  await refreshComposition();
}

async function refreshComposition() {
  try {
    const c = await api(`/api/projects/${currentProject}/composition`);
    $("composition-summary").innerHTML =
      `摘要 <code>${c.digest.slice(0, 16)}…</code> · 修订 <code>${c.revisionId}</code> · ` +
      `条目 ${c.entries.length} · 完整链 ${c.entries.filter(e => e.complete).length} · ` +
      `断链 ${c.entries.filter(e => !e.complete).length} · 问题 ${c.issues.length}`;
    $("composition-inputs").textContent = JSON.stringify({
      ruleVersion: c.ruleVersion,
      levelFingerprints: c.levelFingerprints,
      createdAt: c.createdAt,
    }, null, 2);
    const il = $("issue-list");
    il.innerHTML = "";
    for (const i of c.issues) {
      const li = document.createElement("li");
      li.className = i.severity === "error" ? "issue-error" : "issue-warning";
      li.textContent = `[${i.severity}] ${i.code}: ${i.message}`;
      il.appendChild(li);
    }
    const el = $("entry-list");
    el.innerHTML = "";
    for (const e of c.entries.slice(0, 200)) {
      const div = document.createElement("div");
      div.className = "step";
      const chain = e.steps.map(s => `L${s.level}:${s.path}:${s.line}:${s.col}`).join(" → ");
      div.innerHTML = e.complete
        ? `<span class="ok">✓</span> ${e.generatedPath}:${e.genLine}:${e.genCol} → ${chain}`
        : `<span class="broken">✗ L${e.breakLevel} 断点</span> ${e.generatedPath}:${e.genLine}:${e.genCol} → ${chain} — ${e.breakReason}`;
      el.appendChild(div);
    }
  } catch {
    $("composition-summary").textContent = "尚未组合。";
    $("composition-inputs").textContent = "";
    $("issue-list").innerHTML = "";
    $("entry-list").innerHTML = "";
  }
}

function renderRevisions(p) {
  const ul = $("revision-list");
  ul.innerHTML = "";
  for (const r of p.revisions) {
    const li = document.createElement("li");
    li.innerHTML = `<code>${r.id}</code> ${r.name} [${r.status}] edits=${r.edits.length} `;
    const composeBtn = document.createElement("button");
    composeBtn.textContent = "组合";
    composeBtn.onclick = async () => {
      await post(`/api/projects/${currentProject}/compose`, { revisionId: r.id });
      toast("修订组合完成");
      refreshProject();
    };
    const compareBtn = document.createElement("button");
    compareBtn.textContent = "对比基准";
    compareBtn.onclick = async () => {
      const res = await api(`/api/projects/${currentProject}/compare?revisionId=${r.id}`);
      toast(res.changed
        ? `最早变化: ${res.path}:${res.line}:${res.col} (${res.detail})`
        : "与基准无差异");
    };
    li.appendChild(composeBtn);
    li.appendChild(compareBtn);
    ul.appendChild(li);
  }
}

function renderEvents(p) {
  const ol = $("event-list");
  ol.innerHTML = "";
  for (const e of [...p.events].sort((a, b) => a.seq - b.seq)) {
    const li = document.createElement("li");
    li.innerHTML = `<code>#${e.seq}</code> ${e.type} <small>${e.ts}</small> <pre>${JSON.stringify(e.data)}</pre>`;
    ol.appendChild(li);
  }
}

$("btn-refresh").onclick = refreshProjects;
$("btn-create").onclick = async () => {
  const p = await post("/api/projects", { name: $("new-name").value || "project" });
  toast(`已创建 ${p.id}`);
  refreshProjects();
};
$("btn-demo").onclick = async () => {
  const res = await post("/api/demo");
  toast(`演示项目已就绪 ${res.id}`);
  await refreshProjects();
  openProject(res.id);
};
$("btn-import").onclick = async () => {
  try {
    const levels = JSON.parse($("import-json").value);
    await post(`/api/projects/${currentProject}/imports`, levels);
    toast("导入成功（一次提交）");
    refreshProject();
  } catch (e) { toast(`导入失败，未做任何修改: ${e.message}`, true); }
};
$("btn-compose").onclick = async () => {
  await post(`/api/projects/${currentProject}/compose`, {});
  toast("组合完成");
  refreshProject();
};
$("btn-publish").onclick = async () => {
  await post(`/api/projects/${currentProject}/publish`, {});
  toast("已发布");
  refreshProject();
};
$("btn-trace").onclick = async () => {
  const path = $("trace-path").value, line = $("trace-line").value, col = $("trace-col").value;
  const res = await api(`/api/projects/${currentProject}/trace?path=${encodeURIComponent(path)}&line=${line}&col=${col}`);
  const div = $("trace-result");
  if (!res.found) { div.textContent = "该位置没有映射段。"; return; }
  div.innerHTML = res.entry.steps
    .map(s => `<div class="step">L${s.level} <code>${s.sourceRoot ? s.sourceRoot + "/" : ""}${s.path}</code> 行 ${s.line} 列 ${s.col}</div>`)
    .join("") + (res.entry.complete ? '<div class="ok">完整链 ✓</div>' : `<div class="broken">断点 L${res.entry.breakLevel}: ${res.entry.breakReason}</div>`);
};
$("btn-reverse").onclick = async () => {
  const q = new URLSearchParams({
    level: $("rev-level").value, path: $("rev-path").value,
    line: $("rev-line").value, col: $("rev-col").value, length: $("rev-len").value,
  });
  if ($("rev-root").value) q.set("sourceRoot", $("rev-root").value);
  const res = await api(`/api/projects/${currentProject}/reverse?${q}`);
  $("reverse-result").innerHTML = res.ranges.length === 0 ? "无影响区间。" :
    res.ranges.map(r => `<div class="step">${r.generatedPath} 行 ${r.genLine} 列 ${r.genCol} 长度 ${r.length}</div>`).join("");
};
$("btn-rev-create").onclick = async () => {
  try {
    const edits = JSON.parse($("rev-edits").value);
    const r = await post(`/api/projects/${currentProject}/revisions`, { name: $("rev-name").value || "rev", edits });
    toast(`修订 ${r.id} 已创建`);
    refreshProject();
  } catch (e) { toast(e.message, true); }
};
$("btn-merge").onclick = async () => {
  const res = await post(`/api/projects/${currentProject}/merge`, { a: $("merge-a").value, b: $("merge-b").value });
  const div = $("merge-result");
  if (res.status === "merged") {
    div.innerHTML = `<div class="ok">合并成功 → 新修订 <code>${res.revision.id}</code></div>`;
  } else {
    div.innerHTML = res.conflicts.map(c => `
      <details open><summary>冲突 ${c.segmentKey}</summary>
        <pre>原段: ${JSON.stringify(c.original)}\n修订A: ${JSON.stringify(c.editA)}\n修订B: ${JSON.stringify(c.editB)}\nA下游影响: ${c.downstreamImpactA.join(", ")}\nB下游影响: ${c.downstreamImpactB.join(", ")}</pre>
      </details>`).join("");
  }
  refreshProject();
};
$("btn-export").onclick = async () => {
  const res = await fetch(`/api/projects/${currentProject}/export`);
  $("export-json").value = await res.text();
  toast("已导出到文本框");
};
$("btn-import-project").onclick = async () => {
  const res = await fetch("/api/import", {
    method: "POST",
    headers: { "Content-Type": "application/json", "Idempotency-Key": idemKey() },
    body: $("export-json").value,
  });
  const body = await res.json();
  toast(res.ok ? `导入完成 ${body.id}` : `导入失败: ${body.message}`, !res.ok);
  refreshProjects();
};

refreshProjects();
