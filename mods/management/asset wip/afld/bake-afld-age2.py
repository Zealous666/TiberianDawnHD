#!/usr/bin/env python3
"""AFLD (NOD Airstrip/Starport) Age-2-Sprites aus dem Gemini-Render.
Leinwand 512x256 = EXAKT die Original-Geometrie (128 px/Zelle, 4x2), daher Scale 0.1875 und
Offset 0,0 wie beim Original -- und die aus dem Original vermessenen Lichtpositionen passen
ohne Umrechnung.
Animation nachgebaut (aus den 16 Original-Frames vermessen):
  - Landelichter als LAUFLICHT: 4 Paare (obere+untere Kante gleichzeitig) wandern von rechts
    nach links auf die Landeplatte zu, Frames 1-4, dann 3 Frames Pause, dann Wiederholung.
  - Landeplatte blitzt in Frame 5 und 12 auf.
Player-Color: das Original nutzt RemasteredMaskFilename -- auf dem klassischen Pfad gibt es
das nicht, deshalb Gruentoene ins PlayerColorShift-Fenster (0.29-0.37) normalisiert."""
import numpy as np, os
from PIL import Image, ImageFilter, PngImagePlugin
from scipy import ndimage as ndi
W="mods/management/asset wip/afld/"; B="mods/cnc/bits/"
CW,CH=512,256
MINH,MAXH,REF_H,SATMIN=0.29,0.37,0.333,0.25
# Nietenpositionen im FERTIGEN Sprite vermessen (Randschienen-Profil), nicht aus dem
# Original uebernommen: die x-Werte decken sich zwar (235/300/390/445), die y-Werte lagen
# im Original aber ~8px hoeher -> das Licht sass halb neben der Niete.
LIGHTS=[(445,105),(390,105),(300,105),(235,105),(455,210),(400,210),(305,210),(240,210)]
ORDER=[[0,4],[1,5],[2,6],[3,7]]      # Paar-Index je Aktiv-Frame (rechts -> links)
FLASH=[5,12]                          # Landeplatte blitzt
RAMP_DARK,RAIL_DARK=0.30,0.42            # Rampe fast schwarz / staerkste Abdunkelung der Randflaechen
ARROW_MIN=150                            # ab dieser Groesse gilt ein Loch als Pfeil, nicht als Rauschen
GREEN_SAT=1.95                           # Gruen-Saettigung auf Original-Niveau (0.44 -> ~0.86)
RAIL_FROM,RAIL_TO=0.32,0.85              # Kurvenbereich: darunter unveraendert, darueber voll   # Rampe fast schwarz; alles Grau ab RAIL_FROM gilt als
                                              # helle Randflaeche und wird abgedunkelt
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
def save(frames,name):
    sheet=np.concatenate(frames,1) if len(frames)>1 else frames[0]
    im=Image.fromarray(sheet.astype(np.uint8)); meta=PngImagePlugin.PngInfo()
    if len(frames)>1:
        meta.add_text("FrameSize","%d,%d"%(CW,CH)); meta.add_text("FrameAmount",str(len(frames)))
    im.save(B+name+".png",pnginfo=meta); return os.path.getsize(B+name+".png")//1024

