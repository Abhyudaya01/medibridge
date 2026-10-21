'use strict';
const byId = id => document.getElementById(id);
const form = byId('claim-form');
let selectedId;
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
    row.append(identity, element('td', claim.serviceDate), element('td', new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(claim.amount)), status, element('td', claim.failureCategory));
    return row;
  });
  byId('claims-body').replaceChildren(...rows);
  byId('load-status').textContent = claims.length ? '' : 'No claims have been recorded.';
  if (!selectedId && claims.length) await selectClaim(claims[0].id);
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
    const response = await fetch('/api/claims', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
    const data = await response.json();
    result.replaceChildren(element('strong', data.claim ? `${data.claim.id}: ${data.claim.status} (HTTP ${response.status})` : `Submission returned HTTP ${response.status}`));
    if (data.validation?.length) {
      const list = element('ul');
      data.validation.forEach(failure => list.append(element('li', `${failure.ruleId} · ${failure.field}: ${failure.suggestedFix}`)));
      result.append(list);
    } else if (response.ok) result.append(element('p', 'All validation rules passed. Open the timeline to inspect processing events.'));
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
