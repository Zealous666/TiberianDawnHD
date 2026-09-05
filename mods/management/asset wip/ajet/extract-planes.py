import numpy as np, json, os
from PIL import Image, ImageDraw
from scipy import ndimage

SRC="repaint/generic_repaint_clean.png"; OUT="extract"; os.makedirs(OUT,exist_ok=True)
im=Image.open(SRC).convert("RGBA")
arr=np.asarray(im)
A=arr[...,3].astype(np.int32)
R,G,B=arr[...,0].astype(np.int32),arr[...,1].astype(np.int32),arr[...,2].astype(np.int32)

# Vordergrund NUR ueber Alpha (Bild ist bereits freigestellt)
fg=A>40
fg_c=ndimage.binary_closing(fg,structure=np.ones((5,5)),iterations=2)
lbl,n=ndimage.label(fg_c)
sizes=ndimage.sum(np.ones_like(lbl),lbl,range(1,n+1))
keep=[i+1 for i,s in enumerate(sizes) if s>1500]
print("Komponenten",n,"behalten",len(keep))

# Cockpit-Blau nur auf sichtbaren Pixeln
blue=(A>60)&(B>120)&(B>R+40)&(B>G+20)

comps=[]
for cid in keep:
    m=lbl==cid; ys,xs=np.where(m); cy,cx=ys.mean(),xs.mean()
    bm=m&blue
    if bm.sum()>20:
        bys,bxs=np.where(bm); dx,dy=bxs.mean()-cx,bys.mean()-cy
        heading=float(np.degrees(np.arctan2(dx,-dy))%360); ok=True
    else:
        heading=-1.0; ok=False
    comps.append(dict(cid=int(cid),cx=float(cx),cy=float(cy),
                      bbox=[int(xs.min()),int(ys.min()),int(xs.max()),int(ys.max())],
                      heading=round(heading,1),blue=ok))
comps.sort(key=lambda c:(round(c["cy"]/150),c["cx"]))
pad=6
for i,c in enumerate(comps):
    x0,y0,x1,y1=c["bbox"]
    crop=im.crop((max(0,x0-pad),max(0,y0-pad),x1+pad,y1+pad))  # RGBA unveraendert
    crop.save(f"{OUT}/cell_{i:02d}.png")
    c["idx"]=i
json.dump(comps,open(f"{OUT}/planes.json","w"),indent=1)

cols,rows,cell=6,3,280
mont=Image.new("RGBA",(cols*cell,rows*cell),(30,30,30,255)); d=ImageDraw.Draw(mont)
for c in comps:
    i=c["idx"]; cr=Image.open(f"{OUT}/cell_{i:02d}.png").convert("RGBA")
    s=min((cell-40)/cr.width,(cell-40)/cr.height,1.0)
    cr=cr.resize((int(cr.width*s),int(cr.height*s)),Image.LANCZOS)
    x,y=(i%cols)*cell,(i//cols)*cell
    mont.alpha_composite(cr,(x+(cell-cr.width)//2,y+30+(cell-cr.height)//2))
    d.text((x+6,y+6),f"#{i}  {c['heading']:.0f}deg"+("" if c["blue"] else " no-blue"),fill=(255,255,0,255))
    d.rectangle([x,y,x+cell-1,y+cell-1],outline=(80,80,80,255))
mont.save(f"{OUT}/_montage.png")
print(" ".join(f"#{c['idx']}={c['heading']:.0f}" for c in comps))
