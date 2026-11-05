"""Audit a recorded PreserveShape=false API probe without operating NX.

Only a negative shape counterexample: insufficient native data for the complete
oracle is explicitly reported and is never filled with fabricated face values.
"""
import argparse
import json
import math
from pathlib import Path


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--input',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if args.output.exists(): raise FileExistsError('refusing to overwrite')
    raw=json.loads(args.input.read_text(encoding='utf-8-sig'))
    rows=[x for x in raw['cases'] if x.get('name')=='fixed-solid']
    if len(rows)!=1: raise ValueError('expected one fixed-solid probe row')
    row=rows[0]
    edges=row['edges']; vertices=[]
    for edge in edges:
        for p in edge['vertices']:
            if len(p)!=3 or not all(isinstance(x,(int,float)) and not isinstance(x,bool) and math.isfinite(x) for x in p):
                raise ValueError('bad raw vertex')
            if not any(math.dist(p,q)<=1e-8 for q in vertices): vertices.append(p)
    expected=[[0,0,0],[3,0,0],[0,2,0],[0,0,10],[3,0,10],[0,2,10]]
    missing=[p for p in expected if not any(math.dist(p,q)<=1e-6 for q in vertices)]
    result={'scope':'API probe counterexample; no production, full oracle or acceptance claim',
            'input':str(args.input),'case':row['name'],'nx_reported_feature_type':row.get('feature_type'),
            'actual_edge_count':len(edges),'actual_vertices_mm':vertices,'expected_unique_vertices_mm':expected,
            'expected_missing_vertices_mm':missing,'full_oracle':'NOT_RUN: probe lacks complete face topology and independent measurements',
            'counterexample_detected':len(edges)!=9 and bool(missing)}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8') as stream:
        json.dump(result,stream,indent=2);stream.write('\n')
    print(json.dumps({'counterexample_detected':result['counterexample_detected'],'output':str(args.output)}))
    if not result['counterexample_detected']: raise SystemExit(1)


if __name__=='__main__': main()
