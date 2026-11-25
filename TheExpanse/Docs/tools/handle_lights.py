# The handle lights chart (handle_lights.png) for the docs. Run from TheExpanse/Docs: python tools/handle_lights.py
from PIL import Image, ImageDraw, ImageFont
W, H = 1600, 900
img = Image.new('RGB', (W, H), (22, 24, 28)); d = ImageDraw.Draw(img)
def font(sz):
    for f in ['segoeuib.ttf', 'arialbd.ttf', 'arial.ttf']:
        try: return ImageFont.truetype(f, sz)
        except OSError: pass
    return ImageFont.load_default()
F, FS, FB, FT = font(28), font(22), font(40), font(20)
d.text((40, 24), "Plasma injector release timing  -  use the handle while all three lights are dark", fill=(235, 200, 60), font=FB)

K=[(0,-0.17),(0.10,-0.155),(0.15,-0.09),(0.16,0.0),(0.13,0.09),(0.075,0.17),(0.045,0.24),(0.045,0.31),(0.0,0.25),(-0.035,0.17),(-0.045,0.10),(-0.075,0.13),(-0.085,0.19),(-0.13,0.09),(-0.16,0.0),(-0.15,-0.09),(-0.10,-0.155)]
def flame():
    pts=[];n=len(K)
    for i in range(n):
        p0,p1,p2,p3=K[(i-1)%n],K[i],K[(i+1)%n],K[(i+2)%n]
        for s in range(6):
            t=s/6;t2=t*t;t3=t2*t
            pts.append(tuple(0.5*(2*p1[k]+(p2[k]-p0[k])*t+(2*p0[k]-5*p1[k]+4*p2[k]-p3[k])*t2+(3*p1[k]-p0[k]-3*p2[k]+p3[k])*t3) for k in (0,1)))
    return pts
FL=flame()
def sign(cx, cy, size, up):   # up = +1 tip up, -1 tip down
    def P(x, y): return (cx + x*size, cy - up*y*size)
    def tri(side):
        h=side*0.866; return [P(-side/2,-h/3),P(0,h*2/3),P(side/2,-h/3)]
    d.polygon(tri(1), fill=(10,10,10)); d.polygon(tri(0.78), fill=(240,190,20)); d.polygon([P(x,y) for x,y in FL], fill=(10,10,10))

labels=[(0,"DARK - use it now",(90,220,120)),(1,"1 lit",(240,90,80)),(2,"2 lit",(240,90,80)),(3,"3 lit - dark is next",(240,170,60))]
for n,(lit,label,col) in enumerate(labels):
    x0=40+n*390; cx=x0+175
    d.rounded_rectangle([x0,100,x0+350,560],18,fill=(40,44,50),outline=(90,95,105),width=3)
    for top in (True, False):     # grey mounting blocks with their signs, tips away from the handle
        y0 = 120 if top else 440
        d.rounded_rectangle([x0+60,y0,x0+290,y0+100],10,fill=(95,100,108),outline=(60,62,68),width=2)
        sign(cx, y0+50+(10 if top else -10), 80, 1 if top else -1)
    for k in range(3):
        lx, ly = cx-70+k*50, 260
        on = k < lit
        if on: d.ellipse([lx-24,ly-24,lx+24,ly+24], fill=(130,25,15))
        d.ellipse([lx-15,ly-15,lx+15,ly+15], fill=(255,45,25) if on else (45,18,18), outline=(15,15,15), width=2)
    d.ellipse([cx-75,290,cx+75,430], fill=(30,32,36), outline=(70,72,78), width=3)
    d.rounded_rectangle([cx-20,295,cx+20,425],10,fill=(200,160,40),outline=(120,95,20),width=3)
    d.text((cx,575),label,fill=col,font=F,anchor="mt")

# the cycle
y=650
d.text((40,y),"The cycle repeats on the core's beats (1.5 s each): dark, 1, 2, 3, dark ...  The dark step narrows as the core escalates.",fill=(210,210,210),font=FS)
steps=[("dark",(30,90,50)),("1",(110,30,25)),("2",(140,30,25)),("3",(170,30,25))]*2
bw=(W-80)/len(steps)
for i,(t,c) in enumerate(steps):
    d.rectangle([40+i*bw,y+40,40+(i+1)*bw-4,y+90],fill=c)
    d.text((40+i*bw+bw/2,y+65),t,fill=(235,235,235),font=F,anchor="mm")
rows=[("level 0-1","dark for a whole beat (1.5 s)"),("level 2","dark for about 2/3 of a beat"),("level 3","dark for 1/3 of a beat (0.5 s)"),("the lit steps","share the rest of the 4 beats")]
for i,(a,b) in enumerate(rows):
    d.text((60+(i%2)*760,y+115+(i//2)*34),f"{a}:  {b}",fill=(200,200,200),font=FS)
img.save('handle_lights.png'); print('ok')
