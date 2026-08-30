#!/usr/bin/env python3
"""AFLD Age-1 (TD-Sprite): Landelichter + Landeplatten-Blitz als DAUER-Overlay.
Im Original stecken sie in der 16-Frame-"active"-Sequenz, die im Ruhezustand nicht laeuft.
Hier als WithIdleOverlay verdrahtet, analog zu AFLT.
Uebernommen werden NUR die aufleuchtenden Lichter und der Plattenblitz -- der ebenfalls
animierte Ladearm bleibt aussen vor: sein Body-Frame muesste sonst retuschiert werden,
sonst stuende der Arm doppelt im Bild."""
import importlib.util, io, zipfile, os
import numpy as np
from PIL import Image, PngImagePlugin
from scipy import ndimage as ndi
B="mods/cnc/bits/"
spec=importlib.util.spec_from_file_location("m","extract-meg-unit.py")
mm=importlib.util.module_from_spec(spec); spec.loader.exec_module(mm)
meg="/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/CnCRemastered/Data/TEXTURES_TD_SRGB.MEG"
c,raw=mm.parse_meg(meg)
o,sz=c[r"DATA\ART\TEXTURES\SRGB\TIBERIAN_DAWN\STRUCTURES\AFLD.ZIP"]
z=zipfile.ZipFile(io.BytesIO(raw[o:o+sz]))
def fr(i): return np.array(Image.open(io.BytesIO(z.read("afld-%04d.tga"%i))).convert("RGBA")).astype(np.float32)
def save(frames,name,W,H):
    im=Image.fromarray(np.concatenate(frames,1).astype(np.uint8)); meta=PngImagePlugin.PngInfo()
    meta.add_text("FrameSize","%d,%d"%(W,H)); meta.add_text("FrameAmount",str(len(frames)))
    im.save(B+name+".png",pnginfo=meta); return os.path.getsize(B+name+".png")//1024
def green(a):
    r,g,b,A=a[:,:,0],a[:,:,1],a[:,:,2],a[:,:,3]
    return (g>r+40)&(g>b+40)&(A>60)
for base,tag in [(0,""),(16,"-damaged")]:
    f=[fr(base+i) for i in range(16)]
    H,W=f[0].shape[:2]
    g0=green(f[0])
    # Landeplatte = groesste Gruenflaeche in Frame 0 (blitzt in Frame 5 und 12)
    pl,pn=ndi.label(g0); psz=ndi.sum(np.ones_like(pl),pl,range(1,pn+1))
    pad=(pl==(1+int(np.argmax(psz)))) if pn else np.zeros_like(g0)
    out=[]
    for i in range(16):
        q=np.zeros((H,W,4),np.float32)
        # neu aufleuchtende Lichter
        gi=ndi.binary_opening(green(f[i])&~g0,iterations=1)
        gi=ndi.binary_dilation(gi,iterations=1)&(f[i][:,:,3]>60)
        q[gi]=f[i][gi]
        # Plattenblitz: Platte uebernehmen, wenn sie in diesem Frame heller ist
        if pad.sum() and f[i][pad][:,:3].mean() > f[0][pad][:,:3].mean()+6:
            q[pad]=f[i][pad]
        out.append(q.astype(np.uint8))
    n="afld-age1%s-lights"%tag
    px=sum(int((x[:,:,3]>0).sum()) for x in out)
    print("%-26s %d KB  (%d Overlay-Pixel gesamt)"%(n,save(out,n,W,H),px))
