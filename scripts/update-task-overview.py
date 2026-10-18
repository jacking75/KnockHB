"""Regenerate the read-only task index from the canonical tables in the same document."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parent.parent
PLAN = ROOT / 'docs_working/원격제어-구현계획.md'
BEGIN = '<!-- task-overview:start -->'
END = '<!-- task-overview:end -->'

def render(text):
    body = re.sub(re.escape(BEGIN) + r'.*?' + re.escape(END), '', text, flags=re.S)
    tasks = []
    for line in body.splitlines():
        if line.startswith('| ['):
            cells = [c.strip() for c in line.strip('|').split('|')]
            ident = re.sub(r'<[^>]+>', '', cells[1])
            if re.fullmatch(r'[EVFH]\d{2}', ident):
                title = cells[2] if not ident.startswith('V') else re.split(r'\*\* —', cells[3], maxsplit=1)[0].strip('*')
                tasks.append((ident, cells[0], title))
        match = re.match(r'- (\[[ x]\]) (R30-\d{2}) (.+)', line)
        if match:
            tasks.append((match[2], match[1], match[3].split('。')[0].split('. ')[0]))
    expected = {f'{p}{n:02}' for p, count in [('E',41),('V',41),('F',12),('H',14)] for n in range(1,count+1)} | {f'R30-{n:02}' for n in range(1,9)}
    ids = [t[0] for t in tasks]
    if len(ids) != len(set(ids)) or set(ids) != expected:
        raise ValueError(f'Task coverage mismatch: missing={expected-set(ids)}, duplicates={len(ids)-len(set(ids))}')
    lines = [BEGIN, '## 전체 태스크 현황', '', '본문 태스크 표에서 생성한 조회용 목록이다. 상태 변경은 본문에서 하고 `python scripts/update-task-overview.py`로 갱신한다. 완료는 해당 단계의 완료이며 제품 전체 검증 통과를 뜻하지 않는다. R/G 리뷰 식별자와 과거 T 대응표는 본문에 보존한다.', '']
    for prefix, label in [('H','시험용 원격 조작 도구'),('R30-','E2E 준비'),('E','제품 구현·리뷰'),('V','실행 검증'),('F','최종 E2E')]:
        group = sorted(t for t in tasks if t[0].startswith(prefix))
        lines += [f'### {label} — {sum(t[1]=="[x]" for t in group)}/{len(group)} 완료', '', '| 상태 | ID | 태스크 |', '|---|---|---|']
        for ident, state, title in group:
            anchor = ('validation-'+ident.lower()) if ident.startswith('V') else 'task-'+ident.lower()
            lines.append(f'| {"완료" if state=="[x]" else "미완료"} | [{ident}](#{anchor}) | {title} |')
        lines.append('')
    lines.append(END)
    block = '\n'.join(lines)
    if BEGIN in text:
        return re.sub(re.escape(BEGIN)+r'.*?'+re.escape(END), lambda _: block, text, flags=re.S)
    first, rest = text.split('\n',1)
    return first+'\n\n'+block+'\n'+rest

if __name__ == '__main__':
    old = PLAN.read_text(encoding='utf-8-sig')
    new = render(old)
    if '--check' in sys.argv:
        if old != new:
            raise SystemExit('Task overview is stale')
        print('Task overview: 116 unique tasks, current')
    else:
        PLAN.write_text(new, encoding='utf-8')
        print('Task overview updated')
