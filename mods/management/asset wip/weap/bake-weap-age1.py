#!/usr/bin/env python3
"""Backt die VANILLA-Age-1-WEAP-Sprites neu, damit sie dieselbe saubere Spielerfarbe zeigen
wie die Age-2-Variante. PlayerColorShift faerbt nur Hue 0.29-0.37 um; im Remaster-Sprite
liegen aber ~40% der Gruentoene daneben (0.21-0.50) und die Schornsteinringe sind Messing
(~0.1) -- beides blieb dadurch ungefaerbt neben bereits spielerfarbenen Flaechen.
Geometrie/Helligkeit bleiben 1:1, nur der Farbton wird begradigt."""
import importlib.util, io, zipfile, json, os
import numpy as np
from PIL import Image, PngImagePlugin
spec=importlib.util.spec_from_file_location("m","extract-meg-unit.py")
m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
MEG="/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/CnCRemastered/Data/TEXTURES_TD_SRGB.MEG"
B="mods/cnc/bits/"
MINH,MAXH,REF_H,SATMIN=0.29,0.37,0.333,0.25
CHIM=(0,205,190,345)          # Schornsteinregion auf der 384er-Leinwand: y0,y1,x0,x1
c,raw=m.parse_meg(MEG)
def zf(k):
    o,s=c[k]; return zipfile.ZipFile(io.BytesIO(raw[o:o+s]))
def to_hsv(a):
    r,g,b=a[:,:,0]/255.,a[:,:,1]/255.,a[:,:,2]/255.
    mx=np.max([r,g,b],0); mn=np.min([r,g,b],0); ch=mx-mn
    v=mx; s=np.where(mx>0,ch/np.maximum(mx,1e-6),0); h=np.zeros_like(mx); nz=ch>1e-6
    i=(mx==r)&nz; h[i]=(((g-b)[i]/ch[i])%6)/6
    i=(mx==g)&nz; h[i]=(((b-r)[i]/ch[i])+2)/6
    i=(mx==b)&nz; h[i]=(((r-g)[i]/ch[i])+4)/6
    return h,s,v
def to_rgb(h,s,v):
    i=np.floor(h*6).astype(int)%6; f=h*6-np.floor(h*6)
    p=v*(1-s); q=v*(1-f*s); t=v*(1-(1-f)*s)
    R=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[v,q,p,p,t,v])
    G=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[t,v,v,q,p,p])
    Bl=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[p,p,t,v,v,q])
    return R*255,G*255,Bl*255
def canvas_frame(z,name):
    """Frame auf seine volle Meta-Leinwand setzen -> alle Frames teilen eine Geometrie."""
    img=Image.open(io.BytesIO(z.read(name+".tga"))).convert("RGBA")
    meta=json.loads(z.read(name+".meta")); (CW,CH)=meta["size"]; L,T,_,_=meta["crop"]
    cv=Image.new("RGBA",(CW,CH),(0,0,0,0)); cv.alpha_composite(img,(L,T))
    return np.array(cv).astype(np.float32)
def recolor(a):
    from scipy import ndimage as ndi
    h,s,v=to_hsv(a); A=a[:,:,3]; vis=A>50
    # 1) alle gruenwirkenden Toene exakt ins Fenster (im Original 0.21-0.50 gestreut)
    green=vis&(s>SATMIN)&(h>=0.20)&(h<=0.50)
    h=np.where(green,REF_H,h)
    # 2) Schornstein-Messing ebenfalls; Saettigung anheben, weil der Shader sie abzieht
    y0,y1,x0,x1=CHIM; reg=np.zeros(a.shape[:2],bool); reg[y0:y1,x0:x1]=True
    gold=reg&vis&(s>SATMIN)&(h>=0.05)&(h<0.29)
    h=np.where(gold,REF_H,h); s=np.where(gold,np.maximum(s,0.72),s)
    # 3) freistehende Mini-Inseln im Fenster entsaettigen (Kompressionsrauschen)
    inw=(h>MINH)&(h<=MAXH)&vis
    lbl,n=ndi.label(inw)
    if n:
        sz=ndi.sum(np.ones_like(lbl),lbl,range(1,n+1))
        keep=np.zeros(n+1,bool)
        for i,z in enumerate(sz): keep[i+1]=z>=40
        s=np.where(inw&~keep[lbl],0.0,s)
    R,G,Bl=to_rgb(h,s,v)
    out=a.copy(); out[:,:,0]=R; out[:,:,1]=G; out[:,:,2]=Bl
    return out,int(green.sum()+gold.sum())
def save(frames,name):
    sheet=np.concatenate(frames,axis=1) if len(frames)>1 else frames[0]
    im=Image.fromarray(sheet.astype(np.uint8),"RGBA"); meta=PngImagePlugin.PngInfo()
    if len(frames)>1:
        meta.add_text("FrameSize","%d,%d"%(frames[0].shape[1],frames[0].shape[0]))
        meta.add_text("FrameAmount",str(len(frames)))
    im.save(B+name+".png",pnginfo=meta)
    return os.path.getsize(B+name+".png")//1024
zb=zf(r"DATA\ART\TEXTURES\SRGB\TIBERIAN_DAWN\STRUCTURES\WEAP.ZIP")
zt=zf(r"DATA\ART\TEXTURES\SRGB\TIBERIAN_DAWN\STRUCTURES\WEAP2.ZIP")
tot=0
# Basis-Layer: kein Messing, aber ebenfalls Gruentoene ausserhalb des Fensters.
for idx,nm in [(0,"weap-age1-idle"),(1,"weap-age1-damaged-idle")]:
    a,n=recolor(canvas_frame(zb,"weap-%04d"%idx)); tot+=n
    print("%-26s %d px umgefaerbt, %d KB"%(nm,n,save([a],nm)))
for rng,nm in [(range(0,10),"weap-age1-top"),(range(10,20),"weap-age1-damaged-top")]:
    fr=[]
    for i in rng:
        a,n=recolor(canvas_frame(zt,"weap2-%04d"%i)); tot+=n; fr.append(a)
    print("%-26s %d Frames, %d KB"%(nm,len(fr),save(fr,nm)))
print("gesamt umgefaerbt: %d px"%tot)
