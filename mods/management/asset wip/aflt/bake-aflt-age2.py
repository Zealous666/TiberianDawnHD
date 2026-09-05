#!/usr/bin/env python3
"""AFLT (GDI Air Field) Age-2-Sprites aus dem Gemini-Render.
Leinwand bewusst 384x256 = EXAKT die Age-1-Geometrie (128 px/Zelle, 3x2 Footprint):
so passt die uebernommene RA-Windhose 1:1 ohne Umskalieren, und Scale/Offset sind
identisch zu Age 1 (0.1875 / 0,-64).
Windhose und Rollbahn-Lichter sind EIGENE Dauer-Overlays (WithIdleOverlay), nicht Teil einer
active-Sequenz: das Original haengt seine Animation an WithResupplyAnimation und bewegt sich
deshalb nur beim Auftanken -- im Ruhezustand stand alles still (User-Report 2026-08-30)."""
import importlib.util, io, zipfile, json, os
import numpy as np
from PIL import Image, ImageFilter, PngImagePlugin
from scipy import ndimage as ndi
W="mods/management/asset wip/aflt/"; B="mods/cnc/bits/"
CW,CH=384,256
MINH,MAXH,REF_H,SATMIN=0.29,0.37,0.333,0.25
LIGHT=[1.00,1.10,1.00,1.20,1.00,1.00,1.00,1.00]   # Rollbahn-Puls, aus dem Original gemessen
SOCK=(8,104,140,238)                               # KOMPLETTE Windhose inkl. ganzer Stange bis zum Fuss

def to_hsv(a):
    r,g,b=a[:,:,0]/255.,a[:,:,1]/255.,a[:,:,2]/255.
    mx=np.max([r,g,b],0); mn=np.min([r,g,b],0); c=mx-mn
    v=mx; s=np.where(mx>0,c/np.maximum(mx,1e-6),0); h=np.zeros_like(mx); nz=c>1e-6
    i=(mx==r)&nz; h[i]=(((g-b)[i]/c[i])%6)/6
    i=(mx==g)&nz; h[i]=(((b-r)[i]/c[i])+2)/6
    i=(mx==b)&nz; h[i]=(((r-g)[i]/c[i])+4)/6
    return h,s,v
def to_rgb(h,s,v):
    i=np.floor(h*6).astype(int)%6; f=h*6-np.floor(h*6)
    p=v*(1-s); q=v*(1-f*s); t=v*(1-(1-f)*s)
    R=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[v,q,p,p,t,v])
    G=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[t,v,v,q,p,p])
    Bl=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[p,p,t,v,v,q])
    return R*255,G*255,Bl*255

# ---------- 1) Gemini freistellen: NUR der Hauptkoerper (Sock/Stange/Fuss fliegen raus) ----------
src=np.array(Image.open(W+"aflt_ages.jpg").convert("RGB")).astype(np.float32)
r,g,b=src[:,:,0],src[:,:,1],src[:,:,2]
mag=(np.minimum(r,b)-g)>40
lbl,n=ndi.label(ndi.binary_opening(~mag,iterations=1))
sz=ndi.sum(np.ones_like(lbl),lbl,range(1,n+1))
body=lbl==(1+int(np.argmax(sz)))                 # groesste Komponente = Gebaeude+Rollbahn
body=ndi.binary_fill_holes(body)&~mag            # eingeschlossene Magenta-Reste raus
A=np.array(Image.fromarray((body*255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.6)))
bgcol=np.array([252.,5.,251.]); al=A/255.
mfr=np.clip((0.9-al)/0.9,0,1)*(al>0.05); den=np.clip(al,1e-3,1)[...,None]
rgb=np.clip((src-bgcol[None,None,:]*mfr[...,None])/den,0,255)
gem=np.dstack([rgb,A]).astype(np.float32)
ys,xs=np.where(A>30); gy0,gy1,gx0,gx1=ys.min(),ys.max(),xs.min(),xs.max()
gem=gem[gy0:gy1+1,gx0:gx1+1]
print("Gemini-Koerper %dx%d"%(gem.shape[1],gem.shape[0]))

# ---------- 2) auf 384 Breite skalieren, unten buendig wie Age 1 ----------
sc=CW/gem.shape[1]; nw,nh=CW,max(1,round(gem.shape[0]*sc))
small=np.array(Image.fromarray(gem.astype(np.uint8)).resize((nw,nh),Image.LANCZOS)).astype(np.float32)
BOT=252                                          # Unterkante wie im Age-1-Sprite
canvas=np.zeros((CH,CW,4),np.float32)
top=BOT-nh+1
ys0=max(0,top); src0=ys0-top
canvas[ys0:ys0+min(nh-src0,CH-ys0)]=small[src0:src0+min(nh-src0,CH-ys0)]
print("skaliert auf %dx%d, oben bei y=%d, unten y=%d"%(nw,nh,top,BOT))

