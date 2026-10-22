'use strict';
const byId = id => document.getElementById(id);
const form = byId('claim-form');
let selectedId;
const requestKeys = new Map();
function requestKey(scope, body) {
  const fingerprint = scope + '\n' + body;
  if (!requestKeys.has(fingerprint)) requestKeys.set(fingerprint, crypto.randomUUID());
  return requestKeys.get(fingerprint);
}
const element = (tag, text, className) => {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
};
function newId() {
  form.elements.id.value = `CLM-DEMO-${crypto.randomUUID().slice(0, 12)}`;
}
async function loadClaims() {
  const response = await fetch('/api/claims');
  if (!response.ok) throw new Error(`Claims could not be loaded (HTTP ${response.status}).`);
  const claims = await response.json();
  byId('total').textContent = claims.length;
  byId('succeeded').textContent = claims.filter(c => c.status === 'Succeeded').length;
  byId('attention').textContent = claims.filter(c => ['Rejected', 'ManualReview', 'RetryPending'].includes(c.status)).length;
  byId('suppressed').textContent = claims.filter(c => c.status === 'Suppressed').length;
  const rows = claims.map(claim => {
    const row = element('tr');
    if (claim.id === selectedId) row.className = 'selected';
    const identity = element('td');
    const button = element('button', claim.id, 'claim-link');
    button.type = 'button';
    button.setAttribute('aria-label', `View timeline for ${claim.id}`);
    button.addEventListener('click', () => selectClaim(claim.id).catch(showLoadError));
    identity.append(button, element('small', claim.memberId));
    const status = element('td');
    status.append(element('span', claim.status, `badge ${claim.status}`));
    row.append(identity, element('td', claim.serviceDate), element('td', new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(claim.amount)), status, element('td', claim.failureCategory), element('td', `${claim.attemptCount} attempts · ${claim.retryCount} retries`));
    return row;
  });
  byId('claims-body').replaceChildren(...rows);
  byId('load-status').textContent = claims.length ? '' : 'No claims have been recorded.';
  if (!selectedId && claims.length) await selectClaim(claims[0].id);
  const [queueResponse, outboxResponse] = await Promise.all([fetch('/api/queues/correction'), fetch('/api/outbox')]);
  if (!queueResponse.ok || !outboxResponse.ok) throw new Error('Processing status is temporarily unavailable.');
  const queue = await queueResponse.json();
  byId('correction-list').replaceChildren(...(queue.length ? queue.map(item => {
    let reason = item.reason;
    try { reason = JSON.parse(reason).map(f => `${f.RuleId || f.ruleId}: ${f.SuggestedFix || f.suggestedFix}`).join('; '); } catch { }
    return element('li', `${item.claimId} · ${reason}`);
  }) : [element('li', 'No claims awaiting correction.')]));
  const messages = await outboxResponse.json();
  byId('outbox-list').replaceChildren(...(messages.length ? messages.map(message => element('li', `${message.claimId} · ${message.state} · ${message.deliveries} deliveries${message.state === 'Pending' ? ` · due ${message.nextAttemptAt} UTC` : ''}`)) : [element('li', 'No outbox messages.')]));
}
async function selectClaim(id) {
  const response = await fetch(`/api/claims/${encodeURIComponent(id)}`);
  if (!response.ok) throw new Error(`Timeline could not be loaded (HTTP ${response.status}).`);
  const data = await response.json();
  selectedId = id;
  byId('timeline-summary').textContent = `${id} · ${data.claim.status} · ${data.claim.failureCategory}`;
  byId('timeline').replaceChildren(...data.timeline.map(event => {
    const item = element('li');
    const time = element('time', `${event.timestamp.replace('T', ' ')} UTC`);
    time.dateTime = `${event.timestamp.replace(/Z$/, '')}Z`;
    item.append(element('strong', `${event.stage} · ${event.outcome}`), time, element('p', event.detail));
    return item;
  }));
  document.querySelectorAll('#claims-body tr').forEach(row => row.classList.toggle('selected', row.querySelector('button').textContent === id));
}
function showLoadError(error) { byId('load-status').textContent = error.message; }
form.addEventListener('submit', async event => {
  event.preventDefault();
  const button = byId('submit-button');
  button.disabled = true;
  const result = byId('submission-result');
  result.textContent = 'Validating synthetic claim…';
  const values = new FormData(form);
  const request = Object.fromEntries(values);
  request.amount = Number(values.get('amount'));
  if (!request.ruleVersion) delete request.ruleVersion;
  request.procedureCodes = values.get('procedureCodes').split(',').map(code => code.trim()).filter(Boolean);
  try {
    const response = await fetch('/api/claims', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': requestKey('/api/claims', JSON.stringify(request)) }, body: JSON.stringify(request) });
    const data = await response.json();
    result.replaceChildren(element('strong', data.claim ? `${data.claim.id}: ${data.claim.status} (HTTP ${response.status})` : `Submission returned HTTP ${response.status}`));
    if (data.validation?.length) {
      const list = element('ul');
      data.validation.forEach(failure => list.append(element('li', `${failure.ruleId} · ${failure.field}: ${failure.suggestedFix}`)));
      result.append(list);
    } else if (response.ok) result.append(element('p', 'Submission recorded. Processing is asynchronous; use Refresh claims to see attempts and the final outcome.'));
    else result.append(element('p', data.detail || 'Check the supplied JSON fields.'));
    if (data.claim) {
      selectedId = data.claim.id;
      await loadClaims();
      await selectClaim(data.claim.id);
    }
  } catch (error) { result.textContent = `Submission failed: ${error.message}`; }
  finally { button.disabled = false; }
});
byId('new-id').addEventListener('click', newId);
byId('refresh').addEventListener('click', () => loadClaims().catch(showLoadError));
newId();
loadClaims().catch(showLoadError);

const x12Text = byId('x12-text');
byId('x12-file').addEventListener('change', async event => {
  const file = event.target.files[0];
  if (!file) return;
  if (file.size > 65536) { byId('x12-status').textContent = 'File exceeds the 64 KiB limit.'; x12Text.value = ''; return; }
  try {
    x12Text.value = await file.text();
    byId('x12-status').textContent = `${file.name} loaded. Select Import X12 to process it.`;
  } catch (error) { byId('x12-status').textContent = `File could not be read: ${error.message}`; }
});
byId('x12-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = byId('x12-submit');
  const status = byId('x12-status');
  const results = byId('x12-results');
  if (new TextEncoder().encode(x12Text.value).length > 65536) { status.textContent = 'Input exceeds the 64 KiB limit.'; return; }
  button.disabled = true;
  status.textContent = 'Parsing the synthetic interchange…';
  results.replaceChildren();
  try {
    const response = await fetch('/api/x12/837', { method: 'POST', headers: { 'Content-Type': 'text/plain; charset=utf-8', 'X-Synthetic-Plan-Type': byId('x12-plan-type').value, 'Idempotency-Key': requestKey('/api/x12/837:' + byId('x12-plan-type').value, x12Text.value) }, body: x12Text.value });
    if (![200, 422].includes(response.status)) throw new Error(`HTTP ${response.status}; verify content type, encoding, and size.`);
    const data = await response.json();
    status.textContent = `${data.interchanges.length} interchange(s) processed · HTTP ${response.status}. Accepted syntax and claim outcomes are reported separately.`;
    data.interchanges.forEach((interchange, index) => {
      const section = element('section', undefined, 'x12-result');
      section.append(element('h3', `Interchange ${index + 1} · ${interchange.interchangeControlNumber || 'Unknown control'} · ${interchange.status}`));
      if (interchange.acknowledgment.errors.length) {
        const errors = element('ul');
        interchange.acknowledgment.errors.forEach(error => errors.append(element('li', `${error.segmentId} · segment ${error.segmentPosition} · ${error.elementPosition ? `element ${error.elementPosition}` : 'segment error'}${error.componentPosition ? ` · component ${error.componentPosition}` : ''}: ${error.reason}`)));
        section.append(errors);
      }
      section.append(element('h3', 'Parsed claims'));
      if (!interchange.parsedClaims.length) section.append(element('p', 'No claims could be parsed.'));
      else {
        const list = element('ul');
        interchange.parsedClaims.forEach((parsed, claimIndex) => {
          const claim = parsed.claim;
          const outcome = interchange.outcomes[claimIndex];
          list.append(element('li', `${claim.id} · ${claim.memberId} · ${claim.serviceDate} · ${claim.procedureCodes.join(', ')} · $${Number(claim.amount).toFixed(2)} · ${outcome?.status || 'Not imported (interchange rejected)'} · HI: ${parsed.diagnosisCodes.join(', ')}`));
        });
        section.append(list);
      }
      section.append(element('h3', '999-style acknowledgment'), element('pre', interchange.acknowledgment.text));
      results.append(section);
    });
    await loadClaims();
  } catch (error) { status.textContent = `X12 intake failed: ${error.message}`; }
  finally { button.disabled = false; }
});


