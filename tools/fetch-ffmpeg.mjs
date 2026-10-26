// 下载 ffmpeg / ffprobe 6.1.1（Windows x64，gyan.dev essentials 的 GPL 构建，
// 取自 npmmirror 的 ffmpeg-static 镜像），解压到 tools/ffmpeg/。
// 用法：node tools/fetch-ffmpeg.mjs
import { createWriteStream } from 'node:fs';
import { mkdir, stat, unlink } from 'node:fs/promises';
import { createGunzip } from 'node:zlib';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';

const base = process.argv[2] || 'https://registry.npmmirror.com/-/binary/ffmpeg-static/b6.1.1';
const dest = path.resolve('tools/ffmpeg');
await mkdir(dest, { recursive: true });

for (const name of ['ffmpeg', 'ffprobe']) {
  const exe = path.join(dest, `${name}.exe`);
  const existing = await stat(exe).catch(() => null);
  if (existing && existing.size > 20_000_000) {
    console.log(`已存在，跳过：${exe}`);
    continue;
  }
  const url = `${base}/${name}-win32-x64.gz`;
  console.log(`下载 ${url}`);
  const res = await fetch(url);
  if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}：${url}`);
  await pipeline(Readable.fromWeb(res.body), createGunzip(), createWriteStream(exe));
  const size = (await stat(exe)).size;
  console.log(`${name}.exe  ${(size / 1048576).toFixed(1)} MB`);
  await unlink(`${exe}.gz`).catch(() => {});
}

console.log('完成：tools/ffmpeg/ffmpeg.exe、tools/ffmpeg/ffprobe.exe');
