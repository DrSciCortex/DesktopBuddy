const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const buildDir = path.join(__dirname, '..', 'DesktopBuddy', 'bin', 'Debug', 'net10.0-windows10.0.22621.0');
const outZip = path.join(__dirname, '..', 'DesktopBuddy_Install.zip');
const staging = path.join(os.tmpdir(), 'DesktopBuddy_package');

// Clean staging
fs.rmSync(staging, { recursive: true, force: true });
fs.mkdirSync(path.join(staging, 'rml_mods'), { recursive: true });
fs.mkdirSync(path.join(staging, 'ffmpeg'), { recursive: true });
fs.mkdirSync(path.join(staging, 'cloudflared'), { recursive: true });

// Copy mod DLLs
const modDlls = ['DesktopBuddy.dll', 'NvEncSharp.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll'];
for (const dll of modDlls) {
    const src = path.join(buildDir, dll);
    if (!fs.existsSync(src)) { console.error(`Missing: ${src}`); process.exit(1); }
    fs.copyFileSync(src, path.join(staging, 'rml_mods', dll));
    console.log(`  rml_mods/${dll}`);
}

// Find and copy ffmpeg
// Find ffmpeg from PATH or Program Files
const ffmpegCandidates = [
    path.join(process.env.ProgramFiles || '', 'ffmpeg', 'bin', 'ffmpeg.exe'),
    ...(process.env.PATH || '').split(';').map(p => path.join(p, 'ffmpeg.exe')),
];
let ffmpegDir = null;
for (const c of ffmpegCandidates) {
    if (fs.existsSync(c)) { ffmpegDir = path.dirname(c); break; }
}
if (ffmpegDir) {
    const ffmpegFiles = fs.readdirSync(ffmpegDir).filter(f => f === 'ffmpeg.exe' || f.endsWith('.dll'));
    for (const f of ffmpegFiles) {
        fs.copyFileSync(path.join(ffmpegDir, f), path.join(staging, 'ffmpeg', f));
        console.log(`  ffmpeg/${f}`);
    }
} else {
    console.warn('WARNING: ffmpeg not found, skipping');
}

// Find and copy cloudflared
const cfCandidates = [
    'C:\\Program Files (x86)\\cloudflared\\cloudflared.exe',
    ...(process.env.PATH || '').split(';').map(p => path.join(p, 'cloudflared.exe')),
];
let cfPath = null;
for (const c of cfCandidates) {
    if (fs.existsSync(c)) { cfPath = c; break; }
}
if (cfPath) {
    fs.copyFileSync(cfPath, path.join(staging, 'cloudflared', 'cloudflared.exe'));
    console.log(`  cloudflared/cloudflared.exe`);
} else {
    console.warn('WARNING: cloudflared not found, skipping');
}

// Zip using PowerShell
if (fs.existsSync(outZip)) fs.unlinkSync(outZip);
console.log('\nCreating zip...');
execSync(`powershell.exe -Command "Compress-Archive -Path '${staging}\\*' -DestinationPath '${outZip}'"`, { stdio: 'inherit' });

const size = (fs.statSync(outZip).size / 1024 / 1024).toFixed(1);
console.log(`\nDone: DesktopBuddy_Install.zip (${size} MB)`);
console.log('Extract into Resonite root folder.');
