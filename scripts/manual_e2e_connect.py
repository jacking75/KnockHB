import re,time
from manual_e2e_desktop import *

def window(side,predicate):
    end=time.monotonic()+12
    while True:
        rows=action(side,'windows',{})['data']['windows']
        found=[w for w in rows if predicate(w)]
        if len(found)==1:return found[0]['windowId']
        if len(found)>1 or time.monotonic()>end:raise RuntimeError('Expected exactly one target window')
        time.sleep(.2)

def click(side,w,id):
    end=time.monotonic()+8
    while True:
        elements=action(side,'controls',{'windowId':w})['data']['elements']
        found=[e for e in elements if e['automationId']==id and e['enabled']]
        if len(found)==1:break
        if len(found)>1 or time.monotonic()>end:raise RuntimeError('Target unavailable: '+id)
        time.sleep(.2)
    action(side,'focus',{'windowId':w})
    action(side,'click',{'windowId':w,'elementId':found[0]['elementId']})

def connect():
    b=window('b',lambda w:w['imagePath'].endswith('RemoteAssist.Host.Tray.exe'))
    end=time.monotonic()+12
    while True:
        elements=action('b','controls',{'windowId':b})['data']['elements']
        code=next((e['name'] for e in elements if e['automationId']=='PairingCode'),'')
        pin=next((e['name'] for e in elements if e['automationId']=='PairingPin'),'')
        if len(re.sub(r'\D','',code))==9 and len(re.sub(r'\D','',pin))==4:break
        if time.monotonic()>end:raise RuntimeError('Pairing not ready')
        time.sleep(.2)
    a=(OUT/'viewer-window.txt').read_text()
    for id,value in [('name','E2E Support'),('code',re.sub(r'\D','',code)),('pin',re.sub(r'\D','',pin))]:
        click('a',a,id)
        action('a','type-text',{'windowId':a,'text':value},private=True)
    action('a','key',{'windowId':a,'key':'ENTER','action':'press'})
    print('JOIN_SUBMITTED_NO_CREDENTIALS_LOGGED')

def consent(admin=False):
    raise RuntimeError('Unexpected extra consent in initial-admin flow; stop this run')

if __name__=='__main__':
    import sys
    try:
        {'connect':connect,'consent':consent,'admin':lambda:consent(True)}[sys.argv[1]]()
    except Exception:
        (OUT/'failed.json').write_text('{"reason":"ConnectionStepFailed"}',encoding='utf-8')
        raise
