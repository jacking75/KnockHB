"""Pin reviewable common rounds and all V/F subcondition requirements; never execute products."""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT.parent / "docs_working" / "\uc6d0\uaca9\uc81c\uc5b4-\uad6c\ud604\uacc4\ud68d.md"
ENV = {
    1: ["clean-two-pc", "signed-candidate"], 2: ["two-pc", "additional-gpu", "no-hw-vm", "monitor-topology"],
    3: ["two-pc", "input-target", "dpi-letterbox-ime"], 4: ["two-pc", "independent-consent-cases"],
    5: ["two-pc", "secure-desktop", "independent-console"], 6: ["two-pc", "file-sandbox", "clipboard-fixture"],
    7: ["two-pc", "tls-turn-nat"], 8: ["two-pc", "registered-runbook"], 9: ["two-pc", "service-failure"],
    10: ["two-pc", "isolated-security-fixtures"], 11: ["two-pc", "long-load"], 12: ["two-pc", "install-update-rollback"],
}
VMAP = [4,1,10,10,5,2,4,9,9,4,2,2,1,9,10,4,4,4,7,10,3,3,3,4,5,5,7,7,7,2,6,6,8,8,11,1,12,9,1,11,12]


def assertion(fact, expected, source, role, relation="eq"):
    return dict(fact=fact, relation=relation, expected=expected, source=source, role=role)


def step(id, role, action, args=None, checks=None):
    return dict(id=id, role=role, action=action, arguments=args or {},
                assertions=checks or [assertion("action-receipt", "true", "driver-local", role)], timeoutMilliseconds=3000)


def generate():
    rows = re.findall(r'^\| \[ \] \| <a id="(?:validation-v|task-f)\d+"></a>([VF]\d+) \| (.*?) \|$', SOURCE.read_text(encoding="utf-8"), re.M)
    if len(rows) != 53:
        raise ValueError("Review changed V/F source before regenerating")
    cases = []
    for id, body in rows:
        n = int(id[1:]) if id.startswith("F") else VMAP[int(id[1:])-1]
        cases.append(dict(id=id, purpose=re.sub(r"<[^>]+>", "", body).replace("**", "").strip(),
                          planIds=[f"ra-f{n:02d}"], environments=ENV[n],
                          criteria=[f"spec-{id.lower()}-all-subconditions", "matching-candidate-and-environment", "independent-observation-and-cleanup"], status="NotRun"))
    files = {ROOT / "adapters/remoteassist/acceptance.json": cases}
    for n in range(1, 13):
        id = f"F{n:02d}"
        case = next(c for c in cases if c["id"] == id)
        steps = [step("start", "host", "host-start"), step("join", "viewer", "viewer-join"),
                 step("consent", "host", "consent", dict(monitor="REPLACE_WITH_PREPARED_MONITOR", permissions="screen,control" if n == 3 else "screen", decision="deny" if n == 4 else "approve"))]
        if n == 3:
            steps += [step("seal", "host", "seal-host"), step("move", "viewer", "viewer-pointer", dict(x="0.5", y="0.5", action="move")),
                      step("text", "viewer", "viewer-text", dict(text="DesktopTestDriver fixture")),
                      step("input", "host", "observe-input", checks=[assertion("events", "1", "host-input-target", "host", "ge"), assertion("keys-released", "true", "host-input-target", "host"), assertion("text-matches", "true", "host-input-target", "host"), assertion("host-input-sealed", "true", "host-input-target", "host")])]
        elif n == 6:
            steps += [step("file", "host", "observe-file", checks=[assertion("file-hash-matches", "true", "received-file", "host")]),
                      step("chat", "viewer", "observe-chat", checks=[assertion("chat-count", "1", "viewer-chat", "viewer")])]
        else:
            steps += [step("video", "viewer", "observe-video", checks=[assertion("decoded-delta", "0" if n == 4 else "1", "browser-decoder", "viewer", "eq" if n == 4 else "ge")] +
                           ([] if n == 4 else [assertion("marker-matches", "true", "browser-decoder", "viewer"), assertion("monitor-matches", "true", "browser-decoder", "viewer")]))]
        steps += [step("end", "host", "target-end"), step("audit", "host", "observe-audit", dict(kind="3", result="2"), [assertion("audit-matches", "1", "product-audit", "host", "ge")])]
        plan = dict(id=f"ra-f{n:02d}", adapter="remoteassist", purpose=case["purpose"], coverage=[c["id"] for c in cases if c["planIds"] == case["planIds"]],
                    preconditions=ENV[n] + ["D21 deployment and D22 recovery proof", "candidate fixed; telemetry fresh", "all per-condition fixtures reviewed separately; common round never accepts whole F"],
                    evidence=case["criteria"], stopCondition="first failure or missing readiness ends round; no retry or edit",
                    cleanup="release browser keys; stop both nodes; verify owned cleanup and normal desktop; retain dirty reservations",
                    reuse="matching-candidate contracts and fixtures once; unexecuted subconditions remain NOT RUN",
                    targetSeconds=600, maximumSeconds=900, screenSeconds=300, recovery="R2" if n in (5, 12) else "R1", steps=steps)
        files[ROOT / f"scenarios/remoteassist/ra-f{n:02d}.json"] = plan
    files[ROOT / "scenarios/examples/counter.json"] = dict(id="counter-sample", adapter="counter-example", purpose="Independent product-free adapter example", coverage=["D20"],
        preconditions=["simulation=true", "signed owner delegation for counter-sample"], evidence=["sample observer count and cleanup"],
        stopCondition="first mismatch or deadline", cleanup="reset sample; no OS resource", reuse="independent contracts", targetSeconds=10, maximumSeconds=20, screenSeconds=0, recovery="R0",
        steps=[step("increment", "observer", "increment", checks=[assertion("count", "1", "sample-observer", "observer")]), step("observe", "observer", "observe", checks=[assertion("count", "1", "sample-observer", "observer")])])
    for path, value in files.items():
        content = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
        if "--check" in sys.argv:
            if not path.exists() or path.read_text(encoding="utf-8") != content:
                raise ValueError("Plan drift: " + path.name)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")


if __name__ == "__main__":
    generate()
