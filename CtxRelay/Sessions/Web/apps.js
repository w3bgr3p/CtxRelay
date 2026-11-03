'use strict';
const appStrings={
  apps:['Applications','Приложения','Aplicaciones'],sessions:['Sessions','Сессии','Sesiones'],
  subtitle:['Install and update desktop clients without Microsoft Store.','Установка и обновление настольных клиентов без Microsoft Store.','Instala y actualiza clientes sin Microsoft Store.'],
  detect:['Detect installations','Найти установки','Detectar instalaciones'],
  current:['Installed version','Установленная версия','Versión instalada'],latest:['Available version','Доступная версия','Versión disponible'],
  unknown:['Not checked','Не проверено','Sin comprobar'],missing:['Not installed','Не установлено','Sin instalar'],
  choose:['Use this installation','Использовать эту установку','Usar esta instalación'],
  check:['Check updates','Проверить обновления','Buscar actualizaciones'],launch:['Launch','Запустить','Abrir'],
  connect:['Choose application folder…','Выбрать папку приложения…','Elegir carpeta de la aplicación…'],folder:['Open folder','Открыть папку','Abrir carpeta'],
  portable:['Install portable…','Установить portable…','Instalar portable…'],msix:['Install MSIX','Установить MSIX','Instalar MSIX'],
  update:['Download update','Загрузить обновление','Descargar actualización'],apply:['Apply staged update','Применить загруженное','Aplicar actualización'],
  rollback:['Previous version','Предыдущая версия','Versión anterior'],remove:['Uninstall…','Удалить…','Desinstalar…'],
  discard:['Discard download','Удалить загруженное','Descartar descarga'],cancel:['Cancel download','Отменить загрузку','Cancelar descarga'],
  working:['Working…','Выполняется…','En curso…'],downloading:['Downloading','Загрузка','Descargando'],
  verifying:['Verifying package and publisher…','Проверка пакета и издателя…','Verificando paquete y editor…'],
  extracting:['Extracting portable client…','Распаковка portable-клиента…','Extrayendo cliente portable…'],
  installing:['Applying installation…','Применение установки…','Aplicando instalación…'],
  waiting:['Package verified. Close the client, then click “Apply staged update”.','Пакет проверен. Закройте клиент и нажмите «Применить загруженное».','Paquete verificado. Cierra el cliente y aplica la actualización.'],
  done:['Completed','Выполнено','Completado'],cancelled:['Download cancelled','Загрузка отменена','Descarga cancelada'],error:['Operation failed','Ошибка операции','La operación falló'],
  claudeNote:['Install portable from the official package or copy an existing installation into a separate folder. The original installation stays in place.','Установите portable из официального пакета или скопируйте установленный Claude в отдельную папку. Исходная установка сохраняется.','Instala portable desde el paquete oficial o copia una instalación a otra carpeta. Se conserva el original.'],
  makePortable:['Copy as portable…','Скопировать как portable…','Copiar como portable…'],
  copying:['Copying installed application…','Копирование установленного приложения…','Copiando la aplicación instalada…'],
  codexNote:['Portable installs into the folder you choose and keeps the bundled CLI. Existing Store installations remain separate.','Portable устанавливается в выбранную папку вместе со встроенным CLI. Существующая Store-установка остаётся отдельной.','Portable se instala en la carpeta elegida con su CLI. Las instalaciones de Store permanecen separadas.'],
  dataNote:['Clients are never closed automatically. Credentials and session histories are preserved when removing an application.','Клиенты автоматически не закрываются. При удалении приложения авторизация и история сессий сохраняются.','Los clientes no se cierran automáticamente. Se conservan credenciales e historiales al desinstalar.'],
  confirmRemove:['Uninstall {0}?\n\n{1}\n\nClient data will be preserved.','Удалить {0}?\n\n{1}\n\nДанные клиента будут сохранены.','¿Desinstalar {0}?\n\n{1}\n\nSe conservarán los datos.'],
  confirmRollback:['Restore the previous version of {0}? Close the client first.','Вернуть предыдущую версию {0}? Сначала закройте клиент.','¿Restaurar la versión anterior de {0}? Cierra el cliente primero.'],
  backup:['Client data backup: {0}','Резервная копия данных клиента: {0}','Copia de los datos del cliente: {0}'],
  upToDate:['Already up to date','Установлена актуальная версия','Ya está actualizado'],
  available:['Update available','Есть обновление','Hay una actualización'],
  currentStatus:['Up to date — no update needed','Актуальная версия — обновление не требуется','Actualizado — no necesita actualización'],
  newer:['Installed version is newer than the release','Установленная версия новее доступной','La versión instalada es más reciente'],
  unchecked:['Updates have not been checked','Обновления ещё не проверены','No se han buscado actualizaciones'],
  versionUnknown:['Version could not be read','Не удалось определить версию','No se pudo leer la versión'],
  checkFailed:['Could not check for updates','Не удалось проверить обновления','No se pudieron buscar actualizaciones'],
  checking:['Checking for updates…','Проверка обновлений…','Buscando actualizaciones…'],
  checked:['Last checked: {0}','Проверено: {0}','Última comprobación: {0}'],
  package:['Package version','Версия пакета','Versión del paquete'],
};
function at(key,...args){let value=appStrings[key]?.[{en:0,ru:1,es:2}[language]]??key;args.forEach((a,i)=>value=value.replaceAll('{'+i+'}',String(a)));return value;}
let appsVisible=false,appsState=null,appsPolling=false;
function appView(data){return {...data,apps:data.apps.map(app=>({...app,
  defaultDirectory:app.default_directory??app.defaultDirectory,checkedAt:app.checked_at??app.checkedAt,checkError:app.check_error??app.checkError,
  installations:app.installations.map(x=>({...x,packageVersion:x.package_version??x.packageVersion,appId:x.app_id??x.appId,
    canRollback:x.can_rollback??x.canRollback,updateState:x.update_state??x.updateState}))
}))};}
const appsToggle=$('#apps-toggle');appsToggle.textContent=at('apps');
function showApplications(show=true){appsVisible=show;$('#library-view').hidden=show;$('#apps-view').hidden=!show;appsToggle.textContent=at(show?'sessions':'apps');if(show)pollApps(true);}
appsToggle.addEventListener('click',()=>showApplications(!appsVisible));
async function pollApps(force=false){
  if(appsPolling||(!appsVisible&&!force))return;appsPolling=true;
  try{appsState=appView(await api('/api/apps'));renderApps();}catch(e){toast(e.message);}finally{appsPolling=false;}
}
function selectedInstall(app){return app.installations.find(x=>app.selected?.path===(x.mode==='msix'?x.appId:x.executable))||app.installations[0];}
const appMode=mode=>({external:'Standalone',portable:'Portable',msix:'MSIX'}[mode]||mode);
function renderApps(){
  if(!appsState)return;
  const button=(action,label,disabled=false,extra='')=>`<button data-app-action="${action}" ${disabled?'disabled':''} ${extra}>${esc(at(label))}</button>`;
  $('#apps-view').innerHTML=`<div class="apps-heading"><div><h1 id="apps-title">${at('apps')}</h1><p>${at('subtitle')}</p></div>${button('detect','detect',appsState.busy)}</div><div class="apps-grid">`+
    appsState.apps.map(app=>{
      const chosen=selectedInstall(app),name=app.provider==='codex'?'Codex':'Claude',busy=appsState.busy;
      const operation=app.operation,state=operation?.state||'',hasPending=!!app.pending;
      const updateState=chosen?.updateState||(!chosen?'missing':'unchecked');
      const health=appsState.busy&&operation?.action==='check'&&state==='working'?'checking':app.checkError?'checkFailed':updateState;
      const healthLabel=health==='current'?'currentStatus':health==='unknown'?'versionUnknown':health;
      return `<article class="app-panel" data-app="${app.provider}"><h2>${name} <small>${esc(app.architecture)}</small></h2>
        <div class="app-health" data-update="${esc(health)}" role="status"><strong>${esc(at(healthLabel))}</strong>${health==='available'?`<span>${esc(chosen.version||chosen.packageVersion)} → ${esc(app.release.version)}</span>`:''}</div>
        <dl class="app-versions"><dt>${at('current')}</dt><dd>${esc(chosen?(chosen.version||at('versionUnknown')):at('missing'))}</dd><dt>${at('latest')}</dt><dd>${esc(app.release?.version||at('unknown'))}</dd>${chosen?.packageVersion&&chosen.packageVersion!==chosen.version?`<dt>${at('package')}</dt><dd>${esc(chosen.packageVersion)}</dd>`:''}</dl>
        ${app.checkedAt&&new Date(app.checkedAt).getFullYear()>2000?`<p class="app-checked">${esc(at('checked',new Date(app.checkedAt).toLocaleString(locale)))}</p>`:''}
        <label>${at('choose')}<select data-app-install ${busy||!chosen?'disabled':''}>${app.installations.length?app.installations.map(x=>`<option value="${esc(x.id)}" ${x.id===chosen?.id?'selected':''}>${esc(appMode(x.mode)+' · '+(x.version||at('versionUnknown'))+' · '+x.path)}</option>`).join(''):`<option>${at('missing')}</option>`}</select></label>
        <code class="app-path">${esc(chosen?.path||app.defaultDirectory)}</code>
        <div class="app-buttons">${button('launch','launch',busy||!chosen)}${button('check','check',busy)}${button('connect','connect',busy)}${button('folder','folder',busy||!chosen)}</div>
        <div class="app-buttons">${button('update','update',busy||hasPending||updateState!=='available','class="app-update"')}${button('install','portable',busy||hasPending,'data-mode="portable"')}${button('copy','makePortable',busy||hasPending||!chosen)}${button('install','msix',busy||hasPending,'data-mode="msix"')}</div>
        <div class="app-buttons">${hasPending?button('apply','apply',busy)+button('discard','discard',busy):''}${chosen?.managed?button('rollback','rollback',busy||!chosen.canRollback):''}${chosen&&chosen.mode!=='external'?button('remove','remove',busy,'class="danger"'):''}${['downloading','copying'].includes(state)&&busy?button('cancel','cancel'):''}</div>
        ${operation&&!(state==='done'&&['check','detect'].includes(operation.action))?`<div class="app-status" data-state="${esc(state)}" role="status">${esc(at(state==='current'?'upToDate':state))}${state==='downloading'?`<progress ${operation.total?`value="${operation.received}" max="${operation.total}"`:''}></progress>${esc(bytes(operation.received))} / ${esc(bytes(operation.total))}`:''}${operation.error?`<p>${esc(operation.error)}</p>`:''}</div>`:''}
        <p class="apps-note">${at(app.provider==='codex'?'codexNote':'claudeNote')}</p>${app.backup?`<p class="apps-note">${esc(at('backup',app.backup))}</p>`:''}</article>`;
    }).join('')+`</div><p class="apps-note">${at('dataNote')}</p>`;
}
async function appAction(provider,action,id='',mode='',confirmed=false){
  try{appsState=appView(await api('/api/apps',{provider,action,id,mode,confirmed}));renderApps();}catch(e){toast(e.message);}finally{pollApps(true);}
}
$('#apps-view').addEventListener('change',e=>{
  if(e.target.matches('[data-app-install]'))appAction(e.target.closest('[data-app]').dataset.app,'select',e.target.value);
});
$('#apps-view').addEventListener('click',async e=>{
  const button=e.target.closest('[data-app-action]');if(!button||button.disabled)return;
  const panel=button.closest('[data-app]'),provider=panel?.dataset.app||'codex',action=button.dataset.appAction;
  const app=appsState.apps.find(x=>x.provider===provider),chosen=selectedInstall(app);
  if(action==='remove'&&!await confirmDeletion(at('confirmRemove',provider==='codex'?'Codex':'Claude',chosen?.path)))return;
  if(action==='rollback'&&!window.confirm(at('confirmRollback',provider==='codex'?'Codex':'Claude')))return;
  await appAction(provider,action,chosen?.id||'',button.dataset.mode||'',action==='remove');
});
setInterval(()=>{if(appsVisible&&!$('#apps-view select:focus'))pollApps();},1000);
if(location.hash==='#apps')showApplications();
