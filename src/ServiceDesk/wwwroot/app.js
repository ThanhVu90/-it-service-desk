'use strict';
const $ = id => document.getElementById(id);
let me, csrf, page = 1, total = 0, currentTicket, editAction;
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const date = value => new Date(value).toLocaleString([], {month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});
async function token(){ csrf = (await (await fetch('/api/csrf')).json()).token; }
async function api(path, data){
 const response = await fetch('/api'+path, {method:data === undefined?'GET':'POST',headers:data === undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf},body:data === undefined?undefined:JSON.stringify(data)});
 if(!response.ok){const error=await response.json().catch(()=>({}));if(response.status===401 && path!=='/login'){me=null;$('workspace').hidden=true;$('login-view').hidden=false;await token();}throw new Error(error.title || `Request failed (${response.status}).`);}
 return response.status===204?null:response.json();
}
function notify(message){$('notice').textContent=message;$('notice').hidden=false;}
function guarded(fn){return async event=>{try{await fn(event);}catch(error){notify(error.message);}};}
async function showWorkspace(){
 $('login-view').hidden=true;$('workspace').hidden=false;$('identity').textContent=`${me.name} · ${me.role}`;
 $('new-asset').hidden=me.role!=='Admin'; await token(); await loadTickets();
}
async function loadTickets(){
 const params=new URLSearchParams({page,pageSize:10,search:$('search').value});if($('status-filter').value)params.set('status',$('status-filter').value);
 const [result,stats]=await Promise.all([api('/tickets?'+params),api('/dashboard')]);total=result.total;
 $('metric-open').textContent=stats.open;$('metric-progress').textContent=stats.inProgress;$('metric-resolved').textContent=stats.resolved;$('metric-overdue').textContent=stats.overdue;
 $('tickets').innerHTML=result.items.map(t=>`<tr><td class="title"><small>REQ-${String(t.id).padStart(4,'0')}</small>${esc(t.title)}</td><td><span class="badge ${esc(t.priority)}">${esc(t.priority)}</span></td><td><span class="badge ${esc(t.status)}">${esc(t.status)}</span></td><td class="${new Date(t.dueAt)<new Date()&&['Open','InProgress'].includes(t.status)?'overdue':''}">${date(t.dueAt)}</td><td><button class="secondary small" data-ticket="${t.id}">View →</button></td></tr>`).join('')||'<tr><td colspan="5" class="empty">No requests match your filters.</td></tr>';
 $('page-info').textContent=`${total} request${total===1?'':'s'} · Page ${page}`;$('previous').disabled=page===1;$('next').disabled=page*10>=total;
 for(const button of document.querySelectorAll('[data-ticket]'))button.onclick=guarded(()=>openTicket(button.dataset.ticket));
}
async function openTicket(id){
 const {ticket:t,audit}=await api('/tickets/'+id);currentTicket=t;
 let action='';
 if(t.status==='Open'&&me.role!=='Employee')action='<button data-transition="InProgress">Take ownership</button>';
 if(t.status==='InProgress'&&(me.role==='Admin'||me.id===t.assigneeId))action='<label>Resolution notes<textarea id="resolution" maxlength="2000" placeholder="What changed and how did you verify it?"></textarea></label><button data-transition="Resolved">Mark resolved</button>';
 if(t.status==='Resolved'&&(me.role==='Admin'||me.id===t.requesterId))action='<button data-transition="Closed">Confirm & close</button>';
 $('detail-content').innerHTML=`<p class="eyebrow">REQ-${String(t.id).padStart(4,'0')} / VERSION ${t.version}</p><h2>${esc(t.title)}</h2><span class="badge ${esc(t.status)}">${esc(t.status)}</span> <span class="badge ${esc(t.priority)}">${esc(t.priority)}</span><p class="detail-description">${esc(t.description)}</p><p class="muted">Due ${date(t.dueAt)} · Requester #${t.requesterId}${t.assigneeId?' · Assigned to #'+t.assigneeId:''}</p>${t.resolution?'<h3>Resolution</h3><p class="detail-description">'+esc(t.resolution)+'</p>':''}<h3>Activity</h3><div class="timeline">${audit.map(a=>`<p>${esc(a.action)}<small>${date(a.at)} · User #${a.actorId}</small></p>`).join('')||'<p class="muted">Seeded sample · no changes yet.</p>'}</div><h3>Discussion</h3><div id="discussion"></div><form id="comment-form"><label>Reply<textarea id="comment-text" maxlength="2000" required placeholder="Add context or ask a question that belongs to this request..."></textarea></label><button type="submit">Send reply</button><p id="comment-error" class="error" role="alert"></p></form><div>${action}</div><p id="detail-error" class="error" role="alert"></p>`;
 for(const button of document.querySelectorAll('[data-transition]'))button.onclick=async()=>{button.disabled=true;try{await api(`/tickets/${t.id}/transition`,{version:t.version,status:button.dataset.transition,resolution:$('resolution')?.value});await loadTickets();await openTicket(t.id);}catch(error){$('detail-error').textContent=error.message;}finally{button.disabled=false;}};
 loadComments(t.id,$('discussion'));
 if(!$('detail').open)$('detail').showModal();
}
function editor(title,fields,action){$('editor-title').textContent=title;$('editor-fields').innerHTML=fields;$('editor-error').textContent='';editAction=action;$('editor').showModal();}
$('new-ticket').onclick=()=>editor('New support request','<label>Title<input name="title" required maxlength="160" placeholder="Summarize the issue"></label><label>Description<textarea name="description" required maxlength="4000" placeholder="What happened? What have you already tried?"></textarea></label><label>Priority<select name="priority"><option>Normal</option><option>High</option><option>Low</option></select></label>',async data=>{await api('/tickets',data);page=1;await loadTickets();notify('Request created.');});
$('editor-form').onsubmit=async e=>{e.preventDefault();$('save-editor').disabled=true;try{await editAction(Object.fromEntries(new FormData(e.target)));$('editor').close();}catch(error){$('editor-error').textContent=error.message;}finally{$('save-editor').disabled=false;}};
$('close-editor').onclick=()=>$('editor').close();$('close-detail').onclick=()=>$('detail').close();
$('login-form').onsubmit=async e=>{e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;$('login-error').textContent='';try{await token();me=await api('/login',Object.fromEntries(new FormData(e.target)));e.target.elements.password.value='';await showWorkspace();}catch(error){$('login-error').textContent=error.message;}finally{button.disabled=false;}};
$('logout').onclick=guarded(async()=>{await api('/logout',{});location.reload();});
$('filters').onsubmit=guarded(async e=>{e.preventDefault();page=1;await loadTickets();});
$('previous').onclick=guarded(async()=>{page--;await loadTickets();});$('next').onclick=guarded(async()=>{page++;await loadTickets();});
async function loadAssets(){
 const assets=await api('/assets');
 $('assets').innerHTML=assets.map(a=>`<tr><td class="title">${esc(a.tag)}</td><td>${esc(a.model)}</td><td><span class="badge">${a.assignedTo?'Assigned · User #'+a.assignedTo:'Available'}</span></td><td>${me.role==='Admin'?`<button class="secondary small" data-asset="${a.id}">${a.assignedTo?'Return':'Assign'}</button> <button class="secondary small" data-history="${a.id}">History</button>`:'—'}</td></tr>`).join('')||'<tr><td colspan="4" class="empty">No devices allocated to this account.</td></tr>';
 for(const button of document.querySelectorAll('[data-asset]'))button.onclick=guarded(async()=>{const asset=assets.find(a=>a.id===Number(button.dataset.asset));if(asset.assignedTo){editor('Return '+asset.tag,'<p>Confirm the device has been physically returned. Its handover history will be retained.</p>',async()=>{await api(`/assets/${asset.id}/return`,{version:asset.version});await loadAssets();notify('Device returned.');});}else{const users=await api('/users');editor('Assign '+asset.tag,`<label>Recipient<select name="userId">${users.map(u=>`<option value="${u.id}">${esc(u.name)} · ${esc(u.role)}</option>`).join('')}</select></label>`,async data=>{await api(`/assets/${asset.id}/assign`,{userId:Number(data.userId),version:asset.version});await loadAssets();notify('Device assigned.');});}});
 for(const button of document.querySelectorAll('[data-history]'))button.onclick=guarded(async()=>{const history=await api(`/assets/${button.dataset.history}/history`);editor('Handover history',history.map(h=>`<p>User #${h.userId} · ${date(h.assignedAt)}<br><span class="muted">${h.returnedAt?'Returned '+date(h.returnedAt):'Currently assigned'}</span></p>`).join('')||'<p>No handovers yet.</p>',async()=>{});});
}
function selectSection(assets){$('ticket-section').hidden=assets;$('asset-section').hidden=!assets;$('new-ticket').hidden=assets;$('nav-assets').classList.toggle('active',assets);$('nav-tickets').classList.toggle('active',!assets);$('page-heading').textContent=$('section-label').textContent=assets?'Device inventory':'Support queue';$('page-description').textContent=assets?'Know where each device is, and who is responsible for it.':'Track requests, take ownership and keep a clear record.';$('notice').hidden=true;}
$('nav-assets').onclick=guarded(async()=>{selectSection(true);await loadAssets();});$('nav-tickets').onclick=guarded(async()=>{selectSection(false);await loadTickets();});
$('new-asset').onclick=()=>editor('Register a device','<label>Asset tag<input name="tag" required maxlength="40" placeholder="LT-003"></label><label>Model<input name="model" required maxlength="160" placeholder="Model and specification"></label>',async data=>{await api('/assets',data);await loadAssets();notify('Device registered.');});
async function loadComments(ticketId, container){
 try{
  const result=await api(`/tickets/${ticketId}/comments?page=1&pageSize=50`);
  renderComments(container,result);
  const form=$('comment-form'),text=$('comment-text'),error=$('comment-error');
  form.onsubmit=async event=>{
   event.preventDefault();error.textContent='';
   try{await api(`/tickets/${currentTicket.id}/comments`,{body:text.value});text.value='';notify('Reply saved to the request history.');await loadComments(currentTicket.id,container);}
   catch(exception){error.textContent=exception.message;}
  };
 }
 catch(error){
  container.textContent='';
  const failure=document.createElement('p');failure.className='muted';failure.textContent='Replies could not be loaded: '+error.message;container.appendChild(failure);
 }
}
function renderComments(container,result){
 container.textContent='';
 const items=Array.isArray(result&&result.items)?result.items:[];
 if(items.length===0){
  const empty=document.createElement('p');empty.className='muted';
  empty.textContent='No replies yet. Clarifying questions and context belong here.';
  container.appendChild(empty);
  return;
 }
 for(const comment of items){
  const entry=document.createElement('article');entry.className='comment';
  const byline=document.createElement('p');byline.className='comment-by';
  byline.textContent=`User #${comment.authorId} · ${date(comment.createdAt)}`;
  const body=document.createElement('p');body.className='comment-body';
  body.textContent=comment.body;
  entry.append(byline,body);
  container.appendChild(entry);
 }
 const count=document.createElement('p');count.className='muted';
 count.textContent=`Showing ${items.length} of ${result.total} ${result.total===1?'reply':'replies'}.`;
 if(items.length<result.total)container.appendChild(count);
}
(async()=>{await token();try{me=await api('/me');await showWorkspace();}catch{/* A signed-out session is expected on first visit. */}})();
