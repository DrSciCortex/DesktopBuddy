const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const buildDir = path.join(__dirname, '..', 'DesktopBuddy', 'bin', 'Debug', 'net10.0-windows10.0.22621.0');
const ffmpegDir = path.join(__dirname, '..', 'ffmpeg');
const outZip = path.join(__dirname, '..', 'DesktopBuddy_Install.zip');
const staging = path.join(os.tmpdir(), 'DesktopBuddy_package');

// Clean staging
fs.rmSync(staging, { recursive: true, force: true });
fs.mkdirSync(path.join(staging, 'rml_mods'), { recursive: true });
fs.mkdirSync(path.join(staging, 'ffmpeg'), { recursive: true });
fs.mkdirSync(path.join(staging, 'cloudflared'), { recursive: true });

// Copy mod DLL (all managed deps are ILRepack'd into this single file)
const modDll = path.join(buildDir, 'DesktopBuddy.dll');
if (!fs.existsSync(modDll)) { console.error(`Missing: ${modDll}`); process.exit(1); }
fs.copyFileSync(modDll, path.join(staging, 'rml_mods', 'DesktopBuddy.dll'));
console.log('  rml_mods/DesktopBuddy.dll');

// Copy FFmpeg shared libraries from repo ffmpeg/ folder
const requiredDlls = fs.readdirSync(ffmpegDir).filter(f => f.endsWith('.dll'));
if (requiredDlls.length === 0) { console.error(`No FFmpeg DLLs found in ${ffmpegDir}`); process.exit(1); }
for (const dll of requiredDlls) {
    fs.copyFileSync(path.join(ffmpegDir, dll), path.join(staging, 'ffmpeg', dll));
    console.log(`  ffmpeg/${dll}`);
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
    console.log('  cloudflared/cloudflared.exe');
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
