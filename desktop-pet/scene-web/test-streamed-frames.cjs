const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../assets/scene-runtime/scene.js'), 'utf8')
  .replace("import {decodeApng} from './apng.js';", '');

function harness(size = 512) {
  const messages = [], images = [], requests = [], timers = new Map(), gates = new Map();
  let receive, now = 0, nextTimer = 0, pixels = null, live = 0, peak = 0, aborted = 0, drawCost = 0;
  const canvas = { width: 512, height: 512, style: {}, getBoundingClientRect: () => ({width:512,height:512}),
    getContext: () => ({clearRect(){ pixels = null; }, drawImage(image){assert.ok(!image.closed);pixels=image.label;now+=drawCost;}}) };
  const video = {style:{},pause(){},removeAttribute(){},load(){},addEventListener(){}};
  const elements = new Map();
  const waitGate = (key, signal) => {
    if (!gates.has(key)) return Promise.resolve();
    return new Promise((resolve,reject) => {
      const abort = () => {aborted++;reject(new DOMException('Cancelled','AbortError'));};
      if (signal?.aborted) return abort();
      signal?.addEventListener('abort',abort,{once:true});
      gates.get(key).then(() => {signal?.removeEventListener('abort',abort);resolve();});
    });
  };
  const sandbox = {
    window:{devicePixelRatio:1,chrome:{webview:{postMessage:m=>messages.push(m),addEventListener:(_,fn)=>receive=fn}}},
    document:{getElementById(id){if(id==='canvas')return canvas;if(id==='video')return video;if(!elements.has(id))elements.set(id,{style:{}});return elements.get(id);}},
    ResizeObserver:class {observe(){}disconnect(){}},Blob,DataView,Uint8Array,AbortController,DOMException,
    performance:{now:()=>now},
    setTimeout(fn,ms){const id=++nextTimer;timers.set(id,{fn,at:now+ms});return id;},clearTimeout:id=>timers.delete(id),
    async fetch(url,{signal}) {
      requests.push(url);await waitGate(url,signal);
      const match=url.match(/(\d+)\.png$/), label=Number(match?.[1]??900);
      const bytes=new Uint8Array(33),view=new DataView(bytes.buffer);
      bytes.set([137,80,78,71,13,10,26,10]);view.setUint32(8,13);view.setUint32(12,0x49484452);
      view.setUint32(16,size);view.setUint32(20,size);view.setUint32(28,label);
      return {ok:true,arrayBuffer:async()=>bytes.buffer};
    },
    async createImageBitmap(blob) {
      const bytes=await blob.arrayBuffer(),v=new DataView(bytes),label=v.getUint32(28);
      await waitGate('decode:'+label);
      const image={width:size,height:size,label,close(){assert.ok(!this.closed,'bitmap released twice');this.closed=true;live--;}};
      images.push(image);live++;peak=Math.max(peak,live);return image;
    }
  };
  vm.runInNewContext(source,sandbox,{filename:'scene.js'});
  const send=data=>receive({data});
  const times=Array.from({length:456},(_,i)=>Math.round((i+1)*19000/456)-Math.round(i*19000/456));
  const clip=(n,base=0)=>({type:'frames',frames:Array.from({length:n},(_,i)=>(base+i)+'.png'),fps:24,
    durationsMs:n===456?times:Array.from({length:n},()=>42)});
  send({type:'load',pack:{actions:{dance:{loop:clip(456),repeat:false},idle:{loop:clip(1,900),repeat:true},
    phased:{start:clip(3,800),loop:clip(456),end:clip(2,850),repeat:true}}}});
  return {send,images,messages,requests,timers,diag:()=>sandbox.window.sceneDiagnostics(),pixels:()=>pixels,
    live:()=>live,peak:()=>peak,aborted:()=>aborted,now:()=>now,setDrawCost:ms=>drawCost=ms,
    hold(key){let release;gates.set(key,new Promise(r=>release=r));return release;},
    tick(ms){now+=ms;const ready=[...timers].filter(([,t])=>t.at<=now);for(const [id,t] of ready){timers.delete(id);t.fn();}},
    next(){const at=Math.min(...[...timers.values()].map(t=>t.at));if(Number.isFinite(at))this.tick(Math.max(0,at-now));}
  };
}
async function settled(){for(let i=0;i<300;i++)await Promise.resolve();}
async function play(h,action='dance',repeat=false){h.send({type:'switch',action,requestId:action,repeat});await settled();}

