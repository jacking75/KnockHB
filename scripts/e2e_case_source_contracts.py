"""Documentation metadata for the fixed live reductions; no execution hooks."""
from e2e_case_runtime import require

# Exact field roots read by assemble/derive. Per-step payloads and predicates
# remain defined in those functions and CASES, rather than duplicated here.
RAW={
 'C01':('browser.frames/videoWidth/videoHeight/selected/markerMatched/joinVisible/srcObjectPresent; fixture.marker/bounds; trace.capture-start.metadata; service.local-stop-observation.observation; trace.stop-ui.metadata.requestId/frequency; last.pairing-valid/host-start-count', 'browser marker/frame samples, service stop completion, terminal audit'),
 'C02':('trace.consent-ui.metadata.mask/decision; browser.frames/inputEnabled/joinVisible; events.channel-created/offer/data-ready; trace.native-input/admin-desktop; negativeWindow tick range', 'consent denied, viewer closed and measured 5-second negative window'),
 'C03':('fixture.clicks/text/textFocused/f12Down/f12Up/dragPoints/dragCount/wheel/wheelDeltaSum/events; browser.f12-keydown.defaultPrevented; events.reset/input-reset-ack.message.id/inputEpoch; trace.reservation-ack/input-backend-ready.metadata.heldCount; os.heldVirtualKeys', 'fixture counter/text arrival and correlated reset ACK'),
 'C04':('events.admin-state.message.state/adminGeneration/generation; trace.admin-consent-ui/admin-desktop/admin-mapping-closed; os.desktop/sid/session/activeSessionId/heldVirtualKeys; ocr-cancel.uacSceneMatched/x/y/width/height; uac-trigger.cancelled/unexpectedApproval; product inventory', 'second approval, Winlogon frame, unique OCR target, Default and teardown'),
 'C05':('events.admin-sas/admin-state; audit.Kind=11; trace.admin-desktop; ocr-security.kind/actualScreenMatched; os.desktop/sid/session/activeSessionId/heldVirtualKeys; last.viewer-cancel-count', 'actual SAS audit/security classification and same-user return'),
 'C06':('session-events.locked/tick/frequency/runId; operator.runId/caseId/operator/state; os.desktop/sid/session/activeSessionId/heldVirtualKeys; events.admin-state; trace.capture-start/session-runtime-start; negativeWindow tick range', 'WTS lock and witnessed operator return, measured no-resume window'),
 'C07':('events.file-offer/file-accept/file-complete/file-received.message.id/name/size/hash; binary.id/validHeader/chunkSeq/length/tick; trace.file-dialog-open/file-consent-ui.metadata.id/approved/pathHash; product-inventory.receive.name/size/sha256', 'actual local approve, all chunks, receipt and final file flush'),
 'C08':('browser.fileOfferId; events.file-offer/file-accept/file-complete/file-received.message; binary.id/validHeader/chunkSeq/length/tick; save-picker-approved.id/save-stream-closed.id; local receive name/size/sha256; last.receive-path-confirmed', 'native picker resolution and actual writer.close, final file hash'),
 'C09':('events.file-offer/file-reject/file-accept/file-complete/file-received.message.id/code; binary.length; trace.file-consent-ui; product-inventory.receive; last.file-deny-count', 'actual denied response, zero accept/binary/receipt/final files'),
 'C10':('events.chat.message.textEqualsTest/testText; browser.chatAEqualsTest/chatBEqualsTest/chatBLineCount/chatRenderMode; Tray ChatHistory exact synthetic line count; local sent counters', 'matching Unicode DOM/Tray UI line; duplicate lines fail'),
 'C11':('guard.runId/caseId/pid/createdAt/state/captured/restorable; events.clipboard-set/get/applied/text.message.id/hash/testText; browser.clipboardEqualsTest; clipboard-compare.osEqualsTestText; guard.baselineRestored/flushCompleted/comparisonVerified/startedTick/tick/frequency', 'full private baseline, response ID/hash and UI, same guard restore'),
 'C12':('events.telemetry.message.snapshot.utc/cpuPercent/memory.totalBytes/availableBytes/unavailableProviders; os.osTotalBytes; browser.frames/diagnosticsHasNA/diagnosticsDelayed; trace.capture-start/native-input; events.track/channel-created', 'two distinct fresh samples, provider UI only for observed branches'),
 'C13':('events.runbook-catalog.message.books.bookId/bookVersion/hash/requiresAdmin; installed-book.hash; browser.bookDetail/bookPreviewHash; Tray LocalActionSummary SHA256; trace.runbook-consent-ui/runbook-start/runbook-exit.metadata.id/hash/userSid/session/isSystem/exitCode; events.runbook-result', 'same operation approval, one worker start/exit/result, terminal audit'),
 'C14':('C13 catalog fields; events.runbook-request/runbook-cancel.message.id; trace.dismiss-sent/dialog-closed/dismiss-ack/input-resumed.metadata.id/epoch; browser.inputEnabled/inputReady; fixture.clicks; trace.reservation-ack.metadata.heldCount; os.heldVirtualKeys', 'same-ID dismissal ACK before new-epoch input resume; worker count stays zero'),
 'C16':('fault.runId/caseId/servicePid/serviceStart/fixturePid/nativeDownTick/faultTick/frequency; trace.reservation-ack/native-input.metadata.revision/f12Down; trace.producers-fenced/recovery-native-up/recovery-up; metadata.f12HeldBefore; trace.pid/processStartUtcTicks/generation; inventory and os return', 'bound ACK, real down, one owned service death, same Recovery fence/native up'),
 'C15':('future TURN specification only; excluded from this delivery','EXCLUDED')}

def contract(case,source):
    require(case in RAW,'undocumented-case-source')
    raw,arrival=RAW[case]
    if source in ('audit','end','final','restored'):
        raw+='; audit.EventId/Kind/Result/SessionId/ConnectionId/PermissionGeneration/Operation.Id'
    return {'raw':raw,'arrival':arrival,
            'binding':'runId + sessionId/connectionId + permission generation; per-operation id/revision; producer pid/start',
            'clock':'browser performance.now/frequency=1000; B native QPC/own frequency; A perf_counter_ns/frequency=1e9. Cross-domain ticks are not subtracted.'}
