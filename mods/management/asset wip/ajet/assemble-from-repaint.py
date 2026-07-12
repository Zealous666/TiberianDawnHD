import numpy as np, json, os
from PIL import Image, ImageDraw

OUT="out"; os.makedirs(OUT,exist_ok=True)
planes=json.load(open("extract/planes.json"))
def load(i):
    # Bild ist bereits sauber freigestellt -> vorhandenes Alpha nutzen, RGB nicht anfassen
    im=Image.open(f"extract/cell_{i:02d}.png").convert("RGBA")
    b=im.getbbox()          # trimmt an vorhandenem Alpha
    return im.crop(b) if b else im
imgs={p["idx"]:load(p["idx"]) for p in planes}
head={p["idx"]:p["heading"] for p in planes}

# gemeinsame Leinwand
CAN=max(max(im.width,im.height) for im in imgs.values())+20
def canvas(im):
    c=Image.new("RGBA",(CAN,CAN),(0,0,0,0))
    c.alpha_composite(im,((CAN-im.width)//2,(CAN-im.height)//2)); return c

def cdist(a,b):
    d=abs(a-b)%360; return min(d,360-d)
def nearest(h):
    return min(head, key=lambda i: cdist(head[i],h))

def frame_heading(N): return (360-11.25*N)%360   # A10-Konvention: 0=N,8=W,16=S,24=E

assign={}
report=[]
# direkte Ost-Slots: 0 und 16..31
for N in [0]+list(range(16,32)):
    th=frame_heading(N); pi=nearest(th)
    assign[N]=("direct",pi); report.append((N,round(th,2),pi,head[pi],"direct"))
# West-Slots 1..15 gespiegelt aus 32-N
for N in range(1,16):
    src=32-N; _,pi=assign[src]
    assign[N]=("mirror",pi); report.append((N,round(frame_heading(N),2),pi,round((360-head[pi])%360,1),"mirror<-%d"%src))

for N in range(32):
    mode,pi=assign[N]; im=canvas(imgs[pi])
    if mode=="mirror": im=im.transpose(Image.FLIP_LEFT_RIGHT)
    im.save(f"{OUT}/ajet-{N:04d}.png")

# Preview 8x4 mit Labels
cols,rows=8,4
sheet=Image.new("RGBA",(cols*CAN,rows*(CAN+18)),(25,25,25,255)); d=ImageDraw.Draw(sheet)
for N in range(32):
    im=Image.open(f"{OUT}/ajet-{N:04d}.png").convert("RGBA")
    x,y=(N%cols)*CAN,(N//cols)*(CAN+18)
    sheet.alpha_composite(im,(x,y+18))
    mode,pi=assign[N]
    d.text((x+4,y+4),f"F{N} {frame_heading(N):.0f}d <#{pi}{'m' if mode=='mirror' else ''}",fill=(255,255,0,255))
sheet.save(f"{OUT}/_preview_32.png")
print("CAN",CAN)
for r in report: print("F%02d target%6.1f  <- plane #%d (%.0f)  %s"%r)
