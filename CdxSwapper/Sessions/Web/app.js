'use strict';
const $ = s => document.querySelector(s);
const token = $('meta[name="session-token"]').content;
const clients = {codex:'Codex',claude:'Claude',hermes:'Hermes',antigravity:'Gemini (Antigravity)'};
let sessions = [], provider = 'all', selected = null, loading = false;
let detailVersion = 0, currentSize = 0, sourceData = {}, scanErrors = [], toastTimer;
let currentRevision = '';
let matches = {}, searchTimer, searchVersion = 0, indexState = {}, indexPollBusy = false;
let renderedSearch = '', matchVersion = 0;
const checkedSessions=new Set();let batchBusy=false;
function updateSelection(){
  const visible=[...$('#list').querySelectorAll('.card')].map(c=>c.dataset.key);
  $('#select-visible').checked=visible.length>0&&visible.every(k=>checkedSessions.has(k));
  $('#select-visible').indeterminate=visible.some(k=>checkedSessions.has(k))&&!$('#select-visible').checked;
  $('#delete-selected').textContent=t('deleteSelected',checkedSessions.size);
  $('#delete-selected').disabled=batchBusy||checkedSessions.size===0;
  $('#select-visible').disabled=batchBusy;$('#clear-selection').disabled=batchBusy||checkedSessions.size===0;
}
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const date = value => value ? new Date(value).toLocaleString(locale, {day:'2-digit',month:'short',hour:'2-digit',minute:'2-digit'}) : '—';
const bytes = n => n > 1048576 ? (n/1048576).toFixed(1)+' MB' : (n/1024).toFixed(1)+' KB';
function highlighted(text) {
  const query = $('#search').value.trim().toLowerCase();
  if (!query || !$('#content-search').checked) return esc(text);
  const value = String(text ?? ''); let result='', position=0;
  while (position < value.length) {
    const found=value.toLowerCase().indexOf(query,position);
    if(found<0) {result+=esc(value.slice(position));break;}
    result+=esc(value.slice(position,found))+'<mark>'+esc(value.slice(found,found+query.length))+'</mark>';
    position=found+query.length;
  }
  return result;
}
function showIndex(state) {
  indexState=state;
  $('#index-status').textContent=state.busy?t('indexProgress',state.done,state.total):state.errors?.length?t('indexError'):t('indexReady');
}
async function searchContent() {
  const version=++searchVersion, query=$('#search').value.trim();
  if(!$('#content-search').checked || !query) {
    matches={};renderList();
    if(selected && renderedSearch) await selectSession(selected,true);
    return;
  }
  try {
    const previousMatch=JSON.stringify(matches[selected]??null);
    const data=await api('/api/search?q='+encodeURIComponent(query));
    if(version!==searchVersion)return;
    matches=data.matches;showIndex(data.index);renderList();
    if(selected && (renderedSearch!==query || previousMatch!==JSON.stringify(matches[selected]??null))) await selectSession(selected,true,renderedSearch!==query);
  }catch(e){if(version===searchVersion)toast(e.message);}
}
function queryChanged() {
  ++searchVersion; matches={}; clearTimeout(searchTimer);renderList();
  $('#search').placeholder=t($('#content-search').checked?'contentPlaceholder':'placeholder');
  searchTimer=setTimeout(searchContent,350);
}
async function api(path, body) {
  const response = await fetch(path, {method:body?'POST':'GET', headers:{'X-Session-Token':token,'Content-Type':'application/json'}, ...(body ? {body:JSON.stringify(body)} : {})});
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || t('apiError'));
  return data;
}
function toast(text) { clearTimeout(toastTimer); $('#toast').textContent=text; $('#toast').style.display='block'; toastTimer=setTimeout(()=>$('#toast').style.display='none',4500); }
function renderList() {
  const query = $('#search').value.trim().toLowerCase();
  let items = sessions.filter(s => (provider==='all'||s.provider===provider) && ($('#agents').checked||!s.subagent) && ($('#status').value==='all'||s.archived===($('#status').value==='archived')) && (!query||[s.title,s.id,s.cwd,s.path,s.model].join(' ').toLowerCase().includes(query)||($('#content-search').checked&&matches[s.key])));
  if ($('#sort').value==='old') items.reverse();
  if ($('#sort').value==='title') items.sort((a,b)=>(a.title||a.id).localeCompare(b.title||b.id));
  $('#count').textContent=items.length+' / '+sessions.length;
  for (const name of Object.keys(clients)) $('#'+name+'-count').textContent=sessions.filter(s=>s.provider===name).length;
  $('#list').innerHTML=items.length ? items.map(s=>`<button class="card ${s.key===selected?'selected':''}" data-key="${s.key}"><div class="card-top"><span class="badge ${s.provider}">${s.provider}</span>${s.archived?`<span>${t('archive')}</span>`:''}${s.subagent?`<span>${t('subagent')}</span>`:''}<span class="date">${esc(date(s.updated))}</span></div><div class="card-title">${esc(s.title||t('untitled'))}</div><div class="card-path" title="${esc(s.cwd)}">${esc(s.cwd||t('noProject'))}</div><div class="card-id">${esc(s.id)}</div></button>`).join('') : `<div class="empty">${t('noSessions')}</div>`;
  if($('#content-search').checked&&query) for(const card of $('#list').querySelectorAll('.card')) {
    const match=matches[card.dataset.key];
    if(match)card.insertAdjacentHTML('beforeend',`<div class="card-snippet">${highlighted(match.hits[0].snippet)}<br><small>${t('matchesCount',match.count)}</small></div>`);
  }
  for(const card of $('#list').querySelectorAll('.card')) {
    const s=sessions.find(s=>s.key===card.dataset.key);
    const row=document.createElement('div');row.className='card-row';card.before(row);row.append(card);
    const check=document.createElement('input');check.type='checkbox';check.dataset.checkKey=card.dataset.key;
    check.checked=checkedSessions.has(card.dataset.key);check.disabled=batchBusy;check.setAttribute('aria-label',t('selectSession',s?.title||s?.id));row.prepend(check);
    if(s?.client)card.querySelector('.badge').textContent=s.client;
    if(s?.metadata_only)card.insertAdjacentHTML('beforeend',`<div class="card-snippet">${t(s.provider==='antigravity'?'encryptedCard':'noJournal')}</div>`);
  }
  updateSelection();
}
async function refresh() {
  if (loading) return;
  loading=true; $('#refresh').disabled=true;
  try {
    const data=await api('/api/sessions'); sessions=data.sessions; sourceData=data.sources; scanErrors=data.errors;
    for(const key of checkedSessions)if(!sessions.some(s=>s.key===key))checkedSessions.delete(key);
    showIndex(data.index);renderList();
    $('#sync').textContent=t('updated',new Date().toLocaleTimeString(locale))+(scanErrors.length?' · '+t('errors',scanErrors.length):'');
    if (selected) {
      const s=sessions.find(s=>s.key===selected);
      if(s && (s.size!==currentSize || (s.revision||'')!==currentRevision)) await selectSession(selected, true);
      if(!s) {selected=null; $('#detail').innerHTML=`<div class="empty">${t('fileGone')}</div>`;}
    }
  } catch(e) {toast(e.message); $('#sync').textContent=t('connectionError');}
  finally {loading=false; $('#refresh').disabled=false;}
}
function messageHTML(m) {
  const text=`<pre>${highlighted(m.text)}</pre>${m.truncated?`<div class="truncated">${t('truncated')}</div>`:''}`;
  return `<article class="message ${esc(m.role)}"><div class="message-head"><b>${t(m.role==='user'?'you':m.role==='tool'?'tool':m.role==='metadata'?'metadata':'assistant')}</b><span>${esc(date(m.timestamp))}</span></div>${m.role==='tool'||m.role==='metadata'?`<details><summary>${t(m.role==='metadata'?'showMetadata':'showTool')}</summary>${text}</details>`:text}</article>`;
}
async function selectSession(key, quiet=false, jumpToMatch=false) {
  selected=key; const version=++detailVersion; renderList();
  if(!quiet) $('#detail').innerHTML=`<div class="empty">${t('loadingConversation')}</div>`;
  try {
    const data=await api('/api/session?key='+key);
    if(version!==detailVersion) return;
    const s=data.session; currentSize=s.size;currentRevision=s.revision||'';
    const rows=[['ID',s.id,'copy-id'],[t(s.provider==='hermes'?'database':'file'),s.path,'copy-path'],[t('project'),s.cwd],[t('model'),s.model||'—'],[t('created'),date(s.created)],[t('modified'),date(s.updated)],[t('size'),bytes(s.size)+(s.tokens!=null?' · '+s.tokens.toLocaleString(locale)+' '+t('tokens'):'')]];
    if(s.provider==='hermes')rows.push([t('profile'),s.profile],[t('platform'),s.source]);
    if(s.cli_id)rows.splice(1,0,['CLI ID',s.cli_id,'copy-cli-id']);
    if(s.desktop_path && s.desktop_path!==s.path)rows.splice(3,0,['Desktop',s.desktop_path,'copy-desktop-path']);
    $('#detail').innerHTML=`<div class="detail-header"><span class="badge ${s.provider}">${clients[s.provider]}</span> ${s.archived?`<span class="badge">${t('archive')}</span>`:''}<h2>${esc(s.title||t('untitled'))}</h2><div class="meta">${rows.map(([label,value,action])=>`<span class="label">${label}</span><code class="${action?'':'wide'}">${esc(value||'—')}</code>${action?`<button data-action="${action}">${t('copy')}</button>`:''}`).join('')}</div><div class="actions"><button class="primary" data-action="resume" ${s.subagent?'disabled':''}>${t('resume',clients[s.provider])}</button><button data-action="copy-command">${t('command')}</button><button data-action="folder">${t('showFile')}</button><button data-action="project">${t('projectFolder')}</button><button data-action="file">${t(s.provider==='hermes'?'openTranscript':'openJsonl')}</button></div></div><div class="conversation"><div class="conversation-title"><span>${t('conversation')}</span><button id="reload-detail">↻</button></div><button class="older" id="older" hidden>${t('older')}</button><div id="messages"></div></div>`;
    $('#messages').innerHTML=data.messages.map(messageHTML).join('')||`<div class="empty">${t('emptyFragment')}</div>`;
    if(s.provider==='codex') {
      $('#detail [data-action="resume"]').textContent=t('resume','Codex Desktop');
      $('#detail .detail-header > .badge').textContent='Codex Desktop';
    }
    if(s.metadata_only)$('#messages').insertAdjacentHTML('afterbegin',`<div class="empty">${t(s.provider==='antigravity'?'encryptedHistory':'summaryOnly')}</div>`);
    if(s.provider==='claude'||s.provider==='hermes')$('#detail [data-action="resume"]').textContent=t('resume',s.provider==='claude'?'Claude Desktop':'Hermes Desktop');
    if(s.metadata_only&&s.provider!=='claude')$('#detail [data-action="resume"]').disabled=true;
    $('.actions').insertAdjacentHTML('afterbegin',Object.entries(clients).filter(([name])=>name!==s.provider).map(([name,title])=>`<button class="primary" data-action="continue-${name}" ${s.metadata_only?'disabled':''}>${t('convert',name==='antigravity'?'Antigravity':title)}</button>`).join(''));
    if(s.provider!=='antigravity')$('.actions').insertAdjacentHTML('beforeend',`<button data-action="continue-antigravity-ide" ${s.metadata_only?'disabled':''}>${t('convert','Antigravity IDE')}</button>`);
    $('.actions').insertAdjacentHTML('beforeend',`<button data-action="delete" style="color:var(--danger,#ef6666)">${t('deleteSession')}</button>`);
    if(s.provider==='antigravity') {
      const button=$('#detail [data-action="resume"]');button.dataset.action='open-client';button.disabled=false;button.textContent=t('openClient',s.client);
      $('.actions').insertAdjacentHTML('afterend',`<p class="card-snippet">${t('antigravityOpenHint')}</p>`);
      $('#detail .detail-header > .badge').textContent=s.client;
      $('#detail [data-action="copy-command"]').textContent=t('launchCommand');
      $('#detail [data-action="file"]').textContent=t('openTranscript');
      if(s.metadata_only)$('#detail [data-action="file"]').disabled=true;
      if(s.path.endsWith('.db'))$('.conversation-title').insertAdjacentHTML('afterend',`<p class="card-snippet">${t('nativeFieldsHint')}</p>`);
    } else $('.actions').insertAdjacentHTML('beforeend',`<button data-action="context-antigravity" ${s.metadata_only?'disabled':''}>${t('antigravityContext')}</button>`);
    const match=$('#content-search').checked?matches[key]:null;
    renderedSearch=$('#content-search').checked?$('#search').value.trim():'';
    if(match) {
      $('.conversation').insertAdjacentHTML('beforebegin',`<div class="search-hits"><h3>${t('hits',match.count)}${match.count>30?t('first30'):''}</h3>${match.hits.map(h=>`<button class="hit" data-offset="${h.offset}"><small>${esc(h.role)} ↗</small>${highlighted(h.snippet)}</button>`).join('')}<div id="matched-message" hidden></div></div>`);
      await openMatch(match.hits[0].offset,version,!quiet||jumpToMatch);
      if(version!==detailVersion)return;
    }
    setCursor(data.before);
    if(!quiet && !match) $('#detail').scrollTop=0;
  } catch(e) {if(version===detailVersion) $('#detail').innerHTML=`<div class="empty">${esc(e.message)}</div>`;}
}
async function openMatch(offset,version=detailVersion,jump=true) {
  const request=++matchVersion;
  try {
    const data=await api('/api/match?key='+selected+'&offset='+offset);
    if(version!==detailVersion||request!==matchVersion)return;
    const box=$('#matched-message');if(!box)return;
    box.hidden=false;box.innerHTML=data.message?messageHTML(data.message):`<div class="empty">${t('fileChanged')}</div>`;
    box.querySelectorAll('details').forEach(d=>d.open=true);
    if(jump) {
      const mark=box.querySelector('mark');
      if(mark)mark.scrollIntoView({block:'center'});else box.scrollIntoView({block:'start'});
    }
  }catch(e){toast(e.message);}
}
function setCursor(before) {const button=$('#older'); button.hidden=before===null; button.dataset.before=before??''; button.disabled=false; button.textContent=t('older');}
async function older() {
  const button=$('#older'); const key=selected, version=detailVersion; button.disabled=true; button.textContent=t('loading');
  try {
    const data=await api('/api/session?key='+key+'&before='+button.dataset.before);
    if(version!==detailVersion) return;
    const messages=$('#messages'); if(messages.querySelector('.empty')) messages.innerHTML='';
    const height=$('#detail').scrollHeight;
    messages.insertAdjacentHTML('afterbegin',data.messages.map(messageHTML).join(''));
    setCursor(data.before); $('#detail').scrollTop+= $('#detail').scrollHeight-height;
  } catch(e) {toast(e.message); if(version===detailVersion) {button.disabled=false;button.textContent=t('retry');}}
}
$('#list').addEventListener('click',e=>{const card=e.target.closest('[data-key]');if(card) selectSession(card.dataset.key);});
$('#list').addEventListener('change',e=>{const key=e.target.dataset.checkKey;if(!key)return;e.target.checked?checkedSessions.add(key):checkedSessions.delete(key);updateSelection();});
$('#select-visible').addEventListener('change',e=>{for(const card of $('#list').querySelectorAll('.card'))e.target.checked?checkedSessions.add(card.dataset.key):checkedSessions.delete(card.dataset.key);renderList();});
$('#clear-selection').addEventListener('click',()=>{checkedSessions.clear();renderList();});
$('#close-batch').addEventListener('click',()=>$('#batch-dialog').close());
$('#delete-selected').addEventListener('click',async()=>{
  const keys=[...checkedSessions];if(batchBusy||!keys.length||!confirm(t('batchConfirm',keys.length)))return;
  batchBusy=true;renderList();
  try{
    const {results}=await api('/api/delete-batch',{keys,confirmed:true});
    for(const r of results)if(r.deleted){checkedSessions.delete(r.key);delete matches[r.key];if(selected===r.key){selected=null;++detailVersion;$('#detail').innerHTML=`<div class="empty">${t('deleted')}</div>`;}}
    const failed=results.filter(r=>!r.deleted);
    $('#batch-result').innerHTML=`<p>${t('batchSummary',results.length-failed.length,failed.length)}</p>`+failed.map(r=>`<p><b>${esc(sessions.find(s=>s.key===r.key)?.title||r.key)}</b><br>${esc(r.error)}</p>`).join('');
    await refresh();$('#batch-dialog').showModal();
  }catch(e){toast(e.message);}finally{batchBusy=false;renderList();}
});
$('#providers').addEventListener('click',e=>{const b=e.target.closest('[data-provider]');if(!b)return;provider=b.dataset.provider;$('#providers .active').classList.remove('active');b.classList.add('active');renderList();});
for(const selector of ['#status','#agents','#sort']) $(selector).addEventListener('input',renderList);
$('#search').addEventListener('input',queryChanged);
$('#content-search').addEventListener('change',queryChanged);
$('#refresh').addEventListener('click',refresh);
$('#detail').addEventListener('click',async e=>{
  const b=e.target.closest('button'); if(!b)return;
  if(b.dataset.offset!==undefined)return openMatch(b.dataset.offset);
  if(b.id==='older') return older();
  if(b.id==='reload-detail') return selectSession(selected,true);
  if(!b.dataset.action)return;
  const key=selected;
  if(b.dataset.action==='delete' && !confirm(t('deleteConfirm',sessions.find(s=>s.key===key)?.title||key)))return;
  b.disabled=true;
  try{
    if(b.dataset.action.startsWith('continue-'))toast(t('converting'));
    const result=await api('/api/action',{key,action:b.dataset.action,confirmed:b.dataset.action==='delete'});
    if(result.deleted){if(selected===key){++detailVersion;selected=null;$('#detail').innerHTML=`<div class="empty">${t('deleted')}</div>`;}delete matches[key];await refresh();toast(t('deleted'));return;}
    if(result.conversion){
      toast(t('converted',result.conversion.provider,result.conversion.messages));
      $('#search').value='';matches={};provider='all';
      $('#providers .active').classList.remove('active');$('#providers [data-provider="all"]').classList.add('active');
      await refresh();
      const converted=sessions.find(s=>s.id===result.conversion.id);
      if(converted)await selectSession(converted.key);
    }else toast(t(b.dataset.action==='context-antigravity'?'contextCopied':b.dataset.action==='open-client'?'clientOpened':b.dataset.action.startsWith('copy-')?'copied':'opened'));
  }catch(err){toast(err.message);}finally{b.disabled=false;}
});
$('#sources').addEventListener('click',()=>{ $('#source-content').innerHTML=Object.entries(sourceData).map(([p,path])=>`<b>${esc(p)}</b><code>${esc(path)}</code>`).join('')+[...scanErrors,...(indexState.errors||[])].map(e=>`<p>${esc(e)}</p>`).join('');$('#source-dialog').showModal(); });
$('#close-dialog').addEventListener('click',()=>$('#source-dialog').close());
setInterval(()=>{if($('#auto').checked)refresh();},15000);
setInterval(async()=>{
  if(indexPollBusy)return;indexPollBusy=true;
  try{const wasBusy=indexState.busy;const data=await api('/api/index');showIndex(data.index);if(wasBusy||data.index.busy)await searchContent();}catch(e){/* Normal refresh reports connection errors. */}finally{indexPollBusy=false;}
},5000);
let savedTheme;try{savedTheme=localStorage.getItem('clodex-theme');}catch(e){}
if(['dark','light','hyper','graphite'].includes(savedTheme))$('#theme').value=savedTheme;
document.documentElement.dataset.theme=$('#theme').value;
$('#theme').addEventListener('change',()=>{document.documentElement.dataset.theme=$('#theme').value;try{localStorage.setItem('clodex-theme',$('#theme').value);}catch(e){}});
const initial=new URLSearchParams(location.search);
if(initial.get('q'))$('#search').value=initial.get('q');
refresh().then(async()=>{
  if(initial.get('q'))await searchContent();
  const session=sessions.find(s=>s.id===initial.get('session'));
  if(session)await selectSession(session.key);
});
