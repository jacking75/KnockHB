"""Fixed selectors; locale-specific overrides must be frozen before Prepare.

PID/start/image always come from launches, never from these selectors.
"""
from copy import deepcopy
from e2e_case_runtime import require


def rules(overrides):
    result={'pc-a':{},'pc-b':{}}
    def add(machine,target,role,title,element):
        result[machine][target]={'ownerRole':role,'window':{'title':title},'element':element}
    browser='RemoteAssist Viewer - Google Chrome'
    ids=('name','code','pin','screen','remote-text','send-text','admin-request','admin-sas','admin-revoke',
         'file-source','file-send','file-accept','chat-text','chat-send','clipboard-text','clipboard-send','clipboard-get',
         'runbook-choice','runbook-detail','runbook-send','runbook-cancel')
    for name in ids:add('pc-a',name,'viewer',browser,{'automationId':name})
    for target,name in (('join-submit','접속'),('runbook-network-summary','네트워크 어댑터 개수 조회'),('runbook-summary','요약')):
        add('pc-a',target,'viewer',browser,{'name':name})
    for prefix,title,approve in (('file','열기','열기(O)'),('receive','다른 이름으로 저장','저장(S)')):
        add('pc-a',prefix+'-dialog-path','viewer',title,{'automationId':'1148'})
        add('pc-a',prefix+'-dialog-approve','viewer',title,{'name':approve,'controlType':'ControlType.Button'})
    main='RemoteAssist — 대상자 지원'
    for name in ('HostStart','HostStop','PairingCode','PairingPin'):
        add('pc-b',name,'tray',main,{'automationId':name})
    for target,name in (('chat-text','ChatText'),('chat-send','ChatSend'),('chat-history','ChatHistory'),('file-send-local','FileSend')):
        add('pc-b',target,'tray',main,{'automationId':name})
    for name in ('ConsentApprove','ConsentDeny','ConsentScreen','ConsentControl','ConsentAdmin',
                 'consent-file','consent-clipboard','consent-diagnostics','consent-runbook','consent-chat'):
        add('pc-b',name,'tray','원격 지원 권한 승인',{'automationId':name})
    for name in ('AdminConsentApprove','AdminConsentScope'):
        add('pc-b',name,'tray','관리자 지원 2차 승인',{'automationId':name})
    for prefix,title in (('file','보낼 파일 선택'),('receive','수신 파일 저장 승인')):
        for target,name in (('path','LocalFilePath'),('approve','LocalFileApprove')):
            add('pc-b',prefix+'-dialog-'+target,'tray',title,{'automationId':name})
    add('pc-b','file-deny','tray','수신 파일 저장 승인',{'automationId':'LocalFileDeny'})
    for target,name in (('runbook-approve','LocalActionApprove'),('runbook-summary-text','LocalActionSummary')):
        add('pc-b',target,'tray','이번 Runbook 실행 승인',{'automationId':name})
    for machine,target in (('pc-a','blur-window'),('pc-b','fixture')):
        add(machine,target,'fixture','RemoteAssist 격리 입력 시험',{'automationId':'FixtureButton'})
    require(isinstance(overrides,dict) and set(overrides)<=set(result),'ui-machine')
    for machine,entries in overrides.items():
        require(isinstance(entries,dict) and set(entries)<=set(result[machine]),'unknown-ui-target')
        for target,entry in entries.items():
            require(entry.get('ownerRole')==result[machine][target]['ownerRole'],'ui-owner-role-override')
            result[machine][target]=deepcopy(entry)
    for machine,entries in result.items():
        for target,entry in entries.items():
            require(set(entry)=={'ownerRole','window','element'} and entry['ownerRole'] in ('viewer','tray','fixture'),'ui-rule-schema')
            require(set(entry['window'])=={'title'} and isinstance(entry['window']['title'],str) and entry['window']['title'],'ui-window-selector')
            require(bool(entry['element']) and set(entry['element'])<={'automationId','name','controlType'},'ui-element-selector')
            require(all(isinstance(v,str) and 0<len(v)<=256 for v in entry['element'].values()),'ui-selector-value')
    return result