async function loadPaSample() {
  const format = byId('pa-format').value;
  const response = await fetch(`/samples/pa-${format}.${format === 'fhir' ? 'json' : 'txt'}`);
  if (!response.ok) throw new Error('The synthetic PA sample could not be loaded.');
  const id = `PA-DEMO-${crypto.randomUUID().slice(0, 12)}`;
  byId('pa-text').value = (await response.text()).replaceAll('PA-SYN-SAMPLE', id);
  byId('pa-status').textContent = 'Synthetic sample ready. Select Submit to record it.';
}
function showPa(data, status) {
  const result = byId('pa-result'); result.replaceChildren();
  byId('pa-status').textContent = `${data.outcome ? 'Intake ' + data.outcome : 'Request'}${data.lifecycle ? ' · Review ' + data.lifecycle.decision : ''} · HTTP ${status}`;
  if (data.request) {
    byId('pa-lookup-id').value = data.request.id;
    byId('pa-decision-id').value = data.request.id;
    result.append(element('h3', 'Canonical request'), element('pre', JSON.stringify(data.request, null, 2)));
  }
  if (data.lifecycle) result.append(element('h3', 'Decision clock'), element('pre', JSON.stringify(data.lifecycle, null, 2)));
  if (data.errors?.length) {
    const list = element('ul');
    data.errors.forEach(error => list.append(element('li', `${error.ruleId} · ${error.path || error.field}${error.segmentId ? ` · ${error.segmentId} segment ${error.segmentPosition}, element ${error.elementPosition}` : ''}: ${error.reason}`)));
    result.append(list);
  }
  if (data.detail) result.append(element('p', data.detail));
  if (data.timeline) {
    const list = element('ol', undefined, 'timeline');
    data.timeline.forEach(event => {
      const item = element('li');
      item.append(element('strong', `${event.stage} · ${event.outcome}`), element('time', event.timestamp), element('p', event.detail)); list.append(item);
    });
    result.append(element('h3', 'Audit timeline'), list);
  }
}
byId('pa-sample').addEventListener('click', () => loadPaSample().catch(error => byId('pa-status').textContent = error.message));
byId('pa-format').addEventListener('change', () => loadPaSample().catch(error => byId('pa-status').textContent = error.message));
byId('pa-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = byId('pa-submit'); button.disabled = true;
  const format = byId('pa-format').value; const payload = byId('pa-text').value; const path = `/api/pa/${format}`;
  try {
    if (new TextEncoder().encode(payload).length > 65536) throw new Error('Input exceeds 64 KiB.');
    const response = await fetch(path, { method: 'POST', headers: { 'Content-Type': format === 'fhir' ? 'application/fhir+json' : 'text/plain; charset=utf-8', 'X-Synthetic-Plan-Type': byId('pa-plan-type').value, 'Idempotency-Key': requestKey(path + ':' + byId('pa-plan-type').value, payload) }, body: payload });
    const body = await response.text(); showPa(body ? JSON.parse(body) : { detail: 'Check content type, encoding, and size.' }, response.status);
    await loadSla();
  } catch (error) { byId('pa-status').textContent = `PA intake failed: ${error.message}`; }
  finally { button.disabled = false; }
});
byId('pa-lookup-form').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    const response = await fetch(`/api/pa/${encodeURIComponent(byId('pa-lookup-id').value.trim())}`);
    if (!response.ok) throw new Error(`Request lookup returned HTTP ${response.status}.`);
    showPa(await response.json(), response.status);
  } catch (error) { byId('pa-status').textContent = error.message; }
});
loadPaSample().catch(error => byId('pa-status').textContent = error.message);

