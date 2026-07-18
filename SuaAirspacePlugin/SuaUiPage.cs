namespace SuaAirspacePlugin;

public static class SuaUiPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<title>SUA Airspace</title>
<style>
*{margin:0;padding:0;box-sizing:border-box;}
html,body{min-height:100%;background:#a0aaaa;font-family:'Terminus',monospace;font-weight:bold;-webkit-user-select:none;user-select:none;}
.top{position:sticky;top:0;background:#829292;border-bottom:2px solid #5a6a6a;padding:10px 14px;display:flex;flex-wrap:wrap;gap:10px;align-items:center;z-index:5;}
.top h1{color:#000060;font-size:18px;letter-spacing:1px;margin-right:auto;}
.top .clock{color:#000060;font-size:14px;}
.top a.maplink{font-family:'Terminus',monospace;font-weight:bold;font-size:13px;border:2px solid #5a6a6a;background:#829292;color:#000060;padding:5px 10px;text-decoration:none;letter-spacing:1px;}
.top a.maplink:active{background:#000060;color:#c8ffc8;}
.request-count{display:inline-block;min-width:20px;margin-left:5px;padding:1px 5px;background:#600000;color:#fff;text-align:center;border-radius:9px;}
.controls{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:10px 14px;}
button,select,input{font-family:'Terminus',monospace;font-weight:bold;font-size:13px;border:2px solid #5a6a6a;background:#829292;color:#000060;padding:6px 10px;cursor:pointer;letter-spacing:1px;}
input[type=text],input[type=number],input[type=datetime-local]{background:#c8d0d0;cursor:text;}
input[type=text]{width:170px;}
input[type=number]{width:90px;}
button:active{background:#000060;color:#c8ffc8;}
button.on{background:#000060;color:#c8ffc8;}
button:disabled{cursor:default;color:#404a4a;background:#a0aaaa;border-color:#707878;}
button.danger-btn{border-color:#600000;color:#600000;}
.notams{margin:0 14px 4px;border:2px solid #5a6a6a;background:#8f9c9c;}
.notams .nt-head{background:#5a6a6a;color:#e0e8e8;padding:6px 10px;font-size:12px;letter-spacing:1px;display:flex;gap:10px;align-items:center;}
.notams .nt-head button{padding:3px 8px;font-size:12px;background:#829292;}
.nt-card{border-top:1px solid #5a6a6a;padding:8px 10px;font-size:13px;color:#101820;}
.nt-card .nt-title{color:#000060;font-size:14px;}
.nt-card .nt-dates{color:#404a4a;font-size:12px;margin:2px 0 4px;}
.nt-card .nt-des,.nt-card .nt-times{color:#101820;font-size:12px;word-wrap:break-word;margin-top:2px;}
.nt-card .nt-unmatched{color:#600000;font-size:12px;margin-top:2px;}
.nt-card button{margin-top:6px;margin-right:6px;}
.list{padding:0 14px 24px;}
.row{display:grid;grid-template-columns:150px 100px 140px minmax(180px,1fr) 280px 140px;gap:8px;align-items:center;background:#8f9c9c;border:1px solid #5a6a6a;border-top:none;padding:7px 10px;font-size:13px;color:#101820;}
.row.head{background:#5a6a6a;color:#e0e8e8;position:sticky;top:52px;border-top:1px solid #5a6a6a;font-size:12px;letter-spacing:1px;}
.row .nm{color:#000060;font-size:14px;}
.pill{display:inline-block;padding:3px 8px;font-size:12px;letter-spacing:1px;border:1px solid #5a6a6a;background:#a0aaaa;color:#404a4a;}
.pill.act{background:#c00000;border-color:#800000;color:#ffffff;}
.pill.pre{background:#c8a000;border-color:#806000;color:#000000;}
.pill.man{background:#000060;border-color:#000040;color:#c8ffc8;}
.pill.sched{background:#005050;border-color:#003030;color:#c8ffff;}
.pill.stage{background:#304878;border-color:#182848;color:#ffffff;}
.pill.def{background:#707878;border-color:#404848;color:#ffffff;}
.pill.user{background:#385878;border-color:#203850;color:#ffffff;}
.pill.ra1{background:#008040;border-color:#005828;color:#ffffff;}.pill.ra2{background:#d0b000;border-color:#806800;color:#000000;}.pill.ra3{background:#c00000;border-color:#800000;color:#ffffff;}
.status-pills,.row-actions{display:flex;align-items:center;gap:6px;white-space:nowrap;}
.row-actions{gap:6px;}
.rowbtn{padding:6px 6px;width:64px;}
.dataset-lock{width:134px;height:31px;border:2px solid #707878;background:#a0aaaa;color:#404848;display:flex;align-items:center;justify-content:center;font-size:11px;letter-spacing:1px;}
.user-lock{width:134px;height:31px;border:2px solid #203850;background:#8c9ca8;color:#183850;display:flex;align-items:center;justify-content:center;font-size:11px;letter-spacing:1px;}
.editor{border:1px solid #5a6a6a;border-top:none;background:#96a4a4;padding:10px 12px;font-size:13px;color:#101820;}
.editor h3{color:#000060;font-size:13px;letter-spacing:1px;margin:8px 0 4px;}
.editor h3:first-child{margin-top:0;}
.editor .lv-row,.editor .win-row{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:6px;}
.editor label{color:#404a4a;font-size:12px;}
.editor .hint{color:#404a4a;font-size:11px;margin-top:4px;}
.msg{padding:8px 14px;color:#600000;font-size:13px;min-height:28px;}
.request-panel{display:none;position:fixed;inset:0;background:rgba(20,30,30,.7);z-index:20;padding:5vh 14px;overflow:auto;}
.request-panel.open{display:block;}.request-box{max-width:820px;margin:auto;background:#a0aaaa;border:3px solid #5a6a6a;box-shadow:8px 8px 0 rgba(0,0,0,.3)}
.request-head{display:flex;align-items:center;gap:10px;padding:10px;background:#5a6a6a;color:#e0e8e8;letter-spacing:1px}.request-head span{margin-right:auto}
.request-card{padding:12px;border-top:1px solid #5a6a6a;color:#101820}.request-card:first-child{border-top:0}.request-title{color:#000060;font-size:15px}.request-meta{margin-top:5px;font-size:12px;color:#344444}.request-notes{margin-top:7px;white-space:pre-wrap;font-weight:normal}.request-actions{display:flex;gap:7px;margin-top:9px}.request-actions button{width:110px}
.request-edit{margin-top:10px;padding:10px;border:2px solid #5a6a6a;background:#96a4a4;display:grid;grid-template-columns:1fr 1fr;gap:9px}.request-edit .full{grid-column:1/-1}.request-edit label{display:block;color:#404a4a;font-size:11px;margin-bottom:3px}.request-edit input,.request-edit select,.request-edit textarea{width:100%;background:#c8d0d0}.request-edit select[multiple]{height:130px}.request-edit textarea{min-height:60px;resize:vertical}.request-edit .request-actions{grid-column:1/-1;margin-top:0}
@media (max-width:760px){
  .row{grid-template-columns:minmax(0,1fr) 100px;grid-template-areas:'name type' 'status status' 'actions actions';grid-auto-rows:auto;}
  .row .nm{grid-area:name;}
  .row .type{grid-area:type;}
  .row .status-pills{grid-area:status;}
  .row .row-actions{grid-area:actions;}
  .row > .sched,.row > .lv{display:none;}
  .row.head{grid-template-areas:'name type';}
  .row.head .st,.row.head .ac{display:none;}
  .request-edit{grid-template-columns:1fr}.request-edit .full{grid-column:auto}
}
</style>
</head>
<body>
<div class='top'>
  <h1>SUA AIRSPACE</h1>
  <button id='requestsBtn'>REQUESTS <span class='request-count' id='requestCount'>0</span></button>
  <a class='maplink' href='/request'>REQUEST FORM</a>
  <a class='maplink' href='/map'>AIRSPACE MAP</a>
  <span class='clock' id='clock'>----Z</span>
</div>
<div class='controls'>
  <button id='fAll' class='on'>ALL</button>
  <button id='fDanger'>DANGER</button>
  <button id='fRestricted'>RESTRICTED</button>
  <button id='fMilitary'>MILITARY</button>
  <input type='text' id='search' placeholder='FILTER NAME...'>
  <select id='dur'>
    <option value='0'>UNTIL DEACTIVATED</option>
    <option value='30'>30 MIN</option>
    <option value='60'>1 HR</option>
    <option value='120'>2 HR</option>
    <option value='240'>4 HR</option>
  </select>
  <button id='deactAll' class='danger-btn'>CLEAR SHARED ACTIVATIONS</button>
</div>
<div class='notams'>
  <div class='nt-head'><span>AIRSPACE NOTAMS</span><span class='pill sched'>AUTO</span><button id='scan'>REFRESH</button><span id='ntStatus'></span></div>
  <div id='ntList'></div>
</div>
<div class='msg' id='msg'></div>
<div class='list' id='list'></div>
<div class='request-panel' id='requestPanel'><div class='request-box'>
  <div class='request-head'><span>ACTIVATION REQUESTS</span><button id='closeRequests'>CLOSE</button></div>
  <div id='requestList'></div>
</div></div>
<script>
(function(){
  var areas = [];
  var requestItems = [];
  var editingRequest = null;
  var filter = 'ALL';
  var search = '';
  var editing = null;   // area name whose editor is open
  var draft = null;     // {floor, ceiling, windows:[{f,t}]} while editing

  function esc(t){
    return String(t).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/'/g,'&#39;');
  }

  function lv(v){
    if (v <= 0) return 'SFC';
    if (v >= 11000) return 'FL' + Math.round(v/100);
    return v + 'FT';
  }

  function setMsg(t){ document.getElementById('msg').textContent = t || ''; }

  // wire 'yyyyMMddHHmm' <-> input 'yyyy-MM-ddTHH:mm' (times are UTC)
  function wireToInput(w){
    return w.substring(0,4)+'-'+w.substring(4,6)+'-'+w.substring(6,8)+'T'+w.substring(8,10)+':'+w.substring(10,12);
  }
  function inputToWire(v){
    return v ? v.replace(/[-T:]/g,'') : '';
  }
  function wireToShow(w){
    return w.substring(6,8)+'/'+w.substring(4,6)+' '+w.substring(8,12);
  }

  function render(){
    var el = document.getElementById('list');
    var html = ""<div class='row head'><span>NAME</span><span>TYPE</span><span class='lv'>LEVELS</span><span class='sched'>SCHEDULE</span><span class='st'>STATUS</span><span class='ac'></span></div>"";
    var shown = 0;
    for (var i = 0; i < areas.length; i++){
      var a = areas[i];
      if (filter !== 'ALL' && a.Type.toUpperCase() !== filter) continue;
      if (search && a.Name.toUpperCase().indexOf(search) < 0) continue;
      shown++;
      var status = a.Active ? ""<span class='pill act'>ACTIVE</span>"" : (a.PreActive ? ""<span class='pill pre'>PREACT</span>"" : ""<span class='pill'>DEACTIVE</span>"");
      if (a.Controller){
        var userLabel = 'USER';
        if (a.ControllerCids && a.ControllerCids.length) userLabel += ': ' + a.ControllerCids.join(', ');
        status += ""<span class='pill user'>"" + esc(userLabel) + ""</span>"";
      } else {
        if (a.Default) status += ""<span class='pill def'>DEFAULT</span>"";
        // A DEFAULT (dataset-locked) area never shows shared-source MAN/SAVED tags.
        if (a.Manual && !a.Default) status += ""<span class='pill man'>MAN</span>"";
        if (a.Scheduled && !a.Manual) status += ""<span class='pill sched'>SCHED</span>"";
        if (a.Saved && !a.Default) status += ""<span class='pill stage'>SAVED</span>"";
      }
      if (a.RaCategory) status += ""<span class='pill "" + a.RaCategory.toLowerCase() + ""'>"" + esc(a.RaCategory) + ""</span>"";
      var sched = a.Schedule ? esc(a.Schedule) : '';
      if (a.Windows.length){
        var wins = [];
        for (var k = 0; k < a.Windows.length && k < 3; k++){
          var pr = a.Windows[k].split('-');
          wins.push(wireToShow(pr[0]) + '-' + pr[1].substring(8,12) + 'Z');
        }
        if (a.Windows.length > 3) wins.push('+' + (a.Windows.length-3));
        sched += (sched ? ' | ' : '') + 'PLN: ' + wins.join(', ');
      }
      var enc = encodeURIComponent(a.Name);
      var actBtn = a.Staged
        ? ""<button class='rowbtn' onclick=\""act('deactivate','"" + enc + ""')\"">DEACT</button>""
        : ""<button class='rowbtn' onclick=\""act('activate','"" + enc + ""')\"">ACT</button>"";
      var editBtn = ""<button class='rowbtn' onclick=\""toggleEdit('"" + enc + ""')\"">"" + (editing === a.Name ? 'CLOSE' : 'EDIT') + ""</button>"";
      var actions = a.Default
        ? ""<span class='dataset-lock'>DATASET LOCKED</span>""
        : (a.Controller ? ""<span class='user-lock'>USER LOCKED</span>"" : actBtn + editBtn);
      html += ""<div class='row'>""
        + ""<span class='nm'>"" + esc(a.Name) + ""</span>""
        + ""<span class='type'>"" + esc(a.Type) + ""</span>""
        + ""<span class='lv'>"" + lv(a.Floor) + '&ndash;' + lv(a.Ceiling) + ""</span>""
        + ""<span class='sched'>"" + (sched || '&mdash;') + ""</span>""
        + ""<span class='status-pills'>"" + status + ""</span>""
        + ""<span class='row-actions'>"" + actions + ""</span>""
        + ""</div>"";
      if (!a.Default && !a.Controller && editing === a.Name) html += renderEditor(a);
    }
    if (!areas.length) html += ""<div class='row'>The shared airspace catalogue is empty.</div>"";
    else if (!shown) html += ""<div class='row'>No areas match the filter.</div>"";
    el.innerHTML = html;
  }

  function renderEditor(a){
    var h = ""<div class='editor'>"";
    h += ""<h3>LEVELS (FT)"" + (a.LevelsEdited ? "" &mdash; EDITED"" : """") + ""</h3>"";
    h += ""<div class='lv-row'><label>FLOOR</label><input type='number' id='edFloor' step='100' value='"" + draft.floor + ""'>""
       + ""<label>CEILING</label><input type='number' id='edCeiling' step='100' value='"" + draft.ceiling + ""'>""
       + ""<button onclick='saveLevels()'>SET LEVELS</button></div>"";
    h += ""<h3>ACTIVE TIMES (UTC)</h3>"";
    for (var i = 0; i < draft.windows.length; i++){
      h += ""<div class='win-row'><label>FROM</label><input type='datetime-local' data-wi='"" + i + ""' data-side='f' value='"" + draft.windows[i].f + ""' onchange='winEdit(this)'>""
         + ""<label>TO</label><input type='datetime-local' data-wi='"" + i + ""' data-side='t' value='"" + draft.windows[i].t + ""' onchange='winEdit(this)'>""
         + ""<button onclick='winDel("" + i + "")'>X</button></div>"";
    }
    h += ""<div class='win-row'><button onclick='winAdd()'>+ ADD TIME</button><button onclick='saveWindows()'>SAVE TIMES</button>"";
    if (a.Staged) h += ""<button class='danger-btn' onclick=\""act('deactivate','"" + encodeURIComponent(a.Name) + ""')\"">CLEAR SAVED</button>"";
    h += ""</div>"";
    h += ""<div class='hint'>Times and levels are saved to the shared cloud activation for this area.</div>"";
    h += ""</div>"";
    return h;
  }

  window.toggleEdit = function(enc){
    var name = decodeURIComponent(enc);
    if (editing === name){ editing = null; draft = null; render(); return; }
    var a = null;
    for (var i = 0; i < areas.length; i++) if (areas[i].Name === name) a = areas[i];
    if (!a) return;
    editing = name;
    draft = { floor: a.Floor, ceiling: a.Ceiling, windows: [] };
    for (var k = 0; k < a.Windows.length; k++){
      var pr = a.Windows[k].split('-');
      draft.windows.push({ f: wireToInput(pr[0]), t: wireToInput(pr[1]) });
    }
    render();
  };

  window.winEdit = function(inp){
    var i = parseInt(inp.getAttribute('data-wi'), 10);
    if (!draft.windows[i]) return;
    draft.windows[i][inp.getAttribute('data-side')] = inp.value;
  };

  window.winAdd = function(){
    syncLevelDraft();
    var now = new Date();
    var pad = function(n){ return (n < 10 ? '0' : '') + n; };
    var base = now.getUTCFullYear() + '-' + pad(now.getUTCMonth()+1) + '-' + pad(now.getUTCDate()) + 'T' + pad(now.getUTCHours()) + ':' + pad(now.getUTCMinutes());
    var end = new Date(now.getTime() + 3600000);
    var baseEnd = end.getUTCFullYear() + '-' + pad(end.getUTCMonth()+1) + '-' + pad(end.getUTCDate()) + 'T' + pad(end.getUTCHours()) + ':' + pad(end.getUTCMinutes());
    draft.windows.push({ f: base, t: baseEnd });
    render();
  };

  window.winDel = function(i){
    syncLevelDraft();
    draft.windows.splice(i, 1);
    render();
  };

  function syncLevelDraft(){
    var f = document.getElementById('edFloor');
    var c = document.getElementById('edCeiling');
    if (f) draft.floor = f.value;
    if (c) draft.ceiling = c.value;
  }

  window.saveLevels = function(){
    syncLevelDraft();
    var url = '/api/sua/levels?name=' + encodeURIComponent(editing) + '&floor=' + encodeURIComponent(draft.floor) + '&ceiling=' + encodeURIComponent(draft.ceiling);
    post(url);
  };

  window.saveWindows = function(){
    syncLevelDraft();
    var parts = [];
    for (var i = 0; i < draft.windows.length; i++){
      var f = inputToWire(draft.windows[i].f);
      var t = inputToWire(draft.windows[i].t);
      if (f.length === 12 && t.length === 12) parts.push(f + '-' + t);
    }
    post('/api/sua/windows?name=' + encodeURIComponent(editing) + '&windows=' + parts.join(','));
  };

  function post(url){
    fetch(url, { method: 'POST' }).then(function(r){ return r.json(); }).then(function(d){
      setMsg(d.Success ? '' : (d.Error || 'Request failed.'));
      refresh(true);
    }).catch(function(e){ setMsg(e.message || 'Request failed.'); });
  }

  function refresh(force){
    if (editing && !force) return; // don't clobber open editor inputs
    fetch('/api/sua/areas').then(function(r){ return r.json(); }).then(function(d){
      areas = d.Areas || [];
      document.getElementById('clock').textContent = d.UtcTime || '----Z';
      setMsg('');
      if (editing && !force) return;
      render();
    }).catch(function(){ setMsg('The cloud service is temporarily unavailable.'); });
  }

  window.act = function(verb, name){
    var url = '/api/sua/' + verb + '?name=' + name;
    if (verb === 'activate') url += '&minutes=' + document.getElementById('dur').value;
    if (verb === 'deactivate' && editing === decodeURIComponent(name)){ editing = null; draft = null; }
    post(url);
  };

  function renderNotams(notams){
    var el = document.getElementById('ntList');
    if (!notams.length){
      el.innerHTML = ""<div class='nt-card'>No airspace NOTAMs found.</div>"";
      return;
    }
    var html = '';
    for (var i = 0; i < notams.length; i++){
      var n = notams[i];
      html += ""<div class='nt-card'>""
        + ""<div class='nt-title'>"" + esc(n.Title) + "" <span class='pill "" + (n.Status === 'CURRENT' ? 'act' : 'pre') + ""'>"" + esc(n.Status) + ""</span> ""
        + ""<span class='pill sched'>AUTO SCHEDULED</span></div>""
        + ""<div class='nt-dates'>"" + esc((n.Start||'').replace('T',' ').substring(0,16)) + 'Z &ndash; ' + esc((n.End||'PERM').replace('T',' ').substring(0,16)) + ""Z</div>""
        + ""<div class='nt-des'>AREAS: "" + esc(n.Designators.join(', ')) + ""</div>"";
      if (n.Windows.length)
        html += ""<div class='nt-times'>TIMES: "" + esc(n.Windows.join(', ')) + ""</div>"";
      if (n.Unmatched.length)
        html += ""<div class='nt-unmatched'>NOT IN DATASET: "" + esc(n.Unmatched.join(', ')) + ""</div>"";
      html += ""</div>"";
    }
    el.innerHTML = html;
  }

  function scanNotams(){
    document.getElementById('ntStatus').textContent = 'LOADING...';
    fetch('/api/sua/notams').then(function(r){ return r.json(); }).then(function(d){
      document.getElementById('ntStatus').textContent = '';
      if (!d.Success){ setMsg(d.Error || 'NOTAM scan failed.'); return; }
      renderNotams(d.Notams || []);
    }).catch(function(){ document.getElementById('ntStatus').textContent = ''; setMsg('NOTAM scan failed.'); });
  }

  function showRequestTime(value){
    return String(value || '').replace('T',' ').substring(0,16) + 'Z';
  }

  function requestInputTime(value){ return String(value || '').substring(0,16); }

  function renderRequestEditor(q){
    var selectedNames = q.AreaNames && q.AreaNames.length ? q.AreaNames : [q.AreaName];
    var options = '';
    for (var i = 0; i < areas.length; i++){
      options += ""<option value='"" + esc(areas[i].Name) + ""'"" + (selectedNames.indexOf(areas[i].Name) >= 0 ? ' selected' : '') + "">"" + esc(areas[i].Name) + "" ("" + esc(areas[i].Type) + "")</option>"";
    }
    var category = q.RaCategory || 'RA1';
    return ""<div class='request-edit'>""
      + ""<div class='full'><label>AIRSPACE (CTRL/SHIFT TO SELECT MULTIPLE)</label><select id='rqAreas-"" + esc(q.Id) + ""' multiple>"" + options + ""</select></div>""
      + ""<div><label>START (UTC)</label><input id='rqStart-"" + esc(q.Id) + ""' type='datetime-local' value='"" + esc(requestInputTime(q.StartUtc)) + ""'></div>""
      + ""<div><label>END (UTC)</label><input id='rqEnd-"" + esc(q.Id) + ""' type='datetime-local' value='"" + esc(requestInputTime(q.EndUtc)) + ""'></div>""
      + ""<div><label>RA CATEGORY</label><select id='rqCategory-"" + esc(q.Id) + ""'><option"" + (category === 'RA1' ? ' selected' : '') + "">RA1</option><option"" + (category === 'RA2' ? ' selected' : '') + "">RA2</option><option"" + (category === 'RA3' ? ' selected' : '') + "">RA3</option></select></div>""
      + ""<div><label>REQUESTER</label><input id='rqRequester-"" + esc(q.Id) + ""' maxlength='80' value='"" + esc(q.Requester) + ""'></div>""
      + ""<div class='full'><label>NOTES</label><textarea id='rqNotes-"" + esc(q.Id) + ""' maxlength='500'>"" + esc(q.Notes || '') + ""</textarea></div>""
      + ""<div class='request-actions'><button onclick=\""saveRequestEdit('"" + esc(q.Id) + ""')\"">SAVE CHANGES</button><button onclick='cancelRequestEdit()'>CANCEL</button></div></div>"";
  }

  function loadRequests(){
    fetch('/api/sua/requests').then(function(r){ return r.json(); }).then(function(d){
      var requests = d.Requests || [];
      requestItems = requests;
      document.getElementById('requestCount').textContent = requests.length;
      var html = '';
      for (var i = 0; i < requests.length; i++){
        var q = requests[i];
        var requestedNames = q.AreaNames && q.AreaNames.length ? q.AreaNames : [q.AreaName];
        var category = q.RaCategory || 'RA1';
        html += ""<div class='request-card'><div class='request-title'>"" + esc(requestedNames.join(', ')) + "" <span class='pill "" + category.toLowerCase() + ""'>"" + esc(category) + ""</span></div>""
          + ""<div class='request-meta'>"" + esc(showRequestTime(q.StartUtc)) + "" &ndash; "" + esc(showRequestTime(q.EndUtc)) + "" &bull; REQUESTED BY "" + esc(q.Requester) + ""</div>""
          + (q.Notes ? ""<div class='request-notes'>"" + esc(q.Notes) + ""</div>"" : '')
          + ""<div class='request-actions'><button onclick=\""editRequest('"" + esc(q.Id) + ""')\"">EDIT</button><button onclick=\""reviewRequest('"" + esc(q.Id) + ""','accept')\"">ACCEPT</button><button class='danger-btn' onclick=\""reviewRequest('"" + esc(q.Id) + ""','decline')\"">DECLINE</button></div>""
          + (editingRequest === q.Id ? renderRequestEditor(q) : '') + ""</div>"";
      }
      document.getElementById('requestList').innerHTML = html || ""<div class='request-card'>No pending activation requests.</div>"";
    }).catch(function(){ setMsg('Could not load activation requests.'); });
  }

  window.editRequest = function(id){ editingRequest = id; loadRequests(); };
  window.cancelRequestEdit = function(){ editingRequest = null; loadRequests(); };
  window.saveRequestEdit = function(id){
    var areaSelect = document.getElementById('rqAreas-' + id);
    var names = [];
    for (var i = 0; i < areaSelect.options.length; i++) if (areaSelect.options[i].selected) names.push(areaSelect.options[i].value);
    var payload = {
      Id: id, AreaNames: names,
      StartUtc: document.getElementById('rqStart-' + id).value + ':00Z',
      EndUtc: document.getElementById('rqEnd-' + id).value + ':00Z',
      RaCategory: document.getElementById('rqCategory-' + id).value,
      Requester: document.getElementById('rqRequester-' + id).value,
      Notes: document.getElementById('rqNotes-' + id).value
    };
    fetch('/api/sua/requests/update', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) })
      .then(function(r){ return r.json(); }).then(function(d){
        if (!d.Success){ setMsg(d.Error || 'Request update failed.'); return; }
        editingRequest = null; setMsg(''); loadRequests();
      }).catch(function(){ setMsg('Request update failed.'); });
  };

  window.reviewRequest = function(id, decision){
    fetch('/api/sua/requests/review', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({Id:id, Decision:decision}) })
      .then(function(r){ return r.json(); }).then(function(d){
        if (!d.Success){ setMsg(d.Error || 'Request review failed.'); return; }
        editingRequest = null; setMsg(''); loadRequests(); refresh(true);
      }).catch(function(){ setMsg('Request review failed.'); });
  };

  function setFilter(f, btn){
    filter = f;
    ['fAll','fDanger','fRestricted','fMilitary'].forEach(function(id){ document.getElementById(id).className = ''; });
    btn.className = 'on';
    render();
  }

  document.getElementById('fAll').onclick = function(){ setFilter('ALL', this); };
  document.getElementById('fDanger').onclick = function(){ setFilter('DANGER', this); };
  document.getElementById('fRestricted').onclick = function(){ setFilter('RESTRICTED', this); };
  document.getElementById('fMilitary').onclick = function(){ setFilter('MILITARY', this); };
  document.getElementById('search').oninput = function(){ search = this.value.trim().toUpperCase(); render(); };
  document.getElementById('deactAll').onclick = function(){
    editing = null; draft = null;
    fetch('/api/sua/deactivateall', { method: 'POST' }).then(function(){ refresh(true); }).catch(function(e){ setMsg(e.message || 'Request failed.'); });
  };
  document.getElementById('scan').onclick = scanNotams;
  document.getElementById('requestsBtn').onclick = function(){ document.getElementById('requestPanel').className = 'request-panel open'; loadRequests(); };
  document.getElementById('closeRequests').onclick = function(){ document.getElementById('requestPanel').className = 'request-panel'; };
  document.getElementById('requestPanel').onclick = function(e){ if (e.target === this) this.className = 'request-panel'; };

  refresh(true);
  scanNotams();
  loadRequests();
  setInterval(function(){ refresh(false); }, 5000);
  setInterval(scanNotams, 60000);
  setInterval(loadRequests, 30000);
})();
</script>
</body>
</html>";
}
