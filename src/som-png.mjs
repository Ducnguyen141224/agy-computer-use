import { deflateSync, inflateSync } from 'node:zlib';

const PNG_SIG = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
  return (c ^ 0xFFFFFFFF) >>> 0;
}

function makeChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  if (pa <= pb && pa <= pc) return a;
  if (pb <= pc) return b;
  return c;
}

const FONT_5X7 = {
  '0': ['01110', '10001', '10011', '10101', '11001', '10001', '01110'],
  '1': ['00100', '01100', '00100', '00100', '00100', '00100', '01110'],
  '2': ['01110', '10001', '00001', '00110', '01000', '10000', '11111'],
  '3': ['11110', '00001', '00001', '01110', '00001', '00001', '11110'],
  '4': ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
  '5': ['11111', '10000', '11110', '00001', '00001', '10001', '01110'],
  '6': ['01110', '10000', '10000', '11110', '10001', '10001', '01110'],
  '7': ['11111', '00001', '00010', '00100', '01000', '01000', '01000'],
  '8': ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
  '9': ['01110', '10001', '10001', '01111', '00001', '00001', '01110']
};

export function decodePng(buffer) {
  if (buffer.length < 24 || !buffer.subarray(0, 8).equals(PNG_SIG)) throw new Error('Invalid PNG header.');
  let offset = 8;
  let width = 0, height = 0, bitDepth = 0, colorType = 0;
  const idats = [];
  while (offset + 8 <= buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    const start = offset + 8;
    const end = start + length;
    if (end + 4 > buffer.length) throw new Error('Truncated PNG chunk.');
    if (type === 'IHDR') {
      width = buffer.readUInt32BE(start);
      height = buffer.readUInt32BE(start + 4);
      bitDepth = buffer[start + 8];
      colorType = buffer[start + 9];
      if (bitDepth !== 8 || ![2, 6].includes(colorType) || buffer[start + 12] !== 0) {
        throw new Error('Unsupported PNG encoding for Set-of-Marks annotation.');
      }
    } else if (type === 'IDAT') {
      idats.push(buffer.subarray(start, end));
    } else if (type === 'IEND') {
      break;
    }
    offset = end + 4;
  }
  const bpp = colorType === 6 ? 4 : 3;
  const stride = width * bpp;
  const raw = inflateSync(Buffer.concat(idats));
  const pixels = new Uint8Array(height * stride);
  let src = 0;
  for (let y = 0; y < height; y++) {
    const filter = raw[src++];
    const rowStart = y * stride;
    for (let x = 0; x < stride; x++) {
      const byte = raw[src++];
      const a = x >= bpp ? pixels[rowStart + x - bpp] : 0;
      const b = y > 0 ? pixels[rowStart - stride + x] : 0;
      const c = (x >= bpp && y > 0) ? pixels[rowStart - stride + x - bpp] : 0;
      let val = byte;
      if (filter === 1) val = (byte + a) & 0xFF;
      else if (filter === 2) val = (byte + b) & 0xFF;
      else if (filter === 3) val = (byte + ((a + b) >> 1)) & 0xFF;
      else if (filter === 4) val = (byte + paeth(a, b, c)) & 0xFF;
      pixels[rowStart + x] = val;
    }
  }
  return { width, height, colorType, bpp, pixels };
}

export function encodePng({ width, height, colorType, bpp, pixels }) {
  const stride = width * bpp;
  const raw = Buffer.allocUnsafe(height * (stride + 1));
  for (let y = 0; y < height; y++) {
    const outStart = y * (stride + 1);
    raw[outStart] = 0; // Filter None for fast encoding
    raw.set(pixels.subarray(y * stride, y * stride + stride), outStart + 1);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = colorType;
  const compressed = deflateSync(raw, { level: 3 });
  return Buffer.concat([PNG_SIG, makeChunk('IHDR', ihdr), makeChunk('IDAT', compressed), makeChunk('IEND', Buffer.alloc(0))]);
}

export function annotatePng(pngBuffer, marks) {
  if (!marks || marks.length === 0) return pngBuffer;
  const img = decodePng(pngBuffer);
  const { width, height, bpp, pixels } = img;

  const setPx = (x, y, r, g, b) => {
    if (x < 0 || y < 0 || x >= width || y >= height) return;
    const idx = (y * width + x) * bpp;
    pixels[idx] = r;
    pixels[idx + 1] = g;
    pixels[idx + 2] = b;
    if (bpp === 4) pixels[idx + 3] = 255;
  };

  const fillRect = (rx, ry, rw, rh, r, g, b) => {
    const x0 = Math.max(0, rx), y0 = Math.max(0, ry);
    const x1 = Math.min(width, rx + rw), y1 = Math.min(height, ry + rh);
    for (let y = y0; y < y1; y++) {
      for (let x = x0; x < x1; x++) setPx(x, y, r, g, b);
    }
  };

  const drawOutline = (rx, ry, rw, rh, t, r, g, b) => {
    fillRect(rx, ry, rw, t, r, g, b);
    fillRect(rx, ry + rh - t, rw, t, r, g, b);
    fillRect(rx, ry, t, rh, r, g, b);
    fillRect(rx + rw - t, ry, t, rh, r, g, b);
  };

  const drawBadge = (bx, by, label) => {
    const scale = 2;
    const digitW = 5 * scale;
    const digitH = 7 * scale;
    const gap = 2;
    const padX = 4;
    const padY = 2;
    const bw = padX * 2 + label.length * digitW + Math.max(0, label.length - 1) * gap;
    const bh = padY * 2 + digitH;
    const clampedX = Math.max(1, Math.min(width - bw - 1, bx));
    const clampedY = Math.max(1, Math.min(height - bh - 1, by));

    // Badge fill (#085CA7) + crisp light rim (#F7FCFF)
    fillRect(clampedX, clampedY, bw, bh, 8, 92, 167);
    drawOutline(clampedX, clampedY, bw, bh, 1, 247, 252, 255);

    for (let d = 0; d < label.length; d++) {
      const glyph = FONT_5X7[label[d]];
      if (!glyph) continue;
      const gx = clampedX + padX + d * (digitW + gap);
      const gy = clampedY + padY;
      for (let row = 0; row < 7; row++) {
        for (let col = 0; col < 5; col++) {
          if (glyph[row][col] === '1') {
            fillRect(gx + col * scale, gy + row * scale, scale, scale, 255, 255, 255);
          }
        }
      }
    }
  };

  for (const item of marks) {
    const b = item.bounds;
    if (!b || b.width < 4 || b.height < 4) continue;
    const rx = Math.max(0, Math.min(width - 4, Math.round(b.x)));
    const ry = Math.max(0, Math.min(height - 4, Math.round(b.y)));
    const rw = Math.max(4, Math.min(width - rx, Math.round(b.width)));
    const rh = Math.max(4, Math.min(height - ry, Math.round(b.height)));
    // #31A8FF 2px outline
    drawOutline(rx, ry, rw, rh, 2, 49, 168, 255);
    const label = String(item.mark);
    const badgeTop = ry >= 20 ? ry - 19 : ry + 1;
    drawBadge(rx, badgeTop, label);
  }

  return encodePng(img);
}