let slaNext = null;
const formatHours = value => value == null ? '—' : Number(value).toFixed(2);
async function loadSla(more = false) {
  const response = await fetch('/api/pa?limit=100' + (more && slaNext ? '&after=' + encodeURIComponent(slaNext) : ''));
  if (!response.ok) throw new Error(`SLA board returned HTTP ${response.status}.`);
  const board = await response.json();
  byId('sla-ontrack').textContent = board.counts.onTrack;
  byId('sla-risk').textContent = board.counts.atRisk;
  byId('sla-breached').textContent = board.counts.breached;
  if (!more) byId('sla-rows').replaceChildren();
  for (const item of board.requests) {
    const state = item.lifecycle; const row = element('tr'); const identity = element('td');
    const button = element('button', item.id, 'claim-link'); button.type = 'button';
    button.addEventListener('click', async () => {
      byId('pa-decision-id').value = item.id; byId('pa-lookup-id').value = item.id;
      try {
        const detail = await fetch('/api/pa/' + encodeURIComponent(item.id));
        if (!detail.ok) throw new Error(`Request returned HTTP ${detail.status}.`);
        showPa(await detail.json(), detail.status); byId('pa-result').scrollIntoView({ behavior: 'smooth', block: 'start' });
      } catch (error) { byId('sla-status').textContent = error.message; }
    });
    identity.append(button); const status = element('td'); status.append(element('span', state.sla, 'badge ' + state.sla));
    const dates = element('td', new Date(state.receivedAt).toISOString()); dates.append(element('small', 'Due ' + new Date(state.dueAt).toISOString()));
    row.append(identity, element('td', `${state.priority} / ${state.decision}`), status, dates); byId('sla-rows').append(row);
  }
  slaNext = board.next; byId('sla-more').hidden = !slaNext;
  byId('sla-status').textContent = `Active requests only · As of ${new Date(board.asOf).toISOString()} · SLA scans every 30 seconds.`;
  const metricsResponse = await fetch('/api/reports/prior-auth-metrics');
  if (!metricsResponse.ok) throw new Error(`Metrics returned HTTP ${metricsResponse.status}.`);
  const report = await metricsResponse.json();
  byId('pa-metrics-rows').replaceChildren(...report.metrics.map(metric => {
    const row = element('tr');
    for (const value of [metric.priority, `${metric.received} / ${metric.finalized}`, metric.approvalRate == null ? '—' : (metric.approvalRate * 100).toFixed(1) + '%', metric.denialRate == null ? '—' : (metric.denialRate * 100).toFixed(1) + '%', formatHours(metric.meanTurnaroundHours), formatHours(metric.medianTurnaroundHours)]) row.append(element('td', value));
    return row;
  }));
  byId('pa-metrics-asof').textContent = `As of ${new Date(report.asOf).toISOString()} · ${report.source}`;
}
function refreshSla() { return loadSla().catch(error => byId('sla-status').textContent = error.message); }
byId('sla-refresh').addEventListener('click', refreshSla);
byId('sla-more').addEventListener('click', () => loadSla(true).catch(error => byId('sla-status').textContent = error.message));
byId('pa-decision').addEventListener('change', () => {
  const denied = byId('pa-decision').value === 'Denied';
  byId('pa-denial-reason').disabled = !denied; byId('pa-denial-reason').required = denied;
});
byId('pa-decision-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = byId('pa-decision-submit'); button.disabled = true;
  try {
    const id = byId('pa-decision-id').value.trim(); const decision = byId('pa-decision').value;
    const payload = JSON.stringify({ decision, reasonCode: decision === 'Denied' ? byId('pa-denial-reason').value : null });
    const path = '/api/pa/' + encodeURIComponent(id) + '/decisions';
    const response = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': requestKey(path, payload) }, body: payload });
    const data = await response.json();
    if (response.status < 500) requestKeys.delete(path + '\n' + payload);
    byId('pa-decision-status').textContent = response.ok ? `${id}: ${decision} recorded.` : `HTTP ${response.status}: ${data.error?.reason || data.detail || 'Decision rejected.'}`;
    if (response.ok) showPa(data, response.status);
    await loadSla();
  } catch (error) { byId('pa-decision-status').textContent = error.message; }
  finally { button.disabled = false; }
});
refreshSla();
setInterval(refreshSla, 30000);

