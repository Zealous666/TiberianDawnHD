from PIL import Image
from PIL.PngImagePlugin import PngInfo
import numpy as np, os

SP='/private/tmp/claude-501/-Users-moritzgiuliani-Documents-openRA-Projekte/52eb85af-251c-4a19-8a6c-d828fd02dfa2/scratchpad'
BITS='mods/cnc/bits'
UP=4                 # TS-Voxelscale ~11.5 -> unser 48
CANVAS=191           # identisch zum art2-Body-Sprite => gleicher Anker, Sequence-Offset 0
HOUSE=range(16,32)

def read_pal(p):
    d=open(p,'rb').read()
    pal=[]
    for i in range(256):
        r,g,b=d[i*3],d[i*3+1],d[i*3+2]
        pal.append(((r<<2)|(r>>4),(g<<2)|(g>>4),(b<<2)|(b>>4)))
    return pal
PAL=read_pal('/tmp/shp/unittem.pal')

def load_idx(p):
    return np.array(Image.open(p), dtype=np.uint8)

def split_layers(idx):
    """-> (body RGBA, overlay index array) at native SHP size."""
    h,w=idx.shape
    body=np.zeros((h,w,4),dtype=np.uint8)
    ov=np.zeros((h,w),dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            i=int(idx[y,x])
            if i==0: continue
            if 16<=i<=31:
                r,g,b=PAL[i]
                v=max(r,g,b)/255.0                      # Helligkeit -> House-Ramp 176..191
                ov[y,x]=176+int(round((1.0-v)*15))
            else:
                body[y,x]=(*PAL[i],255)
    return body,ov

def up_rgba(a):
    return np.array(Image.fromarray(a,'RGBA').resize((a.shape[1]*UP,a.shape[0]*UP),Image.NEAREST))
def up_idx(a):
    return np.array(Image.fromarray(a,'L').resize((a.shape[1]*UP,a.shape[0]*UP),Image.NEAREST))

def centroid(mask):
    ys,xs=np.where(mask)
    return xs.mean(), ys.mean()

# Referenz: art2-Voxel bei Deploy-Facing (TS Facing 384 ~ Frame 12)
ref=np.array(Image.open(os.path.join(SP,'body-art2','art2-0012.png')).convert('RGBA'))
rcx,rcy=centroid(ref[:,:,3]>8)
print(f'Referenz art2 f12 Schwerpunkt ({rcx:.1f},{rcy:.1f}) auf {ref.shape[1]}px')

def place(body_up, ov_up, dx=0, dy=0):
    """Auf CANVAS legen, Schwerpunkt auf Referenz-Schwerpunkt (+ manuelle Korrektur)."""
    m=body_up[:,:,3]>8
    if ov_up is not None: m=m|(ov_up>0)
    cx,cy=centroid(m)
    ox=int(round(rcx-cx))+dx; oy=int(round(rcy-cy))+dy
    B=np.zeros((CANVAS,CANVAS,4),dtype=np.uint8)
    O=np.zeros((CANVAS,CANVAS),dtype=np.uint8)
    h,w=body_up.shape[:2]
    for sy in range(h):
        ty=sy+oy
        if ty<0 or ty>=CANVAS: continue
        for sx in range(w):
            tx=sx+ox
            if tx<0 or tx>=CANVAS: continue
            if body_up[sy,sx,3]>0: B[ty,tx]=body_up[sy,sx]
            if ov_up[sy,sx]>0: O[ty,tx]=ov_up[sy,sx]
    return B,O

def save_overlay_sheet(frames, path):
    n=len(frames); cols=8; rows=(n+cols-1)//cols
    sheet=np.zeros((rows*CANVAS,cols*CANVAS),dtype=np.uint8)
    for i,f in enumerate(frames):
        r,c=i//cols,i%cols
        sheet[r*CANVAS:(r+1)*CANVAS, c*CANVAS:(c+1)*CANVAS]=f
    im=Image.fromarray(sheet,'P')
    pal=[0,0,0]*256
    for i in range(16):
        v=255-i*17
        pal[(176+i)*3:(176+i)*3+3]=[v,v,v]
    im.putpalette(pal)
    meta=PngInfo(); meta.add_text('FrameSize',f'{CANVAS},{CANVAS}'); meta.add_text('FrameAmount',str(n))
    im.save(path, pnginfo=meta)

def save_body_sheet(frames, path):
    n=len(frames); cols=8; rows=(n+cols-1)//cols
    sheet=np.zeros((rows*CANVAS,cols*CANVAS,4),dtype=np.uint8)
    for i,f in enumerate(frames):
        r,c=i//cols,i%cols
        sheet[r*CANVAS:(r+1)*CANVAS, c*CANVAS:(c+1)*CANVAS]=f
    im=Image.fromarray(sheet,'RGBA')
    meta=PngInfo(); meta.add_text('FrameSize',f'{CANVAS},{CANVAS}'); meta.add_text('FrameAmount',str(n))
    im.save(path, pnginfo=meta)

# --- deployed (gtarty frame 0 = normal, frame 1 = damaged) ---
db,do=[],[]
for fr in (0,1):
    b,o=split_layers(load_idx(f'/tmp/shp/gtarty-{fr:04d}.png'))
    B,O=place(up_rgba(b),up_idx(o))
    db.append(B); do.append(O)
save_body_sheet(db, f'{BITS}/aot-arty-deployed.png')
save_overlay_sheet(do, f'{BITS}/aot-arty-deployed-remap.png')
print('deployed:', CANVAS, 'px, 2 Frames (0=normal, 1=damaged)')

# --- deploy-Animation (gtartymk 0..15) ---
mb,mo=[],[]
for fr in range(16):
    b,o=split_layers(load_idx(f'/tmp/shp/gtartymk-{fr:04d}.png'))
    B,O=place(up_rgba(b),up_idx(o))
    mb.append(B); mo.append(O)
save_body_sheet(mb, f'{BITS}/aot-arty-deploy-anim.png')
save_overlay_sheet(mo, f'{BITS}/aot-arty-deploy-anim-remap.png')
print('deploy-anim:', CANVAS,'px, 16 Frames')