# --- freistellen ---
src=np.array(Image.open(W+"afld_ages.jpg").convert("RGB")).astype(np.float32)
r,g,b=src[:,:,0],src[:,:,1],src[:,:,2]
mag=(np.minimum(r,b)-g)>40
lbl,n=ndi.label(ndi.binary_opening(~mag,iterations=1))
sz=ndi.sum(np.ones_like(lbl),lbl,range(1,n+1))
body=ndi.binary_fill_holes(lbl==(1+int(np.argmax(sz))))&~mag
A=np.array(Image.fromarray((body*255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.6)))
bgcol=np.array([252.,5.,251.]); al=A/255.
mfr=np.clip((0.9-al)/0.9,0,1)*(al>0.05); den=np.clip(al,1e-3,1)[...,None]
gem=np.dstack([np.clip((src-bgcol[None,None,:]*mfr[...,None])/den,0,255),A]).astype(np.float32)
ys,xs=np.where(A>30); gem=gem[ys.min():ys.max()+1,xs.min():xs.max()+1]
print("Gemini-Koerper %dx%d"%(gem.shape[1],gem.shape[0]))
sc=CW/gem.shape[1]; nh=max(1,round(gem.shape[0]*sc))
small=np.array(Image.fromarray(gem.astype(np.uint8)).resize((CW,nh),Image.LANCZOS)).astype(np.float32)
BOT=252
canvas=np.zeros((CH,CW,4),np.float32)
top=BOT-nh+1; ys0=max(0,top); s0=ys0-top
canvas[ys0:ys0+min(nh-s0,CH-ys0)]=small[s0:s0+min(nh-s0,CH-ys0)]
print("skaliert auf %dx%d, oben y=%d"%(CW,nh,top))
# --- Farben ---
h,s,v=to_hsv(canvas); vis=canvas[:,:,3]>50
a_r,a_g,a_b=canvas[:,:,0],canvas[:,:,1],canvas[:,:,2]
# Gruen breit fassen (auch schwach gesaettigte Schatten-/Kantenpixel), sonst bleiben sie
# ausserhalb des Shader-Fensters gruen stehen, waehrend die Nachbarn Spielerfarbe werden.
green=vis&(a_g>a_r+6)&(a_g>a_b+6)&(s>0.06)&(h>=0.18)&(h<=0.52)
h=np.where(green,REF_H,h)
# Die grossen Gruenflaechen (Landeplatte, Scheibe unterm Turm) tragen JPEG-Rauschen. Nach dem
# Hue-Flatten faellt das als fleckige Verpixelung auf -> Saettigung und Helligkeit INNERHALB
# der Flaeche mit einem Median glaetten (erhaelt Kanten, killt das Geflimmer).
region=ndi.binary_fill_holes(ndi.binary_closing(green,iterations=3))
s_m=ndi.median_filter(s,size=3); v_m=ndi.median_filter(v,size=3)
s=np.where(region,s_m,s); v=np.where(region,v_m,v)
# Loecher SCHLIESSEN: mitten in der Flaeche sitzen graue Rauschpixel, die nie in der
# Gruenmaske waren. Ihr Farbton ist neutral -> der Shader faerbt sie NICHT ein und sie
# schimmern als Grau durch die Spielerfarbe. Also Farbton setzen und Saettigung auf das
# Niveau der umgebenden Flaeche heben; die Helligkeit bleibt, damit die Plastik erhalten ist.
# Saettigung auf Original-Niveau anheben. Der Gemini-Render ist mit sat~0.44 nur halb so
# gesaettigt wie das TD-Original (0.87). PlayerColorShift ZIEHT nochmal 0.145 ab
# (ReferenceSaturation 0.925) -> mein Gruen landete bei ~0.30 und wirkte als blasses,
# grau-durchscheinendes Rot, waehrend das Original bei ~0.72 kraeftig bleibt.
# Helligkeit bleibt unangetastet, damit die Plastik der Flaechen erhalten bleibt.
s=np.where(green,np.clip(s*GREEN_SAT,0,0.95),s)
holes=region&~green&vis
# Die Loecher sind ZWEIERLEI: winzige Rauschpixel (sollen Spielerfarbe werden) und der grosse
# weisse Richtungspfeil auf der Landeplatte (soll dunkel bleiben). Nach Komponentengroesse
# trennen -- sonst wird der Pfeil mit eingefaerbt und verschwindet in der Spielerfarbe.
if holes.sum():
    _hl,_hn=ndi.label(holes)
    arrow=np.zeros_like(holes); noise=np.zeros_like(holes)
    if _hn:
        _hz=ndi.sum(np.ones_like(_hl),_hl,range(1,_hn+1))
        for _i,_q in enumerate(_hz):
            (arrow if _q>=ARROW_MIN else noise)[_hl==_i+1]=True
    h=np.where(noise,REF_H,h)
    s=np.where(noise,np.maximum(s,0.80),s)
    # Antialiasing-Saum mitnehmen: der Pfeil war weiss, seine Kantenpixel sind hell und
    # schwach gesaettigt und blieben sonst als weisser Rand um den dunklen Pfeil stehen.
    _ring=ndi.binary_dilation(arrow,iterations=2)&region&vis&~arrow&(s<0.45)&(v>0.45)
    arrow=arrow|_ring
    # Pfeil: entsaettigen und so dunkel wie die Rampe -> klarer Kontrast auf der Platte
    s=np.where(arrow,0.0,s)
    v=np.where(arrow,v*RAMP_DARK,v)
    print("   Loecher: Pfeil %d px (dunkelgrau), Rauschen %d px (Spielerfarbe)"%(int(arrow.sum()),int(noise.sum())))
# Rausch-Inseln nur AUSSERHALB der Gruenflaechen entsaettigen -- innerhalb wuerde der Filter
# die Flaeche mit grauen Sprenkeln durchsetzen (genau der "unsaubere" Eindruck).
inw=(h>MINH)&(h<=MAXH)&vis&~region
L,nn=ndi.label(inw)
if nn:
    zz=ndi.sum(np.ones_like(L),L,range(1,nn+1)); keep=np.zeros(nn+1,bool)
    for i,q in enumerate(zz): keep[i+1]=q>=16
    s=np.where(inw&~keep[L],0.0,s)
