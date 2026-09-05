#!/usr/bin/env python3
"""aot_playercolor -- gemeinsame Funktion fuer den SAUBEREN Player-Color-Pfad (kein Krisseln).

Hintergrund (siehe Memory gemini-playercolor-shader-recipe):
Der Engine-Shader PlayerColorShift faerbt NUR Pixel mit Hue 0.29-0.37, verschiebt Hue->Spielerfarbe
und ZIEHT Saettigung ab (ReferenceSaturation 0.925 ~ -0.145). Damit ein Sprite ueber diesen Pfad
sauber Spielerfarbe bekommt, muss die Player-Region auf Hue 0.333 mit ANGEHOBENER Saettigung
gebracht werden -- sonst landet man nach dem Abzug bei blassem, grau-durchscheinendem Rot.

Zwei Einstiegspunkte:
  rebuild_from_remap(idle, remap, out)  -- fuer Bestands-Gebaeude: grau-Body + indiziertes
      remap-PNG (das die Player-Region markiert) -> EIN Truecolor-Sprite. Loest das
      WithIdleOverlay@tsremap ab; die Region behaelt die Plastik/Helligkeit des Body.
  apply_to_mask(canvas, mask)           -- fuer frische Renders: Maske (Farberkennung) direkt.
"""
import numpy as np
from PIL import Image
from scipy import ndimage as ndi

MINH, MAXH, REF_H = 0.29, 0.37, 0.333
GREEN_SAT_TARGET = 0.86     # Ziel-Saettigung der Region VOR dem Shader (danach ~0.72, wie Vanilla)

def _to_hsv(a):
    r,g,b = a[:,:,0]/255., a[:,:,1]/255., a[:,:,2]/255.
    mx=np.max([r,g,b],0); mn=np.min([r,g,b],0); c=mx-mn
    v=mx; s=np.where(mx>0, c/np.maximum(mx,1e-6), 0); h=np.zeros_like(mx); nz=c>1e-6
    i=(mx==r)&nz; h[i]=(((g-b)[i]/c[i])%6)/6
    i=(mx==g)&nz; h[i]=(((b-r)[i]/c[i])+2)/6
    i=(mx==b)&nz; h[i]=(((r-g)[i]/c[i])+4)/6
    return h,s,v

def _to_rgb(h,s,v):
    i=np.floor(h*6).astype(int)%6; f=h*6-np.floor(h*6)
    p=v*(1-s); q=v*(1-f*s); t=v*(1-(1-f)*s)
    R=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[v,q,p,p,t,v])
    G=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[t,v,v,q,p,p])
    Bl=np.select([i==0,i==1,i==2,i==3,i==4,i==5],[p,p,t,v,v,q])
    return R*255,G*255,Bl*255

def apply_to_mask(canvas, mask, sat_target=GREEN_SAT_TARGET, denoise=True, value_gamma=1.0):
    """canvas: HxWx4 float RGBA. mask: bool, die Player-Region. In-place-artig -> gibt canvas zurueck.
    Bringt die Region auf Hue REF_H + Saettigung sat_target, Helligkeit (v) BLEIBT (Plastik)."""
    a=canvas.astype(np.float32); A=a[:,:,3]; vis=A>40
    h,s,v=_to_hsv(a)
    m=mask&vis
    h=np.where(m, REF_H, h)
    # Helligkeit der Player-Region anheben, wenn sie zu dunkel leuchtet (value_gamma<1).
    # Gamma statt linearem Faktor: hebt Mitteltoene, ohne die Lichter zu ueberstrahlen.
    if value_gamma!=1.0: v=np.where(m, np.clip(v,0,1)**value_gamma, v)
    s=np.where(m, sat_target, s)   # feste Ziel-Saettigung (Helligkeit v bleibt fuer die Plastik)
    if denoise:
        # NUR KLEINE Loecher IN der Region fuellen (JPEG-Rauschen). Grosse eingeschlossene
        # Flaechen NICHT: umschliesst die Player-Region z.B. eine andersfarbige Mitte (Ring-
        # Layout wie die Shipyard-Kuppeln um den Turm), wuerde fill_holes die ganze Mitte
        # mitfaerben. Deshalb Loecher per Komponentengroesse filtern.
        filled=ndi.binary_fill_holes(ndi.binary_closing(m, iterations=2))
        hole_all=filled&~m&vis
        hl,hn=ndi.label(hole_all)
        holes=np.zeros_like(m)
        if hn:
            hz=ndi.sum(np.ones_like(hl),hl,range(1,hn+1))
            for i,q in enumerate(hz):
                if q<400: holes[hl==i+1]=True     # klein = Rauschloch; gross = fremde Flaeche
        h=np.where(holes, REF_H, h); s=np.where(holes, sat_target, s)
        m=m|holes
        # Saettigung in der ECHTEN Player-Region (nur m, nicht die grossen Loecher) glaetten
        s=np.where(m, ndi.median_filter(s,size=3), s)
    # SCHUTZ: Fremdfarben im Hue-Fenster, die NICHT zur Player-Region gehoeren (z.B. eine
    # olivgruene Flaeche), wuerde der Shader sonst mitfaerben. Knapp aus dem Fenster schieben
    # (0.29->0.275 bzw. 0.37->0.385) -- optisch fast identisch, aber Shader-safe. Die um 2px
    # dilatierte Maske bleibt ausgespart, damit die Player-Kanten voll gefaerbt bleiben.
    guard=ndi.binary_dilation(m,iterations=2)
    outside=~guard&vis&(h>MINH)&(h<=MAXH)&(s>0.12)
    below=outside&(h<REF_H); above=outside&(h>=REF_H)
    h=np.where(below, MINH-0.015, h); h=np.where(above, MAXH+0.015, h)
    R,G,Bl=_to_rgb(h,s,v)
    paint=m|below|above
    a[:,:,0]=np.where(paint,R,a[:,:,0]); a[:,:,1]=np.where(paint,G,a[:,:,1]); a[:,:,2]=np.where(paint,Bl,a[:,:,2])
    return a, int(m.sum())

