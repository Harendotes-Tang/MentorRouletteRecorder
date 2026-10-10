#!/usr/bin/env python3
"""Train the bounded local job-icon MLP on CPU, without using real screenshots.

Install numpy and opencv-python-headless in an isolated training environment.
Example: python scripts/train-job-icon-classifier.py --output-model <model.json>
Model artwork sources retain the existing Square Enix distribution boundary.
Synthetic splits share canonical artwork; their accuracy is not field accuracy.
"""
import os
os.environ['OPENBLAS_NUM_THREADS']='2'
os.environ['OMP_NUM_THREADS']='2'
from pathlib import Path
import argparse,hashlib,json,re,time,sys
import cv2
import numpy as np
cv2.setNumThreads(2)
ROOT=Path(__file__).resolve().parents[1]
catalog=json.loads((ROOT/'src/Desktop/resources/icons/manifest.json').read_text(encoding='utf-8'))
catalog_source=(ROOT/'src/Desktop/cpp/JobCatalog.cpp').read_text(encoding='utf-8')
base=set(map(int,re.search(r'baseClasses\s*=\s*\{([^}]+)',catalog_source).group(1).split(',')))
limited=set(map(int,re.search(r'limitedJobs\s*=\s*\{([^}]+)',catalog_source).group(1).split(',')))
jobs=[j for j in catalog['jobs'] if j['role'] != '其他' and j['id'] not in base|limited]
ids=[0]+[j['id'] for j in jobs]
def read_image(path):return cv2.imdecode(np.frombuffer(Path(path).read_bytes(),np.uint8),cv2.IMREAD_UNCHANGED)
icons=[read_image(ROOT/'src/Desktop/resources/icons'/j['icon']['file']) for j in jobs]
excluded=[read_image(ROOT/'src/Desktop/resources/icons'/j['icon']['file']) for j in catalog['jobs'] if j not in jobs]
content=[read_image(p) for p in (ROOT/'src/Desktop/resources/icons/content_types').glob('*.png')]

def features(bgr):
    if bgr is None or bgr.size==0:return None
    alpha=bgr[:,:,3]>100 if bgr.shape[2]>3 else np.ones(bgr.shape[:2],bool)
    bgr=bgr[:,:,:3].astype(np.int16)
    b,g,r=cv2.split(bgr)
    mask=(alpha&(r>100)&(g>75)&(r*10>=g*9)&(r-b>25)&(g-b>20)).astype(np.uint8)
    n,labels,stats,_=cv2.connectedComponentsWithStats(mask,8)
    if n<2:return None
    selected=[]
    for i in range(1,n):
        x,y,w,h,a=stats[i]
        if a>=3 and x>0 and y>0 and x+w<mask.shape[1] and y+h<mask.shape[0]:selected.append(i)
    mask=np.isin(labels,selected).astype(np.uint8)
    yy,xx=np.nonzero(mask)
    if len(xx)<12:return None
    x0,x1,y0,y1=xx.min(),xx.max()+1,yy.min(),yy.max()+1
    w,h=x1-x0,y1-y0
    if w<3 or h<3 or w/h<.25 or w/h>4:return None
    glyph=mask[y0:y1,x0:x1]
    # Exact area sampling, mirrored by C++ integer overlap arithmetic.
    cells=np.arange(16)[:,None]
    def overlap(size):
        pixels=np.arange(size)[None,:]
        return np.maximum(0,np.minimum((cells+1)*size,(pixels+1)*16)-np.maximum(cells*size,pixels*16)).astype(np.float32)
    sample=overlap(h)@glyph.astype(np.float32)@overlap(w).T/(w*h)
    return np.r_[sample.flatten(),np.log(w/h)].astype(np.float32)

def augment(icon,rng,severity=1):
    h=int(rng.integers(20,93));w=int(h*rng.uniform(.82,1.22))
    resized=cv2.resize(icon,(w,h),interpolation=int(rng.choice([cv2.INTER_LINEAR,cv2.INTER_AREA,cv2.INTER_CUBIC])))
    py,px=int(rng.integers(6,30)),int(rng.integers(6,35))
    oh,ow=h+py*2,w+px*2
    mode=int(rng.integers(0,5))
    background=[(35,65,43),(26,30,33),(225,233,224),(241,241,241),(82,79,74)][mode]
    if mode==0: background=tuple(int(np.clip(v+rng.uniform(-15,15),0,255)) for v in background)
    canvas=np.full((oh,ow,3),background,np.float32)
    alpha=resized[:,:,3:4].astype(np.float32)/255
    rgb=resized[:,:,:3].astype(np.float32)
    rgb=np.clip(rgb*rng.uniform(.72,1.13)+rng.uniform(-8,8),0,255)
    if rng.random()<.8:
        shadow=np.zeros((oh,ow),np.float32)
        shadow[py:py+h,px:px+w]=alpha[:,:,0]
        shadow=cv2.GaussianBlur(shadow,(0,0),float(rng.uniform(.4,2.5)))
        shadow=np.roll(shadow,(int(rng.integers(0,4)),int(rng.integers(0,3))),(0,1))
        canvas*=1-shadow[:,:,None]*float(rng.uniform(.25,.6))
    canvas[py:py+h,px:px+w]=canvas[py:py+h,px:px+w]*(1-alpha)+rgb*alpha
    if rng.random()<.4:
        canvas=cv2.GaussianBlur(canvas,(3,3),float(rng.uniform(.15,.7)))
    canvas=np.clip(canvas+rng.normal(0,rng.uniform(0,2.5),canvas.shape),0,255).astype(np.uint8)
    if rng.random()<.6:
        ok,encoded=cv2.imencode('.jpg',canvas,[cv2.IMWRITE_JPEG_QUALITY,int(rng.integers(43,101))])
        canvas=cv2.imdecode(encoded,cv2.IMREAD_COLOR)
    return canvas

