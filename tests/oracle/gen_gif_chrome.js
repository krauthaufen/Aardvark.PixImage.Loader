// Rewrites the GIF references in tests/fixtures/gif with what Chromium shows.
//
// Every fixture is decoded in a real Chromium through the WebCodecs ImageDecoder API (unpremultiplied, no color
// conversion), which returns each frame fully composited on the logical screen. Where Chromium can read the file its
// frames become name.gif.N.ref and name.gif.ref (frame 0; RGB when the first frame has no transparent index and covers
// the whole screen, the rule Gif.Decode documents). Files Chromium rejects keep the references gen_gif.py wrote.
// chrome.json records, per fixture, where the references came from, Chromium's frame durations and repetition count,
// what ImageDecoder does when it knows the data is complete (it refuses frames that are not all there, while an <img>
// and a still running download show them as far as they go), and how the earlier (Pillow / ImageMagick / by
// construction) references compared. For hand_delay_*cs.gif it also holds "playback": the frame duration measured
// while an <img> plays the file - ImageDecoder reports the stored delay, the clamping of tiny delays happens in the
// animation player.
//
// usage: node gen_gif_chrome.js [--check]      (--check: compare only, write nothing)
//
// Needs a Chromium with remote debugging: CDP_URL (default http://127.0.0.1:9222), reached through puppeteer-core
// from NODE_PATH or from ~/.headed-chrome. The tests never need this script: the references are committed.
'use strict';
const fs = require('fs');
const path = require('path');

const OUT = path.join(__dirname, '..', 'fixtures', 'gif');
const CHECK = process.argv.includes('--check');
const ORIGIN = 'http://localhost:3000/';        // any localhost URL is a secure context; the page itself is faked

async function connect() {
    try {
        return await require(path.join(process.env.HOME, '.headed-chrome')).browser();
    } catch (e) {
        const puppeteer = require('puppeteer-core');
        return await puppeteer.connect({ browserURL: process.env.CDP_URL || 'http://127.0.0.1:9222' });
    }
}

