"""Read-only coturn allocation evidence supplied by the relay's operator.

The relay exporter must append current run-bound records from its allocation
database/log collector. This adapter cannot install, stop, or reconfigure coturn.
"""
from e2e_case_runtime import read_json,get,require
from e2e_case_observations import Pending,last


def observe_turn(config,ctx,browser,trace,source):
    evidence=read_json(get(config,'caseInputs.turnEvidence'))
    require(evidence.get('runId')==ctx.run_id and evidence.get('caseId')=='C15','turn-evidence-owner')
    require(evidence.get('provider')=='coturn' and evidence.get('collectorReady') is True,'turn-source-not-ready')
    require(evidence.get('relayInstance')==get(config,'caseInputs.relayInstance'),'turn-instance-mismatch')
    rows=evidence.get('allocations')
    require(isinstance(rows,list) and len(rows)<=64,'turn-allocation-bound')
    result={'sourceReady':True,'allocationMatched':False,'allocationOwnerMatched':False,
            'allocationClosed':False,'sharedRelayStopped':evidence.get('relayRunning') is not True}
    if source not in ('allocation','end'):return result
    host=last(trace,'selected-pair')['metadata']
    if not browser['selected']:raise Pending('viewer-selected-relay')
    addresses={(p[side+'Address'],p[side+'Port']) for p in (host,*browser['selected'])
               for side in ('local','remote') if p[side+'Type']=='relay'}
    matching=[r for r in rows if (r['relayAddress'],r['relayPort']) in addresses]
    require(bool(addresses),'relay-address-missing')
    if {(r['relayAddress'],r['relayPort']) for r in matching}!=addresses:raise Pending('coturn-allocation-correlation')
    require(all(r['sessionId']==ctx.session_id and r['connectionGeneration']==ctx.generation and
                r['runId']==ctx.run_id and r['allocationId'] and r['sourceRecordId'] for r in matching),'turn-allocation-binding')
    result.update(allocationMatched=True,allocationOwnerMatched=True,
        allocationClosed=all(r['state']=='closed' for r in matching))
    return result
