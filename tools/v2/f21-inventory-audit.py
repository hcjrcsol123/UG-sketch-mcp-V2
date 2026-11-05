"""Independent offline verification of F21 inventory guard captures; no NX access."""
import json, math, struct

MM=1e-9

def need(value,message):
    if not value:raise ValueError(message)

def num(x):
    need(type(x) in (int,float) and math.isfinite(x),'nonfinite or nonnumeric value')
    return float(x)

def vec(x,n):
    need(isinstance(x,list) and len(x)==n,'invalid vector')
    return [num(z) for z in x]

def norm(v):return math.hypot(*v)
def sub(a,b):return [x-y for x,y in zip(a,b)]
def dot(a,b):return sum(x*y for x,y in zip(a,b))
def cross(a,b):return [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]
def steps(a,b):
    def order(v):
        bits=struct.unpack('>Q',struct.pack('>d',num(v)))[0];m=bits&((1<<63)-1)
        return (1<<63)-m if bits>>63 else (1<<63)+m
    return abs(order(a)-order(b))

def mapping(m,tag,limits,normal,origin):
    need(isinstance(m,dict) and m.get('face_tag')==tag,'missing/mismatched mapping tag')
    limits=vec(limits,4);need(vec(m.get('uv_limits'),4)==limits,'mapping UV limits mismatch')
    a,b,c,d=limits;need(a<b and c<d,'degenerate UV limits');normal=vec(normal,3);nn=norm(normal);need(math.isfinite(nn) and nn>0,'invalid normal')
    origin=vec(origin,3);uc=a/2+b/2;vc=c/2+d/2;uvs=[[a,c],[a,d],[b,c],[b,d],[uc,vc]];samples=m.get('samples');need(isinstance(samples,list) and len(samples)==5,'five mapping samples required')
    points=[];dus=[];dvs=[]
    for expected,s in zip(uvs,samples):
        need(isinstance(s,dict) and vec(s.get('uv'),2)==expected,'mapping UV binding')
        p=vec(s.get('point_mm'),3);du=vec(s.get('du'),3);dv=vec(s.get('dv'),3);nu=norm(du);nv=norm(dv)
        need(math.isfinite(nu*nv) and nu>0 and nv>0 and norm(cross(du,dv))>1e-12*nu*nv,'degenerate derivatives')
        need(abs(dot(du,normal))/nn*(b-a)<=MM and abs(dot(dv,normal))/nn*(d-c)<=MM,'derivatives not planar')
        points.append(p);dus.append(du);dvs.append(dv)
    for i,(u,v) in enumerate(uvs):
        need(abs(dot(sub(points[i],origin),normal))/nn<=MM,'points not on native plane')
        expect=[points[4][k]+dus[4][k]*(u-uc)+dvs[4][k]*(v-vc) for k in range(3)]
        need(norm(sub(points[i],expect))<=MM,'non-affine points')
        need(norm(sub(dus[i],dus[4]))*(b-a)<=MM and norm(sub(dvs[i],dvs[4]))*(d-c)<=MM,'non-affine derivatives')
    return points,dus,dvs

def canonical(x):return json.dumps(x,sort_keys=True,separators=(',',':'),allow_nan=False)