// Runs in the page. Returns { frames: [base64 RGBA], durations: [microseconds], width, height, repetitions, error? }.
// With partial = true the bytes arrive through a stream that never ends, which is how Chromium sees a download that
// is still running: it then hands out what it could decode of an incomplete frame instead of failing.
async function decodeInPage(b64, partial) {
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const out = { frames: [], durations: [] };
    try {
        const data = partial ? new ReadableStream({ start(c) { c.enqueue(bytes); } }) : bytes;
        const decoder = new ImageDecoder({ data, type: 'image/gif', premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
        await decoder.tracks.ready;
        if (!partial) await decoder.completed;
        const track = decoder.tracks.selectedTrack;
        out.repetitions = track.repetitionCount;
        out.count = track.frameCount;
        for (let i = 0; i < track.frameCount; i++) {
            let image;
            try {
                // an unfinished stream can leave a decode pending forever
                const pending = decoder.decode({ frameIndex: i, completeFramesOnly: !partial });
                const result = await Promise.race([pending, new Promise((_, no) => setTimeout(() => no(new Error('no result within 3 s')), 3000))]);
                pending.catch(() => { });
                image = result.image;
            } catch (e) {
                out.failedAt = i; out.error = String(e);
                break;
            }
            const w = image.displayWidth, h = image.displayHeight;
            const buf = new Uint8Array(w * h * 4);
            await image.copyTo(buf, { format: 'RGBA', colorSpace: 'srgb' });
            out.width = w; out.height = h;
            out.durations.push(image.duration);
            image.close();
            let s = '';
            for (let k = 0; k < buf.length; k += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(k, k + 0x8000));
            out.frames.push(btoa(s));
        }
        if (!partial) decoder.close();
    } catch (e) {
        out.error = String(e);
    }
    return out;
}

// First pixel of a PNG (the first pixel of the first row is stored as is under every filter type).
function pngFirstPixel(png) {
    const idat = [];
    for (let p = 8; p < png.length;) {
        const len = png.readUInt32BE(p), type = png.toString('latin1', p + 4, p + 8);
        if (type === 'IDAT') idat.push(png.subarray(p + 8, p + 8 + len));
        p += 12 + len;
    }
    const raw = require('zlib').inflateSync(Buffer.concat(idat));
    return [raw[1], raw[2], raw[3]];
}

// Shows the GIF in an <img> and watches the page through the DevTools screencast, which delivers a timestamped picture
// for every repaint. The fixture's frames are solid colors in palette order, so each picture tells which frame is up;
// the time per frame is the slope over the whole observation. Returns milliseconds per frame.
async function measurePlayback(page, bytes, palette, frameCount) {
    await page.evaluate(async b64 => {
        const img = new Image();
        img.id = 'playback';
        img.style.cssText = 'position:fixed;left:0;top:0;width:64px;height:64px;image-rendering:pixelated';
        img.src = 'data:image/gif;base64,' + b64;
        document.body.appendChild(img);
        await img.decode();
    }, bytes.toString('base64'));
    const cdp = await page.target().createCDPSession();
    const seen = [];
    cdp.on('Page.screencastFrame', async f => {
        seen.push([f.metadata.timestamp, Buffer.from(f.data, 'base64')]);
        await cdp.send('Page.screencastFrameAck', { sessionId: f.sessionId }).catch(() => { });
    });
    await cdp.send('Page.startScreencast', { format: 'png', everyNthFrame: 1 });
    await new Promise(r => setTimeout(r, 4000));
    await cdp.send('Page.stopScreencast');
    await cdp.detach();
    await page.evaluate(() => document.getElementById('playback').remove());
    let shown = 0, last = -1, t0 = 0, t1 = 0, n = 0;
    for (const [t, png] of seen) {
        const px = pngFirstPixel(png);
        const k = palette.findIndex(c => c[0] === px[0] && c[1] === px[1] && c[2] === px[2]);
        if (k < 0) continue;
        if (last < 0) t0 = t; else shown += (k - last + frameCount) % frameCount;
        last = k; t1 = t; n++;
    }
    return { pictures: n, msPerFrame: shown > 0 ? (t1 - t0) * 1000 / shown : null };
}

function readRef(file) {
    const b = fs.readFileSync(file);
    return { w: b.readInt32LE(4), h: b.readInt32LE(8), c: b.readInt32LE(12), data: b.subarray(20) };
}

function writeRef(file, w, h, c, data) {
    const head = Buffer.alloc(20);
    head.write('PXRF');
    head.writeInt32LE(w, 4); head.writeInt32LE(h, 8); head.writeInt32LE(c, 12); head.writeInt32LE(0, 16);
    fs.writeFileSync(file, Buffer.concat([head, data]));
}

// First image descriptor: does Gif.Decode return RGB or RGBA for this file?
function firstFrameHasAlpha(d) {
    let w = d.readUInt16LE(6), h = d.readUInt16LE(8);
    let p = 13 + ((d[10] & 0x80) ? 3 * (2 << (d[10] & 7)) : 0);
    let trans = false;
    for (;;) {
        const b = d[p++];
        if (b === 0x21) {
            const label = d[p++];
            if (label === 0xF9) trans = (d[p + 1] & 1) !== 0;
            while (d[p]) p += d[p] + 1;
            p++;
        } else if (b === 0x2C) {
            const x = d.readUInt16LE(p), y = d.readUInt16LE(p + 2), fw = d.readUInt16LE(p + 4), fh = d.readUInt16LE(p + 6);
            if (w === 0 || h === 0) { w = x + fw; h = y + fh; }
            return trans || x !== 0 || y !== 0 || fw < w || fh < h;
        } else throw new Error('no image');
    }
}

(async () => {
    const browser = await connect();
    const page = await browser.newPage();
    const report = {};
    try {
        await page.setRequestInterception(true);
        page.on('request', r => r.respond({ status: 200, contentType: 'text/html', body: '<!doctype html><title>gif oracle</title><body style="margin:0">' }));
        await page.goto(ORIGIN);
        report._chromium = await browser.version();

        const names = fs.readdirSync(OUT).filter(f => f.endsWith('.gif')).sort();
        let verified = 0, changed = 0, unreadable = 0;
        for (const name of names) {
            const file = path.join(OUT, name);
            const bytes = fs.readFileSync(file);
            let r = await page.evaluate(decodeInPage, bytes.toString('base64'), false);
            const all = r;
            let partial = false;
            if (r.frames.length === 0 || r.error) {
                const p = await page.evaluate(decodeInPage, bytes.toString('base64'), true);
                if (p.frames.length > 0 && !p.error) { r = p; partial = true; }
            }
            const old = [];
            for (let i = 0; fs.existsSync(`${file}.${i}.ref`); i++) old.push(readRef(`${file}.${i}.ref`));
            const entry = report[name] = {};
            // What ImageDecoder makes of the file when it is told that this is all the data there will be.
            entry.givenAllData = all.error ? `${all.frames.length} of ${all.count === undefined ? '?' : all.count} frames, then ${all.error}` : `${all.frames.length} frames`;
            entry.framesGivenAllData = all.frames.length;
            const delay = /^hand_delay_(\d+)cs\.gif$/.exec(name);
            if (delay) {
                // How long an <img> really shows a frame with this stored delay, in whole milliseconds. This is a
                // measurement: it moves by a millisecond or two from run to run (the only values in this file that do).
                const palette = [];
                for (let i = 0; i < 8; i++) palette.push([bytes[13 + i * 3], bytes[14 + i * 3], bytes[15 + i * 3]]);
                const m = await measurePlayback(page, bytes, palette, 6);
                if (m.msPerFrame === null) throw new Error(name + ': the animation did not advance');
                console.log(`${name}: stored delay ${delay[1]} cs plays at ${m.msPerFrame.toFixed(1)} ms per frame (${m.pictures} pictures observed)`);
                entry.playback = { storedDelayCs: +delay[1], measuredMsPerFrame: Math.round(m.msPerFrame), clampedTo100ms: +delay[1] * 10 < 90 && m.msPerFrame > 90 };
            }
            if (r.frames.length === 0 || r.error) {
                entry.source = 'gen_gif.py';
                entry.chromium = 'cannot decode: ' + (r.error || 'no frames') + (r.failedAt !== undefined ? ` (frame ${r.failedAt} of ${r.count})` : '');
                unreadable++;
                continue;
            }
            const frames = r.frames.map(f => {
                const b = Buffer.from(f, 'base64');
                for (let k = 0; k < b.length; k += 4) if (b[k + 3] === 0) b[k] = b[k + 1] = b[k + 2] = 0;
                return b;
            });
            const sameSize = old.length > 0 && old[0].w === r.width && old[0].h === r.height;
            const diff = [];
            if (old.length !== frames.length) diff.push(`frame count ${old.length} -> ${frames.length}`);
            else if (!sameSize) diff.push(`size ${old[0].w}x${old[0].h} -> ${r.width}x${r.height}`);
            else frames.forEach((f, i) => { if (!f.equals(old[i].data)) diff.push(i); });
            entry.source = partial ? 'chromium, as an unfinished download' : 'chromium';
            entry.frames = frames.length;
            entry.size = [r.width, r.height];
            entry.durationsMs = r.durations.map(d => d / 1000);
            entry.repetitions = r.repetitions === null || r.repetitions === Infinity ? 'infinite' : r.repetitions;   // Infinity arrives as null
            entry.previousReference = diff.length === 0 ? 'identical' : 'differed: ' + (typeof diff[0] === 'number' ? 'frames ' + diff.join(',') : diff.join('; '));
            verified++;
            if (diff.length) { changed++; console.log(`${name}: chromium differs from the previous reference (${entry.previousReference})`); }
            if (CHECK) continue;
            for (let i = 0; fs.existsSync(`${file}.${i}.ref`); i++) fs.unlinkSync(`${file}.${i}.ref`);
            frames.forEach((f, i) => writeRef(`${file}.${i}.ref`, r.width, r.height, 4, f));
            if (firstFrameHasAlpha(bytes)) writeRef(file + '.ref', r.width, r.height, 4, frames[0]);
            else {
                const rgb = Buffer.alloc(r.width * r.height * 3);
                for (let k = 0, o = 0; k < frames[0].length; k += 4, o += 3) frames[0].copy(rgb, o, k, k + 3);
                writeRef(file + '.ref', r.width, r.height, 3, rgb);
            }
        }
        for (const [name, e] of Object.entries(report)) if (e.chromium) console.log(`${name}: ${e.chromium}`);
        console.log(`${names.length} fixtures: ${verified} read by ${report._chromium} (${changed} differ from the previous reference), ${unreadable} not readable`);
        if (!CHECK) fs.writeFileSync(path.join(OUT, 'chrome.json'), JSON.stringify(report, null, 1) + '\n');
    } finally {
        await page.close();
        browser.disconnect();
    }
})().catch(e => { console.error(e); process.exit(1); });
