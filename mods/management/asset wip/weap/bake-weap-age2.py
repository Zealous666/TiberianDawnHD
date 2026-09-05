#!/usr/bin/env python3
"""Backt alle WEAP-Age-2-Sprites aus _gem_clean.png. Idempotent -- immer hier aendern,
nie die PNGs in bits/ nachtraeglich patchen."""
from PIL import Image, PngImagePlugin
import numpy as np, os
W="mods/management/asset wip/weap/"; B="mods/cnc/bits/"
MINH,MAXH,REF_H=0.29,0.37,0.333          # PlayerColorShift-Fenster (engine defaults)
SATMIN=0.25                               # darunter = JPEG-Rauschen, muss AUS dem Fenster
GEARS=[(700,572,31),(699,649,34)]
CHIM=(0,480,480,976)                      # Schornstein-Region (native): y0,y1,x0,x1
F=0.5; ND,NG,NM=8,24,14

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

NAT=np.array(Image.open(W+"_gem_clean.png").convert("RGBA")).astype(np.float32)
H0,W0=NAT.shape[:2]; A0=NAT[:,:,3]
r,g,b=NAT[:,:,0],NAT[:,:,1],NAT[:,:,2]
h,s,v=to_hsv(NAT)
vis=A0>50
# 1) echte Gruentoene -> exakt ins Fenster (Streuung 0.19..0.44 sonst teils ungefaerbt).
#    Ueber den HUE greifen, nicht ueber RGB-Differenzen: eine Maske "g>r+8 & g>b+8" laesst
#    genau die Gelbgruen-Pixel mit r~=g durch, die dann als Olivstich stehen bleiben.
green=vis&(s>SATMIN)&(h>=0.20)&(h<=0.50)
h=np.where(green,REF_H,h)
# 2) Schornstein-Gold -> Playercolor. Gleiche Logik, aber der Gelb-/Messingbereich (ab 0.08)
#    kommt mit rein, und die Saettigung wird angehoben: der Shader ZIEHT Saettigung ab
#    (ReferenceSaturation 0.925), aus dunklem Gold wuerde sonst stumpfes Bordeaux.
y0,y1,x0,x1=CHIM; reg=np.zeros((H0,W0),bool); reg[y0:y1,x0:x1]=True
gold=reg&vis&(s>SATMIN)&(h>=0.08)&(h<=0.50)
h=np.where(gold,REF_H,h); s=np.where(gold,np.maximum(s,0.72),s)
# 3) Entrauschen, RAEUMLICH statt per Schwellwert: JPEG-Rauschen sind einzelne blasse
#    Pixel im Hue-Fenster mitten auf grauen Flaechen -> die zu ENTSAETTIGEN (nicht den Hue
#    zu verbiegen: ein verschobener Hue bleibt bei mittlerer Saettigung als Olivstich sichtbar).
#    Blasse Pixel DIREKT an echten Farbflaechen sind Antialiasing-Kanten und bleiben drin,
#    sonst bekommt jede spielerfarbene Flaeche einen grauen Saum.
from scipy import ndimage as _ndi
inw=(h>MINH)&(h<=MAXH)&vis
# Zusammenhaengende Farbinseln ueber die GANZE Fenster-Maske labeln: echte Bauteile (Ringe,
# Kragen, Warnstreifen) haengen samt ihrer Antialiasing-Kanten als grosse Komponente zusammen,
# JPEG-Rauschen bleibt als 1-3px-Insel uebrig. Nur Letztere entsaettigen -> keine dunkelroten
# Sprenkel auf den grauen Schornsteinen, aber auch kein grauer Saum um die Farbflaechen.
_lbl,_n=_ndi.label(inw)
if _n:
    _sz=_ndi.sum(np.ones_like(_lbl),_lbl,range(1,_n+1))
    _keep=np.zeros(_n+1,bool)
    for _i,_z in enumerate(_sz): _keep[_i+1]=_z>=40
    _noise=inw&~_keep[_lbl]
    s=np.where(_noise,0.0,s)
    print("Rausch-Inseln entfernt: %d px in %d Komponenten"%(int(_noise.sum()),int((~_keep[1:]).sum())))
R,G,Bl=to_rgb(h,s,v)
NAT=np.dstack([R,G,Bl,A0]).astype(np.float32)
print("gruen normalisiert=%d  gold->player=%d"%(green.sum(),gold.sum()))