def compare(before,after,ma,mb):
    a=json.loads(before,parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite inventory')));b=json.loads(after,parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite inventory')))
    need(isinstance(a,list) and isinstance(b,list) and len(a)==len(b)==3,'inventory shape')
    need(canonical(a[1:])==canonical(b[1:]),'dirty or active changed');ar,br=a[0],b[0];need(isinstance(ar,list) and isinstance(br,list) and len(ar)==len(br),'inventory count')
    need(set(ma)==set(mb),'mapping identity changed')
    for tag in ma:
        sa,sb=ma[tag]['samples'],mb[tag]['samples'];need(len(sa)==len(sb)==5,'mapping sample count')
        need(max(norm(sub(vec(x['point_mm'],3),vec(y['point_mm'],3))) for x,y in zip(sa,sb))<=MM,'physical world drift')
    exceptions=[]
    for index,(x,y) in enumerate(zip(ar,br)):
        need(isinstance(x,str) and isinstance(y,str),'inventory row type')
        if x==y:continue
        xs=x.split('|',4);ys=y.split('|',4);need(len(xs)==len(ys)==5 and xs[:4]==ys[:4] and xs[0]=='B','nonbody inventory changed')
        aa=json.loads(xs[4]);bb=json.loads(ys[4]);need(isinstance(aa,dict) and isinstance(bb,dict) and set(aa)==set(bb),'body shape')
        for k in aa:
            if k!='faces':need(canonical(aa[k])==canonical(bb[k]),'body nonface changed')
        fa,fb=aa['faces'],bb['faces'];need(len(fa)==len(fb),'face count')
        for f,g in zip(fa,fb):
            need(set(f)==set(g),'face shape')
            for k in f:
                if k!='uv_limits':need(canonical(f[k])==canonical(g[k]),'face nonUV changed')
            if canonical(f['uv_limits'])==canonical(g['uv_limits']):continue
            tag=f['face_tag'];need(tag==g['face_tag'] and f['is_planar'] is True and g['is_planar'] is True and f['native_type']==22,'not same plane')
            ua=vec(f['uv_limits'],4);ub=vec(g['uv_limits'],4);s=[steps(i,j) for i,j in zip(ua,ub)];need(max(s)<=8,'UV exceeds 8 ULP')
            pa,da,ea=mapping(ma.get(tag),tag,ua,f['direction'],f['origin_mm']);pb,db,eb=mapping(mb.get(tag),tag,ub,f['direction'],f['origin_mm'])
            maxdu=max(map(norm,da+db));maxdv=max(map(norm,ea+eb));bound=maxdu*max(abs(ua[0]-ub[0]),abs(ua[1]-ub[1]))+maxdv*max(abs(ua[2]-ub[2]),abs(ua[3]-ub[3]));world=max(norm(sub(i,j)) for i,j in zip(pa,pb))
            need(math.isfinite(bound) and bound<=MM and world<=MM,'physical bound exceeded')
            exceptions.append({'face_tag':tag,'steps':s,'physical_bound_mm':bound,'max_world_delta_mm':world})
    return exceptions

def validate_snapshot(raw,maps):
    data=json.loads(raw,parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite inventory')))
    need(isinstance(data,list) and len(data)==3 and data[1] is False and data[2] is True,'dirty or active sketch snapshot')
    seen=set()
    for row in data[0]:
        if not isinstance(row,str) or not row.startswith('B|'):continue
        parts=row.split('|',4);need(len(parts)==5,'invalid body row');body=json.loads(parts[4]);canonical(body)
        for face in body['faces']:
            if face.get('is_planar') is True:
                need(face.get('native_type')==22,'planar type mismatch');tag=face['face_tag'];need(tag not in seen,'duplicate plane tag');seen.add(tag)
                mapping(maps.get(tag),tag,face['uv_limits'],face['direction'],face['origin_mm'])
    need(set(maps)==seen,'missing or extra physical mapping')


def audit(raw):
    guards=raw.get('inventory_comparisons');need(isinstance(guards,list) and len(guards)==len(raw.get('cases',[]))+1,'missing inventory guards')
    need(guards[-1].get('label')=='outer' and guards[-1].get('before')==raw.get('inventory_before') and guards[-1].get('after')==raw.get('inventory_after'),'outer inventory not bound')
    results=[]
    expected_labels=['definition:'+row['definition']['feature_tag'] for row in raw['cases']]+['outer']
    need([g.get('label') for g in guards]==expected_labels,'definition guard bindings differ')
    for guard in guards:
        need(isinstance(guard.get('label'),str) and isinstance(guard.get('maps_before'),dict) and isinstance(guard.get('maps_after'),dict),'invalid guard')
        validate_snapshot(guard['before'],guard['maps_before']);validate_snapshot(guard['after'],guard['maps_after'])
        results.append({'label':guard['label'],'exceptions':compare(guard['before'],guard['after'],guard['maps_before'],guard['maps_after'])})
    return results
