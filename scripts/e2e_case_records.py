"""Strict bounded wire decoding. Only an unfinished final record is Pending."""
import json
import math
from e2e_case_runtime import require,CaseError,LIMIT
from e2e_case_observations import Pending

def decode(raw,ndjson=False):
    require(len(raw)<=LIMIT,'source-size-bound')
    def unique(pairs):
        result={}
        for key,value in pairs:
            require(key not in result,'duplicate-json-key');result[key]=value
        return result
    try:
        text=raw.decode('utf-8-sig')
        if ndjson and raw and not raw.endswith(b'\n'):raise Pending('source-write-in-progress')
        parse=lambda value:json.loads(value,object_pairs_hook=unique,parse_constant=lambda _:(_ for _ in ()).throw(ValueError()))
        return [parse(line) for line in text.splitlines() if line] if ndjson else parse(text)
    except (UnicodeError,ValueError) as e:raise CaseError('source-malformed',type(e).__name__) from e

def sequence(rows,run_id,key='seq'):
    require(all(r.get('runId')==run_id for r in rows),'source-owner')
    require([r.get(key) for r in rows]==list(range(1,len(rows)+1)) and
            all(type(r.get(key)) is int for r in rows),'source-sequence')
    for row in rows:
        if 'frequency' in row:require(type(row['frequency']) is int and row['frequency']>0,'source-frequency')
        if 'tick' in row:require(type(row['tick']) in (int,float) and math.isfinite(row['tick']) and row['tick']>=0,'source-tick')
    return rows
