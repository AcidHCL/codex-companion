import {decodeApng} from './apng.js';
const canvas=document.getElementById('canvas'),ctx=canvas.getContext('2d'),video=document.getElementById('video');
const bridge=window.chrome?.webview;
let pack=null,active=null,paused=false,disposed=false,timer=0,draws=0,serial=0;
let hasFrame=false,displayedAction=null,preservedTransitions=0,blankClears=0,retainedRequestId=null;
let audioEnabled=false,audioVolume=.35,thoughtTimer=0;
let bodyViewportHeight=null,thoughtHeadroom=0;
let motionAction=null,motionFrame=0;
const MAX_SEQUENCE_FRAMES=720,PNG_CACHE_FRAMES=12,PNG_CACHE_PIXELS=64*1024*1024,PNG_FETCH_CONCURRENCY=2;
const thoughtPanel=document.getElementById('thoughts'),thoughtTitle=document.getElementById('thought-title'),thoughtText=document.getElementById('thought-text');
const notify=(type,extra={},run=active)=>bridge?.postMessage({type,requestId:run?.id,...extra});
const urlFor=path=>{
  if(typeof path!=='string'||!path||path.split('/').some(s=>!s||s==='.'||s==='..')||/[\\?#\x00-\x1f]/.test(path)||/^[a-z][\w+.-]*:/i.test(path))throw new Error('Invalid local media path');
  return '/media/'+path.split('/').map(encodeURIComponent).join('/');
};
const alive=run=>!disposed&&active===run&&!run.abort.signal.aborted;
const requireThat=(condition,message)=>{if(!condition)throw new Error(message);};
const motionControlled=run=>!!run&&motionAction===run.actionId&&run.phase==='loop'&&run.clip?.kind!=='video';
function applyMotionFrame(run){
  if(!motionControlled(run)||!run.clip)return false;
  stopTimer();run.frame=Math.min(motionFrame,run.clip.count-1);draw(run);return true;
}
function setMotionFrame(action,frame){
  requireThat(action===null||typeof action==='string'&&action.length>0&&action.length<=80,'Invalid motion action');
  requireThat(Number.isInteger(frame)&&frame>=0&&frame<MAX_SEQUENCE_FRAMES,'Invalid motion frame');
  const previouslyControlled=motionControlled(active);
  motionAction=action;motionFrame=frame;
  if(applyMotionFrame(active))return;
  // Releasing control resumes the current frame's duration. A pending control
  // for another action never freezes an unrelated idle/emotion animation.
  if(previouslyControlled&&active?.clip)schedule(active,active.clip.durations[active.frame]);
}
function stopTimer(){if(timer)clearTimeout(timer);timer=0;}
function clearAudio(run){const sound=run?.audio;if(!sound)return;sound.pause();sound.removeAttribute('src');sound.load();run.audio=null;}
function audioSettings(enabled,volume){audioEnabled=!!enabled;audioVolume=Number.isFinite(volume)?Math.max(0,Math.min(1,volume)):0;if(active?.audio){active.audio.muted=!audioEnabled;active.audio.volume=audioVolume;}}
function clearThoughts(){if(thoughtTimer)clearTimeout(thoughtTimer);thoughtTimer=0;thoughtPanel.hidden=true;thoughtTitle.textContent='';thoughtText.textContent='';}
function viewportLayout(bodyHeight,headroom){
  bodyViewportHeight=Number.isFinite(bodyHeight)?Math.max(1,Math.min(2048,bodyHeight)):null;
  thoughtHeadroom=Number.isFinite(headroom)?Math.max(0,Math.min(1024,headroom)):0;
  const height=bodyViewportHeight===null?'100%':`${bodyViewportHeight}px`;
  canvas.style.height=height;video.style.height=height;
  thoughtPanel.style.height=`${Math.max(0,thoughtHeadroom-8)}px`;
  if(active?.clip)draw(active);
}
function updateThoughts(run){
  if(!alive(run)||!run.action.thoughts||paused)return;
  const content=run.action.thoughts;
  thoughtPanel.hidden=false;thoughtTitle.textContent=content.title;
  const seconds=run.audio?.currentTime??Math.max(0,(performance.now()-run.startedAt)/1000);
  const progress=(seconds%14.1)/14.1,text=content.lines.join('\n');
  thoughtText.textContent=text.slice(0,Math.floor(progress*text.length));
  thoughtText.scrollTop=thoughtText.scrollHeight;
  thoughtTimer=setTimeout(()=>updateThoughts(run),100);
}
function clearVideo(){video.pause();video.removeAttribute('src');video.load();video.style.display='none';canvas.style.display='block';}
function clearCanvas(){ctx.clearRect(0,0,canvas.width,canvas.height);hasFrame=false;displayedAction=null;retainedRequestId=null;blankClears++;}
function releaseClip(clip){if(clip?.release){clip.release();return;}if(clip?.frames)for(const f of clip.frames)f.image?.close();clip?.sheet?.close();}
function fit(){
  if(!active?.clip)return;
  const clip=active.clip, bounds=canvas.getBoundingClientRect(),dpr=Math.min(window.devicePixelRatio||1,2);
  const w=Math.max(1,Math.round(bounds.width*dpr)),h=Math.max(1,Math.round(bounds.height*dpr));
  if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
  const scale=Math.min(w/clip.width,h/clip.height);
  active.rect={x:(w-clip.width*scale)*(pack.anchorX??.5),y:(h-clip.height*scale)*(pack.anchorY??1),w:clip.width*scale,h:clip.height*scale};
  video.style.objectPosition=`${(pack.anchorX??.5)*100}% ${(pack.anchorY??1)*100}%`;
}
function draw(run){
  if(!alive(run)||!run.clip||run.clip.kind==='video')return false;
  const c=run.clip;
  let image;
  if(c.kind==='frames'){
    c.seek(run.frame);image=c.image(run.frame);
    // Never resize or clear the canvas before an asynchronously requested frame
    // exists. Canvas pixels survive eviction/close of the bitmap that drew them.
    if(!image){run.waitingFrame=true;return false;}
  }else if(!c.sheet)image=c.frames[run.frame].image;
  fit();const r=run.rect;ctx.clearRect(0,0,canvas.width,canvas.height);
  if(c.sheet){const x=(run.frame%c.columns)*c.width,y=Math.floor(run.frame/c.columns)*c.height;ctx.drawImage(c.sheet,x,y,c.width,c.height,r.x,r.y,r.w,r.h);}
  else ctx.drawImage(image,r.x,r.y,r.w,r.h);
  draws++;hasFrame=true;displayedAction=run.actionId;run.retainedFrameWhileLoading=false;run.waitingFrame=false;run.renderedFrame=run.frame;
  frameReady(run);return true;
}
async function getBytes(path,run,signal=run.abort.signal){
  const response=await fetch(urlFor(path),{signal,cache:'no-store'});
  if(!response.ok)throw new Error('Media load failed: '+response.status);
  const bytes=await response.arrayBuffer();requireThat(bytes.byteLength<=96*1024*1024,'Image exceeds 96 MiB');return bytes;
}
async function bitmap(path,run,signal=run.abort.signal,expected=null){
  const bytes=await getBytes(path,run,signal),header=new DataView(bytes),signature=[137,80,78,71,13,10,26,10];
  requireThat(bytes.byteLength>=33&&signature.every((x,i)=>header.getUint8(i)===x)&&header.getUint32(8)===13&&header.getUint32(12)===0x49484452,'Invalid PNG header');
  const width=header.getUint32(16),height=header.getUint32(20);
  requireThat(width>0&&height>0&&width<=4096&&height<=4096&&width*height<=PNG_CACHE_PIXELS,'PNG decoded dimensions exceed memory budget');
  requireThat(!expected||width===expected.width&&height===expected.height,'PNG frames must have matching dimensions');
  if(!alive(run)||signal.aborted)throw new DOMException('Superseded','AbortError');
  const image=await createImageBitmap(new Blob([bytes]));
  if(!alive(run)||signal.aborted){image.close();throw new DOMException('Superseded','AbortError');}
  if(image.width!==width||image.height!==height){image.close();throw new Error('Decoded PNG dimensions do not match header');}return image;
}
function duration(clip,i){return clip.durationsMs?.[i]??1000/clip.fps;}
// Only PNG sequences use this cache. APNG compositing and spritesheets retain
// their separate eager-decode limits. Pending decodes reserve cache slots too.
function pngSequence(spec,run,first,signal){
  const images=new Map([[0,first]]),jobs=new Map();
  const pixels=first.width*first.height,capacity=Math.min(PNG_CACHE_FRAMES,Math.floor(PNG_CACHE_PIXELS/pixels));
  let wanted=[],dead=false,closedFrames=0,loads=1,peakFrames=1,inflight=0;
  const clip={kind:'frames',width:first.width,height:first.height,count:spec.frames.length,
    durations:spec.frames.map((_,i)=>duration(spec,i)),image:i=>images.get(i),seek,release,
    diagnostics:()=>({capacity,cachedFrames:images.size,pendingFrames:inflight,queuedFrames:wanted.filter(i=>!images.has(i)&&!jobs.has(i)).length,
      peakFrames,peakPixels:peakFrames*pixels,closedFrames,loads})};
  function close(image){image.close();closedFrames++;}
  function release(){
    if(dead)return;dead=true;signal.removeEventListener('abort',release);
    for(const image of images.values())close(image);images.clear();
    for(const job of jobs.values())job.abort();wanted=[];
  }
  function seek(index){
    if(dead)return;
    // Prioritize the requested frame, then forward lookahead. The final slot
    // retains the previous frame for small backward physical-motion updates.
    wanted=[index];
    for(let n=1;n<Math.min(capacity,clip.count)-1;n++)wanted.push((index+n)%clip.count);
    if(wanted.length<Math.min(capacity,clip.count))wanted.push((index+clip.count-1)%clip.count);
    const keep=new Set(wanted);
    for(const [i,image] of images)if(!keep.has(i)){close(image);images.delete(i);}
    for(const [i,job] of jobs)if(!keep.has(i))job.abort();
    pump();
  }
  function pump(){
    if(dead)return;
    while(inflight<PNG_FETCH_CONCURRENCY&&images.size+inflight<capacity){
      const index=wanted.find(i=>!images.has(i)&&!jobs.has(i));if(index===undefined)break;
      const abort=new AbortController();jobs.set(index,abort);inflight++;
      void bitmap(spec.frames[index],run,abort.signal,clip).then(image=>{
        loads++;
        if(dead||abort.signal.aborted||!wanted.includes(index)){close(image);return;}
        images.set(index,image);peakFrames=Math.max(peakFrames,images.size);
        if(alive(run)&&run.clip===clip&&run.frame===index)draw(run);
      }).catch(error=>{
        if(!dead&&!abort.signal.aborted&&alive(run)&&run.clip===clip)fail(error,run);
      }).finally(()=>{inflight--;jobs.delete(index);pump();});
    }
  }
  signal.addEventListener('abort',release,{once:true});
  if(signal.aborted)release();
  return clip;
}
async function prepare(spec,run){
  const type=spec.type,signal=run.phaseAbort.signal;
  if(type==='video')return {kind:'video',spec,width:1,height:1};
  if(type==='apng'){
    const apng=await decodeApng(await getBytes(spec.file,run,signal),signal);
    return {...apng,kind:type,count:apng.frames.length,durations:apng.frames.map(f=>f.duration)};
  }
  if(type==='spritesheet'){
    const sheet=await bitmap(spec.file,run,signal),columns=Math.floor(sheet.width/spec.frameWidth),rows=Math.floor(sheet.height/spec.frameHeight);
    if(!Number.isInteger(spec.frameWidth)||!Number.isInteger(spec.frameHeight)||spec.frameWidth<=0||spec.frameHeight<=0||!Number.isInteger(spec.frameCount)||spec.frameCount<1||spec.frameCount>300||columns*rows<spec.frameCount||sheet.width%spec.frameWidth||sheet.height%spec.frameHeight){sheet.close();throw new Error('Invalid spritesheet grid');}
    return {kind:type,sheet,columns,width:spec.frameWidth,height:spec.frameHeight,count:spec.frameCount,durations:Array.from({length:spec.frameCount},(_,i)=>duration(spec,i))};
  }
  requireThat(type==='frames'&&Array.isArray(spec.frames)&&spec.frames.length>0&&spec.frames.length<=MAX_SEQUENCE_FRAMES,'Invalid PNG frame list');
  const first=await bitmap(spec.frames[0],run,signal);
  return pngSequence(spec,run,first,signal);
}
function frameReady(run){if(!run.ready){run.ready=true;notify('playing',{},run);}}
function schedule(run,ms,due=performance.now()+ms){stopTimer();run.remaining=ms;run.due=due;if(!paused&&!motionControlled(run))timer=setTimeout(()=>advance(run),Math.max(0,due-performance.now()));}
function advance(run){
  timer=0;if(!alive(run)||paused||!run.clip||motionControlled(run))return;
  const now=performance.now();let due=run.due;
  // Keep the source deadline, including loop wrap. Decode and drawing latency
  // must not accumulate across hundreds of 41/42 ms frames.
  while(now>=due){
    if(++run.frame>=run.clip.count){
      if(run.phase==='loop'&&(run.repeat||run.action.audio&&!run.audioEnded)&&!run.finishing){run.frame=0;run.cycles++;}
      else{phaseEnded(run);return;}
    }
    due+=run.clip.durations[run.frame];
  }
  draw(run);schedule(run,Math.max(0,due-now),due);
}
function waitVideo(event,run,timeout=15000){
  return new Promise((resolve,reject)=>{
    const clean=()=>{clearTimeout(t);video.removeEventListener(event,yes);video.removeEventListener('error',no);run.abort.signal.removeEventListener('abort',abort);};
    const yes=()=>{clean();resolve();},no=()=>{clean();reject(new Error('Video decode failed ('+(video.error?.code??'unknown')+')'));},abort=()=>{clean();reject(new DOMException('Superseded','AbortError'));};
    const t=setTimeout(()=>{clean();reject(new Error('Video load timeout'));},timeout);
    video.addEventListener(event,yes,{once:true});video.addEventListener('error',no,{once:true});run.abort.signal.addEventListener('abort',abort,{once:true});
  });
}
function waitAudio(sound,event,run,timeout=15000){
  return new Promise((resolve,reject)=>{
    const clean=()=>{clearTimeout(t);sound.removeEventListener(event,yes);sound.removeEventListener('error',no);run.abort.signal.removeEventListener('abort',abort);};
    const yes=()=>{clean();resolve();},no=()=>{clean();reject(new Error('Audio decode failed'));},abort=()=>{clean();reject(new DOMException('Superseded','AbortError'));};
    const t=setTimeout(()=>{clean();reject(new Error('Audio load timeout'));},timeout);
    sound.addEventListener(event,yes,{once:true});sound.addEventListener('error',no,{once:true});run.abort.signal.addEventListener('abort',abort,{once:true});
  });
}
async function prepareAudio(run){
  if(!run.action.audio||run.audioPrepared)return;
  const sound=new Audio();run.audio=sound;sound.preload='auto';sound.loop=false;sound.muted=!audioEnabled;sound.volume=audioVolume;
  const metadata=waitAudio(sound,'loadedmetadata',run);
  sound.src=urlFor(run.action.audio.file);sound.load();await metadata;
  if(!alive(run))return;
  requireThat(Number.isFinite(sound.duration)&&sound.duration>0&&sound.duration<=600,'Audio must be at most 10 minutes');
  if(sound.readyState<2)await waitAudio(sound,'loadeddata',run);
  if(!alive(run))return;
  sound.addEventListener('ended',()=>{if(!alive(run)||run.audio!==sound)return;run.audioEnded=true;run.finishing=true;clearAudio(run);void beginPhase(run,'end');});
  sound.addEventListener('error',()=>{if(alive(run)&&run.audio===sound)fail(new Error('Audio decode failed'),run);});
  run.audioPrepared=true;
}
async function startAudio(run){if(!run.audio||run.audioStarted||paused||!alive(run))return;run.audioStarted=true;await run.audio.play();}
async function beginPhase(run,phase){
  if(!alive(run))return;stopTimer();run.phaseAbort?.abort();run.phaseAbort=new AbortController();run.phase=phase;run.renderedFrame=null;
  const phaseSerial=run.phaseSerial=(run.phaseSerial??0)+1;
  const current=()=>alive(run)&&run.phaseSerial===phaseSerial;
  const spec=run.action[phase];
  if(!spec){phaseEnded(run);return;}
  clearVideo();releaseClip(run.clip);run.clip=null;run.frame=0;
  try{
    await prepareAudio(run);
    if(!current())return;
    const clip=await prepare(spec,run);
    if(!current()){releaseClip(clip);return;}
    run.clip=clip;
    if(clip.kind==='video'){
      const metadata=waitVideo('loadedmetadata',run);video.muted=true;video.loop=false;video.src=urlFor(spec.file);video.load();await metadata;
      if(!current())return;
      requireThat(Number.isFinite(video.duration)&&video.duration>0&&video.duration<=60&&video.videoWidth>0&&video.videoHeight>0&&video.videoWidth<=2048&&video.videoHeight<=2048&&video.videoWidth*video.videoHeight<=4*1024*1024,'Video must be <=60 seconds, <=2048 per side and <=4 megapixels');
      clip.width=video.videoWidth;clip.height=video.videoHeight;fit();
      if(video.readyState<2)await waitVideo('loadeddata',run);
      if(!current())return;
      canvas.style.display='none';video.style.display='block';hasFrame=true;displayedAction=run.actionId;run.retainedFrameWhileLoading=false;frameReady(run);
      if(!paused)await video.play();
    }else{if(!applyMotionFrame(run))draw(run);schedule(run,clip.durations[run.frame]);}
    await startAudio(run);
    if(!current())return;
    if(!run.thoughtsStarted){run.thoughtsStarted=true;run.startedAt=performance.now();updateThoughts(run);}
    notify('phase',{phase},run);
  }catch(e){if(current())fail(e,run);}
}
function phaseEnded(run){
  if(!alive(run))return;
  if(run.phase==='start'){void beginPhase(run,run.finishing?'end':'loop');return;}
  if(run.phase==='loop'&&(run.repeat||run.action.audio&&!run.audioEnded)&&!run.finishing){
    run.cycles++;
    if(run.clip?.kind==='video'){video.currentTime=0;if(!paused)void video.play().catch(e=>fail(e,run));}
    else{run.frame=0;draw(run);schedule(run,run.clip.durations[0]);}
    return;
  }
  if(run.phase!=='end'){void beginPhase(run,'end');return;}
  complete(run);
}
function complete(run){
  if(!alive(run))return;
  // Automatic one-shot previews notify the host but retain their last frame
  // until it resumes idle/working. An explicit stop still removes all pixels.
  const retained=cancel(false,run.preserveFrame&&!run.finishing);
  if(retained)retainedRequestId=run.id;
  notify('completed',{},run);
}
function fail(error,run){if(!alive(run))return;const message=String(error?.message||error);cancel(false);notify('error',{message},run);}
function cancel(send=true,preserveFrame=false){
  stopTimer();const run=active;
  // Keep the last rendered pixels while the next state decodes. A video must be
  // copied first because detaching its source also removes its visible frame.
  if(preserveFrame&&run?.clip?.kind==='video'&&video.readyState>=2&&run.ready){
    fit();const r=run.rect;ctx.clearRect(0,0,canvas.width,canvas.height);ctx.drawImage(video,r.x,r.y,r.w,r.h);
    hasFrame=true;displayedAction=run.actionId;
  }
  active=null;run?.abort.abort();run?.phaseAbort?.abort();clearVideo();clearAudio(run);clearThoughts();releaseClip(run?.clip);
  const retained=preserveFrame&&hasFrame;
  if(retained)preservedTransitions++;else clearCanvas();
  if(send&&run)notify('completed',{cancelled:true},run);
  return retained;
}
function pause(value){
  if(value===paused)return;paused=value;
  if(!active)return;
  if(paused){if(timer)active.remaining=Math.max(0,active.due-performance.now());stopTimer();video.pause();active.audio?.pause();active.pausedAt=performance.now();if(thoughtTimer)clearTimeout(thoughtTimer);thoughtTimer=0;}
  else{
    const run=active;if(run.pausedAt)run.startedAt+=performance.now()-run.pausedAt;run.pausedAt=0;
    if(run.clip?.kind==='video')void video.play().catch(e=>fail(e,run));
    else if(run.clip)schedule(run,run.remaining??run.clip.durations[run.frame]);
    if(run.audio&&run.audioPrepared){run.audioStarted=true;void run.audio.play().catch(e=>fail(e,run));}
    if(run.thoughtsStarted)updateThoughts(run);
  }
}
function message(data){
  try{
    if(data.type==='load'){cancel(false);pack=data.pack;notify('loaded',{requestId:data.requestId});}
    else if(data.type==='play'||data.type==='switch'){
      requireThat(typeof data.requestId==='string'&&data.requestId.length>0&&data.requestId.length<=128,'Invalid scene request id');
      requireThat(pack&&Object.hasOwn(pack.actions,data.action),'Unknown scene action');
      const retained=cancel(false,data.type==='switch');
      const action=pack.actions[data.action],run={id:data.requestId,serial:++serial,actionId:data.action,action,repeat:!!data.repeat&&action.repeat!==false,preserveFrame:data.type==='switch',finishing:false,ready:false,abort:new AbortController(),clip:null,frame:0,cycles:0,phase:'start',retainedFrameWhileLoading:retained,audio:null,audioPrepared:false,audioStarted:false,audioEnded:false,thoughtsStarted:false,startedAt:performance.now()};
      retainedRequestId=null;
      active=run;void beginPhase(run,action.start?'start':'loop');
    }else if(data.type==='pause')pause(!!data.paused);
    else if(data.type==='viewport-layout')viewportLayout(data.bodyHeight,data.thoughtHeadroom);
    else if(data.type==='motionFrame')setMotionFrame(data.action,data.frame);
    else if(data.type==='audio-settings')audioSettings(data.enabled,data.volume);
    else if(data.type==='stop'){
      if(data.requestId&&(active?.id??retainedRequestId)!==data.requestId)return;
      if(data.immediate||!active||!active.ready)cancel();else {clearAudio(active);active.audioEnded=true;active.finishing=true;if(motionControlled(active)){setMotionFrame(null,0);}}
    }else if(data.type==='dispose'){cancel(false);disposed=true;observer.disconnect();}
  }catch(e){notify('error',{message:String(e.message||e),requestId:data.requestId});}
}
video.addEventListener('ended',()=>{if(active?.clip?.kind==='video')phaseEnded(active);});
video.addEventListener('error',()=>{if(active?.clip?.kind==='video')fail(new Error('Video decode failed'),active);});
const observer=new ResizeObserver(()=>{if(active)draw(active);});observer.observe(canvas);
// WPF owns pause for hidden windows, display suspension and reduced motion.
// WebView can report document.hidden when an otherwise active pet is merely
// occluded. Pausing only this timeline would let physics finish an externally
// driven jump but permanently freeze its timed landing/idle continuation.
bridge?.addEventListener('message',event=>message(event.data));
window.sceneDiagnostics=()=>({ready:!!pack,motionAction,motionFrame,motionControlled:motionControlled(active),playing:!!active,paused,phase:active?.phase??null,frame:active?.frame??null,renderedFrame:active?.renderedFrame??null,waitingFrame:active?.waitingFrame??false,cache:active?.clip?.diagnostics?.()??null,kind:active?.clip?.kind??null,cycles:active?.cycles??0,draws,timerActive:!!timer,videoTime:video.currentTime,videoPaused:video.paused,muted:video.muted,requestId:active?.id??null,action:active?.actionId??null,displayedAction,retainedFrameWhileLoading:active?.retainedFrameWhileLoading??false,preservedTransitions,blankClears,hasFrame,audioTime:active?.audio?.currentTime??0,audioDuration:active?.audio?.duration??0,audioPaused:active?.audio?.paused??true,audioMuted:active?.audio?.muted??true,audioVolume,audioEnabled,audioActive:!!active?.audio,thoughtVisible:!thoughtPanel.hidden,thoughtText:thoughtText.textContent,bodyViewportHeight,thoughtHeadroom,mediaRect:active?.rect??null,canvasWidth:canvas.width,canvasHeight:canvas.height,canvasTop:canvas.getBoundingClientRect().top,viewportHeight:window.innerHeight});
notify('ready');