def mask_from_remap(remap_path):
    """Player-Maske aus einem indizierten remap-PNG (mode P): alle nicht-transparenten Pixel."""
    rm=Image.open(remap_path)
    ri=np.array(rm); trans=rm.info.get("transparency",0)
    return ri!=trans

def rebuild_from_remap(idle_path, remap_path, out_path, sat_target=GREEN_SAT_TARGET, value_gamma=1.0):
    """Bestands-Gebaeude: grau-Body (idle) + remap-Maske -> ein Truecolor-Sprite mit
    player-tauglicher Region. Groesse/Position bleiben = idle."""
    idle=np.array(Image.open(idle_path).convert("RGBA")).astype(np.float32)
    mask=mask_from_remap(remap_path)
    if mask.shape != idle.shape[:2]:
        raise ValueError("remap %s != idle %s"%(mask.shape, idle.shape[:2]))
    out,px=apply_to_mask(idle, mask, sat_target=sat_target, value_gamma=value_gamma)
    # RGB der voll-transparenten Pixel nullen (kein Halo beim Downscale)
    out[out[:,:,3]<8,:3]=0
    Image.fromarray(out.astype(np.uint8),"RGBA").save(out_path)
    return px

def shader_preview(sprite_path, player=(200,44,44)):
    """Exakter Nachbau von combined.frag ColorShift zum Gegencheck (kein grobes Toenen)."""
    import colorsys
    REF_S,REF_V=0.925,0.95
    ph,ps,pv=colorsys.rgb_to_hsv(*[x/255. for x in player]); sh,ss,sv=ph-REF_H,ps-REF_S,pv/REF_V
    a=np.array(Image.open(sprite_path).convert("RGBA")).astype(np.float32)/255.
    h,s,v=_to_hsv(a*255); A=a[:,:,3]
    m=(h>MINH)&(h<=MAXH)
    h2=np.where(m,(h+sh)%1.,h); s2=np.where(m,np.clip(s+ss,0,1),s); v2=np.where(m,v*min(max(sv,0),1),v)
    R,G,Bl=_to_rgb(h2,s2,v2)
    return Image.fromarray(np.dstack([R,G,Bl,A*255]).astype(np.uint8),"RGBA")


# --- CLI: für die Serien-Überarbeitung der Bestands-Gemini-Gebäude ---------------------
# Nutzung:
#   python3 aot_playercolor.py <idle.png> <remap.png> <out.png> [sat_target]
# Baut aus grau-Body + altem indizierten Remap ein Truecolor-Sprite für den Shader-Pfad,
# und schreibt eine rote Shader-Vorschau daneben (out_shaderred.png) zum Gegencheck.
if __name__ == "__main__":
    import sys, os
    idle, remap, out = sys.argv[1], sys.argv[2], sys.argv[3]
    sat = float(sys.argv[4]) if len(sys.argv) > 4 else GREEN_SAT_TARGET
    px = rebuild_from_remap(idle, remap, out, sat_target=sat)
    prev = shader_preview(out, (200, 44, 44))
    pv = out.rsplit(".", 1)[0] + "_shaderred.png"
    bg = Image.new("RGBA", prev.size, (238, 238, 238, 255)); bg.alpha_composite(prev)
    bg.convert("RGB").save(pv)
    print("Player-Region: %d px -> %s  (Vorschau: %s)" % (px, out, os.path.basename(pv)))


RAMP=[176,178,180,182,184,186,189,191,177,179,181,183,185,187,188,190]  # PlayerColorPalette RemapIndex
def indexed_to_truecolor(in_path, out_path, sat_target=GREEN_SAT_TARGET, value_floor=0.20):
    """Wandelt ein INDIZIERTES Player-Remap-PNG (mode P, ggf. multi-frame) in ein truecolor-
    Gruen-Sprite fuer den Shader-Pfad. Die Helligkeit kommt aus der Rampen-Position des Index
    (dunkel->hell), Hue=REF_H, Saettigung=sat_target. Behebt das Banding/Krisseln der 16-Stufen-
    Rampe bei feinen Details + Animation. Danach das Overlay auf Default-player-Palette stellen
    (Palette: player / IsPlayerPalette entfernen)."""
    im=Image.open(in_path); txt=dict(im.text) if hasattr(im,"text") else {}
    idx=np.array(im.convert("P")) if im.mode!="P" else np.array(im)
    trans=im.info.get("transparency",0)
    # Helligkeit von value_floor..1 (nicht 0..1): Index 176 ist die DUNKELSTE Player-
    # Farbe, nicht Schwarz -- ohne Boden wuerden die dunkelsten Facetten schwarz.
    lut={v:value_floor+(1-value_floor)*(i/15.0) for i,v in enumerate(RAMP)}
    H,W=idx.shape
    v=np.zeros((H,W),np.float32); m=np.zeros((H,W),bool)
    for val,lev in lut.items():
        sel=idx==val; v[sel]=lev; m|=sel
    R,G,B=_to_rgb(np.full((H,W),REF_H), np.where(m,sat_target,0), v)
    A=np.where(m,255,0).astype(np.uint8)
    out=np.dstack([R,G,B,A]).astype(np.uint8)
    meta=PngImagePlugin.PngInfo() if False else None
    from PIL import PngImagePlugin as _P
    meta=_P.PngInfo()
    for k,val in txt.items(): meta.add_text(k,val)
    Image.fromarray(out,"RGBA").save(out_path,pnginfo=meta)
    return int(m.sum())