let ruleRegistry = null;
let ruleImpactRequest = 0;
let ruleRequestInFlight = false;
async function loadRules() {
  const response = await fetch('/api/rules');
  if (!response.ok) throw new Error(`Rules returned HTTP ${response.status}.`);
  ruleRegistry = await response.json();
  const select = byId('rules-version'); const previous = select.value;
  select.replaceChildren(...ruleRegistry.versions.map(version => {
    const option = element('option', version.version + (version.active ? ' · active' : version.shadow ? ' · shadow' : ''));
    option.value = version.version; return option;
  }));
  select.value = ruleRegistry.versions.some(v => v.version === previous) ? previous : ruleRegistry.shadowVersion || ruleRegistry.versions.find(v => !v.active)?.version || ruleRegistry.activeVersion;
  byId('rules-active').textContent = `Live: ${ruleRegistry.activeVersion} · Shadow: ${ruleRegistry.shadowVersion || 'disabled'} · Revision ${ruleRegistry.revision}`;
  const submittedVersions = form.elements.ruleVersion; const selected = submittedVersions.value;
  submittedVersions.replaceChildren(element('option', 'Active version at receipt'));
  submittedVersions.options[0].value = '';
  for (const version of ruleRegistry.versions) { const option = element('option', `${version.version} · explicit`); option.value = version.version; submittedVersions.append(option); }
  const conflict = element('option', 'phase0-v0 · conflict'); conflict.value = 'phase0-v0'; submittedVersions.append(conflict);
  if ([...submittedVersions.options].some(option => option.value === selected)) submittedVersions.value = selected;
  await showRuleImpact();
}
async function showRuleImpact() {
  const requestNumber = ++ruleImpactRequest;
  if (!ruleRegistry) return;
  const version = ruleRegistry.versions.find(v => v.version === byId('rules-version').value);
  if (!version) return;
  byId('rules-definition').textContent = version.definitionJson;
  byId('rules-definition-meta').textContent = `${version.contentHash} · Effective ${version.effectiveFrom} until ${version.effectiveUntil || 'open ended'} · ${version.eligibleNow ? 'effective now' : 'outside effective window'}`;
  byId('rules-enable').disabled = ruleRequestInFlight || version.active || !!ruleRegistry.shadowVersion;
  byId('rules-disable').disabled = ruleRequestInFlight || !version.shadow;
  byId('rules-promote').disabled = ruleRequestInFlight || version.active || !version.eligibleNow;
  byId('rules-sample').disabled = ruleRequestInFlight || !ruleRegistry.shadowVersion;
  const response = await fetch(`/api/rules/${encodeURIComponent(version.version)}/shadow-diff`);
  if (!response.ok) throw new Error(`Impact returned HTTP ${response.status}.`);
  const diff = await response.json();
  if (requestNumber !== ruleImpactRequest || byId('rules-version').value !== version.version) return;
  byId('rules-compared').textContent = diff.compared;
  byId('rules-flipped').textContent = diff.flipped;
  byId('rules-to-reject').textContent = diff.acceptToReject;
  byId('rules-to-accept').textContent = diff.rejectToAccept;
  byId('rules-diff-note').textContent = `${diff.observed} observations · ${diff.candidateErrors} candidate errors · ${diff.protocolRejected} protocol rejections · Baselines: ${diff.baselineVersions.join(', ') || 'no comparisons yet'}. Code groups overlap; totals count each mapped transaction once. Groups below show observed flips.`;
  for (const [id, rows] of [['rules-provider', diff.byProvider], ['rules-code', diff.byProcedureCode], ['rules-plan', diff.byPlanType]]) {
    const changed = rows.filter(row => row.flipped > 0);
    byId(id).replaceChildren(...changed.map(group => {
      const row = element('tr');
      for (const value of [`${group.domain} / ${group.value}`, group.compared, group.acceptToReject, group.rejectToAccept]) row.append(element('td', value));
      return row;
    }));
    if (!changed.length) { const row = element('tr'); const cell = element('td', 'No observed flips.'); cell.colSpan = 4; row.append(cell); byId(id).append(row); }
  }
}
async function changeRules(action) {
  if (!ruleRegistry || ruleRequestInFlight) return;
  const version = byId('rules-version').value; const path = `/api/rules/${encodeURIComponent(version)}/${action === 'promote' ? 'promote' : 'shadow'}`;
  const command = { expectedRevision: ruleRegistry.revision };
  if (action !== 'promote') command.enabled = action === 'enable';
  const payload = JSON.stringify(command); ruleRequestInFlight = true;
  for (const id of ['rules-enable', 'rules-disable', 'rules-promote', 'rules-sample']) byId(id).disabled = true;
  try {
    const response = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': requestKey(path, payload) }, body: payload });
    const result = await response.json();
    if (response.status < 500) requestKeys.delete(path + '\n' + payload);
    if (!response.ok) throw new Error(result.detail || `HTTP ${response.status}`);
    byId('rules-status').textContent = `${version}: ${action === 'promote' ? 'promoted for new effective receipts' : action === 'enable' ? 'shadow enabled' : 'shadow disabled'}.`;
  } catch (error) { byId('rules-status').textContent = error.message; }
  finally { ruleRequestInFlight = false; await loadRules().catch(error => byId('rules-status').textContent = error.message); }
}
byId('rules-refresh').addEventListener('click', () => loadRules().catch(error => byId('rules-status').textContent = error.message));
byId('rules-version').addEventListener('change', () => showRuleImpact().catch(error => byId('rules-status').textContent = error.message));
byId('rules-enable').addEventListener('click', () => changeRules('enable'));
byId('rules-disable').addEventListener('click', () => changeRules('disable'));
byId('rules-promote').addEventListener('click', () => changeRules('promote'));
byId('rules-sample').addEventListener('click', async () => {
  if (ruleRequestInFlight) return;
  ruleRequestInFlight = true; byId('rules-sample').disabled = true;
  try {
    const suffix = crypto.randomUUID().slice(0, 12);
    const request = { id: 'CLM-IMPACT-' + suffix, providerNpi: '0000000006', memberId: 'SYN-IMPACT-' + suffix,
      serviceDate: '2026-01-20', procedureCodes: ['SYN-IMPACT'], amount: 150, planType: byId('rules-sample-plan').value };
    const response = await fetch('/api/claims', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify(request) });
    const result = await response.json();
    if (!result.claim) throw new Error(result.detail || `HTTP ${response.status}`);
    byId('rules-status').textContent = `${request.id}: live ${result.claim.status} under ${result.claim.ruleVersion}; HTTP ${response.status}. Stored impact refreshed.`;
    await loadClaims();
  } catch (error) { byId('rules-status').textContent = error.message; }
  finally { ruleRequestInFlight = false; await loadRules().catch(error => byId('rules-status').textContent = error.message); }
});
loadRules().catch(error => byId('rules-status').textContent = error.message);


