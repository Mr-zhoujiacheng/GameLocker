"""Original pixel cat-maid. Pillow only; no external character assets.
96x64 logical pixels, cropped to 96x60 (contact shadow at the bottom) and exported at 1x.
"""
from pathlib import Path
from PIL import Image, ImageColor, ImageDraw
import argparse
import math

ROOT = Path(__file__).resolve().parent
C={'ink':'#493052','dark':'#76528e','hair':'#c18bd6','light':'#e9b6eb','shine':'#ffdcf3','skin':'#ffe7df','shade':'#edb9cd','pink':'#ef93bc','dress':'#80619f','dressLight':'#ac86c1','white':'#fff5f7','gray':'#d6c6e6','eye':'#8b61c6','key':'#b2a0c9'}

HAIR_RAMP = {
 'hair': ('#9e7cdb','#f39ac9'),
 'light': ('#c5a7f2','#ffb9dc'),
 'shine': ('#e8d5ff','#ffdef0'),
 'dark': ('#7252a5','#bc628e'),
}

def shade_hair(im):
 # Six pixel-art shades, applied before drawing the accessories and eyes.
 ramps={}
 for key,(root,tip) in HAIR_RAMP.items():
  start,end=ImageColor.getrgb(root),ImageColor.getrgb(tip)
  ramps[ImageColor.getcolor(C[key],'RGBA')]=[
      tuple(round(a+(b-a)*n/5) for a,b in zip(start,end))+(255,) for n in range(6)]
 pixels=im.load()
 for y in range(52):
  band=min(5,y//9)
  for x in range(im.width):
   ramp=ramps.get(pixels[x,y])
   if ramp is not None: pixels[x,y]=ramp[band]

def draw_teacher_glasses(im):
 d=ImageDraw.Draw(im)
 ink=C['ink']
 d.line([(31,34),(33,35)],fill=ink)
 d.line([(44,36),(46,35),(48,35),(50,36)],fill=ink)
 d.line([(61,35),(64,34)],fill=ink)
 for x in (33,49):
  d.ellipse((x,30,x+12,42),outline=ink)
  d.line([(x+3,31),(x+4,30),(x+7,30)],fill='#bd8ea9')

HEADPHONE_POSES = (
 None,
 (33, 48, 29, 34, 51),
 (27, 43, 20, 27, 46),
 (22, 36, 10, 21, 39),
 (22, 31, 5, 21, 34),
 (22, 31, 5, 36, 54),
)


def draw_headphones(d, x, y, top):
 right = 94 - x
 outer = '#332743'
 purple = '#624881'
 width = right - x
 band = [(x, y-2), (x+1, top+13), (x+round(width*.12), top+6),
         (x+round(width*.28), top+1), (x+round(width*.4), top),
         (right-round(width*.4), top), (right-round(width*.28), top+1),
         (right-round(width*.12), top+6), (right-1, top+13), (right, y-2)]
 d.line(band, fill=outer, width=5, joint='curve')
 d.line(band, fill=purple, width=3, joint='curve')
 d.line(band[2:8], fill='#d29ddb')
 for cup in (x, right):
  inward = 1 if cup == x else -1
  d.rounded_rectangle((cup-5, y-7, cup+5, y+7), radius=4, fill=outer)
  d.rounded_rectangle((cup-4, y-6, cup+4, y+6), radius=3, fill=purple)
  d.line([(cup+3*inward, y-4), (cup+3*inward, y+4)], fill='#443354', width=2)
  d.rounded_rectangle((cup-2, y-4, cup+2, y+3), radius=2, fill='#ec98c5')
  d.point((cup-1, y-3), fill='#fff0f9')
  d.line([(cup-1, y+4), (cup+1, y+4)], fill='#b180d5')


def upper_inset(action,phase):
 if action=='blink': return (2,1,1,2)[phase%4]
 if action=='wake': return (0,0,0,1,1,1,2,2)[phase]
 if action=='listen': return (2,1,0,1,2,3,4,3)[phase]
 return (2,2,1,0,0,1,2,2)[phase]

def draw(action,t,*,tail_resting=False,wearing_glasses=True,wearing_headphones=False):
 im=Image.new('RGBA',(96,64)); d=ImageDraw.Draw(im)
 def poly(p,c): d.polygon(p,fill=C.get(c,c))
 def rect(b,c): d.rectangle(b,fill=C.get(c,c))
 def line(p,c,w=1): d.line(p,fill=C.get(c,c),width=w)
 def ellipse(b,c): d.ellipse(b,fill=C.get(c,c))
 # The tile starts at y=52. Only the hands may cross this line.
 sway=0 if tail_resting else round((5 if action=='swish' else 3)*math.sin(t*math.tau))
 tail=[(63,49),(74,47),(81,42),(85,34+sway),(83,28+sway),(77,27+sway),(75,30+sway)]
 line(tail,'ink',6);line(tail,'hair',4);line(tail[3:],'light',2)
 tail_layer=im
 im=Image.new('RGBA',tail_layer.size);d=ImageDraw.Draw(im)
 # Shoulders and apron stop at the edge; the tile conceals the rest.
 poly([(34,40),(41,37),(55,37),(63,40),(66,51),(30,51)],'ink')
 poly([(35,41),(42,39),(54,39),(61,42),(64,51),(32,51)],'dress')
 poly([(42,41),(54,41),(56,51),(39,51)],'white')
 line([(42,48),(45,50),(52,50),(54,48)],'gray')
 # The back hair follows the crown, then tapers inward below each cheek.
 poly([(32,16),(26,19),(23,26),(23,37),(22,43),(23,48),(26,51),(26,49),
       (29,50),(33,46),(34,38),(38,24)],'ink')
 poly([(32,19),(27,21),(25,27),(25,37),(24,43),(25,47),(28,48),(31,45),
       (32,38),(36,25)],'hair')
 line([(27,29),(27,38),(26,43),(27,46)],'light',2)
 line([(30,39),(30,44),(28,47)],'dark')
 poly([(61,16),(67,19),(70,26),(70,37),(71,42),(70,47),(67,51),(67,48),
       (64,49),(60,45),(58,37),(55,23)],'ink')
 poly([(61,19),(66,21),(68,27),(68,37),(69,42),(68,47),(65,47),(62,44),
       (60,37),(57,25)],'hair')
 line([(66,29),(66,37),(67,42),(66,45)],'light',2)
 line([(63,38),(63,43),(65,46)],'dark')
 # Upright cat ears flank the frill. The cowlick arches through the gap.
 poly([(25,16),(24,4),(25,2),(28,3),(34,8),(40,13)],'ink')
 poly([(27,13),(26,5),(29,7),(35,12)],'hair');poly([(28,12),(27,6),(33,11)],'pink')
 poly([(54,13),(61,7),(67,2),(69,3),(68,16)],'ink')
 poly([(59,12),(66,6),(67,5),(66,13)],'hair');poly([(61,11),(66,6),(65,12)],'pink')
 poly([(50,12),(48,7),(45,3),(42,3),(38,6),(38,3),(41,0),(46,0),(50,3),
       (53,8),(53,12)],'ink')
 poly([(51,10),(49,6),(46,2),(42,1),(40,3),(42,2),(45,3),(48,7),(50,11)],'hair')
 line([(43,1),(46,1),(48,3)],'light')
 ellipse((24,9,69,44),'ink');ellipse((26,11,67,42),'hair')
 # Elongate the lower face without moving the eyes or fringe.
 poly([(34,22),(59,21),(64,26),(66,34),(65,40),(62,45),(57,48),(51,50),
       (44,50),(37,48),(32,44),(29,39),(29,29)],'ink')
 poly([(35,23),(58,23),(63,27),(64,34),(63,40),(60,44),(56,47),(51,49),
       (44,49),(38,47),(33,43),(31,39),(31,29)],'skin')
 # Front locks overlap the back hair and curve toward the face.
 poly([(25,23),(29,17),(36,14),(51,14),(63,19),(67,25),(67,33),(65,39),(62,43),
       (62,36),(60,31),(57,28),(55,31),(51,30),(49,28),(48,33),(46,35),(43,31),
       (41,27),(38,31),(34,31),(32,34),(31,38),(31,43),(27,40),(25,34)],'hair')
 poly([(30,24),(34,21),(39,19),(45,19),(49,20),(47,21),(41,21),(36,22),(32,25)],'light')
 line([(52,20),(56,21),(59,23)],'shine')
 line([(43,24),(45,28),(46,31)],'dark')
 line([(30,31),(29,36),(30,39)],'light')
 line([(63,31),(64,36),(63,39)],'light')
 shade_hair(im)
 # Maid frill and pink bow.
 poly([(29,17),(31,12),(35,10),(38,11),(41,8),(45,10),(49,8),(53,10),(56,9),
       (60,12),(62,17),(57,16),(52,14),(47,15),(42,14),(36,16),(33,18)],'ink')
 poly([(31,16),(33,12),(35,12),(38,13),(41,10),(45,12),(49,10),(53,12),(56,11),
       (59,13),(60,16),(55,14),(52,13),(47,14),(42,13),(37,14),(34,16)],'white')
 line([(35,15),(42,13),(48,13),(55,14)],'gray')
 poly([(27,21),(22,17),(20,18),(20,24),(25,24),(29,27),(32,25),(31,20)],'ink')
 poly([(26,21),(22,19),(22,23),(26,22),(29,25),(30,24),(30,21)],'pink')
 rect((25,21,27,22),'shine')
 phase=round(t*8)%8
 headphone_pose=HEADPHONE_POSES[phase] if action=='headphones' else None
 listening=action=='listen'
 winking=action=='wink' and 2<=phase<=4
 blushing=action=='blush' and 1<=phase<=6
 glance=({'peek':(0,-1,-2,-2,0,2,2,0),'blush':(0,0,1,1,1,0,0,0)}
         .get(action,(0,)*8))[phase]
 alarmed=action=='scared' or (action=='wake' and phase<6)
 # A gently arched eyelid and bright lower iris give the idle gaze more lift.
 for x in (() if listening else (35,51)):
  if winking and x==51: continue
  if action=='happy':
   line([(x,36),(x+2,33),(x+5,33),(x+8,36)],'ink')
  elif action=='angry':
   inner=1 if x==35 else -1
   brow=[(x if inner==1 else x+8,32),(x+4,34),(x+8 if inner==1 else x,35)]
   poly([(x,35),(x+8,35),(x+7,39),(x+5,41),(x+2,41),(x,39)],'white')
   ellipse((x+2,35,x+6,40),'eye');rect((x+3,35,x+5,38),'dark')
   line(brow,'ink',2)
  elif action in ('blink','sleep'):
   line([(x,35),(x+2,37),(x+5,37),(x+8,35)],'ink')
  elif alarmed:
   ellipse((x,30,x+8,41),'ink')
   ellipse((x+1,31,x+7,40),'white')
   pupil=x+4+(phase%3==2 if action=='scared' else 0)
   ellipse((pupil-1,34,pupil,37),'dark')
  else:
   lid=33 if action=='grip' else 32
   poly([(x,lid+2),(x+2,lid),(x+5,lid),(x+8,lid+2),(x+8,38),(x+6,41),(x+2,41),(x,39)],'white')
   poly([(x+1,lid+2),(x+3,lid+1),(x+5,lid+1),(x+7,lid+3),(x+7,38),(x+5,41),(x+2,40),(x+1,38)],'eye')
   ellipse((x+2+glance,lid+1,x+6+glance,38),'dark')
   line([(x+2,39),(x+5,39)],'#c5b5ed')
   line([(x+3,40),(x+5,40)],'#e0d6fa')
   line([(x-1,lid+1),(x,lid+2),(x+2,lid),(x+5,lid),(x+7,lid+1),(x+8,lid+2)],'ink')
   rect((x+2+glance,lid+2,x+3+glance,lid+3),'white')
   d.point((x+6+glance,38),fill=C['shine'])
 ellipse((31,40,36,43),'shade');ellipse((58,40,63,43),'shade')
 line([(32,41),(34,42)],'pink');line([(60,42),(62,41)],'pink')
 d.point((33,41),fill=C['shine']);d.point((61,41),fill=C['shine'])
 if action=='happy':
  line([(45,42),(47,44),(49,42)],'ink');line([(46,43),(48,43)],'pink')
 elif action=='angry':
  line([(44,45),(46,43),(48,43),(50,45)],'ink')
 elif alarmed:
  if action=='wake': ellipse((45,42,49,46),'ink')
  else: ellipse((44,42,50,46),'ink')
  line([(46,45),(48,45)],'pink')
 elif action=='sleep':
  line([(46,43),(48,43)],'dark')
 elif not (listening or winking or blushing or (action=='peek' and phase==4)):
  line([(45,43),(46,44),(48,44),(49,43)],'dark')
 if action=='teacher' and wearing_glasses:
  draw_teacher_glasses(im)
 if headphone_pose:
  draw_headphones(d,*headphone_pose[:3])
 elif wearing_headphones or listening:
  draw_headphones(d,*HEADPHONE_POSES[-1][:3])
 # Collar and cuffed sleeves behind the fingers.
 poly([(43,48),(48,49),(53,48),(53,51),(48,50),(43,51)],'pink')
 line([(47,49),(49,49)],'shine')
 # Breathe around the grip line without displacing the cuffs or fingers.
 inset=upper_inset(action,phase)
 upper=im.crop((0,0,96,48)).resize((96,48-inset),Image.Resampling.NEAREST)
 tail_layer.alpha_composite(upper,(0,inset))
 tail_layer.alpha_composite(im.crop((0,48,96,52)),(0,48))
 im=tail_layer;d=ImageDraw.Draw(im)
 if listening or winking:
  # Draw after scaling so a one-pixel eyelid stays intact.
  eye_y=35+round(inset*(48-35)/48)
  for x in ((35,51) if listening else (51,)):
   line([(x,eye_y+1),(x+2,eye_y),(x+6,eye_y),(x+8,eye_y+1)],'ink')
  mouth_y=42+round(inset*(48-42)/48)
  line([(44,mouth_y),(45,mouth_y+1),(48,mouth_y+1),(49,mouth_y)],'ink')
 if blushing:
  blush_level=min(phase-1,6-phase)
  cheeks=('#f7c9d9','#f3abc5','#ef93bc')[blush_level]
  hatch=('#e8a1bb','#e185ab','#d76b9e')[blush_level]
  for x in (32,57):
   ellipse((x-2,40,x+6,45),cheeks)
   for stripe in (0,2,4):
    line([(x+stripe,43),(x+stripe+1,41)],hatch)
  line([(45,44),(46,43),(48,43),(49,44)],'ink')
 elif action=='peek' and phase==4:
  ellipse((45,42,49,46),'ink')
  rect((46,43,48,44),'pink')
 squeeze=int(action=='grip' and 2<=round(t*8)<=5)
 for x in ((36,) if action=='teacher' else (36,59)):
  if headphone_pose and phase<5:
   _,_,_,hand_x,hand_y=headphone_pose
   side=1 if x==36 else -1
   hand_x=hand_x if side==1 else 94-hand_x
   hand_y+=round(inset*(48-hand_y)/48)
   line([(x,48),(hand_x+side*2,hand_y+3),(hand_x,hand_y)],'ink',8)
   line([(x,48),(hand_x+side*2,hand_y+3)],'dress',5)
   ellipse((hand_x-4,hand_y-4,hand_x+4,hand_y+3),'ink')
   ellipse((hand_x-3,hand_y-3,hand_x+3,hand_y+2),'skin')
   line([(hand_x-1,hand_y+1),(hand_x+1,hand_y+1)],'shade')
   continue
  poly([(x-5,48),(x-3,45),(x+3,45),(x+5,48),(x+5,51),(x-5,51)],'ink')
  rect((x-3,47,x+3,51),'white')
  # Fingers curl onto the front of the tile, with the wrist above its edge.
  end=58-squeeze
  poly([(x-5,51),(x-3,49),(x+3,49),(x+5,51),(x+5,end-1),(x+3,end),(x-3,end),(x-5,end-1)],'ink')
  poly([(x-4,51),(x-2,50),(x+2,50),(x+4,51),(x+4,end-1),(x-4,end-1)],'skin')
  line([(x-2,end-2),(x-2,end-1)],'shade');line([(x+1,end-2),(x+1,end-1)],'shade')
 # sparse expressive pixels, not random speech
 if action=='happy':
  poly([(68,19),(70,17),(72,19),(70,23)],'pink');rect((69,18,70,19),'shine')
 if action=='sleep':
  dy=phase//2
  line([(69,23-dy),(73,23-dy),(69,27-dy),(73,27-dy)],'light')
  if phase>=2: line([(75,16-dy),(80,16-dy),(75,20-dy),(80,20-dy)],'light')
 if action=='wake' and phase<5:
  rect((74,10-phase,76,16-phase),'shine');rect((74,18-phase,76,20-phase),'pink')
 if action=='scared':
  dy=phase%4
  poly([(69,30+dy),(68,33+dy),(69,35+dy),(71,34+dy)],'#fff0fc')
 if action=='angry':
  dy=phase//2
  line([(73,19-dy),(76,19-dy),(76,16-dy)],'#f5769f',2)
  line([(80,19-dy),(80,22-dy),(83,22-dy)],'#f5769f',2)
  line([(73,26-dy),(76,26-dy),(76,29-dy)],'#f5769f',2)
  line([(80,26-dy),(83,26-dy)],'#ffb9dc')
 if winking:
  x,y=72+phase,24-phase
  poly([(x,y-4),(x+1,y-1),(x+4,y),(x+1,y+1),(x,y+4),(x-1,y+1),(x-4,y),(x-1,y-1)],'#edac66')
  line([(x-1,y),(x+1,y)],'white')
 if listening:
  for x,start,color in ((10,0,'#eca9d3'),(76,4,'#cdb4f1')):
   age=(phase-start)%8
   if age>=6: continue
   y=29-age*2
   ellipse((x-3,y,x+1,y+3),color)
   line([(x+1,y+1),(x+1,y-5),(x+4,y-3)],color)
 return im

def save_music_frames(cw,ch):
 for resting in (False,True):
  prefix='rest-' if resting else ''
  for action,count in (('headphones',6),('listen',8),('scared',8),('happy',8),('angry',8)):
   name=f'listen-{action}' if action in ('scared','happy','angry') else action
   for n in range(count):
    frame=draw(action,n/8,tail_resting=resting,wearing_headphones=name!=action)
    frame.crop((0,0,cw,ch)).save(ROOT/f'catmaid-{prefix}{name}-{n}.png')

def save_teacher_frames(cw,ch):
 pose=3/8
 bare=draw('teacher',pose,tail_resting=True,wearing_glasses=False).crop((0,0,cw,ch))
 inset=upper_inset('teacher',round(pose*8)%8)
 upper=Image.new('RGBA',(cw,48))
 draw_teacher_glasses(upper)
 glasses=Image.new('RGBA',(cw,ch))
 glasses.alpha_composite(upper.resize((cw,48-inset),Image.Resampling.NEAREST),(0,inset))
 bare.save(ROOT/'catmaid-teacher-bare.png')
 glasses.save(ROOT/'catmaid-teacher-glasses.png')
 # Preserve the legacy sprite when generating either the layers or all frames.
 legacy=ROOT/'catmaid-teacher.png'
 if not legacy.exists():
  with legacy.open('xb') as target:
   Image.alpha_composite(bare,glasses).save(target,format='PNG')

def main():
 parser=argparse.ArgumentParser(description=__doc__)
 mode=parser.add_mutually_exclusive_group()
 mode.add_argument('--teacher-only',action='store_true',help='Generate only the classroom sprites without overwriting the legacy teacher PNG.')
 mode.add_argument('--music-only',action='store_true',help='Generate only the headphone and listening sprites.')
 args=parser.parse_args()
 cw,ch=96,60
 if args.music_only:
  save_music_frames(cw,ch)
  return
 save_teacher_frames(cw,ch)
 if args.teacher_only: return
 save_music_frames(cw,ch)
 actions=('idle','blink','grip','swish','happy','sleep','wake','scared','angry','peek','wink','blush')
 frames={}
 for resting in (False,True):
  prefix='rest-' if resting else ''
  for action in actions:
   for n in range(8):
    im=draw(action,n/8,tail_resting=resting).crop((0,0,cw,ch)); frames[resting,action,n]=im
    im.save(ROOT/f'catmaid-{prefix}{action}-{n}.png')
 backdrop=Image.new('RGBA',(96,100),'#24212e')
 ImageDraw.Draw(backdrop).rounded_rectangle((6,60,89,99),radius=5,fill='#363744',outline='#66677c')
 seq=[];times=[]
 # Condensed interaction preview; the application uses a 60-second idle timeout.
 for action,duration,step in (('idle',1200,150),('happy',1280,160),('angry',1200,150),
                              ('sleep',1840,230),('wake',720,90),('scared',1600,100),('idle',1200,150)):
  for elapsed in range(0,duration,step):
   offset=0
   if action=='scared':
    offset=round(8 * min(1,elapsed/350) * (1 - min(1,max(0,elapsed-1200)/350)**2))
   canvas=backdrop.copy()
   canvas.alpha_composite(frames[action=='sleep',action,(elapsed//step)%8],(offset,8))
   seq.append(canvas.resize((384,400),Image.Resampling.NEAREST).convert('RGB'))
   times.append(min(step,duration-elapsed))
 out=ROOT.parents[1]/'docs'/'catmaid-preview.gif'
 seq[0].save(out,save_all=True,append_images=seq[1:],duration=times,loop=0,disposal=2)
 print('frames',cw,ch,'preview',out)
if __name__=='__main__': main()
