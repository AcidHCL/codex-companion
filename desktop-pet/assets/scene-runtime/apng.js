// Independent APNG parser/compositor. PNG decompression remains in the browser.
// No third-party executable content or decoder is loaded from animation packs.
const signature = new Uint8Array([137,80,78,71,13,10,26,10]);
const crcTable = Uint32Array.from({length:256}, (_, i) => {
  let c=i; for(let k=0;k<8;k++) c=(c&1) ? 0xedb88320^(c>>>1) : c>>>1; return c>>>0;
});
function crc(bytes) { let c=0xffffffff; for(const b of bytes)c=crcTable[(c^b)&255]^(c>>>8); return (c^0xffffffff)>>>0; }
function chunk(type, data) {
  const bytes=new Uint8Array(12+data.length), v=new DataView(bytes.buffer);
  v.setUint32(0,data.length); for(let i=0;i<4;i++)bytes[4+i]=type.charCodeAt(i);
  bytes.set(data,8);v.setUint32(8+data.length,crc(bytes.subarray(4,8+data.length)));return bytes;
}
function requireThat(condition,message){if(!condition)throw new Error(message);}

export function parseApng(buffer) {
  const bytes=new Uint8Array(buffer), view=new DataView(buffer);
  requireThat(bytes.length>=33&&signature.every((x,i)=>bytes[i]===x),'Invalid PNG signature');
  let ihdr, width=0,height=0, expected=0, sequence=0, current=null, ended=false, dataStarted=false;
  const shared=[], frames=[];
  for(let pos=8;pos+12<=bytes.length;) {
    const length=view.getUint32(pos);requireThat(length<=96*1024*1024&&pos+length+12<=bytes.length,'Truncated PNG chunk');
    const type=String.fromCharCode(...bytes.subarray(pos+4,pos+8)), data=bytes.slice(pos+8,pos+8+length), d=new DataView(data.buffer);
    requireThat(crc(bytes.subarray(pos+4,pos+8+length))===view.getUint32(pos+8+length),'PNG CRC mismatch');
    if(type==='IHDR') {
      requireThat(pos===8&&length===13,'Invalid IHDR');ihdr=data;width=d.getUint32(0);height=d.getUint32(4);
      requireThat(width>0&&height>0&&width<=4096&&height<=4096,'APNG dimensions exceed 4096');
    } else if(type==='acTL') {
      requireThat(!dataStarted&&length===8&&!expected,'Invalid acTL');expected=d.getUint32(0);
      requireThat(expected>0&&expected<=300,'APNG requires 1–300 frames');
      requireThat(width*height*expected<=64*1024*1024,'APNG decoded memory exceeds 256 MiB');
    } else if(type==='fcTL') {
      requireThat(expected&&length===26&&d.getUint32(0)===sequence++,'Invalid APNG frame sequence');
      current={width:d.getUint32(4),height:d.getUint32(8),x:d.getUint32(12),y:d.getUint32(16),
        duration:Math.max(10,1000*(d.getUint16(20)||1)/(d.getUint16(22)||100)),dispose:data[24],blend:data[25],parts:[]};
      requireThat(current.width>0&&current.height>0&&current.x+current.width<=width&&current.y+current.height<=height&&current.dispose<=2&&current.blend<=1,'Invalid APNG frame rectangle/operation');
      frames.push(current);
    } else if(type==='IDAT') {
      dataStarted=true;
      // A PNG default image before the first fcTL is a poster, not an animation frame.
      if(current)current.parts.push(data);
    } else if(type==='fdAT') {
      dataStarted=true;requireThat(current&&length>4&&d.getUint32(0)===sequence++,'Invalid fdAT');current.parts.push(data.subarray(4));
    } else if(type==='IEND') { ended=true;break; }
    else if(!dataStarted&&['PLTE','tRNS','gAMA','cHRM','sRGB','iCCP','sBIT'].includes(type))shared.push(chunk(type,data));
    pos+=length+12;
  }
  requireThat(ihdr&&ended&&expected===frames.length&&frames.every(f=>f.parts.length),'Incomplete APNG');
  return {width,height,frames:frames.map(frame=>{
    const header=ihdr.slice(), h=new DataView(header.buffer);h.setUint32(0,frame.width);h.setUint32(4,frame.height);
    return {...frame,blob:new Blob([signature,chunk('IHDR',header),...shared,...frame.parts.map(p=>chunk('IDAT',p)),chunk('IEND',new Uint8Array())],{type:'image/png'})};
  })};
}

export async function decodeApng(buffer,signal) {
  const parsed=parseApng(buffer), surface=document.createElement('canvas');surface.width=parsed.width;surface.height=parsed.height;
  const ctx=surface.getContext('2d',{willReadFrequently:true}), frames=[];
  try {
    for(const f of parsed.frames) {
      signal?.throwIfAborted();
      const before=f.dispose===2?ctx.getImageData(0,0,parsed.width,parsed.height):null;
      const patch=await createImageBitmap(f.blob);
      try {
        signal?.throwIfAborted();
        if(f.blend===0)ctx.clearRect(f.x,f.y,f.width,f.height);
        ctx.drawImage(patch,f.x,f.y);
      } finally {patch.close();}
      frames.push({image:await createImageBitmap(surface),duration:f.duration});
      if(f.dispose===1)ctx.clearRect(f.x,f.y,f.width,f.height);
      else if(f.dispose===2){ctx.clearRect(0,0,parsed.width,parsed.height);ctx.putImageData(before,0,0);}
    }
    signal?.throwIfAborted();return {width:parsed.width,height:parsed.height,frames};
  } catch(e){for(const f of frames)f.image.close();throw e;}
  finally{surface.width=surface.height=1;}
}