async function loadRecordedShadowBenchmark() {
  const response = await fetch('/benchmarks/shadow.json');
  if (!response.ok) throw new Error('Recorded local benchmark is unavailable. See the measured results ledger.');
  const report = await response.json();
  if (report.status !== 'completed') throw new Error('The recorded benchmark did not complete successfully. See its JSON artifact.');
  const number = new Intl.NumberFormat('en-US');
  byId('shadow-benchmark-claims').textContent = number.format(report.replayedClaims);
  byId('shadow-benchmark-flips').textContent = `${number.format(report.flipCount)} / ${number.format(report.expectedFlips)}`;
  byId('shadow-benchmark-detection').textContent = report.detectionPercent === null ? 'No expected flips' : `${report.detectionPercent.toFixed(4)}%`;
  byId('shadow-benchmark-runtime').textContent = `${report.wallSeconds.toFixed(3)} s`;
  byId('shadow-benchmark-status').textContent = `Recorded ${new Date(report.completedAtUtc).toISOString()} · local ${report.environment.architecture} · ${report.environment.buildConfiguration}.`;
  byId('shadow-benchmark-detail').textContent = `${number.format(report.v1.rejected)} baseline rejections; ${number.format(report.falsePositive)} false positives; ${number.format(report.falseNegative)} missed flips. Evaluation plus oracle: ${report.evaluationAndOracleSeconds.toFixed(3)} s. Wall time includes SQLite reads and observation/audit persistence. This is a recorded local measurement; it does not measure Azure API capacity or change live rules.`;
}
loadRecordedShadowBenchmark().catch(error => byId('shadow-benchmark-status').textContent = error.message);