panel=np.array(Image.open(W+"weap_panel_mask.png").convert("L"))>127  # Torpanel-Maske (native Aufloesung)
yy,xx=np.mgrid[0:H0,0:W0]
mx=NAT[:,:,:3].max(2); mn=NAT[:,:,:3].min(2); grey=((mx-mn)<45)&(A0>60)
disc=np.zeros((H0,W0),bool)
for cx,cy,rr in GEARS: disc|=((xx-cx)**2+(yy-cy)**2)<=rr*rr
gearpx=grey&disc
ring=disc&~gearpx&(A0>60)
fill=np.median(NAT[ring][:,:3],axis=0) if ring.sum()>50 else np.array([40,150,50.])
def down(a): return np.array(Image.fromarray(a.astype(np.uint8),"RGBA").resize((round(W0*F),round(H0*F)),Image.LANCZOS)).astype(float)
def save(a,n,frames=1,fw=None,fh=None):
    im=Image.fromarray(a.astype(np.uint8),"RGBA"); m=PngImagePlugin.PngInfo()
    if frames>1: m.add_text("FrameSize",f"{fw},{fh}"); m.add_text("FrameAmount",str(frames))
    im.save(B+n+".png",pnginfo=m); return os.path.getsize(B+n+".png")//1024

# BODY: komplettes Gebaeude (unter den Einheiten), Tor -> Hoehle, Zahnraeder ausgeschnitten
bd=NAT.copy()
for c in range(3): bd[:,:,c]=np.where(gearpx,fill[c],bd[:,:,c])
for c in range(3): bd[:,:,c]=np.where(panel,[18,18,20][c],bd[:,:,c])
bd[:,:,3]=np.where(panel,255,bd[:,:,3])
body=down(bd); Hs,Ws=body.shape[:2]; body[body[:,:,3]<8,:3]=0
print("idle",save(body,"weap-age2-idle"),"KB")
# FULL (Platzier-Ghost) + MAKE
full=down(NAT); full[full[:,:,3]<8,:3]=0
print("full",save(full,"weap-age2-full"),"KB")
sal=full[:,:,3]; sy=np.where(sal>10)[0]; top,bot=sy.min(),sy.max(); fe=(bot-top)*0.12; mf=[]
for i in range(NM):
    t=i/(NM-1); thr=bot-t*(bot-top+fe); Y=np.arange(Hs).reshape(-1,1).astype(float)
    fr=full.copy(); fr[:,:,3]=full[:,:,3]*np.clip((Y-thr)/fe,0,1); mf.append(fr.astype(np.uint8))
print("make",save(np.concatenate(mf,1),"weap-age2-make",NM,Ws,Hs),"KB")
# DOOR: NUR das Torpanel (ueber den Einheiten), gleitet nach oben ins Gebaeude
pys,_=np.where(panel); PT,PB=pys.min(),pys.max()
dp=np.zeros_like(NAT)
for c in range(4): dp[:,:,c]=np.where(panel,NAT[:,:,c],0)
clip=np.zeros((H0,W0),bool); clip[PT:,:]=True; travel=int((PB-PT)*0.86); df=[]
for i in range(ND):
    dy=int((i/(ND-1))*travel); sfr=np.zeros_like(dp)
    if dy>0:
        sfr[0:H0-dy,:,:]=dp[dy:H0,:,:]
        for c in range(4): sfr[:,:,c]=np.where(clip,sfr[:,:,c],0)
    else: sfr=dp.copy()
    df.append(down(sfr))
print("door",save(np.concatenate([x.astype(np.uint8) for x in df],1),"weap-age2-door",ND,Ws,Hs),"KB")
# GEARS: volle 360 Grad -> nahtloser Loop
ad=down(NAT); gp=np.array(Image.fromarray((gearpx*255).astype(np.uint8)).resize((Ws,Hs),Image.LANCZOS))>110
cog=np.zeros_like(ad)
for c in range(4): cog[:,:,c]=np.where(gp,ad[:,:,c],0)
Yg,Xg=np.mgrid[0:Hs,0:Ws]; gf=[]
for i in range(NG):
    acc=np.zeros_like(cog)
    for (cx,cy,rr) in GEARS:
        cxs,cys=cx*F,cy*F; msk=((Xg-cxs)**2+(Yg-cys)**2)<=(rr*F)**2
        pc=np.zeros_like(cog)
        for c in range(4): pc[:,:,c]=np.where(msk,cog[:,:,c],0)
        ro=np.array(Image.fromarray(pc.astype(np.uint8),"RGBA").rotate(i*360.0/NG,resample=Image.BICUBIC,center=(cxs,cys))).astype(float)
        acc=np.where(ro[:,:,3:4]>0,ro,acc)
    gf.append(acc.astype(np.uint8))
print("gears",save(np.concatenate(gf,1),"weap-age2-gears",NG,Ws,Hs),"KB")
print("canvas %dx%d"%(Ws,Hs))