# --- Abdunkeln: der Gemini-Render ist deutlich heller als das Original (Median 0.58 vs 0.39)
# und wirkte im Schnee "bleich". Referenz ist das NOD-Helipad daneben (Median 0.35).
# Farbige Flaechen (Landeplatte/Spielerfarbe) bleiben unangetastet, nur die grauen Flaechen.
grey=vis&(s<0.30)
# Rampe = die GROSSE zusammenhaengende Mittelgrau-Flaeche der Rollbahn. Ueber einen reinen
# Helligkeitsbereich zu gehen erfasst auch Turm und Rumpf und dunkelt das ganze Gebaeude ab.
# Rampe = die Rollbahnflaeche ZWISCHEN den beiden Randschienen (obere ~y105, untere ~y210)
# und rechts der Landeplatte. Rein ueber Helligkeit zu gehen erfasst auch Turm und Rumpf und
# dunkelt das ganze Gebaeude ab; rein ueber Zusammenhang ebenso (Rumpf haengt dran).
midg=grey&(v>0.20)&(v<0.72)
_box=np.zeros_like(midg); _box[112:202,200:CW]=True
ramp=midg&_box
# Randflaechen STUFENLOS abdunkeln: eine harte Helligkeitsschwelle schneidet mitten durch die
# weichen Verlaeufe der Panels und hinterlaesst Flecken/Baender ("unsauber"). Stattdessen eine
# Smoothstep-Kurve -- je heller ein Grauwert, desto staerker die Abdunkelung, ohne Kante.
t=np.clip((v-RAIL_FROM)/(RAIL_TO-RAIL_FROM),0,1); t=t*t*(3-2*t)
fac=1.0+(RAIL_DARK-1.0)*t
v=np.where(ramp,v*RAMP_DARK,np.where(grey,v*fac,v))
print("   Rampe %d px, Randflaechen weich abgedunkelt (Kurve %.2f..%.2f)"%(int(ramp.sum()),RAIL_FROM,RAIL_TO))
R,G,Bl=to_rgb(h,s,v); canvas[:,:,0],canvas[:,:,1],canvas[:,:,2]=R,G,Bl
_vv=canvas[:,:,:3].max(2)/255.
print("gruen normalisiert: %d px | Helligkeit nach Abdunkeln: median=%.2f (Ziel ~0.35)"%(
    int(green.sum()),np.median(_vv[vis])))
print("idle   ",save([canvas.astype(np.uint8)],"afld-age2-idle"),"KB")
# --- Landeplatte finden (groesste zusammenhaengende Gruenflaeche links) ---
padmask=green.copy(); padmask[:, CW//2:]=False
pl,pn=ndi.label(padmask)
psz=ndi.sum(np.ones_like(pl),pl,range(1,pn+1))
pad=pl==(1+int(np.argmax(psz))) if pn else np.zeros_like(padmask)
pys,pxs=np.where(pad); print("Landeplatte: %d px @x%d y%d"%(pad.sum(),int(pxs.mean()),int(pys.mean())))
# --- Lichter-Overlay: 16 Frames ---
YY,XX=np.mgrid[0:CH,0:CW]
disc=[((XX-x)**2+(YY-y)**2)<=7*7 for x,y in LIGHTS]
lf=[]
for i in range(16):
    q=np.zeros((CH,CW,4),np.float32)
    k=i%8
    if 1<=k<=4:
        for idx in ORDER[k-1]:
            m=disc[idx]&(canvas[:,:,3]>0)
            q[m]=[70,255,90,255]
    if i in FLASH:
        for ch,val in zip(range(3),(150,255,170)):
            q[:,:,ch]=np.where(pad,np.clip(canvas[:,:,ch]*1.35,0,255),q[:,:,ch])
        q[:,:,3]=np.where(pad,canvas[:,:,3],q[:,:,3])
    lf.append(q.astype(np.uint8))
print("lights ",save(lf,"afld-age2-lights"),"KB")
# --- make ---
al2=canvas[:,:,3]; yy=np.where(al2>10)[0]; tp,bt=yy.min(),yy.max()
NM=14; fe=(bt-tp)*0.12; mf=[]
for i in range(NM):
    t=i/(NM-1); thr=bt-t*(bt-tp+fe); Y=np.arange(CH).reshape(-1,1).astype(float)
    q=canvas.copy(); q[:,:,3]=al2*np.clip((Y-thr)/fe,0,1); mf.append(q.astype(np.uint8))
print("make   ",save(mf,"afld-age2-make"),"KB")
