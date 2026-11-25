# Previews of the shutdown screen for the Nexus page, mirroring Reactor/TerminalScreens.cs at the in-game size
# (the terminal's face 1.50 x 1.19 m: 446 x 352). Run from TheExpanse/Docs: python tools/screens.py
# Writes screen_balance_small.png, screen_memorize_small.png, screen_resonance_small.png (300 px wide).
# Keep it in step with TerminalScreens.cs when the screen's layout changes.
from PIL import Image
import math
H=176; W=round(H*1.50/1.19); K=2
G={'A':"01110100011000111111100011000110001",'B':"11110100011000111110100011000111110",'C':"01110100011000010000100001000101110",'D':"11110100011000110001100011000111110",'E':"11111100001000011110100001000011111",'F':"11111100001000011110100001000010000",'G':"01110100011000010111100011000101111",'H':"10001100011000111111100011000110001",'I':"01110001000010000100001000010001110",'K':"10001100101010011000101001001010001",'L':"10000100001000010000100001000011111",'N':"10001110011010110011100011000110001",'O':"01110100011000110001100011000101110",'P':"11110100011000111110100001000010000",'R':"11110100011000111110101001001010001",'S':"01111100001000001110000010000111110",'T':"11111001000010000100001000010000100",'U':"10001100011000110001100011000101110",'V':"10001100011000110001100010101000100",'Y':"10001100010101000100001000010000100"}
Back=(3,10,14);Grid=(8,22,28);Dim=(30,70,80);Ink=(110,170,180);Bright=(190,240,245);Missing=(10,20,24);White=(225,235,240)
LC=[(140,190,255),(255,140,40),(255,40,20),(190,50,255)]
PC=[(40,220,255),(255,70,220),(255,220,40),(80,255,90)]
TitleY=H-20;StripY=16;LabelGap,LabelW,LabelH=13,23,7
RingRad=min(round(W*0.115),(TitleY-StripY)//2-7-LabelGap-LabelH-4)
RingCX=RingRad+7+LabelGap+LabelW+8;RingCY=(StripY+TitleY)//2+2
PanelX=max(round(W*0.52),RingCX+RingRad+7+LabelGap+LabelW+10)
LampsLeft=W-12-2*13-8
px=None
def P(x,y,c):
    if 0<=x<W*K and 0<=y<H*K: px[y][x]=c
def Pc(v): return v*K+K//2
def disc(cx,cy,r,c):
    x0,y0,pr=Pc(cx),Pc(cy),r*K;lim=(pr+.5)**2
    for y in range(-pr-1,pr+2):
        for x in range(-pr-1,pr+2):
            if x*x+y*y<=lim: P(x0+x,y0+y,c)
def circle(cx,cy,r,c):
    x0,y0,pr=Pc(cx),Pc(cy),r*K;n=max(32,round(pr*7))
    for a in range(n):
        t=a*2*math.pi/n;P(x0+round(pr*math.cos(t)),y0+round(pr*math.sin(t)),c)
def hl(x0,x1,y,c):
    py=Pc(y)
    for x in range(x0*K,x1*K+K): P(x,py,c)
def vl(x,y0,y1,c):
    p=Pc(x)
    for y in range(y0*K,y1*K+K): P(p,y,c)
def rect(x0,y0,x1,y1,c): hl(x0,x1,y0,c);hl(x0,x1,y1,c);vl(x0,y0,y1,c);vl(x1,y0,y1,c)
def fill(x0,y0,x1,y1,c):
    for y in range(y0*K,y1*K+K):
        for x in range(x0*K,x1*K+K): P(x,y,c)
def corners(x0,y0,x1,y1,c):
    k=8;hl(x0,x0+k,y0,c);vl(x0,y0,y0+k,c);hl(x1-k,x1,y0,c);vl(x1,y0,y0+k,c);hl(x0,x0+k,y1,c);vl(x0,y1-k,y1,c);hl(x1-k,x1,y1,c);vl(x1,y1-k,y1,c)
def line(x0,y0,x1,y1,c):
    ax,ay,bx,by=Pc(x0),Pc(y0),Pc(x1),Pc(y1)
    dx=abs(bx-ax);dy=-abs(by-ay);sx=1 if ax<bx else -1;sy=1 if ay<by else -1;err=dx+dy
    while True:
        P(ax,ay,c)
        if ax==bx and ay==by: break
        e2=2*err
        if e2>=dy: err+=dy;ax+=sx
        if e2<=dx: err+=dx;ay+=sy
def cross(cx,cy,r,c):
    a,b=Pc(cx),Pc(cy)
    for t in (-1,0,1):
        for k in range(-r*K,r*K+1): P(a+k+t,b+k,c);P(a+k+t,b-k,c)
def textw(t,dot): return (len(t)*6-1)*dot//K
def text(t,x,y,c,dot=3):
    pxx,py=x*K,y*K
    for ch in t:
        g=G.get(ch)
        if g:
            for row in range(7):
                for col in range(5):
                    if g[row*5+col]=='1':
                        for dy in range(dot):
                            for dx in range(dot): P(pxx+col*dot+dx,py+(6-row)*dot+dy,c)
        pxx+=6*dot
# injector directions on screen: starboard left; injector 0 at dorsal, then round
ang=[math.pi/2-i*math.pi/4 for i in range(8)]
def dotat(i,r): return RingCX+round(math.cos(ang[i])*r),RingCY+round(math.sin(ang[i])*r)
def base(level,nextfrac):
    global px
    px=[[Back]*(W*K) for _ in range(H*K)]
    for x in range(0,W,12): vl(x,0,H-1,Grid)
    for y in range(0,H,12): hl(0,W-1,y,Grid)
    rect(1,1,W-2,H-2,Dim);corners(3,3,W-4,H-4,Ink)
    title="FUSION TORCH DRIVE"
    text(title,8,TitleY+4,Bright,3 if textw(title,3)+8+6<=LampsLeft else 2);hl(6,W-7,TitleY,Dim)
    for k in (1,2,3):
        x=W-12-(3-k)*13;y=TitleY+9;c=LC[k]
        if level>=k: disc(x,y,7,tuple(v//5 for v in c));disc(x,y,6,tuple(v//2 for v in c));disc(x,y,4,c);disc(x-1,y+1,1,(255,245,230))
        else: disc(x,y,4,Back);circle(x,y,4,Dim)
    hl(6,W-7,StripY+14,Dim);t0,t1=8,W-10;rect(t0,4,t1,14,Dim)
    if nextfrac is not None:
        c=LC[min(3,level+1)];end=t0+2+round((t1-t0-4)*nextfrac);fill(t0+2,6,end,12,c);hl(t0+2,end,11,tuple(min(255,v+60) for v in c))
HERE=[math.radians(171.0)]
def discf(cx,cy,r,c):
    x0,y0,pr=Pc(cx),Pc(cy),r*K;n=int(pr)+2
    for y in range(-n,n+1):
        for x in range(-n,n+1):
            if x*x+y*y<=pr*pr: P(x0+x,y0+y,c)
def tri(ax,ay,bx,by,cx,cy,c):
    x0,y0,x1,y1,x2,y2=Pc(ax),Pc(ay),Pc(bx),Pc(by),Pc(cx),Pc(cy)
    for y in range(min(y0,y1,y2),max(y0,y1,y2)+1):
        for x in range(min(x0,x1,x2),max(x0,x1,x2)+1):
            e=lambda a1,b1,a2,b2:(a2-a1)*(y-b1)-(b2-b1)*(x-a1)
            e0,e1,e2=e(x0,y0,x1,y1),e(x1,y1,x2,y2),e(x2,y2,x0,y0)
            if (e0>=0 and e1>=0 and e2>=0) or (e0<=0 and e1<=0 and e2<=0): P(x,y,c)
def ring(con,color,mistake=-1):
    r=RingRad;circle(RingCX,RingCY,r,Dim);circle(RingCX,RingCY,r+9,Grid)
    for a in range(0,360,15):
        rad=math.radians(a);ln=5 if a%45==0 else 2
        line(RingCX+round(math.cos(rad)*(r+9)),RingCY+round(math.sin(rad)*(r+9)),RingCX+round(math.cos(rad)*(r+9+ln)),RingCY+round(math.sin(rad)*(r+9+ln)),Dim)
    hl(RingCX-4,RingCX+4,RingCY,Dim);vl(RingCX,RingCY-4,RingCY+4,Dim);circle(RingCX,RingCY,3,Ink)
    for i in range(8):
        x,y=dotat(i,r);disc(x,y,7,Back)
        if con[i]: disc(x,y,6,color(i))
        else: disc(x,y,6,Missing);circle(x,y,6,White)
        if i==mistake: cross(x,y,9,(255,30,30))
    a=HERE[0];ca,sa=math.cos(a),math.sin(a);reach=r+9+9*max(0,-sa)-2*max(0,sa)
    tx,ty=RingCX+round(ca*reach),RingCY+round(sa*reach);hy=ty+7;rad=3.5
    tri(tx,ty,tx-round(rad*.9),hy,tx+round(rad*.9),hy,(255,255,255));discf(tx,hy,rad,(255,255,255));discf(tx,hy,rad*.4,Back)
    e=r+7+LabelGap
    text("DRSL",RingCX-LabelW//2,RingCY+e,Ink,2);text("VNTR",RingCX-LabelW//2,RingCY-e-LabelH,Ink,2)
    text("STBD",RingCX-e-LabelW,RingCY-LabelH//2,Ink,2);text("PORT",RingCX+e,RingCY-LabelH//2,Ink,2)
def lamp(x,y,on,c):
    if on: disc(x,y,5,c)
    else: disc(x,y,5,Missing);circle(x,y,5,White)
def save(name):
    img=Image.new('RGB',(W*K,H*K))
    for y in range(H*K):
        for x in range(W*K): img.putpixel((x,H*K-1-y),px[y][x])
    img.resize((W*K*2,H*K*2),Image.NEAREST).save(name)
con=[True]*8;con[0]=con[4]=False;con[2]=False
# ---- Balance: level 1, counting down
base(1,0.6)
for i in range(4):
    ax,ay=dotat(i,RingRad-7);bx,by=dotat(i+4,RingRad-7)
    c=PC[i];line(ax,ay,bx,by,(c[0]//2,c[1]//2,c[2]//2) if (con[i] or con[i+4]) else (50,55,60))
ring(con,lambda i:PC[i%4])
for i in range(4):
    y=TitleY-22-i*24;x=PanelX;c=PC[i]
    fill(x,y-1,x+12,y+1,(c[0]//2,c[1]//2,c[2]//2));lamp(x+20,y,con[i],c);hl(x+26,x+32,y,Dim);lamp(x+38,y,con[i+4],c)
    if not con[i] and not con[i+4]: text("OK",x+45,y-3,Ink,2)
save('screen_balance.png')
HERE[0]=math.radians(-10.5)
# ---- Memorize: level 2 after a mistake (on 2), the order being read out, step 3 of 5
order=[3,7,1,5,6];step=3;shown=order[step]
base(2,0.4)
ring(con,lambda i:(255,255,255) if i==shown else (30,80,90),mistake=2)
x,y=dotat(shown,RingRad);circle(x,y,10,Bright)
cx=(PanelX+W-10)//2;cy=RingCY;r=min((W-10-PanelX)//2-8,(TitleY-StripY)//2-14)
def at(i): return cx+round(math.cos(ang[i])*r),cy+round(math.sin(ang[i])*r)
circle(cx,cy,r,Grid)
for i in range(8):
    x,y=at(i)
    if con[i]: disc(x,y,3,(30,80,90))
    else: circle(x,y,3,White)
sx,sy=at(order[0]);circle(sx,sy,6,Bright)
def arrow(ax,ay,bx,by,c,stop):
    dx,dy=bx-ax,by-ay;l=math.hypot(dx,dy);ux,uy=dx/l,dy/l
    tx,ty=bx-round(ux*stop),by-round(uy*stop)
    line(ax+round(ux*stop),ay+round(uy*stop),tx,ty,c)
    for sd in (-1,1):
        wx=-ux*0.87+sd*-uy*0.5;wy=-uy*0.87+sd*ux*0.5;line(tx,ty,tx+round(wx*5),ty+round(wy*5),c)
for k in range(1,step+1):
    a=at(order[k-1]);b=at(order[k]);v=int(80+175*k/step);arrow(a[0],a[1],b[0],b[1],(v,v,min(255,v+10)),5)
x,y=at(order[step]);disc(x,y,4,(255,255,255))
save('screen_memorize.png')
HERE[0]=math.radians(100.0)
# ---- Resonance: level 0, no clock; the owed one (3) on the beat
base(0,None)
teal=(60,200,215)
def shade(g): return (int(20+(teal[0]-20)*g),int(45+(teal[1]-45)*g),int(50+(teal[2]-50)*g))
beats=0.5
offs={i:0.5+((i*37)%25-12)/100 for i in range(8)};offs[3]=0
def glow(o):
    f=(beats+o-0.5)%1;d=min(f,1-f);return max(0,min(1,1-d*4))
ring(con,lambda i:shade(glow(offs[i])))
disc(RingCX,RingCY,5,shade(1));circle(RingCX,RingCY,8,teal)
x0,x1,mid=PanelX,W-10,RingCY;amp=min(26,(TitleY-StripY)//4)
rect(x0-4,mid-amp-10,x1+2,mid+amp+10,Dim);hl(x0,x1,mid,Grid)
for x in range(x0,x1,16): vl(x,mid-amp-8,mid+amp+8,Grid)
def wave(ph,c,th):
    last=None
    for p in range(x0*K,x1*K):
        x=p/K;py=round((mid+amp*math.sin(((x-x0)/32+ph)*2*math.pi))*K)+K//2
        a,b=(py,py) if last is None else (min(last,py),max(last,py))
        for yy in range(a,b+1):
            for t in range(-(th-1),th): P(p,yy+t,c)
        last=py
sh=0
for i in range(8):
    if con[i] and i!=3:
        g=55+(sh*9)%36;sh+=1;wave(offs[i],(g,g+10,g+18),1)
wave(0,(255,220,40),2)
save('screen_resonance.png')

# the page's small copies; the full-size drafts are not kept
import os
for n in ('balance','memorize','resonance'):
    im=Image.open(f'screen_{n}.png'); w=300
    im.resize((w,round(im.height*w/im.width)),Image.LANCZOS).save(f'screen_{n}_small.png')
    os.remove(f'screen_{n}.png')
print('written: screen_balance_small.png, screen_memorize_small.png, screen_resonance_small.png')
