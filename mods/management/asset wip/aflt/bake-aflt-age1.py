#!/usr/bin/env python3
"""AFLT Age-1 (RA-Sprite): Windhose + Rollbahn-Lichter als DAUER-Overlays.
Im Original haengt die Animation an WithResupplyAnimation und laeuft nur beim Auftanken --
im Ruhezustand steht alles still. Hier wird dieselbe Bewegung als WithIdleOverlay verdrahtet,
analog zu Age 2. Dafuer muss die Windhose aus dem Body RETUSCHIERT werden, sonst blitzt die
statische unter der animierten hervor."""
import importlib.util, io, zipfile, json, os
import numpy as np
from PIL import Image, PngImagePlugin
W="mods/management/asset wip/aflt/"; B="mods/cnc/bits/"
CW,CH=384,256
SOCK=(8,104,140,238)          # identisch zum Age-2-Bake
spec=importlib.util.spec_from_file_location("m","extract-meg-unit.py")
mm=importlib.util.module_from_spec(spec); spec.loader.exec_module(mm)
meg="/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/CnCRemastered/Data/TEXTURES_RA_SRGB.MEG"
c,raw=mm.parse_meg(meg)
o,sz=c[r"DATA\ART\TEXTURES\SRGB\RED_ALERT\STRUCTURES\AFLD.ZIP"]
z=zipfile.ZipFile(io.BytesIO(raw[o:o+sz]))
def canv(i):
    im=Image.open(io.BytesIO(z.read("afld-%04d.tga"%i))).convert("RGBA")
    L,T,_,_=json.loads(z.read("afld-%04d.meta"%i))["crop"]
    cv=Image.new("RGBA",(CW,CH),(0,0,0,0)); cv.alpha_composite(im,(L,T)); return np.array(cv).astype(np.float32)
def save(frames,name):
    sheet=np.concatenate(frames,1) if len(frames)>1 else frames[0]
    im=Image.fromarray(sheet.astype(np.uint8)); meta=PngImagePlugin.PngInfo()
    if len(frames)>1:
        meta.add_text("FrameSize","%d,%d"%(CW,CH)); meta.add_text("FrameAmount",str(len(frames)))
    im.save(B+name+".png",pnginfo=meta); return os.path.getsize(B+name+".png")//1024
y0,y1,x0,x1=SOCK
box=np.zeros((CH,CW),bool); box[y0:y1,x0:x1]=True
for base,tag in [(0,""),(8,"-damaged")]:
    f=[canv(base+i) for i in range(8)]
    # animierte Pixel (ohne Windhosen-Box) = die pulsenden Rollbahn-Markierungen
    d=np.zeros((CH,CW))
    for i in range(1,8): d=np.maximum(d,np.abs(f[i]-f[0]).sum(2))
    lights=(d>40)&~box
    # Body: Frame 0, Windhose herausretuschiert
    body=f[0].copy(); body[box,3]=0
    n="aflt-age1%s-idle"%tag
    print("%-28s %d KB"%(n,save([body.astype(np.uint8)],n)))
    # Windhose als eigener Layer
    ws=[]
    for i in range(8):
        q=np.zeros((CH,CW,4),np.float32); q[box]=f[i][box]; ws.append(q.astype(np.uint8))
    n="aflt-age1%s-windsock"%tag
    print("%-28s %d KB  (%d px/Frame)"%(n,save(ws,n),int(box.sum())))
    # Rollbahn-Lichter als eigener Layer
    lf=[]
    for i in range(8):
        q=np.zeros((CH,CW,4),np.float32); q[lights]=f[i][lights]; lf.append(q.astype(np.uint8))
    n="aflt-age1%s-lights"%tag
    print("%-28s %d KB  (%d px)"%(n,save(lf,n),int(lights.sum())))