# ---------- 3) Farben: Gruen -> Shader-Fenster, Rauschen entsaettigen ----------
h,s,v=to_hsv(canvas); vis=canvas[:,:,3]>50
green=vis&(s>SATMIN)&(h>=0.20)&(h<=0.50)
h=np.where(green,REF_H,h)
inw=(h>MINH)&(h<=MAXH)&vis
L,nn=ndi.label(inw)
if nn:
    zz=ndi.sum(np.ones_like(L),L,range(1,nn+1)); keep=np.zeros(nn+1,bool)
    for i,q in enumerate(zz): keep[i+1]=q>=12
    s=np.where(inw&~keep[L],0.0,s)
R,G,Bl=to_rgb(h,s,v)
canvas[:,:,0],canvas[:,:,1],canvas[:,:,2]=R,G,Bl
print("gruen normalisiert: %d px"%int(green.sum()))
lights=green&(canvas[:,:,3]>50)                 # Rollbahn-Markierungen = das Gruen

# ---------- 4) RA-Windhose (8 Original-Frames) holen ----------
spec=importlib.util.spec_from_file_location("m","extract-meg-unit.py")
mm=importlib.util.module_from_spec(spec); spec.loader.exec_module(mm)
meg="/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/CnCRemastered/Data/TEXTURES_RA_SRGB.MEG"
c,raw=mm.parse_meg(meg)
o,sz2=c[r"DATA\ART\TEXTURES\SRGB\RED_ALERT\STRUCTURES\AFLD.ZIP"]
z=zipfile.ZipFile(io.BytesIO(raw[o:o+sz2]))
def ra(i):
    im=Image.open(io.BytesIO(z.read("afld-%04d.tga"%i))).convert("RGBA")
    Lx,Tt,_,_=json.loads(z.read("afld-%04d.meta"%i))["crop"]
    cv=Image.new("RGBA",(CW,CH),(0,0,0,0)); cv.alpha_composite(im,(Lx,Tt)); return np.array(cv).astype(np.float32)
y0,y1,x0,x1=SOCK
socks=[]
for i in range(8):
    f=ra(i); layer=np.zeros((CH,CW,4),np.float32)
    layer[y0:y1,x0:x1]=f[y0:y1,x0:x1]
    socks.append(layer)
print("RA-Windhose: 8 Frames, Region y[%d..%d] x[%d..%d]"%(y0,y1,x0,x1))

# ---------- 5) Body (statisch) + zwei Dauer-Overlays ----------
def save(frames,name):
    sheet=np.concatenate(frames,1) if len(frames)>1 else frames[0]
    im=Image.fromarray(sheet.astype(np.uint8)); meta=PngImagePlugin.PngInfo()
    if len(frames)>1:
        meta.add_text("FrameSize","%d,%d"%(CW,CH)); meta.add_text("FrameAmount",str(len(frames)))
    im.save(B+name+".png",pnginfo=meta); return os.path.getsize(B+name+".png")//1024
print("idle    ",save([canvas.astype(np.uint8)],"aflt-age2-idle"),"KB")
# Windhose: die kompletten 8 Original-Frames als eigener Layer
print("windsock",save([s.astype(np.uint8) for s in socks],"aflt-age2-windsock"),"KB")
# Rollbahn-Lichter: nur die gruenen Markierungen, Helligkeit gepulst. Der Layer liegt exakt
# ueber denselben Pixeln im Body und ersetzt sie -> sichtbares Blinken.
lf=[]
for i in range(8):
    q=np.zeros((CH,CW,4),np.float32)
    for ch in range(3): q[:,:,ch]=np.where(lights,np.clip(canvas[:,:,ch]*LIGHT[i],0,255),0)
    q[:,:,3]=np.where(lights,canvas[:,:,3],0)
    lf.append(q.astype(np.uint8))
print("lights  ",save(lf,"aflt-age2-lights"),"KB")
# ---------- 6) make (Bottom-up-Wipe) ----------
f0=np.where(socks[0][:,:,3:4]>0,socks[0],canvas).astype(np.float32); al=f0[:,:,3]; yy=np.where(al>10)[0]; tp,bt=yy.min(),yy.max()
NM=14; fe=(bt-tp)*0.12; mf=[]
for i in range(NM):
    t=i/(NM-1); thr=bt-t*(bt-tp+fe); Y=np.arange(CH).reshape(-1,1).astype(float)
    q=f0.copy(); q[:,:,3]=al*np.clip((Y-thr)/fe,0,1); mf.append(q.astype(np.uint8))
print("make  ",save(mf,"aflt-age2-make"),"KB")
