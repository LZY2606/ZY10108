let workspace;

const value = id => document.getElementById(id).value;
const number = id => Number.parseInt(value(id), 10);
const show = (id, payload) => document.getElementById(id).textContent = typeof payload === 'string' ? payload : JSON.stringify(payload, null, 2);

async function postJson(url, body) {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body ?? {})
  });
  const payload = await response.json();
  if (!response.ok) throw new Error(payload.error ?? 'Request failed');
  return payload;
}

async function loadWorkspace() {
  workspace = await fetch('/api/workspace').then(response => response.json());
  document.getElementById('state').textContent = `${workspace.state} · ${workspace.ruleVersion}`;
  const validationClass = workspace.validation.complete ? 'ok' : 'warn';
  document.getElementById('summary').innerHTML = `
    <p><b class="${validationClass}">${workspace.validation.complete ? '链完整' : '链不完整'}</b>
    ，有效问题 <b class="${workspace.validation.valid ? 'ok' : 'bad'}">${workspace.validation.issues.length}</b>
    ，断点 <b class="${workspace.validation.breaks.length ? 'warn' : 'ok'}">${workspace.validation.breaks.length}</b>。</p>
    <p class="muted">确定摘要：${workspace.deterministicSummary}</p>
    <p class="muted">发布版本：${workspace.publications.length ? workspace.publications.at(-1).deterministicSummary : '尚未发布'}</p>`;
  document.getElementById('stages').innerHTML = `<table><thead><tr><th>级别</th><th>输出</th><th>输出指纹</th><th>Map 指纹</th><th>来源</th></tr></thead><tbody>
    ${workspace.stages.map(stage => `<tr><td>${stage.id}<br><span class="muted">${stage.label}</span></td>
      <td><pre>${escapeHtml(stage.outputText)}</pre></td><td>${stage.outputSha256.slice(0, 16)}</td><td>${stage.mapSha256.slice(0, 16)}</td>
      <td>${(workspace.validation.sources ?? []).filter(source => source.stageId === stage.id).map(source => source.resolvedPath).join('<br>')}</td></tr>`).join('')}
  </tbody></table>`;
  document.getElementById('events').innerHTML = workspace.events
    .map(event => `<li><b>${event.sequence}</b> ${event.type} <span class="muted">${event.idempotencyKey}</span></li>`).join('');
  document.getElementById('revisions').innerHTML = workspace.revisions.map(revision =>
    `<p>候选 <b>${revision.id}</b>：${revision.stageId}#${revision.segmentIndex} → (${revision.originalLine},${revision.originalColumn})
      <button onclick="compareRevision('${revision.id}')">比较最早变化</button></p>`).join('') || '<p class="muted">暂无修订；原始 map 不会被改写。</p>';
  show('jobs', workspace.jobs);
}

async function trace() {
  try {
    show('traceResult', await postJson('/api/trace', {
      line: number('traceLine'),
      column: number('traceColumn'),
      columnEncoding: value('traceEncoding')
    }));
  } catch (error) {
    show('traceResult', error.message);
  }
}

async function reverse() {
  try {
    show('reverseResult', await postJson('/api/reverse', {
      sourcePath: value('sourcePath'),
      startLine: number('startLine'),
      startColumn: number('startColumn'),
      endLine: number('endLine'),
      endColumn: number('endColumn')
    }));
  } catch (error) {
    show('reverseResult', error.message);
  }
}

async function addRevision() {
  try {
    const result = await postJson('/api/revisions', {
      id: value('revisionId') || undefined,
      stageId: value('revisionStage'),
      kind: 'segment',
      segmentIndex: number('revisionSegment'),
      originalLine: number('revisionLine'),
      originalColumn: number('revisionColumn'),
      idempotencyKey: `revision-${value('revisionStage')}-${number('revisionSegment')}-${number('revisionColumn')}`
    });
    show('revisionResult', result);
    await loadWorkspace();
  } catch (error) {
    show('revisionResult', error.message);
  }
}

async function compareRevision(id) {
  show('revisionResult', await postJson(`/api/revisions/${id}/compare`));
}

async function addPathRevision() {
  try {
    const id = value('pathRevisionId') || `path-${Date.now()}`;
    const rule = value('pathRevisionRule');
    const result = await postJson('/api/revisions', {
      id,
      stageId: value('pathRevisionStage'),
      kind: 'path-rule',
      segmentIndex: -1,
      sourcePathRule: rule,
      idempotencyKey: `path-${value('pathRevisionStage')}-${rule}`
    });
    show('revisionResult', result);
    await loadWorkspace();
  } catch (error) {
    show('revisionResult', error.message);
  }
}

async function mergeRevisions() {
  try {
    show('revisionResult', await postJson('/api/revisions/merge', {
      firstRevisionId: value('mergeA'),
      secondRevisionId: value('mergeB')
    }));
  } catch (error) {
    show('revisionResult', error.message);
  }
}

async function publish() {
  const ids = workspace.revisions.map(revision => revision.id);
  show('revisionResult', await postJson('/api/publish', {
    idempotencyKey: 'publish-' + workspace.deterministicSummary.slice(0, 12),
    revisionIds: ids
  }));
  await loadWorkspace();
}

async function recoverJob() {
  const jobs = await postJson('/api/jobs/recover', { idempotencyKey: value('jobKey'), kind: 'fingerprint-scan' });
  show('jobs', jobs);
}

async function exportThenImport() {
  const exported = await fetch('/api/export').then(response => response.json());
  const result = await postJson('/api/export/import', exported);
  show('exportResult', result);
}

function escapeHtml(text) {
  return String(text).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}

loadWorkspace().catch(error => show('summary', error.message));
