import numpy as np
from scipy import ndimage
from PIL import Image

SRC="repaint/generic_repaint_magenta.png"
CLEAN="repaint/generic_repaint_clean.png"
M=np.array([250,3,251],float)

a=np.asarray(Image.open(SRC).convert("RGB")).astype(float)
R,G,B=a[...,0],a[...,1],a[...,2]
# 0 = reines Magenta, 1 = klar Nicht-Magenta (Flieger)
alpha_est=np.clip(1-(np.minimum(R,B)-G)/247.0,0,1)

# Hintergrund = magenta-Bereich, der den Bildrand beruehrt (schuetzt innere lila Panels)
bg_cand=alpha_est<0.2
lbl,n=ndimage.label(bg_cand)
border=set(lbl[0,:])|set(lbl[-1,:])|set(lbl[:,0])|set(lbl[:,-1]); border.discard(0)
bg=np.isin(lbl,list(border))
fg=~bg
ring=fg & ndimage.binary_dilation(bg,iterations=2)   # weiche Kante nur am Rand

out=np.zeros((*a.shape[:2],4),float)
out[...,:3]=a; out[...,3]=255.0
out[bg,3]=0
# Kante dekontaminieren: F=(C-(1-a)M)/a, weiches Alpha
ae=np.clip(alpha_est[ring],0.15,1.0)[:,None]
F=(a[ring][:,:3]-(1-ae)*M)/ae
out[ring,:3]=np.clip(F,0,255)
out[ring,3]=np.clip(alpha_est[ring]*255,0,255)

Image.fromarray(out.astype(np.uint8)).save(CLEAN)
# Kontrolle: Rest-Magenta auf halbtransparenten Kanten
o=out.astype(int); A=o[...,3]; Rr,Gg,Bb=o[...,0],o[...,1],o[...,2]
edge=(A>20)&(A<235); mag=edge&(Rr>Gg+25)&(Bb>Gg+25)
print("clean gespeichert. Kantenpixel",int(edge.sum()),"davon magenta",int(mag.sum()),
      f"({100*mag.sum()/max(1,edge.sum()):.0f}%)")