test('456-frame PNG starts on demand, jumps to frame 455, keeps a bounded cache and closes all images',async()=>{
  const h=harness();await play(h);assert.equal(h.pixels(),0);assert.ok(h.requests.length<=12);
  assert.equal(h.diag().cache.cachedFrames,12);
  for(const frame of [455,230,27,451,0]){
    const previous=h.pixels();h.send({type:'motionFrame',action:'dance',frame});
    assert.ok(h.pixels()===previous||h.pixels()===frame,'never clears while loading');
    await settled();assert.equal(h.pixels(),frame);assert.equal(h.diag().renderedFrame,frame);
    assert.ok(h.diag().cache.cachedFrames<=12);assert.ok(h.diag().cache.peakPixels<=64*1024*1024);
  }
  assert.ok(h.peak()<=12,`peak ${h.peak()} exceeds reserved capacity`);
  h.send({type:'dispose'});await settled();assert.equal(h.live(),0);assert.ok(h.images.every(i=>i.closed));
});

test('missing target keeps pixels, obsolete fetches abort and late unabortable bitmap closes',async()=>{
  const h=harness();await play(h);const first=h.pixels();
  const fetchRelease=h.hold('/media/300.png');h.send({type:'motionFrame',action:'dance',frame:300});await settled();
  assert.equal(h.pixels(),first);assert.equal(h.diag().waitingFrame,true);
  h.send({type:'motionFrame',action:'dance',frame:455});await settled();
  assert.equal(h.pixels(),455);assert.ok(h.aborted()>0);fetchRelease();await settled();assert.equal(h.pixels(),455);
  const decodeRelease=h.hold('decode:200');h.send({type:'motionFrame',action:'dance',frame:200});await settled();
  await play(h,'idle',true);assert.equal(h.pixels(),900);decodeRelease();await settled();
  assert.equal(h.pixels(),900);assert.ok(h.images.filter(i=>i.label===200).every(i=>i.closed));
  assert.equal(h.messages.some(m=>m.type==='error'),false);
  h.send({type:'dispose'});await settled();assert.equal(h.live(),0);
});

test('24 fps uses exact source deadlines across 456 frames, even with drawing overhead',async()=>{
  const h=harness();h.setDrawCost(3);await play(h);const started=h.now();
  let count=0;
  while(h.diag().playing&&count++<500){h.next();await settled();}
  assert.equal(h.now()-started,19000,'456 frames must take 19000 ms, not 19000 + per-frame draw time');
  assert.equal(h.messages.filter(m=>m.type==='completed').length,1);assert.equal(h.live(),0);
  assert.ok(h.peak()<=12);assert.equal(h.messages.some(m=>m.type==='error'),false);
});

test('start/loop/end, external late frame, pause, and graceful stop release each phase',async()=>{
  const h=harness();h.send({type:'motionFrame',action:'phased',frame:455});await play(h,'phased',true);
  assert.equal(h.diag().phase,'start');assert.equal(h.pixels(),800);assert.equal(h.diag().motionControlled,false);
  for(let n=0;n<3;n++){h.next();await settled();}
  assert.equal(h.diag().phase,'loop');assert.equal(h.pixels(),455);assert.equal(h.diag().timerActive,false);
  h.send({type:'pause',paused:true});h.tick(5000);h.send({type:'pause',paused:false});await settled();
  assert.equal(h.diag().timerActive,false);assert.equal(h.pixels(),455);
  h.send({type:'stop',requestId:'phased',immediate:false});h.next();await settled();
  assert.equal(h.diag().phase,'end');assert.equal(h.pixels(),850);
  h.next();await settled();h.next();await settled();assert.equal(h.diag().playing,false);assert.equal(h.live(),0);
});

test('large frames reduce cache capacity to preserve 64 Mi pixel budget',async()=>{
  const h=harness(4096);await play(h);assert.equal(h.diag().cache.capacity,4);assert.equal(h.live(),4);
  h.send({type:'motionFrame',action:'dance',frame:455});await settled();assert.equal(h.pixels(),455);
  assert.ok(h.peak()<=4);h.send({type:'dispose'});await settled();assert.equal(h.live(),0);
});
