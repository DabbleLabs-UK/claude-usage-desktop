'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const root = path.join(__dirname, '../..');
const project = fs.readFileSync(path.join(root, 'ClaudeUsage.csproj'), 'utf8');
const installer = fs.readFileSync(path.join(root, 'installer/ClaudeUsage.nsi'), 'utf8');
const tools = [
  'install-relay-startup.ps1',
  'install-openrouter-spend-sync.ps1',
  'sync-openrouter-spend.ps1',
  'openrouter-spend-export.py'
];

test('release tools always copy to build and publish output', () => {
  for (const tool of tools) {
    assert.ok(fs.statSync(path.join(root, 'tools', tool)).isFile(), tool);
    const entries = [...project.matchAll(/<None\s+Update="([^"]+)"\s*>([^]*?)<\/None>/g)]
      .filter(match => match[1] === 'tools\\' + tool);
    assert.equal(entries.length, 1, tool + ' project entry');
    assert.match(entries[0][2], /<CopyToOutputDirectory>Always<\/CopyToOutputDirectory>/);
    assert.match(entries[0][2], /<CopyToPublishDirectory>Always<\/CopyToPublishDirectory>/);
  }
});

test('installer includes every release tool under the tools directory', () => {
  const installed = [];
  let outputPath = '';
  for (const line of installer.split(/\r?\n/)) {
    const output = line.match(/^\s*SetOutPath "([^"]+)"\s*$/);
    if (output) outputPath = output[1];
    const file = line.match(/^\s*File "\$\{DIST_DIR\}\\tools\\([^"]+)"\s*$/);
    if (file) installed.push({ name: file[1], outputPath });
  }
  for (const tool of tools) {
    const entries = installed.filter(file => file.name === tool);
    assert.equal(entries.length, 1, tool + ' installer entry');
    assert.equal(entries[0].outputPath, '$INSTDIR\\tools');
  }
});

test('uninstaller removes only named release tools without recursive tools deletion', () => {
  const uninstall = installer.split('Section "Uninstall"')[1];
  assert.ok(uninstall, 'uninstall section');
  const deleted = [...uninstall.matchAll(/^\s*Delete "\$INSTDIR\\tools\\([^"]+)"\s*$/gm)]
    .map(match => match[1]);
  assert.deepEqual(deleted.slice().sort(), tools.slice().sort());
  assert.match(uninstall, /^\s*RMDir "\$INSTDIR\\tools"\s*$/m);
  assert.doesNotMatch(uninstall, /^\s*RMDir\s+\/r\s+"\$INSTDIR\\tools"/mi);
});