def unknown(rng):
    if rng.random()<.7:return augment((excluded+content)[int(rng.integers(len(excluded+content)))],rng)
    canvas=np.full((90,130,3),rng.choice([(35,65,43),(235,235,235),(30,30,30)]),np.uint8)
    for _ in range(int(rng.integers(1,9))):
        color=tuple(map(int,rng.choice([(140,195,227),(241,241,241),(193,212,237),(58,147,213)])))
        x,y=int(rng.integers(6,110)),int(rng.integers(6,75))
        cv2.line(canvas,(x,y),(int(rng.integers(6,120)),int(rng.integers(6,80))),color,int(rng.integers(1,7)))
    return canvas

def dataset(seed,count,negcount):
    rng=np.random.default_rng(seed);xx=[];yy=[];missing=0
    for j,icon in enumerate(icons,1):
        for _ in range(count):
            f=features(augment(icon,rng))
            if f is None:missing+=1;continue
            xx.append(f);yy.append(j)
    for _ in range(negcount):
        f=features(unknown(rng))
        if f is None:missing+=1;continue
        xx.append(f);yy.append(0)
    return np.asarray(xx,np.float32),np.asarray(yy,np.int64),missing

def softmax(z):
    z=z-z.max(1,keepdims=True);q=np.exp(z);return q/q.sum(1,keepdims=True)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-model',type=Path,required=True)
    parser.add_argument('--report-dir',type=Path,required=True)
    args=parser.parse_args()
    args.output_model.parent.mkdir(parents=True,exist_ok=True)
    args.report_dir.mkdir(parents=True,exist_ok=True)
    started=time.perf_counter()
    x,y,missing=dataset(104729,240,2400)
    vx,vy,vmissing=dataset(130363,80,900)
    tx,ty,tmissing=dataset(169087,120,1300)
    print('data',x.shape,vx.shape,tx.shape,'missing',missing,vmissing,tmissing,flush=True)
    rng=np.random.default_rng(196613)
    # Equal-frequency weighting prevents variable invalid unknown crops biasing a class.
    weights=(len(y)/len(ids))/np.maximum(1,np.bincount(y,minlength=len(ids)))
    W1=(rng.standard_normal((257,64))*.075).astype(np.float32);b1=np.zeros(64,np.float32)
    W2=(rng.standard_normal((64,len(ids)))*.1).astype(np.float32);b2=np.zeros(len(ids),np.float32)
    params=[W1,b1,W2,b2];mom=[np.zeros_like(p) for p in params];vel=[np.zeros_like(p) for p in params];step=0
    for epoch in range(180):
        idx=rng.permutation(len(y))
        for start in range(0,len(y),192):
            batch=idx[start:start+192];xx=x[batch];labels=y[batch]
            z=xx@W1+b1;h=np.maximum(z,0);p=softmax(h@W2+b2)
            p[np.arange(len(batch)),labels]-=1;p*=weights[labels,None]/len(batch)
            gh=p@W2.T;gh[z<=0]=0
            grads=[xx.T@gh+W1*.0001,gh.sum(0),h.T@p+W2*.0001,p.sum(0)]
            step+=1
            for param,grad,ma,va in zip(params,grads,mom,vel):
                ma*=.9;ma+=.1*grad;va*=.999;va+=.001*grad*grad
                param-=.003*(ma/(1-.9**step))/(np.sqrt(va/(1-.999**step))+1e-8)
        if epoch%30==0:
            p=softmax(np.maximum(vx@W1+b1,0)@W2+b2)
            print('epoch',epoch,'valid accuracy',float((p.argmax(1)==vy).mean()),flush=True)
    # Thresholds fixed exclusively by calibration split. Rejecting errors is privileged over coverage.
    vp=softmax(np.maximum(vx@W1+b1,0)@W2+b2)
    order=np.argsort(vp,axis=1);winner=order[:,-1];runner=order[:,-2];score=vp[np.arange(len(vx)),winner];margin=score-vp[np.arange(len(vx)),runner]
    centers=np.asarray([x[y==i].mean(0) for i in range(len(ids))],np.float32)
    distances=((vx-centers[winner])**2).mean(1)
    thresholds=[]
    for conf in [.97,.99,.995,.999]:
        for gap in [.15,.25,.35,.5]:
            for maxdist in [.05,.075,.1,.125,.15,.175,.2]:
                accepted=(winner!=0)&(score>=conf)&(margin>=gap)&(distances<=maxdist)
                wrong=int(((winner!=vy)&accepted).sum());known=vy!=0;correct=int((accepted&known&(winner==vy)).sum())
                thresholds.append((wrong,-correct,conf,gap,maxdist))
    _,_,conf,gap,maxdist=min(thresholds)
    report={'classes':ids,'training':{'seed':104729,'per_class':240,'unknown_requested':2400,'actual_samples':len(y),'feature_rejected':missing},'calibration':{'seed':130363,'per_class':80,'unknown_requested':900,'actual_samples':len(vy),'feature_rejected':vmissing},'synthetic_test':{'seed':169087,'per_class':120,'unknown_requested':1300,'actual_samples':len(ty),'feature_rejected':tmissing},'thresholds':{'confidence':conf,'margin':gap,'max_distance':maxdist},'training_seconds':time.perf_counter()-started,'limitation':'All splits derive from the same canonical icons with distinct rendering seeds; synthetic accuracy is not real screenshot accuracy.'}
    for name,xx,labels in [('calibration',vx,vy),('synthetic_test',tx,ty)]:
        p=softmax(np.maximum(xx@W1+b1,0)@W2+b2);order=np.argsort(p,axis=1);a=order[:,-1];b=order[:,-2];s=p[np.arange(len(xx)),a];g=s-p[np.arange(len(xx)),b];dist=((xx-centers[a])**2).mean(1);accept=(a!=0)&(s>=conf)&(g>=gap)&(dist<=maxdist)
        report[name].update({'top1_accuracy':float((a==labels).mean()),'accepted_correct':int((accept&(a==labels)).sum()),'accepted_wrong':int((accept&(a!=labels)).sum()),'known_coverage':float(accept[labels!=0].mean()),'unknown_feature_samples':int((labels==0).sum()),'unknown_false_accepts':int((accept&(labels==0)).sum())})
    model={'format':'mr-job-icon-mlp-v1','feature_format':'gold-area16-v1','input_count':257,'hidden_count':64,'class_ids':ids,
           'confidence_threshold':conf,'margin_threshold':gap,'max_feature_distance':maxdist,
           'weights':{name:[round(float(v),8) for v in a.flatten()] for name,a in [('input_hidden',W1),('hidden_bias',b1),('hidden_output',W2),('output_bias',b2),('feature_centers',centers)]}}
    payload=json.dumps(model,separators=(',',':'),allow_nan=False).encode('utf-8')
    args.output_model.write_bytes(payload)
    sources=[ROOT/'src/Desktop/resources/icons/manifest.json',ROOT/'src/Desktop/cpp/JobCatalog.cpp']
    sources.extend(ROOT/'src/Desktop/resources/icons'/j['icon']['file'] for j in catalog['jobs'])
    sources.extend(sorted((ROOT/'src/Desktop/resources/icons/content_types').glob('*.png')))
    report.update({'model_sha256':hashlib.sha256(payload).hexdigest(),'model_bytes':len(payload),'model_format':model['format'],
                   'feature_format':model['feature_format'],'training_script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                   'dependencies':{'python':sys.version.split()[0],'numpy':np.__version__,'opencv':cv2.__version__},
                   'sources':{p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
                   'PUBLIC_DISTRIBUTION_READY':False,'license_note':'Derived from local FINAL FANTASY XIV icons; see resources/icons/LICENSE-NOTE.md. Model is gated by MR_BUNDLE_GAME_ICONS, with no newly established public redistribution permission.',
                   'real_screenshots_used_for_training_or_threshold_selection':False,
                   'augmentation':{'icon_height':[20,92],'aspect_scale':[.82,1.22],'backgrounds':['dark green','dark gray','light green','white','gray'],'shadow':'variable blur, offset and opacity','blur_sigma':[.15,.7],'jpeg_quality':[43,100],'noise_sigma':[0,2.5]},
                   'prior_exploratory_test_seed':155921,'test_split_note':'Final test seed 169087 was not used to select confidence, margin or support distance. Prior exploratory test was replaced after introducing a conservative confidence floor.'})
    report_payload=json.dumps(report,indent=2,ensure_ascii=False,allow_nan=False)+'\n'
    args.output_model.with_suffix('.metadata.json').write_text(report_payload,encoding='utf-8')
    (args.report_dir/'training-report.json').write_text(report_payload,encoding='utf-8')
    print(json.dumps(report,indent=2),flush=True)

if __name__=='__main__': main()
