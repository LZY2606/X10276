const compare = document.querySelector('#compare');
compare?.addEventListener('submit', async event => {
  event.preventDefault();
  const left = document.querySelector('#left').value;
  const right = document.querySelector('#right').value;
  const response = await fetch(`/api/compare/${encodeURIComponent(left)}/${encodeURIComponent(right)}`);
  if (!response.ok) { document.querySelector('#comparison').textContent = '比较失败：' + response.status; return; }
  const data = await response.json();
  const key = item => JSON.stringify(item);
  const changed = (name, side) => {
    const a = new Map(data.left[side].map(x => [x.id ?? x.eventId ?? x.code, x]));
    const b = new Map(data.right[side].map(x => [x.id ?? x.eventId ?? x.code, x]));
    const rows = [];
    for (const [id, value] of a) if (JSON.stringify(value) !== JSON.stringify(b.get(id))) rows.push(`${id}\n  左: ${JSON.stringify(value)}\n  右: ${JSON.stringify(b.get(id))}`);
    return `## ${name}: ${rows.length}\n${rows.slice(0, 20).join('\n')}`;
  };
  document.querySelector('#comparison').textContent = [
    `左依赖 ${data.left.dependencyVersion} -> 右依赖 ${data.right.dependencyVersion}`,
    `结果 ${data.left.outcome} -> ${data.right.outcome}`,
    changed('诊断', 'diagnostics'), changed('引用边', 'references'), changed('spine', 'spine'), changed('navigation', 'navigation'), changed('fallback', 'fallback')
  ].join('\n\n');
});

document.querySelector('#deleteReimport')?.addEventListener('click', async () => {
  const file = document.querySelector('#importFile').files[0];
  if (!file) return alert('请先点击“导出项目”，再选择导出的 .zip。');
  const response = await fetch('/api/import', { method: 'POST', body: await file.arrayBuffer(), headers: { 'Content-Type': 'application/zip' } });
  if (!response.ok) return alert('导入失败：' + await response.text());
  const result = await response.json();
  alert(`已导入：${result.caseId}，事件 ${result.events} 条，材料 ${result.materials} 份。刷新页面可按原事件身份读回。`);
});
