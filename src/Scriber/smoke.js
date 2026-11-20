// Self-check run by `Scriber.exe --smoke` (see RunSmokeAsync in MainWindow.xaml.cs).
// Writes a row, adds a comment, saves the shareable HTML and the backup, then opens that HTML
// again the way a returned review copy would be. Throwing fails the check.
// It runs in its own scratch profile, so the real outline is never touched.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const $ = s => document.querySelector(s);
  const need = (ok, what) => { if (!ok) throw new Error(what); };

  // keep a copy of everything the app hands to a Save dialog
  const saved = [];
  const makeUrl = URL.createObjectURL;
  URL.createObjectURL = blob => { saved.push(blob); return makeUrl(blob); };

  const first = $('.edit');
  first.focus();
  first.textContent = 'Smoke row';
  first.dispatchEvent(new Event('input'));
  $('#add').click();
  await sleep(200);
  need(document.querySelectorAll('.row').length === 2, 'adding a row did not give 2 rows');

  $('#comment').click();
  await sleep(200);
  $('#name').value = 'Smoke';
  $('#newComment').value = 'Looks good';
  $('#post').click();
  await sleep(200);
  need($('.badge.open'), 'the comment did not show on its row');

  $('#save').click();
  $('#backup').click();
  await sleep(500);
  need(saved.length === 2, 'expected 2 saved files, got ' + saved.length);
  const html = await saved[0].text();
  const inHtml = JSON.parse(new DOMParser().parseFromString(html, 'text/html').querySelector('#state').textContent);
  const backup = JSON.parse(await saved[1].text());
  for (const [name, doc] of [['shared HTML', inHtml], ['backup', backup]]) {
    need(doc.blocks.length === 2 && doc.blocks[0].html === 'Smoke row', name + ' lost the rows');
    need(doc.comments.length === 1 && doc.comments[0].text === 'Looks good', name + ' lost the comment');
  }

  // start over, then open the shared HTML
  localStorage.clear();
  const files = new DataTransfer();
  files.items.add(new File([html], 'shared.html', { type: 'text/html' }));
  $('#file').files = files.files;
  $('#file').dispatchEvent(new Event('change'));
  await sleep(600);
  need($('.edit').textContent === 'Smoke row' && $('.badge.open'), 'opening the shared HTML did not bring the outline back');
  need(JSON.parse(localStorage.getItem('outlineReviewNotepad')).comments.length === 1, 'the opened outline was not stored');

  return JSON.stringify({ version: $('.version').textContent, rows: document.querySelectorAll('.row').length, sharedHtmlBytes: html.length });
})()
