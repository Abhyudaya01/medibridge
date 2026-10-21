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
    const response = await fetch('/api/x12/837', { method: 'POST', headers: { 'Content-Type': 'text/plain; charset=utf-8', 'Idempotency-Key': requestKey('/api/x12/837', x12Text.value) }, body: x12Text.value });
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
